namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub

type MigrationHistoricalLossSourceBinding =
    { Family: string; ProducerId: string; SourceRevision: string
      SourceUri: string; SourceBlobSha256: string }

type MigrationHistoricalLossRequest =
    { RepositoryId: int64; RepositoryFullName: string; CutoffUtc: DateTimeOffset
      MissingFamilies: string list; SourceBindings: MigrationHistoricalLossSourceBinding list
      CallerCompletenessFlags: Map<string, bool> }

type MigrationHistoricalLossPageBinding =
    { Stream: string; SubjectNumber: int option; RequestedUri: string
      PayloadSha256: string; NextUri: string option }

type MigrationHistoricalLossSubjectBinding =
    { SubjectKind: string; SubjectNumber: int; NodeId: string
      ObservedAtUtc: DateTimeOffset; PayloadSha256: string }

type MigrationHistoricalLossObservation =
    { Family: string; SubjectKind: string; SubjectNumber: int; NodeId: string
      ObservedAtUtc: DateTimeOffset; PayloadSha256: string; MarkerSha256: string }

type MigrationHistoricalLossFamilyCensus =
    { Family: string; ObservationState: string; Observations: MigrationHistoricalLossObservation list
      ProducerGap: string; HistoryGap: string }

type MigrationHistoricalLossProposal =
    { RepositoryId: int64; RepositoryFullName: string; CutoffUtc: DateTimeOffset
      Pages: MigrationHistoricalLossPageBinding list; Subjects: MigrationHistoricalLossSubjectBinding list
      Families: MigrationHistoricalLossFamilyCensus list
      UnresolvedProducerFamilies: string list; UnresolvedHistoryFamilies: string list
      CensusFingerprint: string; ProposalFingerprint: string }

type internal MigrationHistoricalLossTextSource =
    { SourceKind: string; SubjectNumber: int; NodeId: string; ObservedAtUtc: DateTimeOffset
      PayloadSha256: string; Body: string }

[<RequireQualifiedAccess>]
module MigrationHistoricalLossCensus =
    let private requiredFamilies = [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ]
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private fingerprint values = values |> Seq.map frame |> String.concat "" |> sha
    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')
    let private refuse reason = Error $"historical-loss-census-refused:{reason}"

    let private validRequest (request: MigrationHistoricalLossRequest) =
        let pieces = if isNull request.RepositoryFullName then [||] else request.RepositoryFullName.Split('/')
        let validUri value =
            let mutable uri = Unchecked.defaultof<Uri>
            Uri.TryCreate(value, UriKind.Absolute, &uri) && uri.Scheme = Uri.UriSchemeHttps
        request.RepositoryId > 0L && pieces.Length = 2
        && pieces |> Array.forall (String.IsNullOrWhiteSpace >> not)
        && request.CutoffUtc.Offset = TimeSpan.Zero
        && request.MissingFamilies = requiredFamilies
        && request.CallerCompletenessFlags.IsEmpty
        && request.SourceBindings.Length = 3
        && (request.SourceBindings |> List.map _.Family) = requiredFamilies
        && request.SourceBindings |> List.forall (fun source ->
            not (String.IsNullOrWhiteSpace source.ProducerId)
            && hex 40 source.SourceRevision && hex 64 source.SourceBlobSha256
            && validUri source.SourceUri)

    let private markerPattern =
        Regex("<!--\\s*fsgg:(?<prefix>[A-Za-z0-9:._/-]+)(?<tail>[^<>]*)-->", RegexOptions.CultureInvariant ||| RegexOptions.Compiled)
    let private knownPrefixes =
        set [ "claim"; "intake:v1"; "review-decision"; "review-decision/v2"; "review-wait"; "review-wait/v1"
              "delivery-obligation"; "delivery-obligations"; "delivery-receipt"; "delivery-completion/v1"
              "completion-correction/v1"; "done-receipt"; "intake-receipt" ]
    let private familyForPrefix = function
        | "delivery-receipt" -> Some "delivery-receipt"
        | "intake-receipt" -> Some "intake-receipt"
        | "done-receipt" -> Some "legacy-done-receipt"
        | _ -> None

    let private observations (texts: MigrationHistoricalLossTextSource list) =
        texts
        |> List.fold (fun state source ->
            state |> Result.bind (fun accumulated ->
                if isNull source.Body then refuse "body-null"
                elif source.ObservedAtUtc.Offset <> TimeSpan.Zero then refuse "subject-time-not-utc"
                elif not (hex 64 source.PayloadSha256) then refuse "subject-payload-hash"
                else
                    let matches = markerPattern.Matches source.Body |> Seq.cast<Match> |> Seq.toList
                    let prefixes = matches |> List.map (fun item -> item.Groups["prefix"].Value)
                    match prefixes |> List.tryFind (knownPrefixes.Contains >> not) with
                    | Some prefix -> refuse $"unknown-prefix:{prefix}"
                    | None ->
                        let supported =
                            matches |> List.choose (fun matched ->
                                familyForPrefix matched.Groups["prefix"].Value
                                |> Option.map (fun family -> family, matched))
                        let duplicate = supported |> List.countBy fst |> List.tryFind (snd >> ((<) 1))
                        match duplicate with
                        | Some(family, _) -> refuse $"duplicate-marker:{family}:{source.NodeId}"
                        | None ->
                            let malformed =
                                requiredFamilies
                                |> List.tryFind (fun family ->
                                    let prefix = if family = "legacy-done-receipt" then "done-receipt" else family
                                    source.Body.Contains("fsgg:" + prefix, StringComparison.Ordinal)
                                    && supported |> List.exists (fst >> (=) family) |> not)
                            match malformed with
                            | Some family -> refuse $"malformed-marker:{family}:{source.NodeId}"
                            | None ->
                                let schemaValid =
                                    supported |> List.forall (fun (family, _) ->
                                        match family, source.SourceKind with
                                        | "delivery-receipt", "pull-comment" -> true
                                        | "legacy-done-receipt", "issue-comment" ->
                                            MigrationLegacyReceiptParser.tryParse WorkItemComment source.Body = Ok(Some LegacyDoneReceipt)
                                        | "intake-receipt", ("issue-body" | "issue-comment" | "pull-comment") -> true
                                        | _ -> false)
                                let deliveryValid =
                                    supported |> List.forall (fun (family, _) ->
                                        if family <> "delivery-receipt" then true
                                        else
                                            match MigrationLegacyReceiptParser.tryParse PullRequestComment source.Body with
                                            | Ok(Some(MigrationLegacyReceipt.DeliveryReceipt(_, _, _))) -> true
                                            | _ -> false)
                                if not schemaValid || not deliveryValid then
                                    let family = supported.Head |> fst
                                    refuse $"malformed-marker:{family}:{source.NodeId}"
                                else
                                    let parsed =
                                        supported |> List.map (fun (family, matched) ->
                                            { Family=family; SubjectKind=source.SourceKind
                                              SubjectNumber=source.SubjectNumber; NodeId=source.NodeId
                                              ObservedAtUtc=source.ObservedAtUtc; PayloadSha256=source.PayloadSha256
                                              MarkerSha256=sha matched.Value })
                                    Ok(accumulated @ parsed))) (Ok [])
        |> Result.bind (fun values ->
            let keys = values |> List.map (fun value -> value.Family, value.NodeId, value.MarkerSha256)
            if keys.Length <> (keys |> Set.ofList |> Set.count) then refuse "duplicate-observation"
            else Ok(values |> List.sortBy (fun value -> value.Family, value.SubjectNumber, value.NodeId)))

    let internal proposeVerifiedForTests
        (request: MigrationHistoricalLossRequest)
        (pages: MigrationHistoricalLossPageBinding list)
        (subjects: MigrationHistoricalLossSubjectBinding list)
        (texts: MigrationHistoricalLossTextSource list) =
        if not (validRequest request) then refuse "invalid-request"
        elif pages.IsEmpty then refuse "incomplete-pages"
        elif pages |> List.exists (fun page ->
            String.IsNullOrWhiteSpace page.Stream || String.IsNullOrWhiteSpace page.RequestedUri
            || not (hex 64 page.PayloadSha256)) then refuse "page-binding"
        elif (pages |> List.map (fun page -> page.Stream, page.SubjectNumber, page.RequestedUri) |> Set.ofList |> Set.count) <> pages.Length then
            refuse "duplicate-page"
        elif subjects |> List.exists (fun subject ->
            subject.ObservedAtUtc > request.CutoffUtc || subject.ObservedAtUtc.Offset <> TimeSpan.Zero
            || String.IsNullOrWhiteSpace subject.NodeId || not (hex 64 subject.PayloadSha256)) then
            refuse "post-cutoff-or-unknown-subject"
        elif (subjects |> List.map (fun subject -> subject.SubjectKind, subject.NodeId) |> Set.ofList |> Set.count) <> subjects.Length then
            refuse "duplicate-subject"
        elif texts |> List.exists (fun text -> text.ObservedAtUtc > request.CutoffUtc) then refuse "post-cutoff-or-unknown-subject"
        else
            observations texts
            |> Result.map (fun found ->
                let families =
                    requiredFamilies |> List.map (fun family ->
                        let rows = found |> List.filter (_.Family >> (=) family)
                        { Family=family
                          ObservationState=if rows.IsEmpty then "observed-zero" else $"observed-{rows.Length}"
                          Observations=rows
                          ProducerGap=$"protected-producer-unavailable:{family}"
                          HistoryGap=$"historical-completeness-unresolved:{family}" })
                let census =
                    fingerprint (seq {
                        yield "fsgg.migration-historical-loss-census/1"; yield string request.RepositoryId
                        yield request.RepositoryFullName; yield request.CutoffUtc.ToString("O")
                        for source: MigrationHistoricalLossSourceBinding in request.SourceBindings do
                            yield source.Family; yield source.ProducerId; yield source.SourceRevision
                            yield source.SourceUri; yield source.SourceBlobSha256
                        for page in pages do
                            yield page.Stream; yield defaultArg (page.SubjectNumber |> Option.map string) ""
                            yield page.RequestedUri; yield page.PayloadSha256; yield defaultArg page.NextUri ""
                        for subject: MigrationHistoricalLossSubjectBinding in subjects do
                            yield subject.SubjectKind; yield string subject.SubjectNumber; yield subject.NodeId
                            yield subject.ObservedAtUtc.ToString("O"); yield subject.PayloadSha256
                        for row in found do
                            yield row.Family; yield row.SubjectKind; yield string row.SubjectNumber
                            yield row.NodeId; yield row.MarkerSha256 })
                let proposalFingerprint =
                    fingerprint (seq {
                        yield "fsgg.migration-historical-loss-proposal/1"; yield census
                        for family in families do
                            yield family.Family; yield family.ObservationState
                            yield family.ProducerGap; yield family.HistoryGap })
                { RepositoryId=request.RepositoryId; RepositoryFullName=request.RepositoryFullName
                  CutoffUtc=request.CutoffUtc; Pages=pages; Subjects=subjects; Families=families
                  UnresolvedProducerFamilies=requiredFamilies; UnresolvedHistoryFamilies=requiredFamilies
                  CensusFingerprint=census; ProposalFingerprint=proposalFingerprint })

    let private timelineTimestampFromRaw (record: MigrationTimelineRecord) =
        try
            use document = JsonDocument.Parse record.PayloadJson
            let root = document.RootElement
            let names =
                if root.ValueKind = JsonValueKind.Object then root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                else []
            let text (name: string) =
                let mutable value = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String
                then Some(value.GetString()) else None
            if root.ValueKind <> JsonValueKind.Object || names.Length <> (names |> Set.ofList |> Set.count)
               || text "node_id" <> Some record.NodeId || text "event" <> Some record.EventKind then None
            else
                [ "created_at"; "submitted_at"; "updated_at" ]
                |> List.tryPick (fun name ->
                    let mutable parsed = DateTimeOffset.MinValue
                    text name |> Option.bind (fun value ->
                        if DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed)
                        then Some(parsed.ToUniversalTime()) else None))
        with :? JsonException | :? InvalidOperationException -> None

    let private pageBindings (input: MigrationNativeActivityInput) =
        let bind stream subject (pages: MigrationRestPageEvidence list) =
            pages |> List.map (fun page ->
                { Stream=stream; SubjectNumber=subject; RequestedUri=page.RequestedUri
                  PayloadSha256=page.PayloadSha256; NextUri=page.NextUri })
        [ yield! bind "issues" None input.Issues.Pages
          yield! bind "pulls" None input.PullRequests.Pages
          for row in input.IssueComments do yield! bind "issue-comments" (Some row.SubjectNumber) row.Pages
          for row in input.IssueEvents do yield! bind "issue-events" (Some row.SubjectNumber) row.Pages
          for row in input.IssueTimelines do yield! bind "issue-timeline" (Some row.SubjectNumber) row.Pages
          for row in input.PullRequestComments do yield! bind "pull-comments" (Some row.SubjectNumber) row.Pages
          for row in input.PullRequestEvents do yield! bind "pull-events" (Some row.SubjectNumber) row.Pages
          for row in input.PullRequestTimelines do yield! bind "pull-timeline" (Some row.SubjectNumber) row.Pages ]

    let private captureBindings (input: MigrationNativeActivityInput) =
        let subject kind number node (at: DateTimeOffset) digest : MigrationHistoricalLossSubjectBinding =
            { SubjectKind=kind; SubjectNumber=number; NodeId=node
              ObservedAtUtc=at.ToUniversalTime(); PayloadSha256=digest }
        let mutable unknownTimeline = false
        let subjects = ResizeArray<MigrationHistoricalLossSubjectBinding>()
        let texts = ResizeArray<MigrationHistoricalLossTextSource>()
        for issue: MigrationIssueRecord in input.Issues.Issues do
            subjects.Add(subject "issue" issue.Number issue.NodeId issue.UpdatedAt issue.PayloadSha256)
            try
                use document = JsonDocument.Parse issue.PayloadJson
                let mutable body = Unchecked.defaultof<JsonElement>
                let text = if document.RootElement.TryGetProperty("body", &body) && body.ValueKind = JsonValueKind.String then body.GetString() else ""
                texts.Add { SourceKind="issue-body"; SubjectNumber=issue.Number; NodeId=issue.NodeId
                            ObservedAtUtc=issue.UpdatedAt.ToUniversalTime(); PayloadSha256=issue.PayloadSha256; Body=text }
            with :? JsonException -> unknownTimeline <- true
        for pull: MigrationPullRequestRecord in input.PullRequests.PullRequests do
            subjects.Add(subject "pull-request" pull.Number pull.NodeId pull.UpdatedAt pull.PayloadSha256)
        let comments kind streams =
            for stream: MigrationIssueCommentPopulation in streams do
                for comment in stream.Comments do
                    subjects.Add(subject kind comment.SubjectNumber comment.NodeId comment.UpdatedAt comment.PayloadSha256)
                    texts.Add { SourceKind=kind; SubjectNumber=comment.SubjectNumber; NodeId=comment.NodeId
                                ObservedAtUtc=comment.UpdatedAt.ToUniversalTime(); PayloadSha256=comment.PayloadSha256; Body=comment.Body }
        comments "issue-comment" input.IssueComments
        comments "pull-comment" input.PullRequestComments
        let events kind streams =
            for stream: MigrationIssueEventPopulation in streams do
                for event in stream.Events do subjects.Add(subject kind event.SubjectNumber event.NodeId event.CreatedAt event.PayloadSha256)
        events "issue-event" input.IssueEvents; events "pull-event" input.PullRequestEvents
        let timelines kind streams =
            for stream: MigrationTimelinePopulation in streams do
                for event in stream.Records do
                    match timelineTimestampFromRaw event with
                    | Some at -> subjects.Add(subject kind event.SubjectNumber event.NodeId at event.PayloadSha256)
                    | None -> unknownTimeline <- true
        timelines "issue-timeline" input.IssueTimelines; timelines "pull-timeline" input.PullRequestTimelines
        if unknownTimeline then refuse "unknown-subject-time" else Ok(List.ofSeq subjects, List.ofSeq texts)

    let propose (request: MigrationHistoricalLossRequest) (capture: MigrationClaimEventPartialCapture) =
        if not (validRequest request) then refuse "invalid-request"
        else
            match MigrationClaimEventInspectBinder.qualifyCanonical capture with
            | Error reason when reason = "claim-event-authority-incomplete:legacy-inventory-producer-unavailable:" + String.concat "," requiredFamilies ->
                let pieces = request.RepositoryFullName.Split('/')
                let firstPage = capture.NativeFirst.Input.Issues.Pages |> List.tryHead
                match firstPage with
                | None -> refuse "incomplete-pages"
                | Some page ->
                    let uri = Uri page.RequestedUri
                    let options =
                        { ApiBase=Uri(uri.GetLeftPart(UriPartial.Authority) + "/")
                          GraphQLUri=Uri(uri.GetLeftPart(UriPartial.Authority) + "/graphql")
                          Token="retained-capture"; UserAgent="historical-loss-census"
                          Owner=pieces[0]; Repository=pieces[1]; ExpectedRepositoryId=request.RepositoryId }
                    match MigrationNativeActivity.reconcile options capture.NativeFirst.Input,
                          MigrationNativeActivity.reconcile options capture.NativeSecond.Input with
                    | Ok first, Ok second when first = capture.NativeFirst.Snapshot
                                                    && second = capture.NativeSecond.Snapshot
                                                    && capture.NativeFirst = capture.NativeSecond ->
                        match MigrationClaimEventCaptureContract.validateLegacyInventory capture.LegacyInventory with
                        | Error reason -> refuse ("source-inventory:" + reason)
                        | Ok inventory ->
                            let sourceMismatch =
                                request.SourceBindings |> List.exists (fun binding ->
                                    let identity = binding.SourceUri + "#sha256:" + binding.SourceBlobSha256
                                    inventory.Sources |> List.exists (fun source ->
                                        source.SchemaFamily = binding.Family && source.ProducerId = binding.ProducerId
                                        && source.ProducerRevision = binding.SourceRevision && source.SourceIdentity = identity) |> not)
                            if sourceMismatch then refuse "source-mismatch"
                            else
                                captureBindings capture.NativeFirst.Input
                                |> Result.bind (fun (subjects, texts) ->
                                    proposeVerifiedForTests request (pageBindings capture.NativeFirst.Input) subjects texts)
                    | Ok _, Ok _ -> refuse "pass-drift"
                    | _ -> refuse "native-capture"
            | Error reason -> refuse ("partial-capture:" + reason)
            | Ok _ -> refuse "canonical-capture-not-a-loss-proposal"
