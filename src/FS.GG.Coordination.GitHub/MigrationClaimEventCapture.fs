namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type MigrationClaimJournalTarget =
    { Kind: JournalKind
      AggregateId: string }

type MigrationClaimEventLink =
    { CommentNodeId: string
      Target: MigrationClaimJournalTarget
      Generation: int64
      OperationId: string }

type MigrationClaimEventDeclaration =
    { RepositoryId: int64
      RepositoryFullName: string
      InventoryComplete: bool
      Targets: MigrationClaimJournalTarget list
      Links: MigrationClaimEventLink list }

type MigrationClaimEventMarker =
    { CommentNodeId: string
      SubjectNumber: int
      SubjectNodeId: string
      Kind: string
      BodySha256: string }

type MigrationClaimEventSnapshot =
    { RepositoryId: int64
      NativeSha256: string
      Markers: MigrationClaimEventMarker list
      JournalCount: int
      JournalEventCount: int
      NormalizedSha256: string }

type MigrationClaimEventFailure =
    | NativeFailure of MigrationReadFailure
    | MissingInventory
    | InvalidDeclaration of string
    | InvalidMarker of string
    | MissingCorrespondence of string
    | ForeignCorrespondence of string
    | JournalReadFailure of string
    | JournalInvalid of JournalFailure
    | ChangedPass

type IMigrationClaimJournalRead =
    abstract Read: AggregateAddress -> JournalObservation

[<RequireQualifiedAccess>]
module MigrationClaimEventCapture =
    let private shaText (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()

    let private markerRegex =
        Regex(@"\A<!--\s*fsgg:(?<tag>[a-z0-9/-]+)(?:\s+(?<fields>[^>]*?))?\s*-->", RegexOptions.CultureInvariant)

    let private fieldRegex =
        Regex(@"(?<key>[a-z][a-z0-9-]*)=(?<value>[^\s]+)", RegexOptions.CultureInvariant)

    let private distinct rows = (rows |> Set.ofList).Count = rows.Length

    let private jsonString (root: JsonElement) (name: string) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &property) && property.ValueKind = JsonValueKind.String then
            let value = property.GetString()
            if String.IsNullOrWhiteSpace value then None else Some value
        else None

    let private rawCommentMatches repositoryFullName (comment: MigrationIssueCommentRecord) =
        try
            use document = JsonDocument.Parse comment.PayloadJson
            let root = document.RootElement
            let mutable number = 0L
            let mutable idProperty = Unchecked.defaultof<JsonElement>
            let expectedPath =
                $"/repos/{repositoryFullName}/issues/{comment.SubjectNumber}"
            match jsonString root "issue_url" with
            | None -> false
            | Some issueUrl ->
                let mutable uri = Unchecked.defaultof<Uri>
                root.TryGetProperty("id", &idProperty)
                && idProperty.ValueKind = JsonValueKind.Number
                && idProperty.TryGetInt64(&number)
                && number = comment.DatabaseId
                && jsonString root "node_id" = Some comment.NodeId
                && jsonString root "body" = Some comment.Body
                && Uri.TryCreate(issueUrl, UriKind.Absolute, &uri)
                && uri.Scheme = Uri.UriSchemeHttps
                && uri.AbsolutePath = expectedPath
        with _ -> false

    let private markerFor repositoryFullName subjectNodeId (comment: MigrationIssueCommentRecord) =
        let body = comment.Body
        let matchResult = markerRegex.Match body
        let startsFsgg = Regex.IsMatch(body, @"\A<!--\s*fsgg:", RegexOptions.CultureInvariant)
        if not matchResult.Success then
            if startsFsgg then Error(InvalidMarker comment.NodeId) else Ok None
        else
            let kind = matchResult.Groups.["tag"].Value
            if kind <> "claim" && not (kind.Contains("receipt", StringComparison.Ordinal)) then Ok None
            else
                let fields = matchResult.Groups.["fields"].Value
                let parsed = fieldRegex.Matches fields |> Seq.cast<Match> |> Seq.toList
                let keys = parsed |> List.map (fun item -> item.Groups.["key"].Value)
                let residue = fieldRegex.Replace(fields, "").Trim()
                let values = parsed |> List.map (fun item -> item.Groups.["key"].Value, item.Groups.["value"].Value) |> Map.ofList
                let claimValid =
                    if kind <> "claim" then true
                    else
                        match Map.tryFind "worker" values, Map.tryFind "lease" values with
                        | Some worker, Some lease ->
                            let mutable minutes = 0
                            not (String.IsNullOrWhiteSpace worker)
                            && Int32.TryParse(lease, &minutes) && minutes > 0
                        | _ -> false
                if residue <> "" || not (distinct keys) || not claimValid
                   || not (rawCommentMatches repositoryFullName comment) then
                    Error(InvalidMarker comment.NodeId)
                else
                    Ok(Some
                        { CommentNodeId=comment.NodeId
                          SubjectNumber=comment.SubjectNumber
                          SubjectNodeId=subjectNodeId
                          Kind=kind
                          BodySha256=shaText body })

    let private markers repositoryFullName (native: MigrationNativeActivityCapture) =
        let issueRows =
            native.Input.IssueComments
            |> List.collect (fun stream -> stream.Comments |> List.map (fun comment -> stream.SubjectNodeId, comment))
        let prRows =
            native.Input.PullRequestComments
            |> List.collect (fun stream -> stream.Comments |> List.map (fun comment -> stream.SubjectNodeId, comment))
        (issueRows @ prRows)
        |> List.fold (fun state (nodeId, comment) ->
            state |> Result.bind (fun found ->
                markerFor repositoryFullName nodeId comment
                |> Result.map (function Some marker -> marker :: found | None -> found))) (Ok [])
        |> Result.map List.rev

    let private jsonInt64 (root: JsonElement) (name: string) =
        let mutable property = Unchecked.defaultof<JsonElement>
        let mutable value = 0L
        if root.TryGetProperty(name, &property) && property.ValueKind = JsonValueKind.Number
           && property.TryGetInt64(&value) then Some value else None

    let private eventLink (commit: JournalCommit) =
        try
            use document = JsonDocument.Parse commit.Event.Bytes
            let root = document.RootElement
            if root.ValueKind <> JsonValueKind.Object then None
            else jsonString root "legacyCommentNodeId"
        with _ -> None

    let private matchesEvent repositoryId (marker: MigrationClaimEventMarker) (commit: JournalCommit) =
        try
            use document = JsonDocument.Parse commit.Event.Bytes
            let root = document.RootElement
            root.ValueKind = JsonValueKind.Object
            && jsonInt64 root "repositoryId" = Some repositoryId
            && jsonInt64 root "subjectNumber" = Some(int64 marker.SubjectNumber)
            && jsonString root "subjectNodeId" = Some marker.SubjectNodeId
            && jsonString root "legacyCommentNodeId" = Some marker.CommentNodeId
            && jsonString root "legacyMarkerKind" = Some marker.Kind
            && jsonString root "legacyBodySha256" = Some marker.BodySha256
            && jsonString root "operationId" = Some commit.OperationId
        with _ -> false

    let reconcile declaration native journals =
        let fail reason = Error reason
        let targets = declaration.Targets
        let links = declaration.Links
        if not declaration.InventoryComplete then fail MissingInventory
        elif declaration.RepositoryId <= 0L
             || String.IsNullOrWhiteSpace declaration.RepositoryFullName
             || not (declaration.RepositoryFullName.Contains('/'))
             || (targets |> List.exists (fun target ->
                 target.Kind <> JournalKind.Claim && target.Kind <> JournalKind.Operation))
             || not (distinct targets)
             || not (distinct (links |> List.map _.CommentNodeId))
             || not (distinct (links |> List.map (fun link -> link.Target, link.Generation))) then
            fail (InvalidDeclaration "inventory")
        elif native.Snapshot.RepositoryId <> declaration.RepositoryId then
            fail (ForeignCorrespondence "repository")
        else
            match MigrationNativeActivity.reconcile native.Input with
            | Error reason -> fail (NativeFailure reason)
            | Ok verified when verified <> native.Snapshot -> fail (NativeFailure(MigrationReadFailure.SnapshotMismatch "native-seal"))
            | Ok verified ->
                match markers declaration.RepositoryFullName native with
                | Error reason -> fail reason
                | Ok markerRows ->
                    let markerIds = markerRows |> List.map _.CommentNodeId
                    let linkIds = links |> List.map _.CommentNodeId
                    let journalTargets = journals |> List.map fst
                    if Set.ofList markerIds <> Set.ofList linkIds then
                        fail (MissingCorrespondence "legacy-markers")
                    elif not (distinct journalTargets) || Set.ofList journalTargets <> Set.ofList targets then
                        fail (MissingCorrespondence "journal-inventory")
                    elif links |> List.exists (fun link ->
                        not (List.contains link.Target targets)
                        || link.Generation <= 0L
                        || String.IsNullOrWhiteSpace link.OperationId) then
                        fail (InvalidDeclaration "link")
                    else
                        let checkedJournals =
                            journals |> List.fold (fun state (target, observation) ->
                                state |> Result.bind (fun snapshots ->
                                    match ShardedJournalAdapter.address target.Kind target.AggregateId with
                                    | Error reason -> Error(JournalInvalid reason)
                                    | Ok address ->
                                        ShardedJournalAdapter.validate address observation
                                        |> Result.mapError JournalInvalid
                                        |> Result.map (fun snapshot -> (target, snapshot) :: snapshots))) (Ok [])
                        match checkedJournals with
                        | Error reason -> fail reason
                        | Ok snapshots ->
                            let allCommits = snapshots |> List.collect (fun (target, snapshot) ->
                                snapshot.Commits |> List.map (fun commit -> target, commit))
                            let boundEventIds = allCommits |> List.choose (fun (_, commit) -> eventLink commit)
                            if not (distinct boundEventIds) || Set.ofList boundEventIds <> Set.ofList linkIds then
                                fail (MissingCorrespondence "journal-events")
                            else
                                let markerMap = markerRows |> List.map (fun marker -> marker.CommentNodeId, marker) |> Map.ofList
                                let commitMap = allCommits |> List.map (fun (target, commit) ->
                                    (target, commit.Head.Generation), commit) |> Map.ofList
                                let wrong = links |> List.tryPick (fun link ->
                                    let marker = Map.find link.CommentNodeId markerMap
                                    let expectedSubject =
                                        $"{declaration.RepositoryFullName}#{marker.SubjectNumber}"
                                    let expectedClaimAddress = ClaimTouchSetAdapter.claimAddress expectedSubject
                                    let linkedAddress =
                                        ShardedJournalAdapter.address link.Target.Kind link.Target.AggregateId
                                    if marker.Kind = "claim"
                                       && (link.Target.Kind <> JournalKind.Claim
                                           || match expectedClaimAddress, linkedAddress with
                                              | Ok expected, Ok linked -> expected <> linked
                                              | _ -> true) then
                                        Some "claim-address"
                                    else
                                        match Map.tryFind (link.Target, link.Generation) commitMap with
                                        | None -> Some "journal-generation"
                                        | Some commit when commit.OperationId <> link.OperationId -> Some "operation"
                                        | Some commit when not (matchesEvent declaration.RepositoryId marker commit) -> Some "event-payload"
                                        | Some _ -> None)
                                match wrong with
                                | Some reason -> fail (ForeignCorrespondence reason)
                                | None ->
                                    let markerRows = markerRows |> List.sortBy _.CommentNodeId
                                    let parts =
                                        [ string declaration.RepositoryId; declaration.RepositoryFullName; verified.NormalizedSha256 ]
                                        @ (markerRows |> List.collect (fun marker ->
                                            [ marker.CommentNodeId; string marker.SubjectNumber; marker.SubjectNodeId
                                              marker.Kind; marker.BodySha256 ]))
                                        @ (snapshots |> List.sortBy (fun (target, _) -> target.Kind, target.AggregateId)
                                           |> List.collect (fun (target, snapshot) ->
                                               [ ShardedJournalAdapter.journalKind target.Kind; target.AggregateId; snapshot.Revision ]
                                               @ (snapshot.Commits |> List.collect (fun commit ->
                                                   [ commit.CommitOid; string commit.Head.Generation; commit.OperationId
                                                     commit.Event.Digest; commit.Head.HeadDigest ]))))
                                    let framed = parts |> List.map (fun part -> $"{Encoding.UTF8.GetByteCount part}:{part}") |> String.concat ""
                                    Ok { RepositoryId=declaration.RepositoryId
                                         NativeSha256=verified.NormalizedSha256
                                         Markers=markerRows
                                         JournalCount=snapshots.Length
                                         JournalEventCount=allCommits.Length
                                         NormalizedSha256=shaText framed }

    let private readJournals declaration (reader: IMigrationClaimJournalRead) =
        declaration.Targets
        |> List.fold (fun state target ->
            state |> Result.bind (fun rows ->
                match ShardedJournalAdapter.address target.Kind target.AggregateId with
                | Error reason -> Error(JournalInvalid reason)
                | Ok address ->
                    try Ok((target, reader.Read address) :: rows)
                    with ex -> Error(JournalReadFailure ex.Message))) (Ok [])
        |> Result.map List.rev

    let private captureOnce options nativeTransport declaration journalRead =
        MigrationNativeActivity.capture options nativeTransport
        |> Result.mapError NativeFailure
        |> Result.bind (fun native ->
            readJournals declaration journalRead
            |> Result.bind (reconcile declaration native))

    let captureStable options nativeTransport declaration journalRead =
        captureOnce options nativeTransport declaration journalRead
        |> Result.bind (fun first ->
            captureOnce options nativeTransport declaration journalRead
            |> Result.bind (fun second ->
                if first = second then Ok second else Error ChangedPass))
