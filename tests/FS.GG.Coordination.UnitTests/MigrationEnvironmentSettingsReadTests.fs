module FS.GG.Coordination.MigrationEnvironmentSettingsReadTests

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
    """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy","updated_at":"2026-09-25T01:00:00Z"}"""

let private reviewers =
    """[{"type":"User","reviewer":{"id":7,"node_id":"USER_7","login":"alice"}},
          {"type":"Team","reviewer":{"id":8,"node_id":"TEAM_8","slug":"operators"}}]"""

let private rules =
    $"""[{{"id":11,"node_id":"RULE_11","type":"wait_timer","wait_timer":5}},
           {{"id":12,"node_id":"RULE_12","type":"required_reviewers","prevent_self_review":true,"reviewers":{reviewers}}},
           {{"id":13,"node_id":"RULE_13","type":"branch_policy"}}]"""

let private environment =
    $"""{{"id":100,"node_id":"ENV_100","name":"fleet-cutover",
          "url":"https://api.github.test/repos/FS-GG/copy/environments/fleet-cutover",
          "updated_at":"2026-09-25T01:00:00Z","protection_rules":{rules},
          "deployment_branch_policy":{{"protected_branches":false,"custom_branch_policies":true}}}}"""

let private list one total = $"""{{"total_count":{total},"environments":[{one}]}}"""
let private branches =
    """{"total_count":1,"branch_policies":[{"id":21,"node_id":"BRANCH_21","name":"main","type":"branch"}]}"""
let private custom = """{"total_count":0,"custom_deployment_protection_rules":[]}"""
let private success = [ ok repository; ok (list environment 1); ok environment; ok branches; ok custom; ok repository ]

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``environment source reads exact repository terminal settings with raw evidence`` () =
    let transport = FakeTransport success
    match MigrationEnvironmentSettingsRead.read options transport with
    | Error failure -> failwithf "environment read refused: %A" failure
    | Ok observed ->
        Assert.Equal(42L, observed.RepositoryId)
        Assert.Equal("REPO_42", observed.RepositoryNodeId)
        Assert.True(observed.Terminal)
        Assert.Equal(1, observed.TotalCount)
        Assert.Single(observed.Pages) |> ignore
        let item = Assert.Single(observed.Environments)
        Assert.Equal(100L, item.EnvironmentId)
        Assert.Equal(3, item.ProtectionRules.Length)
        Assert.Equal(Some 5, item.ProtectionRules.[0].WaitMinutes)
        Assert.Equal(Some true, item.ProtectionRules.[1].PreventSelfReview)
        Assert.Equal(2, item.ProtectionRules.[1].Reviewers.Length)
        Assert.True(item.CustomBranchPolicies)
        Assert.Single(item.BranchPolicies) |> ignore
        Assert.Equal("main", item.BranchPolicies.Head.Name)
        Assert.Empty(item.CustomRules)
        Assert.Equal(sha environment, item.DetailPayloadSha256)
        Assert.Equal(sha custom, item.CustomRulesPayloadSha256)
        Assert.Equal(sha repository, observed.IdentityPayloadSha256)
        Assert.Equal(6, transport.Requests.Length)
        for request in transport.Requests do
            match request with
            | Rest value -> Assert.Equal(Get, value.Method); Assert.True(value.Body.IsNone)
            | GraphQL _ -> failwith "environment read issued GraphQL"

[<Fact>]
let ``environment source refuses incomplete and escaped pagination`` () =
    let incomplete = FakeTransport [ ok repository; ok (list environment 2) ]
    match MigrationEnvironmentSettingsRead.read options incomplete with
    | Error(MigrationReadFailure.PaginationRefused _) -> ()
    | result -> failwithf "incomplete list accepted: %A" result
    let escaped =
        reply 200 (Map.ofList [ "link", "<https://evil.example/repos/FS-GG/copy/environments?per_page=100&page=2>; rel=\"next\"" ])
            (list environment 2)
    let transport = FakeTransport [ ok repository; escaped ]
    match MigrationEnvironmentSettingsRead.read options transport with
    | Error(MigrationReadFailure.PaginationRefused _) -> ()
    | result -> failwithf "escaped continuation accepted: %A" result
    Assert.Equal(2, transport.Requests.Length)

[<Fact>]
let ``environment source follows exact terminal environment pages`` () =
    let second =
        environment.Replace("\"id\":100", "\"id\":101")
                   .Replace("ENV_100", "ENV_101")
                   .Replace("fleet-cutover", "release-successor")
    let firstPage =
        reply 200
            (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/environments?per_page=100&page=2>; rel=\"next\"" ])
            (list environment 2)
    let transport =
        FakeTransport [ ok repository; firstPage; ok (list second 2)
                        ok environment; ok branches; ok custom
                        ok second; ok branches; ok custom; ok repository ]
    match MigrationEnvironmentSettingsRead.read options transport with
    | Error failure -> failwithf "terminal pagination refused: %A" failure
    | Ok observed ->
        Assert.Equal(2, observed.Pages.Length)
        Assert.Equal(2, observed.TotalCount)
        Assert.Equal(2, observed.Environments.Length)
        Assert.Equal(Some "https://api.github.test/repos/FS-GG/copy/environments?per_page=100&page=2",
                     observed.Pages.Head.NextUri)
        Assert.True(observed.Pages.[1].NextUri.IsNone)
        Assert.Equal(10, transport.Requests.Length)

[<Fact>]
let ``environment source refuses changed duplicate and foreign identities`` () =
    let duplicate = FakeTransport [ ok repository; ok (list (environment + "," + environment) 2) ]
    match MigrationEnvironmentSettingsRead.read options duplicate with
    | Error(MigrationReadFailure.DuplicateIdentity _) -> ()
    | result -> failwithf "duplicate environment accepted: %A" result
    let foreign = environment.Replace("https://api.github.test/repos/FS-GG/copy", "https://api.github.test/repos/Other/copy")
    let foreignTransport = FakeTransport [ ok repository; ok (list foreign 1) ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationEnvironmentSettingsRead.read options foreignTransport)
    let changed = environment.Replace("2026-09-25T01:00:00Z", "2026-09-25T01:01:00Z")
    let drift = FakeTransport [ ok repository; ok (list environment 1); ok changed ]
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationEnvironmentSettingsRead.read options drift)

[<Fact>]
let ``environment source refuses unsupported rules and denied reads`` () =
    let unknown = environment.Replace("\"type\":\"branch_policy\"", "\"type\":\"unknown_rule\"")
    let transport = FakeTransport [ ok repository; ok (list unknown 1) ]
    match MigrationEnvironmentSettingsRead.read options transport with
    | Error(MigrationReadFailure.MalformedResponse reason) -> Assert.Contains("unsupported", reason)
    | result -> failwithf "unknown rule accepted: %A" result
    let denied = FakeTransport [ ok repository; reply 403 Map.empty "{}" ]
    Assert.Equal(Error(MigrationReadFailure.HttpRefused 403),
                 MigrationEnvironmentSettingsRead.read options denied)
    let absent = FakeTransport [ ok repository; ok (list environment 1); reply 404 Map.empty "{}" ]
    Assert.Equal(Error(MigrationReadFailure.HttpRefused 404),
                 MigrationEnvironmentSettingsRead.read options absent)

[<Fact>]
let ``environment source refuses custom-rule count and terminal repository drift`` () =
    let partialCustom = """{"total_count":1,"custom_deployment_protection_rules":[]}"""
    let partial = FakeTransport [ ok repository; ok (list environment 1); ok environment
                                  ok branches; ok partialCustom ]
    match MigrationEnvironmentSettingsRead.read options partial with
    | Error(MigrationReadFailure.PaginationRefused _) -> ()
    | result -> failwithf "partial custom rule inventory accepted: %A" result
    let changedRepo = repository.Replace("2026-09-25T01:00:00Z", "2026-09-25T01:01:00Z")
    let drift = FakeTransport [ ok repository; ok (list environment 1); ok environment
                                ok branches; ok custom; ok changedRepo ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationEnvironmentSettingsRead.read options drift)

[<Fact>]
let ``environment source refuses unexpected custom-rule continuation`` () =
    let continued =
        reply 200
            (Map.ofList [ "Link", "<https://api.github.test/repos/FS-GG/copy/environments/fleet-cutover/deployment_protection_rules?page=2>; rel=\"next\"" ])
            custom
    let transport = FakeTransport [ ok repository; ok (list environment 1); ok environment
                                    ok branches; continued; ok repository ]
    match MigrationEnvironmentSettingsRead.read options transport with
    | Error(MigrationReadFailure.PaginationRefused "unexpected-custom-rule-link") -> ()
    | result -> failwithf "custom rule continuation accepted: %A" result
    Assert.Equal(5, transport.Requests.Length)

[<Fact>]
let ``environment source refuses unknown or inconsistent branch policy`` () =
    let unknown = branches.Replace("\"type\":\"branch\"", "\"type\":\"unknown\"")
    let transport = FakeTransport [ ok repository; ok (list environment 1); ok environment; ok unknown ]
    match MigrationEnvironmentSettingsRead.read options transport with
    | Error(MigrationReadFailure.MalformedResponse reason) -> Assert.Contains("unsupported", reason)
    | result -> failwithf "unknown branch policy accepted: %A" result
    let inconsistent = environment.Replace("\"custom_branch_policies\":true", "\"custom_branch_policies\":false")
    let inconsistentTransport = FakeTransport [ ok repository; ok (list inconsistent 1) ]
    match MigrationEnvironmentSettingsRead.read options inconsistentTransport with
    | Error(MigrationReadFailure.MalformedResponse _) -> ()
    | result -> failwithf "inconsistent branch policy accepted: %A" result
