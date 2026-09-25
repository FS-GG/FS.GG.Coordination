module FS.GG.Coordination.MigrationRepositoryWorkflowPolicyReadTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="test-token"; UserAgent="fsgg-migration-test"
      Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }

let private reply status headers body =
    Response
        { StatusCode=status; Headers=headers; Body=body; ETag=None
          RateBudget={ Limit=Some 5000; Remaining=Some 4999
                       ResetAt=Some(DateTimeOffset.UtcNow.AddHours 1.); Cost=Some 1 } }

let private ok body = reply 200 Map.empty body

type private FakeTransport(responses: TransportOutcome list) =
    let pending = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if pending.Count = 0 then NetworkFailure else pending.Dequeue()

let private repository =
    """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy","visibility":"private",
         "private":true,"updated_at":"2026-09-25T01:00:00Z",
         "url":"https://api.github.test/repos/FS-GG/copy"}"""
let private workflow =
    """{"default_workflow_permissions":"read","can_approve_pull_request_reviews":false}"""
let private fork =
    """{"run_workflows_from_fork_pull_requests":true,"send_write_tokens_to_workflows":false,
         "send_secrets_and_variables":false,"require_approval_for_fork_pr_workflows":true}"""
let private access = """{"access_level":"organization"}"""
let private success =
    [ ok repository; ok workflow; ok fork; ok access
      ok repository; ok workflow; ok fork; ok access; ok repository ]

let private sha (body: string) =
    body |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``workflow policy source binds two private policy passes and three identities`` () =
    let transport = FakeTransport success
    match MigrationRepositoryWorkflowPolicyRead.read options transport with
    | Error failure -> failwithf "workflow policy read refused: %A" failure
    | Ok observed ->
        Assert.Equal(42L, observed.RepositoryId)
        Assert.Equal("REPO_42", observed.RepositoryNodeId)
        Assert.Equal("private", observed.Visibility)
        Assert.Equal(WorkflowRead, observed.FirstPass.DefaultPermission)
        Assert.False(observed.FirstPass.CanApprovePullRequestReviews)
        Assert.True(observed.FirstPass.ForkPullRequests.RunWorkflowsFromForkPullRequests)
        Assert.False(observed.FirstPass.ForkPullRequests.SendWriteTokensToWorkflows)
        Assert.False(observed.FirstPass.ForkPullRequests.SendSecretsAndVariables)
        Assert.True(observed.FirstPass.ForkPullRequests.RequireApprovalForForkPrWorkflows)
        Assert.Equal(AccessOrganization, observed.FirstPass.AccessLevel)
        Assert.Equal(observed.FirstPass, observed.SecondPass)
        Assert.Equal(sha workflow, observed.FirstPass.WorkflowEvidence.PayloadSha256)
        Assert.Equal(sha fork, observed.FirstPass.ForkEvidence.PayloadSha256)
        Assert.Equal(sha access, observed.FirstPass.AccessEvidence.PayloadSha256)
        Assert.Equal(sha repository, observed.TerminalIdentity.PayloadSha256)
        Assert.Equal(9, transport.Requests.Length)
        let paths =
            transport.Requests |> List.map (function
                | Rest request ->
                    Assert.Equal(Get, request.Method)
                    Assert.True(request.Body.IsNone)
                    request.Uri.AbsolutePath
                | GraphQL _ -> failwith "workflow policy read issued GraphQL")
        Assert.Equal<string list>(
            [ "/repos/FS-GG/copy"; "/repos/FS-GG/copy/actions/permissions/workflow"
              "/repos/FS-GG/copy/actions/permissions/fork-pr-workflows-private-repos"
              "/repos/FS-GG/copy/actions/permissions/access"
              "/repos/FS-GG/copy"; "/repos/FS-GG/copy/actions/permissions/workflow"
              "/repos/FS-GG/copy/actions/permissions/fork-pr-workflows-private-repos"
              "/repos/FS-GG/copy/actions/permissions/access"; "/repos/FS-GG/copy" ], paths)

[<Fact>]
let ``workflow policy source accepts documented enterprise access level`` () =
    let enterprise = """{"access_level":"enterprise"}"""
    let transport =
        FakeTransport [ ok repository; ok workflow; ok fork; ok enterprise
                        ok repository; ok workflow; ok fork; ok enterprise; ok repository ]
    match MigrationRepositoryWorkflowPolicyRead.read options transport with
    | Error failure -> failwithf "documented enterprise level refused: %A" failure
    | Ok observed ->
        Assert.Equal(AccessEnterprise, observed.FirstPass.AccessLevel)
        Assert.Equal(AccessEnterprise, observed.SecondPass.AccessLevel)
        Assert.Equal(sha enterprise, observed.FirstPass.AccessEvidence.PayloadSha256)

[<Fact>]
let ``workflow policy source refuses drift between policy passes`` () =
    let changed = workflow.Replace("\"read\"", "\"write\"")
    let transport =
        FakeTransport [ ok repository; ok workflow; ok fork; ok access
                        ok repository; ok changed; ok fork; ok access; ok repository ]
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationRepositoryWorkflowPolicyRead.read options transport)
    Assert.Equal(8, transport.Requests.Length)
    let reordered =
        """{"can_approve_pull_request_reviews":false,"default_workflow_permissions":"read"}"""
    let rawDrift =
        FakeTransport [ ok repository; ok workflow; ok fork; ok access
                        ok repository; ok reordered; ok fork; ok access ]
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationRepositoryWorkflowPolicyRead.read options rawDrift)

[<Fact>]
let ``workflow policy source refuses middle and terminal identity drift`` () =
    let revision = repository.Replace("2026-09-25T01:00:00Z", "2026-09-25T01:01:00Z")
    let middle = FakeTransport [ ok repository; ok workflow; ok fork; ok access; ok revision ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationRepositoryWorkflowPolicyRead.read options middle)
    Assert.Equal(5, middle.Requests.Length)
    let terminal =
        FakeTransport [ ok repository; ok workflow; ok fork; ok access
                        ok repository; ok workflow; ok fork; ok access; ok revision ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationRepositoryWorkflowPolicyRead.read options terminal)
    Assert.Equal(9, terminal.Requests.Length)

[<Fact>]
let ``workflow policy source refuses foreign or public repository before policy GET`` () =
    for changed in
        [ repository.Replace("\"id\":42", "\"id\":43")
          repository.Replace("\"visibility\":\"private\"", "\"visibility\":\"public\"")
          repository.Replace("\"private\":true", "\"private\":false")
          repository.Replace("https://api.github.test/repos/FS-GG/copy",
                             "https://api.github.test/repos/Other/copy") ] do
        let transport = FakeTransport [ ok changed ]
        Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                     MigrationRepositoryWorkflowPolicyRead.read options transport)
        Assert.Single(transport.Requests) |> ignore

[<Fact>]
let ``workflow policy source refuses denied missing and linked singleton reads`` () =
    let denied = FakeTransport [ ok repository; reply 403 Map.empty "{}" ]
    Assert.Equal(Error(MigrationReadFailure.HttpRefused 403),
                 MigrationRepositoryWorkflowPolicyRead.read options denied)
    let missing = FakeTransport [ ok repository; ok workflow; reply 404 Map.empty "{}" ]
    Assert.Equal(Error(MigrationReadFailure.HttpRefused 404),
                 MigrationRepositoryWorkflowPolicyRead.read options missing)
    for prefix, body in
        [ [ ok repository ], workflow
          [ ok repository; ok workflow ], fork
          [ ok repository; ok workflow; ok fork ], access ] do
        let linked =
            reply 200 (Map.ofList [ "LiNk", "<https://api.github.test/next>; rel=\"next\"" ]) body
        let transport = FakeTransport(prefix @ [ linked ])
        Assert.Equal(Error(MigrationReadFailure.PaginationRefused "unexpected-singleton-link"),
                     MigrationRepositoryWorkflowPolicyRead.read options transport)

[<Fact>]
let ``workflow policy source refuses null successful response body`` () =
    for prefix in
        [ []
          [ ok repository ]
          [ ok repository; ok workflow ]
          [ ok repository; ok workflow; ok fork ] ] do
        let transport = FakeTransport(prefix @ [ reply 200 Map.empty null ])
        Assert.Equal(Error(MigrationReadFailure.MalformedResponse "missing:response-body"),
                     MigrationRepositoryWorkflowPolicyRead.read options transport)
        Assert.Equal(prefix.Length + 1, transport.Requests.Length)

[<Fact>]
let ``workflow policy source refuses missing unknown and malformed policy fields`` () =
    for changed in
        [ workflow.Replace("\"read\"", "\"admin\"")
          workflow.Replace(",\"can_approve_pull_request_reviews\":false", "")
          workflow.Replace("false}", "false,\"unknown\":true}") ] do
        let transport = FakeTransport [ ok repository; ok changed ]
        match MigrationRepositoryWorkflowPolicyRead.read options transport with
        | Error(MigrationReadFailure.MalformedResponse _) -> ()
        | result -> failwithf "invalid workflow policy accepted: %A" result
    for changed in
        [ fork.Replace("\"send_secrets_and_variables\":false,", "")
          fork.Replace("\"send_write_tokens_to_workflows\":false",
                       "\"send_write_tokens_to_workflows\":null") ] do
        let transport = FakeTransport [ ok repository; ok workflow; ok changed ]
        match MigrationRepositoryWorkflowPolicyRead.read options transport with
        | Error(MigrationReadFailure.MalformedResponse _) -> ()
        | result -> failwithf "invalid fork policy accepted: %A" result
    let unknownAccess = FakeTransport [ ok repository; ok workflow; ok fork; ok """{"access_level":"world"}""" ]
    match MigrationRepositoryWorkflowPolicyRead.read options unknownAccess with
    | Error(MigrationReadFailure.MalformedResponse _) -> ()
    | result -> failwithf "unknown access policy accepted: %A" result
