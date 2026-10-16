namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FS.GG.Execution.Contracts

/// Completion only; no task arrays, snapshots, streams or operational receipt escape.
[<RequireQualifiedAccess>]
type FiniteCustodyClosedSourceStatus = Complete | Refused

/// Qualification-only cuts. No public provider or activation selector exists.
type internal ClosedSourceFixtureCut = NoCut | RequestWrite | TruncatedReply | ChildControl

type internal ClosedSourceFixtureAdmission() =
    let gate = obj()
    let mutable taken = false
    member _.Take() = lock gate (fun () ->
        if taken then false
        else
            taken <- true
            true)

/// Source-owned finite fixture buffers. These are not production endpoints.
type private ClosedSourceInput(maximumFrames: int, maximumFrameBytes: int, confirmed: unit -> int, onEnd: unit -> unit) =
    inherit Stream()
    let queue = Channel.CreateBounded<int * byte array>(maximumFrames)
    let mutable frames = 0
    let mutable calls = 0
    let mutable current = Array.empty<byte>
    let mutable offset = 0
    let mutable required = 0
    let mutable waits = 0
    let mutable aborted = false
    member _.Send(bytes: byte array, afterAck: int) =
        if bytes.Length > maximumFrameBytes || Interlocked.Increment(&frames) > maximumFrames then invalidOp "closed-source-frame-limit"
        if not (queue.Writer.TryWrite((afterAck, Array.copy bytes))) then invalidOp "closed-source-queue-limit"
    member _.Finish() = queue.Writer.TryComplete() |> ignore
    member _.Abort() = aborted <- true; queue.Writer.TryComplete() |> ignore
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())
    override _.Position with get() = raise (NotSupportedException()) and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.ReadAsync(buffer: Memory<byte>, token: CancellationToken) =
        // Reserve cumulatively before allocating the task, including EOF/failure.
        if Interlocked.Increment(&calls) > maximumFrames * maximumFrameBytes + 1 then invalidOp "closed-source-read-limit"
        ValueTask<int>(task {
            try
                if offset = current.Length then
                    let! afterAck, value = queue.Reader.ReadAsync(token).AsTask()
                    required <- afterAck
                    current <- value
                    offset <- 0
                while not aborted && confirmed() < required do
                    if Interlocked.Increment(&waits) > 4096 then invalidOp "closed-source-ack-wait-limit"
                    do! Task.Delay 1
                if aborted then return 0
                else
                    let count = min buffer.Length (current.Length - offset)
                    current.AsMemory(offset, count).CopyTo buffer
                    offset <- offset + count
                    return count
            with :? ChannelClosedException ->
                onEnd()
                return 0 })

type private ClosedSourceOutput(maximumCalls: int, maximumBytes: int, onWrite: ReadOnlyMemory<byte> -> unit) =
    inherit Stream()
    let mutable calls = 0
    let mutable bytes = 0
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())
    override _.Position with get() = raise (NotSupportedException()) and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, _: CancellationToken) =
        if Interlocked.Increment(&calls) > maximumCalls || Interlocked.Add(&bytes, buffer.Length) > maximumBytes then invalidOp "closed-source-write-limit"
        onWrite buffer
        ValueTask.CompletedTask

module private ClosedSourceConstruction =
    let profileDigest = "ed652bb3fe4f6132a9329f4b791f60f51297bb2ac0507dacb344b0e5191df660"
    let vectorsDigest = "edb142bac8a253be2e031c7a5ccad1f75ab669a55ec6d9f598439130e43836aa"
    let digest (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let profileMatches (bytes: ReadOnlyMemory<byte>) =
        bytes.Length > 0 && bytes.Length <= 65536 && digest(bytes.ToArray()) = profileDigest
    let text (value: JsonElement) (name: string) = value.GetProperty(name).GetString()
    let fixture cut =
        // This internal friend-test seam selects only the maintained immutable
        // fixture checkout. It is unreachable from public production Prepare.
        let prepared = Environment.GetEnvironmentVariable "FSGG_UTEL_CALLBACK_SOURCE"
        if String.IsNullOrWhiteSpace prepared || not (Path.IsPathFullyQualified prepared) then invalidOp "closed-source-fixture-unavailable"
        let root = prepared + "-closed"
        let read name maximum =
            let path = Path.Combine(root, "tests/release-successor-authentication", name)
            if FileInfo(path).Length > int64 maximum then invalidOp "closed-source-input-limit"
            File.ReadAllBytes path
        let profileBytes = read "closed-source-profile.json" 65537
        if profileBytes.Length = 0 || profileBytes[profileBytes.Length - 1] <> 10uy
           || not (profileMatches(ReadOnlyMemory<byte>(profileBytes, 0, profileBytes.Length - 1))) then invalidOp "closed-source-profile-refused"
        let raw = read "slot-callback-pair-vectors.json" 131072
        if digest raw <> vectorsDigest then invalidOp "closed-source-vectors-refused"
        use document = JsonDocument.Parse(ReadOnlyMemory<byte> raw)
        let rows = document.RootElement.GetProperty("exchanges").EnumerateArray() |> Seq.toArray
        if rows.Length <> 13 then invalidOp "closed-source-exchange-limit"
        let requests = rows |> Array.map (fun row -> Convert.FromHexString(text row "request"))
        let responses = rows |> Array.map (fun row -> Convert.FromHexString(text row "response"))
        if (requests |> Array.exists (fun bytes -> bytes.Length > 4096))
           || Array.sumBy Array.length requests > 40960
           || (responses |> Array.exists (fun bytes -> bytes.Length > 512)) then invalidOp "closed-source-byte-limit"
        let binding = (PhaseCallbackProtocol.decode requests[0]).Data.Value |> Convert.FromBase64String
        use boundDocument = JsonDocument.Parse(ReadOnlyMemory<byte> binding)
        let bound = boundDocument.RootElement
        let pins, workflow, app = bound.GetProperty "source", bound.GetProperty "workflow", bound.GetProperty "app"
        let processBinding, clockBinding = bound.GetProperty "process", bound.GetProperty "clock"
        let domain: ClockDomain = { Id = Guid.Parse(text clockBinding "domain"); Frequency = 1000L }
        let at ticks: ClockStamp = { Domain = domain; Ticks = ticks }
        let original = Budget.create (at 0L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> invalidOp "closed-source-budget")
        let descriptor: FiniteCustodyInertDescriptor =
            { Mode = "inert"
              Identity = { InvocationId = Guid.Parse(text bound "invocation"); Execution = None; LaunchOrdinal = 0UL
                           SourceDigest = text pins "childClosure"; ProfileId = FiniteCustodyWindow.profileId; ProfileDigest = String.replicate 64 "f" }
              OriginalOperation = "utel-rel-21"; OriginalGeneration = 2L; DomainProfile = text bound "profile"
              Process = { Platform = "inert"; ScopeId = "closed-source-fixture"; Pid = processBinding.GetProperty("pid").GetInt32(); BirthToken = text processBinding "birth" }
              ChildRevision = text pins "childRevision"; RunnerRevision = text pins "runnerRevision"
              RunnerClosure = text pins "runnerClosure"; ContractDigest = text pins "contract"; RuntimeClosure = String.replicate 64 "0"
              WorkflowRepository = workflow.GetProperty("repositoryId").GetUInt64(); WorkflowPath = text workflow "path"; WorkflowRevision = text workflow "revision"
              WorkflowRun = 0UL; WorkflowAttempt = 0u; App = app.GetProperty("id").GetUInt64(); Installation = app.GetProperty("installation").GetUInt64()
              ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]; ResultDestination = "closed-owned-source-slot" }
        let phases = requests |> Array.choose (fun bytes -> let request = PhaseCallbackProtocol.decode bytes in if request.Kind = "validate" then Some(Convert.FromBase64String request.Data.Value) else None)
        if phases.Length <> 6 || (phases |> Array.exists (fun bytes -> bytes.Length > 2048)) || Array.sumBy Array.length phases > 16384 then invalidOp "closed-source-phase-limit"
        let termination = TaskCompletionSource<bool option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable confirmed = fun () -> 0
        let child = new ClosedSourceInput(6, 2048, (fun () -> confirmed()), ignore)
        let replies = new ClosedSourceInput(13, 512, (fun () -> 0), fun () -> termination.TrySetResult(Some true) |> ignore)
        let mutable exchange = 0
        let mutable acknowledgments = 0
        let requestsOut = new ClosedSourceOutput(13, 40960, fun bytes ->
            if cut = RequestWrite || exchange >= 13 || bytes.ToArray() <> requests[exchange] then invalidOp "closed-source-request-refused"
            let reply = responses[exchange]
            exchange <- exchange + 1
            if cut = TruncatedReply then
                replies.Send(reply[0..reply.Length - 2], 0)
                replies.Finish()
            else
                replies.Send(reply, 0)
                if exchange = 13 then replies.Finish())
        let control = new ClosedSourceOutput(4, 16384, fun bytes ->
            if cut = ChildControl then invalidOp "closed-source-control-refused"
            use row = JsonDocument.Parse(bytes.Slice 4)
            match text row.RootElement "k" with
            | "start" -> child.Send(phases[0], 0)
            | "ack" ->
                acknowledgments <- acknowledgments + 1
                match row.RootElement.GetProperty("n").GetInt32() with
                | 1 -> child.Send(phases[1], 1)
                | 2 -> child.Send(phases[2], 2); child.Send(phases[3], 2)
                | 4 -> child.Send(phases[4], 4); child.Send(phases[5], 4); child.Finish()
                | _ -> invalidOp "closed-source-ack-order"
            | _ -> invalidOp "closed-source-control-kind")
        let stop () =
            child.Abort()
            replies.Abort()
            termination.TrySetResult(Some true) |> ignore
            Task.FromResult true
        let channels: FiniteCustodyInertChannels =
            { ChildResults = child; ChildControl = control; OwnerResponses = replies; OwnerRequests = requestsOut
              RequestRetirement = stop; Termination = termination.Task }
        let clock = { new IMonotonicClock with member _.Now() = at 1L }
        let adapter = FiniteCustodySessionAdapter.Prepare(FiniteCustodyInertAdmission(), descriptor, descriptor, clock, original,
                            ReadOnlyMemory<byte> binding, channels, CancellationToken.None) |> Result.defaultWith (fun _ -> invalidOp "closed-source-preparation-refused")
        confirmed <- fun () -> adapter.ConfirmedAckOrdinal
        adapter, (fun () -> exchange = 13 && acknowledgments = 3), stop

/// Opt-in SOURCE facade over the maintained adapter. No production provider exists.
/// Internal qualification owns every legacy input and keeps the returned handle private.
type FiniteCustodyClosedSource private (adapter: FiniteCustodySessionAdapter, complete: unit -> bool, stop: unit -> Task<bool>) =
    let gate = obj()
    let mutable attempted = false
    /// Fixed completion only. Retained tasks/bytes remain inside the owned graph;
    /// completion proves neither physical release nor operational acceptance.
    member _.Run() =
        let first = lock gate (fun () ->
            if attempted then false
            else
                attempted <- true
                true)
        if not first then FiniteCustodyClosedSourceStatus.Refused
        else
            try
                let outcome = adapter.Run().GetAwaiter().GetResult()
                Task.WhenAll(adapter.RetainedTasks).GetAwaiter().GetResult()
                match outcome with
                | Ok result when result.ProtocolComplete && result.StopReason.IsNone && adapter.LocalTasks = Settled && complete() -> FiniteCustodyClosedSourceStatus.Complete
                | _ -> FiniteCustodyClosedSourceStatus.Refused
            with _ ->
                stop().GetAwaiter().GetResult() |> ignore
                FiniteCustodyClosedSourceStatus.Refused
    /// Exact shipped profile only. Provider/enrollment/accounting remain null;
    /// no clock, file lookup, callback, reservation or process is touched here.
    static member Prepare(profile: ReadOnlyMemory<byte>): Result<FiniteCustodyClosedSource, string> =
        if not (ClosedSourceConstruction.profileMatches profile) then Error "closed-source-profile-refused"
        else Error "closed-source-provider-unavailable"
    /// Test population only; no caller streams, callbacks, runtime, binding or path.
    static member internal PrepareFixture(admission: ClosedSourceFixtureAdmission, cut: ClosedSourceFixtureCut) =
        if not (admission.Take()) then Error "closed-source-original-consumed"
        else
            try
                let adapter, complete, stop = ClosedSourceConstruction.fixture cut
                Ok(FiniteCustodyClosedSource(adapter, complete, stop))
            with _ -> Error "closed-source-fixture-refused"
