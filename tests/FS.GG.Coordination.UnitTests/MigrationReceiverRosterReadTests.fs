module FS.GG.Coordination.MigrationReceiverRosterReadTests

open System
open System.Collections.Generic
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
        InstallationId = 7001L
        AccountLogin = "FS-GG"
        AccountId = 9L
        AccountNodeId = "ORG_9"
        RequiredPermissions = Map [ "contents", "read"; "metadata", "read" ]
        AppToken = "app-jwt"
        InstallationToken = "installation-token"
        UserAgent = "fsgg-receiver-roster-test"
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

let private installation selection suspended permissions =
    $"""{{"id":7001,"target_id":9,"target_type":"Organization","account":{{"login":"FS-GG","id":9,"node_id":"ORG_9"}},"repository_selection":"{selection}","permissions":{permissions},"repositories_url":"https://api.github.test/installation/repositories","suspended_at":{suspended}}}"""

let private selected =
    installation "selected" "null" """{"metadata":"read","contents":"read","issues":"write"}"""

let private repository id node name =
    $"""{{"id":{id},"node_id":"{node}","full_name":"FS-GG/{name}","private":true,"archived":false,"disabled":false,"permissions":{{"admin":false,"push":false,"pull":true}}}}"""

let private page total repositories =
    $"""{{"total_count":{total},"repositories":[{String.concat "," repositories}]}}"""

let private next pageNumber =
    $"https://api.github.test/installation/repositories?per_page=100&page={pageNumber}"

let private linked pageNumber body =
    envelope 200 (Map [ "Link", $"<{next pageNumber}>; rel=\"next\"" ]) body

let private onePass =
    [ ok selected; ok (page 1 [ repository 42L "REPO_42" "copy" ]) ]

let private expectError expected (result: Result<'a, string>) =
    match result with
    | Error reason -> Assert.Equal(expected, reason)
    | Ok _ -> Assert.Fail($"expected refusal {expected}")

let private copyRepository =
    {
        DeclaredRepositoryId = 42L
        DeclaredRepositoryNodeId = "REPO_42"
        DeclaredRepositoryFullName = "FS-GG/copy"
    }

[<Fact>]
let ``selected installation scope is captured completely in two equal passes`` () =
    let transport = FakeTransport(onePass @ onePass)

    match MigrationReceiverRosterRead.captureTwoPass options transport with
    | Error reason -> Assert.Fail reason
    | Ok capture ->
        Assert.Equal("selected", capture.First.ScopeSettings.RepositorySelection)
        Assert.Equal("read", capture.First.ScopeSettings.Permissions.["contents"])
        Assert.Equal(1, capture.First.RepositoryTotalCount)
        Assert.Equal("FS-GG/copy", capture.First.Repositories.Head.RosterRepositoryFullName)
        Assert.True(capture.First.Repositories.Head.RosterPermissions.["pull"])
        Assert.Equal(2, capture.First.Pages.Length)
        Assert.Equal(64, capture.CaptureFingerprint.Length)

        let calls = transport.Calls
        Assert.Equal(4, calls.Length)

        match calls with
        | [ Rest installationOne; Rest rosterOne; Rest installationTwo; Rest rosterTwo ] ->
            Assert.Equal("Bearer app-jwt", installationOne.Headers.["Authorization"])
            Assert.Equal("Bearer installation-token", rosterOne.Headers.["Authorization"])
            Assert.Equal(installationOne.Uri, installationTwo.Uri)
            Assert.Equal(rosterOne.Uri, rosterTwo.Uri)
        | _ -> Assert.Fail "expected two installation and two roster GETs"

[<Fact>]
let ``terminal multi-page census rejects gaps escaped scope and count drift`` () =
    let first = repository 42L "REPO_42" "copy"
    let second = repository 43L "REPO_43" "copy-two"

    match
        MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport [ ok selected; linked 2 (page 2 [ first ]); ok (page 2 [ second ]) ])
    with
    | Error reason -> Assert.Fail reason
    | Ok captured -> Assert.Equal<int64 list>([ 42L; 43L ], captured.Repositories |> List.map _.RosterRepositoryId)

    expectError
        "receiver-roster-pagination-incomplete"
        (MigrationReceiverRosterRead.capturePass options (FakeTransport [ ok selected; ok (page 2 [ first ]) ]))

    expectError
        "receiver-roster-total-count-drift"
        (MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport [ ok selected; linked 2 (page 2 [ first ]); ok (page 3 [ second ]) ]))

    let escaped =
        envelope
            200
            (Map
                [
                    "Link", "<https://api.github.test/installation/repositories?per_page=100&page=%32>; rel=\"next\""
                ])
            (page 2 [ first ])

    expectError
        "receiver-roster-pagination-continuation"
        (MigrationReceiverRosterRead.capturePass options (FakeTransport [ ok selected; escaped ]))

    let foreign =
        envelope
            200
            (Map
                [
                    "Link",
                    "<https://api.github.test/user/installations/7001/repositories?per_page=100&page=2>; rel=\"next\""
                ])
            (page 2 [ first ])

    expectError
        "receiver-roster-pagination-continuation"
        (MigrationReceiverRosterRead.capturePass options (FakeTransport [ ok selected; foreign ]))

[<Fact>]
let ``unselected suspended or permission-reduced installation scope refuses`` () =
    expectError
        "receiver-roster-installation-not-selected"
        (MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport [ ok (installation "all" "null" """{"metadata":"read","contents":"read"}""") ]))

    expectError
        "receiver-roster-installation-suspended"
        (MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport
                [
                    ok (installation "selected" "\"2026-09-27T10:00:00Z\"" """{"metadata":"read","contents":"read"}""")
                ]))

    expectError
        "receiver-roster-installation-permission-drift"
        (MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport [ ok (installation "selected" "null" """{"metadata":"read"}""") ]))

[<Fact>]
let ``duplicate identities malformed permissions and provider unavailability refuse`` () =
    let duplicate =
        page 2 [ repository 42L "REPO_42" "copy"; repository 42L "REPO_43" "copy-two" ]

    expectError
        "receiver-roster-duplicate-repository"
        (MigrationReceiverRosterRead.capturePass options (FakeTransport [ ok selected; ok duplicate ]))

    let unreadableRepository =
        (repository 42L "REPO_42" "copy").Replace("\"pull\":true", "\"pull\":false")

    expectError
        "receiver-roster-repository-permission-drift"
        (MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport [ ok selected; ok (page 1 [ unreadableRepository ]) ]))

    expectError
        "receiver-roster-read-unknown:http-403"
        (MigrationReceiverRosterRead.capturePass options (FakeTransport [ envelope 403 Map.empty "forbidden" ]))

    expectError
        "receiver-roster-read-unknown:http-404"
        (MigrationReceiverRosterRead.capturePass
            options
            (FakeTransport [ ok selected; envelope 404 Map.empty "hidden" ]))

    expectError
        "receiver-roster-read-unknown:timeout"
        (MigrationReceiverRosterRead.capturePass options (FakeTransport [ TimedOut ]))

[<Fact>]
let ``two-pass raw drift refuses even when typed roster is unchanged`` () =
    let changed =
        selected.Replace(
            "\"repository_selection\":\"selected\"",
            "\"app_slug\":\"changed\",\"repository_selection\":\"selected\""
        )

    expectError
        "receiver-roster-pass-drift"
        (MigrationReceiverRosterRead.captureTwoPass options (FakeTransport(onePass @ [ ok changed; onePass.[1] ])))

[<Fact>]
let ``provider scope rejects cohort mismatch but cannot protect caller receiver names`` () =
    let capture =
        MigrationReceiverRosterRead.captureTwoPass options (FakeTransport(onePass @ onePass))
        |> Result.defaultWith failwith

    match MigrationReceiverRosterRead.assessCallerCohort true [ copyRepository ] [ 42L ] capture with
    | Error reason -> Assert.Fail reason
    | Ok assessment ->
        Assert.True(assessment.TokenScopeExhaustive)
        Assert.True(assessment.CohortRepositoriesMatchScope)
        Assert.False(assessment.CallerReceiverRosterProtected)
        Assert.False(assessment.ReceiverIdentityComplete)
        Assert.Equal(64, assessment.ScopedSettingsSha256.Length)
        Assert.Equal(64, assessment.RepositoryRosterSha256.Length)

    let extra =
        {
            DeclaredRepositoryId = 43L
            DeclaredRepositoryNodeId = "REPO_43"
            DeclaredRepositoryFullName = "FS-GG/copy-two"
        }

    expectError
        "receiver-roster-cohort-scope-mismatch"
        (MigrationReceiverRosterRead.assessCallerCohort true [ copyRepository; extra ] [ 42L ] capture)

    let wrongIdentity =
        { copyRepository with
            DeclaredRepositoryNodeId = "REPO_FORGED"
        }

    expectError
        "receiver-roster-cohort-identity-mismatch"
        (MigrationReceiverRosterRead.assessCallerCohort true [ wrongIdentity ] [ 42L ] capture)

[<Fact>]
let ``invalid local configuration refuses before transport`` () =
    for invalid in
        [
            { options with
                ApiBase = Uri "http://api.github.test/"
            }
            { options with
                RequiredPermissions = Map.empty
            }
            { options with InstallationToken = "" }
        ] do
        let transport = FakeTransport onePass
        expectError "receiver-roster-invalid-options" (MigrationReceiverRosterRead.capturePass invalid transport)
        Assert.Empty(transport.Calls)
