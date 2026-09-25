namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System

/// Structural correlation of a prospective first start with a sealed journal terminal.
/// The usage update count is event metadata, not completed-turn token usage.
type CodexAppServerCorrelatedTerminal =
    {
        Reservation: DirectSessionChallengeReservationReceipt
        Binding: CodexAppServerSubscriptionBinding
        FirstEntryId: string
        SealedHeadEntryId: string
        TerminalStatus: string
        UsageUpdateCount: int
        UsageWireSha256s: string list
        UsageSnapshots: CodexAppServerUsageUpdate list
    }

[<RequireQualifiedAccess>]
module CodexAppServerCorrelatedTerminal =
    /// Both sources are future trusted ports. A supplied snapshot or receipt cannot prove custody.
    /// A confirmed challenge remains burned if the sealed read or replay fails.
    let correlate
        (expectedScope: DirectSessionTurnScope)
        (expectedSourceAdapterId: string)
        (expectedTransport: string)
        (clock: IDirectSessionWindowClock)
        (issuer: IDirectSessionProspectiveWindowIssuer)
        (subscriptionSource: ICodexAppServerCurrentSubscriptionSource)
        (store: IDirectSessionChallengeReservationStore)
        (firstStartSource: ICodexAppServerFirstStartSource)
        (sealedSource: ICodexAppServerJournalRecoverySource)
        : Result<CodexAppServerCorrelatedTerminal, DirectSessionReservationFailure> =
        if isNull (box sealedSource) then
            Error(Rejected "app-server-correlated-sealed-source-missing")
        else
            match CodexAppServerFirstStart.bind expectedScope expectedSourceAdapterId
                      expectedTransport clock issuer subscriptionSource store firstStartSource with
            | Error failure -> Error failure
            | Ok started ->
                let gap code = Error(ReservedGap(started.Reservation, code))
                let snapshotResult =
                    try sealedSource.ReadSealedSnapshot started.Binding
                    with _ -> Error "source-threw"
                match snapshotResult with
                | Error _ -> gap "app-server-correlated-sealed-source-unavailable"
                | Ok snapshot when
                    isNull (box snapshot)
                    || isNull (box snapshot.Entries)
                    || List.isEmpty snapshot.Entries ->
                    gap "app-server-correlated-snapshot-invalid"
                | Ok snapshot when List.head snapshot.Entries <> started.StartReceipt ->
                    gap "app-server-correlated-first-receipt-mismatch"
                | Ok snapshot ->
                    let authenticator =
                        { new ICodexAppServerSubscriptionAuthenticator with
                            member _.ReadBoundSubscription() = Ok started.Binding }
                    let cachedSource =
                        { new ICodexAppServerJournalRecoverySource with
                            member _.ReadSealedSnapshot _ = Ok snapshot }
                    match CodexAppServerJournalRecovery.recover
                              expectedScope started.Binding.TurnId expectedTransport
                              authenticator cachedSource with
                    | Error code -> gap code
                    | Ok(ProvisionalGap code) -> gap code
                    | Ok(ProvisionalTerminal(terminal, count, usageDigests, usageSnapshots)) ->
                        Ok
                            { Reservation = started.Reservation
                              Binding = started.Binding
                              FirstEntryId = started.StartReceipt.EntryId
                              SealedHeadEntryId = snapshot.Seal.HeadEntryId
                              TerminalStatus = terminal
                              UsageUpdateCount = count
                              UsageWireSha256s = usageDigests
                              UsageSnapshots = usageSnapshots }
