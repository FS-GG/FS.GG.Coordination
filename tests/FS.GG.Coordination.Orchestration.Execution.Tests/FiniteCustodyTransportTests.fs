namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.Text
open Xunit
open FS.GG.Coordination.Orchestration.Execution

/// Component vectors. The hosted paired Python/supervisor journey is a separate
/// required gate; these reducer controls do not establish that integration.
module FiniteCustodyTransportTests =
    let private invocation = Guid.Parse "00000000-0000-0000-0000-000000000001"
    let private binding = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes "{\"fixture\":true}")
    let private reply kind bytes =
        let request = PhaseCallbackProtocol.decode bytes
        PhaseCallbackProtocol.encode { request with Kind = kind; Data = None }
    let private throws action = Assert.ThrowsAny<Exception>(Action action) |> ignore
    let private bound () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        session.ConfirmWrite request.Length
        Assert.True(session.Feed(ReadOnlyMemory<byte>(reply "bound" request)))
        session.TakeResponse() |> ignore
        session
    let private snapshot (session: PhaseCallbackProtocol.Session) ordinal kind =
        let frame = Encoding.ASCII.GetBytes(sprintf "frame-%d-%s" ordinal kind)
        PhaseResultSnapshot(session.Binding, ordinal, kind, frame, Encoding.ASCII.GetBytes "{}", false)

    [<Fact>]
    let ``callback bind owns canonical bytes and roundtrips exact schema`` () =
        let bytes = binding.ToArray()
        let session = PhaseCallbackProtocol.Session(invocation, ReadOnlyMemory<byte> bytes)
        bytes[0] <- 0uy
        let request = session.Bind()
        let decoded = PhaseCallbackProtocol.decode request
        Assert.Equal(binding.ToArray(), Convert.FromBase64String decoded.Data.Value)
        Assert.Equal(request, PhaseCallbackProtocol.encode decoded)

    [<Fact>]
    let ``callback fast reply remains provisional until complete write`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        Assert.True(session.Feed(ReadOnlyMemory<byte>(reply "bound" request)))
        throws (fun () -> session.TakeResponse() |> ignore)
        Assert.True session.Refused

    [<Fact>]
    let ``callback byte fragmented response requires exact identity`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        session.ConfirmWrite request.Length
        let response = reply "bound" request
        for index = 0 to response.Length - 1 do
            Assert.Equal(index = response.Length - 1, session.Feed(ReadOnlyMemory<byte>(response, index, 1)))
        Assert.Equal("bound", session.TakeResponse().Kind)

    [<Fact>]
    let ``callback second response cannot wait for a future request`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        session.ConfirmWrite request.Length
        let response = reply "bound" request
        Assert.True(session.Feed(ReadOnlyMemory<byte> response))
        throws (fun () -> session.Feed(ReadOnlyMemory<byte> response) |> ignore)
        Assert.True session.Refused

    [<Fact>]
    let ``callback partial write never accepts matching response`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        Assert.True(session.Feed(ReadOnlyMemory<byte>(reply "bound" request)))
        throws (fun () -> session.ConfirmWrite(request.Length - 1))
        throws (fun () -> session.TakeResponse() |> ignore)

    [<Fact>]
    let ``callback retain cannot precede validated response`` () =
        let session = bound ()
        throws (fun () -> session.Retain(snapshot session 1 "ready") |> ignore)
        Assert.True session.Refused

    [<Fact>]
    let ``callback validate retain identities are immutable and terminal closes channel`` () =
        let session = bound ()
        for ordinal, kind in [(1, "ready"); (2, "terminal")] do
            let value = snapshot session ordinal kind
            let validation = session.Validate value
            session.ConfirmWrite validation.Length
            Assert.True(session.Feed(ReadOnlyMemory<byte>(reply "validated" validation)))
            session.TakeResponse() |> ignore
            let retention = session.Retain value
            let row = PhaseCallbackProtocol.decode retention
            Assert.True row.Data.IsNone
            session.ConfirmWrite retention.Length
            Assert.True(session.Feed(ReadOnlyMemory<byte>(reply "retained" retention)))
            Assert.Equal(value.Digest, session.TakeResponse().FrameDigest)
        throws (fun () -> session.Validate(snapshot session 3 "terminal") |> ignore)

    [<Fact>]
    let ``callback forged retained response cannot validate`` () =
        let session = bound ()
        let request = session.Validate(snapshot session 1 "ready")
        session.ConfirmWrite request.Length
        throws (fun () -> session.Feed(ReadOnlyMemory<byte>(reply "retained" request)) |> ignore)

    [<Fact>]
    let ``callback oversized response prefix refuses without body allocation`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        session.Bind() |> ignore
        throws (fun () -> session.Feed(ReadOnlyMemory<byte>([|0uy;0uy;2uy;0uy|])) |> ignore)
        Assert.Equal(4L, session.ObservedIncomingBytes)

    [<Fact>]
    let ``callback canonical encoding rejects boolean sequence and duplicate keys`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        let body = Encoding.ASCII.GetString(request, 4, request.Length - 4)
        for altered in [body.Replace("\"sequence\":1", "\"sequence\":true"); body.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1")] do
            let payload = Encoding.ASCII.GetBytes altered
            let frame = Array.zeroCreate<byte> (payload.Length + 4)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), uint32 payload.Length)
            payload.CopyTo(frame, 4)
            throws (fun () -> PhaseCallbackProtocol.decode frame |> ignore)

    [<Fact>]
    let ``callback terminal reserves only its remaining exchanges at exact capacity`` () =
        for requestBytes, retaining, expected in [(4096, false, 5632); (512, true, 1024)] do
            Assert.Equal(expected, PhaseCallbackProtocol.requiredCapacity requestBytes true retaining)
            Assert.True(PhaseCallbackProtocol.canAdmit expected requestBytes true retaining)
            Assert.False(PhaseCallbackProtocol.canAdmit (expected - 1) requestBytes true retaining)
        Assert.Equal(10240, PhaseCallbackProtocol.requiredCapacity 4096 false false)

    [<Fact>]
    let ``callback complete history uses thirteen requests without exceeding aggregate`` () =
        let session = bound ()
        for ordinal, kind in List.indexed ["ready"; "issuance-possible"; "token-owned"; "revocation-possible"; "revoked"; "terminal"] do
            let value = snapshot session (ordinal + 1) kind
            let request = session.Validate value
            session.ConfirmWrite request.Length
            session.Feed(ReadOnlyMemory<byte>(reply "validated" request)) |> ignore
            session.TakeResponse() |> ignore
            let request = session.Retain value
            session.ConfirmWrite request.Length
            session.Feed(ReadOnlyMemory<byte>(reply "retained" request)) |> ignore
            Assert.Equal((ordinal + 1) * 2 + 1, session.TakeResponse().Sequence)
        Assert.True(session.ChargedBytes <= PhaseCallbackProtocol.maximumAggregateBytes)

    [<Fact>]
    let ``callback retain rejects replacement snapshot even with identical identity`` () =
        let session = bound ()
        let request = session.Validate(snapshot session 1 "ready")
        session.ConfirmWrite request.Length
        session.Feed(ReadOnlyMemory<byte>(reply "validated" request)) |> ignore
        session.TakeResponse() |> ignore
        throws (fun () -> session.Retain(snapshot session 1 "ready") |> ignore)

    [<Fact>]
    let ``callback wrong invocation response cannot qualify current pending request`` () =
        let session = PhaseCallbackProtocol.Session(invocation, binding)
        let request = session.Bind()
        session.ConfirmWrite request.Length
        let original = PhaseCallbackProtocol.decode (reply "bound" request)
        let response = PhaseCallbackProtocol.encode { original with Invocation = Guid.NewGuid() }
        throws (fun () -> session.Feed(ReadOnlyMemory<byte> response) |> ignore)

    [<Fact>]
    let ``callable native selector refuses before clock transport or retirement`` () =
        let descriptor: FiniteCustodyInertDescriptor =
            { Mode = "native"; Identity = Unchecked.defaultof<_>
              OriginalOperation = "utel-rel-21"; OriginalGeneration = 2L; DomainProfile = "held"
              Process = Unchecked.defaultof<_>; ChildRevision = ""; RunnerRevision = ""
              RunnerClosure = ""; ContractDigest = ""; RuntimeClosure = ""
              WorkflowRepository = 0UL; WorkflowPath = ""; WorkflowRevision = ""
              WorkflowRun = 0UL; WorkflowAttempt = 0u; App = 0UL; Installation = 0UL
              ChannelRoles = []; ResultDestination = "" }
        let mutable touched = false
        let clock = { new FS.GG.Execution.Contracts.IMonotonicClock with
                        member _.Now() = touched <- true; failwith "native-clock-must-not-run" }
        let result = FiniteCustodySessionAdapter.Prepare(FiniteCustodyInertAdmission(), descriptor, descriptor,
                        clock, Unchecked.defaultof<_>, binding, Unchecked.defaultof<_>, System.Threading.CancellationToken.None)
        Assert.True(Result.isError result)
        Assert.False touched

    [<Fact>]
    let ``callback bridge keeps typed deadline cancellation and clock refusal before send`` () =
        let domain: FS.GG.Execution.Contracts.ClockDomain = { Id = Guid.NewGuid(); Frequency = 1000L }
        let at ticks: FS.GG.Execution.Contracts.ClockStamp = { Domain = domain; Ticks = ticks }
        let original = FS.GG.Execution.Contracts.Budget.create (at 0L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> failwith "fixture-budget")
        let window = FiniteCustodyWindow.prepare (at 0L) original |> Result.defaultWith (fun _ -> failwith "fixture-window")
        for normal, ticks, cancel, expected in [(true, 270000L, false, FS.GG.Execution.Contracts.WorkDeadline); (false, 285000L, false, FS.GG.Execution.Contracts.WorkDeadline); (false, 1L, true, FS.GG.Execution.Contracts.CallerCancellation); (false, -1L, false, FS.GG.Execution.Contracts.OwnerLost)] do
            use cancellation = new System.Threading.CancellationTokenSource()
            if cancel then cancellation.Cancel()
            let clock = { new FS.GG.Execution.Contracts.IMonotonicClock with member _.Now() = at ticks }
            let endpoint = FiniteCustodyCallbackBridge.Endpoint(clock, window, invocation, binding, Unchecked.defaultof<_>, cancellation.Token)
            throws (fun () -> endpoint.Checkpoint normal)
            Assert.Equal(Some expected, endpoint.StopReason)
            cancellation.Cancel()
            throws (fun () -> endpoint.Checkpoint normal)
            Assert.Equal(Some expected, endpoint.StopReason)
            Assert.Empty endpoint.Tasks

    type private CallbackResponseStream(bytes: byte array, requestEntered: System.Threading.Tasks.Task, completed: unit -> unit) =
        inherit System.IO.MemoryStream(bytes)
        let settled = System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        member _.Finish() = settled.TrySetResult 0 |> ignore
        override this.ReadAsync(buffer: Memory<byte>, _: System.Threading.CancellationToken) =
            if this.Position = this.Length then System.Threading.Tasks.ValueTask<int>(settled.Task)
            else System.Threading.Tasks.ValueTask<int>(task {
                do! requestEntered
                let count = this.Read buffer.Span
                if this.Position = this.Length then completed ()
                return count })

    type private CallbackRequestStream(entered: System.Threading.Tasks.TaskCompletionSource<unit>) =
        inherit System.IO.MemoryStream()
        override _.WriteAsync(_: ReadOnlyMemory<byte>, _: System.Threading.CancellationToken) =
            entered.TrySetResult() |> ignore
            System.Threading.Tasks.ValueTask.CompletedTask

    [<Fact>]
    let ``callback response boundary preserves typed stop and reader custody after full write`` () = task {
        let domain: FS.GG.Execution.Contracts.ClockDomain = { Id = Guid.NewGuid(); Frequency = 1000L }
        let at ticks: FS.GG.Execution.Contracts.ClockStamp = { Domain = domain; Ticks = ticks }
        let original = FS.GG.Execution.Contracts.Budget.create (at 0L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> failwith "fixture-budget")
        let window = FiniteCustodyWindow.prepare (at 0L) original |> Result.defaultWith (fun _ -> failwith "fixture-window")
        for cancel in [false; true] do
            let mutable ticks = 0L
            use cancellation = new System.Threading.CancellationTokenSource()
            let entered = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            let wire = PhaseCallbackProtocol.Session(invocation, binding)
            let response = reply "bound" (wire.Bind())
            use responses = new CallbackResponseStream(response, entered.Task, fun () -> if cancel then cancellation.Cancel() else ticks <- 270000L)
            use requests = new CallbackRequestStream(entered)
            let channels: FiniteCustodyInertChannels =
                { ChildResults = System.IO.Stream.Null; ChildControl = System.IO.Stream.Null
                  OwnerResponses = responses; OwnerRequests = requests
                  RequestRetirement = fun () -> System.Threading.Tasks.Task.FromResult false
                  Termination = System.Threading.Tasks.Task.FromResult(None: bool option) }
            let clock = { new FS.GG.Execution.Contracts.IMonotonicClock with member _.Now() = at ticks }
            let endpoint = FiniteCustodyCallbackBridge.Endpoint(clock, window, invocation, binding, channels, cancellation.Token)
            try
                let! result = endpoint.Bind().WaitAsync(TimeSpan.FromSeconds 2.)
                Assert.True(Result.isError result)
                Assert.Equal(Some(if cancel then FS.GG.Execution.Contracts.CallerCancellation else FS.GG.Execution.Contracts.WorkDeadline), endpoint.StopReason)
                Assert.Equal(FS.GG.Execution.Contracts.Outstanding, endpoint.LocalTasks)
            finally
                responses.Finish()
                System.Threading.Tasks.Task.WhenAll(endpoint.Tasks).WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
            Assert.Equal(FS.GG.Execution.Contracts.Settled, endpoint.LocalTasks)
    }

    let private composedDomain: FS.GG.Execution.Contracts.ClockDomain = { Id = Guid.Parse "7c287080-68e9-4846-b369-cb46f37ec374"; Frequency = 1000L }
    let private composedDescriptor: FiniteCustodyInertDescriptor =
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

    let private composedBindingText =
        sprintf """{"app":{"id":4882140,"installation":123},"clock":{"domain":"%O","frequency":"1000","normal":"271000","start":"1000","total":"301000","work":"286000"},"invocation":"%O","mode":"inert","operation":"utel-rel-21","originalGeneration":2,"process":{"birth":"abc","pid":1},"profile":"%s","source":{"childClosure":"%s","childRevision":"%s","contract":"%s","runnerClosure":"%s","runnerRevision":"%s"},"workflow":{"attempt":0,"path":".github/workflows/release-successor-publish.yml","repositoryId":1269292704,"revision":"%s","run":0}}"""
            composedDomain.Id composedDescriptor.Identity.InvocationId composedDescriptor.DomainProfile composedDescriptor.Identity.SourceDigest
            composedDescriptor.ChildRevision composedDescriptor.ContractDigest composedDescriptor.RunnerClosure composedDescriptor.RunnerRevision composedDescriptor.WorkflowRevision

    [<Fact>]
    let ``composition preserves typed callback failure before its next supervisor sample`` () = task {
        let at ticks: FS.GG.Execution.Contracts.ClockStamp = { Domain = composedDomain; Ticks = ticks }
        let original = FS.GG.Execution.Contracts.Budget.create (at 1000L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> failwith "fixture-budget")
        let canonical = ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes composedBindingText)
        let digest = PhaseResultProtocol.bindingDigest canonical |> Result.defaultWith failwith
        let body = Encoding.ASCII.GetBytes(sprintf "{\"b\":\"%s\",\"k\":\"ready\",\"n\":1,\"p\":{\"binding\":%s},\"v\":\"%s\"}" digest composedBindingText PhaseResultProtocol.identifier)
        let frame = Array.zeroCreate<byte> (body.Length + 4)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), uint32 body.Length)
        body.CopyTo(frame, 4)
        for cause in [FS.GG.Execution.Contracts.WorkDeadline; FS.GG.Execution.Contracts.CallerCancellation; FS.GG.Execution.Contracts.OwnerLost] do
            for callbackError in [false; true] do
                let mutable observed = None
                let mutable read = false
                let mutable retains = 0
                let mutable writes = 0
                let termination = System.Threading.Tasks.TaskCompletionSource<bool option>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
                let ports: FiniteCustodyComposition.Ports =
                    { Read = fun () ->
                          let bytes =
                              if read then ReadOnlyMemory<byte>.Empty
                              else
                                  read <- true
                                  ReadOnlyMemory<byte> frame
                          System.Threading.Tasks.Task.FromResult bytes
                      Write = fun bytes -> writes <- writes + 1; System.Threading.Tasks.Task.FromResult bytes.Length
                      Validate = fun _ -> observed <- Some cause; if callbackError then Error "fixture-callback-refused" else Ok ()
                      PersistAndReadback = fun snapshot ->
                          retains <- retains + 1
                          System.Threading.Tasks.Task.FromResult(Ok { Binding = snapshot.Binding; Ordinal = snapshot.Ordinal; FrameDigest = snapshot.Digest })
                      RequestRetirement = fun () -> termination.TrySetResult(Some true) |> ignore; System.Threading.Tasks.Task.FromResult true
                      Termination = termination.Task }
                let clock = { new FS.GG.Execution.Contracts.IMonotonicClock with member _.Now() = at 1000L }
                let session = FiniteCustodyComposition.Session.Prepare(FiniteCustodyComposition.Admission(), composedDescriptor, composedDescriptor,
                                  clock, original, canonical, ports, System.Threading.CancellationToken.None, channelStop = (fun () -> observed)) |> Result.defaultWith failwith
                let! outcome = session.Run().WaitAsync(TimeSpan.FromSeconds 2.)
                let outcome = outcome |> Result.defaultWith failwith
                Assert.Equal(Some cause, outcome.StopReason)
                Assert.Equal(0, retains)
                Assert.Equal(1, writes)
                Assert.Equal(FS.GG.Execution.Contracts.Settled, session.LocalTasks)
    }

    type private InMemoryPhaseStream(confirmed: unit -> int) =
        inherit System.IO.MemoryStream()
        let input = System.Threading.Channels.Channel.CreateUnbounded<int * ReadOnlyMemory<byte>>()
        let mutable aborted = false
        member _.Send(required, bytes: byte array) = Assert.True(input.Writer.TryWrite((required, ReadOnlyMemory<byte> bytes)))
        member _.Abort() = aborted <- true; input.Writer.TryComplete() |> ignore
        member _.Complete() = input.Writer.TryComplete() |> ignore
        override _.ReadAsync(buffer: Memory<byte>, _: System.Threading.CancellationToken) =
            System.Threading.Tasks.ValueTask<int>(task {
                let! available = input.Reader.WaitToReadAsync().AsTask()
                if not available then return 0
                else
                    let! required, bytes = input.Reader.ReadAsync().AsTask()
                    while not aborted && confirmed () < required do do! System.Threading.Tasks.Task.Delay 1
                    if aborted then return 0
                    else
                        bytes.CopyTo buffer
                        return bytes.Length })

    type private InMemoryControlStream(onWrite: ReadOnlyMemory<byte> -> unit) =
        inherit System.IO.MemoryStream()
        override _.WriteAsync(bytes: ReadOnlyMemory<byte>, _: System.Threading.CancellationToken) =
            onWrite bytes
            System.Threading.Tasks.ValueTask.CompletedTask

    [<Fact>]
    let ``hosted callback pair uses actual Python sink with in memory child streams only`` () = task {
        // The gate supplies an immutable pinned owner checkout. This fixture has
        // two processes: this test supervisor and a callback-only Python owner.
        // No authentication child, FD provisioner or native custody is exercised.
        let source = Environment.GetEnvironmentVariable "FSGG_UTEL_CALLBACK_SOURCE"
        Assert.True(not (String.IsNullOrWhiteSpace source) && System.IO.Path.IsPathFullyQualified source, "pinned callback source required")
        use vectors = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(System.IO.File.ReadAllBytes(System.IO.Path.Combine(source, "tests/release-successor-authentication/callback-vectors.json"))))
        let exchanges = vectors.RootElement.GetProperty("exchanges").EnumerateArray() |> Seq.toArray
        let trustedBind = PhaseCallbackProtocol.decode (Convert.FromHexString(exchanges[0].GetProperty("request").GetString()))
        let bindingBytes = Convert.FromBase64String trustedBind.Data.Value
        use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte> bindingBytes)
        let bound = document.RootElement
        let text (element: System.Text.Json.JsonElement) name = element.GetProperty(name: string).GetString()
        let sourcePins = bound.GetProperty "source"
        let workflow = bound.GetProperty "workflow"
        let processBinding = bound.GetProperty "process"
        let clockBinding = bound.GetProperty "clock"
        let app = bound.GetProperty "app"
        let domain: FS.GG.Execution.Contracts.ClockDomain = { Id = Guid.Parse(text clockBinding "domain"); Frequency = 1000L }
        let mutable ticks = 1L
        let at value: FS.GG.Execution.Contracts.ClockStamp = { Domain = domain; Ticks = value }
        let original = FS.GG.Execution.Contracts.Budget.create (at 0L) (TimeSpan.FromSeconds 300.) (TimeSpan.FromSeconds 15.) |> Result.defaultWith (fun _ -> failwith "fixture-budget")
        let evidence = Environment.GetEnvironmentVariable "FSGG_UTEL_CALLBACK_EVIDENCE"
        let root = System.IO.Path.Combine((if String.IsNullOrWhiteSpace evidence then System.IO.Path.GetTempPath() else evidence), "utel-callback-pair-" + Guid.NewGuid().ToString("N"))
        System.IO.Directory.CreateDirectory root |> ignore
        System.IO.File.SetUnixFileMode(root, System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserWrite ||| System.IO.UnixFileMode.UserExecute)
        let resultPath = System.IO.Path.Combine(root, "callback-result.json")
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
              ChannelRoles = ["parent-start-and-ack"; "child-phase-result"]; ResultDestination = resultPath }
        let phases = exchanges |> Array.filter (fun row -> (PhaseCallbackProtocol.decode(Convert.FromHexString(row.GetProperty("request").GetString()))).Kind = "validate")
                     |> Array.map (fun row -> PhaseCallbackProtocol.decode(Convert.FromHexString(row.GetProperty("request").GetString())) |> _.Data.Value |> Convert.FromBase64String)
        let info = System.Diagnostics.ProcessStartInfo("python3")
        info.UseShellExecute <- false
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment.Clear()
        info.Environment["PATH"] <- "/usr/bin:/bin"
        for argument in ["-I"; "-S"; "-B"; System.IO.Path.Combine(source, "tests/release-successor-authentication/callback-fixture.py"); Convert.ToBase64String bindingBytes
                         Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes clockBinding); resultPath] do info.ArgumentList.Add argument
        use owner = System.Diagnostics.Process.Start info
        let diagnostic = owner.StandardError.ReadToEndAsync()
        let mutable confirmed = fun () -> 0
        use child = new InMemoryPhaseStream(fun () -> confirmed ())
        let termination = System.Threading.Tasks.TaskCompletionSource<bool option>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable acknowledgments = 0
        use control = new InMemoryControlStream(fun bytes ->
            use row = System.Text.Json.JsonDocument.Parse(bytes.Slice 4)
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
        let channels: FiniteCustodyInertChannels =
            { ChildResults = child; ChildControl = control; OwnerResponses = owner.StandardOutput.BaseStream; OwnerRequests = owner.StandardInput.BaseStream
              RequestRetirement = fun () -> child.Abort(); termination.TrySetResult(Some true) |> ignore; System.Threading.Tasks.Task.FromResult true
              Termination = termination.Task }
        let clock = { new FS.GG.Execution.Contracts.IMonotonicClock with member _.Now() = at (System.Threading.Interlocked.Read(&ticks)) }
        let session = FiniteCustodySessionAdapter.Prepare(FiniteCustodyInertAdmission(), descriptor, { descriptor with ResultDestination = resultPath }, clock,
                          original, ReadOnlyMemory<byte>(bindingBytes), channels, System.Threading.CancellationToken.None) |> Result.defaultWith failwith
        confirmed <- fun () -> session.ConfirmedAckOrdinal
        let observedExit = task {
            do! owner.WaitForExitAsync()
            termination.TrySetResult(Some true) |> ignore }
        let running = session.Run()
        try
            let! outcome = running.WaitAsync(TimeSpan.FromSeconds 20.)
            let outcome = outcome |> Result.defaultWith failwith
            do! observedExit.WaitAsync(TimeSpan.FromSeconds 5.)
            do! System.Threading.Tasks.Task.WhenAll(session.RetainedTasks).WaitAsync(TimeSpan.FromSeconds 5.)
            let! diagnostic = diagnostic
            Assert.Equal(0, owner.ExitCode)
            Assert.Equal("", diagnostic)
            Assert.True(outcome.ProtocolComplete)
            Assert.Equal(None, outcome.StopReason)
            Assert.Equal(3, acknowledgments)
            Assert.Equal(FS.GG.Execution.Contracts.Settled, session.LocalTasks)
            use retained = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(System.IO.File.ReadAllBytes resultPath))
            let projection = retained.RootElement.GetProperty "authenticationSession"
            let expected = vectors.RootElement.GetProperty "finalReadback"
            Assert.Equal(text expected "binding", text projection "binding")
            Assert.Equal(text expected "frameDigest", text projection "frameDigest")
            Assert.Equal(6, projection.GetProperty("ordinal").GetInt32())
        finally
            System.Threading.Interlocked.Exchange(&ticks, 300000L) |> ignore
            child.Abort()
            termination.TrySetResult(Some true) |> ignore
            owner.StandardInput.Close()
            if not owner.HasExited then owner.Kill(true)
            Assert.True(owner.WaitForExit(5000), "callback fixture retirement")
            running.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore
            System.Threading.Tasks.Task.WhenAll(session.RetainedTasks).WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
            diagnostic.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore
            if String.IsNullOrWhiteSpace evidence then System.IO.Directory.Delete(root, true)
    }
