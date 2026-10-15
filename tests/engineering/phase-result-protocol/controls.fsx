#load "../../../src/FS.GG.Coordination.Orchestration.Execution/FiniteCustodyComposition.fs"
// Pure in-memory protocol controls; supply the compiled production assembly with --reference.
// No process, descriptor, network, credential or real persistence operation is exercised.
open System
open System.Buffers.Binary
open System.Text
open System.Threading.Tasks
open FS.GG.Execution.Contracts
open FS.GG.Coordination.Orchestration.Execution

let require name condition = if not condition then failwith name else printfn "passed:%s" name
let accepted = function Ok value -> value | Error reason -> failwithf "%A" reason
let rejected = function Error _ -> true | Ok _ -> false
let binding = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes "{\"fixture\":true}")
let digest = PhaseResultProtocol.bindingDigest binding |> accepted
let domain = { Id = Guid.Parse "7c287080-68e9-4846-b369-cb46f37ec374"; Frequency = 1000L }
let at ticks = { Domain = domain; Ticks = ticks }
let original = Budget.create (at 1000L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> accepted
let window = FiniteCustodyWindow.prepare (at 1000L) original |> accepted
let framed (body: string) =
    let body = Encoding.ASCII.GetBytes body
    let result = Array.zeroCreate<byte> (body.Length + 4)
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), uint32 body.Length)
    body.CopyTo(result, 4)
    ReadOnlyMemory<byte>(result)
let child n kind payload =
    framed (sprintf "{\"b\":\"%s\",\"k\":\"%s\",\"n\":%d,\"p\":%s,\"v\":\"%s\"}" digest kind n payload PhaseResultProtocol.identifier)
let ready = child 1 "ready" "{\"binding\":{\"fixture\":true}}"
let fresh () =
    let session = PhaseResultProtocol.Session binding
    session.StartFrame() |> ignore
    session
let receive (session: PhaseResultProtocol.Session) input = session.Feed input |> accepted |> List.exactlyOne
let validate (_: PhaseResultSnapshot) = Ok () // Domain verification is separately owned.
let readback (snapshot: PhaseResultSnapshot) =
    Task.FromResult(Ok { Binding = snapshot.Binding; Ordinal = snapshot.Ordinal; FrameDigest = snapshot.Digest })
let retain (session:PhaseResultProtocol.Session) (snapshot: PhaseResultSnapshot) =
    let gate = session.BeginRetention(snapshot,readback) |> accepted
    gate.Completion.GetAwaiter().GetResult() |> accepted |> ignore
    gate
let acceptReady (session: PhaseResultProtocol.Session) =
    let snapshot = receive session ready
    require "ready-requires-retention" (session.AcceptChild(snapshot, validate) |> accepted)
    snapshot

let session = fresh ()
for n, kind, payload in
    [1,"ready","{\"binding\":{\"fixture\":true}}"; 2,"issuance-possible","{}"; 3,"token-owned","{}"
     4,"revocation-possible","{}"; 5,"revoked","{}"; 6,"terminal","{}"] do
    let input = child n kind payload
    // Exercise one-byte fragments through the actual production decoder.
    let mutable snapshots = []
    for index = 0 to input.Length - 1 do
        snapshots <- snapshots @ (session.Feed(input.Slice(index,1)) |> accepted)
    let snapshot = List.exactlyOne snapshots
    let needsAck = session.AcceptChild(snapshot, validate) |> accepted
    if needsAck then
        let ack = session.TakeAck(at 2000L, window, retain session snapshot) |> accepted
        require ("bounded-digest-ack-" + kind) (ack.Length <= 512 && Encoding.ASCII.GetString(ack.Span).Contains snapshot.Digest)
        session.ConfirmAckSent(at 2000L,window,snapshot) |> accepted |> ignore
require "complete-six-frame-session" (session.Accepted.Length = 6 && session.FinishInput() = Ok ())
require "both-directions-charged" (session.ChargedBytes > int64 (6 * ready.Length) && session.ObservedBytes = int64 session.ChargedBytes)
require "extra-after-terminal-refuses" (session.Feed(child 6 "terminal" "{}") |> rejected)

let duplicate = fresh ()
let dupBody = sprintf "{\"b\":\"%s\",\"k\":\"ready\",\"k\":\"ready\",\"n\":1,\"p\":{},\"v\":\"%s\"}" digest PhaseResultProtocol.identifier
require "duplicate-key-refuses" (duplicate.Feed(framed dupBody) |> rejected)
let noncanonical = fresh ()
require "noncanonical-whitespace-refuses" (noncanonical.Feed(framed "{ \"a\":1}") |> rejected)
let oversized = fresh ()
let prefix = [| 0uy; 0uy; 8uy; 0uy |]
require "oversized-prefix-refuses-before-body" (oversized.Feed(ReadOnlyMemory<byte> prefix) |> rejected)
let partial = fresh ()
partial.Feed(ready.Slice(0,3)) |> accepted |> ignore
require "partial-eof-refuses-and-charges" ((partial.FinishInput() |> rejected) && partial.ObservedBytes > 3L)

let mismatch = fresh ()
let mismatchSnapshot = acceptReady mismatch
let badGate = mismatch.BeginRetention(mismatchSnapshot, (fun s -> Task.FromResult(Ok {Binding=s.Binding;Ordinal=s.Ordinal;FrameDigest=String.replicate 64 "0"}))) |> accepted
badGate.Completion.GetAwaiter().GetResult() |> accepted |> ignore
require "mismatched-readback-prevents-ack" (mismatch.TakeAck(at 2000L,window,badGate) |> rejected)
require "retained-facts-survive-readback-failure" (mismatch.Accepted.Length=1)

let expired = fresh ()
let expiredSnapshot = acceptReady expired
require "original-normal-cutoff-prevents-ready-ack" (expired.TakeAck(at 271000L,window,retain expired expiredSnapshot) |> rejected)
let failed = fresh ()
let failedSnapshot = receive failed ready
require "domain-refusal-prevents-acceptance" (failed.AcceptChild(failedSnapshot,fun _ -> Error "synthetic-domain-failure") |> rejected)
require "domain-refusal-retains-no-unvalidated-facts" failed.Accepted.IsEmpty

let replay = fresh ()
let replaySnapshot = acceptReady replay
let replayGate = retain replay replaySnapshot
replay.TakeAck(at 2000L,window,replayGate) |> accepted |> ignore
require "ack-retransmission-refuses" (replay.TakeAck(at 2000L,window,replayGate) |> rejected)
let foreign = fresh ()
require "foreign-session-snapshot-refuses" (foreign.AcceptChild(replaySnapshot,validate) |> rejected)

let blocked = fresh ()
let blockedSnapshot = acceptReady blocked
let pending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
let blockedGate = blocked.BeginRetention(blockedSnapshot, (fun _ -> pending.Task)) |> accepted
require "outstanding-writer-is-accounted" (blocked.LocalTasks = Outstanding)
require "outstanding-writer-cannot-enable-ack" (blocked.TakeAck(at 271000L,window,blockedGate) |> rejected)
pending.SetResult(Error "synthetic-writer-stop")
blockedGate.Completion.GetAwaiter().GetResult() |> ignore
require "writer-task-settled-independently" (blockedGate.Completion.IsCompleted && blocked.LocalTasks = Settled && blocked.RetentionTasks.Length = 1)

let aggregate = fresh ()
let startBytes = aggregate.ChargedBytes
let flood = Array.zeroCreate<byte> 16385
require "aggregate-refuses-before-frame-processing" (aggregate.Feed(ReadOnlyMemory<byte> flood) |> rejected)
require "rejected-overflow-bytes-remain-observed" (aggregate.ObservedBytes = startBytes + 16385L && aggregate.ChargedBytes = aggregate.ObservedBytes)

let negative = fresh ()
require "negative-ordinal-refuses" (negative.Feed(child -1 "ready" "{}") |> rejected)
let skipped = fresh ()
let skippedSnapshot = receive skipped (child 2 "ready" "{\"binding\":{\"fixture\":true}}")
require "skipped-ordinal-refuses" (skipped.AcceptChild(skippedSnapshot,validate) |> rejected)
let unknown = fresh ()
let unknownBody = sprintf "{\"b\":\"%s\",\"extra\":0,\"k\":\"ready\",\"n\":1,\"p\":{},\"v\":\"%s\"}" digest PhaseResultProtocol.identifier
require "unknown-envelope-key-refuses" (unknown.Feed(framed unknownBody) |> rejected)

let throwing = fresh ()
let throwingSnapshot = acceptReady throwing
let throwingGate = throwing.BeginRetention(throwingSnapshot,fun _ -> failwith "synthetic-secret-exception-never-reported") |> accepted
throwingGate.Completion.GetAwaiter().GetResult() |> ignore
require "writer-exception-prevents-ack" (throwing.TakeAck(at 2000L,window,throwingGate) |> rejected)
require "writer-exception-task-is-settled" (throwing.LocalTasks=Settled)

let duplicateRetention = fresh ()
let retentionSnapshot = acceptReady duplicateRetention
let retentionGate = retain duplicateRetention retentionSnapshot
require "second-retention-attempt-refuses" (duplicateRetention.BeginRetention(retentionSnapshot,readback) |> rejected)
require "second-retention-does-not-launch-another-task" (duplicateRetention.RetentionTasks.Length=1)
require "retention-order-refusal-prevents-original-ack" (duplicateRetention.TakeAck(at 2000L,window,retentionGate) |> rejected)

let maximum = fresh ()
let maxReady = acceptReady maximum
maximum.TakeAck(at 2000L,window,retain maximum maxReady) |> accepted |> ignore
maximum.ConfirmAckSent(at 2000L,window,maxReady) |> accepted |> ignore
let emptyPad = child 2 "issuance-possible" "{\"pad\":[\"\",\"\"]}"
let padding = 2048 - emptyPad.Length
let payload = "{\"pad\":[\"" + String.replicate (padding/2) "x" + "\",\"" + String.replicate (padding-padding/2) "x" + "\"]}"
let maxFrame = child 2 "issuance-possible" payload
require "exact-2048-byte-frame-decodes" (maxFrame.Length=2048 && (maximum.Feed maxFrame |> accepted).Length=1)

let earlyTerminal = fresh ()
let earlySnapshot = acceptReady earlyTerminal
let earlyPending = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
let earlyGate = earlyTerminal.BeginRetention(earlySnapshot,fun _ -> earlyPending.Task) |> accepted
let terminalSnapshot = receive earlyTerminal (child 2 "terminal" "{}")
require "terminal-before-pending-ack-is-retained" (earlyTerminal.AcceptChild(terminalSnapshot,validate)=Ok false && earlyTerminal.Accepted.Length=2)
require "terminal-before-ack-keeps-writer-outstanding" (earlyTerminal.LocalTasks=Outstanding)
earlyPending.SetResult(Ok {Binding=earlySnapshot.Binding;Ordinal=earlySnapshot.Ordinal;FrameDigest=earlySnapshot.Digest})
earlyGate.Completion.GetAwaiter().GetResult() |> accepted |> ignore
require "late-writer-cannot-revive-terminal-ack" (earlyTerminal.TakeAck(at 2000L,window,earlyGate) |> rejected)
require "terminal-before-ack-cannot-qualify-session" (earlyTerminal.FinishInput() |> rejected)
require "no-effect-phase-after-early-terminal" (earlyTerminal.Feed(child 3 "issuance-possible" "{}") |> rejected)

let concatenate (frames:ReadOnlyMemory<byte> list) =
    frames |> List.collect (fun frame -> List.ofArray(frame.ToArray())) |> List.toArray |> ReadOnlyMemory<byte>
let coalesced = fresh ()
require "coalesced-before-ready-ack-refuses" (coalesced.Feed(concatenate [ready;child 2 "issuance-possible" "{}";child 3 "token-owned" "{}";child 4 "revocation-possible" "{}";child 5 "revoked" "{}";child 6 "terminal" "{}"]) |> rejected)

let earlySuccessor = fresh ()
let earlyReady = acceptReady earlySuccessor
let earlyReadyGate = retain earlySuccessor earlyReady
earlySuccessor.TakeAck(at 2000L,window,earlyReadyGate) |> accepted |> ignore
require "ack-bytes-alone-do-not-open-ingress" (earlySuccessor.Feed(child 2 "issuance-possible" "{}") |> rejected)

let fragmented = fresh ()
let fragmentReady = acceptReady fragmented
fragmented.Feed((child 2 "issuance-possible" "{}").Slice(0,1)) |> accepted |> ignore
require "partial-pre-ack-prefix-prevents-ack" (fragmented.TakeAck(at 2000L,window,retain fragmented fragmentReady) |> rejected)
require "partial-pre-ack-prefix-cannot-be-laundered" (fragmented.Feed((child 2 "issuance-possible" "{}").Slice(1)) |> rejected)

let terminalBeforeReduction = fresh ()
let reduceReady = acceptReady terminalBeforeReduction
let reduceGate = retain terminalBeforeReduction reduceReady
let reduceTerminal = receive terminalBeforeReduction (child 2 "terminal" "{}")
require "decoded-terminal-immediately-fences-ack" (terminalBeforeReduction.TakeAck(at 2000L,window,reduceGate) |> rejected)
require "terminal-remains-retainable-after-ack-refusal" (terminalBeforeReduction.AcceptChild(reduceTerminal,validate)=Ok false && terminalBeforeReduction.Accepted.Length=2)

let leftover = fresh ()
let leftoverReady = acceptReady leftover
leftover.TakeAck(at 2000L,window,retain leftover leftoverReady) |> accepted |> ignore
leftover.ConfirmAckSent(at 2000L,window,leftoverReady) |> accepted |> ignore
let leftoverTerminal = receive leftover (child 2 "terminal" "{}")
require "unconsumed-decoded-terminal-blocks-finish" (leftover.FinishInput() |> rejected)
require "refused-close-keeps-terminal-available" (leftover.AcceptChild(leftoverTerminal,validate)=Ok false)

let legalCoalescing = fresh ()
for n,kind,payload in [1,"ready","{\"binding\":{\"fixture\":true}}";2,"issuance-possible","{}"] do
    let snapshot = receive legalCoalescing (child n kind payload)
    legalCoalescing.AcceptChild(snapshot,validate) |> accepted |> ignore
    legalCoalescing.TakeAck(at 2000L,window,retain legalCoalescing snapshot) |> accepted |> ignore
    legalCoalescing.ConfirmAckSent(at 2000L,window,snapshot) |> accepted |> ignore
let middleFrames = legalCoalescing.Feed(concatenate [child 3 "token-owned" "{}";child 4 "revocation-possible" "{}"]) |> accepted
for snapshot in middleFrames do
    if legalCoalescing.AcceptChild(snapshot,validate) |> accepted then
        legalCoalescing.TakeAck(at 2000L,window,retain legalCoalescing snapshot) |> accepted |> ignore
        legalCoalescing.ConfirmAckSent(at 2000L,window,snapshot) |> accepted |> ignore
let finalFrames = legalCoalescing.Feed(concatenate [child 5 "revoked" "{}";child 6 "terminal" "{}"]) |> accepted
for snapshot in finalFrames do legalCoalescing.AcceptChild(snapshot,validate) |> accepted |> ignore
require "legal-ungated-coalescing-completes" (legalCoalescing.FinishInput()=Ok () && legalCoalescing.Accepted.Length=6)


// The exact internal composition source is loaded above. No assembly-wide friend
// grant is needed, and these fixtures remain CLR work under the admitted profile.
module CompositionControls =
    open System.Threading
    open System.Threading.Channels
    open FiniteCustodyComposition

    let descriptor =
        { Mode = "inert"
          Identity = { InvocationId = Guid.Parse "a4305dba-ce1b-4938-8804-b8d1a7189fb8"
                       Execution = None; LaunchOrdinal = 0UL
                       SourceDigest = String.replicate 64 "a"
                       ProfileId = FiniteCustodyWindow.profileId; ProfileDigest = String.replicate 64 "b" }
          OriginalOperation = "utel-rel-21"; OriginalGeneration = 2L
          DomainProfile = "utel-rel-21/original-tag-authentication-only/2"
          Process = { Platform = "inert"; ScopeId = "fixture"; Pid = 1; BirthToken = "abc" }
          ChildRevision = String.replicate 40 "c"; RunnerRevision = String.replicate 40 "d"
          RunnerClosure = String.replicate 64 "e"; ContractDigest = String.replicate 64 "f"
          RuntimeClosure = String.replicate 64 "0"
          WorkflowRepository = 1269292704UL; WorkflowPath = ".github/workflows/release-successor-publish.yml"
          WorkflowRevision = String.replicate 40 "1"; WorkflowRun = 0UL; WorkflowAttempt = 0u
          App = 4882140UL; Installation = 123UL
          ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]
          ResultDestination = "inert-result-slot" }

    let bindingText =
        sprintf """{"app":{"id":4882140,"installation":123},"clock":{"domain":"%O","frequency":"1000","normal":"271000","start":"1000","total":"301000","work":"286000"},"invocation":"%O","mode":"inert","operation":"utel-rel-21","originalGeneration":2,"process":{"birth":"abc","pid":1},"profile":"%s","source":{"childClosure":"%s","childRevision":"%s","contract":"%s","runnerClosure":"%s","runnerRevision":"%s"},"workflow":{"attempt":0,"path":".github/workflows/release-successor-publish.yml","repositoryId":1269292704,"revision":"%s","run":0}}"""
            domain.Id descriptor.Identity.InvocationId descriptor.DomainProfile descriptor.Identity.SourceDigest
            descriptor.ChildRevision descriptor.ContractDigest descriptor.RunnerClosure descriptor.RunnerRevision descriptor.WorkflowRevision
    let wireBinding = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes bindingText)
    let wireDigest = PhaseResultProtocol.bindingDigest wireBinding |> accepted
    let child ordinal kind payload =
        framed (sprintf "{\"b\":\"%s\",\"k\":\"%s\",\"n\":%d,\"p\":%s,\"v\":\"%s\"}" wireDigest kind ordinal payload PhaseResultProtocol.identifier)
    let wait (work: Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
    let waitUntil predicate = task {
        let limit = DateTime.UtcNow + TimeSpan.FromSeconds 2.
        while not (predicate ()) && DateTime.UtcNow < limit do do! Task.Delay 1
        require "composed-fixture-progress" (predicate ()) }

    type Fixture() =
        let mutable ticks = 1000L
        let mutable retirements = 0
        let input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>()
        let writes = ResizeArray<byte array>()
        let termination = TaskCompletionSource<bool option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable persist = readback
        let mutable writeCount = fun (bytes: ReadOnlyMemory<byte>) -> bytes.Length
        let clock = { new IMonotonicClock with member _.Now() = at (Interlocked.Read(&ticks)) }
        let ports =
            { Read = fun () -> task {
                  let! available = input.Reader.WaitToReadAsync().AsTask()
                  if available then return! input.Reader.ReadAsync().AsTask()
                  else return ReadOnlyMemory<byte>.Empty }
              Write = fun bytes -> task {
                  lock writes (fun () -> writes.Add(bytes.ToArray()))
                  return writeCount bytes }
              Validate = fun _ -> Ok ()
              PersistAndReadback = fun snapshot -> persist snapshot
              RequestRetirement = fun () ->
                  Interlocked.Increment(&retirements) |> ignore
                  Task.FromResult true
              Termination = termination.Task }
        member _.Prepare(admission, expected, actual) =
            Session.Prepare(admission, expected, actual, clock, original, wireBinding, ports, CancellationToken.None)
        member _.Send bytes = require "composed-fixture-enqueue" (input.Writer.TryWrite bytes)
        member _.Eof() = input.Writer.TryComplete() |> ignore
        member _.Terminate() = termination.TrySetResult(Some true) |> ignore
        member _.Expire() = Interlocked.Exchange(&ticks,301000L) |> ignore
        member _.WorkExpires() = Interlocked.Exchange(&ticks,286000L) |> ignore
        member _.Persist with set callback = persist <- callback
        member _.WriteCount with set callback = writeCount <- callback
        member _.Writes = lock writes (fun () -> writes.Count)
        member _.Retirements = Interlocked.CompareExchange(&retirements,0,0)

    let admissionFixture = Fixture()
    let admission = Admission()
    require "composed-native-refused-before-channel-work"
        (admissionFixture.Prepare(admission,descriptor,{descriptor with Mode="native"}) |> rejected)
    require "composed-wrong-source-refused-before-channel-work"
        (admissionFixture.Prepare(admission,descriptor,{descriptor with Identity={descriptor.Identity with SourceDigest=String.replicate 64 "0"}}) |> rejected)
    require "composed-unsupported-runner-refused"
        (admissionFixture.Prepare(admission,descriptor,{descriptor with Identity={descriptor.Identity with ProfileId="unsupported"}}) |> rejected)
    require "composed-no-admission-io" (admissionFixture.Writes=0 && admissionFixture.Retirements=0)
    admissionFixture.Prepare(admission,descriptor,descriptor) |> accepted |> ignore
    require "composed-repeated-descriptor-refused" (admissionFixture.Prepare(admission,descriptor,descriptor) |> rejected)

    let successful = Fixture()
    let composed = successful.Prepare(Admission(),descriptor,descriptor) |> accepted
    let running = composed.Run()
    waitUntil (fun () -> successful.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
    for ordinal,kind,payload,needsAck in
        [1,"ready","{\"binding\":"+bindingText+"}",true
         2,"issuance-possible","{}",true; 3,"token-owned","{}",false
         4,"revocation-possible","{}",true; 5,"revoked","{}",false; 6,"terminal","{}",false] do
        successful.Send(child ordinal kind payload)
        waitUntil (fun () -> composed.Protocol.Accepted.Length >= ordinal && (not needsAck || composed.ConfirmedAckOrdinal=ordinal))
        |> fun work -> work.GetAwaiter().GetResult()
    successful.Eof()
    waitUntil (fun () -> composed.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
    successful.Terminate()
    let finished = wait running |> accepted
    require "composed-real-session-completes" (finished.ProtocolComplete && finished.LastThreadTerminated && not finished.SignalRequested)
    require "composed-run-cannot-repeat" (wait (composed.Run()) |> rejected)

    let blockedFixture = Fixture()
    let blockedRetention = TaskCompletionSource<Result<PhaseResultReadback,string>>(TaskCreationOptions.RunContinuationsAsynchronously)
    blockedFixture.Persist <- fun _ -> blockedRetention.Task
    let blockedSession = blockedFixture.Prepare(Admission(),descriptor,descriptor) |> accepted
    let blockedRun = blockedSession.Run()
    waitUntil (fun () -> blockedFixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
    blockedFixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
    waitUntil (fun () -> blockedSession.Protocol.RetentionTasks.Length=1) |> fun work -> work.GetAwaiter().GetResult()
    blockedFixture.WorkExpires()
    waitUntil (fun () -> blockedFixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
    require "composed-signal-does-not-prove-termination" (blockedSession.LocalTasks=Outstanding)
    blockedFixture.Expire()
    let stopped = wait blockedRun |> accepted
    require "composed-unknown-retains-outstanding-work" (stopped.SignalRequested && not stopped.LastThreadTerminated && blockedSession.LocalTasks=Outstanding)
    let blockedSnapshot = blockedSession.Protocol.Accepted |> List.exactlyOne
    blockedRetention.SetResult(Ok { Binding=blockedSnapshot.Binding; Ordinal=blockedSnapshot.Ordinal; FrameDigest=blockedSnapshot.Digest })
    blockedFixture.Eof()
    waitUntil (fun () -> blockedSession.LocalTasks=Settled) |> fun work -> work.GetAwaiter().GetResult()
    require "composed-late-readback-does-not-send-ack" (blockedFixture.Writes=1 && blockedSession.ConfirmedAckOrdinal=0)

    let partialFixture = Fixture()
    partialFixture.WriteCount <- fun bytes -> if partialFixture.Writes=1 then bytes.Length else bytes.Length-1
    let partialSession = partialFixture.Prepare(Admission(),descriptor,descriptor) |> accepted
    let partialRun = partialSession.Run()
    waitUntil (fun () -> partialFixture.Writes=1) |> fun work -> work.GetAwaiter().GetResult()
    partialFixture.Send(child 1 "ready" ("{\"binding\":"+bindingText+"}"))
    waitUntil (fun () -> partialFixture.Retirements=1) |> fun work -> work.GetAwaiter().GetResult()
    require "composed-partial-ack-retains-possible-delivery" (partialSession.ConfirmedAckOrdinal=0 && partialSession.Protocol.Accepted.Length=1)
    partialFixture.Eof(); partialFixture.Expire()
    let partialResult = wait partialRun |> accepted
    require "composed-partial-write-cannot-qualify" (not partialResult.ProtocolComplete && not partialResult.LastThreadTerminated)
