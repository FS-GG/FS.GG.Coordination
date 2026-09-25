module FS.GG.Coordination.MigrationSandboxProviderObservationTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli
open FS.GG.Coordination.GitHub

let private options =
    { ApiBase=Uri "https://api.github.com/"
      GraphQLUri=Uri "https://api.github.com/graphql"
      Token="synthetic-controlled-token"; UserAgent="sandbox-provider-controlled-test" }

let private repo =
    """{"id":1353050537,"node_id":"R_kgDOUKXpqQ","full_name":"FS-GG/FS.GG.GitHub.Substrate.Sandbox","private":true,"description":"fsgg-sandbox-gs2-04-9 disposable qualification target; never production"}"""
let private viewer =
    """{"data":{"viewer":{"login":"fs-gg-cross-repo-dispatch[bot]","databaseId":297630107}}}"""
let private projectScope =
    """{"data":{"organization":{"login":"FS-GG","projectV2":{"id":"PVT_kwDOEYAWY84BiESo","number":2,"title":"fsgg-sandbox-gs2-04-9","closed":false,"public":false}}}}"""
let private installationRepositories =
    """{"total_count":1,"repositories":[{"id":1353050537,"node_id":"R_kgDOUKXpqQ","full_name":"FS-GG/FS.GG.GitHub.Substrate.Sandbox"}]}"""
let private issue1 =
    """{"number":1,"id":101,"node_id":"ISSUE_1","state":"open","updated_at":"2026-09-25T10:00:00Z","title":"fsgg-sandbox-gs2-04-9 fixture primary","body":"baseline","labels":[{"name":"baseline"}]}"""
let private issue2 =
    """{"number":2,"id":102,"node_id":"ISSUE_2","state":"open","updated_at":"2026-09-25T10:00:00Z","title":"unowned","body":null,"labels":[]}"""
let private projectEmpty =
    """{"data":{"organization":{"projectV2":{"id":"PVT_kwDOEYAWY84BiESo","number":2,"items":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}"""
let private projectItem =
    """{"id":"ITEM_1","isArchived":false,"updatedAt":"2026-09-25T10:00:00Z","content":{"__typename":"Issue","id":"ISSUE_1","number":1,"repository":{"databaseId":1353050537}}}"""
let private projectWithItem =
    projectEmpty.Replace("\"totalCount\":0,\"nodes\":[]", "\"totalCount\":1,\"nodes\":[" + projectItem + "]")

let private reply body =
    Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
               RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=None; Cost=Some 1 } }
let private exactIssue body =
    Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=Some "etag-1"
               RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=None; Cost=Some 1 } }
let private forbidden =
    Response { StatusCode=403; Headers=Map.empty; Body="{}"; ETag=None
               RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let calls = ResizeArray<GitHubRequest>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            calls.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

let private adapter responses =
    let transport = FakeTransport responses
    MigrationSandboxProviderObservation(options, transport), transport

let private tokenSha =
    options.Token |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``scope binds four exact raw provider identities but installation grant remains unavailable`` () =
    let reader, transport = adapter [ reply viewer; reply repo; reply projectScope; reply installationRepositories ]
    match reader.ReadScopeIdentity() with
    | Error failure -> failwithf "scope identity refused: %A" failure
    | Ok evidence ->
        Assert.Equal(297630107L, evidence.ActorDatabaseId)
        Assert.Equal(1353050537L, evidence.RepositoryId)
        Assert.Equal("PVT_kwDOEYAWY84BiESo", evidence.ProjectNodeId)
        Assert.Equal(tokenSha, evidence.TokenSha256)
        Assert.True(evidence.TokenRepositorySelectionComplete)
        Assert.Equal(4, evidence.Proofs.Length)
        Assert.All(evidence.Proofs, fun p -> Assert.Equal(64, p.RawSha256.Length))
        Assert.Equal(4, transport.Calls.Length)
        Assert.All(transport.Calls, fun request ->
            match request with
            | Rest value ->
                Assert.Equal(Get, value.Method)
                Assert.True(value.Body.IsNone)
                Assert.Equal(Some "Bearer synthetic-controlled-token", Map.tryFind "authorization" value.Headers)
            | GraphQL value ->
                Assert.StartsWith("query", value.Document)
                Assert.Equal(Some "Bearer synthetic-controlled-token", Map.tryFind "authorization" value.Headers))
    let refusal, _ = adapter [ reply viewer; reply repo; reply projectScope; reply installationRepositories ]
    Assert.Equal(Error MigrationSandboxProviderFailure.GrantUnavailable, refusal.ObserveScope())
    let forgedPermissions =
        installationRepositories.Replace("\"total_count\":1",
            "\"permissions\":{\"issues\":\"write\",\"organization_projects\":\"write\"},\"total_count\":1")
    let stillUnavailable, _ = adapter [ reply viewer; reply repo; reply projectScope; reply forgedPermissions ]
    Assert.Equal(Error MigrationSandboxProviderFailure.GrantUnavailable, stillUnavailable.ObserveScope())

[<Fact>]
let ``scope refuses partial GraphQL errors foreign identities missing selection and HTTP 403`` () =
    let partial, _ = adapter [ reply """{"data":{"viewer":{"login":"fs-gg-cross-repo-dispatch[bot]","databaseId":297630107}},"errors":[{"message":"partial"}]}""" ]
    Assert.Equal(Error MigrationSandboxProviderFailure.GraphQLErrors, partial.ReadScopeIdentity())
    let foreign, _ = adapter [ reply viewer; reply (repo.Replace("R_kgDOUKXpqQ", "FOREIGN")) ]
    Assert.Equal(Error MigrationSandboxProviderFailure.ForeignIdentity, foreign.ReadScopeIdentity())
    let selected, _ = adapter [ reply viewer; reply repo; reply projectScope
                                reply (installationRepositories.Replace("\"total_count\":1", "\"total_count\":2")) ]
    Assert.Equal(Error MigrationSandboxProviderFailure.ForeignIdentity, selected.ReadScopeIdentity())
    let denied, _ = adapter [ reply viewer; forbidden ]
    Assert.Equal(Error(MigrationSandboxProviderFailure.HttpRefused 403), denied.ReadScopeIdentity())

[<Fact>]
let ``fixture parses terminal issue and Project censuses with exact ETag and raw proofs`` () =
    let issues = "[" + issue1 + "," + issue2 + "]"
    let reader, transport = adapter [ reply repo; reply issues; exactIssue issue1; reply projectWithItem ]
    match reader.ReadFixture("36086215835-1-" + String.replicate 40 "a") with
    | Error failure -> failwithf "fixture refused: %A" failure
    | Ok evidence ->
        let observed = evidence.Observation
        Assert.True(observed.Complete)
        Assert.Equal("etag-1", observed.Issue.Revision)
        Assert.Equal(tokenSha, evidence.TokenSha256)
        Assert.Equal([ "ITEM_1" ], observed.ProjectItemIdsForIssue)
        Assert.Empty(observed.NonceIssueNodeIds)
        Assert.Single(evidence.IssuePages) |> ignore
        Assert.Single(evidence.ProjectPages) |> ignore
        Assert.True(evidence.IssuePages.Head.NextIdentity.IsNone)
        Assert.True(evidence.ProjectPages.Head.NextIdentity.IsNone)
        Assert.Equal(4, transport.Calls.Length)
        Assert.All(transport.Calls, fun request ->
            match request with
            | Rest value -> Assert.Equal(Some "Bearer synthetic-controlled-token", Map.tryFind "authorization" value.Headers)
            | GraphQL value -> Assert.Equal(Some "Bearer synthetic-controlled-token", Map.tryFind "authorization" value.Headers))

[<Fact>]
let ``fixture census includes nonce marker on another issue`` () =
    let nonce = "77-1-" + String.replicate 40 "a"
    let marked = issue2.Replace("\"title\":\"unowned\"", "\"title\":\"foreign [fsgg:gs2-09-7:" + nonce + "]\"")
    let reader, _ = adapter [ reply repo; reply ("[" + issue1 + "," + marked + "]")
                              exactIssue issue1; reply projectEmpty ]
    match reader.ObserveFixture(nonce) with
    | Error failure -> failwithf "nonce census refused: %A" failure
    | Ok observed -> Assert.Equal([ "ISSUE_2" ], observed.NonceIssueNodeIds)

[<Fact>]
let ``fixture refuses changed exact issue missing ETag partial Project and missing terminal page`` () =
    let nonce = "77-1-" + String.replicate 40 "a"
    let mismatched, _ = adapter [ reply repo; reply ("[" + issue1 + "]")
                                  exactIssue (issue1.Replace("baseline", "changed")); reply projectEmpty ]
    Assert.Equal(Error MigrationSandboxProviderFailure.PopulationDrift, mismatched.ReadFixture nonce)
    let noEtag, _ = adapter [ reply repo; reply ("[" + issue1 + "]"); reply issue1 ]
    Assert.Equal(Error MigrationSandboxProviderFailure.MissingRevision, noEtag.ReadFixture nonce)
    let partialProject = projectEmpty.[0 .. projectEmpty.Length - 2] + ",\"errors\":[{\"message\":\"partial\"}]}"
    let partial, _ = adapter [ reply repo; reply ("[" + issue1 + "]"); exactIssue issue1; reply partialProject ]
    Assert.Equal(Error MigrationSandboxProviderFailure.GraphQLErrors, partial.ReadFixture nonce)
    let pending = projectEmpty.Replace("\"hasNextPage\":false,\"endCursor\":null",
                                       "\"hasNextPage\":true,\"endCursor\":\"cursor-1\"")
    let missing, _ = adapter [ reply repo; reply ("[" + issue1 + "]"); exactIssue issue1; reply pending ]
    Assert.Equal(Error MigrationSandboxProviderFailure.TransportRefused, missing.ReadFixture nonce)

[<Fact>]
let ``fixture refuses foreign Project and escaped issue pagination before dispatch`` () =
    let nonce = "77-1-" + String.replicate 40 "a"
    let foreign, _ = adapter [ reply repo; reply ("[" + issue1 + "]"); exactIssue issue1
                               reply (projectEmpty.Replace("PVT_kwDOEYAWY84BiESo", "FOREIGN")) ]
    Assert.Equal(Error MigrationSandboxProviderFailure.ForeignIdentity, foreign.ReadFixture nonce)
    let linked =
        Response { StatusCode=200
                   Headers=Map.ofList [ "link", "<https://foreign.example/repos/FS-GG/FS.GG.GitHub.Substrate.Sandbox/issues?page=2>; rel=\"next\"" ]
                   Body="[" + issue1 + "]"; ETag=None
                   RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let escaped, transport = adapter [ reply repo; linked ]
    Assert.True(escaped.ReadFixture(nonce).IsError)
    Assert.Equal(2, transport.Calls.Length)

[<Fact>]
let ``fixture refuses inconsistent issue membership duplicate JSON and invalid nonce`` () =
    let nonce = "77-1-" + String.replicate 40 "a"
    let contradictory = projectWithItem.Replace("\"id\":\"ISSUE_1\"", "\"id\":\"FOREIGN_ISSUE\"")
    let memberDrift, _ = adapter [ reply repo; reply ("[" + issue1 + "]"); exactIssue issue1; reply contradictory ]
    Assert.Equal(Error MigrationSandboxProviderFailure.PopulationDrift, memberDrift.ReadFixture nonce)
    let duplicated = issue1.Replace("\"title\":", "\"title\":\"other\",\"title\":")
    let duplicate, _ = adapter [ reply repo; reply ("[" + duplicated + "]") ]
    Assert.True(duplicate.ReadFixture nonce |> Result.isError)
    let invalid, transport = adapter []
    Assert.Equal(Error MigrationSandboxProviderFailure.InvalidNonce,
                 invalid.ReadFixture("wrong/nonce"))
    Assert.Empty(transport.Calls)
