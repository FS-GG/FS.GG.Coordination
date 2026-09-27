module FS.GG.Coordination.MigrationImmutableReleasesReadTests

open System
open System.Collections.Generic
open Xunit
open FS.GG.Coordination.GitHub

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="test-token"
      UserAgent="fsgg-immutable-test"
      Owner="FS-GG"
      Repository="copy"
      ExpectedRepositoryId=42L }

let private envelope status headers body =
    Response
        { StatusCode=status; Headers=headers; Body=body; ETag=None
          RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

let private ok body = envelope 200 Map.empty body

let private identity = """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}"""
let private localEnabled enforced =
    $"""{{"enabled":true,"enforced_by_owner":{(if enforced then "true" else "false")}}}"""
let private localDisabled = """{"enabled":false,"enforced_by_owner":false}"""
let private org policy = $"""{{"enforced_repositories":"{policy}"}}"""
let private selected id node full = $"""{{"id":{id},"node_id":"{node}","full_name":"{full}"}}"""
let private roster total repositories =
    $"""{{"total_count":{total},"repositories":[{String.concat "," repositories}]}}"""
let private next page =
    $"https://api.github.test/orgs/FS-GG/settings/immutable-releases/repositories?per_page=100&page={page}"
let private linked page body = ok body |> function
    | Response value -> Response { value with Headers=Map [ "Link", $"<{next page}>; rel=\"next\"" ] }
    | _ -> failwith "expected response"

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let calls = ResizeArray<GitHubRequest>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            calls.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

let private pass responses =
    let transport = FakeTransport responses
    MigrationImmutableReleasesRead.capturePass options transport, transport

let private expectError (expected: string) (result: Result<'a, string>) =
    match result with
    | Error reason -> Assert.Equal(expected, reason)
    | Ok _ -> Assert.Fail($"expected refusal {expected}")

let private selectedPass =
    [ ok identity
      ok (localEnabled true)
      ok (org "selected")
      linked 2 (roster 2 [ selected 7 "REPO_7" "FS-GG/other" ])
      ok (roster 2 [ selected 42 "REPO_42" "FS-GG/copy" ]) ]

[<Fact>]
let ``two-pass selected policy closes two pages and finds target on second page`` () =
    let transport = FakeTransport(selectedPass @ selectedPass)
    match MigrationImmutableReleasesRead.captureTwoPass options transport with
    | Error reason -> Assert.Fail reason
    | Ok capture ->
        Assert.Equal(SelectedRepositories, capture.First.OrganizationPolicy)
        Assert.Equal(Some 2, capture.First.SelectedTotalCount)
        Assert.Equal<int64 list>([ 7L; 42L ], capture.First.SelectedRepositories |> List.map _.RepositoryId)
        Assert.True(capture.First.EffectiveEnabled)
        Assert.Equal(5, capture.First.ImmutableReleasePages.Length)
        Assert.All(capture.First.ImmutableReleasePages, fun page -> Assert.Equal(64, page.ImmutableRequestIdentitySha256.Length))
        Assert.Equal(capture.First.Fingerprint, capture.Second.Fingerprint)
        Assert.Equal(10, transport.Calls.Length)
        for call in transport.Calls do
            match call with
            | Rest request ->
                Assert.Equal(Get, request.Method)
                Assert.True(request.Body.IsNone)
                Assert.Equal(ApiVersion.value ApiVersion.required, ApiVersion.value request.ApiVersion)
            | GraphQL _ -> Assert.Fail "reader emitted GraphQL"

[<Fact>]
let ``all policy and none policy with local enable reconcile`` () =
    match fst (pass [ ok identity; ok (localEnabled true); ok (org "all") ]) with
    | Ok observed ->
        Assert.Equal(AllRepositories, observed.OrganizationPolicy)
        Assert.True(observed.EffectiveEnabled)
        Assert.Empty(observed.SelectedRepositories)
    | Error reason -> Assert.Fail reason

    match fst (pass [ ok identity; ok (localEnabled false); ok (org "none") ]) with
    | Ok observed ->
        Assert.Equal(NoRepositories, observed.OrganizationPolicy)
        Assert.True(observed.EffectiveEnabled)
        Assert.False(observed.EnforcedByOwner)
    | Error reason -> Assert.Fail reason

[<Fact>]
let ``wrong repository identity and duplicate JSON members refuse`` () =
    expectError "repository-identity-drift"
        (fst (pass [ ok """{"id":43,"node_id":"REPO_43","full_name":"FS-GG/copy"}""" ]))
    expectError "duplicate-json-member"
        (fst (pass [ ok """{"id":42,"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy"}""" ]))
    expectError "duplicate-json-member"
        (fst (pass [ ok identity; ok """{"enabled":true,"enabled":true,"enforced_by_owner":false}""" ]))

[<Fact>]
let ``duplicate selected numeric or node identity refuses`` () =
    let duplicateId = roster 2 [ selected 42 "REPO_42" "FS-GG/copy"; selected 42 "REPO_X" "FS-GG/other" ]
    expectError "duplicate-selected-repository"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "selected"); ok duplicateId ]))
    let duplicateNode = roster 2 [ selected 42 "REPO_42" "FS-GG/copy"; selected 7 "REPO_42" "FS-GG/other" ]
    expectError "duplicate-selected-repository"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "selected"); ok duplicateNode ]))
    expectError "selected-membership-identity-drift"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "selected")
                     ok (roster 1 [ selected 42 "WRONG_NODE" "FS-GG/copy" ]) ]))

[<Fact>]
let ``selected pagination refuses lost terminal changed total and escaped continuation`` () =
    expectError "selected-pagination-incomplete"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "selected")
                     ok (roster 2 [ selected 7 "REPO_7" "FS-GG/other" ]) ]))

    expectError "selected-total-drift"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "selected")
                     linked 2 (roster 2 [ selected 7 "REPO_7" "FS-GG/other" ])
                     ok (roster 3 [ selected 42 "REPO_42" "FS-GG/copy" ]) ]))

    let escaped =
        ok (roster 2 [ selected 7 "REPO_7" "FS-GG/other" ]) |> function
        | Response value ->
            Response { value with Headers=Map [ "Link", "<https://api.github.test/orgs/FS-GG/settings/immutable-releases/repositories?per_page=100&page=%32>; rel=\"next\"" ] }
        | _ -> failwith "expected response"
    expectError "pagination-escaped-continuation"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "selected"); escaped ]))

[<Fact>]
let ``singleton endpoints refuse pagination`` () =
    let linkedIdentity = linked 2 identity
    expectError "singleton-pagination-refused" (fst (pass [ linkedIdentity ]))

[<Fact>]
let ``inheritance contradictions refuse`` () =
    expectError "inheritance-contradiction:all"
        (fst (pass [ ok identity; ok localDisabled; ok (org "all") ]))
    expectError "inheritance-contradiction:none"
        (fst (pass [ ok identity; ok (localEnabled true); ok (org "none") ]))
    expectError "inheritance-contradiction:selected"
        (fst (pass [ ok identity; ok (localEnabled false); ok (org "selected")
                     ok (roster 1 [ selected 42 "REPO_42" "FS-GG/copy" ]) ]))

[<Fact>]
let ``raw typed and fingerprint drift refuse revalidation`` () =
    let observed =
        match fst (pass [ ok identity; ok (localEnabled false); ok (org "none") ]) with
        | Ok value -> value
        | Error reason -> failwith reason
    expectError "raw-typed-drift"
        (MigrationImmutableReleasesRead.validatePass options { observed with RepositoryEnabled=false })
    expectError "immutable-releases-capture-shape"
        (MigrationImmutableReleasesRead.validatePass options
            { observed with ImmutableReleasePages=
                                { observed.ImmutableReleasePages.Head with ImmutableRawSha256=String.replicate 64 "0" }
                                :: observed.ImmutableReleasePages.Tail })
    expectError "pass-fingerprint-drift"
        (MigrationImmutableReleasesRead.validatePass options { observed with Fingerprint=String.replicate 64 "0" })

[<Fact>]
let ``two-pass reader refuses stable-shape policy drift`` () =
    let first = [ ok identity; ok (localEnabled false); ok (org "none") ]
    let second = [ ok identity; ok localDisabled; ok (org "none") ]
    expectError "immutable-releases-pass-drift"
        (MigrationImmutableReleasesRead.captureTwoPass options (FakeTransport(first @ second)))

[<Fact>]
let ``forbidden missing and timeout remain unknown`` () =
    expectError "immutable-releases-read-unknown:http-403"
        (fst (pass [ envelope 403 Map.empty "forbidden" ]))
    expectError "immutable-releases-read-unknown:http-404"
        (fst (pass [ ok identity; envelope 404 Map.empty "disabled-or-unavailable" ]))
    expectError "immutable-releases-read-unknown:timeout"
        (fst (pass [ TimedOut ]))

[<Fact>]
let ``guard blocks writes foreign reads and GraphQL before transport`` () =
    let inner = FakeTransport [ ok "{}" ]
    let guarded = MigrationImmutableReleasesRead.guardReadTransport options inner
    let write =
        Rest { Method=Put; Uri=Uri "https://api.github.test/repos/FS-GG/copy/immutable-releases"
               Headers=Map.empty; Body=Some "{}"; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
    let foreign =
        Rest { Method=Get; Uri=Uri "https://api.github.test/orgs/Other/settings/immutable-releases"
               Headers=Map.empty; Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
    let graph =
        GraphQL { Uri=options.GraphQLUri; Document="query { viewer { login } }"; Variables=Map.empty
                  Headers=Map.empty; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
    Assert.Equal(NetworkFailure, guarded.Send write)
    Assert.Equal(NetworkFailure, guarded.Send foreign)
    Assert.Equal(NetworkFailure, guarded.Send graph)
    Assert.Empty(inner.Calls)
