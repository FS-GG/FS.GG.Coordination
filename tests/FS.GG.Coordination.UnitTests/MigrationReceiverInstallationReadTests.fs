module FS.GG.Coordination.MigrationReceiverInstallationReadTests

open System
open System.Collections.Generic
open Xunit
open FS.GG.Coordination.GitHub

type private FakeTransport(outcomes: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(outcomes)
    let calls = ResizeArray<GitHubRequest>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            calls.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

let private response status headers body =
    Response { StatusCode=status; Headers=headers; Body=body; ETag=None
               RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
let private ok body = response 200 Map.empty body
let private app permissions =
    $"""{{"id":8001,"node_id":"APP_8001","slug":"receiver-reader","owner":{{"login":"app-builder","id":19,"node_id":"USER_19"}},"permissions":{permissions}}}"""
let private installation selection permissions =
    $"""{{"id":7001,"app_id":8001,"app_slug":"receiver-reader","target_id":9,"target_type":"Organization","account":{{"login":"FS-GG","id":9,"node_id":"ORG_9"}},"repository_selection":"{selection}","permissions":{permissions},"repositories_url":"https://api.github.test/installation/repositories","suspended_at":null}}"""
let private repository id nodeId fullName =
    $"""{{"id":{id},"node_id":"{nodeId}","full_name":"{fullName}","private":true,"archived":false,"disabled":false,"permissions":{{"pull":true,"push":false,"admin":false}}}}"""
let private page total repositories =
    $"""{{"total_count":{total},"repositories":[{String.concat "," repositories}]}}"""
let private permissions = "{\"contents\":\"read\",\"metadata\":\"read\"}"
let private broadPermissions = "{\"contents\":\"write\",\"metadata\":\"read\"}"
let private repositories = [ repository 1353050537L "R_kgDOUKXpqQ" "FS-GG/FS.GG.GitHub.Substrate.Sandbox" ]
let private declarations =
    [ { DeclaredRepositoryId=1353050537L; DeclaredRepositoryNodeId="R_kgDOUKXpqQ"
        DeclaredRepositoryFullName="FS-GG/FS.GG.GitHub.Substrate.Sandbox" } ]
let private options =
    { ApiBase=Uri "https://api.github.test/"; AppId=8001L; AppNodeId="APP_8001"
      AppSlug="receiver-reader"; InstallationId=7001L; AccountLogin="FS-GG"
      AccountId=9L; AccountNodeId="ORG_9"
      ExpectedAppPermissions=Map [ "contents", "write"; "metadata", "read" ]
      ExpectedInstallationPermissions=Map [ "contents", "write"; "metadata", "read" ]
      RequiredTokenPermissions=Map [ "contents", "read"; "metadata", "read" ]
      SelectedRepositories=declarations; AppToken="app-jwt"; InstallationToken="installation-token"
      UserAgent="receiver-installation-test" }
let private cycle appBody installationBody repositoryBody =
    [ ok appBody; ok installationBody; ok repositoryBody ]
let private stable = cycle (app broadPermissions) (installation "selected" broadPermissions) (page 1 repositories) @ [ ok (installation "selected" broadPermissions) ]
let private expectUnavailable (fragment: string) result =
    match result with
    | Error reason ->
        Assert.StartsWith("receiver-installation-authority-adapter-unavailable:", reason)
        Assert.True(reason.Contains(fragment, StringComparison.Ordinal), reason)
    | Ok _ -> Assert.Fail("expected provider authority refusal")

[<Fact>]
let ``broad installation does not attest narrow minted token permissions`` () =
    let transport = FakeTransport(stable @ stable)
    expectUnavailable "token-permission-attestation-unavailable"
        (MigrationReceiverInstallationRead.captureForComposer options transport)
    let requests =
        transport.Calls
        |> List.map (function Rest request -> request | GraphQL _ -> failwith "unexpected GraphQL")
    Assert.Equal(3, requests.Length)
    Assert.DoesNotContain(requests, fun request -> request.Uri.AbsolutePath = "/installation")
    Assert.All(requests, fun request -> Assert.Equal(Get, request.Method))

[<Fact>]
let ``inaccessible App and incomplete repository census refuse explicitly`` () =
    expectUnavailable "app-inaccessible:http-403"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport [ response 403 Map.empty "forbidden" ]))
    let partial = cycle (app broadPermissions) (installation "selected" broadPermissions) (page 1 [])
    expectUnavailable "receiver-roster-pagination-incomplete"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport partial))

[<Fact>]
let ``wrong App installation and unknown permission settings refuse`` () =
    let wrongApp = (app broadPermissions).Replace("8001", "8002")
    expectUnavailable "app-identity-drift"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport [ ok wrongApp ]))
    let extra = "{\"contents\":\"read\",\"issues\":\"read\",\"metadata\":\"read\"}"
    expectUnavailable "app-permission-settings-unknown"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport [ ok (app extra) ]))
    let mismatchedInstallation = cycle (app broadPermissions) (installation "selected" extra) (page 1 repositories)
    expectUnavailable "installation-permission"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport mismatchedInstallation))
    let broadToken = cycle (app broadPermissions) (installation "selected" broadPermissions) (page 1 repositories) @ [ ok (installation "selected" broadPermissions) ]
    expectUnavailable "token-permission-attestation-unavailable"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport broadToken))
    let impossible = { options with ExpectedInstallationPermissions=options.RequiredTokenPermissions
                                    RequiredTokenPermissions=Map [ "contents", "write"; "metadata", "read" ] }
    let transport = FakeTransport stable
    expectUnavailable "invalid-options" (MigrationReceiverInstallationRead.captureForComposer impossible transport)
    Assert.Empty(transport.Calls)

[<Fact>]
let ``all repository selection and unselected repository grants refuse`` () =
    let all = cycle (app broadPermissions) (installation "all" broadPermissions) (page 1 repositories)
    expectUnavailable "receiver-roster-installation-not-selected"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport all))
    let extraRepository = repository 108L "REPO_108" "FS-GG/copy-108"
    let extra = cycle (app broadPermissions) (installation "selected" broadPermissions) (page 2 (repositories @ [ extraRepository ]))
    expectUnavailable "unselected-repository-grant"
        (MigrationReceiverInstallationRead.captureForComposer options (FakeTransport extra))

[<Fact>]
let ``wrong selected repository refuses`` () =
    let wrong = { options with SelectedRepositories=[ { declarations.Head with DeclaredRepositoryNodeId="REPO_WRONG" } ] }
    expectUnavailable "invalid-options"
        (MigrationReceiverInstallationRead.captureForComposer wrong (FakeTransport stable))

[<Fact>]
let ``pagination escape refuses`` () =
    let escape =
        response 200 (Map [ "Link", "<https://evil.test/installation/repositories?per_page=100&page=2>; rel=\"next\"" ])
            (page 2 repositories)
    expectUnavailable "receiver-roster-pagination-continuation"
        (MigrationReceiverInstallationRead.captureForComposer options
            (FakeTransport [ ok (app broadPermissions); ok (installation "selected" broadPermissions); escape ]))

[<Fact>]
let ``invalid or non-sandbox local scope refuses before provider access`` () =
    for invalid in
        [ { options with AppToken="" }
          { options with RequiredTokenPermissions=Map [ "metadata", "read" ] }
          { options with SelectedRepositories=declarations.Tail } ] do
        let transport = FakeTransport stable
        expectUnavailable "invalid-options"
            (MigrationReceiverInstallationRead.captureForComposer invalid transport)
        Assert.Empty(transport.Calls)
