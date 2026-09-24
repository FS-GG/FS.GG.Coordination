module FS.GG.Coordination.MigrationInspectProviderAdapterTests

open System
open System.Collections.Generic
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private cohort =
    { Repositories=[ { Id=42L; NodeId="R_42"; FullName="FS-GG/copy"
                       SourceHead=String.replicate 40 "a"; TargetHead=String.replicate 40 "b" } ]
      ProjectOrganization="FS-GG"; ProjectNumber=1; ProjectNodeId="PROJECT_1"
      SourceRevision=String.replicate 40 "c"; Isolated=true }

let private options =
    { Cohort=cohort
      Repository={ ApiBase=Uri "https://api.github.test/"
                   GraphQLUri=Uri "https://api.github.test/graphql"
                   Token="controlled-test-token"; UserAgent="inspect-adapter-test"
                   Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }
      Project={ GraphQLUri=Uri "https://api.github.test/graphql"
                Token="controlled-test-token"; UserAgent="inspect-adapter-test"
                Organization="FS-GG"; ProjectNumber=1; ExpectedProjectNodeId="PROJECT_1" } }

let private reply body =
    Response { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
               RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=None; Cost=Some 1 } }

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let calls = ResizeArray<GitHubRequest * TransportOutcome>()
    member _.Calls = calls |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            let response = if queue.Count = 0 then NetworkFailure else queue.Dequeue()
            calls.Add(request, response)
            response

let private issueBody =
    """[{"number":1,"id":101,"node_id":"ISSUE_1","state":"open","updated_at":"2026-09-25T10:00:00Z"}]"""

let private projectItem =
    """{"id":"ITEM_1","isArchived":false,"updatedAt":"2026-09-25T10:00:00Z","content":{"__typename":"Issue","id":"ISSUE_1","number":1,"repository":{"databaseId":42}}}"""

let private projectPage total hasNext cursor nodes =
    """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"items":{"totalCount":TOTAL,"nodes":NODES,"pageInfo":{"hasNextPage":HAS_NEXT,"endCursor":CURSOR}}}}}}"""
        .Replace("TOTAL", string total).Replace("NODES", nodes)
        .Replace("HAS_NEXT", hasNext).Replace("CURSOR", cursor)

let private projectValuePage total hasNext cursor nodes =
    """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"items":{"totalCount":TOTAL,"nodes":NODES,"pageInfo":{"hasNextPage":HAS_NEXT,"endCursor":CURSOR}}}}}}"""
        .Replace("TOTAL", string total).Replace("NODES", nodes)
        .Replace("HAS_NEXT", hasNext).Replace("CURSOR", cursor)

let private readIssues responses =
    let transport = FakeTransport responses
    match MigrationGitHubRead.readIssues options.Repository transport with
    | Ok population -> population, transport.Calls
    | Error failure -> failwithf "Expected issue population: %A" failure

let private readProjectItems responses =
    let transport = FakeTransport responses
    match MigrationGitHubRead.readProjectItems options.Project transport with
    | Ok population -> population, transport.Calls
    | Error failure -> failwithf "Expected Project population: %A" failure

let private readProjectFields responses =
    let transport = FakeTransport responses
    match MigrationGitHubRead.readProjectFields options.Project transport with
    | Ok population -> population, transport.Calls
    | Error failure -> failwithf "Expected field population: %A" failure

let private readProjectValues responses =
    let transport = FakeTransport responses
    match MigrationGitHubRead.readProjectValues options.Project transport with
    | Ok population -> population, transport.Calls
    | Error failure -> failwithf "Expected value population: %A" failure

let private projectFieldPage total hasNext cursor nodes =
    """{"data":{"organization":{"projectV2":{"id":"PROJECT_1","number":1,"fields":{"totalCount":TOTAL,"nodes":NODES,"pageInfo":{"hasNextPage":HAS_NEXT,"endCursor":CURSOR}}}}}}"""
        .Replace("TOTAL", string total).Replace("NODES", nodes)
        .Replace("HAS_NEXT", hasNext).Replace("CURSOR", cursor)

[<Fact>]
let ``issue adapter binds exact repository request and raw parsed subjects`` () =
    let source = MigrationInspectProviderAdapter(options, FakeTransport [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply issueBody ])
                 :> IGitHubMigrationInspectSource
    match source.ReadAuthority(1, "issues-open-and-relevant-closed") with
    | Ok value ->
        Assert.True(value.ScopeVerified)
        Assert.True(value.SubjectsParsedFromRaw)
        Assert.Single(value.Pages) |> ignore
        Assert.Equal("repository:42:issue:1", value.Read.Subjects.Head.Identity)
    | Error reason -> failwithf "Unexpected issue adapter refusal: %s" reason

[<Fact>]
let ``foreign repository and raw typed issue mismatch refuse`` () =
    let foreign = MigrationInspectProviderAdapter(options, FakeTransport [ reply """{"id":43,"full_name":"FS-GG/foreign"}""" ])
                  :> IGitHubMigrationInspectSource
    Assert.True(foreign.ReadAuthority(1, "issues-open-and-relevant-closed") |> Result.isError)
    let population, calls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply issueBody ]
    let changed = { population with Issues=[ { population.Issues.Head with NodeId="FOREIGN" } ] }
    Assert.Equal(Error "issue-raw-typed-mismatch",
                 MigrationInspectProviderAdapter.bindIssues options changed calls)

[<Fact>]
let ``extra non-GET capture and unreconciled PR marker count refuse`` () =
    let population, calls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply issueBody ]
    let extra =
        match fst calls.Head with
        | Rest request -> Rest { request with Method=Post }
        | _ -> failwith "issue reader unexpectedly used GraphQL"
    Assert.Equal(Error "issue-capture-shape",
                 MigrationInspectProviderAdapter.bindIssues options population (calls @ [ extra, reply "{}" ]))
    Assert.Equal(Error "issue-pr-count",
                 MigrationInspectProviderAdapter.bindIssues options { population with PullRequestCount=1 } calls)
    let getWithBody =
        calls |> List.mapi (fun index (request, outcome) ->
            match request with
            | Rest value when index = 1 -> Rest { value with Body=Some "{}" }, outcome
            | _ -> request, outcome)
    Assert.Equal(Error "issue-capture-shape",
                 MigrationInspectProviderAdapter.bindIssues options population getWithBody)

[<Fact>]
let ``adapter transport refuses write shaped requests before dispatch`` () =
    let _, calls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply issueBody ]
    let inner = FakeTransport [ reply "{}" ]
    let guarded = MigrationInspectProviderAdapter.guardReadTransport options
                      "issues-open-and-relevant-closed" inner
    let mutation =
        match fst calls.Head with
        | Rest value -> Rest { value with Method=Post; Body=Some "{}" }
        | _ -> failwith "issue reader unexpectedly used GraphQL"
    Assert.Equal(NetworkFailure, guarded.Send mutation)
    Assert.Empty(inner.Calls)
    let _, projectCalls = readProjectItems [ reply (projectPage 1 "false" "null" $"[{projectItem}]") ]
    let projectInner = FakeTransport [ reply "{}" ]
    let projectGuarded = MigrationInspectProviderAdapter.guardReadTransport options "project-items" projectInner
    let graphQLMutation =
        match fst projectCalls.Head with
        | GraphQL value -> GraphQL { value with Document="mutation { deleteProjectV2(input:{}) { clientMutationId } }" }
        | _ -> failwith "Project reader unexpectedly used REST"
    Assert.Equal(NetworkFailure, projectGuarded.Send graphQLMutation)
    Assert.Empty(projectInner.Calls)

[<Fact>]
let ``Project adapter binds GraphQL owner cursor and copy content`` () =
    let first = projectPage 2 "true" "\"cursor-1\"" $"[{projectItem}]"
    let second = projectPage 2 "false" "\"cursor-2\""
                     """[{"id":"ITEM_2","isArchived":true,"updatedAt":"2026-09-25T10:01:00Z","content":{"__typename":"DraftIssue","id":"DRAFT_2"}}]"""
    let values =
        projectValuePage 2 "false" "null"
            """[{"id":"ITEM_1","updatedAt":"2026-09-25T10:00:00Z","fieldValues":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}},{"id":"ITEM_2","updatedAt":"2026-09-25T10:01:00Z","fieldValues":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}]"""
    let source = MigrationInspectProviderAdapter(options, FakeTransport [ reply first; reply second; reply values ])
                 :> IGitHubMigrationInspectSource
    match source.ReadAuthority(1, "project-items") with
    | Ok value ->
        Assert.True(value.ScopeVerified)
        Assert.True(value.SubjectsParsedFromRaw)
        Assert.Equal(3, value.Pages.Length)
        Assert.Equal(Some value.Pages.[1].RequestIdentitySha256,
                     value.Pages.Head.NextRequestIdentitySha256)
        Assert.Equal(Some value.Pages.[2].RequestIdentitySha256,
                     value.Pages.[1].NextRequestIdentitySha256)
        Assert.Equal(4, value.Read.Subjects.Length)
    | Error reason -> failwithf "Unexpected Project adapter refusal: %s" reason

[<Fact>]
let ``wrong GraphQL owner variables and changed typed Project item refuse`` () =
    let body = projectPage 1 "false" "null" $"[{projectItem}]"
    let population, calls = readProjectItems [ reply body ]
    let wrongOwner =
        calls |> List.map (function
            | GraphQL request, outcome ->
                GraphQL { request with Variables=Map.add "owner" "Foreign" request.Variables }, outcome
            | other -> other)
    Assert.Equal(Error "project-request-scope",
                 MigrationInspectProviderAdapter.bindProjectItems options population wrongOwner)
    let forgedDocument =
        calls |> List.map (function
            | GraphQL request, outcome ->
                GraphQL { request with Document=request.Document + " # forged second selection" }, outcome
            | other -> other)
    Assert.Equal(Error "project-request-scope",
                 MigrationInspectProviderAdapter.bindProjectItems options population forgedDocument)
    let emptyAfter =
        calls |> List.map (function
            | GraphQL request, outcome -> GraphQL { request with Variables=Map.add "after" "" request.Variables }, outcome
            | other -> other)
    Assert.Equal(Error "project-request-scope",
                 MigrationInspectProviderAdapter.bindProjectItems options population emptyAfter)
    let extraMutation =
        let _, issueCalls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply issueBody ]
        let request =
            match fst issueCalls.Head with
            | Rest value -> Rest { value with Method=Post; Body=Some "{}" }
            | _ -> failwith "issue reader unexpectedly used GraphQL"
        calls @ [ request, reply "{}" ]
    Assert.Equal(Error "project-page-count",
                 MigrationInspectProviderAdapter.bindProjectItems options population extraMutation)
    let changed = { population with Items=[ { population.Items.Head with ItemNodeId="FOREIGN" } ] }
    Assert.Equal(Error "project-raw-typed-mismatch",
                 MigrationInspectProviderAdapter.bindProjectItems options changed calls)

[<Fact>]
let ``foreign Project identity and wrong GraphQL continuation refuse`` () =
    let first = projectPage 2 "true" "\"cursor-1\"" $"[{projectItem}]"
    let second = projectPage 2 "false" "null"
                     """[{"id":"ITEM_2","isArchived":true,"updatedAt":"2026-09-25T10:01:00Z","content":{"__typename":"DraftIssue","id":"DRAFT_2"}}]"""
    let population, calls = readProjectItems [ reply first; reply second ]
    let wrongProject =
        calls |> List.map (fun (request, outcome) ->
            match outcome with
            | Response value -> request, Response { value with Body=value.Body.Replace("PROJECT_1", "PROJECT_FOREIGN") }
            | other -> request, other)
    Assert.Equal(Error "raw-project-parse-or-scope",
                 MigrationInspectProviderAdapter.bindProjectItems options population wrongProject)
    let wrongCursor =
        calls |> List.mapi (fun index (request, outcome) ->
            match request with
            | GraphQL value when index = 1 ->
                GraphQL { value with Variables=Map.add "after" "wrong-cursor" value.Variables }, outcome
            | _ -> request, outcome)
    Assert.Equal(Error "project-page-chain",
                 MigrationInspectProviderAdapter.bindProjectItems options population wrongCursor)

[<Fact>]
let ``missing terminal Project page partial GraphQL errors and unsupported authority refuse`` () =
    let first = projectPage 2 "true" "\"cursor-1\"" $"[{projectItem}]"
    let missing = MigrationInspectProviderAdapter(options, FakeTransport [ reply first ])
                  :> IGitHubMigrationInspectSource
    Assert.True(missing.ReadAuthority(1, "project-items") |> Result.isError)
    let full = projectPage 1 "false" "null" $"[{projectItem}]"
    let partial = full.Substring(0, full.Length - 1) + ",\"errors\":[{\"type\":\"FORBIDDEN\"}]}"
    let partialSource = MigrationInspectProviderAdapter(options, FakeTransport [ reply partial ])
                        :> IGitHubMigrationInspectSource
    Assert.True(partialSource.ReadAuthority(1, "project-items") |> Result.isError)

[<Fact>]
let ``Project field adapter binds raw declarations and terminal cursor chain`` () =
    let title = """{"__typename":"ProjectV2Field","id":"FIELD_1","name":"Title","dataType":"TITLE"}"""
    let status = """{"__typename":"ProjectV2SingleSelectField","id":"FIELD_2","name":"Status","dataType":"SINGLE_SELECT","options":[{"id":"OPT_1","name":"Ready"}]}"""
    let first = projectFieldPage 2 "true" "\"field-cursor\"" $"[{title}]"
    let last = projectFieldPage 2 "false" "null" $"[{status}]"
    let source = MigrationInspectProviderAdapter(options, FakeTransport [ reply first; reply last ])
                 :> IGitHubMigrationInspectSource
    match source.ReadAuthority(1, "project-fields") with
    | Ok value ->
        Assert.Equal(2, value.Pages.Length)
        Assert.Equal(2, value.Read.Subjects.Length)
        Assert.Equal(Some value.Pages.[1].RequestIdentitySha256,
                     value.Pages.[0].NextRequestIdentitySha256)
        Assert.True(value.ScopeVerified && value.SubjectsParsedFromRaw)
    | Error reason -> failwithf "Unexpected field adapter refusal: %s" reason
    let population, calls = readProjectFields [ reply first; reply last ]
    let changed = { population with Fields=[ { population.Fields.Head with Name="Changed" }; population.Fields.[1] ] }
    Assert.Equal(Error "project-raw-typed-mismatch",
                 MigrationInspectProviderAdapter.bindProjectFields options changed calls)
    let forged =
        calls |> List.map (function
            | GraphQL request, outcome -> GraphQL { request with Document=request.Document + " # hidden" }, outcome
            | other -> other)
    Assert.Equal(Error "project-request-scope",
                 MigrationInspectProviderAdapter.bindProjectFields options population forged)
    Assert.True((MigrationInspectProviderAdapter(options, FakeTransport [ reply first ])
                 :> IGitHubMigrationInspectSource).ReadAuthority(1, "project-fields") |> Result.isError)

[<Fact>]
let ``Project value adapter binds raw values and refuses typed or nested drift`` () =
    let selected =
        """{"__typename":"ProjectV2ItemFieldSingleSelectValue","id":"VALUE_1","field":{"id":"FIELD_1"},"optionId":"OPT_1"}"""
    let item =
        $"""{{"id":"ITEM_1","updatedAt":"2026-09-25T10:00:00Z","fieldValues":{{"totalCount":1,"nodes":[{selected}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}"""
    let body = projectValuePage 1 "false" "null" $"[{item}]"
    let population, calls = readProjectValues [ reply body ]
    match MigrationInspectProviderAdapter.bindProjectValues options population calls with
    | Ok value ->
        Assert.Single(value.Read.Subjects) |> ignore
        Assert.Equal("project-values", value.Read.Authority)
    | Error reason -> failwithf "Unexpected value adapter refusal: %s" reason
    let changedValue = { population.Items.Head.FieldValues.Head with ValueKind="forged" }
    let changed = { population with Items=[ { population.Items.Head with FieldValues=[ changedValue ] } ] }
    Assert.Equal(Error "project-raw-typed-mismatch",
                 MigrationInspectProviderAdapter.bindProjectValues options changed calls)
    let wrongOwner =
        calls |> List.map (function
            | GraphQL request, outcome ->
                GraphQL { request with Variables=Map.add "organization" "Foreign" request.Variables }, outcome
            | other -> other)
    Assert.Equal(Error "project-request-scope",
                 MigrationInspectProviderAdapter.bindProjectValues options population wrongOwner)
    let nestedItem = item.Replace("\"hasNextPage\":false", "\"hasNextPage\":true")
    let nested = projectValuePage 1 "false" "null" $"[{nestedItem}]"
    Assert.Equal(Error(MigrationReadFailure.PaginationRefused "nested:fieldValues"),
                 MigrationGitHubRead.readProjectValues options.Project (FakeTransport [ reply nested ]))
    let nestedCapture =
        calls |> List.map (fun (request, outcome) ->
            match outcome with
            | Response value -> request, Response { value with Body=nested }
            | other -> request, other)
    Assert.Equal(Error "project-value-shape",
                 MigrationInspectProviderAdapter.bindProjectValues options population nestedCapture)

[<Fact>]
let ``Project value pages require terminal continuation and exact Project identity`` () =
    let item1 =
        """{"id":"ITEM_1","updatedAt":"2026-09-25T10:00:00Z","fieldValues":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}"""
    let item2 =
        """{"id":"ITEM_2","updatedAt":"2026-09-25T10:01:00Z","fieldValues":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}"""
    let first = projectValuePage 2 "true" "\"value-cursor\"" $"[{item1}]"
    let last = projectValuePage 2 "false" "null" $"[{item2}]"
    let population, calls = readProjectValues [ reply first; reply last ]
    match MigrationInspectProviderAdapter.bindProjectValues options population calls with
    | Ok value ->
        Assert.Equal(2, value.Pages.Length)
        Assert.Equal(Some value.Pages.[1].RequestIdentitySha256,
                     value.Pages.[0].NextRequestIdentitySha256)
    | Error reason -> failwithf "Unexpected value continuation refusal: %s" reason
    let wrongCursor =
        calls |> List.mapi (fun index (request, outcome) ->
            match request with
            | GraphQL value when index = 1 ->
                GraphQL { value with Variables=Map.add "after" "wrong" value.Variables }, outcome
            | _ -> request, outcome)
    Assert.Equal(Error "project-page-chain",
                 MigrationInspectProviderAdapter.bindProjectValues options population wrongCursor)
    let foreign =
        calls |> List.map (fun (request, outcome) ->
            match outcome with
            | Response value -> request, Response { value with Body=value.Body.Replace("PROJECT_1", "PROJECT_FOREIGN") }
            | other -> request, other)
    Assert.Equal(Error "project-raw-page-or-scope",
                 MigrationInspectProviderAdapter.bindProjectValues options population foreign)
    let missing = MigrationInspectProviderAdapter(options, FakeTransport [ reply (projectPage 0 "false" "null" "[]"); reply first ])
                  :> IGitHubMigrationInspectSource
    Assert.True(missing.ReadAuthority(1, "project-items") |> Result.isError)

[<Fact>]
let ``Project item and value streams retain distinct sorted subjects and refuse drift`` () =
    let membershipBody = projectPage 1 "false" "null" $"[{projectItem}]"
    let valueItem =
        """{"id":"ITEM_1","updatedAt":"2026-09-25T10:00:00Z","fieldValues":{"totalCount":0,"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}"""
    let valuesBody = projectValuePage 1 "false" "null" $"[{valueItem}]"
    let items, itemCalls = readProjectItems [ reply membershipBody ]
    let values, valueCalls = readProjectValues [ reply valuesBody ]
    let itemProof =
        match MigrationInspectProviderAdapter.bindProjectItems options items itemCalls with
        | Ok proof -> proof | Error reason -> failwithf "Item proof refused: %s" reason
    let valueProof =
        match MigrationInspectProviderAdapter.bindProjectValues options values valueCalls with
        | Ok proof -> proof | Error reason -> failwithf "Value proof refused: %s" reason
    match MigrationInspectProviderAdapter.combineProjectItemsAndValues items values itemProof valueProof with
    | Ok combined ->
        Assert.Equal(2, combined.Pages.Length)
        Assert.Equal(2, combined.Read.Subjects.Length)
        Assert.True((combined.Read.Subjects |> List.sortBy _.Identity) = combined.Read.Subjects)
        Assert.Equal(2, combined.Read.Subjects |> List.map _.Identity |> Set.ofList |> Set.count)
        Assert.Equal(Some combined.Pages.[1].RequestIdentitySha256,
                     combined.Pages.[0].NextRequestIdentitySha256)
    | Error reason -> failwithf "Unexpected Project combination refusal: %s" reason
    let drift = { values with Items=[ { values.Items.Head with UpdatedAt=DateTimeOffset.Parse("2026-09-25T11:00:00Z") } ] }
    Assert.Equal(Error "project-item-value-drift",
                 MigrationInspectProviderAdapter.combineProjectItemsAndValues items drift itemProof valueProof)
    let noCalls = FakeTransport []
    let unsupported = MigrationInspectProviderAdapter(options, noCalls) :> IGitHubMigrationInspectSource
    Assert.Equal(Error "authority-adapter-unavailable:claim-and-event-streams",
                 unsupported.ReadAuthority(1, "claim-and-event-streams"))
    Assert.Empty(noCalls.Calls)
