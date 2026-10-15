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
