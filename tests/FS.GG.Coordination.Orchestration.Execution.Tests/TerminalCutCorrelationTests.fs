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

    type private PhaseStream(confirmed: unit -> int) =
        inherit MemoryStream()
        let input = Channel.CreateBounded<int * byte array>(6)
        let mutable aborted = 0
        member _.Send(required, bytes) = Assert.True(input.Writer.TryWrite((required, bytes)))
        member _.Complete() = input.Writer.TryComplete() |> ignore
        member _.Abort() = Interlocked.Exchange(&aborted, 1) |> ignore; input.Writer.TryComplete() |> ignore
        override _.ReadAsync(buffer: Memory<byte>, _: CancellationToken) = ValueTask<int>(task {
            let! available = input.Reader.WaitToReadAsync().AsTask()
            if not available then return 0
            else
                let! required, bytes = input.Reader.ReadAsync().AsTask()
                while Volatile.Read(&aborted) = 0 && confirmed () < required do do! Task.Delay 1
                if Volatile.Read(&aborted) <> 0 then return 0
                else
                    bytes.AsMemory().CopyTo buffer
                    return bytes.Length })

    type private ControlStream(onWrite: ReadOnlyMemory<byte> -> unit) =
        inherit MemoryStream()
        override _.WriteAsync(bytes: ReadOnlyMemory<byte>, _: CancellationToken) =
            onWrite bytes
            ValueTask.CompletedTask

    type private CapturingOutput(target: Stream) =
        inherit Stream()
        let gate = obj()
        let captures = ResizeArray<ReadOnlyMemory<byte> * byte array>()
        member _.Captures = lock gate (fun () -> captures.ToArray())
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
        use control = new ControlStream(fun bytes ->
            use row = JsonDocument.Parse(bytes.Slice 4)
            match text row.RootElement "k" with
            | "start" -> child.Send(0, phases[0])
            | "ack" ->
                acknowledgments <- acknowledgments + 1
                match row.RootElement.GetProperty("n").GetInt32() with
                | 1 -> child.Send(1, phases[1])
                | 2 -> child.Send(2, phases[2]); child.Send(2, phases[3])
                | 4 -> child.Send(4, phases[4]); child.Send(4, phases[5]); child.Complete()
                | _ -> failwith "fixture-ack-order"
            | _ -> failwith "fixture-control-kind")
        use requests = new CapturingOutput(owner.StandardInput.BaseStream)
        let channels: FiniteCustodyInertChannels =
            { ChildResults = child; ChildControl = control; OwnerResponses = owner.StandardOutput.BaseStream; OwnerRequests = requests
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
            do! untilFile ready owner
            use before = readJson ready
            let cut = before.RootElement
            Assert.Equal(nonce, text cut "nonce")
            Assert.Equal(bindingDigest, text cut "binding")
            Assert.Equal("d84e66bc2eafc44b6f53e7ef0b20ed6f1477cc95", text cut "producerRevision")
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
            managedCensus session requests.Captures
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
        cleanup "child" child.Abort
        cleanup "termination" (fun () -> termination.TrySetResult(Some true) |> ignore)
        cleanup "python" (fun () ->
            if not owner.HasExited then owner.Kill(true)
            Assert.True(owner.WaitForExit(5000), "terminal cut owner retirement"))
        cleanup "session" (fun () -> running.WaitAsync(limit).GetAwaiter().GetResult() |> ignore)
        cleanup "retained" (fun () -> Task.WhenAll(session.RetainedTasks).WaitAsync(limit).GetAwaiter().GetResult())
        cleanup "diagnostic" (fun () -> diagnostic.WaitAsync(limit).GetAwaiter().GetResult() |> ignore)
        cleanup "exit" (fun () -> observedExit.WaitAsync(limit).GetAwaiter().GetResult())
        if String.IsNullOrWhiteSpace evidence then
            cleanup "directory" (fun () -> Directory.Delete(root, true))
        if cleanupErrors.Count > 0 then
            eprintfn "terminal-cut-cleanup-errors=%s" (String.Join(",", cleanupErrors))
        match primary with
        | Some error -> ExceptionDispatchInfo.Capture(error).Throw()
        | None -> Assert.Empty(cleanupErrors)
    }
