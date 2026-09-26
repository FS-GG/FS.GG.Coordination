namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Text.RegularExpressions

/// Exact correlation needed before any future native completed-turn usage can be considered.
/// This key is structural and does not authenticate a live session on its own.
type CodexDirectTurnCorrelation =
    {
        Scope: DirectSessionTurnScope
        NativeSessionId: string
        WindowChallenge: string
        ReservationId: string
        AuthorizedSourceAdapterId: string
        IssuedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        NativeTurnId: string
        TransportIdentity: string
        ConnectionId: string
        ProtocolVersion: string
        SubscriptionDigest: string
        FirstEntryId: string
        SealedHeadEntryId: string
        TerminalStatus: string
    }

/// Known nearby evidence that is insufficient for direct completed-turn usage.
type CodexAppServerUsageCandidate =
    | ThreadSnapshot of CodexAppServerUsageUpdate
    | ExecChildCompleted of CodexTurnUsage
    | UpstreamResponseCompleted of threadId: string * turnId: string * responseId: string * counts: CodexAppServerTokenCounts

/// The installed App Server notification contract yields no native completed-turn usage fact.
/// This type has no accepted-usage case and cannot be passed to the telemetry mapper.
type CodexAppServerNoUsageVerdict =
    {
        Correlation: CodexDirectTurnCorrelation
        Reason: string
        ObservedEvidenceClasses: string list
    }

[<RequireQualifiedAccess>]
module CodexAppServerUsageTruth =
    let private digestPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let private canonical (terminal: CodexAppServerCorrelatedTerminal) =
        if isNull (box terminal)
           || isNull (box terminal.Reservation)
           || isNull (box terminal.Reservation.Request)
           || isNull (box terminal.Reservation.Request.Scope)
           || isNull (box terminal.Binding)
           || isNull (box terminal.Binding.Scope)
           || terminal.Binding.Scope <> terminal.Reservation.Request.Scope
           || not (DirectSessionTelemetryFacts.validScope terminal.Binding.Scope)
           || terminal.UsageUpdateCount < 0
           || terminal.UsageUpdateCount > CodexAppServerJournalRecovery.maxEntryCount - 2
           || isNull (box terminal.UsageWireSha256s)
           || List.length terminal.UsageWireSha256s <> terminal.UsageUpdateCount
           || (terminal.UsageWireSha256s |> List.exists (fun digest ->
               isNull digest || not (digestPattern.IsMatch digest)))
           || (terminal.UsageWireSha256s |> Set.ofList |> Set.count)
              <> terminal.UsageUpdateCount
           || isNull (box terminal.UsageSnapshots)
           || List.length terminal.UsageSnapshots <> terminal.UsageUpdateCount
           || (List.zip terminal.UsageWireSha256s terminal.UsageSnapshots
               |> List.exists (fun (digest, update) ->
                   isNull (box update)
                   || update.ThreadId <> terminal.Binding.Scope.ThreadId
                   || update.TurnId <> terminal.Binding.TurnId
                   || update.WireSha256 <> digest
                   || not (CodexAppServerUsageProjection.validCounts update.Last)
                   || not (CodexAppServerUsageProjection.validCounts update.Cumulative)
                   || not (CodexAppServerUsageProjection.dominates update.Cumulative update.Last)))
           || not (boundedText terminal.Reservation.Request.NativeSessionId)
           || isNull terminal.Reservation.Request.Challenge
           || not (digestPattern.IsMatch terminal.Reservation.Request.Challenge)
           || not (boundedText terminal.Reservation.ReservationId)
           || not (boundedText terminal.Reservation.Request.AuthorizedSourceAdapterId)
           || terminal.Reservation.Request.AuthorizedSourceAdapterId.Length > 128
           || terminal.Reservation.Request.IssuedAt.Offset <> TimeSpan.Zero
           || terminal.Reservation.Request.ExpiresAt.Offset <> TimeSpan.Zero
           || terminal.Reservation.Request.ExpiresAt <= terminal.Reservation.Request.IssuedAt
           || terminal.Reservation.Request.ExpiresAt - terminal.Reservation.Request.IssuedAt
              > TimeSpan.FromMinutes 5.
           || not (boundedText terminal.Binding.TurnId)
           || not (boundedText terminal.Binding.TransportIdentity)
           || not (boundedText terminal.Binding.ConnectionId)
           || terminal.Binding.ProtocolVersion <> "codex-app-server-v2/0.156.1"
           || isNull terminal.Binding.SubscriptionDigest
           || not (digestPattern.IsMatch terminal.Binding.SubscriptionDigest)
           || not (boundedText terminal.FirstEntryId)
           || not (boundedText terminal.SealedHeadEntryId)
           || terminal.FirstEntryId = terminal.SealedHeadEntryId
           || not (Set.contains terminal.TerminalStatus (set [ "completed"; "failed"; "interrupted" ])) then
            Error "app-server-usage-correlation-invalid"
        else
            Ok
                { Scope = terminal.Binding.Scope
                  NativeSessionId = terminal.Reservation.Request.NativeSessionId
                  WindowChallenge = terminal.Reservation.Request.Challenge
                  ReservationId = terminal.Reservation.ReservationId
                  AuthorizedSourceAdapterId = terminal.Reservation.Request.AuthorizedSourceAdapterId
                  IssuedAt = terminal.Reservation.Request.IssuedAt
                  ExpiresAt = terminal.Reservation.Request.ExpiresAt
                  NativeTurnId = terminal.Binding.TurnId
                  TransportIdentity = terminal.Binding.TransportIdentity
                  ConnectionId = terminal.Binding.ConnectionId
                  ProtocolVersion = terminal.Binding.ProtocolVersion
                  SubscriptionDigest = terminal.Binding.SubscriptionDigest
                  FirstEntryId = terminal.FirstEntryId
                  SealedHeadEntryId = terminal.SealedHeadEntryId
                  TerminalStatus = terminal.TerminalStatus }

    let private evidenceClass correlation usageDigests usageSnapshots candidate =
        match candidate with
        | ThreadSnapshot snapshot when
            isNull (box snapshot)
            || snapshot.ThreadId <> correlation.Scope.ThreadId
            || snapshot.TurnId <> correlation.NativeTurnId ->
            Error "app-server-usage-candidate-identity-mismatch"
        | ThreadSnapshot snapshot when
            not (CodexAppServerUsageProjection.validCounts snapshot.Last)
            || not (CodexAppServerUsageProjection.validCounts snapshot.Cumulative)
            || not (CodexAppServerUsageProjection.dominates snapshot.Cumulative snapshot.Last)
            || isNull snapshot.WireSha256
            || not (digestPattern.IsMatch snapshot.WireSha256) ->
            Error "app-server-usage-candidate-invalid"
        | ThreadSnapshot snapshot when
            not (Set.contains snapshot.WireSha256 usageDigests) ->
            Error "app-server-usage-snapshot-journal-mismatch"
        | ThreadSnapshot snapshot when
            not (List.contains snapshot usageSnapshots) ->
            Error "app-server-usage-snapshot-counters-mismatch"
        | ThreadSnapshot _ -> Ok "thread-last-total-snapshot"
        | ExecChildCompleted usage when
            isNull (box usage)
            || usage.ThreadId <> correlation.Scope.ThreadId
            || usage.TurnId <> Some correlation.NativeTurnId ->
            Error "app-server-usage-candidate-identity-mismatch"
        | ExecChildCompleted usage when not (DirectSessionTelemetryFacts.validUsage usage) ->
            Error "app-server-usage-candidate-invalid"
        | ExecChildCompleted _ -> Ok "exec-child-turn-completed"
        | UpstreamResponseCompleted(threadId, turnId, responseId, counts) when
            threadId <> correlation.Scope.ThreadId
            || turnId <> correlation.NativeTurnId
            || not (boundedText responseId)
            || isNull (box counts) ->
            Error "app-server-usage-candidate-identity-mismatch"
        | UpstreamResponseCompleted(_, _, _, counts) when
            not (CodexAppServerUsageProjection.validCounts counts) ->
            Error "app-server-usage-candidate-invalid"
        | UpstreamResponseCompleted _ -> Ok "one-upstream-response"

    /// Preserve canonical correlation while refusing to invent turn usage from snapshots,
    /// a separate exec child, or one internal upstream response completion.
    let assess (terminal: CodexAppServerCorrelatedTerminal)
        (candidates: CodexAppServerUsageCandidate list)
        : Result<CodexAppServerNoUsageVerdict, string> =
        match canonical terminal with
        | Error code -> Error code
        | Ok _ when isNull (box candidates) || List.length candidates > 10000 ->
            Error "app-server-usage-candidates-invalid"
        | Ok correlation ->
            let usageDigests = Set.ofList terminal.UsageWireSha256s
            let rec collect remaining classes seenSnapshotDigests snapshotCount =
                match remaining with
                | [] ->
                    Ok
                        { Correlation = correlation
                          Reason = "native-completed-turn-usage-not-established"
                          ObservedEvidenceClasses = Set.toList classes }
                | candidate :: tail when isNull (box candidate) ->
                    Error "app-server-usage-candidates-invalid"
                | ThreadSnapshot _ :: _ when snapshotCount >= terminal.UsageUpdateCount ->
                    Error "app-server-usage-snapshot-count-mismatch"
                | ThreadSnapshot snapshot :: _ when
                    Set.contains snapshot.WireSha256 seenSnapshotDigests ->
                    Error "app-server-usage-snapshot-duplicate"
                | candidate :: tail ->
                    match evidenceClass correlation usageDigests terminal.UsageSnapshots candidate with
                    | Error code -> Error code
                    | Ok evidence ->
                        match candidate with
                        | ThreadSnapshot snapshot ->
                            collect tail (Set.add evidence classes)
                                (Set.add snapshot.WireSha256 seenSnapshotDigests) (snapshotCount + 1)
                        | _ -> collect tail (Set.add evidence classes) seenSnapshotDigests snapshotCount
            collect candidates Set.empty Set.empty 0
