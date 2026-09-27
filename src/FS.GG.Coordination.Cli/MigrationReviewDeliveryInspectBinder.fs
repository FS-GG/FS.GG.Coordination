namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
module MigrationReviewDeliveryInspectBinder =
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hashParts values = values |> List.map frame |> String.concat "" |> sha
    let private text (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private unique (values: 'a list) = values.Length = (values |> Set.ofList |> Set.count)
    let private exactSha length (value: string) =
        text value && value.Length = length
        && value |> Seq.forall (fun character -> character >= '0' && character <= '9' || character >= 'a' && character <= 'f')

    let private parse (body: string) =
        try Ok(JsonDocument.Parse body)
        with :? JsonException -> Error "review-delivery-raw-json"

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private tryString (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)

    let private tryInt64 (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value ->
            let mutable parsed = 0L
            if value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&parsed) then Some parsed else None)

    let private optionalStringMatches name expected element =
        match expected, tryProperty name element with
        | None, None -> true
        | None, Some value -> value.ValueKind = JsonValueKind.Null
                              || (value.ValueKind = JsonValueKind.String && value.GetString() = "")
        | Some wanted, Some value when value.ValueKind = JsonValueKind.String -> value.GetString() = wanted
        | _ -> false

    let private optionalInt64Matches name expected element =
        match expected, tryProperty name element with
        | None, None -> true
        | None, Some value -> value.ValueKind = JsonValueKind.Null
        | Some wanted, _ -> tryInt64 name element = Some wanted

    let rec private descendants (element: JsonElement) =
        seq {
            yield element
            match element.ValueKind with
            | JsonValueKind.Object ->
                for property in element.EnumerateObject() do
                    yield! descendants property.Value
            | JsonValueKind.Array ->
                for child in element.EnumerateArray() do yield! descendants child
            | _ -> ()
        }

    let private decodedDocuments (read: MigrationReviewDeliveryRead) =
        seq {
            match parse read.RawBody with
            | Ok document ->
                use document = document
                yield document.RootElement.Clone()
                for element in descendants document.RootElement do
                    match tryString "content" element, tryString "encoding" element with
                    | Some encoded, Some "base64" ->
                        try
                            let body = encoded.Replace("\n", "") |> Convert.FromBase64String |> Encoding.UTF8.GetString
                            match parse body with
                            | Ok decoded ->
                                use decoded = decoded
                                yield decoded.RootElement.Clone()
                            | Error _ -> ()
                        with :? FormatException -> ()
                    | _ -> ()
            | Error _ -> ()
        }

    let private containsObject (predicate: JsonElement -> bool) (reads: MigrationReviewDeliveryRead list) =
        reads
        |> List.collect (decodedDocuments >> Seq.toList)
        |> List.collect (descendants >> Seq.toList)
        |> List.exists predicate

    let private requestIsGet (repository: MigrationGitHubReadOptions) (read: MigrationReviewDeliveryRead) =
        let request = read.Request
        match Uri.TryCreate(request.Uri, UriKind.Absolute) with
        | true, uri ->
            request.Kind = "rest" && request.Method = "Get" && request.Body.IsNone
            && request.Variables.IsEmpty && uri.Scheme = Uri.UriSchemeHttps
            && uri.Authority = repository.ApiBase.Authority
            && request.Headers |> Map.toList |> List.forall (fun (key, _) ->
                Set.contains (key.ToLowerInvariant())
                    (Set.ofList [ "accept"; "content-type"; "user-agent"; "x-github-api-version" ]))
        | _ -> false

    let private allReads (pass: MigrationReviewDeliveryNativePass) =
        pass.Repository.Read :: pass.PullRequestCensus @ (pass.Streams |> List.collect _.Reads)

    let private journalReads (pass: MigrationJournalPass) =
        pass.Repository.Read
        :: ((pass.Namespaces |> List.collect _.Reads) @ (pass.Histories |> List.collect _.Reads))

    let private repositoryRawMatches (repository: MigrationReviewDeliveryRepository) =
        containsObject
            (fun item ->
                tryInt64 "id" item = Some repository.RepositoryId
                && tryString "node_id" item = Some repository.NodeId
                && tryString "full_name" item = Some repository.FullName)
            [ repository.Read ]

    let private pullRawMatches reads (pull: MigrationReviewDeliveryPullRequest) =
        containsObject
            (fun item ->
                let head = tryProperty "head" item |> Option.bind (tryString "sha")
                tryInt64 "number" item = Some(int64 pull.Number)
                && tryString "node_id" item = Some pull.NodeId
                && head = Some pull.HeadSha)
            reads

    let private subjectNumber (subject: string) =
        let found = Regex.Match(subject, "(?:#|:)([1-9][0-9]*)$")
        if found.Success then
            match Int32.TryParse found.Groups.[1].Value with true, value -> Some value | _ -> None
        else None

    let private streamRecordRawMatches (stream: MigrationReviewDeliveryNativeStream) (record: MigrationReviewDeliveryRecord) =
        let objectMatches predicate = containsObject predicate stream.Reads
        let uriContains commit suffix =
            stream.Reads |> List.exists (fun read ->
                read.Request.Uri.Contains($"/commits/{commit}/{suffix}", StringComparison.Ordinal))
        match record with
        | MigrationReviewDeliveryRecord.Review (number, id)
        | MigrationReviewDeliveryRecord.InlineComment (number, id) ->
            stream.Subject = string number && objectMatches (fun item -> tryInt64 "id" item = Some id)
        | MigrationReviewDeliveryRecord.CheckRun (number, commit, id, name, status, conclusion) ->
            stream.Subject = string number && uriContains commit "check-runs"
            && objectMatches (fun item ->
                tryInt64 "id" item = Some id && tryString "head_sha" item = Some commit
                && tryString "name" item = Some name && tryString "status" item = Some status
                && (match conclusion, tryProperty "conclusion" item with
                    | None, Some value -> value.ValueKind = JsonValueKind.Null
                    | Some expected, Some value when value.ValueKind = JsonValueKind.String -> value.GetString() = expected
                    | _ -> false))
        | MigrationReviewDeliveryRecord.CommitStatus (number, commit, id, context, state) ->
            stream.Subject = string number && uriContains commit "statuses"
            && objectMatches (fun item ->
                tryInt64 "id" item = Some id && tryString "context" item = Some context
                && tryString "state" item = Some state)
        | MigrationReviewDeliveryRecord.PullDelivery (number, merge) ->
            stream.Subject = string number
            && objectMatches (fun item ->
                tryInt64 "number" item = Some(int64 number)
                && (match merge with
                    | Some expected -> tryString "merge_commit_sha" item = Some expected
                    | None ->
                        tryProperty "merge_commit_sha" item
                        |> Option.exists (fun value -> value.ValueKind = JsonValueKind.Null)))
        | MigrationReviewDeliveryRecord.MergeObject (number, commit) ->
            stream.Subject = string number && objectMatches (fun item -> tryString "sha" item = Some commit)
        | MigrationReviewDeliveryRecord.Tag (name, commit) ->
            stream.Subject = "repository"
            && objectMatches (fun item ->
                tryString "name" item = Some name
                && (tryProperty "commit" item |> Option.bind (tryString "sha")) = Some commit)
        | MigrationReviewDeliveryRecord.Release (id, tag, draft) ->
            stream.Subject = "repository"
            && objectMatches (fun item ->
                tryInt64 "id" item = Some id && tryString "tag_name" item = Some tag
                && (tryProperty "draft" item
                    |> Option.exists (fun value ->
                        value.ValueKind = (if draft then JsonValueKind.True else JsonValueKind.False))))

    let private nativeValid repository (pass: MigrationReviewDeliveryNativePass) =
        let pulls = pass.PullRequests |> List.sortBy (fun (item: MigrationReviewDeliveryPullRequest) -> item.Number)
        let numbers = pulls |> List.map _.Number
        let streamCommit suffix (stream: MigrationReviewDeliveryNativeStream) =
            stream.Reads
            |> List.tryHead
            |> Option.bind (fun read ->
                let marker = "/commits/"
                let start = read.Request.Uri.IndexOf(marker, StringComparison.Ordinal)
                if start < 0 then None
                else
                    let valueStart = start + marker.Length
                    let finish = read.Request.Uri.IndexOf($"/{suffix}", valueStart, StringComparison.Ordinal)
                    if finish <= valueStart then None else Some(read.Request.Uri.Substring(valueStart, finish - valueStart)))
        let requiredSingleton = Set.ofList [ "reviews"; "inline-comments"; "pull-delivery" ]
        let allowed = Set.ofList [ "reviews"; "inline-comments"; "check-runs"; "statuses"; "pull-delivery"; "merge-object" ]
        let repositoryKinds =
            pass.Streams |> List.filter (fun stream -> stream.Subject = "repository") |> List.map _.Kind |> List.sort
        pass.Repository.RepositoryId = repository.ExpectedRepositoryId
        && pass.Repository.FullName = $"{repository.Owner}/{repository.Repository}"
        && repositoryRawMatches pass.Repository
        && unique numbers && numbers |> List.forall (fun number -> number > 0)
        && pulls |> List.forall (pullRawMatches pass.PullRequestCensus)
        && pulls |> List.forall (fun pull ->
            let streams = pass.Streams |> List.filter (fun stream -> stream.Subject = string pull.Number)
            let merge =
                streams |> List.collect _.Records
                |> List.choose (function
                    | MigrationReviewDeliveryRecord.PullDelivery (_, value) -> Some value
                    | _ -> None) |> List.tryExactlyOne |> Option.flatten
            let commits = [ yield pull.HeadSha; match merge with Some value when value <> pull.HeadSha -> yield value | _ -> () ]
            let checkCommits = streams |> List.filter (_.Kind >> (=) "check-runs") |> List.map (streamCommit "check-runs")
            let statusCommits = streams |> List.filter (_.Kind >> (=) "statuses") |> List.map (streamCommit "statuses")
            streams |> List.forall (fun stream -> Set.contains stream.Kind allowed)
            && requiredSingleton |> Set.forall (fun kind -> streams |> List.filter (fun stream -> stream.Kind = kind) |> List.length = 1)
            && checkCommits = (commits |> List.map Some)
            && statusCommits = (commits |> List.map Some)
            && (streams |> List.filter (_.Kind >> (=) "merge-object") |> List.length = if merge.IsSome then 1 else 0)
            && streams |> List.filter (fun stream -> stream.Kind = "pull-delivery")
               |> List.forall (fun stream ->
                   containsObject (fun item ->
                       tryInt64 "number" item = Some(int64 pull.Number)
                       && (tryProperty "head" item |> Option.bind (tryString "sha")) = Some pull.HeadSha) stream.Reads))
        && repositoryKinds = [ "releases"; "tags" ]
        && pass.Streams |> List.collect _.Records |> List.forall (fun record ->
            pass.Streams |> List.exists (fun stream -> List.contains record stream.Records && streamRecordRawMatches stream record))
        && allReads pass |> List.forall (requestIsGet repository)

    let private supportedSchema schema =
        Set.contains schema
            (Set.ofList [ "fsgg.coordination.review-authority/1"
                          "fsgg.coordination.delivery-authority/1" ])

    let private journalRawMatches (entry: MigrationJournalHistoryEntry) =
        let expectedChain =
            if entry.Record.Schema = "fsgg.coordination.review-authority/1" then
                ReviewDeliveryAdapter.chainId entry.Record.Subject |> Result.toOption
            else None
        let eventMatches =
            containsObject
                (fun item ->
                    let schema =
                        match tryString "schema" item, tryInt64 "schemaVersion" item with
                        | Some value, _ -> Some value
                        | None, Some 1L -> Some entry.Record.Schema
                        | _ -> None
                    schema = Some entry.Record.Schema
                    && tryString "operationId" item = Some entry.Record.OperationId
                    && tryInt64 "schemaVersion" item = Some 1L
                    && (tryString "kind" item |> Option.forall ((=) entry.Record.Kind))
                    && (match expectedChain with
                        | Some chain -> tryString "chainId" item = Some chain
                        | None -> tryString "subject" item = Some entry.Record.Subject)
                    && optionalStringMatches "mergeCommit" entry.Record.MergeCommit item
                    && optionalInt64Matches "protectedRunId" entry.Record.ProtectedRunId item
                    && optionalStringMatches "protectedRunCommit" entry.Record.ProtectedRunCommit item
                    && optionalStringMatches "protectedRunConclusion" entry.Record.ProtectedRunConclusion item)
                entry.Reads
        let headMatches =
            containsObject
                (fun item ->
                    tryInt64 "schemaVersion" item = Some 1L
                    && tryInt64 "generation" item = Some entry.Record.Generation
                    && (tryString "journalKind" item
                        |> Option.exists (fun kind ->
                            kind = "review" && entry.Record.Schema = "fsgg.coordination.review-authority/1"
                            || kind = "operation" && entry.Record.Schema = "fsgg.coordination.delivery-authority/1")))
                entry.Reads
        eventMatches && headMatches

    let private journalValid repository (pass: MigrationJournalPass) =
        let refs = pass.Namespaces |> List.collect _.Refs
        let historyRefs = pass.Histories |> List.map _.RefName |> Set.ofList
        let namespaceRefs = refs |> List.map _.RefName |> Set.ofList
        let completeHistory refName =
            let entries = pass.Histories |> List.filter (fun item -> item.RefName = refName) |> List.sortBy _.Record.Generation
            entries |> List.mapi (fun index item ->
                item.Record.Generation = int64 (index + 1)
                && (if index = 0 then item.ParentSha.IsNone
                    else item.ParentSha = Some entries.[index - 1].CommitSha))
            |> List.forall id
        pass.Repository.RepositoryId = repository.ExpectedRepositoryId
        && pass.Repository.FullName = $"{repository.Owner}/{repository.Repository}"
        && repositoryRawMatches pass.Repository
        && unique (refs |> List.map _.RefName)
        && namespaceRefs = historyRefs
        && pass.Namespaces |> List.forall (fun census ->
            census.Refs |> List.forall (fun item -> item.RefName.StartsWith(census.Prefix, StringComparison.Ordinal)))
        && refs |> List.forall (fun item ->
            let entries = pass.Histories |> List.filter (fun history -> history.RefName = item.RefName)
            exactSha 40 item.HeadSha && completeHistory item.RefName
            && not (List.isEmpty entries)
            && (entries |> List.maxBy _.Record.Generation |> _.CommitSha) = item.HeadSha)
        && pass.Histories |> List.forall (fun item ->
            supportedSchema item.Record.Schema && item.Record.Generation > 0L
            && text item.Record.OperationId && exactSha 40 item.CommitSha && exactSha 40 item.TreeSha
            && journalRawMatches item)
        && journalReads pass |> List.forall (requestIsGet repository)

    let private correspondence (native: MigrationReviewDeliveryNativePass) (journals: MigrationJournalPass) =
        let allRecords = native.Streams |> List.collect _.Records
        let checks =
            allRecords |> List.choose (function
                | MigrationReviewDeliveryRecord.CheckRun (pr, commit, id, _, status, conclusion) ->
                    Some(pr, commit, id, status, conclusion)
                | _ -> None)
        let statuses =
            allRecords |> List.choose (function MigrationReviewDeliveryRecord.CommitStatus (pr, _, id, _, _) -> Some(pr, id) | _ -> None)
        let merges =
            allRecords |> List.choose (function MigrationReviewDeliveryRecord.PullDelivery (pr, merge) -> Some(pr, merge) | _ -> None) |> Map.ofList
        let tags = allRecords |> List.choose (function MigrationReviewDeliveryRecord.Tag (name, commit) -> Some(name, commit) | _ -> None)
        let releases = allRecords |> List.choose (function MigrationReviewDeliveryRecord.Release (id, tag, false) -> Some(id, tag) | _ -> None)
        let tagTargets = tags |> Map.ofList
        let mergeCommits = merges |> Map.values |> Seq.choose id |> Set.ofSeq
        let releaseTargets =
            releases
            |> List.choose (fun (id, tag) -> Map.tryFind tag tagTargets |> Option.map (fun commit -> id, tag, commit))
        if releaseTargets.Length <> releases.Length then
            Error "review-delivery-release-tag-correspondence"
        elif releaseTargets |> List.exists (fun (_, _, commit) -> not (Set.contains commit mergeCommits)) then
            Error "review-delivery-release-delivery-correspondence"
        else
            let folder (state: Result<MigrationReviewDeliveryCorrespondence list, string>) (pull: MigrationReviewDeliveryPullRequest) =
                state |> Result.bind (fun rows ->
                    let related = journals.Histories |> List.filter (fun item -> subjectNumber item.Record.Subject = Some pull.Number)
                    let reviewRefs =
                        related |> List.filter (fun item -> item.Record.Schema = "fsgg.coordination.review-authority/1")
                        |> List.map _.RefName |> List.distinct |> List.sort
                    let delivery =
                        related |> List.filter (fun item -> item.Record.Schema = "fsgg.coordination.delivery-authority/1")
                    let deliveryRefs = delivery |> List.map _.RefName |> List.distinct |> List.sort
                    let merge = Map.tryFind pull.Number merges |> Option.defaultValue None
                    let deliveryMatches =
                        match merge with
                        | None -> delivery |> List.forall (_.Record.MergeCommit >> Option.isNone)
                        | Some commit ->
                            delivery |> List.exists (fun item -> item.Record.MergeCommit = Some commit)
                            && delivery |> List.forall (fun item ->
                                item.Record.MergeCommit.IsNone || item.Record.MergeCommit = Some commit)
                    let runMatches =
                        delivery |> List.forall (fun item ->
                            match item.Record.ProtectedRunId, item.Record.ProtectedRunCommit with
                            | None, None -> true
                            | Some id, Some commit ->
                                match item.Record.ProtectedRunConclusion with
                                | Some conclusion ->
                                    List.contains (pull.Number, commit, id, "completed", Some conclusion) checks
                                | None -> false
                            | _ -> false)
                    if List.isEmpty reviewRefs || not deliveryMatches || not runMatches then
                        Error $"review-delivery-journal-correspondence:{pull.Number}"
                    else
                        let relatedTags =
                            match merge with
                            | None -> []
                            | Some commit ->
                                tags |> List.choose (fun (name, target) -> if target = commit then Some name else None)
                                |> List.sort
                        let relatedTagSet = Set.ofList relatedTags
                        let relatedReleases =
                            releases
                            |> List.choose (fun (id, tag) -> if Set.contains tag relatedTagSet then Some id else None)
                            |> List.sort
                        Ok({ PullRequestNumber=pull.Number; HeadSha=pull.HeadSha; MergeCommit=merge
                             CheckRunIds=checks |> List.choose (fun (pr, _, id, _, _) -> if pr = pull.Number then Some id else None) |> List.sort
                             StatusIds=statuses |> List.choose (fun (pr, id) -> if pr = pull.Number then Some id else None) |> List.sort
                             ReviewJournalRefs=reviewRefs; DeliveryJournalRefs=deliveryRefs
                             TagNames=relatedTags; ReleaseIds=relatedReleases } :: rows))
            native.PullRequests |> List.sortBy (fun item -> item.Number) |> List.fold folder (Ok []) |> Result.map List.rev

    let private completeFingerprint (native: MigrationReviewDeliveryNativePass)
                                    (journals: MigrationJournalPass)
                                    (rows: MigrationReviewDeliveryCorrespondence list) =
        [ native.Fingerprint; journals.Fingerprint
          rows |> List.collect (fun (row: MigrationReviewDeliveryCorrespondence) ->
              [ string row.PullRequestNumber; row.HeadSha; defaultArg row.MergeCommit ""
                row.CheckRunIds |> List.map string |> String.concat ","
                row.StatusIds |> List.map string |> String.concat ","
                String.concat "," row.ReviewJournalRefs; String.concat "," row.DeliveryJournalRefs
                String.concat "," row.TagNames; row.ReleaseIds |> List.map string |> String.concat "," ])
          |> hashParts ] |> hashParts

    let bind (cohort: GitHubMigrationCopyCohort) (repository: MigrationGitHubReadOptions)
             (native: MigrationReviewDeliveryNativeTwoPass) (journals: MigrationJournalTwoPass) =
        MigrationReviewDeliveryCaptureContract.validateNativeTwoPass native
        |> Result.bind (fun _ -> MigrationReviewDeliveryCaptureContract.validateJournalTwoPass journals)
        |> Result.bind (fun _ ->
            if not (GitHubMigrationInspect.validCohort cohort) || cohort.Repositories.Length <> 1
               || cohort.Repositories.Head.Id <> repository.ExpectedRepositoryId
               || cohort.Repositories.Head.NodeId <> native.First.Repository.NodeId
               || native.First.Repository <> journals.First.Repository
               || native.Second.Repository <> journals.Second.Repository then
                Error "review-delivery-cohort"
            elif not (nativeValid repository native.First && nativeValid repository native.Second) then
                Error "review-delivery-native-raw-typed"
            elif not (journalValid repository journals.First && journalValid repository journals.Second) then
                Error "review-delivery-journal-raw-typed"
            else
                correspondence native.First journals.First
                |> Result.bind (fun firstRows ->
                    correspondence native.Second journals.Second
                    |> Result.bind (fun secondRows ->
                        let first =
                            { Native=native.First; Journals=journals.First; Correspondence=firstRows
                              Fingerprint=completeFingerprint native.First journals.First firstRows }
                        let second =
                            { Native=native.Second; Journals=journals.Second; Correspondence=secondRows
                              Fingerprint=completeFingerprint native.Second journals.Second secondRows }
                        if first <> second then Error "review-delivery-complete-pass-drift"
                        else Ok { First=first; Second=second })))

    let private recordIdentity (record: MigrationReviewDeliveryRecord) =
        match record with
        | MigrationReviewDeliveryRecord.Review (pr, id) -> $"review:{pr}:{id}"
        | MigrationReviewDeliveryRecord.InlineComment (pr, id) -> $"inline-comment:{pr}:{id}"
        | MigrationReviewDeliveryRecord.CheckRun (pr, commit, id, _, _, _) -> $"check-run:{pr}:{commit}:{id}"
        | MigrationReviewDeliveryRecord.CommitStatus (pr, commit, id, _, _) -> $"commit-status:{pr}:{commit}:{id}"
        | MigrationReviewDeliveryRecord.PullDelivery (pr, _) -> $"pull-delivery:{pr}"
        | MigrationReviewDeliveryRecord.MergeObject (pr, commit) -> $"merge-object:{pr}:{commit}"
        | MigrationReviewDeliveryRecord.Tag (name, commit) -> $"tag:{name}:{commit}"
        | MigrationReviewDeliveryRecord.Release (id, _, _) -> $"release:{id}"

    let authority (cohort: GitHubMigrationCopyCohort) passOrdinal (capture: MigrationReviewDeliveryCompleteTwoPass) =
        let pass = if passOrdinal = 1 then Some capture.First elif passOrdinal = 2 then Some capture.Second else None
        match pass with
        | None -> Error "invalid-pass"
        | Some (pass: MigrationReviewDeliveryCompletePass) ->
            let reads = allReads pass.Native @ journalReads pass.Journals
            let nativeSubjects =
                pass.Native.Streams |> List.collect _.Records |> List.map (fun record ->
                    let identity = recordIdentity record
                    { Identity=identity; Revision=pass.Fingerprint; PayloadSha256=sha identity })
            let journalSubjects =
                pass.Journals.Histories |> List.map (fun (item: MigrationJournalHistoryEntry) ->
                    let identity = $"journal:{item.RefName}:{item.Record.Generation}:{item.Record.OperationId}"
                    { Identity=identity; Revision=item.CommitSha; PayloadSha256=sha identity })
            let subjects = nativeSubjects @ journalSubjects |> List.sortBy _.Identity
            if List.isEmpty reads || List.isEmpty subjects || not (unique (subjects |> List.map _.Identity)) then
                Error "review-delivery-authority-empty-or-duplicate"
            else
                let groupedReads = reads |> List.groupBy _.Request.Uri
                if groupedReads |> List.exists (fun (_, sameUri) ->
                    sameUri |> List.map _.RawSha256 |> List.distinct |> List.length <> 1) then
                    Error "review-delivery-repeated-request-drift"
                else
                let distinctReads =
                    groupedReads |> List.map (snd >> List.head)
                let pages =
                    distinctReads |> List.mapi (fun index read ->
                        let next =
                            if index + 1 < distinctReads.Length then Some(sha distinctReads.[index + 1].Request.Uri) else None
                        { RequestedUri=read.Request.Uri; RequestIdentitySha256=sha read.Request.Uri
                          RawBody=read.RawBody; PayloadSha256=read.RawSha256
                          NextRequestIdentitySha256=next
                          Subjects=if index = 0 then subjects else [] })
                Ok { CohortSha256=GitHubMigrationInspect.cohortSha256 cohort
                     ScopeVerified=true; SubjectsParsedFromRaw=true
                     Read={ Authority="review-delivery-release-records"; ObservedAt=DateTimeOffset.UtcNow
                            PageCount=pages.Length; ItemCount=subjects.Length; Terminal=true; NextCursor=None
                            HighWaterMark=pass.Fingerprint; Subjects=subjects }
                     Pages=pages }
