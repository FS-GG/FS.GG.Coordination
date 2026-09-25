module FS.GG.Coordination.MigrationInspectProviderAdapterTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli
open FS.GG.Coordination.Qualification.Contracts

let private cohort =
    { Repositories=[ { Id=42L; NodeId="R_42"; FullName="FS-GG/copy"
                       SourceHead=String.replicate 40 "a"; TargetHead=String.replicate 40 "b" } ]
      Receivers=[ { Receiver="copy-receiver"; RepositoryId=42L
                    RefName="refs/heads/main"; ExpectedHead=String.replicate 40 "b" } ]
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

let private relationNode id repositoryId =
    sprintf """{"id":"%s","repository":{"databaseId":%d}}""" id repositoryId

let private relationConnection (nodes: string list) total hasNext cursor =
    let cursorJson = cursor |> Option.map (sprintf "\"%s\"") |> Option.defaultValue "null"
    sprintf """{"totalCount":%d,"nodes":[%s],"pageInfo":{"hasNextPage":%s,"endCursor":%s}}"""
        total (String.concat "," nodes) (if hasNext then "true" else "false") cursorJson

let private relationReply id number parent children blockers blocking =
    let empty = relationConnection [] 0 false None
    sprintf """{"data":{"node":{"id":"%s","number":%d,"updatedAt":"2026-09-25T10:00:00Z","repository":{"databaseId":42},"parent":%s,"subIssues":%s,"blockedBy":%s,"blocking":%s}}}"""
        id number (Option.defaultValue "null" parent)
        (Option.defaultValue empty children) (Option.defaultValue empty blockers) (Option.defaultValue empty blocking)

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
    Assert.Equal(Error "issue-pr-markers",
                 MigrationInspectProviderAdapter.bindIssues options
                     { population with PullRequestMarkerNumbers=[ 3 ] } calls)
    let getWithBody =
        calls |> List.mapi (fun index (request, outcome) ->
            match request with
            | Rest value when index = 1 -> Rest { value with Body=Some "{}" }, outcome
            | _ -> request, outcome)
    Assert.Equal(Error "issue-capture-shape",
                 MigrationInspectProviderAdapter.bindIssues options population getWithBody)

[<Fact>]
let ``issue adapter binds exact PR marker numbers from raw pages`` () =
    let marker = """{"number":3,"pull_request":{}}"""
    let body = issueBody.TrimEnd(']') + "," + marker + "]"
    let population, calls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply body ]
    Assert.Equal([ 3 ], population.PullRequestMarkerNumbers)
    Assert.True(MigrationInspectProviderAdapter.bindIssues options population calls |> Result.isOk)
    Assert.Equal(Error "issue-pr-markers",
                 MigrationInspectProviderAdapter.bindIssues options
                     { population with PullRequestMarkerNumbers=[ 4 ] } calls)

[<Fact>]
let ``issue adapter refuses ambiguous raw PR marker members`` () =
    let clean = """[{"number":3,"pull_request":{}}]"""
    let ambiguous = """[{"number":3,"pull_request":{},"pull_request":{}}]"""
    let population, calls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply clean ]
    let digest = ambiguous |> Encoding.UTF8.GetBytes |> SHA256.HashData
                 |> Convert.ToHexString |> _.ToLowerInvariant()
    let changed = { population with Pages=[ { population.Pages.Head with PayloadSha256=digest } ] }
    let changedCalls =
        calls |> List.mapi (fun index (request, outcome) ->
            if index = 1 then request, reply ambiguous else request, outcome)
    Assert.Equal(Error "raw-issue-parse",
                 MigrationInspectProviderAdapter.bindIssues options changed changedCalls)

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
    let fields = projectFieldPage 0 "false" "null" "[]"
    let source = MigrationInspectProviderAdapter(options, FakeTransport [ reply first; reply second; reply values; reply fields ])
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
    let emptyMembership = projectPage 0 "false" "null" "[]"
    let emptyValues = projectValuePage 0 "false" "null" "[]"
    let source = MigrationInspectProviderAdapter(options,
                     FakeTransport [ reply emptyMembership; reply emptyValues
                                     reply first; reply last; reply first; reply last ])
                 :> IGitHubMigrationInspectSource
    Assert.True(source.ReadAuthority(1, "project-fields") |> Result.isError)
    Assert.True(source.ReadAuthority(1, "project-items") |> Result.isOk)
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
    Assert.True((MigrationInspectProviderAdapter(options, FakeTransport [])
                 :> IGitHubMigrationInspectSource).ReadAuthority(2, "project-fields") |> Result.isError)

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
    let multi =
        """{"__typename":"ProjectV2ItemFieldMultiSelectValue","field":{"id":"FIELD_1"},"options":[{"id":"OPT_1","name":"Ready"},{"id":"OPT_2","name":"Done"}]}"""
    let multiItem =
        $"""{{"id":"ITEM_1","updatedAt":"2026-09-25T10:00:00Z","fieldValues":{{"totalCount":1,"nodes":[{multi}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}"""
    let multiBody = projectValuePage 1 "false" "null" $"[{multiItem}]"
    let multiPopulation, multiCalls = readProjectValues [ reply multiBody ]
    let duplicateOption = multiBody.Replace("\"OPT_2\"", "\"OPT_1\"")
    let duplicateCapture =
        multiCalls |> List.map (fun (request, outcome) ->
            match outcome with
            | Response value -> request, Response { value with Body=duplicateOption }
            | other -> request, other)
    Assert.Equal(Error "project-value-shape",
                 MigrationInspectProviderAdapter.bindProjectValues options multiPopulation duplicateCapture)

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
    let fieldBody = projectFieldPage 0 "false" "null" "[]"
    let source = MigrationInspectProviderAdapter(options,
                     FakeTransport [ reply membershipBody; reply valuesBody; reply fieldBody ])
                 :> IGitHubMigrationInspectSource
    match source.ReadAuthority(1, "project-items") with
    | Ok combined ->
        Assert.Equal(2, combined.Pages.Length)
        Assert.Equal(2, combined.Read.Subjects.Length)
        Assert.True((combined.Read.Subjects |> List.sortBy _.Identity) = combined.Read.Subjects)
        Assert.Equal(2, combined.Read.Subjects |> List.map _.Identity |> Set.ofList |> Set.count)
        Assert.Equal(Some combined.Pages.[1].RequestIdentitySha256,
                     combined.Pages.[0].NextRequestIdentitySha256)
    | Error reason -> failwithf "Unexpected Project combination refusal: %s" reason
    let driftItem = valueItem.Replace("10:00:00Z", "11:00:00Z")
    let driftValues = projectValuePage 1 "false" "null" $"[{driftItem}]"
    let drift = MigrationInspectProviderAdapter(options,
                    FakeTransport [ reply membershipBody; reply driftValues; reply fieldBody ])
                :> IGitHubMigrationInspectSource
    Assert.True(drift.ReadAuthority(1, "project-items") |> Result.isError)

[<Fact>]
let ``Project inspect refuses unknown value field and changed same-pass field proof`` () =
    let membership = projectPage 1 "false" "null" $"[{projectItem}]"
    let unknownValue =
        """{"__typename":"ProjectV2ItemFieldTextValue","id":"VALUE_1","field":{"id":"FIELD_UNKNOWN"},"text":"ready"}"""
    let valueItem =
        $"""{{"id":"ITEM_1","updatedAt":"2026-09-25T10:00:00Z","fieldValues":{{"totalCount":1,"nodes":[{unknownValue}],"pageInfo":{{"hasNextPage":false,"endCursor":null}}}}}}"""
    let values = projectValuePage 1 "false" "null" $"[{valueItem}]"
    let declared =
        """{"__typename":"ProjectV2Field","id":"FIELD_1","name":"Title","dataType":"TITLE"}"""
    let fieldPage = projectFieldPage 1 "false" "null" $"[{declared}]"
    let source = MigrationInspectProviderAdapter(options,
                     FakeTransport [ reply membership; reply values; reply fieldPage ])
                 :> IGitHubMigrationInspectSource
    Assert.Contains("undeclared-or-duplicate-field",
                    source.ReadAuthority(1, "project-items") |> function Error reason -> reason | Ok _ -> "")
    Assert.Equal(Error "project-item-field-proof-required",
                 source.ReadAuthority(1, "project-fields"))

    let emptyMembership = projectPage 0 "false" "null" "[]"
    let emptyValues = projectValuePage 0 "false" "null" "[]"
    let changedDeclaration = declared.Replace("FIELD_1", "FIELD_2").Replace("Title", "Other")
    let changedFieldPage = projectFieldPage 1 "false" "null" $"[{changedDeclaration}]"
    let changed = MigrationInspectProviderAdapter(options,
                      FakeTransport [ reply emptyMembership; reply emptyValues; reply fieldPage
                                      reply changedFieldPage ])
                  :> IGitHubMigrationInspectSource
    Assert.True(changed.ReadAuthority(1, "project-items") |> Result.isOk)
    Assert.Equal(Error "project-field-proof-drift", changed.ReadAuthority(1, "project-fields"))
    Assert.Equal(Error "project-item-field-proof-required", changed.ReadAuthority(2, "project-fields"))

[<Fact>]
let ``native relation adapter binds census and reciprocal raw edges`` () =
    let issue2 =
        """{"number":2,"id":102,"node_id":"ISSUE_2","state":"open","updated_at":"2026-09-25T10:00:00Z"}"""
    let issuesBody = $"[{issueBody.TrimStart('[').TrimEnd(']')},{issue2}]"
    let first =
        relationReply "ISSUE_1" 1 None
            (Some(relationConnection [ relationNode "ISSUE_2" 42L ] 1 false None))
            None (Some(relationConnection [ relationNode "ISSUE_2" 42L ] 1 false None))
    let second =
        relationReply "ISSUE_2" 2 (Some(relationNode "ISSUE_1" 42L)) None
            (Some(relationConnection [ relationNode "ISSUE_1" 42L ] 1 false None)) None
    let source = MigrationInspectProviderAdapter(options,
                     FakeTransport [ reply """{"id":42,"full_name":"FS-GG/copy"}"""
                                     reply issuesBody
                                     reply """{"id":42,"full_name":"FS-GG/copy"}"""
                                     reply issuesBody; reply first; reply second ])
                 :> IGitHubMigrationInspectSource
    Assert.True(source.ReadAuthority(1, "issues-open-and-relevant-closed") |> Result.isOk)
    match source.ReadAuthority(1, "hierarchy-and-dependencies") with
    | Ok proof ->
        Assert.Equal(3, proof.Pages.Length)
        Assert.Equal(4, proof.Read.Subjects.Length)
        Assert.Equal(Some proof.Pages.[1].RequestIdentitySha256,
                     proof.Pages.[0].NextRequestIdentitySha256)
        Assert.True(proof.ScopeVerified && proof.SubjectsParsedFromRaw)
    | Error reason -> failwithf "Unexpected relation adapter refusal: %s" reason

    let issues, issueCalls = readIssues [ reply """{"id":42,"full_name":"FS-GG/copy"}"""; reply issuesBody ]
    let issueProof =
        match MigrationInspectProviderAdapter.bindIssues options issues issueCalls with
        | Ok proof -> proof | Error reason -> failwithf "Issue proof refused: %s" reason
    let relationTransport = FakeTransport [ reply first; reply second ]
    let population =
        match MigrationGitHubRead.readNativeRelations options.Repository issues relationTransport with
        | Ok value -> value | Error failure -> failwithf "Relation reader refused: %A" failure
    let changed = { population with Edges=[] }
    match MigrationInspectProviderAdapter.bindNativeRelations
              options issues issueProof changed relationTransport.Calls with
    | Error reason -> Assert.StartsWith("relation-raw-or-scope:", reason)
    | Ok _ -> failwith "Forged relation edge set was accepted"
    let alteredIssueProof =
        { issueProof with Pages=[ { issueProof.Pages.Head with RawBody="[]" } ] }
    Assert.Equal(Error "relation-cohort-or-population",
                 MigrationInspectProviderAdapter.bindNativeRelations
                     options issues alteredIssueProof population relationTransport.Calls)
    let wrongIdentity =
        relationTransport.Calls |> List.mapi (fun index (request, outcome) ->
            match request with
            | GraphQL value when index = 0 ->
                GraphQL { value with Variables=Map.ofList [ "id", "FOREIGN" ] }, outcome
            | _ -> request, outcome)
    match MigrationInspectProviderAdapter.bindNativeRelations
              options issues issueProof population wrongIdentity with
    | Error reason -> Assert.StartsWith("relation-raw-or-scope:", reason)
    | Ok _ -> failwith "Wrong relation request identity was accepted"
    let extraMutation =
        let request =
            match fst relationTransport.Calls.Head with
            | GraphQL value -> GraphQL { value with Document="mutation { deleteIssue(input:{}) { clientMutationId } }" }
            | _ -> failwith "Expected GraphQL relation request"
        relationTransport.Calls @ [ request, reply "{}" ]
    Assert.Equal(Error "relation-request-scope",
                 MigrationInspectProviderAdapter.bindNativeRelations
                     options issues issueProof population extraMutation)

[<Fact>]
let ``native relation continuation contributes linked raw page evidence`` () =
    let issue2 =
        """{"number":2,"id":102,"node_id":"ISSUE_2","state":"open","updated_at":"2026-09-25T10:00:00Z"}"""
    let issue3 =
        """{"number":3,"id":103,"node_id":"ISSUE_3","state":"open","updated_at":"2026-09-25T10:00:00Z"}"""
    let issuesBody = $"[{issueBody.TrimStart('[').TrimEnd(']')},{issue2},{issue3}]"
    let first = relationReply "ISSUE_1" 1 None None None
                    (Some(relationConnection [ relationNode "ISSUE_2" 42L ] 2 true (Some "cursor-1")))
    let continuationConnection = relationConnection [ relationNode "ISSUE_3" 42L ] 2 false None
    let continuation =
        sprintf """{"data":{"node":{"id":"ISSUE_1","number":1,"updatedAt":"2026-09-25T10:00:00Z","repository":{"databaseId":42},"blocking":%s}}}"""
            continuationConnection
    let second = relationReply "ISSUE_2" 2 None None
                     (Some(relationConnection [ relationNode "ISSUE_1" 42L ] 1 false None)) None
    let third = relationReply "ISSUE_3" 3 None None
                    (Some(relationConnection [ relationNode "ISSUE_1" 42L ] 1 false None)) None
    let repository = reply """{"id":42,"full_name":"FS-GG/copy"}"""
    let source = MigrationInspectProviderAdapter(options,
                     FakeTransport [ repository; reply issuesBody; repository; reply issuesBody
                                     reply first; reply continuation; reply second; reply third ])
                 :> IGitHubMigrationInspectSource
    Assert.True(source.ReadAuthority(1, "issues-open-and-relevant-closed") |> Result.isOk)
    match source.ReadAuthority(1, "hierarchy-and-dependencies") with
    | Ok proof ->
        Assert.Equal(5, proof.Pages.Length)
        Assert.Equal(Some proof.Pages.[2].RequestIdentitySha256,
                     proof.Pages.[1].NextRequestIdentitySha256)
        Assert.Empty(proof.Pages.[2].Subjects)
        Assert.Equal(6, proof.Read.Subjects.Length)
    | Error reason -> failwithf "Unexpected continuation adapter refusal: %s" reason

[<Fact>]
let ``native relation adapter refuses multi repository and foreign endpoint cohorts`` () =
    let extraRepository =
        { cohort.Repositories.Head with Id=77L; NodeId="R_77"; FullName="FS-GG/other" }
    let foreignCohort =
        { options with Cohort={ cohort with Repositories=cohort.Repositories @ [ extraRepository ] } }
    let noCalls = FakeTransport []
    let multi = MigrationInspectProviderAdapter(foreignCohort, noCalls) :> IGitHubMigrationInspectSource
    Assert.Equal(Error "relation-single-repository-cohort-required",
                 multi.ReadAuthority(1, "hierarchy-and-dependencies"))
    Assert.Empty(noCalls.Calls)
    let foreign = relationReply "ISSUE_1" 1 None None
                      (Some(relationConnection [ relationNode "FOREIGN" 77L ] 1 false None)) None
    let source = MigrationInspectProviderAdapter(options,
                     FakeTransport [ reply """{"id":42,"full_name":"FS-GG/copy"}"""
                                     reply issueBody
                                     reply """{"id":42,"full_name":"FS-GG/copy"}"""
                                     reply issueBody; reply foreign ])
                 :> IGitHubMigrationInspectSource
    Assert.True(source.ReadAuthority(1, "issues-open-and-relevant-closed") |> Result.isOk)
    Assert.Equal(Error "relation-cohort-or-population", source.ReadAuthority(1, "hierarchy-and-dependencies"))

[<Fact>]
let ``native relation adapter refuses missing continuation foreign ID and mutation dispatch`` () =
    let first = relationReply "ISSUE_1" 1 None None None
                    (Some(relationConnection [ relationNode "ISSUE_1" 42L ] 2 true (Some "cursor")))
    let missing = MigrationInspectProviderAdapter(options,
                      FakeTransport [ reply """{"id":42,"full_name":"FS-GG/copy"}"""
                                      reply issueBody
                                      reply """{"id":42,"full_name":"FS-GG/copy"}"""
                                      reply issueBody; reply first ])
                  :> IGitHubMigrationInspectSource
    Assert.True(missing.ReadAuthority(1, "issues-open-and-relevant-closed") |> Result.isOk)
    Assert.True(missing.ReadAuthority(1, "hierarchy-and-dependencies") |> Result.isError)
    let inner = FakeTransport [ reply "{}" ]
    let guard = MigrationInspectProviderAdapter.guardRelationTransport options [ "ISSUE_1" ] inner
    let _, calls = readProjectItems [ reply (projectPage 1 "false" "null" $"[{projectItem}]") ]
    let foreignRequest, mutation =
        match fst calls.Head with
        | GraphQL value ->
            GraphQL { value with Document=MigrationGitHubRead.nativeRelationsQuery
                                 Variables=Map.ofList [ "id", "FOREIGN" ] },
            GraphQL { value with Document="mutation { deleteIssue(input:{}) { clientMutationId } }"
                                 Variables=Map.ofList [ "id", "ISSUE_1" ] }
        | _ -> failwith "Expected GraphQL"
    Assert.Equal(NetworkFailure, guard.Send foreignRequest)
    Assert.Equal(NetworkFailure, guard.Send mutation)
    Assert.Empty(inner.Calls)

[<Fact>]
let ``relation authority refuses changed issue page and missing pass proof`` () =
    let repository = reply """{"id":42,"full_name":"FS-GG/copy"}"""
    let changedIssueBody = issueBody.Replace("\"state\":\"open\"", "\"state\": \"open\"")
    let transport = FakeTransport [ repository; reply issueBody; repository; reply changedIssueBody ]
    let source = MigrationInspectProviderAdapter(options, transport) :> IGitHubMigrationInspectSource
    Assert.Equal(Error "relation-issue-proof-required", source.ReadAuthority(2, "hierarchy-and-dependencies"))
    Assert.True(source.ReadAuthority(1, "issues-open-and-relevant-closed") |> Result.isOk)
    Assert.Equal(Error "relation-issue-proof-drift", source.ReadAuthority(1, "hierarchy-and-dependencies"))
    Assert.Equal(4, transport.Calls.Length)
    let noCalls = FakeTransport []
    let unsupported = MigrationInspectProviderAdapter(options, noCalls) :> IGitHubMigrationInspectSource
    Assert.Equal(Error "authority-adapter-unavailable:claim-and-event-streams",
                 unsupported.ReadAuthority(1, "claim-and-event-streams"))
    Assert.Empty(noCalls.Calls)
