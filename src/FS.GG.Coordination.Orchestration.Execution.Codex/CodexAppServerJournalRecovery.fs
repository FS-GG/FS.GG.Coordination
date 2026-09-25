namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Security.Cryptography
open System.Text.RegularExpressions

/// A future durable store must issue this seal atomically after the writer closes.
/// A caller-supplied JSON snapshot or self-declared seal is not authenticated evidence.
type CodexAppServerJournalSeal =
    {
        Binding: CodexAppServerSubscriptionBinding
        EntryCount: int
        HeadEntryId: string
        SealId: string
    }

type CodexAppServerSealedJournalSnapshot =
    {
        Seal: CodexAppServerJournalSeal
        Entries: CodexAppServerJournalReceipt list
    }

/// Future authenticated, transactionally consistent read of a store-issued closed snapshot.
/// No production store, seal issuer, or recovery reader is installed.
type ICodexAppServerJournalRecoverySource =
    abstract member ReadSealedSnapshot:
        CodexAppServerSubscriptionBinding -> Result<CodexAppServerSealedJournalSnapshot, string>

/// Structural replay only; neither case is completed-turn usage or Host evidence.
type CodexAppServerRecoveryVerdict =
    | ProvisionalTerminal of status: string * usageUpdates: int * usageWireSha256s: string list
    | ProvisionalGap of code: string

[<RequireQualifiedAccess>]
module CodexAppServerJournalRecovery =
    let private shaPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let internal maxEntryCount = 10000

    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let private frameBytes (encoded: string) (digest: string) =
        if isNull encoded || encoded.Length = 0 || encoded.Length > 87384
           || isNull digest || not (shaPattern.IsMatch digest) then
            Error "app-server-recovery-frame-invalid"
        else
            try
                let bytes = Convert.FromBase64String encoded
                if bytes.Length = 0 || bytes.Length > 65536
                   || Convert.ToBase64String bytes <> encoded then
                    Error "app-server-recovery-frame-invalid"
                else
                    let actual =
                        SHA256.HashData bytes
                        |> Convert.ToHexString
                        |> fun value -> value.ToLowerInvariant()
                    if actual <> digest then Error "app-server-recovery-frame-digest-mismatch"
                    else Ok bytes
            with :? FormatException ->
                Error "app-server-recovery-frame-invalid"

    let private eventOrdinal event =
        match event with
        | ObservedFrame(ordinal, _, _, _, _)
        | ObservedDisconnect ordinal
        | ObservedGap(ordinal, _) -> ordinal

    let private replayEntry continuity event =
        match event with
        | ObservedFrame(ordinal, transport, connection, encoded, digest) ->
            match frameBytes encoded digest with
            | Error code -> Error code
            | Ok bytes ->
                let frame =
                    { TransportIdentity = transport
                      ConnectionId = connection
                      Ordinal = ordinal
                      Payload = bytes }
                Ok(Choice1Of2(CodexAppServerContinuity.apply continuity frame))
        | ObservedDisconnect _ ->
            match CodexAppServerContinuity.status continuity with
            | TerminalObserved _ -> Error "app-server-recovery-disconnect-after-terminal"
            | _ -> Ok(Choice1Of2(CodexAppServerContinuity.disconnected continuity))
        | ObservedGap(_, code) when boundedText code -> Ok(Choice2Of2 code)
        | ObservedGap _ -> Error "app-server-recovery-gap-invalid"

    /// Verify a complete chain against the separately authenticated subscription binding.
    /// The source port must authenticate the seal and completeness; this function cannot do so.
    let recover expectedScope expectedTurn expectedTransport
        (authenticator: ICodexAppServerSubscriptionAuthenticator)
        (source: ICodexAppServerJournalRecoverySource)
        : Result<CodexAppServerRecoveryVerdict, string> =
        if isNull (box source) then Error "app-server-recovery-source-missing"
        else
            match CodexAppServerContinuity.beginWindow
                      expectedScope expectedTurn expectedTransport authenticator with
            | Error code -> Error code
            | Ok initial ->
                let binding = CodexAppServerContinuity.subscriptionBinding initial
                let snapshotResult =
                    try source.ReadSealedSnapshot binding
                    with _ -> Error "source-threw"
                match snapshotResult with
                | Error _ -> Error "app-server-recovery-source-unavailable"
                | Ok snapshot when
                    isNull (box snapshot)
                    || isNull (box snapshot.Seal)
                    || isNull (box snapshot.Entries)
                    || snapshot.Seal.Binding <> binding
                    || snapshot.Seal.EntryCount < 1
                    || snapshot.Seal.EntryCount > maxEntryCount
                    || not (boundedText snapshot.Seal.HeadEntryId)
                    || not (boundedText snapshot.Seal.SealId) ->
                    Error "app-server-recovery-seal-invalid"
                | Ok snapshot when List.length snapshot.Entries <> snapshot.Seal.EntryCount ->
                    Error "app-server-recovery-count-mismatch"
                | Ok snapshot ->
                    let rec replay entries expectedOrdinal priorId seen current usageDigests =
                        match entries with
                        | [] ->
                            if priorId <> Some snapshot.Seal.HeadEntryId then
                                Error "app-server-recovery-head-mismatch"
                            else
                                match current with
                                | Choice2Of2 code -> Ok(ProvisionalGap code)
                                | Choice1Of2 continuity ->
                                    match CodexAppServerContinuity.status continuity with
                                    | TerminalObserved(terminal, count) when count = List.length usageDigests ->
                                        Ok(ProvisionalTerminal(terminal, count, List.rev usageDigests))
                                    | TerminalObserved _ -> Error "app-server-recovery-usage-count-mismatch"
                                    | ContinuityGap code -> Ok(ProvisionalGap code)
                                    | _ -> Error "app-server-recovery-terminal-missing"
                        | receipt :: tail when
                            isNull (box receipt)
                            || isNull (box receipt.Append)
                            || receipt.Append.Binding <> binding
                            || receipt.Append.PreviousEntryId <> priorId
                            || not (boundedText receipt.EntryId)
                            || Set.contains receipt.EntryId seen
                            || isNull (box receipt.Append.Event)
                            || eventOrdinal receipt.Append.Event <> expectedOrdinal ->
                            Error "app-server-recovery-chain-invalid"
                        | receipt :: tail ->
                            match current with
                            | Choice2Of2 _ -> Error "app-server-recovery-after-gap"
                            | Choice1Of2 continuity when
                                (match CodexAppServerContinuity.status continuity with
                                 | ContinuityGap _ -> true
                                 | _ -> false) ->
                                Error "app-server-recovery-after-gap"
                            | Choice1Of2 continuity ->
                                match replayEntry continuity receipt.Append.Event with
                                | Error code -> Error code
                                | Ok next ->
                                    let nextUsageDigests =
                                        match receipt.Append.Event, CodexAppServerContinuity.status continuity, next with
                                        | ObservedFrame(_, _, _, _, digest), InTurn before, Choice1Of2 updated ->
                                            match CodexAppServerContinuity.status updated with
                                            | InTurn after when after = before + 1 -> digest :: usageDigests
                                            | _ -> usageDigests
                                        | _ -> usageDigests
                                    replay tail (expectedOrdinal + 1L) (Some receipt.EntryId)
                                        (Set.add receipt.EntryId seen) next nextUsageDigests
                    replay snapshot.Entries 1L None Set.empty (Choice1Of2 initial) []
