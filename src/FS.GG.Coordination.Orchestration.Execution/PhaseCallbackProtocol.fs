namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Buffers.Binary
open System.IO
open System.Text.Encodings.Web
open System.Text.Json

/// Closed callback framing. This carries no domain or native authority.
module internal PhaseCallbackProtocol =
    let identifier = "fsgg.execution.phase-callback/1"
    let maximumAggregateBytes = 40960
    let maximumRequests = 13
    let requiredCapacity requestBytes isTerminal retaining =
        requestBytes + 512 + (if isTerminal then (if retaining then 0 else 1024) else 5632)
    let canAdmit remaining requestBytes isTerminal retaining =
        remaining >= requiredCapacity requestBytes isTerminal retaining
    let private refuse () = invalidOp "phase-callback-refused"
    let private hex (value: string) =
        not (isNull value) && value.Length = 64 && (value |> Seq.forall Char.IsAsciiHexDigitLower)

    type Message =
        { Kind: string; Invocation: Guid; Binding: string; Sequence: int; Ordinal: int
          FrameDigest: string; Data: string option; Reason: string option }

    let private additions kind =
        match kind with
        | "bind" -> ["bindingBase64"]
        | "validate" -> ["frameBase64"]
        | "refused" -> ["reason"]
        | "retain" | "bound" | "validated" | "retained" -> []
        | _ -> refuse ()

    let encode message =
        if message.Invocation = Guid.Empty || not (hex message.Binding) || not (hex message.FrameDigest)
           || message.Sequence < 1 || message.Sequence > maximumRequests
           || message.Ordinal < 0 || message.Ordinal > 6 then refuse ()
        let fields = additions message.Kind
        if message.Data.IsSome <> (fields = ["bindingBase64"] || fields = ["frameBase64"])
           || message.Reason.IsSome <> (fields = ["reason"]) then refuse ()
        match message.Reason with
        | Some reason when not (List.contains reason ["binding"; "order"; "domain"; "retention"; "readback"; "deadline"; "cancelled"; "internal"]) -> refuse ()
        | _ -> ()
        use body = new MemoryStream()
        use writer = new Utf8JsonWriter(body, JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
        writer.WriteStartObject()
        writer.WriteString("binding", message.Binding)
        if message.Kind = "bind" then writer.WriteString("bindingBase64", message.Data.Value)
        if message.Kind = "validate" then writer.WriteString("frameBase64", message.Data.Value)
        writer.WriteString("frameDigest", message.FrameDigest)
        writer.WriteString("invocation", string message.Invocation)
        writer.WriteString("kind", message.Kind)
        writer.WriteNumber("ordinal", message.Ordinal)
        if message.Kind = "refused" then writer.WriteString("reason", message.Reason.Value)
        writer.WriteNumber("sequence", message.Sequence)
        writer.WriteString("v", identifier)
        writer.WriteEndObject()
        writer.Flush()
        let payload = body.ToArray()
        let frame = Array.zeroCreate<byte> (payload.Length + 4)
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), uint32 payload.Length)
        payload.CopyTo(frame, 4)
        let cap = if message.Kind = "bind" || message.Kind = "validate" then 4096 else 512
        if frame.Length > cap then refuse ()
        frame

    let decode (frame: byte array) =
        if frame.Length < 5 || frame.Length > 4096
           || BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4)) <> uint32 (frame.Length - 4) then refuse ()
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(frame, 4, frame.Length - 4), JsonDocumentOptions(MaxDepth = 2))
        let root = document.RootElement
        let text name = root.GetProperty(name: string).GetString()
        let kind = text "kind"
        let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
        let expected = Set.ofList (["v"; "kind"; "invocation"; "binding"; "sequence"; "ordinal"; "frameDigest"] @ additions kind)
        if names.Length <> expected.Count || Set.ofArray names <> expected || text "v" <> identifier then refuse ()
        let mutable invocation = Guid.Empty
        if not (Guid.TryParseExact(text "invocation", "D", &invocation)) then refuse ()
        let result =
            { Kind = kind; Invocation = invocation; Binding = text "binding"
              Sequence = root.GetProperty("sequence").GetInt32(); Ordinal = root.GetProperty("ordinal").GetInt32()
              FrameDigest = text "frameDigest"
              Data = if kind = "bind" then Some(text "bindingBase64") elif kind = "validate" then Some(text "frameBase64") else None
              Reason = if kind = "refused" then Some(text "reason") else None }
        match result.Data with
        | Some data ->
            if data.Length > 2732 then refuse ()
            let bytes = Convert.FromBase64String data
            if Convert.ToBase64String bytes <> data || bytes.Length > (if kind = "bind" then 2044 else 2048) then refuse ()
        | None -> ()
        if encode result <> frame then refuse ()
        result

    /// One pending request and one provisional response. A full write must be
    /// confirmed independently before even a matching fast response is accepted.
    type Session(invocation: Guid, bindingBytes: ReadOnlyMemory<byte>) =
        let gate = obj()
        let owned = bindingBytes.ToArray()
        let binding = PhaseResultProtocol.bindingDigest (ReadOnlyMemory<byte> owned) |> Result.defaultWith (fun _ -> refuse ())
        let mutable sequence = 0
        let mutable ordinal = 0
        let mutable charged = 0
        let mutable observed = 0L
        let mutable closed = false
        let mutable bound = false
        let mutable terminal = false
        let mutable validated: PhaseResultSnapshot option = None
        let mutable pending: (Message * int) option = None
        let mutable writeComplete = false
        let mutable response: Message option = None
        let input = Array.zeroCreate<byte> 512
        let mutable count = 0
        let mutable expected = 4
        let fail () = closed <- true; refuse ()
        let beginRequest kind (snapshot: PhaseResultSnapshot option) = lock gate (fun () ->
            if closed || terminal || pending.IsSome || sequence >= maximumRequests then fail ()
            let n, digest, data, isTerminal =
                match kind, snapshot with
                | "bind", None when not bound && sequence = 0 -> 0, binding, Some(Convert.ToBase64String owned), false
                | "validate", Some value when bound && validated.IsNone && value.Ordinal = ordinal + 1 && value.Binding = binding ->
                    value.Ordinal, value.Digest, Some(Convert.ToBase64String(value.Frame.ToArray())), value.Kind = "terminal"
                | "retain", Some value when bound && (validated |> Option.exists (fun held -> obj.ReferenceEquals(held, value))) ->
                    value.Ordinal, value.Digest, None, value.Kind = "terminal"
                | _ -> fail ()
            sequence <- sequence + 1
            let request = { Kind = kind; Invocation = invocation; Binding = binding; Sequence = sequence
                            Ordinal = n; FrameDigest = digest; Data = data; Reason = None }
            let bytes = encode request
            // The pending response reservation is charged once, before write.
            if not (canAdmit (maximumAggregateBytes - charged) bytes.Length isTerminal (kind = "retain")) then fail ()
            charged <- charged + bytes.Length + 512
            pending <- Some(request, bytes.Length)
            writeComplete <- false
            response <- None
            count <- 0
            expected <- 4
            if kind = "validate" then validated <- snapshot
            bytes)
        member _.TerminalRetained = lock gate (fun () ->
            terminal || (match pending, response, validated with
                         | Some(request, _), Some(reply), Some(snapshot) -> request.Kind = "retain" && reply.Kind = "retained" && snapshot.Kind = "terminal"
                         | _ -> false))
        member _.Binding = binding
        member _.ChargedBytes = lock gate (fun () -> charged)
        member _.ObservedIncomingBytes = lock gate (fun () -> observed)
        member _.Refused = lock gate (fun () -> closed)
        member _.Close() = lock gate (fun () -> closed <- true)
        member _.Bind() = beginRequest "bind" None
        member _.Validate(snapshot) = beginRequest "validate" (Some snapshot)
        member _.Retain(snapshot) = beginRequest "retain" (Some snapshot)
        member _.ConfirmWrite(length) = lock gate (fun () ->
            match pending with
            | Some(_, expectedLength) when not closed && not writeComplete && length = expectedLength -> writeComplete <- true
            | _ -> fail ())
        member _.Feed(bytes: ReadOnlyMemory<byte>) = lock gate (fun () ->
            observed <- observed + int64 bytes.Length
            charged <- charged + (if pending.IsSome then max 0 (count + bytes.Length - 512) - max 0 (count - 512) else bytes.Length)
            if charged > maximumAggregateBytes then fail ()
            if closed || pending.IsNone || response.IsSome || bytes.IsEmpty then fail ()
            for index = 0 to bytes.Length - 1 do
                let value = bytes.Span[index]
                if response.IsSome || count >= input.Length then fail ()
                input[count] <- value
                count <- count + 1
                if count = 4 then
                    let body = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(0, 4))
                    if body = 0u || body > 508u then fail ()
                    expected <- int body + 4
                if count = expected then
                    try
                        let reply = decode input[..count - 1]
                        match pending with
                        | Some(request, _) when reply.Invocation = request.Invocation && reply.Binding = request.Binding
                                                && reply.Sequence = request.Sequence && reply.Ordinal = request.Ordinal
                                                && reply.FrameDigest = request.FrameDigest
                                                && (reply.Kind = "refused" || reply.Kind = (match request.Kind with "bind" -> "bound" | "validate" -> "validated" | _ -> "retained")) ->
                            response <- Some reply
                        | _ -> fail ()
                    with _ -> fail ()
            response.IsSome)
        member _.TakeResponse() = lock gate (fun () ->
            match pending, response with
            | Some(request, _), Some reply when not closed && writeComplete ->
                if reply.Kind = "refused" then fail ()
                match request.Kind with
                | "bind" -> bound <- true
                | "retain" ->
                    ordinal <- request.Ordinal
                    terminal <- validated |> Option.exists (fun snapshot -> snapshot.Kind = "terminal")
                    validated <- None
                | _ -> ()
                pending <- None
                response <- None
                reply
            | _ -> fail ())
