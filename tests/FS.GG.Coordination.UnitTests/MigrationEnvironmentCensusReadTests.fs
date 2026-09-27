module FS.GG.Coordination.MigrationEnvironmentCensusReadTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let calls = ResizeArray<GitHubRequest>()
    member _.Calls = calls |> Seq.toList

    interface IMigrationGitHubReadTransport with
        member _.Send request =
            calls.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

let private options =
    {
        ApiBase = Uri "https://api.github.test/"
        GraphQLUri = Uri "https://api.github.test/graphql"
        Owner = "FS-GG"
        Repository = "copy"
        ExpectedRepositoryId = 42L
        Token = "test-token"
        UserAgent = "fsgg-environment-census-test"
    }

let private envelope status headers body =
    Response
        {
            StatusCode = status
            Headers = headers
            Body = body
            ETag = None
            RateBudget =
                {
                    Limit = None
                    Remaining = None
                    ResetAt = None
                    Cost = None
                }
        }

let private ok body = envelope 200 Map.empty body
let private identity = """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""

let private environment id node name =
    $"""{{"id":{id},"node_id":"{node}","name":"{name}","updated_at":"2026-09-27T00:00:00Z"}}"""

let private revisedEnvironment id node name =
    $"""{{"id":{id},"node_id":"{node}","name":"{name}","updated_at":"2026-09-27T00:01:00Z"}}"""

let private page total environments =
    $"""{{"total_count":{total},"environments":[{String.concat "," environments}]}}"""

let private next pageNumber =
    $"https://api.github.test/repos/FS-GG/copy/environments?per_page=100&page={pageNumber}"

let private linked pageNumber body =
    envelope 200 (Map [ "Link", $"<{next pageNumber}>; rel=\"next\"" ]) body

let private expectError expected (result: Result<'a, string>) =
    match result with
    | Error reason -> Assert.Equal(expected, reason)
    | Ok _ -> Assert.Fail($"expected refusal {expected}")

let private capturePass responses =
    MigrationEnvironmentCensusRead.capturePass options (FakeTransport responses)

let private invalidNumericPayloads value =
    [
        0, identity.Replace("\"id\":42", "\"id\":" + value), "environment-repository-identity-shape"
        1, $"""{{"total_count":{value},"environments":[]}}""", "environment-page-shape"
        1,
        page 1 [ $"""{{"id":{value},"node_id":"ENV_7","name":"preview"}}""" ],
        "environment-identity-shape"
    ]

[<Theory>]
[<InlineData("\"42\"")>]
[<InlineData("null")>]
[<InlineData("true")>]
[<InlineData("false")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("1.5")>]
[<InlineData("-1")>]
[<InlineData("9223372036854775808")>]
let ``invalid numeric provider fields refuse without throwing or continuing capture`` value =
    for index, body, expected in invalidNumericPayloads value do
        let responses = if index = 0 then [ ok body ] else [ ok identity; ok body ]
        let transport = FakeTransport responses
        expectError expected (MigrationEnvironmentCensusRead.capturePass options transport)
        Assert.Equal(index + 1, transport.Calls.Length)

[<Theory>]
[<InlineData("\"42\"")>]
[<InlineData("null")>]
[<InlineData("true")>]
[<InlineData("false")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("1.5")>]
[<InlineData("-1")>]
[<InlineData("9223372036854775808")>]
let ``invalid numeric retained fields refuse after raw digest is recomputed`` value =
    match capturePass [ ok identity; ok (page 0 []) ] with
    | Error reason -> Assert.Fail reason
    | Ok observed ->
        for index, body, expected in invalidNumericPayloads value do
            let pages =
                observed.EnvironmentPages
                |> List.mapi (fun pageIndex evidence ->
                    if pageIndex <> index then evidence
                    else
                        { evidence with
                            EnvironmentRawBody = body
                            EnvironmentRawSha256 =
                                body |> Encoding.UTF8.GetBytes |> SHA256.HashData
                                |> Convert.ToHexString |> _.ToLowerInvariant() })
            expectError expected
                (MigrationEnvironmentCensusRead.validatePass options
                    { observed with EnvironmentPages = pages })

[<Fact>]
let ``two independent terminal empty passes prove supported empty`` () =
    let onePass = [ ok identity; ok (page 0 []) ]
    let transport = FakeTransport(onePass @ onePass)

    match MigrationEnvironmentCensusRead.captureTwoPass options transport with
    | Error reason -> Assert.Fail reason
    | Ok capture ->
        Assert.Equal(0, capture.EnvironmentFirst.EnvironmentTotalCount)
        Assert.Empty(capture.EnvironmentFirst.Environments)
        Assert.Equal(2, capture.EnvironmentFirst.EnvironmentPages.Length)

        Assert.All(
            capture.EnvironmentFirst.EnvironmentPages,
            fun item ->
                Assert.Equal(64, item.EnvironmentRequestIdentitySha256.Length)
                Assert.Equal(64, item.EnvironmentRawSha256.Length)
        )

        Assert.True(capture.EnvironmentFirst.EnvironmentPages.[1].EnvironmentNextUri.IsNone)
        Assert.Equal(capture.EnvironmentFirst, capture.EnvironmentSecond)
        Assert.Equal(64, capture.EnvironmentCaptureFingerprint.Length)
        Assert.Equal(Ok capture, MigrationEnvironmentCensusRead.proveEmpty options capture)
        Assert.Equal(4, transport.Calls.Length)

[<Fact>]
let ``two-page nonempty census is complete but cannot prove supported empty`` () =
    let firstPage = page 2 [ environment 7 "ENV_7" "preview" ]
    let secondPage = page 2 [ environment 8 "ENV_8" "production" ]
    let onePass = [ ok identity; linked 2 firstPage; ok secondPage ]
    let transport = FakeTransport(onePass @ onePass)

    match MigrationEnvironmentCensusRead.captureTwoPass options transport with
    | Error reason -> Assert.Fail reason
    | Ok capture ->
        Assert.Equal(2, capture.EnvironmentFirst.EnvironmentTotalCount)

        Assert.Equal<string list>(
            [ "preview"; "production" ],
            capture.EnvironmentFirst.Environments |> List.map _.EnvironmentName
        )

        Assert.Equal(Some(next 2), capture.EnvironmentFirst.EnvironmentPages.[1].EnvironmentNextUri)
        Assert.Equal(3, capture.EnvironmentFirst.EnvironmentPages.Length)
        expectError "environment-detail-capture-required" (MigrationEnvironmentCensusRead.proveEmpty options capture)

[<Fact>]
let ``malformed duplicate and drifting repository identities refuse`` () =
    expectError "malformed-json" (capturePass [ ok "{" ])

    expectError
        "duplicate-json-member"
        (capturePass [ ok """{"id":42,"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}""" ])

    expectError
        "environment-repository-identity-drift"
        (capturePass [ ok """{"id":43,"node_id":"REPO_43","full_name":"FS-GG/copy"}""" ])

    expectError
        "duplicate-environment-identity"
        (capturePass
            [
                ok identity
                ok (page 2 [ environment 7 "ENV_7" "preview"; environment 7 "ENV_8" "production" ])
            ])

[<Fact>]
let ``count and pagination gaps escaped links and foreign continuations refuse`` () =
    expectError
        "environment-pagination-incomplete"
        (capturePass [ ok identity; ok (page 2 [ environment 7 "ENV_7" "preview" ]) ])

    expectError
        "environment-total-count-drift"
        (capturePass
            [
                ok identity
                linked 2 (page 2 [ environment 7 "ENV_7" "preview" ])
                ok (page 3 [ environment 8 "ENV_8" "production" ])
            ])

    expectError
        "environment-pagination-continuation"
        (capturePass [ ok identity; linked 3 (page 2 [ environment 7 "ENV_7" "preview" ]) ])

    let escaped =
        envelope
            200
            (Map
                [
                    "Link",
                    "<https://api.github.test/repos/FS-GG/copy/environments?per_page=100&page=%32>; rel=\"next\""
                ])
            (page 2 [ environment 7 "ENV_7" "preview" ])

    expectError "environment-pagination-escaped-continuation" (capturePass [ ok identity; escaped ])

    let foreign =
        envelope
            200
            (Map
                [
                    "Link", "<https://api.github.test/repos/Other/copy/environments?per_page=100&page=2>; rel=\"next\""
                ])
            (page 2 [ environment 7 "ENV_7" "preview" ])

    expectError "environment-pagination-continuation" (capturePass [ ok identity; foreign ])

[<Fact>]
let ``raw typed hash and two-pass population drift refuse`` () =
    let observed =
        match capturePass [ ok identity; ok (page 0 []) ] with
        | Ok value -> value
        | Error reason -> failwith reason

    expectError
        "environment-raw-typed-drift"
        (MigrationEnvironmentCensusRead.validatePass
            options
            { observed with
                EnvironmentTotalCount = 1
            })

    expectError
        "environment-census-capture-shape"
        (MigrationEnvironmentCensusRead.validatePass
            options
            { observed with
                EnvironmentPages =
                    observed.EnvironmentPages
                    |> List.mapi (fun index item ->
                        if index = 1 then
                            { item with
                                EnvironmentRawSha256 = String.replicate 64 "0"
                            }
                        else
                            item)
            })

    let first = [ ok identity; ok (page 0 []) ]
    let second = [ ok identity; ok (page 1 [ environment 7 "ENV_7" "preview" ]) ]

    expectError
        "environment-census-pass-drift"
        (MigrationEnvironmentCensusRead.captureTwoPass options (FakeTransport(first @ second)))

    let firstRevision = [ ok identity; ok (page 1 [ environment 7 "ENV_7" "preview" ]) ]

    let secondRevision =
        [ ok identity; ok (page 1 [ revisedEnvironment 7 "ENV_7" "preview" ]) ]

    expectError
        "environment-census-pass-drift"
        (MigrationEnvironmentCensusRead.captureTwoPass options (FakeTransport(firstRevision @ secondRevision)))

[<Fact>]
let ``forbidden missing timeout and redirect remain unknown`` () =
    expectError "environment-census-read-unknown:http-403" (capturePass [ envelope 403 Map.empty "forbidden" ])

    expectError
        "environment-census-read-unknown:http-404"
        (capturePass [ ok identity; envelope 404 Map.empty "missing-or-hidden" ])

    expectError "environment-census-read-unknown:timeout" (capturePass [ TimedOut ])

    expectError
        "environment-census-read-unknown:http-302"
        (capturePass [ envelope 302 (Map [ "Location", "https://example.test/" ]) "redirect" ])

[<Fact>]
let ``non HTTPS API bases refuse before transport`` () =
    for scheme in [ "http"; "ftp" ] do
        let invalid =
            { options with
                ApiBase = Uri $"{scheme}://localhost/"
            }

        let inner = FakeTransport [ ok identity ]

        expectError "environment-census-invalid-options" (MigrationEnvironmentCensusRead.capturePass invalid inner)

        Assert.Empty(inner.Calls)

        let guarded = MigrationEnvironmentCensusRead.guardReadTransport invalid inner

        let request =
            Rest
                {
                    Method = Get
                    Uri = Uri $"{scheme}://localhost/repos/FS-GG/copy"
                    Headers = Map.empty
                    Body = None
                    ApiVersion = ApiVersion.required
                    Idempotency = ReplaySafe
                }

        Assert.Equal(NetworkFailure, guarded.Send request)
        Assert.Empty(inner.Calls)

[<Fact>]
let ``guard permits only exact replay-safe versioned GET census reads`` () =
    let inner = FakeTransport [ ok "{}" ]
    let guarded = MigrationEnvironmentCensusRead.guardReadTransport options inner

    let write =
        Rest
            {
                Method = Put
                Uri = Uri "https://api.github.test/repos/FS-GG/copy/environments"
                Headers = Map.empty
                Body = Some "{}"
                ApiVersion = ApiVersion.required
                Idempotency = NeverReplay
            }

    let unpaged =
        Rest
            {
                Method = Get
                Uri = Uri "https://api.github.test/repos/FS-GG/copy/environments"
                Headers = Map.empty
                Body = None
                ApiVersion = ApiVersion.required
                Idempotency = ReplaySafe
            }

    let continuation =
        Rest
            {
                Method = Get
                Uri = Uri(next 2)
                Headers = Map.empty
                Body = None
                ApiVersion = ApiVersion.required
                Idempotency = ReplaySafe
            }

    let graph =
        GraphQL
            {
                Uri = options.GraphQLUri
                Document = "query { viewer { login } }"
                Variables = Map.empty
                Headers = Map.empty
                ApiVersion = ApiVersion.required
                Idempotency = ReplaySafe
            }

    Assert.Equal(NetworkFailure, guarded.Send write)
    Assert.Equal(NetworkFailure, guarded.Send unpaged)

    Assert.Equal(
        Response
            {
                StatusCode = 200
                Headers = Map.empty
                Body = "{}"
                ETag = None
                RateBudget =
                    {
                        Limit = None
                        Remaining = None
                        ResetAt = None
                        Cost = None
                    }
            },
        guarded.Send continuation
    )

    Assert.Equal(NetworkFailure, guarded.Send graph)
    Assert.Single(inner.Calls) |> ignore
