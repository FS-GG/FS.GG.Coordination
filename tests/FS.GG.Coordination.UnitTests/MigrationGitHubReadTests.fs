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

let private issue number nodeId =
    $"""{{"number":{number},"id":{number + 100},"node_id":"{nodeId}","state":"open","updated_at":"2026-09-23T10:00:00Z"}}"""

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

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
