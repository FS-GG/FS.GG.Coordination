module FS.GG.Coordination.MigrationReceiverInstallationReadTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

type private FakeRead(outcomes: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(outcomes)
    let calls = ResizeArray<GitHubRequest>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            calls.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

type private FakeMint(outcome: TransportOutcome) =
    let calls = ResizeArray<RestRequest>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationReceiverTokenMintTransport with
        member _.Mint request = calls.Add request; outcome

let private response status body =
    Response { StatusCode=status; Headers=Map.empty; Body=body; ETag=None
               RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
let private ok body = response 200 body
let private sha256 (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private broad = """{"contents":"write","metadata":"read"}"""
let private app =
    $"""{{"id":8001,"node_id":"APP_8001","slug":"receiver-reader","owner":{{"login":"app-builder","id":19,"node_id":"USER_19"}},"permissions":{broad}}}"""
let private installation =
    $"""{{"id":143110413,"app_id":8001,"app_slug":"receiver-reader","target_id":9,"target_type":"Organization","account":{{"login":"FS-GG","id":9,"node_id":"ORG_9"}},"repository_selection":"selected","permissions":{broad},"repositories_url":"https://api.github.com/installation/repositories","suspended_at":null}}"""
let private repository =
    """{"id":1353050537,"node_id":"R_kgDOUKXpqQ","full_name":"FS-GG/FS.GG.GitHub.Substrate.Sandbox","private":true,"archived":false,"disabled":false,"permissions":{"pull":true,"push":false,"admin":false}}"""
let private page = $"""{{"total_count":1,"repositories":[{repository}]}}"""
let private reads = [ ok app; ok installation; ok page; ok installation; ok page; ok app ]
let private options =
    { ApiBase=Uri "https://api.github.com/"; AppId=8001L; AppNodeId="APP_8001"
      AppSlug="receiver-reader"; InstallationId=143110413L; AccountLogin="FS-GG"
      AccountId=9L; AccountNodeId="ORG_9"
      ExpectedAppPermissions=Map [ "contents", "write"; "metadata", "read" ]
      ExpectedInstallationPermissions=Map [ "contents", "write"; "metadata", "read" ]
      RequiredTokenPermissions=Map [ "contents", "read"; "metadata", "read" ]
      SelectedRepositories=[ { DeclaredRepositoryId=1353050537L; DeclaredRepositoryNodeId="R_kgDOUKXpqQ"
                               DeclaredRepositoryFullName="FS-GG/FS.GG.GitHub.Substrate.Sandbox" } ]
      AppToken="app-jwt"; WorkflowRunId=991L; WorkflowRunAttempt=2
      RunNonce="991-2-source"; UserAgent="receiver-installation-test" }
let private mintResponse permissions repositories token expiry =
    $"""{{"token":"{token}","expires_at":"{expiry}","permissions":{permissions},"repository_selection":"selected","repositories":{repositories}}}"""
let private expiry () = DateTimeOffset.UtcNow.AddMinutes(30.0).ToString("O")
let private narrowMint () =
    mintResponse """{"contents":"read"}""" $"[{repository}]" "minted-token" (expiry ())
let private capture options mint read =
    MigrationReceiverInstallationRead.captureWithMintForTests options
        (mint :> IMigrationReceiverTokenMintTransport) (read :> IMigrationGitHubReadTransport)
        (fun () -> DateTimeOffset.UtcNow)
let private expectUnavailable fragment result =
    match result with
    | Error reason ->
        Assert.StartsWith("receiver-installation-authority-adapter-unavailable:", reason)
        Assert.Contains(fragment, reason)
    | Ok _ -> Assert.Fail($"expected {fragment}")

[<Fact>]
let ``exact protected mint binds narrow token and reuses bearer for two native roster passes`` () =
    let raw = narrowMint ()
    let mint = FakeMint(response 201 raw)
    let observer = FakeRead reads
    match capture options mint observer with
    | Error reason -> Assert.Fail reason
    | Ok observed ->
        Assert.True(options.RequiredTokenPermissions = observed.TokenPermissions)
        Assert.True(options.ExpectedInstallationPermissions = observed.InstallationPermissions)
        Assert.Equal(options.WorkflowRunId, observed.MintAttestation.WorkflowRunId)
        Assert.Equal(options.WorkflowRunAttempt, observed.MintAttestation.WorkflowRunAttempt)
        Assert.Equal(options.RunNonce, observed.MintAttestation.RunNonce)
        Assert.Equal(64, observed.MintAttestation.TokenSha256.Length)
        Assert.Equal(64, observed.MintAttestation.ResponseSha256.Length)
        Assert.Equal(64, observed.MintAttestation.Fingerprint.Length)
        Assert.DoesNotContain("minted-token", string observed)
        Assert.Equal(observed.ComposerRosterCapture.First, observed.ComposerRosterCapture.Second)
        Assert.Equal(Ok(), MigrationReceiverInstallationRead.ensureFreshForComposer observed (observed.MintAttestation.ExpiresAt.AddSeconds(-1.0)))
        expectUnavailable "mint-expired-before-consumption"
            (MigrationReceiverInstallationRead.ensureFreshForComposer observed observed.MintAttestation.ExpiresAt)
        let repositoryPage = observed.ComposerRosterCapture.First.Pages[1]
        Assert.Equal(sha256 repositoryPage.RosterRequestedUri, repositoryPage.RosterRequestIdentitySha256)
        let request = Assert.Single(mint.Calls)
        Assert.Equal(Post, request.Method)
        Assert.Equal("https://api.github.com/app/installations/143110413/access_tokens", request.Uri.AbsoluteUri)
        Assert.Equal(Some """{"repository_ids":[1353050537],"permissions":{"contents":"read"}}""", request.Body)
        Assert.Equal("Bearer app-jwt", request.Headers.["Authorization"])
        Assert.Equal(NeverReplay, request.Idempotency)
        Assert.Equal(6, observer.Calls.Length)
        for index in [ 2; 4 ] do
            match observer.Calls[index] with
            | Rest call ->
                Assert.Equal(Get, call.Method)
                Assert.Equal("Bearer minted-token", call.Headers.["Authorization"])
            | _ -> Assert.Fail "expected roster GET"
        for index in [ 1; 3 ] do
            match observer.Calls[index] with
            | Rest call ->
                Assert.Equal("Bearer app-jwt", call.Headers.["Authorization"])
                Assert.Equal("https://api.github.com/app/installations/143110413", call.Uri.AbsoluteUri)
            | _ -> Assert.Fail "expected installation settings GET"

[<Fact>]
let ``mint refuses broad grants extra repositories and wrong identities`` () =
    let foreignRepository = repository.Replace("R_kgDOUKXpqQ", "R_foreign")
    let foreignId = repository.Replace("1353050537", "1353050538")
    let foreignName = repository.Replace("FS.GG.GitHub.Substrate.Sandbox", "Other")
    let invalid =
        [ "mint-permissions", mintResponse broad $"[{repository}]" "minted-token" (expiry ())
          "mint-permissions", mintResponse """{"contents":"read","issues":"read"}""" $"[{repository}]" "minted-token" (expiry ())
          "mint-response-shape", mintResponse """{"contents":"read","contents":"write"}""" $"[{repository}]" "minted-token" (expiry ())
          "mint-repository-scope", mintResponse """{"contents":"read"}""" "[]" "minted-token" (expiry ())
          "mint-repository-scope", mintResponse """{"contents":"read"}""" $"[{repository},{repository}]" "minted-token" (expiry ())
          "mint-repository-scope", mintResponse """{"contents":"read"}""" $"[{foreignRepository}]" "minted-token" (expiry ())
          "mint-repository-scope", mintResponse """{"contents":"read"}""" $"[{foreignId}]" "minted-token" (expiry ())
          "mint-repository-scope", mintResponse """{"contents":"read"}""" $"[{foreignName}]" "minted-token" (expiry ()) ]
    for reason, raw in invalid do
        let mint = FakeMint(response 201 raw)
        let observer = FakeRead reads
        expectUnavailable reason (capture options mint observer)
        Assert.Single(observer.Calls) |> ignore

[<Fact>]
let ``mint failure expiry and invalid protected installation refuse`` () =
    let stale = mintResponse """{"contents":"read"}""" $"[{repository}]" "minted-token" (DateTimeOffset.UtcNow.AddMinutes(-1.0).ToString("O"))
    expectUnavailable "mint-expiry" (capture options (FakeMint(response 201 stale)) (FakeRead reads))
    expectUnavailable "mint-inaccessible:http-403" (capture options (FakeMint(response 403 "forbidden")) (FakeRead reads))
    expectUnavailable "mint-inaccessible:http-200" (capture options (FakeMint(response 200 (narrowMint ()))) (FakeRead reads))
    let mint = FakeMint(response 201 (narrowMint ()))
    let observer = FakeRead reads
    expectUnavailable "invalid-options" (capture { options with InstallationId=7001L } mint observer)
    Assert.Empty(mint.Calls)
    Assert.Empty(observer.Calls)
    expectUnavailable "invalid-options"
        (capture { options with ApiBase=Uri "https://api.github.test/" } mint observer)
    Assert.Empty(mint.Calls)
    Assert.Empty(observer.Calls)
    expectUnavailable "invalid-options"
        (capture { options with RequiredTokenPermissions=Map [ "contents", "read"; "metadata", "read"; "issues", "write" ] } mint observer)
    Assert.Empty(mint.Calls)
    Assert.Empty(observer.Calls)

[<Fact>]
let ``explicit metadata grant is accepted but same token cannot cross runs`` () =
    let raw = mintResponse """{"contents":"read","metadata":"read"}""" $"[{repository}]" "minted-token" (expiry ())
    let first = capture options (FakeMint(response 201 raw)) (FakeRead reads) |> Result.defaultWith failwith
    let changed = { options with WorkflowRunId=992L; RunNonce="992-2-source" }
    Assert.True(first.TokenPermissions = options.RequiredTokenPermissions)
    expectUnavailable "mint-token-run-replay"
        (capture changed (FakeMint(response 201 raw)) (FakeRead reads))

[<Fact>]
let ``observer scope drift refuses after mint and never treats installation grants as token grants`` () =
    let changed = installation.Replace("\"contents\":\"write\"", "\"contents\":\"read\"")
    let observer = FakeRead [ ok app; ok changed ]
    expectUnavailable "receiver-roster-installation-permission-drift"
        (capture options (FakeMint(response 201 (narrowMint ()))) observer)

[<Fact>]
let ``mint expiry during first roster pass refuses before second pass`` () =
    let start = DateTimeOffset.UtcNow
    let expiresAt = start.AddMinutes(10.0)
    let raw = mintResponse """{"contents":"read"}""" $"[{repository}]" "minted-token" (expiresAt.ToString("O"))
    let observer = FakeRead reads
    let mutable calls = 0
    let clock () =
        calls <- calls + 1
        if calls < 3 then start else expiresAt.AddSeconds(1.0)
    expectUnavailable "mint-expired-during-capture"
        (MigrationReceiverInstallationRead.captureWithMintForTests options
            (FakeMint(response 201 raw) :> IMigrationReceiverTokenMintTransport)
            (observer :> IMigrationGitHubReadTransport) clock)
    Assert.Equal(3, observer.Calls.Length)

[<Fact>]
let ``App identity and second-pass roster drift refuse`` () =
    let wrongApp = app.Replace("APP_8001", "APP_FOREIGN")
    let mint = FakeMint(response 201 (narrowMint ()))
    let observer = FakeRead [ ok wrongApp ]
    expectUnavailable "app-identity-drift" (capture options mint observer)
    Assert.Empty(mint.Calls)
    let changedPage = page.Replace("\"push\":false", "\"push\":true")
    expectUnavailable "two-pass-drift"
        (capture options (FakeMint(response 201 (narrowMint ())))
            (FakeRead [ ok app; ok installation; ok page; ok installation; ok changedPage; ok app ]))

[<Fact>]
let ``unknown selected repository grant refuses`` () =
    let extra =
        """{"id":99,"node_id":"R_99","full_name":"FS-GG/other","private":true,"archived":false,"disabled":false,"permissions":{"pull":true}}"""
    let changedPage = $"""{{"total_count":2,"repositories":[{repository},{extra}]}}"""
    expectUnavailable "unselected-repository-grant"
        (capture options (FakeMint(response 201 (narrowMint ())))
            (FakeRead [ ok app; ok installation; ok changedPage ]))
