module FS.GG.Coordination.MigrationGitHubReadTests

open System
open System.Collections.Generic
open System.Net.Http
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="test-token"
      UserAgent="fsgg-migration-test"
      Owner="FS-GG"
      Repository="copy"
      ExpectedRepositoryId=42L }

let private projectOptions =
    { GraphQLUri=Uri "https://api.github.test/graphql"
      Token="test-token"
      UserAgent="fsgg-migration-test"
      Organization="FS-GG"
      ProjectNumber=1
      ExpectedProjectNodeId="PROJECT_1" }

let private ok headers body =
    Response
        { StatusCode=200; Headers=headers; Body=body; ETag=None
          RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=Some(DateTimeOffset.UtcNow.AddHours 1.); Cost=Some 1 } }

let private repo = ok Map.empty """{"id":42,"full_name":"FS-GG/copy"}"""

let private repositoryCore =
    """{"id":42,"node_id":"REPO_42","full_name":"FS-GG/copy","default_branch":"main","visibility":"private","archived":false,"disabled":false,"has_issues":true,"allow_squash_merge":true,"allow_merge_commit":false,"allow_rebase_merge":false,"delete_branch_on_merge":true}"""

let private issue number nodeId =
    $"""{{"number":{number},"id":{number + 100},"node_id":"{nodeId}","state":"open","updated_at":"2026-09-23T10:00:00Z"}}"""

let private relationNode id repositoryId =
    $"""{{"id":"{id}","repository":{{"databaseId":{repositoryId}}}}}"""

let private relationPage (nodes: string list) total hasNext cursor =
    let joined = String.concat "," nodes
    let cursorJson = cursor |> Option.map (fun value -> $"\"{value}\"") |> Option.defaultValue "null"
    $"""{{"totalCount":{total},"nodes":[{joined}],"pageInfo":{{"hasNextPage":{(if hasNext then "true" else "false")},"endCursor":{cursorJson}}}}}"""

let private relationConnection (nodes: string list) hasNext =
    relationPage nodes nodes.Length hasNext None

let private relationReply id number parent children blockers blocking =
    let parentJson = parent |> Option.defaultValue "null"
    $"""{{"data":{{"node":{{"id":"{id}","number":{number},"updatedAt":"2026-09-23T10:00:00Z","repository":{{"databaseId":42}},"parent":{parentJson},"subIssues":{relationConnection children false},"blockedBy":{relationConnection blockers false},"blocking":{relationConnection blocking false}}}}}}}"""

let private relationInitialBlocking connection =
    let empty = relationConnection [] false
    $"""{{"data":{{"node":{{"id":"ISSUE_1","number":1,"updatedAt":"2026-09-23T10:00:00Z","repository":{{"databaseId":42}},"parent":null,"subIssues":{empty},"blockedBy":{empty},"blocking":{connection}}}}}}}"""

let private relationContinuationBlocking connection =
    $"""{{"data":{{"node":{{"id":"ISSUE_1","number":1,"updatedAt":"2026-09-23T10:00:00Z","repository":{{"databaseId":42}},"blocking":{connection}}}}}}}"""

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

[<Fact>]
let ``repository core settings retain exact response and use only a GET`` () =
    let responseBody = " \n" + repositoryCore + "\n"
    let transport = FakeTransport [ ok Map.empty responseBody ]
    match MigrationGitHubRead.readRepositoryCoreSettings options transport with
    | Ok observed ->
        Assert.Equal(42L, observed.RepositoryId)
        Assert.Equal("REPO_42", observed.NodeId)
        Assert.Equal("main", observed.DefaultBranch)
        Assert.Equal("private", observed.Visibility)
        Assert.True(observed.HasIssues)
        Assert.False(observed.AllowMergeCommit)
        Assert.Equal(responseBody, observed.PayloadJson)
        let digest = responseBody |> Encoding.UTF8.GetBytes |> SHA256.HashData
                     |> Convert.ToHexString |> _.ToLowerInvariant()
        Assert.Equal(digest, observed.PayloadSha256)
        Assert.Single(transport.Requests) |> ignore
        match transport.Requests.Head with
        | Rest request ->
            Assert.Equal(Get, request.Method)
            Assert.Equal("https://api.github.test/repos/FS-GG/copy", request.Uri.AbsoluteUri)
        | _ -> failwith "repository settings reader issued a non-REST request"
    | Error failure -> failwithf "unexpected repository settings refusal: %A" failure

[<Fact>]
let ``repository core settings refuse identity drift and unknown visibility`` () =
    for changed in
        [ repositoryCore.Replace("\"id\":42", "\"id\":43")
          repositoryCore.Replace("FS-GG/copy", "FS-GG/other") ] do
        let transport = FakeTransport [ ok Map.empty changed ]
        Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                     MigrationGitHubRead.readRepositoryCoreSettings options transport)
    let unknown = FakeTransport [ ok Map.empty (repositoryCore.Replace("\"visibility\":\"private\"", "\"visibility\":\"mystery\"")) ]
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "invalid:visibility"),
                 MigrationGitHubRead.readRepositoryCoreSettings options unknown)

[<Fact>]
let ``repository core settings refuse missing and malformed fields without another request`` () =
    for changed in
        [ repositoryCore.Replace("\"allow_merge_commit\":false,", "")
          repositoryCore.Replace("\"archived\":false", "\"archived\":null")
          repositoryCore.Replace("\"default_branch\":\"main\"", "\"default_branch\":\"\"") ] do
        let transport = FakeTransport [ ok Map.empty changed ]
        match MigrationGitHubRead.readRepositoryCoreSettings options transport with
        | Error(MigrationReadFailure.MalformedResponse _) -> ()
        | outcome -> failwithf "malformed repository settings were accepted: %A" outcome
        Assert.Single(transport.Requests) |> ignore

[<Fact>]
let ``repository core settings refuse invalid options and unavailable provider`` () =
    let invalid = { options with ExpectedRepositoryId=0L }
    let noRequests = FakeTransport []
    Assert.Equal(Error MigrationReadFailure.InvalidOptions,
                 MigrationGitHubRead.readRepositoryCoreSettings invalid noRequests)
    Assert.Empty(noRequests.Requests)
    let unavailable = FakeTransport [ NetworkFailure ]
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readRepositoryCoreSettings options unavailable)

let private propertySchema =
    """[{"property_name":"environment","source_type":"organization","value_type":"single_select","required":true,"require_explicit_values":true,"values_editable_by":"org_actors","default_value":"production","allowed_values":["production","development"]},{"property_name":"teams","source_type":"organization","value_type":"multi_select","required":false,"values_editable_by":null,"allowed_values":["backend","frontend"]},{"property_name":"approved","source_type":"organization","value_type":"true_false","required":false}]"""

let private propertyValues =
    """[{"property_name":"environment","value":"production"},{"property_name":"teams","value":["backend"]},{"property_name":"approved","value":true}]"""

[<Fact>]
let ``custom properties bind exact organization schema and repository values without writes`` () =
    let transport = FakeTransport [ repo; ok Map.empty propertySchema; ok Map.empty propertyValues ]
    match MigrationGitHubRead.readCustomProperties options transport with
    | Error failure -> failwithf "unexpected custom-property refusal: %A" failure
    | Ok observed ->
        Assert.Equal(42L, observed.RepositoryId)
        Assert.Equal("FS-GG/copy", observed.RepositoryFullName)
        Assert.Equal("""{"id":42,"full_name":"FS-GG/copy"}""", observed.IdentityPayloadJson)
        Assert.Equal(3, observed.Definitions.Length)
        Assert.Equal(3, observed.Values.Length)
        Assert.Equal(Some true, observed.Definitions.Head.RequireExplicitValues)
        Assert.Equal(Some(Some "org_actors"), observed.Definitions.Head.ValuesEditableBy)
        Assert.Equal(Some None, observed.Definitions.[1].ValuesEditableBy)
        Assert.Equal(None, observed.Definitions.[2].ValuesEditableBy)
        Assert.Equal(propertySchema, observed.SchemaPayloadJson)
        Assert.Equal(propertyValues, observed.ValuesPayloadJson)
        let hash (body: string) =
            body |> Encoding.UTF8.GetBytes |> SHA256.HashData
            |> Convert.ToHexString |> _.ToLowerInvariant()
        Assert.Equal(hash propertySchema, observed.SchemaPayloadSha256)
        Assert.Equal(hash propertyValues, observed.ValuesPayloadSha256)
        Assert.Equal(3, transport.Requests.Length)
        let uris =
            transport.Requests
            |> List.map (function
                | Rest request ->
                    Assert.Equal(Get, request.Method)
                    Assert.True(request.Body.IsNone)
                    request.Uri.AbsoluteUri
                | _ -> failwith "custom-property reader issued GraphQL")
        Assert.Equal<string list>(
            [ "https://api.github.test/repos/FS-GG/copy"
              "https://api.github.test/orgs/FS-GG/properties/schema"
              "https://api.github.test/repos/FS-GG/copy/properties/values" ], uris)

[<Fact>]
let ``custom properties refuse identity drift and stop before organization read`` () =
    let transport = FakeTransport [ ok Map.empty """{"id":43,"full_name":"FS-GG/copy"}""" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readCustomProperties options transport)
    Assert.Single(transport.Requests) |> ignore

[<Fact>]
let ``custom properties refuse unauthorized or missing schema and values`` () =
    for failingCall in [ 1; 2 ] do
        for status in [ 403; 404 ] do
            let refused =
                Response
                    { StatusCode=status; Headers=Map.empty; Body="{}"; ETag=None
                      RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
            let responses =
                if failingCall = 1 then [ repo; refused ]
                else [ repo; ok Map.empty propertySchema; refused ]
            let transport = FakeTransport responses
            Assert.Equal(Error(MigrationReadFailure.HttpRefused status),
                         MigrationGitHubRead.readCustomProperties options transport)
            Assert.Equal(failingCall + 1, transport.Requests.Length)

[<Fact>]
let ``custom properties refuse incomplete schema and malformed values`` () =
    let badSchemas =
        [ propertySchema.Replace("\"property_name\":\"teams\"", "\"property_name\":\"environment\"")
          propertySchema.Replace("\"property_name\":\"teams\"", "\"property_name\":\"teams\",\"property_name\":\"approved\"")
          propertySchema.Replace("\"default_value\":\"production\"", "\"default_value\":\"unknown\"")
          propertySchema.Replace("\"source_type\":\"organization\"", "\"source_type\":\"enterprise\"")
          propertySchema.Replace("\"required\":true,", "")
          propertySchema.Replace("\"require_explicit_values\":true", "\"require_explicit_values\":\"yes\"")
          propertySchema.Replace("\"values_editable_by\":\"org_actors\"", "\"values_editable_by\":\"everyone\"")
          propertySchema.Replace("\"value_type\":\"single_select\"", "\"value_type\":\"mystery\"")
          propertySchema.Substring(0, propertySchema.Length - 1) ]
    for schema in badSchemas do
        let transport = FakeTransport [ repo; ok Map.empty schema ]
        match MigrationGitHubRead.readCustomProperties options transport with
        | Error _ -> ()
        | Ok _ -> failwith "incomplete or contradictory schema accepted"
        Assert.Equal(2, transport.Requests.Length)
    let badValues =
        [ propertyValues.Replace("\"property_name\":\"approved\"", "\"property_name\":\"unknown\"")
          propertyValues.Replace("\"property_name\":\"approved\"", "\"property_name\":\"environment\"")
          propertyValues.Replace("\"property_name\":\"approved\"", "\"property_name\":\"approved\",\"property_name\":\"teams\"")
          propertyValues.Replace("\"value\":true", "\"value\":\"yes\"")
          propertyValues.Replace("\"value\":[\"backend\"]", "\"value\":[\"backend\",\"backend\"]")
          propertyValues.Replace("\"value\":\"production\"", "\"value\":\"unknown\"")
          propertyValues.Replace("[{\"property_name\":\"environment\",\"value\":\"production\"},", "")
          propertyValues + "{" ]
    for values in badValues do
        let transport = FakeTransport [ repo; ok Map.empty propertySchema; ok Map.empty values ]
        match MigrationGitHubRead.readCustomProperties options transport with
        | Error _ -> ()
        | Ok _ -> failwith "incomplete or contradictory values accepted"

[<Fact>]
let ``custom properties refuse pagination and non-200 partial response`` () =
    let linked = ok (Map.ofList [ "Link", "<https://api.github.test/next>; rel=\"next\"" ]) propertySchema
    let transport = FakeTransport [ repo; linked ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "unexpected:link"),
                 MigrationGitHubRead.readCustomProperties options transport)
    let partial =
        Response
            { StatusCode=206; Headers=Map.empty; Body=propertySchema; ETag=None
              RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
    let transport = FakeTransport [ repo; partial ]
    Assert.Equal(Error(MigrationReadFailure.HttpRefused 206),
                 MigrationGitHubRead.readCustomProperties options transport)

[<Fact>]
let ``custom properties refuse invalid URL value`` () =
    let schema = """[{"property_name":"site","source_type":"organization","value_type":"url","required":true}]"""
    let transport = FakeTransport [ repo; ok Map.empty schema; ok Map.empty """[{"property_name":"site","value":"not-a-url"}]""" ]
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "invalid:property-url"),
                 MigrationGitHubRead.readCustomProperties options transport)

[<Fact>]
let ``custom properties require an explicitly set value when schema says so`` () =
    let schema = """[{"property_name":"owner","source_type":"organization","value_type":"string","required":false,"require_explicit_values":true}]"""
    let transport = FakeTransport [ repo; ok Map.empty schema; ok Map.empty "[]" ]
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "missing:required-property-value"),
                 MigrationGitHubRead.readCustomProperties options transport)

let private rulesetSummary id name target enforcement =
    let targetField = target |> Option.map (fun value -> $"\"target\":\"{value}\",") |> Option.defaultValue ""
    $"""{{"id":{id},"node_id":"RRS_{id}","name":"{name}",{targetField}"source_type":"Repository","source":"FS-GG/copy","enforcement":"{enforcement}","updated_at":"2026-09-24T10:00:00Z"}}"""

let private rulesetDetail id name target =
    $"""{{"id":{id},"node_id":"RRS_{id}","name":"{name}","target":"{target}","source_type":"Repository","source":"FS-GG/copy","enforcement":"active","updated_at":"2026-09-24T10:00:00Z","conditions":{{"ref_name":{{"include":["refs/heads/main"],"exclude":[]}}}},"bypass_actors":[{{"actor_id":7,"actor_type":"Team","bypass_mode":"always"}}],"rules":[{{"type":"required_signatures"}}]}}"""

[<Fact>]
let ``repository rulesets close list pages and bind branch and tag details`` () =
    let first = rulesetSummary 91 "main" None "enabled"
    let second = rulesetSummary 92 "release" (Some "tag") "active"
    let next = "https://api.github.test/repos/FS-GG/copy/rulesets?per_page=100&page=2&includes_parents=true"
    let transport =
        FakeTransport
            [ repo
              ok (Map.ofList [ "Link", $"<{next}>; rel=\"next\"" ]) $"[{first}]"
              ok Map.empty $"[{second}]"
              ok Map.empty (rulesetDetail 91 "main" "branch")
              ok Map.empty (rulesetDetail 92 "release" "tag") ]
    match MigrationGitHubRead.readRepositoryBranchTagRulesets options transport with
    | Error failure -> failwithf "unexpected ruleset refusal: %A" failure
    | Ok observed ->
        Assert.Equal(42L, observed.RepositoryId)
        Assert.Equal(2, observed.PageCount)
        Assert.True(observed.Terminal)
        Assert.Equal(2, observed.ListPages.Length)
        Assert.Equal(Some next, observed.ListPages.Head.ListNextUri)
        Assert.Equal(2, observed.Rulesets.Length)
        Assert.Equal("branch", observed.Rulesets.Head.Target)
        Assert.Equal("tag", observed.Rulesets.[1].Target)
        Assert.Equal("active", observed.Rulesets.Head.Enforcement)
        Assert.Single(observed.Rulesets.Head.BypassActors) |> ignore
        Assert.Equal(5, transport.Requests.Length)
        transport.Requests
        |> List.iter (function
            | Rest request -> Assert.Equal(Get, request.Method); Assert.True(request.Body.IsNone)
            | _ -> failwith "ruleset reader issued GraphQL")

[<Fact>]
let ``repository rulesets refuse inherited and push targets before detail reads`` () =
    let baseRow = rulesetSummary 91 "main" (Some "branch") "active"
    for row in
        [ baseRow.Replace("\"source_type\":\"Repository\"", "\"source_type\":\"Organization\"")
          baseRow.Replace("\"target\":\"branch\"", "\"target\":\"push\"") ] do
        let transport = FakeTransport [ repo; ok Map.empty $"[{row}]" ]
        match MigrationGitHubRead.readRepositoryBranchTagRulesets options transport with
        | Error(MigrationReadFailure.MalformedResponse _) -> ()
        | outcome -> failwithf "inherited or push ruleset accepted: %A" outcome
        Assert.Equal(2, transport.Requests.Length)

[<Fact>]
let ``repository rulesets refuse hidden bypass and malformed detail`` () =
    let row = rulesetSummary 91 "main" (Some "branch") "active"
    let detail = rulesetDetail 91 "main" "branch"
    let malformed =
        [ detail.Replace("\"bypass_actors\":[{\"actor_id\":7,\"actor_type\":\"Team\",\"bypass_mode\":\"always\"}],", "")
          detail.Replace("\"include\":[\"refs/heads/main\"]", "\"include\":[]")
          detail.Replace("\"rules\":[{\"type\":\"required_signatures\"}]", "\"rules\":[{\"type\":7}]")
          detail.Replace("\"enforcement\":\"active\"", "\"enforcement\":\"disabled\"")
          detail.Replace("2026-09-24T10:00:00Z", "2026-09-25T10:00:00Z")
          detail.Replace("\"actor_id\":7,\"actor_type\":\"Team\",\"bypass_mode\":\"always\"",
                         "\"actor_id\":7,\"actor_type\":\"Team\",\"bypass_mode\":\"always\"},{\"actor_id\":7,\"actor_type\":\"Team\",\"bypass_mode\":\"exempt\"") ]
    for changed in malformed do
        let transport = FakeTransport [ repo; ok Map.empty $"[{row}]"; ok Map.empty changed ]
        match MigrationGitHubRead.readRepositoryBranchTagRulesets options transport with
        | Error _ -> ()
        | Ok _ -> failwith "hidden bypass or malformed ruleset accepted"

[<Fact>]
let ``repository rulesets refuse incomplete pages and provider failures`` () =
    let row = rulesetSummary 91 "main" (Some "branch") "active"
    let next = "https://api.github.test/repos/FS-GG/copy/rulesets?per_page=100&page=2&includes_parents=true"
    let firstPage = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) $"[{row}]"
    let repeated = FakeTransport [ repo; firstPage; ok Map.empty $"[{row}]" ]
    match MigrationGitHubRead.readRepositoryBranchTagRulesets options repeated with
    | Error(MigrationReadFailure.DuplicateIdentity _) -> ()
    | outcome -> failwithf "changed page census accepted: %A" outcome
    let escaped = "https://api.github.test/repos/FS-GG/foreign/rulesets?per_page=100&page=2&includes_parents=true"
    let transport = FakeTransport [ repo; ok (Map.ofList [ "link", $"<{escaped}>; rel=\"next\"" ]) $"[{row}]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readRepositoryBranchTagRulesets options transport)
    for status in [ 403; 404; 500 ] do
        let refused =
            Response
                { StatusCode=status; Headers=Map.empty; Body="{}"; ETag=None
                  RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
        let listFailure = FakeTransport [ repo; refused ]
        Assert.Equal(Error(MigrationReadFailure.HttpRefused status),
                     MigrationGitHubRead.readRepositoryBranchTagRulesets options listFailure)
        let detailFailure = FakeTransport [ repo; ok Map.empty $"[{row}]"; refused ]
        Assert.Equal(Error(MigrationReadFailure.HttpRefused status),
                     MigrationGitHubRead.readRepositoryBranchTagRulesets options detailFailure)

[<Fact>]
let ``repository ruleset page evidence detects changed raw bytes across reads`` () =
    let read body =
        let transport = FakeTransport [ repo; ok Map.empty body ]
        match MigrationGitHubRead.readRepositoryBranchTagRulesets options transport with
        | Ok observed -> observed.ListPages.Head.ListPayloadSha256
        | Error failure -> failwithf "unexpected empty ruleset refusal: %A" failure
    Assert.NotEqual(read "[]", read " [ ] ")

let private actionsAll =
    """{"enabled":true,"allowed_actions":"all","selected_actions_url":null,"sha_pinning_required":false}"""

let private actionsSelected =
    """{"enabled":true,"allowed_actions":"selected","selected_actions_url":"https://api.github.test/repositories/42/actions/permissions/selected-actions","sha_pinning_required":true}"""

let private selectedActions =
    """{"github_owned_allowed":true,"verified_allowed":false,"patterns_allowed":["FS-GG/*@*"]}"""

[<Fact>]
let ``repository Actions core policy reads only exact identity and policy GETs`` () =
    let transport = FakeTransport [ repo; ok Map.empty actionsAll ]
    match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
    | Error failure -> failwithf "unexpected Actions refusal: %A" failure
    | Ok observed ->
        Assert.Equal(42L, observed.RepositoryId)
        Assert.True(observed.Enabled)
        Assert.Equal("all", observed.AllowedActions)
        Assert.False(observed.ShaPinningRequired)
        Assert.Equal(None, observed.SelectedActionsUri)
        Assert.Equal(actionsAll, observed.PolicyPayloadJson)
        let expectedHash = actionsAll |> Encoding.UTF8.GetBytes |> SHA256.HashData
                           |> Convert.ToHexString |> _.ToLowerInvariant()
        Assert.Equal(expectedHash, observed.PolicyPayloadSha256)
        Assert.Equal(2, transport.Requests.Length)
        let uris =
            transport.Requests
            |> List.map (function
                | Rest request ->
                    Assert.Equal(Get, request.Method)
                    Assert.True(request.Body.IsNone)
                    request.Uri.AbsoluteUri
                | _ -> failwith "Actions reader issued GraphQL")
        Assert.Equal<string list>(
            [ "https://api.github.test/repos/FS-GG/copy"
              "https://api.github.test/repos/FS-GG/copy/actions/permissions" ], uris)

[<Fact>]
let ``selected Actions policy follows only the exact numeric repository URL`` () =
    let transport = FakeTransport [ repo; ok Map.empty actionsSelected; ok Map.empty selectedActions ]
    match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
    | Error failure -> failwithf "unexpected selected Actions refusal: %A" failure
    | Ok observed ->
        Assert.Equal("selected", observed.AllowedActions)
        Assert.Equal(Some true, observed.GitHubOwnedAllowed)
        Assert.Equal(Some false, observed.VerifiedAllowed)
        Assert.Equal(Some [ "FS-GG/*@*" ], observed.PatternsAllowed)
        Assert.Equal(Some selectedActions, observed.SelectedActionsPayloadJson)
        Assert.Equal(3, transport.Requests.Length)
        match transport.Requests.[2] with
        | Rest request ->
            Assert.Equal(Get, request.Method)
            Assert.Equal("https://api.github.test/repositories/42/actions/permissions/selected-actions",
                         request.Uri.AbsoluteUri)
        | _ -> failwith "selected Actions read was not REST"

[<Fact>]
let ``selected Actions policy accepts exact canonical repository URL`` () =
    let canonical = "https://api.github.test/repos/FS-GG/copy/actions/permissions/selected-actions"
    let policy =
        actionsSelected.Replace("https://api.github.test/repositories/42/actions/permissions/selected-actions", canonical)
    let transport = FakeTransport [ repo; ok Map.empty policy; ok Map.empty selectedActions ]
    match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
    | Error failure -> failwithf "canonical selected URL refused: %A" failure
    | Ok observed ->
        Assert.Equal(Some canonical, observed.SelectedActionsUri)
        match transport.Requests.[2] with
        | Rest request -> Assert.Equal(canonical, request.Uri.AbsoluteUri)
        | _ -> failwith "selected Actions read was not REST"

[<Fact>]
let ``repository Actions policy refuses foreign selected URL before dispatch`` () =
    for url in
        [ "https://foreign.example/repositories/42/actions/permissions/selected-actions"
          "https://api.github.test/repositories/43/actions/permissions/selected-actions"
          "http://api.github.test/repositories/42/actions/permissions/selected-actions"
          "https://api.github.test/repositories/42/actions/permissions/selected-actions?redirect=1" ] do
        let changed = actionsSelected.Replace("https://api.github.test/repositories/42/actions/permissions/selected-actions", url)
        let transport = FakeTransport [ repo; ok Map.empty changed ]
        Assert.Equal(Error(MigrationReadFailure.MalformedResponse "foreign:selected-actions-url"),
                     MigrationGitHubRead.readRepositoryActionsPolicy options transport)
        Assert.Equal(2, transport.Requests.Length)

[<Fact>]
let ``repository Actions policy refuses incomplete core and selected allowlist`` () =
    let malformedCore =
        [ actionsAll.Replace("\"enabled\":true", "\"enabled\":null")
          actionsAll.Replace("\"allowed_actions\":\"all\"", "\"allowed_actions\":\"mystery\"")
          actionsSelected.Replace("\"selected_actions_url\":\"https://api.github.test/repositories/42/actions/permissions/selected-actions\",", "")
          actionsAll.Replace("\"selected_actions_url\":null", "\"selected_actions_url\":\"https://api.github.test/repositories/42/actions/permissions/selected-actions\"") ]
    for core in malformedCore do
        let transport = FakeTransport [ repo; ok Map.empty core ]
        match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
        | Error _ -> ()
        | Ok _ -> failwith "incomplete Actions core accepted"
        Assert.Equal(2, transport.Requests.Length)
    let malformedSelected =
        [ selectedActions.Replace("\"verified_allowed\":false,", "")
          selectedActions.Replace("\"patterns_allowed\":[\"FS-GG/*@*\"]", "\"patterns_allowed\":[\"x\",\"x\"]")
          selectedActions.Replace("\"github_owned_allowed\":true", "\"github_owned_allowed\":\"yes\"") ]
    for selected in malformedSelected do
        let transport = FakeTransport [ repo; ok Map.empty actionsSelected; ok Map.empty selected ]
        match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
        | Error _ -> ()
        | Ok _ -> failwith "malformed selected Actions allowlist accepted"

[<Fact>]
let ``repository Actions policy refuses identity drift and HTTP failures`` () =
    let drift = FakeTransport [ ok Map.empty """{"id":43,"full_name":"FS-GG/copy"}""" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readRepositoryActionsPolicy options drift)
    Assert.Single(drift.Requests) |> ignore
    for status in [ 302; 403; 404; 500 ] do
        let refused =
            Response
                { StatusCode=status; Headers=Map.empty; Body="{}"; ETag=None
                  RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
        for responses in
            [ [ repo; refused ]
              [ repo; ok Map.empty actionsSelected; refused ] ] do
            let transport = FakeTransport responses
            Assert.Equal(Error(MigrationReadFailure.HttpRefused status),
                         MigrationGitHubRead.readRepositoryActionsPolicy options transport)

let private issueCensus () =
    let first = issue 1 "ISSUE_1"
    let second = issue 2 "ISSUE_2"
    let transport = FakeTransport [ repo; ok Map.empty $"[{first},{second}]" ]
    match MigrationGitHubRead.readIssues options transport with
    | Ok population -> population
    | Error failure -> failwithf "unexpected issue census refusal: %A" failure

let private pullRequest number nodeId =
    let head = String.replicate 40 "a"
    let baseRevision = String.replicate 40 "b"
    $"""{{"number":{number},"id":{number + 200},"node_id":"{nodeId}","state":"open","updated_at":"2026-09-23T10:00:00Z","head":{{"sha":"{head}"}},"base":{{"sha":"{baseRevision}","repo":{{"id":42}}}}}}"""

let private issuesWithPullRequests count =
    { issueCensus () with PullRequestCount=count }

[<Fact>]
let ``pull request census binds terminal pages to the issue count and raw page bytes`` () =
    let next = "https://api.github.test/repositories/42/pulls?state=all&per_page=100&after=cursor&page=2"
    let firstRecord = pullRequest 3 "PR_3"
    let secondRecord = pullRequest 4 "PR_4"
    let firstBody = $"[{firstRecord}]"
    let secondBody = $"[{secondRecord}]"
    let transport =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) firstBody
                        ok Map.empty secondBody ]
    match MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 2) transport with
    | Ok observed ->
        Assert.True(observed.Terminal)
        Assert.Equal(2, observed.PageCount)
        Assert.Equal<int list>([ 3; 4 ], observed.PullRequests |> List.map _.Number)
        Assert.Equal(2, observed.Pages.Length)
        Assert.Equal(Some next, observed.Pages.Head.NextUri)
        let digest = firstBody |> Encoding.UTF8.GetBytes |> SHA256.HashData
                     |> Convert.ToHexString |> _.ToLowerInvariant()
        Assert.Equal(digest, observed.Pages.Head.PayloadSha256)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "pull request census issued a non-REST request")
    | Error failure -> failwithf "unexpected pull request census refusal: %A" failure

[<Fact>]
let ``pull request census refuses missing skipped and escaped terminal pages`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/pulls?state=all&per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    let missing = FakeTransport [ repo; first ]
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 0) missing)
    let skipped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/pulls?state=all&per_page=100&page=3>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 0) skipped)
    let escaped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://other.test/pulls?state=all&per_page=100&page=2>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 0) escaped)

[<Fact>]
let ``pull request census refuses changed population duplicate and wrong base repository`` () =
    let first = pullRequest 3 "PR_3"
    let short = FakeTransport [ repo; ok Map.empty $"[{first}]" ]
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "pull-request-count"),
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 2) short)
    let duplicate = FakeTransport [ repo; ok Map.empty $"[{first},{first}]" ]
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "PR_3"),
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 2) duplicate)
    let wrongBase = first.Replace("\"id\":42", "\"id\":43")
    let drift = FakeTransport [ repo; ok Map.empty $"[{wrongBase}]" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 1) drift)

[<Fact>]
let ``pull request census refuses malformed revisions and a nonterminal issue census`` () =
    let malformed = (pullRequest 3 "PR_3").Replace(String.replicate 40 "a", "not-a-sha")
    let transport = FakeTransport [ repo; ok Map.empty $"[{malformed}]" ]
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "invalid:pull-request-revision"),
                 MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 1) transport)
    let noRequests = FakeTransport []
    let nonterminal = { issuesWithPullRequests 1 with Terminal=false }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "issue-census"),
                 MigrationGitHubRead.readPullRequests options nonterminal noRequests)
    Assert.Empty(noRequests.Requests)

let private issueComment id nodeId issueNumber =
    $"""{{"id":{id},"node_id":"{nodeId}","issue_url":"https://api.github.test/repos/FS-GG/copy/issues/{issueNumber}","body":"fsgg:claim payload","created_at":"2026-09-23T10:00:00Z","updated_at":"2026-09-23T10:01:00Z","user":{{"login":"reviewer"}}}}"""

let private pullRequestCensus () =
    let record = pullRequest 3 "PR_3"
    let transport = FakeTransport [ repo; ok Map.empty $"[{record}]" ]
    match MigrationGitHubRead.readPullRequests options (issuesWithPullRequests 1) transport with
    | Ok population -> population
    | Error failure -> failwithf "unexpected pull request census refusal: %A" failure

[<Fact>]
let ``pull request issue comments bind the exact PR census and use only GET`` () =
    let record = issueComment 501 "COMMENT_501" 3
    let transport = FakeTransport [ repo; ok Map.empty $"[{record}]" ]
    match MigrationGitHubRead.readPullRequestComments options (pullRequestCensus ()) 3 transport with
    | Ok observed ->
        Assert.Equal("PR_3", observed.SubjectNodeId)
        Assert.Equal(3, observed.SubjectNumber)
        Assert.Equal("COMMENT_501", observed.Comments.Head.NodeId)
        Assert.True(observed.Terminal)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "pull request comments issued a non-REST request")
    | Error failure -> failwithf "unexpected pull request comment refusal: %A" failure

[<Fact>]
let ``pull request comments refuse foreign subject missing page and missing census`` () =
    let foreign = issueComment 501 "COMMENT_501" 4
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readPullRequestComments options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{foreign}]" ]))
    let next = "https://api.github.test/repos/FS-GG/copy/issues/3/comments?per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readPullRequestComments options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; first ]))
    let noRequests = FakeTransport []
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "uncensused-pull-request"),
                 MigrationGitHubRead.readPullRequestComments options (pullRequestCensus ()) 99 noRequests)
    Assert.Empty(noRequests.Requests)

[<Fact>]
let ``pull request comments refuse nonterminal PR census before dispatch`` () =
    let noRequests = FakeTransport []
    let incomplete = { pullRequestCensus () with Terminal=false }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "pull-request-census"),
                 MigrationGitHubRead.readPullRequestComments options incomplete 3 noRequests)
    Assert.Empty(noRequests.Requests)

let private pullRequestReview id nodeId state number =
    let commit = String.replicate 40 "a"
    $"""{{"id":{id},"node_id":"{nodeId}","pull_request_url":"https://api.github.test/repos/FS-GG/copy/pulls/{number}","state":"{state}","commit_id":"{commit}","submitted_at":"2026-09-23T10:00:00Z","user":{{"login":"reviewer"}}}}"""

[<Fact>]
let ``pull request reviews retain exact terminal pages and native review facts`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/pulls/3/reviews?per_page=100&page=2"
    let first = pullRequestReview 601 "REVIEW_601" "APPROVED" 3
    let second = pullRequestReview 602 "REVIEW_602" "COMMENTED" 3
    let transport =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) $"[{first}]"
                        ok Map.empty $"[{second}]" ]
    match MigrationGitHubRead.readPullRequestReviews options (pullRequestCensus ()) 3 transport with
    | Ok observed ->
        Assert.Equal("PR_3", observed.PullRequestNodeId)
        Assert.Equal(2, observed.PageCount)
        Assert.Equal<string list>([ "APPROVED"; "COMMENTED" ], observed.Reviews |> List.map _.State)
        Assert.Equal(Some "reviewer", observed.Reviews.Head.ActorLogin)
        Assert.Equal(Some next, observed.Pages.Head.NextUri)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "pull request review reader issued a non-REST request")
    | Error failure -> failwithf "unexpected pull request review refusal: %A" failure

[<Fact>]
let ``pull request reviews refuse foreign subject unknown state and duplicates`` () =
    let foreign = pullRequestReview 601 "REVIEW_601" "APPROVED" 4
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readPullRequestReviews options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{foreign}]" ]))
    let unknown = pullRequestReview 601 "REVIEW_601" "UNKNOWN" 3
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "invalid:review-state"),
                 MigrationGitHubRead.readPullRequestReviews options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{unknown}]" ]))
    let first = pullRequestReview 601 "REVIEW_601" "APPROVED" 3
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "REVIEW_601"),
                 MigrationGitHubRead.readPullRequestReviews options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{first},{first}]" ]))

[<Fact>]
let ``pull request reviews refuse missing continuation and nonterminal source`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/pulls/3/reviews?per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readPullRequestReviews options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; first ]))
    let noRequests = FakeTransport []
    let incomplete = { pullRequestCensus () with Terminal=false }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "pull-request-census"),
                 MigrationGitHubRead.readPullRequestReviews options incomplete 3 noRequests)
    Assert.Empty(noRequests.Requests)

let private pullRequestReviewComment id nodeId number =
    $"""{{"id":{id},"node_id":"{nodeId}","pull_request_url":"https://api.github.test/repos/FS-GG/copy/pulls/{number}","pull_request_review_id":601,"path":"src/Program.fs","body":"please guard this","created_at":"2026-09-23T10:00:00Z","updated_at":"2026-09-23T10:01:00Z"}}"""

[<Fact>]
let ``inline review comments retain exact terminal pages and parent review identity`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/pulls/3/comments?per_page=100&page=2"
    let first = pullRequestReviewComment 701 "REVIEW_COMMENT_701" 3
    let second = pullRequestReviewComment 702 "REVIEW_COMMENT_702" 3
    let transport =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) $"[{first}]"
                        ok Map.empty $"[{second}]" ]
    match MigrationGitHubRead.readPullRequestReviewComments options (pullRequestCensus ()) 3 transport with
    | Ok observed ->
        Assert.Equal("PR_3", observed.PullRequestNodeId)
        Assert.Equal(2, observed.PageCount)
        Assert.Equal(Some 601L, observed.Comments.Head.ReviewId)
        Assert.Equal("src/Program.fs", observed.Comments.Head.Path)
        Assert.Equal(Some next, observed.Pages.Head.NextUri)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "inline review comment reader issued a non-REST request")
    | Error failure -> failwithf "unexpected inline review comment refusal: %A" failure

[<Fact>]
let ``inline review comments refuse cross-subject and malformed parent review`` () =
    let foreign = pullRequestReviewComment 701 "REVIEW_COMMENT_701" 4
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readPullRequestReviewComments options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{foreign}]" ]))
    let badParent = (pullRequestReviewComment 701 "REVIEW_COMMENT_701" 3).Replace("\"pull_request_review_id\":601", "\"pull_request_review_id\":0")
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "invalid:pull_request_review_id"),
                 MigrationGitHubRead.readPullRequestReviewComments options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{badParent}]" ]))

[<Fact>]
let ``inline review comments refuse missing page duplicate and nonterminal PR census`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/pulls/3/comments?per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readPullRequestReviewComments options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; first ]))
    let record = pullRequestReviewComment 701 "REVIEW_COMMENT_701" 3
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "REVIEW_COMMENT_701"),
                 MigrationGitHubRead.readPullRequestReviewComments options (pullRequestCensus ()) 3
                     (FakeTransport [ repo; ok Map.empty $"[{record},{record}]" ]))
    let noRequests = FakeTransport []
    let incomplete = { pullRequestCensus () with Terminal=false }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "pull-request-census"),
                 MigrationGitHubRead.readPullRequestReviewComments options incomplete 3 noRequests)
    Assert.Empty(noRequests.Requests)

[<Fact>]
let ``issue comment stream binds a censused subject and terminal raw pages`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues/1/comments?per_page=100&page=2"
    let firstRecord = issueComment 301 "COMMENT_301" 1
    let secondRecord = issueComment 302 "COMMENT_302" 1
    let firstBody = $"[{firstRecord}]"
    let secondBody = $"[{secondRecord}]"
    let transport =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) firstBody
                        ok Map.empty secondBody ]
    match MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 transport with
    | Ok observed ->
        Assert.Equal("ISSUE_1", observed.SubjectNodeId)
        Assert.Equal(2, observed.PageCount)
        Assert.Equal<int64 list>([ 301L; 302L ], observed.Comments |> List.map _.DatabaseId)
        Assert.Equal("fsgg:claim payload", observed.Comments.Head.Body)
        Assert.Equal(Some next, observed.Pages.Head.NextUri)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "comment stream issued a non-REST request")
    | Error failure -> failwithf "unexpected comment stream refusal: %A" failure

[<Fact>]
let ``issue comment stream refuses missing skipped and escaped continuation`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues/1/comments?per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    let missing = FakeTransport [ repo; first ]
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 missing)
    let skipped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/issues/1/comments?per_page=100&page=3>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 skipped)
    let escaped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/issues/2/comments?per_page=100&page=2>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 escaped)

[<Fact>]
let ``issue comment stream refuses duplicate and cross-subject comments`` () =
    let first = issueComment 301 "COMMENT_301" 1
    let duplicate = FakeTransport [ repo; ok Map.empty $"[{first},{first}]" ]
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "COMMENT_301"),
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 duplicate)
    let foreign = issueComment 301 "COMMENT_301" 2
    let drift = FakeTransport [ repo; ok Map.empty $"[{foreign}]" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 drift)

[<Fact>]
let ``issue comment stream refuses unavailable body and uncensused source`` () =
    let missingBody = (issueComment 301 "COMMENT_301" 1).Replace("\"body\":\"fsgg:claim payload\"", "\"body\":null")
    let transport = FakeTransport [ repo; ok Map.empty $"[{missingBody}]" ]
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "invalid:body"),
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 1 transport)
    let noRequests = FakeTransport []
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "uncensused-issue"),
                 MigrationGitHubRead.readIssueComments options (issueCensus ()) 99 noRequests)
    Assert.Empty(noRequests.Requests)

let private issueEvent id nodeId kind =
    $"""{{"id":{id},"node_id":"{nodeId}","event":"{kind}","created_at":"2026-09-23T10:00:00Z","actor":null}}"""

[<Fact>]
let ``issue event stream retains terminal provider pages and system actor absence`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues/1/events?per_page=100&page=2"
    let firstRecord = issueEvent 401 "EVENT_401" "labeled"
    let secondRecord = issueEvent 402 "EVENT_402" "added_to_project_v2"
    let transport =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) $"[{firstRecord}]"
                        ok Map.empty $"[{secondRecord}]" ]
    match MigrationGitHubRead.readIssueEvents options (issueCensus ()) 1 transport with
    | Ok observed ->
        Assert.Equal("ISSUE_1", observed.SubjectNodeId)
        Assert.Equal(2, observed.PageCount)
        Assert.Equal<string list>([ "labeled"; "added_to_project_v2" ],
                                  observed.Events |> List.map _.EventKind)
        Assert.Equal(None, observed.Events.Head.ActorLogin)
        Assert.Equal(Some next, observed.Pages.Head.NextUri)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "issue event stream issued a non-REST request")
    | Error failure -> failwithf "unexpected issue event refusal: %A" failure

[<Fact>]
let ``issue event stream refuses missing skipped and cross-subject pages`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues/1/events?per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readIssueEvents options (issueCensus ()) 1 (FakeTransport [ repo; first ]))
    let skipped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/issues/1/events?per_page=100&page=3>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssueEvents options (issueCensus ()) 1 skipped)
    let foreign = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/issues/2/events?per_page=100&page=2>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssueEvents options (issueCensus ()) 1 foreign)

[<Fact>]
let ``issue event stream refuses duplicates malformed actor and uncensused issue`` () =
    let first = issueEvent 401 "EVENT_401" "labeled"
    let duplicate = FakeTransport [ repo; ok Map.empty $"[{first},{first}]" ]
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "EVENT_401"),
                 MigrationGitHubRead.readIssueEvents options (issueCensus ()) 1 duplicate)
    let badActor = first.Replace("\"actor\":null", "\"actor\":{}")
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "missing:login"),
                 MigrationGitHubRead.readIssueEvents options (issueCensus ()) 1
                     (FakeTransport [ repo; ok Map.empty $"[{badActor}]" ]))
    let noRequests = FakeTransport []
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "uncensused-issue"),
                 MigrationGitHubRead.readIssueEvents options (issueCensus ()) 99 noRequests)
    Assert.Empty(noRequests.Requests)

[<Fact>]
let ``native relation reader proves reciprocal parent and blocking directions`` () =
    let first = relationReply "ISSUE_1" 1 None [relationNode "ISSUE_2" 42L] [] [relationNode "ISSUE_2" 42L]
    let second = relationReply "ISSUE_2" 2 (Some(relationNode "ISSUE_1" 42L)) [] [relationNode "ISSUE_1" 42L] []
    let transport = FakeTransport [ ok Map.empty first; ok Map.empty second ]
    match MigrationGitHubRead.readNativeRelations options (issueCensus ()) transport with
    | Ok population ->
        Assert.True(population.CompleteForRepository)
        Assert.Equal(2, population.IssueCount)
        Assert.Equal(0, population.ExternalEdgeCount)
        Assert.Equal(2, population.Edges.Length)
        Assert.Equal<MigrationRelationKind list>(
            [MigrationRelationKind.ParentChild; MigrationRelationKind.Blocks],
            population.Edges |> List.map _.Kind)
        Assert.All(population.Edges, fun edge ->
            Assert.Equal("ISSUE_1", edge.Source.NodeId)
            Assert.Equal("ISSUE_2", edge.Target.NodeId))
        Assert.All(population.Issues, fun record ->
            let digest = record.PayloadJson |> Encoding.UTF8.GetBytes |> SHA256.HashData
                         |> Convert.ToHexString |> _.ToLowerInvariant()
            Assert.Equal(record.PayloadSha256, digest))
        Assert.All(transport.Requests, fun request ->
            match request with
            | GraphQL value -> Assert.StartsWith("query", value.Document)
            | _ -> failwith "relation reader issued a non-GraphQL request")
    | Error failure -> failwithf "unexpected relation refusal: %A" failure

[<Fact>]
let ``native relation reader refuses missing reciprocal edge`` () =
    let first = relationReply "ISSUE_1" 1 None [relationNode "ISSUE_2" 42L] [] []
    let second = relationReply "ISSUE_2" 2 None [] [] []
    let transport = FakeTransport [ ok Map.empty first; ok Map.empty second ]
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "relation-reciprocity-or-census"),
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) transport)

[<Fact>]
let ``native relation reader refuses nested truncation and partial GraphQL data`` () =
    let empty = relationConnection [] false
    let truncated = relationConnection [relationNode "ISSUE_2" 42L] true
    let first =
        $"""{{"data":{{"node":{{"id":"ISSUE_1","number":1,"updatedAt":"2026-09-23T10:00:00Z","repository":{{"databaseId":42}},"parent":null,"subIssues":{truncated},"blockedBy":{empty},"blocking":{empty}}}}}}}"""
    let transport = FakeTransport [ ok Map.empty first ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "subIssues:missing-end-cursor-or-page"),
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) transport)
    let partial = FakeTransport [ ok Map.empty $"""{{"data":{{"node":null}},"errors":[{{"message":"forbidden"}}]}}""" ]
    Assert.Equal(Error MigrationReadFailure.GraphQLErrors,
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) partial)

[<Fact>]
let ``native relation reader follows a connection cursor and retains every provider page`` () =
    let first = relationInitialBlocking (relationPage [relationNode "EXTERNAL_A" 77L] 2 true (Some "cursor-1"))
    let continuation = relationContinuationBlocking (relationPage [relationNode "EXTERNAL_B" 77L] 2 false None)
    let second = relationReply "ISSUE_2" 2 None [] [] []
    let transport = FakeTransport [ ok Map.empty first; ok Map.empty continuation; ok Map.empty second ]
    match MigrationGitHubRead.readNativeRelations options (issueCensus ()) transport with
    | Ok population ->
        Assert.Equal(2, population.Edges.Length)
        Assert.Equal(2, population.ExternalEdgeCount)
        let evidence = population.Issues.Head.ContinuationPages
        Assert.Single(evidence) |> ignore
        Assert.Equal("blocking", evidence.Head.Connection)
        Assert.Equal("cursor-1", evidence.Head.RequestedCursor)
        let digest = evidence.Head.PayloadJson |> Encoding.UTF8.GetBytes |> SHA256.HashData
                     |> Convert.ToHexString |> _.ToLowerInvariant()
        Assert.Equal(evidence.Head.PayloadSha256, digest)
        match transport.Requests.[1] with
        | GraphQL request ->
            Assert.Equal("cursor-1", request.Variables.["after"])
            Assert.Contains("blocking(first:100,after:$after)", request.Document)
        | _ -> failwith "continuation was not a GraphQL read"
    | Error failure -> failwithf "unexpected continuation refusal: %A" failure

[<Fact>]
let ``native relation reader refuses absent repeated changed and duplicate continuation pages`` () =
    let first = relationInitialBlocking (relationPage [relationNode "EXTERNAL_A" 77L] 2 true (Some "cursor-1"))
    let run next =
        MigrationGitHubRead.readNativeRelations options (issueCensus ())
            (FakeTransport [ ok Map.empty first; next ])
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable, run NetworkFailure)
    let repeated = relationContinuationBlocking (relationPage [relationNode "EXTERNAL_B" 77L] 2 true (Some "cursor-1"))
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "blocking:cycle-or-page-limit"),
                 run (ok Map.empty repeated))
    let changed = relationContinuationBlocking (relationPage [relationNode "EXTERNAL_B" 77L] 3 false None)
    Assert.Equal(Error MigrationReadFailure.PopulationDrift, run (ok Map.empty changed))
    let duplicate = relationContinuationBlocking (relationPage [relationNode "EXTERNAL_A" 77L] 2 false None)
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "EXTERNAL_A"),
                 run (ok Map.empty duplicate))
    let changedRevision =
        (relationContinuationBlocking (relationPage [relationNode "EXTERNAL_B" 77L] 2 false None))
            .Replace("2026-09-23T10:00:00Z", "2026-09-23T10:01:00Z")
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 run (ok Map.empty changedRevision))
    let partial =
        ok Map.empty """{"data":{"node":null},"errors":[{"message":"not authorized"}]}"""
    Assert.Equal(Error MigrationReadFailure.GraphQLErrors, run partial)

[<Fact>]
let ``native relation reader refuses drift and vanished source issue`` () =
    let changed = (relationReply "ISSUE_1" 1 None [] [] []).Replace("2026-09-23T10:00:00Z", "2026-09-23T10:01:00Z")
    let transport = FakeTransport [ ok Map.empty changed ]
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) transport)
    let vanished = FakeTransport [ ok Map.empty """{"data":{"node":null}}""" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) vanished)

[<Fact>]
let ``native relation reader records external endpoints without inventing their reciprocal`` () =
    let first = relationReply "ISSUE_1" 1 None [] [relationNode "EXTERNAL" 77L] []
    let second = relationReply "ISSUE_2" 2 None [] [] []
    let transport = FakeTransport [ ok Map.empty first; ok Map.empty second ]
    match MigrationGitHubRead.readNativeRelations options (issueCensus ()) transport with
    | Ok population ->
        Assert.Equal(1, population.ExternalEdgeCount)
        Assert.Equal(MigrationRelationKind.Blocks, population.Edges.Head.Kind)
        Assert.Equal("EXTERNAL", population.Edges.Head.Source.NodeId)
        Assert.Equal("ISSUE_1", population.Edges.Head.Target.NodeId)
    | Error failure -> failwithf "unexpected external edge refusal: %A" failure

[<Fact>]
let ``native relation reader refuses duplicate and uncensused local endpoints`` () =
    let duplicate =
        relationReply "ISSUE_1" 1 None
            [relationNode "ISSUE_2" 42L; relationNode "ISSUE_2" 42L] [] []
    let duplicateTransport = FakeTransport [ ok Map.empty duplicate ]
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "ISSUE_2"),
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) duplicateTransport)
    let missing = relationReply "ISSUE_1" 1 None [relationNode "ISSUE_3" 42L] [] []
    let second = relationReply "ISSUE_2" 2 None [] [] []
    let missingTransport = FakeTransport [ ok Map.empty missing; ok Map.empty second ]
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "relation-reciprocity-or-census"),
                 MigrationGitHubRead.readNativeRelations options (issueCensus ()) missingTransport)

[<Fact>]
let ``read-only issue census follows all pages and excludes pull requests explicitly`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100&page=2"
    let firstIssue = issue 1 "ISSUE_1"
    let secondIssue = issue 3 "ISSUE_3"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ])
                    $"[{firstIssue},{{\"number\":2,\"pull_request\":{{}}}}]"
    let second = ok Map.empty $"[{secondIssue}]"
    let transport = FakeTransport [ repo; first; second ]
    let observed = MigrationGitHubRead.readIssues options transport
    match observed with
    | Ok population ->
        Assert.Equal(42L, population.RepositoryId)
        Assert.Equal(2, population.PageCount)
        Assert.True(population.Terminal)
        Assert.Equal(2, population.Pages.Length)
        Assert.Equal("https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100", population.Pages.Head.RequestedUri)
        Assert.Equal(Some next, population.Pages.Head.NextUri)
        Assert.Equal(next, population.Pages.[1].RequestedUri)
        Assert.Equal(None, population.Pages.[1].NextUri)
        let pageDigest (value: string) =
            value |> Encoding.UTF8.GetBytes |> SHA256.HashData
            |> Convert.ToHexString |> _.ToLowerInvariant()
        match first, second with
        | Response firstPage, Response lastPage ->
            Assert.Equal(pageDigest firstPage.Body, population.Pages.Head.PayloadSha256)
            Assert.Equal(pageDigest lastPage.Body, population.Pages.[1].PayloadSha256)
        | _ -> failwith "controlled pages were not responses"
        Assert.Equal<int list>([ 1; 3 ], population.Issues |> List.map _.Number)
        Assert.Equal(1, population.PullRequestCount)
        for issue in population.Issues do
            let actual =
                issue.PayloadJson |> Encoding.UTF8.GetBytes |> SHA256.HashData
                |> Convert.ToHexString |> _.ToLowerInvariant()
            Assert.Equal(issue.PayloadSha256, actual)
        Assert.All(transport.Requests, fun request ->
            match request with
            | Rest value -> Assert.Equal(Get, value.Method)
            | _ -> failwith "a read attempted a GraphQL mutation")
    | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``missing terminal page and off-scope continuation refuse`` () =
    let next = "https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    let missing = FakeTransport [ repo; first ]
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readIssues options missing)
    let escaped = FakeTransport [ repo; ok (Map.ofList [ "link", "<https://elsewhere.test/issues?state=all&per_page=100>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssues options escaped)
    Assert.Equal(2, escaped.Requests.Length)

[<Fact>]
let ``issue pagination refuses a skipped or substituted page`` () =
    let skipped =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=100&page=3>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssues options skipped)
    Assert.Equal(2, skipped.Requests.Length)

    let substituted =
        FakeTransport [ repo
                        ok (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/copy/issues?state=all&per_page=1000&page=2>; rel=\"next\"" ]) "[]" ]
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "continuation-escaped-scope"),
                 MigrationGitHubRead.readIssues options substituted)
    Assert.Equal(2, substituted.Requests.Length)

[<Fact>]
let ``issue pagination accepts a repository-id scoped opaque cursor from GitHub`` () =
    let next =
        "https://api.github.test/repositories/42/issues?state=all&per_page=100&after=Y3Vyc29y%3D&page=2"
    let first = ok (Map.ofList [ "link", $"<{next}>; rel=\"next\"" ]) "[]"
    let transport = FakeTransport [ repo; first; ok Map.empty "[]" ]
    match MigrationGitHubRead.readIssues options transport with
    | Ok population -> Assert.Equal(2, population.PageCount)
    | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``duplicate issue identity and repository drift refuse`` () =
    let firstIssue = issue 1 "ISSUE_1"
    let secondIssue = issue 2 "ISSUE_1"
    let duplicate = FakeTransport [ repo; ok Map.empty $"[{firstIssue},{secondIssue}]" ]
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "ISSUE_1"),
                 MigrationGitHubRead.readIssues options duplicate)
    let drift = FakeTransport [ ok Map.empty """{"id":43,"full_name":"FS-GG/copy"}""" ]
    Assert.Equal(Error MigrationReadFailure.IdentityDrift,
                 MigrationGitHubRead.readIssues options drift)
    Assert.Single(drift.Requests) |> ignore

[<Fact>]
let ``GraphQL partial data with an authorization error is never a complete type census`` () =
    let partial =
        """{"data":{"repository":{"databaseId":42,"issueTypes":{"nodes":[{"id":"IT_1","name":"Task"}],"pageInfo":{"hasNextPage":false,"endCursor":"NA"}}}},"errors":[{"type":"FORBIDDEN","message":"not accessible"}]}"""
    let transport = FakeTransport [ ok Map.empty partial ]
    Assert.Equal(Error MigrationReadFailure.GraphQLErrors,
                 MigrationGitHubRead.readIssueTypes options transport)
    Assert.Single(transport.Requests) |> ignore

[<Fact>]
let ``issue type census accepts GitHub terminal cursor and rejects missing continuation`` () =
    let terminal =
        """{"data":{"repository":{"databaseId":42,"issueTypes":{"nodes":[{"id":"IT_1","name":"Task"}],"pageInfo":{"hasNextPage":false,"endCursor":"NA"}}}}}"""
    let observed = MigrationGitHubRead.readIssueTypes options (FakeTransport [ ok Map.empty terminal ])
    match observed with
    | Ok population ->
        Assert.Equal(1, population.PageCount)
        Assert.Equal("Task", population.IssueTypes.Head.Name)
    | Error failure -> failwithf "unexpected refusal: %A" failure

    let missingCursor =
        """{"data":{"repository":{"databaseId":42,"issueTypes":{"nodes":[],"pageInfo":{"hasNextPage":true,"endCursor":null}}}}}"""
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "missing-end-cursor"),
                 MigrationGitHubRead.readIssueTypes options (FakeTransport [ ok Map.empty missingCursor ]))

[<Fact>]
let ``HTTP migration read transport refuses mutation-shaped requests before network send`` () =
    use client = new HttpClient()
    let transport = HttpMigrationGitHubReadTransport(client) :> IMigrationGitHubReadTransport
    let headers = Map.ofList [ "x-github-api-version", ApiVersion.value ApiVersion.required ]
    let rest =
        Rest { Method=Post; Uri=Uri "https://api.github.test/repos/FS-GG/copy/issues"
               Headers=headers; Body=Some "{}"; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
    let graphQL =
        GraphQL { Uri=options.GraphQLUri; Document="mutation { createIssue(input:{}) { clientMutationId } }"
                  Variables=Map.empty; Headers=headers; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
    Assert.Equal(NetworkFailure, transport.Send rest)
    Assert.Equal(NetworkFailure, transport.Send graphQL)

let private projectPage total hasNext cursor nodes =
    sprintf """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"items":{"totalCount":%d,"nodes":%s,"pageInfo":{"hasNextPage":%s,"endCursor":%s}}}}}}"""
        total nodes hasNext cursor

[<Fact>]
let ``Project census binds item identity and complete total across pages`` () =
    let issueItem =
        """{"id":"ITEM_1","isArchived":false,"updatedAt":"2026-09-23T10:00:00Z","content":{"__typename":"Issue","id":"ISSUE_1","number":7,"repository":{"databaseId":42}}}"""
    let draftItem =
        """{"id":"ITEM_2","isArchived":true,"updatedAt":"2026-09-23T10:01:00Z","content":{"__typename":"DraftIssue","id":"DRAFT_1"}}"""
    let first = projectPage 2 "true" "\"cursor-1\"" $"[{issueItem}]"
    let second = projectPage 2 "false" "\"cursor-2\"" $"[{draftItem}]"
    let transport = FakeTransport [ ok Map.empty first; ok Map.empty second ]
    match MigrationGitHubRead.readProjectItems projectOptions transport with
    | Ok population ->
        Assert.Equal(2, population.PageCount)
        Assert.Equal(2, population.TotalCount)
        Assert.Equal(2, population.Items.Length)
        Assert.Equal(MigrationProjectContent.Issue("ISSUE_1", 42L, 7), population.Items.Head.Content)
        Assert.Equal(MigrationProjectContent.DraftIssue "DRAFT_1", population.Items.Tail.Head.Content)
    | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``Project population drift and partial GraphQL response refuse`` () =
    let first = projectPage 2 "true" "\"cursor-1\"" "[]"
    let changed = projectPage 3 "false" "null" "[]"
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationGitHubRead.readProjectItems projectOptions
                     (FakeTransport [ ok Map.empty first; ok Map.empty changed ]))
    let truncated = projectPage 2 "false" "\"cursor-1\"" "[]"
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationGitHubRead.readProjectItems projectOptions
                     (FakeTransport [ ok Map.empty truncated ]))
    let partial =
        """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"items":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}},"errors":[{"type":"FORBIDDEN"}]}"""
    Assert.Equal(Error MigrationReadFailure.GraphQLErrors,
                 MigrationGitHubRead.readProjectItems projectOptions
                     (FakeTransport [ ok Map.empty partial ]))

[<Fact>]
let ``Project census rejects duplicate items and a missing cursor`` () =
    let item =
        """{"id":"ITEM_1","isArchived":false,"updatedAt":"2026-09-23T10:00:00Z","content":{"__typename":"DraftIssue","id":"DRAFT_1"}}"""
    let duplicate = projectPage 2 "false" "\"terminal\"" $"[{item},{item}]"
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "ITEM_1"),
                 MigrationGitHubRead.readProjectItems projectOptions
                     (FakeTransport [ ok Map.empty duplicate ]))
    let missingCursor = projectPage 0 "true" "null" "[]"
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "missing-end-cursor"),
                 MigrationGitHubRead.readProjectItems projectOptions
                     (FakeTransport [ ok Map.empty missingCursor ]))

let private fieldPage total hasNext cursor nodes =
    sprintf """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"fields":{"totalCount":%d,"nodes":%s,"pageInfo":{"hasNextPage":%s,"endCursor":%s}}}}}}"""
        total nodes hasNext cursor

[<Fact>]
let ``Project field census retains schema and options across terminal pagination`` () =
    let builtIn =
        """{"__typename":"ProjectV2Field","id":"FIELD_1","name":"Title","dataType":"TITLE"}"""
    let selected =
        """{"__typename":"ProjectV2SingleSelectField","id":"FIELD_2","name":"Status","dataType":"SINGLE_SELECT","options":[{"id":"OPT_1","name":"Ready"},{"id":"OPT_2","name":"Done"}]}"""
    let first = fieldPage 2 "true" "\"cursor-1\"" $"[{builtIn}]"
    let second = fieldPage 2 "false" "\"terminal\"" $"[{selected}]"
    let transport = FakeTransport [ ok Map.empty first; ok Map.empty second ]
    match MigrationGitHubRead.readProjectFields projectOptions transport with
    | Ok population ->
        Assert.Equal(2, population.TotalCount)
        Assert.Equal(2, population.PageCount)
        Assert.Equal(MigrationProjectFieldKind.BuiltIn, population.Fields.Head.Kind)
        Assert.Equal(MigrationProjectFieldKind.SingleSelect, population.Fields.Tail.Head.Kind)
        Assert.Equal<string list>([ "OPT_1"; "OPT_2" ], population.Fields.Tail.Head.Options |> List.map _.Id)
        Assert.Contains("\"options\"", population.Fields.Tail.Head.PayloadJson)
    | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``Project field census refuses unsupported kind duplicate option and population drift`` () =
    let unsupported =
        """{"__typename":"UnexpectedField","id":"FIELD_1","name":"Other","dataType":"TEXT"}"""
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "unsupported:project-field-kind"),
                 MigrationGitHubRead.readProjectFields projectOptions
                     (FakeTransport [ ok Map.empty (fieldPage 1 "false" "null" $"[{unsupported}]") ]))

    let duplicate =
        """{"__typename":"ProjectV2SingleSelectField","id":"FIELD_1","name":"Status","dataType":"SINGLE_SELECT","options":[{"id":"OPT_1","name":"Ready"},{"id":"OPT_1","name":"Done"}]}"""
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "OPT_1"),
                 MigrationGitHubRead.readProjectFields projectOptions
                     (FakeTransport [ ok Map.empty (fieldPage 1 "false" "null" $"[{duplicate}]") ]))

    let wrongType =
        """{"__typename":"ProjectV2SingleSelectField","id":"FIELD_1","name":"Status","dataType":"TEXT","options":[]}"""
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "unsupported:project-field-data-type"),
                 MigrationGitHubRead.readProjectFields projectOptions
                     (FakeTransport [ ok Map.empty (fieldPage 1 "false" "null" $"[{wrongType}]") ]))

    let first = fieldPage 2 "true" "\"cursor-1\"" "[]"
    let changed = fieldPage 3 "false" "null" "[]"
    Assert.Equal(Error MigrationReadFailure.PopulationDrift,
                 MigrationGitHubRead.readProjectFields projectOptions
                     (FakeTransport [ ok Map.empty first; ok Map.empty changed ]))

let private valuePage total hasNext cursor nodes =
    sprintf """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"items":{"totalCount":%d,"nodes":%s,"pageInfo":{"hasNextPage":%s,"endCursor":%s}}}}}}"""
        total nodes hasNext cursor

let private selectedValue =
    """{"__typename":"ProjectV2ItemFieldSingleSelectValue","id":"VALUE_1","updatedAt":"2026-09-23T10:00:00Z","field":{"id":"FIELD_1"},"optionId":"OPTION_1","name":"Ready"}"""

let private labelsValue nestedMore =
    sprintf """{"__typename":"ProjectV2ItemFieldLabelValue","field":{"id":"FIELD_2"},"labels":{"totalCount":1,"nodes":[{"id":"LABEL_1"}],"pageInfo":{"hasNextPage":%s,"endCursor":"L1"}}}""" nestedMore

let private valueItem values total nestedMore =
    sprintf """{"id":"ITEM_1","updatedAt":"2026-09-23T10:00:00Z","fieldValues":{"totalCount":%d,"nodes":%s,"pageInfo":{"hasNextPage":%s,"endCursor":"V1"}}}"""
        total values nestedMore

[<Fact>]
let ``Project values retain typed provider bytes and exact field identities`` () =
    let labels = labelsValue "false"
    let item = valueItem $"[{selectedValue},{labels}]" 2 "false"
    let transport = FakeTransport [ ok Map.empty (valuePage 1 "false" "\"terminal\"" $"[{item}]") ]
    match MigrationGitHubRead.readProjectValues projectOptions transport with
    | Ok population ->
        Assert.Equal(1, population.TotalCount)
        Assert.Equal(2, population.Items.Head.FieldValueCount)
        Assert.Equal<string list>([ "FIELD_1"; "FIELD_2" ],
                                  population.Items.Head.FieldValues |> List.map _.FieldNodeId)
        for field in population.Items.Head.FieldValues do
            let actual =
                field.PayloadJson |> Encoding.UTF8.GetBytes |> SHA256.HashData
                |> Convert.ToHexString |> _.ToLowerInvariant()
            Assert.Equal(field.PayloadSha256, actual)
        match transport.Requests with
        | [ GraphQL request ] -> Assert.Contains("projectV2(number: 1)", request.Document)
        | _ -> failwith "expected one read-only GraphQL query"
    | Error failure -> failwithf "unexpected refusal: %A" failure

[<Fact>]
let ``Project values refuse nested continuation and unsupported value data`` () =
    let incompleteLabels = labelsValue "true"
    let nestedItem = valueItem $"[{incompleteLabels}]" 1 "false"
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "nested:labels"),
                 MigrationGitHubRead.readProjectValues projectOptions
                     (FakeTransport [ ok Map.empty (valuePage 1 "false" "null" $"[{nestedItem}]") ]))

    let incompleteValues = valueItem $"[{selectedValue}]" 1 "true"
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "nested:fieldValues"),
                 MigrationGitHubRead.readProjectValues projectOptions
                     (FakeTransport [ ok Map.empty (valuePage 1 "false" "null" $"[{incompleteValues}]") ]))

    let unsupported =
        """{"__typename":"ProjectV2ItemIssueFieldValue","field":{"id":"FIELD_1"},"issueFieldValue":{"__typename":"IssueFieldTextValue"}}"""
    let unsupportedItem = valueItem $"[{unsupported}]" 1 "false"
    Assert.Equal(Error(MigrationReadFailure.MalformedResponse "unsupported:issue-field-value"),
                 MigrationGitHubRead.readProjectValues projectOptions
                     (FakeTransport [ ok Map.empty (valuePage 1 "false" "null" $"[{unsupportedItem}]") ]))

[<Fact>]
let ``Project values refuse a missing outer page and duplicate field identity`` () =
    let item = valueItem $"[{selectedValue}]" 1 "false"
    let first = valuePage 2 "true" "\"cursor-1\"" $"[{item}]"
    Assert.Equal(Error MigrationReadFailure.TransportUnavailable,
                 MigrationGitHubRead.readProjectValues projectOptions (FakeTransport [ ok Map.empty first ]))

    let duplicateItem = valueItem $"[{selectedValue},{selectedValue}]" 2 "false"
    Assert.Equal(Error(MigrationReadFailure.DuplicateIdentity "FIELD_1"),
                 MigrationGitHubRead.readProjectValues projectOptions
                     (FakeTransport [ ok Map.empty (valuePage 1 "false" "null" $"[{duplicateItem}]") ]))

[<Fact>]
let ``Project snapshot reconciles exact membership revisions declarations and bytes`` () =
    let membership =
        """{"id":"ITEM_1","isArchived":false,"updatedAt":"2026-09-23T10:00:00Z","content":{"__typename":"DraftIssue","id":"DRAFT_1"}}"""
    let declaration =
        """{"__typename":"ProjectV2SingleSelectField","id":"FIELD_1","name":"Status","dataType":"SINGLE_SELECT","options":[{"id":"OPTION_1","name":"Ready"}]}"""
    let items =
        MigrationGitHubRead.readProjectItems projectOptions
            (FakeTransport [ ok Map.empty (projectPage 1 "false" "null" $"[{membership}]") ])
        |> function Ok value -> value | Error failure -> failwithf "%A" failure
    let fields =
        MigrationGitHubRead.readProjectFields projectOptions
            (FakeTransport [ ok Map.empty (fieldPage 1 "false" "null" $"[{declaration}]") ])
        |> function Ok value -> value | Error failure -> failwithf "%A" failure
    let item = valueItem $"[{selectedValue}]" 1 "false"
    let values =
        MigrationGitHubRead.readProjectValues projectOptions
            (FakeTransport [ ok Map.empty (valuePage 1 "false" "null" $"[{item}]") ])
        |> function Ok value -> value | Error failure -> failwithf "%A" failure
    match MigrationGitHubRead.reconcileProject items fields values with
    | Ok snapshot ->
        Assert.Equal(1, snapshot.ItemCount)
        Assert.Equal(1, snapshot.FieldCount)
        Assert.Equal(1, snapshot.FieldValueCount)
        Assert.Equal(64, snapshot.NormalizedSha256.Length)
    | Error failure -> failwithf "unexpected refusal: %A" failure

    let stale =
        { values with Items=[ { values.Items.Head with UpdatedAt=values.Items.Head.UpdatedAt.AddSeconds 1. } ] }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "item-revision"),
                 MigrationGitHubRead.reconcileProject items fields stale)
    let unknownField =
        { values with Items=[ { values.Items.Head with
                                 FieldValues=[ { values.Items.Head.FieldValues.Head with FieldNodeId="FIELD_2" } ] } ] }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "undeclared-or-duplicate-field"),
                 MigrationGitHubRead.reconcileProject items fields unknownField)
    let altered =
        { items with Items=[ { items.Items.Head with PayloadJson="{}" } ] }
    Assert.Equal(Error(MigrationReadFailure.SnapshotMismatch "payload-digest"),
                 MigrationGitHubRead.reconcileProject altered fields values)
