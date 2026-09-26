namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Security.Cryptography

/// Immutable bytes and source facts to retain before reducing a notification.
type CodexAppServerJournalEvent =
    | ObservedFrame of ordinal: int64 * transportIdentity: string * connectionId: string * wireBase64: string * wireSha256: string
    | ObservedDisconnect of ordinal: int64
    | ObservedGap of ordinal: int64 * code: string

type CodexAppServerJournalAppend =
    {
        Binding: CodexAppServerSubscriptionBinding
        PreviousEntryId: string option
        Event: CodexAppServerJournalEvent
    }

type CodexAppServerJournalReceipt =
    {
        Append: CodexAppServerJournalAppend
        EntryId: string
    }

type CodexAppServerJournalAppendOutcome =
    | JournalAppended of CodexAppServerJournalReceipt
    | JournalAlreadyAdvanced

/// Future durable CAS port. SubscriptionDigest must be globally unique across binding facts.
/// Append must atomically check the prior entry and next ordinal and retain the exact request.
/// Error means the effect is unknown; no production implementation or recovery reader exists.
type ICodexAppServerJournalStore =
    abstract member TryAppend: CodexAppServerJournalAppend -> Result<CodexAppServerJournalAppendOutcome, string>

type CodexAppServerJournalStatus =
    | RecordedContinuity of CodexAppServerContinuityStatus
    | JournalHalted of string

type CodexAppServerJournalWindow =
    private
        {
            Continuity: CodexAppServerContinuityState
            LastEntryId: string option
            SeenEntryIds: Set<string>
            NextOrdinal: int64
            Halt: string option
        }

[<RequireQualifiedAccess>]
module CodexAppServerJournal =
    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let beginWindow expectedScope expectedTurn expectedTransport authenticator =
        CodexAppServerContinuity.beginWindow expectedScope expectedTurn expectedTransport authenticator
        |> Result.map (fun continuity ->
            { Continuity = continuity
              LastEntryId = None
              SeenEntryIds = Set.empty
              NextOrdinal = 1L
              Halt = None })

    let status state =
        match state.Halt with
        | Some code -> JournalHalted code
        | None -> RecordedContinuity(CodexAppServerContinuity.status state.Continuity)

    let private halted code state = { state with Halt = Some code }

    let private append (store: ICodexAppServerJournalStore)
        (state: CodexAppServerJournalWindow) event applyAccepted =
        let request =
            { Binding = CodexAppServerContinuity.subscriptionBinding state.Continuity
              PreviousEntryId = state.LastEntryId
              Event = event }
        let result =
            try store.TryAppend request
            with _ -> Error "store-threw"
        match result with
        | Error _ -> halted "app-server-journal-outcome-unknown" state
        | Ok JournalAlreadyAdvanced -> halted "app-server-journal-already-advanced" state
        | Ok (JournalAppended receipt) when
            isNull (box receipt)
            || isNull (box receipt.Append)
            || receipt.Append <> request
            || not (boundedText receipt.EntryId)
            || Set.contains receipt.EntryId state.SeenEntryIds ->
            halted "app-server-journal-receipt-invalid" state
        | Ok (JournalAppended receipt) ->
            { state with
                Continuity = applyAccepted state.Continuity
                LastEntryId = Some receipt.EntryId
                SeenEntryIds = Set.add receipt.EntryId state.SeenEntryIds
                NextOrdinal = state.NextOrdinal + 1L }

    let private recordGap store state code =
        let retained = append store state (ObservedGap(state.NextOrdinal, code)) id
        match retained.Halt with
        | Some _ -> retained
        | None -> halted code retained

    /// Retain exact immutable bytes before reduction. An unknown store effect halts the window.
    let recordFrame (store: ICodexAppServerJournalStore) state (frame: CodexAppServerObservedFrame) =
        match state.Halt, CodexAppServerContinuity.status state.Continuity with
        | Some _, _ | _, ContinuityGap _ -> state
        | _ when isNull (box store) -> halted "app-server-journal-store-missing" state
        | _ when state.NextOrdinal = Int64.MaxValue ->
            halted "app-server-journal-sequence-exhausted" state
        | _ when isNull (box frame) || isNull frame.Payload || frame.Payload.Length = 0 || frame.Payload.Length > 65536 ->
            recordGap store state "app-server-journal-frame-invalid"
        | _ when frame.Ordinal <> state.NextOrdinal ->
            recordGap store state "app-server-journal-sequence-gap"
        | _ ->
            let immutableBytes = Array.copy frame.Payload
            let sha256 = SHA256.HashData immutableBytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
            let event =
                ObservedFrame(frame.Ordinal, frame.TransportIdentity, frame.ConnectionId,
                              Convert.ToBase64String immutableBytes, sha256)
            let copiedFrame = { frame with Payload = immutableBytes }
            append store state event (fun continuity -> CodexAppServerContinuity.apply continuity copiedFrame)

    /// Retain transport loss as a terminal gap. A failed append still halts the window.
    let recordDisconnect (store: ICodexAppServerJournalStore) state =
        match state.Halt, CodexAppServerContinuity.status state.Continuity with
        | Some _, _ | _, ContinuityGap _ | _, TerminalObserved _ -> state
        | _ when isNull (box store) -> halted "app-server-journal-store-missing" state
        | _ when state.NextOrdinal = Int64.MaxValue ->
            halted "app-server-journal-sequence-exhausted" state
        | _ ->
            append store state (ObservedDisconnect state.NextOrdinal) CodexAppServerContinuity.disconnected
