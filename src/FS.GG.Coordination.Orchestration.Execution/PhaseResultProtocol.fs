namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Buffers.Binary
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Threading.Tasks
open FS.GG.Execution.Contracts

/// A closed immutable snapshot. Array access returns a copy, never the retained bytes.
type PhaseResultSnapshot internal (binding: string, ordinal: int, kind: string, frame: byte array, payload: byte array) =
    member _.Binding = binding
    member _.Ordinal = ordinal
    member _.Kind = kind
    member _.Digest = Convert.ToHexStringLower(SHA256.HashData frame)
    member _.Frame = ReadOnlyMemory<byte>(Array.copy frame)
    member _.Payload = ReadOnlyMemory<byte>(Array.copy payload)

/// Completion of the caller's durable replacement AND independent readback.
/// This declaration does not establish persistence: the supplied writer owns that proof.
type PhaseResultReadback = { Binding: string; Ordinal: int; FrameDigest: string }

/// The caller retains this obligation even after expiry; no abandoned callback is cleanup.
type PhaseResultGate internal (work: Task<Result<PhaseResultReadback, string>>, snapshot: PhaseResultSnapshot) =
    member _.Completion = work
    member _.Snapshot = snapshot

[<RequireQualifiedAccess>]
module PhaseResultProtocol =
    let identifier = "fsgg.execution.phase-result/1"
    let maximumFrameBytes = 2048
    let maximumAckBytes = 512
    let maximumAggregateBytes = 16384

    let private refuse () = invalidOp "phase-protocol-refused"
    let private ascii (text: string) = text |> Seq.forall (fun c -> c >= ' ' && c <= '~')

    let private canonical (element: JsonElement) =
        use bytes = new MemoryStream()
        use writer = new Utf8JsonWriter(bytes, JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
        let mutable entries = 0
        let rec emit depth (value: JsonElement) =
            entries <- entries + 1
            if depth > 12 || entries > 512 then refuse ()
            match value.ValueKind with
            | JsonValueKind.Object ->
                let fields = value.EnumerateObject() |> Seq.toArray
                if fields |> Array.exists (fun f -> not (ascii f.Name)) then refuse ()
                let names = fields |> Array.map _.Name
                if (Set.ofArray names).Count <> names.Length then refuse ()
                writer.WriteStartObject()
                for field in fields |> Array.sortWith (fun x y -> StringComparer.Ordinal.Compare(x.Name, y.Name)) do
                    writer.WritePropertyName field.Name
                    emit (depth + 1) field.Value
                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                for item in value.EnumerateArray() do emit (depth + 1) item
                writer.WriteEndArray()
            | JsonValueKind.String ->
                let text = value.GetString()
                if text.Length > 1024 || not (ascii text) then refuse ()
                writer.WriteStringValue text
            | JsonValueKind.Number ->
                let raw = value.GetRawText()
                if raw.Length = 0 || raw |> Seq.exists (fun c -> c < '0' || c > '9') then refuse ()
                let mutable number = 0UL
                if not (value.TryGetUInt64(&number)) then refuse ()
                writer.WriteNumberValue number
            | JsonValueKind.True -> writer.WriteBooleanValue true
            | JsonValueKind.False -> writer.WriteBooleanValue false
            | JsonValueKind.Null -> writer.WriteNullValue()
            | _ -> refuse ()
        emit 0 element
        writer.Flush()
        bytes.ToArray()

    let private parseCanonical (bytes: byte array) =
        let document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth = 12))
        try
            if canonical document.RootElement <> bytes then refuse ()
            document
        with _ -> document.Dispose(); reraise ()

    let private keys expected (value: JsonElement) =
        value.ValueKind = JsonValueKind.Object
        && (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList expected

    let private text (name: string) (value: JsonElement) =
        let field = value.GetProperty name
        if field.ValueKind <> JsonValueKind.String then refuse ()
        field.GetString()

    let private hash (bytes: byte array) = Convert.ToHexStringLower(SHA256.HashData bytes)
    let private frame (body: byte array) =
        let bytes = Array.zeroCreate<byte> (body.Length + 4)
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), uint32 body.Length)
        body.CopyTo(bytes, 4)
        bytes

    /// Expected binding is prepared independently by the owning adapter. Its exact
    /// canonical bytes must join held process, source and live original clock there.
    let bindingDigest (expectedBinding: ReadOnlyMemory<byte>) =
        try
            if expectedBinding.Length > maximumFrameBytes - 4 then refuse ()
            let bytes = expectedBinding.ToArray()
            if bytes.Length > maximumFrameBytes - 4 then refuse ()
            use document = parseCanonical bytes
            if document.RootElement.ValueKind <> JsonValueKind.Object then refuse ()
            Ok(hash bytes)
        with _ -> Error "phase-binding-refused"

    let private decode expectedBinding (bytes: byte array) =
        if bytes.Length < 5 || bytes.Length > maximumFrameBytes then refuse ()
        if BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) <> uint32 (bytes.Length - 4) then refuse ()
        use document = parseCanonical bytes[4..]
        let value = document.RootElement
        if not (keys ["v"; "b"; "n"; "k"; "p"] value) || text "v" value <> identifier then refuse ()
        if text "b" value <> expectedBinding then refuse ()
        let n = value.GetProperty "n"
        let mutable ordinal = 0
        if n.ValueKind <> JsonValueKind.Number || not (n.TryGetInt32(&ordinal)) || ordinal < 1 || ordinal > 6 then refuse ()
        let kind = text "k" value
        if not (List.contains kind ["ready"; "issuance-possible"; "token-owned"; "revocation-possible"; "revoked"; "terminal"]) then refuse ()
        let payload = value.GetProperty "p"
        if payload.ValueKind <> JsonValueKind.Object then refuse ()
        PhaseResultSnapshot(expectedBinding, ordinal, kind, Array.copy bytes, canonical payload)

    let private ack (snapshot: PhaseResultSnapshot) =
        // Values are fixed identifiers, hashes and a bounded ordinal; no escaping seam.
        let body = Encoding.ASCII.GetBytes($"{{\"b\":\"{snapshot.Binding}\",\"h\":\"{snapshot.Digest}\",\"k\":\"ack\",\"n\":{snapshot.Ordinal},\"v\":\"{identifier}\"}}")
        let result = frame body
        if result.Length > maximumAckBytes then refuse ()
        result

    /// Domain verification/persistence happens on a separate task; the caller must
    /// account for this task, including a blocked or expired writer, until settled.
    let private startGate
        (snapshot: PhaseResultSnapshot)
        (persistAndReadback: PhaseResultSnapshot -> Task<Result<PhaseResultReadback, string>>)
        =
        let work = Task.Run<Result<PhaseResultReadback, string>>(Func<Task<Result<PhaseResultReadback, string>>>(fun () ->
            task {
                try return! persistAndReadback snapshot
                with _ -> return Error "phase-retention-failed"
            }))
        PhaseResultGate(work, snapshot)

    /// One session covers both directions, rejected/partial bytes and every gate.
    /// No stream I/O, callback or wait runs under this reducer's lock.
    type Session(expectedBinding: ReadOnlyMemory<byte>) =
        let gate = obj()
        let binding = bindingDigest expectedBinding |> Result.defaultWith (fun _ -> refuse ())
        let bindingBytes = expectedBinding.ToArray()
        let mutable charged = 0L
        let mutable phase = "start"
        let mutable ordinal = 0
        let mutable started = false
        let mutable refused = false
        let mutable pending: PhaseResultSnapshot option = None
        let mutable retained: PhaseResultSnapshot list = []
        let input = Array.zeroCreate<byte> maximumFrameBytes
        let mutable inputCount = 0
        let mutable inputExpected = 4
        let mutable frames = 0
        let decoded = System.Collections.Generic.HashSet<PhaseResultSnapshot>()
        let retaining = System.Collections.Generic.HashSet<PhaseResultSnapshot>()
        let mutable obligations: PhaseResultGate list = []
        let mutable startingRetention = 0

        let charge count =
            let amount = int64 (max 0 count)
            charged <- if amount > Int64.MaxValue - charged then Int64.MaxValue else charged + amount
            if count < 0 || charged > int64 maximumAggregateBytes then
                refused <- true
                refuse ()

        member _.ChargedBytes = lock gate (fun () -> charged)
        member _.ObservedBytes = lock gate (fun () -> charged)
        member _.Refused = lock gate (fun () -> refused)
        member _.Accepted = lock gate (fun () -> List.rev retained)
        /// Join this with readers and other local obligations in CleanupResult.LocalTasks.
        /// Expiry/refusal never removes an outstanding persistence task.
        member _.LocalTasks = lock gate (fun () ->
            if startingRetention > 0 || (obligations |> List.exists (fun work -> not work.Completion.IsCompleted)) then Outstanding else Settled)
        member _.RetentionTasks = lock gate (fun () -> List.rev obligations)

        member _.BeginRetention(snapshot: PhaseResultSnapshot, persistAndReadback) =
            let admitted = lock gate (fun () ->
                match pending with
                | Some expected when not refused && obj.ReferenceEquals(expected, snapshot) && retaining.Add snapshot ->
                    // Reserve before scheduling: an independent cleanup observer must
                    // never see Settled between callback launch and task registration.
                    startingRetention <- startingRetention + 1
                    true
                | _ -> refused <- true; false)
            if not admitted then Error "phase-retention-order-refused"
            else
                try
                    let work = startGate snapshot persistAndReadback
                    lock gate (fun () ->
                        obligations <- work :: obligations
                        startingRetention <- startingRetention - 1)
                    Ok work
                with _ ->
                    // Scheduling ambiguity retains the reservation and refuses ACK.
                    lock gate (fun () -> refused <- true)
                    Error "phase-retention-start-refused"

        member _.StartFrame() = lock gate (fun () ->
            if started || refused then refuse ()
            let body = Encoding.ASCII.GetBytes($"{{\"b\":\"{binding}\",\"k\":\"start\",\"p\":{Encoding.ASCII.GetString bindingBytes},\"v\":\"{identifier}\"}}")
            let result = frame body
            if result.Length > maximumFrameBytes then refused <- true; refuse ()
            charge result.Length
            started <- true
            ReadOnlyMemory<byte>(result))

        /// Incremental bounded decoding. Prefix and aggregate are checked before
        /// a complete frame copy; rejected and partial input remain charged.
        member _.Feed(bytes: ReadOnlyMemory<byte>) = lock gate (fun () ->
            try
                charge bytes.Length
                if refused || not started || phase = "terminal" then refuse ()
                let snapshots = ResizeArray<PhaseResultSnapshot>()
                for index = 0 to bytes.Length - 1 do
                    input[inputCount] <- bytes.Span[index]
                    inputCount <- inputCount + 1
                    if inputCount = 4 then
                        let length = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(0, 4))
                        if length < 1u || length > uint32 (maximumFrameBytes - 4) then refuse ()
                        inputExpected <- int length + 4
                    if inputCount = inputExpected && inputCount > 4 then
                        frames <- frames + 1
                        if frames > 6 then refuse ()
                        let snapshot = decode binding input[0..inputCount-1]
                        decoded.Add snapshot |> ignore
                        snapshots.Add snapshot
                        inputCount <- 0
                        inputExpected <- 4
                Ok(List.ofSeq snapshots)
            with _ -> refused <- true; Error "phase-frame-refused")

        member _.FinishInput() = lock gate (fun () ->
            if inputCount <> 0 || phase <> "terminal" then refused <- true
            if refused then Error "phase-input-incomplete" else Ok ())

        /// Adapter code calls this on its parser/reporting task, never on the
        /// pidfd loop. The validator is mandatory and runs outside the state lock.
        member this.AcceptChild(snapshot: PhaseResultSnapshot, validateDomain: PhaseResultSnapshot -> Result<unit, string>) =
            let validation = try validateDomain snapshot with _ -> Error "phase-domain-refused"
            match validation with
            | Error _ -> lock gate (fun () -> refused <- true); Error "phase-domain-refused"
            | Ok () -> this.AcceptValidated snapshot

        member private _.AcceptValidated(snapshot: PhaseResultSnapshot) = lock gate (fun () ->
            try
                if not started || refused || (pending.IsSome && snapshot.Kind <> "terminal") || snapshot.Binding <> binding || snapshot.Ordinal <> ordinal + 1 || not (decoded.Remove snapshot) then refuse ()
                let terminalBeforeAck = pending.IsSome && snapshot.Kind = "terminal"
                let next =
                    match phase, snapshot.Kind with
                    | "start", "ready" -> true
                    | "ready", "issuance-possible" -> true
                    | "issuance-possible", "token-owned" -> true
                    | "token-owned", "revocation-possible" -> true
                    | "revocation-possible", "revoked" -> true
                    | previous, "terminal" when previous <> "start" && previous <> "terminal" -> true
                    | _ -> false
                if not next then refuse ()
                if snapshot.Kind = "ready" then
                    use doc = parseCanonical (snapshot.Payload.ToArray())
                    if not (keys ["binding"] doc.RootElement) || canonical (doc.RootElement.GetProperty "binding") <> bindingBytes then refuse ()
                retained <- snapshot :: retained
                ordinal <- snapshot.Ordinal
                phase <- snapshot.Kind
                let requiresAck = List.contains phase ["ready"; "issuance-possible"; "revocation-possible"]
                if requiresAck then pending <- Some snapshot
                if terminalBeforeAck then
                    // Preserve the validated failure snapshot, but a prior writer
                    // finishing later can never revive its ACK or qualify success.
                    pending <- None
                    refused <- true
                Ok requiresAck
            with _ -> refused <- true; Error "phase-order-refused")

        /// Returns ACK bytes at most once, after exact retained readback. Taking ACK
        /// is conservatively possibly delivered, regardless of subsequent I/O outcome.
        member _.TakeAck(now: ClockStamp, window: FiniteCustodyWindow, completed: PhaseResultGate) = lock gate (fun () ->
            try
                let snapshot = completed.Snapshot
                if not (obligations |> List.exists (fun work -> obj.ReferenceEquals(work, completed))) then refuse ()
                match pending with
                | Some expected when obj.ReferenceEquals(expected, snapshot) -> ()
                | _ -> refuse ()
                if refused || not completed.Completion.IsCompletedSuccessfully then refuse ()
                let inTime =
                    if snapshot.Kind = "revocation-possible" then
                        match FiniteCustodyWindow.remaining now window with Ok view -> not view.WorkExpired | Error _ -> false
                    else FiniteCustodyWindow.normalWorkAllowed now window
                if not inTime then refuse ()
                match completed.Completion.Result with
                | Ok readback when readback.Binding = snapshot.Binding && readback.Ordinal = snapshot.Ordinal && readback.FrameDigest = snapshot.Digest -> ()
                | _ -> refuse ()
                let bytes = ack snapshot
                charge bytes.Length
                pending <- None
                Ok(ReadOnlyMemory<byte>(bytes))
            with _ -> refused <- true; Error "phase-ack-refused")
