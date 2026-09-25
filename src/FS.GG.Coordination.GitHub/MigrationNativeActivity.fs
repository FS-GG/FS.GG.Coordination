namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type MigrationNativeActivityInput =
    { Issues: MigrationIssuePopulation
      PullRequests: MigrationPullRequestPopulation
      IssueComments: MigrationIssueCommentPopulation list
      IssueEvents: MigrationIssueEventPopulation list
      PullRequestComments: MigrationIssueCommentPopulation list
      PullRequestReviews: MigrationPullRequestReviewPopulation list
      PullRequestInlineComments: MigrationPullRequestReviewCommentPopulation list }

type MigrationNativeActivitySnapshot =
    { RepositoryId: int64
      IssueCount: int
      PullRequestCount: int
      IssueCommentCount: int
      IssueEventCount: int
      PullRequestCommentCount: int
      ReviewCount: int
      InlineCommentCount: int
      NormalizedSha256: string }

type MigrationNativeActivityCapture =
    { Input: MigrationNativeActivityInput
      Snapshot: MigrationNativeActivitySnapshot }

[<RequireQualifiedAccess>]
module MigrationNativeActivity =
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private payloadValid raw digest = sha raw = digest

    let private unique values = (values |> Set.ofList).Count = values.Length

    let private exactPopulation (expected: Set<int>) (values: 'a list) (number: 'a -> int) =
        let actual = values |> List.map number
        actual.Length = expected.Count && unique actual && Set.ofList actual = expected

    let private pagesValid count (pages: MigrationRestPageEvidence list) =
        let validUri (value: string) =
            let mutable uri = Unchecked.defaultof<Uri>
            Uri.TryCreate(value, UriKind.Absolute, &uri) && uri.Scheme = Uri.UriSchemeHttps
        let rec linked rows =
            match rows with
            | [] -> false
            | [ last ] -> last.NextUri.IsNone
            | first :: ((next :: _) as rest) ->
                first.NextUri = Some next.RequestedUri && linked rest
        count > 0 && pages.Length = count
        && unique (pages |> List.map _.RequestedUri)
        && (pages |> List.forall (fun page ->
            validUri page.RequestedUri
            && page.PayloadSha256.Length = 64
            && (page.PayloadSha256 |> Seq.forall (fun value ->
                (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f')))))
        && linked pages

    let private exactPageQuery census pageIndex (uri: Uri) =
        let entries =
            uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part -> part.Split('=', 2))
        let expected =
            [ if census then "state", "all"
              "per_page", "100"
              if pageIndex > 0 then "page", string (pageIndex + 1) ]
            |> Map.ofList
        let decoded =
            entries
            |> Array.choose (fun parts ->
                if parts.Length = 2 then
                    Some(Uri.UnescapeDataString parts.[0], Uri.UnescapeDataString parts.[1])
                else None)
        let keys = decoded |> Array.map fst |> Set.ofArray
        decoded.Length = entries.Length && keys.Count = entries.Length
        && (expected |> Map.forall (fun name value ->
            decoded |> Array.exists (fun (observedName, observedValue) ->
                observedName = name && observedValue = value)))
        && (decoded |> Array.forall (fun (name, value) ->
            Map.containsKey name expected
            || (pageIndex > 0 && name = "after" && not (String.IsNullOrWhiteSpace value))))

    let private pagesWithinCensusScope (input: MigrationNativeActivityInput) =
        match input.Issues.Pages with
        | [] -> false
        | first :: _ ->
            let mutable censusUri = Unchecked.defaultof<Uri>
            if not (Uri.TryCreate(first.RequestedUri, UriKind.Absolute, &censusUri))
               || not (censusUri.AbsolutePath.EndsWith("/issues", StringComparison.Ordinal)) then false
            else
                let rootPath = censusUri.AbsolutePath.Substring(0, censusUri.AbsolutePath.Length - "/issues".Length)
                let segments = rootPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                if segments.Length < 3 || segments.[segments.Length - 3] <> "repos"
                   || String.IsNullOrWhiteSpace segments.[segments.Length - 2]
                   || String.IsNullOrWhiteSpace segments.[segments.Length - 1] then false
                else
                    let origin = censusUri.GetLeftPart(UriPartial.Authority)
                    let scoped pages path alternate census =
                        pages |> List.mapi (fun index page ->
                            let mutable uri = Unchecked.defaultof<Uri>
                            Uri.TryCreate(page.RequestedUri, UriKind.Absolute, &uri)
                            && uri.Scheme = Uri.UriSchemeHttps
                            && uri.GetLeftPart(UriPartial.Authority) = origin
                            && uri.Fragment = ""
                            && (uri.AbsolutePath = path || alternate = Some uri.AbsolutePath)
                            && exactPageQuery census index uri)
                        |> List.forall id
                    let issuePath = rootPath + "/issues"
                    let pullPath = rootPath + "/pulls"
                    scoped input.Issues.Pages issuePath (Some $"/repositories/{input.Issues.RepositoryId}/issues") true
                    && scoped input.PullRequests.Pages pullPath
                        (Some $"/repositories/{input.PullRequests.RepositoryId}/pulls") true
                    && (input.IssueComments |> List.forall (fun stream ->
                        scoped stream.Pages $"{issuePath}/{stream.SubjectNumber}/comments" None false))
                    && (input.IssueEvents |> List.forall (fun stream ->
                        scoped stream.Pages $"{issuePath}/{stream.SubjectNumber}/events" None false))
                    && (input.PullRequestComments |> List.forall (fun stream ->
                        scoped stream.Pages $"{issuePath}/{stream.SubjectNumber}/comments" None false))
                    && (input.PullRequestReviews |> List.forall (fun stream ->
                        scoped stream.Pages $"{pullPath}/{stream.PullRequestNumber}/reviews" None false))
                    && (input.PullRequestInlineComments |> List.forall (fun stream ->
                        scoped stream.Pages $"{pullPath}/{stream.PullRequestNumber}/comments" None false))

    let private framed (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let reconcile (input: MigrationNativeActivityInput) =
        let fail reason = Error(MigrationReadFailure.SnapshotMismatch reason)
        let issues = input.Issues
        let pullRequests = input.PullRequests
        let issueNumbers = issues.Issues |> List.map _.Number |> Set.ofList
        let pullRequestNumbers = pullRequests.PullRequests |> List.map _.Number |> Set.ofList
        let censusNodes =
            (issues.Issues |> List.map _.NodeId)
            @ (pullRequests.PullRequests |> List.map _.NodeId)
        let issueNodes = issues.Issues |> List.map (fun issue -> issue.Number, issue.NodeId) |> Map.ofList
        let pullRequestNodes = pullRequests.PullRequests |> List.map (fun pullRequest -> pullRequest.Number, pullRequest.NodeId) |> Map.ofList
        let matchingIssue number nodeId = Map.tryFind number issueNodes = Some nodeId
        let matchingPullRequest number nodeId = Map.tryFind number pullRequestNodes = Some nodeId
        let inputPopulations =
            exactPopulation issueNumbers input.IssueComments _.SubjectNumber
            && exactPopulation issueNumbers input.IssueEvents _.SubjectNumber
            && exactPopulation pullRequestNumbers input.PullRequestComments _.SubjectNumber
            && exactPopulation pullRequestNumbers input.PullRequestReviews _.PullRequestNumber
            && exactPopulation pullRequestNumbers input.PullRequestInlineComments _.PullRequestNumber
        let allPageSets =
            pagesValid issues.PageCount issues.Pages
            && pagesValid pullRequests.PageCount pullRequests.Pages
            && (input.IssueComments |> List.forall (fun stream -> pagesValid stream.PageCount stream.Pages))
            && (input.IssueEvents |> List.forall (fun stream -> pagesValid stream.PageCount stream.Pages))
            && (input.PullRequestComments |> List.forall (fun stream -> pagesValid stream.PageCount stream.Pages))
            && (input.PullRequestReviews |> List.forall (fun stream -> pagesValid stream.PageCount stream.Pages))
            && (input.PullRequestInlineComments |> List.forall (fun stream -> pagesValid stream.PageCount stream.Pages))
        let allStreamsTerminal =
            (input.IssueComments |> List.forall _.Terminal)
            && (input.IssueEvents |> List.forall _.Terminal)
            && (input.PullRequestComments |> List.forall _.Terminal)
            && (input.PullRequestReviews |> List.forall _.Terminal)
            && (input.PullRequestInlineComments |> List.forall _.Terminal)
        let allStreamsBound =
            input.IssueComments |> List.forall (fun stream ->
                stream.RepositoryId = issues.RepositoryId
                && matchingIssue stream.SubjectNumber stream.SubjectNodeId
                && stream.Comments |> List.forall (fun comment ->
                    comment.SubjectNumber = stream.SubjectNumber
                    && payloadValid comment.PayloadJson comment.PayloadSha256))
            && input.IssueEvents |> List.forall (fun stream ->
                stream.RepositoryId = issues.RepositoryId
                && matchingIssue stream.SubjectNumber stream.SubjectNodeId
                && stream.Events |> List.forall (fun event ->
                    event.SubjectNumber = stream.SubjectNumber
                    && payloadValid event.PayloadJson event.PayloadSha256))
            && input.PullRequestComments |> List.forall (fun stream ->
                stream.RepositoryId = issues.RepositoryId
                && matchingPullRequest stream.SubjectNumber stream.SubjectNodeId
                && stream.Comments |> List.forall (fun comment ->
                    comment.SubjectNumber = stream.SubjectNumber
                    && payloadValid comment.PayloadJson comment.PayloadSha256))
            && input.PullRequestReviews |> List.forall (fun stream ->
                stream.RepositoryId = issues.RepositoryId
                && matchingPullRequest stream.PullRequestNumber stream.PullRequestNodeId
                && stream.Reviews |> List.forall (fun review ->
                    review.PullRequestNumber = stream.PullRequestNumber
                    && payloadValid review.PayloadJson review.PayloadSha256))
            && input.PullRequestInlineComments |> List.forall (fun stream ->
                stream.RepositoryId = issues.RepositoryId
                && matchingPullRequest stream.PullRequestNumber stream.PullRequestNodeId
                && stream.Comments |> List.forall (fun comment ->
                    comment.PullRequestNumber = stream.PullRequestNumber
                    && payloadValid comment.PayloadJson comment.PayloadSha256))
        let activityNodes =
            [ yield! input.IssueComments |> List.collect (fun stream -> stream.Comments |> List.map _.NodeId)
              yield! input.IssueEvents |> List.collect (fun stream -> stream.Events |> List.map _.NodeId)
              yield! input.PullRequestComments |> List.collect (fun stream -> stream.Comments |> List.map _.NodeId)
              yield! input.PullRequestReviews |> List.collect (fun stream -> stream.Reviews |> List.map _.NodeId)
              yield! input.PullRequestInlineComments |> List.collect (fun stream -> stream.Comments |> List.map _.NodeId) ]
        let activityDatabaseIds =
            [ [ yield! input.IssueComments |> List.collect (fun stream -> stream.Comments |> List.map _.DatabaseId)
                yield! input.PullRequestComments |> List.collect (fun stream -> stream.Comments |> List.map _.DatabaseId) ]
              input.IssueEvents |> List.collect (fun stream -> stream.Events |> List.map _.DatabaseId)
              input.PullRequestReviews |> List.collect (fun stream -> stream.Reviews |> List.map _.DatabaseId)
              input.PullRequestInlineComments |> List.collect (fun stream -> stream.Comments |> List.map _.DatabaseId) ]
        let orphanReviewComment =
            input.PullRequestInlineComments
            |> List.exists (fun inlineStream ->
                let parentReviews =
                    input.PullRequestReviews
                    |> List.tryFind (fun stream -> stream.PullRequestNumber = inlineStream.PullRequestNumber)
                    |> Option.map (fun stream -> stream.Reviews |> List.map _.DatabaseId |> Set.ofList)
                inlineStream.Comments
                |> List.exists (fun comment ->
                    comment.ReviewId |> Option.exists (fun parent ->
                        parentReviews |> Option.forall (fun reviews -> not (Set.contains parent reviews)))))
        if issues.RepositoryId <= 0L || issues.RepositoryId <> pullRequests.RepositoryId
           || not issues.Terminal || issues.PageCount < 1
           || not pullRequests.Terminal || issues.PullRequestCount <> pullRequests.PullRequests.Length
           || issues.PullRequestMarkerNumbers <> (pullRequests.PullRequests |> List.map _.Number |> List.sort) then
            fail "census"
        elif issues.Issues.Length <> issueNumbers.Count
             || pullRequests.PullRequests.Length <> pullRequestNumbers.Count
             || not (Set.isEmpty (Set.intersect issueNumbers pullRequestNumbers))
             || not (unique censusNodes)
             || (censusNodes |> List.exists String.IsNullOrWhiteSpace) then
            fail "subject-identity"
        elif issues.Issues |> List.exists (fun issue -> not (payloadValid issue.PayloadJson issue.PayloadSha256))
             || pullRequests.PullRequests |> List.exists (fun pullRequest ->
                 not (payloadValid pullRequest.PayloadJson pullRequest.PayloadSha256)) then
            fail "census-payload"
        elif not inputPopulations then fail "stream-population"
        elif not allPageSets || not allStreamsTerminal || not (pagesWithinCensusScope input) then
            fail "stream-pages"
        elif not allStreamsBound then fail "stream-binding-or-payload"
        elif not (unique (censusNodes @ activityNodes))
             || (activityDatabaseIds |> List.exists (unique >> not))
             || (activityNodes |> List.exists String.IsNullOrWhiteSpace) then
            fail "duplicate-activity"
        elif orphanReviewComment then fail "orphan-review-comment"
        else
            let pageParts (pages: MigrationRestPageEvidence list) =
                pages |> List.collect (fun page ->
                    [ page.RequestedUri; page.PayloadSha256; page.NextUri |> Option.defaultValue "" ])
            let recordParts records = records |> List.collect (fun (nodeId, digest) -> [ nodeId; digest ])
            let issueCommentCount = input.IssueComments |> List.sumBy (fun stream -> stream.Comments.Length)
            let issueEventCount = input.IssueEvents |> List.sumBy (fun stream -> stream.Events.Length)
            let pullRequestCommentCount = input.PullRequestComments |> List.sumBy (fun stream -> stream.Comments.Length)
            let reviewCount = input.PullRequestReviews |> List.sumBy (fun stream -> stream.Reviews.Length)
            let inlineCount = input.PullRequestInlineComments |> List.sumBy (fun stream -> stream.Comments.Length)
            let parts =
                [ string issues.RepositoryId; string issues.PageCount; string issues.Issues.Length
                  string pullRequests.PageCount; string pullRequests.PullRequests.Length
                  string issueCommentCount; string issueEventCount; string pullRequestCommentCount
                  string reviewCount; string inlineCount ]
                @ pageParts issues.Pages
                @ (issues.Issues |> List.sortBy _.Number |> List.collect (fun issue ->
                    [ string issue.Number; issue.NodeId; issue.PayloadSha256 ]))
                @ pageParts pullRequests.Pages
                @ (pullRequests.PullRequests |> List.sortBy _.Number |> List.collect (fun pullRequest ->
                    [ string pullRequest.Number; pullRequest.NodeId; pullRequest.PayloadSha256 ]))
                @ (input.IssueComments |> List.sortBy _.SubjectNumber |> List.collect (fun stream ->
                    [ string stream.SubjectNumber; stream.SubjectNodeId ] @ pageParts stream.Pages
                    @ (stream.Comments |> List.sortBy _.DatabaseId |> List.map (fun item -> item.NodeId, item.PayloadSha256) |> recordParts)))
                @ (input.IssueEvents |> List.sortBy _.SubjectNumber |> List.collect (fun stream ->
                    [ string stream.SubjectNumber; stream.SubjectNodeId ] @ pageParts stream.Pages
                    @ (stream.Events |> List.sortBy _.DatabaseId |> List.map (fun item -> item.NodeId, item.PayloadSha256) |> recordParts)))
                @ (input.PullRequestComments |> List.sortBy _.SubjectNumber |> List.collect (fun stream ->
                    [ string stream.SubjectNumber; stream.SubjectNodeId ] @ pageParts stream.Pages
                    @ (stream.Comments |> List.sortBy _.DatabaseId |> List.map (fun item -> item.NodeId, item.PayloadSha256) |> recordParts)))
                @ (input.PullRequestReviews |> List.sortBy _.PullRequestNumber |> List.collect (fun stream ->
                    [ string stream.PullRequestNumber; stream.PullRequestNodeId ] @ pageParts stream.Pages
                    @ (stream.Reviews |> List.sortBy _.DatabaseId |> List.map (fun item -> item.NodeId, item.PayloadSha256) |> recordParts)))
                @ (input.PullRequestInlineComments |> List.sortBy _.PullRequestNumber |> List.collect (fun stream ->
                    [ string stream.PullRequestNumber; stream.PullRequestNodeId ] @ pageParts stream.Pages
                    @ (stream.Comments |> List.sortBy _.DatabaseId |> List.map (fun item -> item.NodeId, item.PayloadSha256) |> recordParts)))
            Ok { RepositoryId=issues.RepositoryId; IssueCount=issues.Issues.Length
                 PullRequestCount=pullRequests.PullRequests.Length
                 IssueCommentCount=issueCommentCount; IssueEventCount=issueEventCount
                 PullRequestCommentCount=pullRequestCommentCount; ReviewCount=reviewCount
                 InlineCommentCount=inlineCount
                 NormalizedSha256=parts |> List.map framed |> String.concat "" |> sha }

    let private collect (subjects: 'a list) (read: 'a -> Result<'b, MigrationReadFailure>) =
        subjects
        |> List.fold (fun result subject ->
            result |> Result.bind (fun values -> read subject |> Result.map (fun value -> value :: values))) (Ok [])
        |> Result.map List.rev

    let capture (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        MigrationGitHubRead.readIssues options transport
        |> Result.bind (fun issues ->
            MigrationGitHubRead.readPullRequests options issues transport
            |> Result.bind (fun pullRequests ->
                let issueNumbers = issues.Issues |> List.map _.Number
                let pullRequestNumbers = pullRequests.PullRequests |> List.map _.Number
                collect issueNumbers (fun number -> MigrationGitHubRead.readIssueComments options issues number transport)
                |> Result.bind (fun issueComments ->
                    collect issueNumbers (fun number -> MigrationGitHubRead.readIssueEvents options issues number transport)
                    |> Result.bind (fun issueEvents ->
                        collect pullRequestNumbers (fun number -> MigrationGitHubRead.readPullRequestComments options pullRequests number transport)
                        |> Result.bind (fun pullRequestComments ->
                            collect pullRequestNumbers (fun number -> MigrationGitHubRead.readPullRequestReviews options pullRequests number transport)
                            |> Result.bind (fun pullRequestReviews ->
                                collect pullRequestNumbers (fun number -> MigrationGitHubRead.readPullRequestReviewComments options pullRequests number transport)
                                |> Result.bind (fun inlineComments ->
                                    MigrationGitHubRead.readIssues options transport
                                    |> Result.bind (fun finalIssues ->
                                        MigrationGitHubRead.readPullRequests options finalIssues transport
                                        |> Result.bind (fun finalPullRequests ->
                                            if finalIssues <> issues || finalPullRequests <> pullRequests then
                                                Error MigrationReadFailure.PopulationDrift
                                            else
                                                let input =
                                                    { Issues=issues; PullRequests=pullRequests
                                                      IssueComments=issueComments; IssueEvents=issueEvents
                                                      PullRequestComments=pullRequestComments
                                                      PullRequestReviews=pullRequestReviews
                                                      PullRequestInlineComments=inlineComments }
                                                reconcile input
                                                |> Result.map (fun snapshot -> { Input=input; Snapshot=snapshot }))))))))))

    let captureStable (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        capture options transport
        |> Result.bind (fun first ->
            capture options transport
            |> Result.bind (fun second ->
                if first <> second then Error MigrationReadFailure.PopulationDrift
                else Ok second))
