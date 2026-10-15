namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open FS.GG.Execution.Contracts
open FS.GG.Coordination.Orchestration.Execution

/// Explicitly joins the existing hosted custody-window filter. These controlled
/// endpoint witnesses establish neither deployed retention nor a physical maximum.
module FiniteCustodyWindowTestsManagedCaptureTests =
    let private limit = TimeSpan.FromSeconds 10.
    let private signal () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let private quote (value: string) = JsonSerializer.Serialize value
    let private objectJson fields =
        fields
        |> List.sortWith (fun (left, _) (right, _) -> StringComparer.Ordinal.Compare(left, right))
        |> List.map (fun (name, value) -> quote name + ":" + value)
        |> String.concat ","
        |> fun body -> "{" + body + "}"
    let private framed body =
        let payload = Encoding.ASCII.GetBytes(body: string)
        let bytes = Array.zeroCreate<byte> (payload.Length + 4)
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), uint32 payload.Length)
        payload.CopyTo(bytes, 4)
        bytes
    let private until condition = task {
        let elapsed = Diagnostics.Stopwatch.StartNew()
        while not (condition ()) && elapsed.Elapsed < limit do do! Task.Delay 1
        Assert.True(condition (), "controlled endpoint did not reach the required cut") }

    type private ResponseStream() =
        inherit MemoryStream()
        let bytes = Channel.CreateBounded<byte>(512)
        member _.Reply(frame: byte array) =
            for value in frame do Assert.True(bytes.Writer.TryWrite value, "one bounded callback response at a time")
        member _.End() = bytes.Writer.TryComplete() |> ignore
        override _.ReadAsync(buffer: Memory<byte>, _: CancellationToken) = ValueTask<int>(task {
            let! available = bytes.Reader.WaitToReadAsync().AsTask()
            if not available then return 0
            else
                let! value = bytes.Reader.ReadAsync().AsTask()
                buffer.Span[0] <- value
                return 1 })

    type private WriteMode = Immediate | AsyncBlocked | SyncBlocked

    type private RequestStream(mode: WriteMode, respond: ReadOnlyMemory<byte> -> unit) =
        inherit MemoryStream()
        let gate = obj()
        let witnesses = ResizeArray<ReadOnlyMemory<byte> * byte array>()
        let entered = signal ()
        let asynchronousRelease = signal ()
        let synchronousRelease = new ManualResetEventSlim(false)
        let mutable returned = 0
        member _.Entered = entered.Task
        member _.Returned = Volatile.Read(&returned) <> 0
        member _.DisposeLatch() = synchronousRelease.Dispose()
        member _.Witnesses = lock gate (fun () -> witnesses.ToArray())
        member _.Release() =
            synchronousRelease.Set()
            asynchronousRelease.TrySetResult() |> ignore
        override _.WriteAsync(bytes: ReadOnlyMemory<byte>, _: CancellationToken) =
            // The first value aliases the actual supplied request. The second is
            // deliberately a separate physical copy owned by this test endpoint.
            lock gate (fun () -> witnesses.Add(bytes, bytes.ToArray()))
            entered.TrySetResult() |> ignore
            match mode with
            | Immediate ->
                respond bytes
                Volatile.Write(&returned, 1)
                ValueTask.CompletedTask
            | AsyncBlocked ->
                Volatile.Write(&returned, 1)
                ValueTask(asynchronousRelease.Task :> Task)
            | SyncBlocked ->
                if not (synchronousRelease.Wait limit) then invalidOp "synchronous fixture watchdog"
                Volatile.Write(&returned, 1)
                ValueTask.CompletedTask

    type private ChildStream(phases: (int * byte array) array, confirmed: unit -> int) =
        inherit MemoryStream()
        let mutable index = 0
        let mutable aborted = 0
        member _.Abort() = Volatile.Write(&aborted, 1)
        override _.ReadAsync(buffer: Memory<byte>, _: CancellationToken) = ValueTask<int>(task {
            if index = phases.Length then return 0
            else
                let required, bytes = phases[index]
                while Volatile.Read(&aborted) = 0 && confirmed () < required do do! Task.Delay 1
                if Volatile.Read(&aborted) <> 0 then return 0
                else
                    index <- index + 1
                    bytes.AsMemory().CopyTo buffer
                    return bytes.Length })

    type private ControlStream() =
        inherit MemoryStream()
        let gate = obj()
        let acknowledgments = ResizeArray<int>()
        member _.Acknowledgments = lock gate (fun () -> List.ofSeq acknowledgments)
        override _.WriteAsync(bytes: ReadOnlyMemory<byte>, _: CancellationToken) =
            use document = JsonDocument.Parse(bytes.Slice 4)
            if document.RootElement.GetProperty("k").GetString() = "ack" then
                lock gate (fun () -> acknowledgments.Add(document.RootElement.GetProperty("n").GetInt32()))
            ValueTask.CompletedTask

    /// Independent inert setup: no private fixture from another test module and
    /// no production endpoint, clock reset, domain authentication or durable sink.
    type private Fixture(mode: WriteMode) =
        let domain: ClockDomain = { Id = Guid.NewGuid(); Frequency = 1000L }
        let mutable ticks = 0L
        let at value: ClockStamp = { Domain = domain; Ticks = value }
        let clock = { new IMonotonicClock with member _.Now() = at (Interlocked.Read(&ticks)) }
        let original = Budget.create (at 0L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> failwith "inert budget")
        let descriptor: FiniteCustodyInertDescriptor =
            { Mode = "inert"
              Identity = { InvocationId = Guid.NewGuid(); Execution = None; LaunchOrdinal = 0UL
                           SourceDigest = String.replicate 64 "a"; ProfileId = FiniteCustodyWindow.profileId; ProfileDigest = String.replicate 64 "d" }
              OriginalOperation = "managed-capture-inert"; OriginalGeneration = 1L; DomainProfile = "managed-capture-inert/1"
              Process = { Platform = "inert"; ScopeId = "controlled-capture-endpoint"; Pid = 1; BirthToken = "a" }
              ChildRevision = String.replicate 40 "a"; RunnerRevision = String.replicate 40 "b"
              RunnerClosure = String.replicate 64 "b"; ContractDigest = String.replicate 64 "c"; RuntimeClosure = String.replicate 64 "f"
              WorkflowRepository = 1UL; WorkflowPath = ".github/workflows/inert-capture.yml"; WorkflowRevision = String.replicate 40 "c"
              WorkflowRun = 0UL; WorkflowAttempt = 0u; App = 1UL; Installation = 1UL
              ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]; ResultDestination = "in-memory-capture-witness" }
        let bindingText = objectJson [
            "mode", quote "inert"; "profile", quote descriptor.DomainProfile; "operation", quote descriptor.OriginalOperation
            "originalGeneration", "1"; "invocation", quote (string descriptor.Identity.InvocationId)
            "process", objectJson ["pid", "1"; "birth", quote descriptor.Process.BirthToken]
            "source", objectJson ["childRevision", quote descriptor.ChildRevision; "childClosure", quote descriptor.Identity.SourceDigest
                                  "runnerRevision", quote descriptor.RunnerRevision; "runnerClosure", quote descriptor.RunnerClosure; "contract", quote descriptor.ContractDigest]
            "workflow", objectJson ["repositoryId", "1"; "path", quote descriptor.WorkflowPath; "revision", quote descriptor.WorkflowRevision; "run", "0"; "attempt", "0"]
            "clock", objectJson ["domain", quote (string domain.Id); "frequency", quote "1000"; "start", quote "0"
                                 "normal", quote "270000"; "work", quote "285000"; "total", quote "300000"]
            "app", objectJson ["id", "1"; "installation", "1"] ]
        let binding = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes bindingText)
        let digest = PhaseResultProtocol.bindingDigest binding |> Result.defaultWith failwith
        let phases =
            [| 0, "ready"; 1, "issuance-possible"; 2, "token-owned"; 2, "revocation-possible"; 4, "revoked"; 4, "terminal" |]
            |> Array.mapi (fun index (required, kind) ->
                let payload = if kind = "ready" then objectJson ["binding", bindingText] else "{}"
                required, framed (objectJson ["b", quote digest; "k", quote kind; "n", string (index + 1); "p", payload; "v", quote PhaseResultProtocol.identifier]))
        let termination = TaskCompletionSource<bool option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let retirement = signal ()
        let responses = new ResponseStream()
        let control = new ControlStream()
        let mutable adapter: FiniteCustodySessionAdapter option = None
        let child = new ChildStream(phases, fun () -> adapter |> Option.map _.ConfirmedAckOrdinal |> Option.defaultValue 0)
        let respond (bytes: ReadOnlyMemory<byte>) =
            let request = PhaseCallbackProtocol.decode (bytes.ToArray())
            let kind = match request.Kind with "bind" -> "bound" | "validate" -> "validated" | "retain" -> "retained" | _ -> failwith "inert request kind"
            responses.Reply(PhaseCallbackProtocol.encode { request with Kind = kind; Data = None; Reason = None })
            if request.Kind = "retain" && request.Ordinal = 6 then
                responses.End()
                termination.TrySetResult(Some true) |> ignore
        let requests = new RequestStream(mode, respond)
        let channels: FiniteCustodyInertChannels =
            { ChildResults = child; ChildControl = control; OwnerResponses = responses; OwnerRequests = requests
              RequestRetirement = fun () ->
                  retirement.TrySetResult() |> ignore
                  termination.TrySetResult(Some true) |> ignore
                  Task.FromResult true
              Termination = termination.Task }
        let admission = FiniteCustodyInertAdmission()
        let prepare () = FiniteCustodySessionAdapter.Prepare(admission, descriptor, descriptor, clock, original, binding, channels, CancellationToken.None)
        let session = prepare () |> Result.defaultWith failwith
        let mutable running: Task<Result<FiniteCustodyInertResult, string>> option = None
        do adapter <- Some session
        member _.Session = session
        member _.Requests = requests
        member _.Responses = responses
        member _.Retirement = retirement.Task
        member _.Acknowledgments = control.Acknowledgments
        member _.PrepareAgain() = prepare ()
        member _.Advance(value) =
            Assert.True(value >= Interlocked.Read(&ticks), "test clock never rolls backwards")
            Interlocked.Exchange(&ticks, value) |> ignore
        member _.Run() =
            let work = session.Run()
            running <- Some work
            work
        member _.AssertRetainedCleanup() =
            let evidence: EvidenceRef = { Kind = "controlled-inert"; Reference = "managed-capture-cut"; Digest = None }
            let stamp: ObservationStamp = { At = Some(at (Interlocked.Read(&ticks))); Scope = "capture-cut"; Generation = Some descriptor.Process; Provenance = evidence }
            let owner: OwnerReference = { OwnerId = "capture-fixture-owner"; Evidence = evidence }
            let existing: CleanupResult =
                { Disposition = Complete; RequestedActions = []; LeaderTermination = Known(true, stamp)
                  WorkloadEmpty = Known(true, stamp); OwnedChildrenReaped = Known(true, stamp); LocalTasks = Known(Settled, stamp)
                  Unresolved = []; OmittedUnresolved = 0L; Custody = Released stamp }
            let joined = session.JoinCleanup(existing, stamp, owner)
            Assert.Equal(Incomplete, joined.Disposition)
            Assert.Equal(RetainedByOwner owner, joined.Custody)
            match joined.LocalTasks with Known(Outstanding, _) -> () | _ -> failwith "unfinished writer lost from cleanup"
        member _.Settle() = task {
            requests.Release()
            responses.End()
            do! Task.WhenAll(session.RetainedTasks).WaitAsync limit }
        interface IDisposable with
            member _.Dispose() =
                // Every failure path opens both kinds of writer and every reader.
                Interlocked.Exchange(&ticks, 300000L) |> ignore
                child.Abort()
                responses.End()
                requests.Release()
                termination.TrySetResult(Some true) |> ignore
                running |> Option.iter (fun work -> work.WaitAsync(limit).GetAwaiter().GetResult() |> ignore)
                Task.WhenAll(session.RetainedTasks).WaitAsync(limit).GetAwaiter().GetResult()
                (child :> IDisposable).Dispose()
                (control :> IDisposable).Dispose()
                requests.DisposeLatch()
                (requests :> IDisposable).Dispose()
                (responses :> IDisposable).Dispose()

    let private assertWitnesses (witnesses: (ReadOnlyMemory<byte> * byte array) array) =
        let originals = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let copies = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let mutable retained = 0L
        for alias, copy in witnesses do
            let mutable segment = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(alias, &segment))
            Assert.True(originals.Add(segment.Array :> obj), "count each original backing array once")
            Assert.True(copies.Add(copy :> obj), "each deliberate copy has its own backing array")
            Assert.False(obj.ReferenceEquals(segment.Array, copy))
            Assert.True(alias.Span.SequenceEqual(copy.AsSpan()))
            Assert.InRange(alias.Length, 1, 4096)
            retained <- retained + int64 alias.Length + int64 copy.Length
        Assert.True(retained > 0L)
        // A fixture-local observed lower bound, excluding all other allocations.
        printfn "managed-capture-witness originals=%d independentCopies=%d retainedSegmentBytes=%d" originals.Count copies.Count retained
        Assert.True(Result.isError (FiniteCustodyManagedOwnershipProfile.requireClosedPhysicalCopyProfile FiniteCustodyManagedOwnershipProfile.identifier))

    [<Fact>]
    let ``completed callback writes retain original request aliases and independent copies after task settlement`` () = task {
        use fixture = new Fixture(Immediate)
        let! result = fixture.Run().WaitAsync limit
        let outcome = result |> Result.defaultWith failwith
        Assert.True(outcome.ProtocolComplete)
        Assert.Equal(None, outcome.StopReason)
        Assert.Equal(Settled, outcome.LocalTasks)
        Assert.Equal(Settled, fixture.Session.LocalTasks)
        Assert.True(fixture.Acknowledgments = [1; 2; 4])
        Assert.Equal(13, fixture.Requests.Witnesses.Length)
        let last, _ = Array.last fixture.Requests.Witnesses
        let finalRequest = PhaseCallbackProtocol.decode (last.ToArray())
        Assert.Equal("retain", finalRequest.Kind)
        Assert.Equal(6, finalRequest.Ordinal)
        assertWitnesses fixture.Requests.Witnesses
        Assert.True(Result.isError (fixture.PrepareAgain()))
        Assert.Equal(13, fixture.Requests.Witnesses.Length) }

    [<Fact>]
    let ``blocked callback write preserves outstanding custody and one use at the original cutoff`` () = task {
        use fixture = new Fixture(AsyncBlocked)
        let running = fixture.Run()
        do! fixture.Requests.Entered.WaitAsync limit
        do! until (fun () -> fixture.Requests.Returned && fixture.Session.RetainedTasks.Length >= 3)
        Assert.True fixture.Requests.Returned
        fixture.Advance 285000L
        do! fixture.Retirement.WaitAsync limit
        fixture.Responses.End()
        fixture.Advance 300000L
        let! result = running.WaitAsync limit
        let outcome = result |> Result.defaultWith failwith
        Assert.Equal(Some WorkDeadline, outcome.StopReason)
        Assert.False outcome.ProtocolComplete
        Assert.Equal(Outstanding, outcome.LocalTasks)
        fixture.AssertRetainedCleanup()
        Assert.Empty fixture.Acknowledgments
        Assert.Single(fixture.Requests.Witnesses) |> ignore
        let! retry = fixture.Session.Run().WaitAsync limit
        Assert.True(Result.isError retry)
        do! fixture.Settle()
        Assert.Equal(Settled, fixture.Session.LocalTasks)
        Assert.Equal(Some WorkDeadline, outcome.StopReason)
        Assert.Single(fixture.Requests.Witnesses) |> ignore
        assertWitnesses fixture.Requests.Witnesses }

    [<Fact>]
    let ``synchronous callback write entry preserves the outer writer before task publication`` () = task {
        use fixture = new Fixture(SyncBlocked)
        let running = fixture.Run()
        do! fixture.Requests.Entered.WaitAsync limit
        Assert.False fixture.Requests.Returned
        fixture.Responses.End()
        do! fixture.Retirement.WaitAsync limit
        do! until (fun () -> fixture.Session.RetainedTasks |> Array.filter (fun work -> not work.IsCompleted) |> Array.length = 1)
        Assert.False fixture.Requests.Returned
        fixture.Advance 300000L
        let! result = running.WaitAsync limit
        let outcome = result |> Result.defaultWith failwith
        Assert.Equal(Some OwnerLost, outcome.StopReason)
        Assert.Equal(Outstanding, outcome.LocalTasks)
        fixture.AssertRetainedCleanup()
        Assert.Single(fixture.Requests.Witnesses) |> ignore
        Assert.Empty fixture.Acknowledgments
        do! fixture.Settle()
        Assert.True fixture.Requests.Returned
        Assert.Equal(Settled, fixture.Session.LocalTasks)
        Assert.Equal(Some OwnerLost, outcome.StopReason)
        assertWitnesses fixture.Requests.Witnesses }
