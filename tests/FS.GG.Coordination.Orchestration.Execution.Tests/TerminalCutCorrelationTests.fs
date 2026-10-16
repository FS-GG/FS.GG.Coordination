namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Orchestration.Execution

/// Joins two source witnesses at one held execution cut; no total physical-fit claim.
module FiniteCustodyTransportTestsTerminalCutCorrelationTests =
    let private limit = TimeSpan.FromSeconds 20.
    let private text (value: JsonElement) name = value.GetProperty(name: string).GetString()
    let private sha256 (bytes: ReadOnlySpan<byte>) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let private readJson path = JsonDocument.Parse(ReadOnlyMemory<byte>(File.ReadAllBytes path))
    let private untilFile path (owner: Process) = task {
        let watch = Stopwatch.StartNew()
        while not (File.Exists path) && not owner.HasExited && watch.Elapsed < limit do do! Task.Delay 5
        Assert.False(owner.HasExited && not (File.Exists path), "terminal cut owner exited before rendezvous")
        Assert.True(File.Exists path, "terminal cut rendezvous did not arrive") }

    type private ReadCall =
        { Number: int; Buffer: Memory<byte>
          mutable RequiredAck: int option; mutable Work: Task<int> option }

    type private WriteCall =
        { Number: int; Bytes: ReadOnlyMemory<byte>; mutable Work: Task option }

    type private PhaseStream(confirmed: unit -> int) =
        inherit MemoryStream()
        let input = Channel.CreateBounded<int * byte array>(6)
        let gate = obj()
        let mutable calls = 0
        let mutable pending: ReadCall option = None
        let mutable aborted = 0
        member _.PendingRead = lock gate (fun () -> pending)
        member _.Queued = input.Reader.Count
        member _.Send(required, bytes) = Assert.True(input.Writer.TryWrite((required, bytes)))
        member _.Complete() = input.Writer.TryComplete() |> ignore
        member _.Abort() = Interlocked.Exchange(&aborted, 1) |> ignore; input.Writer.TryComplete() |> ignore
        override _.ReadAsync(buffer: Memory<byte>, _: CancellationToken) =
            let call = { Number = Interlocked.Increment(&calls); Buffer = buffer; RequiredAck = None; Work = None }
            lock gate (fun () -> Assert.True(pending.IsNone); pending <- Some call)
            let work = task {
                try
                    let! available = input.Reader.WaitToReadAsync().AsTask()
                    if not available then return 0
                    else
                        let! required, bytes = input.Reader.ReadAsync().AsTask()
                        lock gate (fun () -> call.RequiredAck <- Some required)
                        while Volatile.Read(&aborted) = 0 && confirmed () < required do do! Task.Delay 1
                        if Volatile.Read(&aborted) <> 0 then return 0
                        else
                            bytes.AsMemory().CopyTo buffer
                            return bytes.Length
                finally
                    lock gate (fun () -> pending <- None) }
            lock gate (fun () -> call.Work <- Some work)
            ValueTask<int>(work)

    type private ControlStream(onWrite: ReadOnlyMemory<byte> -> Task) =
        inherit MemoryStream()
        let gate = obj()
        let mutable calls = 0
        let mutable pending: WriteCall option = None
        member _.PendingWrite = lock gate (fun () -> pending)
        override _.WriteAsync(bytes: ReadOnlyMemory<byte>, _: CancellationToken) =
            let call = { Number = Interlocked.Increment(&calls); Bytes = bytes; Work = None }
            lock gate (fun () -> Assert.True(pending.IsNone); pending <- Some call)
            let work = task {
                try do! onWrite bytes
                finally lock gate (fun () -> pending <- None) }
            lock gate (fun () -> call.Work <- Some work)
            ValueTask(work :> Task)

    type private CallbackReadCall =
        { Buffer: Memory<byte>; Token: CancellationToken; Underlying: Task<int>
          mutable Work: Task<int> option }

    // The actual supplied memory/token are forwarded once to the genuine stdout.
    type private ObservingInput(target: Stream) =
        inherit Stream()
        let gate = obj()
        let mutable pending: CallbackReadCall option = None
        member _.Target = target
        member _.PendingRead = lock gate (fun () -> pending)
        override _.CanRead = target.CanRead
        override _.CanSeek = false
        override _.CanWrite = false
        override _.Length = raise (NotSupportedException())
        override _.Position with get () = raise (NotSupportedException()) and set _ = raise (NotSupportedException())
        override _.Flush() = raise (NotSupportedException())
        override _.Read(_, _, _) = raise (NotSupportedException())
        override _.Seek(_, _) = raise (NotSupportedException())
        override _.SetLength(_) = raise (NotSupportedException())
        override _.Write(_, _, _) = raise (NotSupportedException())
        override _.ReadAsync(buffer: Memory<byte>, token: CancellationToken) =
            let underlying = target.ReadAsync(buffer, token).AsTask()
            let call = { Buffer = buffer; Token = token; Underlying = underlying; Work = None }
            lock gate (fun () -> Assert.True(pending.IsNone); pending <- Some call)
            let work = task {
                try return! underlying
                finally lock gate (fun () -> pending <- None) }
            lock gate (fun () -> call.Work <- Some work)
            ValueTask<int>(work)

    type private CapturingOutput(target: Stream) =
        inherit Stream()
        let gate = obj()
        let captures = ResizeArray<ReadOnlyMemory<byte> * byte array>()
        member _.Captures = lock gate (fun () -> captures.ToArray())
        member _.Target = target
        override _.CanRead = false
        override _.CanSeek = false
        override _.CanWrite = true
        override _.Length = raise (NotSupportedException())
        override _.Position with get () = raise (NotSupportedException()) and set _ = raise (NotSupportedException())
        override _.Flush() = target.Flush()
        override _.FlushAsync(token) = target.FlushAsync token
        override _.Read(_, _, _) = raise (NotSupportedException())
        override _.Seek(_, _) = raise (NotSupportedException())
        override _.SetLength(_) = raise (NotSupportedException())
        override _.Write(_, _, _) = raise (NotSupportedException())
        override _.WriteAsync(bytes: ReadOnlyMemory<byte>, token: CancellationToken) =
            lock gate (fun () -> captures.Add(bytes, bytes.ToArray()))
            target.WriteAsync(bytes, token)

    let private captureDigest (captures: (ReadOnlyMemory<byte> * byte array) array) =
        use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let originals = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let copies = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let mutable length = 0L
        for alias, copy in captures do
            let mutable segment = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(alias, &segment), "actual WriteAsync memory must expose its array")
            Assert.True(originals.Add(segment.Array :> obj), "each retained request backing array is distinct")
            Assert.True(copies.Add(copy :> obj), "each endpoint copy is distinct")
            Assert.False(obj.ReferenceEquals(segment.Array, copy))
            Assert.True(alias.Span.SequenceEqual(copy.AsSpan()), "retained alias still equals endpoint copy")
            Assert.InRange(alias.Length, 1, 4096)
            length <- length + int64 alias.Length + int64 copy.Length
            digest.AppendData(alias.Span)
        Assert.Equal(13, captures.Length)
        Assert.True(length > 0L)
        Assert.Equal(25738L, length) // Pinned thirteen-request source population only.
        printfn "terminal-cut-managed-capture originalRequests=%d endpointCopies=%d logicalBytes=%d" originals.Count copies.Count length
        Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()

    // Walk fields only: public snapshot accessors deliberately allocate copies.
    // The closed type boundary excludes fixture streams, process objects and runtime globals.
    let private managedCensus (session: FiniteCustodySessionAdapter) (captures: (ReadOnlyMemory<byte> * byte array) array) =
        let field name (value: obj) =
            let found = value.GetType().GetField(name, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            Assert.NotNull found
            found.GetValue value
        let composition = field "session" session
        let bridge = field "bridge" session
        let resultProtocol = field "protocol" composition
        let callbackProtocol = field "protocol" bridge
        let snapshots = field "retained" resultProtocol :?> PhaseResultSnapshot list
        Assert.Equal(6, snapshots.Length)
        Assert.Equal<int>([|1..6|], snapshots |> List.map _.Ordinal |> List.sort |> List.toArray)
        let scoped = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let mutable payloadSliceBytes = 0
        for snapshot in snapshots |> List.sortBy _.Ordinal do
            let backing = field "frame" snapshot :?> byte array
            let expected = [|1141; 972; 830; 917; 831; 991|][snapshot.Ordinal - 1]
            Assert.Equal(expected, backing.Length)
            Assert.True(scoped.Add backing, "snapshot backing objects must be distinct")
            let mutable payload = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(snapshot.OwnedPayload, &payload))
            Assert.Same(backing, payload.Array)
            Assert.InRange(payload.Offset, 4, backing.Length - 1)
            Assert.InRange(payload.Count, 1, backing.Length - payload.Offset)
            Assert.True(payload.Count < backing.Length, "payload slice length is not backing capacity")
            payloadSliceBytes <- payloadSliceBytes + payload.Count
        Assert.Equal(4835, payloadSliceBytes)
        Assert.Equal(5682, scoped |> Seq.sumBy (fun value -> (value :?> byte array).Length))
        let addArray expected owner name =
            let bytes = field name owner :?> byte array
            Assert.Equal(expected, bytes.Length)
            Assert.True(scoped.Add bytes, "independently owned backing array must be distinct")
        addArray 2048 resultProtocol "input"
        addArray 512 callbackProtocol "input"
        addArray 994 resultProtocol "bindingBytes"
        addArray 994 callbackProtocol "owned"
        let start = field "startFrame" composition :?> ReadOnlyMemory<byte>
        let mutable startBacking = Unchecked.defaultof<ArraySegment<byte>>
        Assert.True(MemoryMarshal.TryGetArray<byte>(start, &startBacking))
        Assert.Equal(1123, start.Length)
        Assert.Equal(1123, startBacking.Array.Length)
        Assert.Equal(0, startBacking.Offset)
        Assert.True(scoped.Add startBacking.Array)
        let endpointArrays = HashSet<obj>(ReferenceEqualityComparer.Instance)
        for alias, copy in captures do
            let mutable backing = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(alias, &backing))
            Assert.Equal(0, backing.Offset)
            Assert.Equal(alias.Length, backing.Array.Length)
            Assert.True(endpointArrays.Add backing.Array)
            Assert.True(endpointArrays.Add copy)
        Assert.Equal(26, endpointArrays.Count)
        Assert.All(scoped, fun value -> Assert.DoesNotContain(value, endpointArrays))
        let seen = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let arrays = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
        let assembly = typeof<PhaseResultSnapshot>.Assembly
        let rec walk depth (value: obj) =
            if not (isNull value) then
                Assert.InRange(depth, 0, 48)
                let kind = value.GetType()
                if kind = typeof<byte array> then
                    arrays.Add value |> ignore
                elif kind <> typeof<string> && not kind.IsPrimitive && not kind.IsEnum && seen.Add value then
                    Assert.InRange(seen.Count, 1, 8192)
                    let name = kind.FullName
                    let admitted = kind.Assembly = assembly || kind.IsArray
                                   || name.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal)
                                   || name.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal)
                                   || name.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)
                                   || name.StartsWith("System.ReadOnlyMemory`", StringComparison.Ordinal)
                                   || name.StartsWith("System.Memory`", StringComparison.Ordinal)
                                   || name.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                                   || name.StartsWith("Microsoft.FSharp.Core.FSharpOption`", StringComparison.Ordinal)
                                   || name.StartsWith("Microsoft.FSharp.Collections.FSharpList`", StringComparison.Ordinal)
                                   || name.StartsWith("System.Tuple`", StringComparison.Ordinal)
                    if admitted then
                        match value with
                        | :? Array as items ->
                            Assert.InRange(items.Length, 0, 256)
                            for index in 0 .. items.Length - 1 do walk (depth + 1) (items.GetValue index)
                        | _ ->
                            let mutable current = kind
                            while not (isNull current) && current <> typeof<obj> do
                                for field in current.GetFields flags do
                                    walk (depth + 1) (field.GetValue value)
                                current <- current.BaseType
        walk 0 session
        for work in session.RetainedTasks do walk 0 work
        // Assert the discovered graph population, including its one existing request
        // alias. No fixed byte allowance stands in for a missing root/object.
        Assert.Equal(12, arrays.Count)
        for value in scoped do Assert.True(arrays.Contains value, "declared backing must be reachable from live roots")
        let aliases = arrays |> Seq.filter endpointArrays.Contains |> Seq.toArray
        Assert.Single aliases |> ignore
        let mutable lastRequest = Unchecked.defaultof<ArraySegment<byte>>
        Assert.True(MemoryMarshal.TryGetArray<byte>(fst captures[12], &lastRequest))
        Assert.Same(lastRequest.Array, aliases[0])
        Assert.Equal(295, (aliases[0] :?> byte array).Length)
        Assert.Equal(11, scoped.Count)
        let capacity = scoped |> Seq.sumBy (fun value -> (value :?> byte array).Length)
        Assert.Equal(11353, capacity)
        printfn "terminal-cut-managed-source scopedObjects=%d backingCapacityBytes=%d snapshotCapacityBytes=5682 payloadSliceBytes=%d existingRequestAliases=%d unresolvedAckAndReaderRoots=true" scoped.Count capacity payloadSliceBytes aliases.Length
        scoped

    let private ackReadCensus (session: FiniteCustodySessionAdapter) (child: PhaseStream) (control: ControlStream) binding digest (captures: (ReadOnlyMemory<byte> * byte array) array) =
        let read = child.PendingRead.Value
        let write = control.PendingWrite.Value
        Assert.Equal(5, read.Number)
        Assert.Equal(4, write.Number)
        Assert.Equal(Some 4, read.RequiredAck)
        Assert.Equal(2, session.ConfirmedAckOrdinal)
        Assert.False(read.Work.Value.IsCompleted)
        Assert.False(write.Work.Value.IsCompleted)
        Assert.NotSame(read.Work.Value, write.Work.Value)
        let mutable ack = Unchecked.defaultof<ArraySegment<byte>>
        let mutable ingress = Unchecked.defaultof<ArraySegment<byte>>
        Assert.True(MemoryMarshal.TryGetArray<byte>(write.Bytes, &ack))
        Assert.True(MemoryMarshal.TryGetArray<byte>(Memory<byte>.op_Implicit read.Buffer, &ingress))
        Assert.Equal(199, write.Bytes.Length)
        Assert.Equal(199, ack.Array.Length)
        Assert.Equal(2048, read.Buffer.Length)
        Assert.Equal(2048, ingress.Array.Length)
        Assert.Equal(0, ack.Offset)
        Assert.Equal(0, ingress.Offset)
        Assert.NotSame(ack.Array, ingress.Array)
        use row = JsonDocument.Parse(write.Bytes.Slice 4)
        Assert.Equal("ack", text row.RootElement "k")
        Assert.Equal(4, row.RootElement.GetProperty("n").GetInt32())
        Assert.Equal(binding, text row.RootElement "b")
        Assert.Equal(digest, text row.RootElement "h")
        let field name (value: obj) =
            let mutable kind = value.GetType()
            let mutable found: FieldInfo = null
            let mutable layers = 0
            while isNull found && not (isNull kind) do
                layers <- layers + 1
                Assert.InRange(layers, 1, 8)
                found <- kind.GetField(name, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
                kind <- kind.BaseType
            Assert.NotNull found
            found.GetValue value
        let ports = field "ports" (field "session" session) :?> FiniteCustodyComposition.Ports
        let readContinuation = field "m_continuationObject" read.Work.Value
        let writeContinuation = field "m_continuationObject" write.Work.Value
        let readState = field "StateMachine" readContinuation
        let writeState = field "StateMachine" writeContinuation
        Assert.Equal(typeof<FiniteCustodySessionAdapter>.Assembly, readState.GetType().Assembly)
        Assert.Equal(typeof<FiniteCustodySessionAdapter>.Assembly, writeState.GetType().Assembly)
        Assert.Equal("ports@194-1", readState.GetType().Name)
        Assert.Equal("ports@198-3", writeState.GetType().Name)
        Assert.Same(field "channels" ports.Read, field "channels" readState)
        Assert.Same(field "channels" ports.Write, field "channels" writeState)
        let readChannels = field "channels" readState :?> FiniteCustodyInertChannels
        let writeChannels = field "channels" writeState :?> FiniteCustodyInertChannels
        Assert.Same(child, readChannels.ChildResults)
        Assert.Same(control, writeChannels.ChildControl)
        Assert.Same(ingress.Array, field "buffer" readState)
        let sourceAck = field "bytes" writeState :?> ReadOnlyMemory<byte>
        let mutable sourceAckBacking = Unchecked.defaultof<ArraySegment<byte>>
        Assert.True(MemoryMarshal.TryGetArray<byte>(sourceAck, &sourceAckBacking))
        Assert.Same(ack.Array, sourceAckBacking.Array)
        Assert.Equal(write.Bytes.Length, sourceAck.Length)
        Assert.Equal(ack.Offset, sourceAckBacking.Offset)
        Assert.Same(read.Work.Value, field "m_task" (field "awaiter" readState))
        Assert.Same(write.Work.Value, field "m_task" (field "awaiter1" writeState))
        let resultParser = field "input" (field "protocol" (field "session" session))
        Assert.NotSame(ingress.Array, resultParser)
        Assert.NotSame(ack.Array, resultParser)
        Assert.Equal(9, captures.Length)
        let endpointArrays = HashSet<obj>(ReferenceEqualityComparer.Instance)
        for alias, copy in captures do
            let mutable request = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(alias, &request))
            Assert.True(endpointArrays.Add request.Array)
            Assert.True(endpointArrays.Add copy)
        Assert.Equal(18, endpointArrays.Count)
        Assert.False(endpointArrays.Contains ack.Array)
        Assert.False(endpointArrays.Contains ingress.Array)
        Assert.False((readContinuation :?> Task).IsCompleted)
        Assert.False((writeContinuation :?> Task).IsCompleted)
        Assert.Equal(1, child.Queued)
        Assert.Same(read, child.PendingRead.Value)
        Assert.Same(write, control.PendingWrite.Value)
        Assert.False(read.Work.Value.IsCompleted)
        Assert.False(write.Work.Value.IsCompleted)
        Assert.Equal(2, session.ConfirmedAckOrdinal)
        printfn "ack-read-managed-source writeCall=%d readCall=%d pendingAckOrdinal=4 confirmedAckOrdinal=2 ackBackingBytes=%d childReadBackingBytes=%d exactAwaiterJoins=true distinctFromResultParserAndEndpointArrays=true" write.Number read.Number ack.Array.Length ingress.Array.Length

    // Exact named roots only. The two generated state names were observed in the
    // genuine held-cut source test; runtime/compiler structure drift refuses.
    let private callbackReadCensus (session: FiniteCustodySessionAdapter) (responses: ObservingInput) (stdout: Stream) (selected: HashSet<obj>) (captures: (ReadOnlyMemory<byte> * byte array) array) = task {
        let field name (value: obj) =
            Assert.NotNull value
            let mutable kind = value.GetType()
            let mutable found: FieldInfo = null
            let mutable layers = 0
            while isNull found && not (isNull kind) do
                layers <- layers + 1
                Assert.InRange(layers, 1, 8)
                found <- kind.GetField(name, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly)
                kind <- kind.BaseType
            Assert.NotNull found
            found.GetValue value
        let readyRead () =
            match responses.PendingRead with
            | Some read when read.Work.IsSome ->
                not read.Work.Value.IsCompleted && not read.Underlying.IsCompleted
                && not (isNull (field "m_continuationObject" read.Work.Value))
                && not (isNull (field "m_continuationObject" read.Underlying))
            | _ -> false
        let wait = Stopwatch.StartNew()
        while not (readyRead ()) && wait.Elapsed < limit do do! Task.Delay 1
        Assert.True(readyRead (), "callback read must be pending with registered continuations")
        let read = responses.PendingRead.Value
        Assert.False(read.Work.Value.IsCompleted)
        Assert.False(read.Underlying.IsCompleted)
        Assert.NotSame(read.Work.Value, read.Underlying)
        Assert.Equal(CancellationToken.None, read.Token)
        Assert.Same(stdout, responses.Target)
        let mutable backing = Unchecked.defaultof<ArraySegment<byte>>
        Assert.True(MemoryMarshal.TryGetArray<byte>(Memory<byte>.op_Implicit read.Buffer, &backing))
        Assert.Equal(1, read.Buffer.Length)
        Assert.Equal(1, backing.Array.Length)
        Assert.Equal(0, backing.Offset)
        let continuation = field "m_continuationObject" read.Work.Value
        let state = field "StateMachine" continuation
        Assert.Equal(typeof<FiniteCustodySessionAdapter>.Assembly, state.GetType().Assembly)
        Assert.Equal("work@88-2", state.GetType().Name)
        let bridge = field "bridge" session
        Assert.Same(bridge, field "this" state)
        let channels = field "channels" bridge :?> FiniteCustodyInertChannels
        Assert.Same(responses, channels.OwnerResponses)
        Assert.Same(stdout, field "target" responses)
        Assert.Same(backing.Array, field "buffer" state)
        Assert.Same(read.Work.Value, field "m_task" (field "awaiter" state))
        let underlyingContinuation = field "m_continuationObject" read.Underlying
        let wrapperState = field "StateMachine" underlyingContinuation
        Assert.Equal(typeof<ObservingInput>.Assembly, wrapperState.GetType().Assembly)
        Assert.Equal("work@110-2", wrapperState.GetType().Name)
        Assert.Same(responses, field "_" wrapperState)
        Assert.Same(read.Underlying, field "underlying" wrapperState)
        Assert.Same(read.Underlying, field "m_task" (field "awaiter" wrapperState))
        let callbackReader = field "reader" bridge :?> Task option
        Assert.True(callbackReader.IsSome)
        Assert.False(callbackReader.Value.IsCompleted)
        Assert.Contains(callbackReader.Value, session.RetainedTasks)
        Assert.False((continuation :?> Task).IsCompleted)
        Assert.False((underlyingContinuation :?> Task).IsCompleted)
        Assert.Equal(11, selected.Count)
        Assert.False(selected.Contains backing.Array)
        let endpoints = HashSet<obj>(ReferenceEqualityComparer.Instance)
        for alias, copy in captures do
            let mutable request = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(alias, &request))
            Assert.True(endpoints.Add request.Array)
            Assert.True(endpoints.Add copy)
        Assert.Equal(26, endpoints.Count)
        Assert.False(endpoints.Contains backing.Array)
        Assert.Same(read, responses.PendingRead.Value)
        Assert.False(read.Work.Value.IsCompleted)
        Assert.False(read.Underlying.IsCompleted)
        printfn "terminal-cut-callback-read-source backingCapacityBytes=%d memoryLength=%d offset=%d exactAwaiterJoins=true distinctFromSelected11AndEndpoint26=true" backing.Array.Length read.Buffer.Length backing.Offset }

    // Observe only the selected Process/text-adapter fields. No read, flush,
    // encoding or collection is performed to expose or populate these buffers.
    let private stdioCensus (owner: Process) (diagnostic: Task<string>) (requests: CapturingOutput) (responses: ObservingInput) (selected: HashSet<obj>) (captures: (ReadOnlyMemory<byte> * byte array) array) =
        let field name (value: obj) =
            Assert.NotNull value
            let found = value.GetType().GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic)
            Assert.NotNull found
            found.GetValue value
        Assert.IsType<Process>(owner) |> ignore
        let input = Assert.IsType<StreamWriter>(field "_standardInput" owner)
        let output = Assert.IsType<StreamReader>(field "_standardOutput" owner)
        let error = Assert.IsType<StreamReader>(field "_standardError" owner)
        Assert.Same(owner.StandardInput, input)
        Assert.Same(owner.StandardOutput, output)
        Assert.Same(owner.StandardError, error)
        Assert.NotSame(output, error)
        Assert.Same(requests.Target, field "_stream" input)
        Assert.Same(responses.Target, field "_stream" output)
        Assert.Same(owner.StandardError.BaseStream, field "_stream" error)
        Assert.NotSame(field "_stream" input, field "_stream" output)
        Assert.NotSame(field "_stream" output, field "_stream" error)
        Assert.NotSame(field "_stream" input, field "_stream" error)
        Assert.False(diagnostic.IsCompleted)
        Assert.Same(diagnostic, field "_asyncReadTask" error)
        let read = responses.PendingRead.Value
        Assert.False(read.Work.Value.IsCompleted)
        Assert.False(read.Underlying.IsCompleted)
        let mutable callback = Unchecked.defaultof<ArraySegment<byte>>
        Assert.True(MemoryMarshal.TryGetArray<byte>(Memory<byte>.op_Implicit read.Buffer, &callback))
        Assert.Equal(1, callback.Array.Length)
        Assert.Equal(11, selected.Count)
        let existing = HashSet<obj>(selected, ReferenceEqualityComparer.Instance)
        Assert.True(existing.Add callback.Array)
        for alias, copy in captures do
            let mutable request = Unchecked.defaultof<ArraySegment<byte>>
            Assert.True(MemoryMarshal.TryGetArray<byte>(alias, &request))
            Assert.True(existing.Add request.Array)
            Assert.True(existing.Add copy)
        Assert.Equal(38, existing.Count)
        let observed = HashSet<obj>(ReferenceEqualityComparer.Instance)
        let add value =
            Assert.False(existing.Contains value, "stdio backing must not alias prior selected/callback/endpoint backings")
            Assert.True(observed.Add value, "stdio buffers must be distinct between adapters and fields")
        let rows = [| "stdin", "protocol", input :> obj, 4096
                      "stdout", "protocol", output :> obj, 4097
                      "stderr", "diagnostic", error :> obj, 4097 |]
        Assert.Equal(3, rows.Length)
        let mutable selectedFields = 0
        for name, ownership, adapter, expectedChars in rows do
            let byteBuffer = field "_byteBuffer" adapter
            let charBuffer = Assert.IsType<char array>(field "_charBuffer" adapter)
            selectedFields <- selectedFields + 2
            Assert.Same(field "_charBuffer" adapter, charBuffer)
            Assert.Equal(expectedChars, charBuffer.Length)
            add charBuffer
            let byteCapacity =
                if name = "stdin" then
                    Assert.Null byteBuffer // Observe the unallocated optional buffer; do not populate it.
                    None
                else
                    let bytes = Assert.IsType<byte array>(byteBuffer)
                    Assert.Same(field "_byteBuffer" adapter, bytes)
                    Assert.Equal(4096, bytes.Length)
                    add bytes
                    Some bytes.Length
            printfn "terminal-cut-stdio-source adapter=%s ownership=%s byteBufferPresent=%b byteCapacity=%d charElements=%d" name ownership byteCapacity.IsSome (defaultArg byteCapacity 0) charBuffer.Length
        Assert.Equal(6, selectedFields)
        Assert.Equal(5, observed.Count)
        Assert.Equal(10, Environment.Version.Major)
        let core = typeof<obj>.Assembly
        let processAssembly = typeof<Process>.Assembly
        Assert.Equal("System.Private.CoreLib", core.GetName().Name)
        Assert.Equal("System.Diagnostics.Process", processAssembly.GetName().Name)
        Assert.Equal(10, core.GetName().Version.Major)
        Assert.Equal(10, processAssembly.GetName().Version.Major)
        let metadata (assembly: Assembly) =
            let version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            Assert.NotNull version
            Assert.False(String.IsNullOrWhiteSpace version.InformationalVersion)
            version.InformationalVersion
        printfn "terminal-cut-stdio-runtime framework=%s version=%O coreMvid=%O processMvid=%O coreInformational=%s processInformational=%s" RuntimeInformation.FrameworkDescription Environment.Version core.ManifestModule.ModuleVersionId processAssembly.ManifestModule.ModuleVersionId (metadata core) (metadata processAssembly)
        Assert.Same(read, responses.PendingRead.Value)
        Assert.False(read.Work.Value.IsCompleted)
        Assert.False(read.Underlying.IsCompleted)
        Assert.False(diagnostic.IsCompleted)

    [<Fact>]
    let ``same execution holds Python sixth readback across managed request aliases and copies`` () = task {
        let sourceRoot = Environment.GetEnvironmentVariable "FSGG_UTEL_CALLBACK_SOURCE"
        Assert.True(not (String.IsNullOrWhiteSpace sourceRoot) && Path.IsPathFullyQualified sourceRoot, "pinned callback source required")
        let source = sourceRoot + "-slots"
        use vectors = readJson (Path.Combine(source, "tests/release-successor-authentication/slot-callback-pair-vectors.json"))
        let exchanges = vectors.RootElement.GetProperty("exchanges").EnumerateArray() |> Seq.toArray
        Assert.Equal(13, exchanges.Length)
        let trusted = PhaseCallbackProtocol.decode(Convert.FromHexString(text exchanges[0] "request"))
        let bindingBytes = Convert.FromBase64String trusted.Data.Value
        use bindingDocument = JsonDocument.Parse(ReadOnlyMemory<byte> bindingBytes)
        let bound = bindingDocument.RootElement
        let sourcePins, workflow = bound.GetProperty "source", bound.GetProperty "workflow"
        let processBinding, clockBinding, app = bound.GetProperty "process", bound.GetProperty "clock", bound.GetProperty "app"
        let bindingDigest = PhaseResultProtocol.bindingDigest(ReadOnlyMemory<byte> bindingBytes) |> Result.defaultWith failwith
        let domain: FS.GG.Execution.Contracts.ClockDomain = { Id = Guid.Parse(text clockBinding "domain"); Frequency = 1000L }
        let mutable ticks = 1L
        let at value: FS.GG.Execution.Contracts.ClockStamp = { Domain = domain; Ticks = value }
        let original = FS.GG.Execution.Contracts.Budget.create (at 0L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> failwith "fixture-budget")
        let evidence = Environment.GetEnvironmentVariable "FSGG_UTEL_CALLBACK_EVIDENCE"
        let root = Path.Combine((if String.IsNullOrWhiteSpace evidence then Path.GetTempPath() else evidence), "utel-terminal-cut-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        File.SetUnixFileMode(root, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let result = Path.Combine(root, "result.json")
        let ready = Path.Combine(root, "ready.json")
        let release = Path.Combine(root, "release.json")
        let after = Path.Combine(root, "after.json")
        let nonce = Guid.NewGuid().ToString("N")
        let descriptor: FiniteCustodyInertDescriptor =
            { Mode = "inert"
              Identity = { InvocationId = Guid.Parse(text bound "invocation"); Execution = None; LaunchOrdinal = 0UL
                           SourceDigest = text sourcePins "childClosure"; ProfileId = FiniteCustodyWindow.profileId; ProfileDigest = String.replicate 64 "f" }
              OriginalOperation = "utel-rel-21"; OriginalGeneration = 2L; DomainProfile = text bound "profile"
              Process = { Platform = "inert"; ScopeId = "in-memory-phase-fixture"; Pid = processBinding.GetProperty("pid").GetInt32(); BirthToken = text processBinding "birth" }
              ChildRevision = text sourcePins "childRevision"; RunnerRevision = text sourcePins "runnerRevision"
              RunnerClosure = text sourcePins "runnerClosure"; ContractDigest = text sourcePins "contract"; RuntimeClosure = String.replicate 64 "0"
              WorkflowRepository = workflow.GetProperty("repositoryId").GetUInt64(); WorkflowPath = text workflow "path"; WorkflowRevision = text workflow "revision"
              WorkflowRun = 0UL; WorkflowAttempt = 0u; App = app.GetProperty("id").GetUInt64(); Installation = app.GetProperty("installation").GetUInt64()
              ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]; ResultDestination = result }
        let phases = exchanges |> Array.filter (fun row ->
                         (PhaseCallbackProtocol.decode(Convert.FromHexString(text row "request"))).Kind = "validate")
                     |> Array.map (fun row ->
                         PhaseCallbackProtocol.decode(Convert.FromHexString(text row "request"))
                         |> _.Data.Value |> Convert.FromBase64String)
        Assert.Equal(6, phases.Length)
        let info = ProcessStartInfo("python3")
        info.UseShellExecute <- false
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment.Clear()
        info.Environment["PATH"] <- "/usr/bin:/bin"
        let ownerScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../eng/tests/utel-callback-fixture/terminal_cut_owner.py"))
        Assert.True(File.Exists ownerScript, "terminal cut source script required")
        for argument in ["-I"; "-S"; "-B"; ownerScript
                         source; Convert.ToBase64String bindingBytes
                         Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes clockBinding)
                         result; ready; release; after; nonce; string Environment.ProcessId] do info.ArgumentList.Add argument
        use owner = Process.Start info
        // Also covers a preparation exception before Run and its finally exist.
        use ownerLifetime =
            { new IDisposable with
                member _.Dispose() =
                    try
                        if not owner.HasExited then owner.Kill(true)
                        if not (owner.WaitForExit(5000)) then
                            eprintfn "terminal-cut-owner-retirement-incomplete"
                    with error ->
                        eprintfn "terminal-cut-owner-retirement-error=%s" (error.GetType().Name) }
        let diagnostic = owner.StandardError.ReadToEndAsync()
        let termination = TaskCompletionSource<bool option>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable confirmed = fun () -> 0
        use child = new PhaseStream(fun () -> confirmed ())
        let mutable acknowledgments = 0
        let ackHeld = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseAck = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use control = new ControlStream(fun bytes -> (task {
            use row = JsonDocument.Parse(bytes.Slice 4)
            match text row.RootElement "k" with
            | "start" -> child.Send(0, phases[0])
            | "ack" ->
                acknowledgments <- acknowledgments + 1
                match row.RootElement.GetProperty("n").GetInt32() with
                | 1 -> child.Send(1, phases[1])
                | 2 -> child.Send(2, phases[2]); child.Send(2, phases[3])
                | 4 ->
                    child.Send(4, phases[4]); child.Send(4, phases[5]); child.Complete()
                    ackHeld.TrySetResult() |> ignore
                    do! releaseAck.Task
                | _ -> failwith "fixture-ack-order"
            | _ -> failwith "fixture-control-kind" } :> Task))
        use requests = new CapturingOutput(owner.StandardInput.BaseStream)
        use responses = new ObservingInput(owner.StandardOutput.BaseStream)
        let channels: FiniteCustodyInertChannels =
            { ChildResults = child; ChildControl = control; OwnerResponses = responses; OwnerRequests = requests
              RequestRetirement = fun () -> child.Abort(); termination.TrySetResult(Some true) |> ignore; Task.FromResult true
              Termination = termination.Task }
        let clock = { new FS.GG.Execution.Contracts.IMonotonicClock with member _.Now() = at (Interlocked.Read(&ticks)) }
        let session = FiniteCustodySessionAdapter.Prepare(FiniteCustodyInertAdmission(), descriptor, descriptor, clock,
                          original, ReadOnlyMemory<byte> bindingBytes, channels, CancellationToken.None) |> Result.defaultWith failwith
        confirmed <- fun () -> session.ConfirmedAckOrdinal
        let observedExit = task {
            do! owner.WaitForExitAsync()
            termination.TrySetResult(Some true) |> ignore }
        let running = session.Run()
        let mutable primary: exn option = None
        try
            do! ackHeld.Task.WaitAsync limit
            let readWait = Stopwatch.StartNew()
            let continuationField = typeof<Task>.GetField("m_continuationObject", BindingFlags.Instance ||| BindingFlags.NonPublic)
            Assert.NotNull continuationField
            let awaiting (work: Task) = not (isNull (continuationField.GetValue work))
            let pendingCalls () =
                match child.PendingRead, control.PendingWrite with
                | Some read, Some write ->
                    read.RequiredAck = Some 4 && read.Work.IsSome && write.Work.IsSome
                    && awaiting read.Work.Value && awaiting write.Work.Value
                | _ -> false
            while not (pendingCalls ()) && readWait.Elapsed < limit do do! Task.Delay 1
            Assert.True(pendingCalls (), "actual read/write tasks must remain pending at the held third ACK")
            Assert.Equal(3, acknowledgments)
            ackReadCensus session child control bindingDigest (sha256(ReadOnlySpan<byte>(phases[3]))) requests.Captures
            releaseAck.TrySetResult() |> ignore
            do! untilFile ready owner
            use before = readJson ready
            let cut = before.RootElement
            Assert.Equal(nonce, text cut "nonce")
            Assert.Equal(bindingDigest, text cut "binding")
            Assert.Equal("c9525d5f4afaadcbb69f8f71c25aee0939b32b7c", text cut "producerRevision")
            Assert.Equal(Environment.ProcessId, cut.GetProperty("managedPid").GetInt32())
            Assert.Equal(owner.Id, cut.GetProperty("pythonPid").GetInt32())
            Assert.Equal(6, cut.GetProperty("ordinal").GetInt32())
            Assert.Equal("readback", text cut "stage")
            let objects = cut.GetProperty("objects")
            let rows = objects.EnumerateObject() |> Seq.toArray
            Assert.Equal(16, rows.Length)
            let identities = rows |> Array.map (fun row -> row.Value[0].GetInt64())
            Assert.Equal(16, identities |> Array.distinct |> Array.length)
            let lengths = rows |> Array.map (fun row -> row.Value[1].GetInt32())
            Assert.Equal(11660, Array.sum lengths) // Scoped Python source objects only.
            let requestDigest = captureDigest requests.Captures
            let selected = managedCensus session requests.Captures
            do! callbackReadCensus session responses owner.StandardOutput.BaseStream selected requests.Captures
            stdioCensus owner diagnostic requests responses selected requests.Captures
            Assert.Equal(requestDigest, text cut "request")
            // The child remains inside its sixth readback stage while the managed
            // endpoint observes the actual supplied request memories and copies.
            let releaseRecord = JsonSerializer.SerializeToUtf8Bytes(
                {| nonce = nonce; binding = bindingDigest; request = requestDigest; ordinal = 6; stage = "readback" |})
            let releaseTemp = Path.Combine(root, "release.tmp")
            use releaseFile = new FileStream(releaseTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            releaseFile.Write releaseRecord
            releaseFile.Flush true
            releaseFile.Close()
            File.Move(releaseTemp, release)
            do! untilFile after owner
            use resampled = readJson after
            Assert.Equal(nonce, text resampled.RootElement "nonce")
            Assert.Equal(requestDigest, text resampled.RootElement "request")
            Assert.Equal(Environment.ProcessId, resampled.RootElement.GetProperty("managedPid").GetInt32())
            Assert.Equal(owner.Id, resampled.RootElement.GetProperty("pythonPid").GetInt32())
            Assert.True(resampled.RootElement.GetProperty("unchanged").GetBoolean())
            Assert.Equal(cut.GetProperty("objects").GetRawText(), resampled.RootElement.GetProperty("objects").GetRawText())
            let! outcome = running.WaitAsync limit
            let outcome = outcome |> Result.defaultWith failwith
            do! observedExit.WaitAsync limit
            do! Task.WhenAll(session.RetainedTasks).WaitAsync limit
            let! errors = diagnostic
            Assert.Equal(0, owner.ExitCode)
            Assert.True(String.IsNullOrEmpty errors, "Python owner emitted diagnostics")
            Assert.True(outcome.ProtocolComplete)
            Assert.Equal(None, outcome.StopReason)
            Assert.Equal(3, acknowledgments)
            Assert.Equal(FS.GG.Execution.Contracts.Settled, session.LocalTasks)
            Assert.True(responses.PendingRead.IsNone, "settled callback observation must be cleared")
            use retained = readJson result
            let evidence = retained.RootElement
            Assert.Equal("fsgg.inert-slot-callback-test-evidence/1", text evidence "schema")
            Assert.Equal(bindingDigest, text evidence "binding")
            Assert.Equal(6, evidence.GetProperty("ordinal").GetInt32())
            Assert.True(evidence.GetProperty("terminalResponseConfirmed").GetBoolean())
            Assert.False(evidence.GetProperty("inputEofObserved").GetBoolean())
            Assert.False(evidence.GetProperty("trailingInputChecked").GetBoolean())
            Assert.False(evidence.GetProperty("operationalReceipt").GetBoolean())
            let slots = evidence.GetProperty("slots").EnumerateArray() |> Seq.toArray
            Assert.Equal(6, slots.Length)
            for index in 0..5 do
                Assert.Equal(index + 1, slots[index].GetProperty("ordinal").GetInt32())
                Assert.Equal<byte>(phases[index], Convert.FromHexString(text slots[index] "bytes"))
                Assert.Equal(sha256(ReadOnlySpan<byte>(phases[index])), text slots[index] "sha256")
        with error -> primary <- Some error
        let cleanupErrors = ResizeArray<string>()
        let cleanup name action =
            try action()
            with error -> cleanupErrors.Add(name + ":" + error.GetType().Name)
        cleanup "clock" (fun () -> Interlocked.Exchange(&ticks, 300000L) |> ignore)
        cleanup "ack-hold" (fun () -> releaseAck.TrySetResult() |> ignore)
        cleanup "child" child.Abort
        cleanup "termination" (fun () -> termination.TrySetResult(Some true) |> ignore)
        cleanup "python" (fun () ->
            if not owner.HasExited then owner.Kill(true)
            Assert.True(owner.WaitForExit(5000), "terminal cut owner retirement"))
        cleanup "session" (fun () -> running.WaitAsync(limit).GetAwaiter().GetResult() |> ignore)
        cleanup "retained" (fun () -> Task.WhenAll(session.RetainedTasks).WaitAsync(limit).GetAwaiter().GetResult())
        cleanup "diagnostic" (fun () -> diagnostic.WaitAsync(limit).GetAwaiter().GetResult() |> ignore)
        cleanup "exit" (fun () -> observedExit.WaitAsync(limit).GetAwaiter().GetResult())
        cleanup "callback-observation" (fun () -> Assert.True(responses.PendingRead.IsNone))
        if String.IsNullOrWhiteSpace evidence then
            cleanup "directory" (fun () -> Directory.Delete(root, true))
        if cleanupErrors.Count > 0 then
            eprintfn "terminal-cut-cleanup-errors=%s" (String.Join(",", cleanupErrors))
        match primary with
        | Some error -> ExceptionDispatchInfo.Capture(error).Throw()
        | None -> Assert.Empty(cleanupErrors)
    }
