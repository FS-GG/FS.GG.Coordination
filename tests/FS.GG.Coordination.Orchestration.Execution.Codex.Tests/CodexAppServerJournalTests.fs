namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type private AtomicAppServerJournalFake() =
    let gate = obj ()
    let entries = ConcurrentDictionary<string, CodexAppServerJournalReceipt list>()
    member _.Entries = entries

    interface ICodexAppServerJournalStore with
        member _.TryAppend request =
            lock gate (fun () ->
                let digest = request.Binding.SubscriptionDigest
                let ordinal =
                    match request.Event with
                    | ObservedFrame(number, _, _, _, _)
                    | ObservedDisconnect number
                    | ObservedGap(number, _) -> number
                match entries.TryGetValue digest with
                | false, _ when request.PreviousEntryId.IsNone && ordinal = 1L ->
                    let receipt = { Append = request; EntryId = "entry-1" }
                    entries.[digest] <- [ receipt ]
                    Ok(JournalAppended receipt)
                | true, history when
                    let previous = List.last history
                    previous.Append.Binding = request.Binding
                    && request.PreviousEntryId = Some previous.EntryId
                    && (match previous.Append.Event with
                        | ObservedFrame(number, _, _, _, _) -> ordinal = number + 1L
                        | ObservedDisconnect _ | ObservedGap _ -> false) ->
                    let receipt = { Append = request; EntryId = sprintf "entry-%d" ordinal }
                    entries.[digest] <- history @ [ receipt ]
                    Ok(JournalAppended receipt)
                | _ -> Ok JournalAlreadyAdvanced)

type CodexAppServerJournalTests() =
    let scope: DirectSessionTurnScope =
        { WorkspaceId = "main-fsharp-dev"
          Repository = "FS-GG/.github"
          ItemId = "work-item-v1-" + String.replicate 64 "b"
          IssueRef = "FS-GG/.github#123"
          AttemptId = "attempt-1"
          InvocationId = "invocation-" + String.replicate 64 "a"
          ThreadId = "native-thread"
          SourceIdentity = "coordination"
          ProducerId = "fsharp-dev-main"
          BindingDigest = String.replicate 64 "c" }
    let binding =
        { Scope = scope
          TurnId = "native-turn"
          TransportIdentity = "authenticated-local-app-server"
          ConnectionId = "connection-1"
          SubscriptionDigest = String.replicate 64 "d"
          ProtocolVersion = "codex-app-server-v2/0.156.1" }
    let fixture name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", name)
        |> File.ReadAllBytes
    let started = fixture "turn-started.json"
    let usage = fixture "usage-updated.json"
    let completed = fixture "turn-completed.json"
    let beginFor expected bindingValue =
        let authenticator =
            { new ICodexAppServerSubscriptionAuthenticator with
                member _.ReadBoundSubscription() = Ok bindingValue }
        match CodexAppServerJournal.beginWindow expected bindingValue.TurnId
                  bindingValue.TransportIdentity authenticator with
        | Ok window -> window
        | Error code -> failwithf "unexpected binding refusal: %s" code
    let beginBound () = beginFor scope binding
    let frame ordinal payload =
        { TransportIdentity = binding.TransportIdentity
          ConnectionId = binding.ConnectionId
          Ordinal = ordinal
          Payload = payload }
    let status = CodexAppServerJournal.status

    [<Fact>]
    member _.``journal retains contiguous start usage terminal without creating usage fact``() =
        let fake = AtomicAppServerJournalFake()
        let store = fake :> ICodexAppServerJournalStore
        let first = CodexAppServerJournal.recordFrame store (beginBound ()) (frame 1L started)
        Assert.Equal(RecordedContinuity(InTurn 0), status first)
        let second = CodexAppServerJournal.recordFrame store first (frame 2L usage)
        Assert.Equal(RecordedContinuity(InTurn 1), status second)
        let third = CodexAppServerJournal.recordFrame store second (frame 3L completed)
        Assert.Equal(RecordedContinuity(TerminalObserved("completed", 1)), status third)
        let history = fake.Entries.[binding.SubscriptionDigest]
        Assert.Equal(3, history.Length)
        Assert.Equal(Some "entry-2", (List.last history).Append.PreviousEntryId)
        match history.Head.Append.Event with
        | ObservedFrame(_, _, _, encoded, _) -> Assert.Equal<byte>(started, Convert.FromBase64String encoded)
        | _ -> failwith "expected retained start frame"

    [<Fact>]
    member _.``atomic same-ordinal racers have one winner and replay is refused``() =
        let store = AtomicAppServerJournalFake() :> ICodexAppServerJournalStore
        let initial = beginBound ()
        let jobs =
            [| for _ in 1 .. 32 ->
                Task.Run(fun () -> CodexAppServerJournal.recordFrame store initial (frame 1L started)) |]
        let outcomes = Task.WhenAll(jobs).GetAwaiter().GetResult()
        Assert.Equal(1, outcomes |> Array.filter (fun value -> status value = RecordedContinuity(InTurn 0)) |> Array.length)
        Assert.Equal(31, outcomes |> Array.filter (fun value -> status value = JournalHalted "app-server-journal-already-advanced") |> Array.length)

    [<Fact>]
    member _.``same subscription digest cannot be reused by a foreign workspace or item``() =
        let store = AtomicAppServerJournalFake() :> ICodexAppServerJournalStore
        let first = CodexAppServerJournal.recordFrame store (beginBound ()) (frame 1L started)
        Assert.Equal(RecordedContinuity(InTurn 0), status first)
        let foreignScope = { scope with WorkspaceId = "other-workspace"; ItemId = "other-item" }
        let foreign = { binding with Scope = foreignScope }
        let other = CodexAppServerJournal.recordFrame store (beginFor foreignScope foreign) (frame 1L started)
        Assert.Equal(JournalHalted "app-server-journal-already-advanced", status other)

    [<Fact>]
    member _.``unknown or mismatched receipt halts before reducing a frame``() =
        let unknown =
            { new ICodexAppServerJournalStore with
                member _.TryAppend _ = Error "timeout-after-possible-commit" }
        let first = CodexAppServerJournal.recordFrame unknown (beginBound ()) (frame 1L started)
        Assert.Equal(JournalHalted "app-server-journal-outcome-unknown", status first)
        Assert.Equal(status first, status (CodexAppServerJournal.recordFrame unknown first (frame 1L started)))
        let inconsistent =
            { new ICodexAppServerJournalStore with
                member _.TryAppend request =
                    let wrong = { request with Binding = { binding with TurnId = "other-turn" } }
                    Ok(JournalAppended { Append = wrong; EntryId = "entry-1" }) }
        let second = CodexAppServerJournal.recordFrame inconsistent (beginBound ()) (frame 1L started)
        Assert.Equal(JournalHalted "app-server-journal-receipt-invalid", status second)

    [<Fact>]
    member _.``sequence gap is retained and a copied frame resists caller mutation``() =
        let fake = AtomicAppServerJournalFake()
        let store = fake :> ICodexAppServerJournalStore
        let wrong = CodexAppServerJournal.recordFrame store (beginBound ()) (frame 2L started)
        Assert.Equal(JournalHalted "app-server-journal-sequence-gap", status wrong)
        Assert.Equal(
            ObservedGap(1L, "app-server-journal-sequence-gap"),
            (List.last fake.Entries.[binding.SubscriptionDigest]).Append.Event
        )
        let stillHalted = CodexAppServerJournal.recordFrame store wrong (frame 1L started)
        Assert.Equal(status wrong, status stillHalted)
        let independent = AtomicAppServerJournalFake()
        let independentStore = independent :> ICodexAppServerJournalStore
        let mutablePayload = Array.copy started
        let frameValue = frame 1L mutablePayload
        let first = CodexAppServerJournal.recordFrame independentStore (beginBound ()) frameValue
        mutablePayload.[0] <- byte 'x'
        Assert.Equal(RecordedContinuity(InTurn 0), status first)
        let retained = (List.last independent.Entries.[binding.SubscriptionDigest]).Append.Event
        match retained with
        | ObservedFrame(_, _, _, encoded, _) -> Assert.Equal<byte>(started, Convert.FromBase64String encoded)
        | _ -> failwith "expected retained frame"

    [<Fact>]
    member _.``missing frame bytes are retained as a terminal gap``() =
        let fake = AtomicAppServerJournalFake()
        let store = fake :> ICodexAppServerJournalStore
        let invalid = { frame 1L started with Payload = null }
        let result = CodexAppServerJournal.recordFrame store (beginBound ()) invalid
        Assert.Equal(JournalHalted "app-server-journal-frame-invalid", status result)
        Assert.Equal(
            ObservedGap(1L, "app-server-journal-frame-invalid"),
            (List.last fake.Entries.[binding.SubscriptionDigest]).Append.Event
        )

    [<Fact>]
    member _.``disconnect is durably recorded as terminal gap before reduction``() =
        let fake = AtomicAppServerJournalFake()
        let store = fake :> ICodexAppServerJournalStore
        let first = CodexAppServerJournal.recordFrame store (beginBound ()) (frame 1L started)
        let lost = CodexAppServerJournal.recordDisconnect store first
        Assert.Equal(RecordedContinuity(ContinuityGap "app-server-continuity-disconnected"), status lost)
        Assert.Equal(ObservedDisconnect 2L, (List.last fake.Entries.[binding.SubscriptionDigest]).Append.Event)
        let replay = CodexAppServerJournal.recordFrame store lost (frame 3L usage)
        Assert.Equal(status lost, status replay)
        Assert.Equal("entry-2", (List.last fake.Entries.[binding.SubscriptionDigest]).EntryId)
