namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub

type MigrationLegacyClaimMarker =
    {
        SubjectNumber: int
        CommentNodeId: string
        Worker: string
        LeaseMinutes: int
        Renewed: int64
        SessionOperationId: string option
        PayloadSha256: string
    }

type MigrationLegacyIntakeMarker =
    {
        IssueNumber: int
        IssueNodeId: string
        DraftId: string
        DraftDigest: string
        PayloadSha256: string
    }

type MigrationClaimEventPartialCapture =
    {
        NativeFirst: MigrationNativeActivityCapture
        NativeSecond: MigrationNativeActivityCapture
        Journals: MigrationClaimJournalTwoPass
        LegacyInventory: MigrationLegacyReceiptInventory
        ClaimMarkers: MigrationLegacyClaimMarker list
        IntakeMarkers: MigrationLegacyIntakeMarker list
        MissingAuthorities: string list
        Fingerprint: string
    }

[<RequireQualifiedAccess>]
module MigrationClaimEventInspectBinder =
    let private claimPattern =
        Regex(
            "^<!-- fsgg:claim worker=(?<worker>[^ ]+) lease=(?<lease>[0-9]+) renewed=(?<renewed>[0-9]+)(?: session=(?<session>[a-f0-9]{32}))?(?: prev=(?<prev>[^ ]+))?(?: pathRepo=(?<pathRepo>[^ ]+))?(?: agentContract=(?<agentContract>[^ ]+))? -->$",
            RegexOptions.CultureInvariant ||| RegexOptions.Compiled
        )

    let private intakePattern =
        Regex(
            "^<!-- fsgg:intake:v1 id=(?<id>[A-Za-z0-9_.-]+) digest=(?<digest>[a-f0-9]{64}) -->(?:\\r?\\n|$)",
            RegexOptions.CultureInvariant ||| RegexOptions.Compiled
        )

    let private shaText (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private frame (value: string) =
        $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private membersUnique (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> Set.ofList |> Set.count)

    let private stringProperty (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>

        if value.TryGetProperty(name, &found) && found.ValueKind = JsonValueKind.String then
            Some(found.GetString())
        else
            None

    let private int64Property (name: string) (value: JsonElement) =
        let mutable found = Unchecked.defaultof<JsonElement>
        let mutable parsed = 0L

        if
            value.TryGetProperty(name, &found)
            && found.ValueKind = JsonValueKind.Number
            && found.TryGetInt64(&parsed)
        then
            Some parsed
        else
            None

    let private timestampProperty (name: string) (value: JsonElement) =
        stringProperty name value
        |> Option.bind (fun text ->
            let mutable parsed = DateTimeOffset.MinValue

            if DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed) then
                Some parsed
            else
                None)

    let private parseMarker (comment: MigrationIssueCommentRecord) =
        try
            if shaText comment.PayloadJson <> comment.PayloadSha256 then
                Error "claim-event-comment-payload"
            else
                use document = JsonDocument.Parse comment.PayloadJson
                let root = document.RootElement

                let user =
                    let mutable found = Unchecked.defaultof<JsonElement>

                    if root.TryGetProperty("user", &found) then
                        Some found
                    else
                        None

                let typedMatches =
                    membersUnique root
                    && int64Property "id" root = Some comment.DatabaseId
                    && stringProperty "node_id" root = Some comment.NodeId
                    && stringProperty "body" root = Some comment.Body
                    && timestampProperty "created_at" root = Some comment.CreatedAt
                    && timestampProperty "updated_at" root = Some comment.UpdatedAt
                    && (user |> Option.filter membersUnique |> Option.bind (stringProperty "login")) =
                        Some comment.ActorLogin
                    && (stringProperty "issue_url" root
                        |> Option.exists (fun value ->
                            value.EndsWith($"/issues/{comment.SubjectNumber}", StringComparison.Ordinal)))

                if not typedMatches then
                    Error "claim-event-comment-raw-typed"
                elif comment.Body.Contains("fsgg:claim", StringComparison.Ordinal) then
                    let matched = claimPattern.Match comment.Body

                    if not matched.Success then
                        Error "claim-event-unknown-claim-marker"
                    else
                        let mutable lease = 0
                        let mutable renewed = 0L

                        if
                            not (
                                Int32.TryParse(
                                    matched.Groups["lease"].Value,
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    &lease
                                )
                            )
                            || lease <= 0
                            || not (
                                Int64.TryParse(
                                    matched.Groups["renewed"].Value,
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    &renewed
                                )
                            )
                            || renewed <= 0L
                        then
                            Error "claim-event-invalid-claim-marker"
                        else
                            let session = matched.Groups["session"]

                            Ok(
                                Some
                                    {
                                        SubjectNumber = comment.SubjectNumber
                                        CommentNodeId = comment.NodeId
                                        Worker = matched.Groups["worker"].Value
                                        LeaseMinutes = lease
                                        Renewed = renewed
                                        SessionOperationId = if session.Success then Some session.Value else None
                                        PayloadSha256 = comment.PayloadSha256
                                    }
                            )
                elif comment.Body.Contains("C-claim", StringComparison.Ordinal) then
                    Error "claim-event-historical-claim-parser-unavailable"
                else
                    Ok None
        with
        | :? JsonException
        | :? InvalidOperationException -> Error "claim-event-comment-json"

    let private allComments (input: MigrationNativeActivityInput) =
        (input.IssueComments @ input.PullRequestComments) |> List.collect _.Comments

    let private parseMarkers native =
        allComments native.Input
        |> List.fold
            (fun state comment ->
                state
                |> Result.bind (fun values ->
                    parseMarker comment
                    |> Result.map (function
                        | Some value -> value :: values
                        | None -> values)))
            (Ok [])
        |> Result.map (List.sortBy (fun value -> value.SubjectNumber, value.CommentNodeId))

    let private parseIntakeMarker (issue: MigrationIssueRecord) =
        try
            if shaText issue.PayloadJson <> issue.PayloadSha256 then
                Error "claim-event-issue-payload"
            else
                use document = JsonDocument.Parse issue.PayloadJson
                let root = document.RootElement

                let body =
                    let mutable found = Unchecked.defaultof<JsonElement>

                    if not (root.TryGetProperty("body", &found)) then
                        None
                    elif found.ValueKind = JsonValueKind.Null then
                        Some ""
                    elif found.ValueKind = JsonValueKind.String then
                        Some(found.GetString())
                    else
                        None

                let typedMatches =
                    membersUnique root
                    && int64Property "number" root = Some(int64 issue.Number)
                    && int64Property "id" root = Some issue.DatabaseId
                    && stringProperty "node_id" root = Some issue.NodeId
                    && stringProperty "state" root = Some issue.State
                    && timestampProperty "updated_at" root = Some issue.UpdatedAt

                match typedMatches, body with
                | false, _
                | _, None -> Error "claim-event-issue-raw-typed"
                | true, Some value when value.Contains("fsgg:intake", StringComparison.Ordinal) ->
                    let matched = intakePattern.Match value

                    if not matched.Success then
                        Error "claim-event-unknown-intake-marker"
                    else
                        Ok(
                            Some
                                {
                                    IssueNumber = issue.Number
                                    IssueNodeId = issue.NodeId
                                    DraftId = matched.Groups["id"].Value
                                    DraftDigest = matched.Groups["digest"].Value
                                    PayloadSha256 = issue.PayloadSha256
                                }
                        )
                | true, Some _ -> Ok None
        with
        | :? JsonException
        | :? InvalidOperationException -> Error "claim-event-issue-json"

    let private parseIntakeMarkers native =
        native.Input.Issues.Issues
        |> List.fold
            (fun state issue ->
                state
                |> Result.bind (fun values ->
                    parseIntakeMarker issue
                    |> Result.map (function
                        | Some value -> value :: values
                        | None -> values)))
            (Ok [])
        |> Result.map (List.sortBy (fun value -> value.IssueNumber, value.IssueNodeId))

    let private decodedSource (read: MigrationReviewDeliveryRead) =
        try
            use document = JsonDocument.Parse read.RawBody
            let content = stringProperty "content" document.RootElement |> Option.get
            let bytes = Convert.FromBase64String(content.Replace("\n", ""))
            let digest = bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
            Some(read.Request.Uri + "#sha256:" + digest, Encoding.UTF8.GetString bytes)
        with
        | :? JsonException
        | :? FormatException
        | :? InvalidOperationException -> None

    let private intakeProducerBound (inventory: MigrationLegacyReceiptInventory) =
        // ca6dd7bd's IntakeReceipt.marker formats the marker passed to the issue-body
        // writer in Writes.renderIntake. The identity is the complete audited blob.
        let revision = "ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce"
        let uri =
            "https://api.github.com/repos/FS-GG/.github/contents/src/FS.GG.Coord.Core/IntakeReceipt.fs?ref="
            + revision
        let identity = uri + "#sha256:6ae65a6b3b48f7f865330efca90e41a1786dd820da01c19f5623698b2761baa6"
        let intakeSources =
            inventory.Sources
            |> List.filter (fun source ->
                source.SchemaFamily = "intake-marker"
                && source.SourceKind = ProtectedProducer
                && source.ProducerRevision = revision
                && source.SourceIdentity = identity)

        intakeSources
        |> List.exists (fun source ->
            inventory.ProducerReads
            |> List.exists (fun read ->
                decodedSource read
                |> Option.exists (fun (decodedIdentity, _) ->
                    read.Request.Uri = uri
                    && decodedIdentity = identity
                    && source.SourceIdentity = decodedIdentity)))

    let private journalClaims (journals: MigrationClaimJournalTwoPass) =
        try
            let claims =
                journals.ClaimFirst.ClaimHistories
                |> List.collect _.ClaimEntries
                |> List.choose (fun entry ->
                    if entry.ClaimRecord.Family <> ClaimSchemaFamily then
                        None
                    else
                        use response = JsonDocument.Parse entry.ClaimReads[2].RawBody
                        let content = stringProperty "content" response.RootElement |> Option.get
                        let bytes = Convert.FromBase64String(content.Replace("\n", ""))
                        use event = JsonDocument.Parse bytes
                        let operation = stringProperty "operationId" event.RootElement |> Option.get
                        let subject = stringProperty "subject" event.RootElement |> Option.get
                        let owner = stringProperty "owner" event.RootElement |> Option.get
                        Some(operation, (subject, owner)))

            let conflicts =
                claims
                |> List.groupBy fst
                |> List.choose (fun (operation, values) ->
                    let identities = values |> List.map snd |> Set.ofList
                    if identities.Count = 1 then None else Some operation)

            if not (List.isEmpty conflicts) then
                Error("claim-event-journal-operation-conflict:" + String.concat "," conflicts)
            else
                Ok(claims |> Map.ofList)
        with
        | :? JsonException
        | :? FormatException
        | :? InvalidOperationException -> Error "claim-event-journal-claim-raw"

    let private fingerprint
        (nativeFirst: MigrationNativeActivityCapture)
        (nativeSecond: MigrationNativeActivityCapture)
        (journals: MigrationClaimJournalTwoPass)
        (inventory: MigrationLegacyReceiptInventory)
        (markers: MigrationLegacyClaimMarker list)
        (intakeMarkers: MigrationLegacyIntakeMarker list)
        (missing: string list)
        =
        [
            nativeFirst.Snapshot.NormalizedSha256
            nativeSecond.Snapshot.NormalizedSha256
            journals.ClaimFirst.ClaimFingerprint
            inventory.Fingerprint
            yield!
                markers
                |> List.collect (fun marker ->
                    [
                        string marker.SubjectNumber
                        marker.CommentNodeId
                        marker.Worker
                        string marker.LeaseMinutes
                        string marker.Renewed
                        defaultArg marker.SessionOperationId ""
                        marker.PayloadSha256
                    ])
            yield!
                intakeMarkers
                |> List.collect (fun marker ->
                    [
                        string marker.IssueNumber
                        marker.IssueNodeId
                        marker.DraftId
                        marker.DraftDigest
                        marker.PayloadSha256
                    ])
            yield! missing
        ]
        |> List.map frame
        |> String.concat ""
        |> shaText

    let bindPartial options nativeFirst nativeSecond journals legacyInventory =
        match
            MigrationNativeActivity.reconcile options nativeFirst.Input,
            MigrationNativeActivity.reconcile options nativeSecond.Input
        with
        | Error _, _
        | _, Error _ -> Error "claim-event-native-capture"
        | Ok firstSnapshot, Ok secondSnapshot when
            firstSnapshot <> nativeFirst.Snapshot || secondSnapshot <> nativeSecond.Snapshot
            ->
            Error "claim-event-native-snapshot"
        | Ok _, Ok _ when nativeFirst <> nativeSecond -> Error "claim-event-native-pass-drift"
        | Ok _, Ok _ ->
            MigrationClaimEventCaptureContract.validateTwoPass journals
            |> Result.mapError (fun reason -> "claim-event-journal:" + reason)
            |> Result.bind (fun _ ->
                MigrationClaimEventCaptureContract.validateLegacyInventory legacyInventory
                |> Result.mapError (fun reason -> "claim-event-legacy:" + reason))
            |> Result.bind (fun _ -> parseMarkers nativeFirst)
            |> Result.bind (fun markers ->
                parseIntakeMarkers nativeFirst
                |> Result.bind (fun intakeMarkers ->
                    if not (intakeProducerBound legacyInventory) then
                        Error "claim-event-intake-producer-unavailable"
                    else
                        journalClaims journals |> Result.map (fun claims -> intakeMarkers, claims))
                |> Result.bind (fun (intakeMarkers, claims) ->
                    let unmatched =
                        markers
                        |> List.choose (fun marker ->
                            marker.SessionOperationId
                            |> Option.bind (fun operation ->
                                let expectedSubject = $"{options.Owner}/{options.Repository}#{marker.SubjectNumber}"

                                match Map.tryFind operation claims with
                                | Some(subject, owner) when
                                    subject.Equals(expectedSubject, StringComparison.OrdinalIgnoreCase)
                                    && owner.Equals(marker.Worker, StringComparison.OrdinalIgnoreCase)
                                    ->
                                    None
                                | _ -> Some operation))

                    if nativeFirst.Snapshot.RepositoryId <> options.ExpectedRepositoryId then
                        Error "claim-event-repository"
                    elif not (List.isEmpty unmatched) then
                        Error("claim-event-journal-correspondence:" + String.concat "," unmatched)
                    else
                        let producerGap =
                            match MigrationClaimEventCaptureContract.qualifyLegacyInventory legacyInventory with
                            | Ok _ -> []
                            | Error reason -> [ reason ]

                        let historicalGap =
                            if markers |> List.exists (_.SessionOperationId >> Option.isNone) then
                                [ "legacy-sessionless-claim-correspondence" ]
                            else
                                []

                        let missing = producerGap @ historicalGap

                        let partial =
                            {
                                NativeFirst = nativeFirst
                                NativeSecond = nativeSecond
                                Journals = journals
                                LegacyInventory = legacyInventory
                                ClaimMarkers = markers
                                IntakeMarkers = intakeMarkers
                                MissingAuthorities = missing
                                Fingerprint = ""
                            }

                        Ok
                            { partial with
                                Fingerprint =
                                    fingerprint
                                        nativeFirst
                                        nativeSecond
                                        journals
                                        legacyInventory
                                        markers
                                        intakeMarkers
                                        missing
                            }))

    let qualifyCanonical capture =
        let expectedFingerprint =
            fingerprint
                capture.NativeFirst
                capture.NativeSecond
                capture.Journals
                capture.LegacyInventory
                capture.ClaimMarkers
                capture.IntakeMarkers
                capture.MissingAuthorities

        if capture.Fingerprint <> expectedFingerprint then
            Error "claim-event-partial-fingerprint"
        else
            MigrationClaimEventCaptureContract.qualifyLegacyInventory capture.LegacyInventory
            |> Result.mapError (fun reason -> "claim-event-authority-incomplete:" + reason)
            |> Result.bind (fun _ ->
                if List.isEmpty capture.MissingAuthorities then
                    Ok capture
                else
                    Error(
                        "claim-event-authority-incomplete:"
                        + String.concat "," capture.MissingAuthorities
                    ))
