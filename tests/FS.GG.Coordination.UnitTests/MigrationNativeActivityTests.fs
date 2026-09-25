module FS.GG.Coordination.MigrationNativeActivityTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

let private digest (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private stamp = DateTimeOffset.Parse "2026-09-24T10:00:00Z"
let private raw = "{}"
let private rawDigest = digest raw

let private page path =
    let query = if path = "issues" || path = "pulls" then "?state=all&per_page=100" else "?per_page=100"
    { RequestedUri="https://api.github.test/repos/FS-GG/copy/" + path + query
      PayloadSha256=rawDigest; NextUri=None }

let private issue =
    { Number=1; DatabaseId=101L; NodeId="I_1"; State="open"
      UpdatedAt=stamp; PayloadJson=raw; PayloadSha256=rawDigest }

let private pullRequest =
    { Number=2; DatabaseId=102L; NodeId="P_2"; State="open"
      UpdatedAt=stamp; HeadSha=String.replicate 40 "a"; BaseSha=String.replicate 40 "b"
      PayloadJson=raw; PayloadSha256=rawDigest }

let private issueComment =
    { DatabaseId=201L; NodeId="IC_201"; SubjectNumber=1; ActorLogin="actor"
      CreatedAt=stamp; UpdatedAt=stamp; Body="claim"
      PayloadJson=raw; PayloadSha256=rawDigest }

let private issueEvent =
    { DatabaseId=202L; NodeId="IE_202"; SubjectNumber=1; EventKind="assigned"
      ActorLogin=Some "actor"; CreatedAt=stamp; PayloadJson=raw; PayloadSha256=rawDigest }

let private pullRequestComment =
    { issueComment with DatabaseId=203L; NodeId="PC_203"; SubjectNumber=2 }

let private review =
    { DatabaseId=204L; NodeId="R_204"; PullRequestNumber=2; State="APPROVED"
      ActorLogin=Some "reviewer"; CommitSha=Some(String.replicate 40 "a")
      SubmittedAt=Some stamp; PayloadJson=raw; PayloadSha256=rawDigest }

let private inlineComment =
    { DatabaseId=205L; NodeId="RC_205"; PullRequestNumber=2; ReviewId=Some 204L
      Path="src/a.fs"; Body="review"; CreatedAt=stamp; UpdatedAt=stamp
      PayloadJson=raw; PayloadSha256=rawDigest }

let private sample () : MigrationNativeActivityInput =
    { Issues={ RepositoryId=42L; PageCount=1; Terminal=true; Pages=[ page "issues" ]
               Issues=[ issue ]; PullRequestCount=1; PullRequestMarkerNumbers=[ 2 ] }
      PullRequests={ RepositoryId=42L; PageCount=1; Terminal=true
                     Pages=[ page "pulls" ]; PullRequests=[ pullRequest ] }
      IssueComments=[ { RepositoryId=42L; SubjectNumber=1; SubjectNodeId="I_1"
                        PageCount=1; Terminal=true; Pages=[ page "issues/1/comments" ]
                        Comments=[ issueComment ] } ]
      IssueEvents=[ { RepositoryId=42L; SubjectNumber=1; SubjectNodeId="I_1"
                      PageCount=1; Terminal=true; Pages=[ page "issues/1/events" ]
                      Events=[ issueEvent ] } ]
      PullRequestComments=[ { RepositoryId=42L; SubjectNumber=2; SubjectNodeId="P_2"
                              PageCount=1; Terminal=true; Pages=[ page "issues/2/comments" ]
                              Comments=[ pullRequestComment ] } ]
      PullRequestReviews=[ { RepositoryId=42L; PullRequestNumber=2; PullRequestNodeId="P_2"
                             PageCount=1; Terminal=true; Pages=[ page "pulls/2/reviews" ]
                             Reviews=[ review ] } ]
      PullRequestInlineComments=[ { RepositoryId=42L; PullRequestNumber=2; PullRequestNodeId="P_2"
                                    PageCount=1; Terminal=true; Pages=[ page "pulls/2/comments" ]
                                    Comments=[ inlineComment ] } ] }

let private assertRefused expected input =
    match MigrationNativeActivity.reconcile input with
    | Error(MigrationReadFailure.SnapshotMismatch actual) -> Assert.Equal(expected, actual)
    | other -> failwithf "Expected %s refusal; got %A" expected other

let private requireOk result =
    match result with
    | Ok value -> value
    | Error reason -> failwithf "Unexpected refusal: %A" reason

[<Fact>]
let ``native activity refuses an issue and pull request sharing one node identity`` () =
    let input = sample ()
    let issueNode = input.Issues.Issues.Head.NodeId
    let duplicate =
        { input with
            PullRequests={ input.PullRequests with
                            PullRequests=[ { input.PullRequests.PullRequests.Head with NodeId=issueNode } ] }
            PullRequestComments=[ { input.PullRequestComments.Head with SubjectNodeId=issueNode } ]
            PullRequestReviews=[ { input.PullRequestReviews.Head with PullRequestNodeId=issueNode } ]
            PullRequestInlineComments=[ { input.PullRequestInlineComments.Head with PullRequestNodeId=issueNode } ] }
    assertRefused "subject-identity" duplicate
    let missing =
        { input with
            Issues={ input.Issues with Issues=[ { input.Issues.Issues.Head with NodeId="" } ] }
            IssueComments=[ { input.IssueComments.Head with SubjectNodeId="" } ]
            IssueEvents=[ { input.IssueEvents.Head with SubjectNodeId="" } ] }
    assertRefused "subject-identity" missing

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="controlled-test-token"; UserAgent="migration-capture-test"
      Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=42L }

let private response body =
    Response
        { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
          RateBudget={ Limit=Some 5000; Remaining=Some 4999; ResetAt=None; Cost=Some 1 } }

type private FakeTransport(responses: TransportOutcome list) =
    let queue = Queue<TransportOutcome>(responses)
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            if queue.Count = 0 then NetworkFailure else queue.Dequeue()

let private emptyCensusResponses =
    [ response """{"id":42,"full_name":"FS-GG/copy"}"""; response "[]" ]

[<Fact>]
let ``complete native activity binds every censused subject and raw item`` () =
    let input = sample ()
    match MigrationNativeActivity.reconcile input with
    | Error reason -> failwithf "Unexpected refusal: %A" reason
    | Ok snapshot ->
        Assert.Equal(42L, snapshot.RepositoryId)
        Assert.Equal(1, snapshot.IssueCount)
        Assert.Equal(1, snapshot.PullRequestCount)
        Assert.Equal(1, snapshot.IssueCommentCount)
        Assert.Equal(1, snapshot.IssueEventCount)
        Assert.Equal(1, snapshot.PullRequestCommentCount)
        Assert.Equal(1, snapshot.ReviewCount)
        Assert.Equal(1, snapshot.InlineCommentCount)
        Assert.Equal(64, snapshot.NormalizedSha256.Length)
        Assert.Equal(snapshot, MigrationNativeActivity.reconcile input |> requireOk)

[<Fact>]
let ``native activity refuses a PR population outside issue marker set`` () =
    let input = sample ()
    assertRefused "census"
        { input with Issues={ input.Issues with PullRequestMarkerNumbers=[ 3 ] } }

[<Fact>]
let ``missing subject stream refuses without trying to find an absent review stream`` () =
    let input = sample ()
    assertRefused "stream-population" { input with PullRequestReviews=[] }
    assertRefused "stream-population" { input with IssueEvents=[] }

[<Fact>]
let ``nonterminal and disconnected page chains refuse`` () =
    let input = sample ()
    let first = input.IssueComments.Head
    assertRefused "stream-pages" { input with IssueComments=[ { first with Terminal=false } ] }
    let badPage = { first.Pages.Head with NextUri=Some "https://api.github.test/absent" }
    assertRefused "stream-pages" { input with IssueComments=[ { first with Pages=[ badPage ] } ] }

[<Fact>]
let ``native activity refuses a stream page from another repository`` () =
    let input = sample ()
    let stream = input.IssueEvents.Head
    for foreign in
        [ "https://api.github.test/repos/FS-GG/other/issues/1/events?per_page=100"
          "https://foreign.example/repos/FS-GG/copy/issues/1/events?per_page=100"
          "https://api.github.test/repos/FS-GG/copy/issues/2/events?per_page=100" ] do
        let foreignPage = { stream.Pages.Head with RequestedUri=foreign }
        assertRefused "stream-pages"
            { input with IssueEvents=[ { stream with Pages=[ foreignPage ] } ] }

[<Fact>]
let ``native activity refuses undersized and misnumbered stream page queries`` () =
    let input = sample ()
    let stream = input.IssueEvents.Head
    let undersized =
        { stream.Pages.Head with
            RequestedUri="https://api.github.test/repos/FS-GG/copy/issues/1/events?per_page=1" }
    assertRefused "stream-pages"
        { input with IssueEvents=[ { stream with Pages=[ undersized ] } ] }
    let secondUri = "https://api.github.test/repos/FS-GG/copy/issues/1/events?per_page=100&page=3"
    let twoPages =
        [ { stream.Pages.Head with NextUri=Some secondUri }
          { stream.Pages.Head with RequestedUri=secondUri } ]
    assertRefused "stream-pages"
        { input with IssueEvents=[ { stream with PageCount=2; Pages=twoPages } ] }
    for query in
        [ "?state=open&per_page=100"
          "?state=all&per_page=100&per_page=100"
          "?state=all&per_page=100&after=cursor" ] do
        let alteredPage =
            { input.Issues.Pages.Head with
                RequestedUri="https://api.github.test/repos/FS-GG/copy/issues" + query }
        assertRefused "stream-pages"
            { input with Issues={ input.Issues with Pages=[ alteredPage ] } }

[<Fact>]
let ``missing issue census page proof refuses and changed proof changes digest`` () =
    let input = sample ()
    assertRefused "stream-pages" { input with Issues={ input.Issues with Pages=[] } }
    let first = MigrationNativeActivity.reconcile input |> requireOk
    let alteredPage = { input.Issues.Pages.Head with PayloadSha256=digest "changed issue page" }
    let altered = { input with Issues={ input.Issues with Pages=[ alteredPage ] } }
    let second = MigrationNativeActivity.reconcile altered |> requireOk
    Assert.NotEqual(first.NormalizedSha256, second.NormalizedSha256)

[<Fact>]
let ``modified raw activity and wrong subject identity refuse`` () =
    let input = sample ()
    let stream = input.IssueComments.Head
    assertRefused "stream-binding-or-payload"
        { input with IssueComments=[ { stream with Comments=[ { issueComment with Body="changed"; PayloadJson="modified" } ] } ] }
    assertRefused "stream-binding-or-payload"
        { input with IssueComments=[ { stream with SubjectNodeId="foreign" } ] }

[<Fact>]
let ``orphan inline review comment and duplicate native node refuse`` () =
    let input = sample ()
    let inlineStream = input.PullRequestInlineComments.Head
    assertRefused "orphan-review-comment"
        { input with PullRequestInlineComments=[ { inlineStream with Comments=[ { inlineComment with ReviewId=Some 999L } ] } ] }
    let eventStream = input.IssueEvents.Head
    assertRefused "duplicate-activity"
        { input with IssueEvents=[ { eventStream with Events=[ { issueEvent with NodeId="IC_201" } ] } ] }

[<Fact>]
let ``native activity refuses one node identity used as both subject and event`` () =
    let input = sample ()
    let issueNode = input.Issues.Issues.Head.NodeId
    let eventStream = input.IssueEvents.Head
    assertRefused "duplicate-activity"
        { input with IssueEvents=[ { eventStream with Events=[ { issueEvent with NodeId=issueNode } ] } ] }
    assertRefused "duplicate-activity"
        { input with IssueComments=[ { input.IssueComments.Head with
                                        Comments=[ { issueComment with NodeId="" } ] } ] }

[<Fact>]
let ``native activity refuses one database identity counted twice`` () =
    let input = sample ()
    let eventStream = input.IssueEvents.Head
    let duplicateEvent = { issueEvent with NodeId="IE_203" }
    assertRefused "duplicate-activity"
        { input with IssueEvents=[ { eventStream with Events=[ issueEvent; duplicateEvent ] } ] }
    let prStream = input.PullRequestComments.Head
    let duplicateComment = { pullRequestComment with DatabaseId=issueComment.DatabaseId }
    assertRefused "duplicate-activity"
        { input with PullRequestComments=[ { prStream with Comments=[ duplicateComment ] } ] }

[<Fact>]
let ``changed page digest changes the full activity snapshot`` () =
    let input = sample ()
    let first = MigrationNativeActivity.reconcile input |> requireOk
    let stream = input.IssueEvents.Head
    let changed =
        { input with IssueEvents=[ { stream with Pages=[ { stream.Pages.Head with PayloadSha256=digest "other page" } ] } ] }
    let second = MigrationNativeActivity.reconcile changed |> requireOk
    Assert.NotEqual(first.NormalizedSha256, second.NormalizedSha256)

[<Fact>]
let ``provider capture rereads an empty terminal cohort using only GET requests`` () =
    let responses =
        emptyCensusResponses @ emptyCensusResponses @ emptyCensusResponses @ emptyCensusResponses
    let transport = FakeTransport responses
    let capture = MigrationNativeActivity.capture options transport |> requireOk
    Assert.Equal(0, capture.Snapshot.IssueCount)
    Assert.Equal(0, capture.Snapshot.PullRequestCount)
    Assert.Empty(capture.Input.IssueComments)
    Assert.Empty(capture.Input.PullRequestReviews)
    Assert.Equal(8, transport.Requests.Length)
    Assert.All(transport.Requests, fun request ->
        match request with
        | Rest rest -> Assert.Equal(Get, rest.Method)
        | GraphQL _ -> failwith "native activity capture issued GraphQL")

[<Fact>]
let ``provider capture refuses a subject added during the final census`` () =
    let addedIssue =
        """[{"number":1,"id":101,"node_id":"I_1","state":"open","updated_at":"2026-09-24T10:00:00Z"}]"""
    let responses =
        emptyCensusResponses @ emptyCensusResponses
        @ [ response """{"id":42,"full_name":"FS-GG/copy"}"""; response addedIssue ]
        @ emptyCensusResponses
    let transport = FakeTransport responses
    match MigrationNativeActivity.capture options transport with
    | Error MigrationReadFailure.PopulationDrift -> ()
    | other -> failwithf "Expected population drift; got %A" other
    Assert.Equal(8, transport.Requests.Length)

[<Fact>]
let ``provider capture reads every nonempty native stream before reconciling`` () =
    let repository = response """{"id":42,"full_name":"FS-GG/copy"}"""
    let issuePage =
        response """[{"number":1,"id":101,"node_id":"I_1","state":"open","updated_at":"2026-09-24T10:00:00Z"},{"number":2,"pull_request":{}}]"""
    let pullRequestPage =
        response ($"""[{{"number":2,"id":102,"node_id":"P_2","state":"open","updated_at":"2026-09-24T10:00:00Z","head":{{"sha":"{String.replicate 40 "a"}"}},"base":{{"sha":"{String.replicate 40 "b"}","repo":{{"id":42}}}}}}]""")
    let comment number id node =
        response ($"""[{{"id":{id},"node_id":"{node}","issue_url":"https://api.github.test/repos/FS-GG/copy/issues/{number}","body":"body","created_at":"2026-09-24T10:00:00Z","updated_at":"2026-09-24T10:00:00Z","user":{{"login":"actor"}}}}]""")
    let eventPage =
        response """[{"id":202,"node_id":"IE_202","event":"assigned","created_at":"2026-09-24T10:00:00Z","actor":null}]"""
    let reviewPage =
        response """[{"id":204,"node_id":"R_204","pull_request_url":"https://api.github.test/repos/FS-GG/copy/pulls/2","state":"APPROVED","commit_id":null,"submitted_at":"2026-09-24T10:00:00Z","user":{"login":"reviewer"}}]"""
    let inlinePage =
        response """[{"id":205,"node_id":"RC_205","pull_request_url":"https://api.github.test/repos/FS-GG/copy/pulls/2","pull_request_review_id":204,"path":"src/a.fs","body":"review","created_at":"2026-09-24T10:00:00Z","updated_at":"2026-09-24T10:00:00Z"}]"""
    let responses =
        [ repository; issuePage; repository; pullRequestPage
          repository; comment 1 201 "IC_201"
          repository; eventPage
          repository; comment 2 203 "PC_203"
          repository; reviewPage
          repository; inlinePage
          repository; issuePage; repository; pullRequestPage ]
    let transport = FakeTransport responses
    let capture = MigrationNativeActivity.capture options transport |> requireOk
    Assert.Equal(1, capture.Snapshot.IssueCount)
    Assert.Equal(1, capture.Snapshot.PullRequestCount)
    Assert.Equal(1, capture.Snapshot.IssueCommentCount)
    Assert.Equal(1, capture.Snapshot.IssueEventCount)
    Assert.Equal(1, capture.Snapshot.PullRequestCommentCount)
    Assert.Equal(1, capture.Snapshot.ReviewCount)
    Assert.Equal(1, capture.Snapshot.InlineCommentCount)
    Assert.Equal(18, transport.Requests.Length)

[<Fact>]
let ``two complete native passes agree before a stable result is returned`` () =
    let onePass =
        emptyCensusResponses @ emptyCensusResponses @ emptyCensusResponses @ emptyCensusResponses
    let transport = FakeTransport(onePass @ onePass)
    let capture = MigrationNativeActivity.captureStable options transport |> requireOk
    Assert.Equal(0, capture.Snapshot.IssueCount)
    Assert.Equal(16, transport.Requests.Length)

[<Fact>]
let ``changed raw issue census page between two full passes refuses`` () =
    let onePass =
        emptyCensusResponses @ emptyCensusResponses @ emptyCensusResponses @ emptyCensusResponses
    let changedIssues =
        [ response """{"id":42,"full_name":"FS-GG/copy"}"""; response "[ ]" ]
    let changedPass =
        changedIssues @ emptyCensusResponses @ changedIssues @ emptyCensusResponses
    let transport = FakeTransport(onePass @ changedPass)
    match MigrationNativeActivity.captureStable options transport with
    | Error MigrationReadFailure.PopulationDrift -> ()
    | other -> failwithf "Expected changed raw page refusal; got %A" other
    Assert.Equal(16, transport.Requests.Length)
