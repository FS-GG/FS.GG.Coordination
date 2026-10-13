namespace FS.GG.Execution.Processes

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Execution.Contracts

// This capture is closed over producer-owned readers. No caller callback or sink
// can replace admission, decoding, transport accounting or retirement.
module internal BoundedText =
    let private diagnostic code = { Code = code; Message = "Bounded decoded-text capture requires retirement."; Evidence = None }
    let private add current count =
        if count > Int64.MaxValue - current then Int64.MaxValue, true else current + count, false

    type private Capture(chunkSize: int) =
        let mutable completed: string list = []
        let mutable active: char array option = None
        let mutable activeCount = 0
        let mutable retained = 0L
        let mutable observed = 0L
        let mutable admitted = 0L
        member _.Observe(charge: int64) =
            let next, overflow = add observed charge
            observed <- next
            overflow
        member _.Retain(buffer: char array, count: int, charge: int64) =
            // Admission was committed before retention. Allocation failure can leave
            // a partial retained prefix, accompanied by an explicit latched failure.
            admitted <- admitted + charge
            let mutable offset = 0
            while offset < count do
                let storage =
                    match active with
                    | Some existing -> existing
                    | None ->
                        let created = Array.zeroCreate<char> chunkSize
                        active <- Some created
                        created
                let copied = min (chunkSize - activeCount) (count - offset)
                Array.Copy(buffer,offset,storage,activeCount,copied)
                offset <- offset + copied
                activeCount <- activeCount + copied
                retained <- retained + int64 copied
                if activeCount = chunkSize then
                    let frozen = String(storage,0,activeCount)
                    completed <- frozen :: completed
                    activeCount <- 0
        member _.Snapshot(raw: int64, reader: Observation<ReaderState>, copy: bool) : TextStreamCapture =
            let chunks =
                match copy,active with
                | false,_ -> []
                | true,Some storage when activeCount > 0 -> String(storage,0,activeCount) :: completed |> List.rev
                | _ -> List.rev completed
            { Chunks = chunks; ObservedDecodedUtf8Bytes = observed; AdmittedDecodedUtf8Bytes = admitted
              RetainedUtf16CodeUnits = retained; RawTransportBytes = raw; OmittedDecodedUtf8Bytes = observed - admitted
              Reader = reader }

    type CaptureState(bounds: TextCaptureBounds, transport: IBoundedByteSink) =
        let stdout = Capture(bounds.ReaderCharacterChunkSize)
        let stderr = Capture(bounds.ReaderCharacterChunkSize)
        let mutable charged = 0L
        let mutable first: Diagnostic option = None
        let decodedLimit = diagnostic "text-decoded-limit"
        let counterLimit = diagnostic "text-counter-limit"
        let allocationFailure = diagnostic "text-retention-failed"
        let copyFailure = diagnostic "text-observation-copy-failed"
        let latch cause =
            if first.IsNone then first <- Some cause
            RetireRequired(first.Value)
        member _.RecordTransport(channel, bytes) =
            match transport.Append(channel,bytes) with
            | RetireRequired cause -> latch cause
            | Accepted when channel = TerminalEvidence -> Accepted
            | Accepted -> match first with Some cause -> RetireRequired cause | None -> Accepted
        member _.Admit(channel, buffer: char array, count: int) =
            let charge = int64 (Encoding.UTF8.GetByteCount(buffer,0,count))
            let capture = if channel = Stdout then stdout else stderr
            let next, overflow = add charged charge
            charged <- next
            let streamOverflow = capture.Observe charge
            if overflow || streamOverflow then latch counterLimit
            elif charged > bounds.MaximumDecodedUtf8Bytes then latch decodedLimit
            else
                match first with
                | Some cause -> RetireRequired cause
                | None ->
                    try
                        capture.Retain(buffer,count,charge)
                        Accepted
                    with :? OutOfMemoryException -> latch allocationFailure
        member _.RawSnapshot() =
            let value = transport.Snapshot()
            { value with FirstFailure = first |> Option.orElse value.FirstFailure }
        member _.RecordCopyFailure() = latch copyFailure |> ignore
        member private _.SnapshotCore(result: ExecutionResult, copy: bool) : TextOutputSnapshot =
            let value = transport.Snapshot()
            { Stdout = stdout.Snapshot(value.Stdout.Totals.Observed,result.Stdout.Reader,copy)
              Stderr = stderr.Snapshot(value.Stderr.Totals.Observed,result.Stderr.Reader,copy)
              ChargedDecodedUtf8Bytes = charged
              RawTransportBytes = value.Stdout.Totals.Observed + value.Stderr.Totals.Observed
              FirstFailure = first; SnapshotComplete = copy }
        member this.Snapshot(result) = this.SnapshotCore(result,true)
        member this.FailedSnapshot(result) = this.SnapshotCore(result,false)

    // StreamReader owns its bounded byte buffer and decoder. This wrapper counts
    // real pipe transport before decoder consumption without substituting encoding.
    // The session retains the original Process/pipe; disposing this view cannot
    // manufacture pipe EOF or close an outstanding reader's native obligations.
    type TransportStream(inner: Stream, observe: ReadOnlyMemory<byte> -> unit) =
        inherit Stream()
        override _.CanRead = inner.CanRead
        override _.CanSeek = false
        override _.CanWrite = false
        override _.Length = raise (NotSupportedException())
        override _.Position with get() = raise (NotSupportedException()) and set _ = raise (NotSupportedException())
        override _.Flush() = ()
        override _.Seek(_,_) = raise (NotSupportedException())
        override _.SetLength _ = raise (NotSupportedException())
        override _.Write(_,_,_) = raise (NotSupportedException())
        override _.Read(buffer,offset,count) =
            let read = inner.Read(buffer,offset,count)
            if read > 0 then observe(ReadOnlyMemory<byte>(buffer,offset,read))
            read
        override _.ReadAsync(buffer: Memory<byte>, cancellation: CancellationToken) =
            ValueTask<int>(task {
                let! read = inner.ReadAsync(buffer,cancellation).AsTask()
                if read > 0 then observe(ReadOnlyMemory<byte>(buffer.Slice(0,read).ToArray()))
                return read })
        override this.ReadAsync(buffer: byte array, offset, count, cancellation) =
            this.ReadAsync(buffer.AsMemory(offset,count),cancellation).AsTask()
