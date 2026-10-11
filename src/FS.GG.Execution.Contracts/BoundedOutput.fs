namespace FS.GG.Execution.Contracts
open System

[<RequireQualifiedAccess>]
module BoundedOutput =
    type private Capture(prefixSize: int, tailSize: int) =
        let prefix = Array.zeroCreate<byte> prefixSize
        let tail = Array.zeroCreate<byte> tailSize
        let mutable prefixCount = 0
        let mutable tailCount = 0
        let mutable tailNext = 0
        let mutable observed = 0L
        member _.Observe(count: int) =
            let overflow = int64 count > Int64.MaxValue - observed
            observed <- if overflow then Int64.MaxValue else observed + int64 count
            overflow
        member _.Retain(bytes: ReadOnlyMemory<byte>) =
            for i in 0 .. bytes.Length - 1 do
                let value = bytes.Span[i]
                if prefixCount < prefixSize then
                    prefix[prefixCount] <- value
                    prefixCount <- prefixCount + 1
                elif tailSize > 0 then
                    tail[tailNext] <- value
                    tailNext <- (tailNext + 1) % tailSize
                    tailCount <- min tailSize (tailCount + 1)
        member _.Snapshot() =
            let ordered = Array.zeroCreate<byte> tailCount
            let start = if tailCount < tailSize then 0 else tailNext
            for i in 0 .. tailCount - 1 do ordered[i] <- tail[(start + i) % tailSize]
            let retained = int64 prefixCount + int64 tailCount
            { Prefix = ReadOnlyMemory(prefix[0 .. prefixCount - 1]); Tail = ReadOnlyMemory ordered
              Totals = { Observed = observed; Charged = observed; Retained = retained; Omitted = observed - retained }
              Reader = Pending { Code = "reader-unobserved"; Message = "No EOF observation recorded."; Evidence = None } }

    let create (policy: CapturePolicy) =
        match Validation.capture policy with
        | errors when not errors.IsEmpty -> Error errors
        | _ ->
            let gate = obj()
            let stdout = Capture(policy.StdoutPrefixBytes, policy.StdoutTailBytes)
            let stderr = Capture(policy.StderrPrefixBytes, policy.StderrTailBytes)
            let mutable total = 0L
            let mutable ordinary = 0L
            let mutable evidence = 0L
            let mutable terminal = 0L
            let mutable first: Diagnostic option = None
            let add current count =
                if int64 count > Int64.MaxValue - current then Int64.MaxValue, true else current + int64 count, false
            let fail code =
                if first.IsNone then first <- Some { Code = code; Message = "Output admission requires retirement."; Evidence = None }
            Ok
                { new IBoundedByteSink with
                    member _.Append(channel, bytes) =
                        lock gate (fun () ->
                            let next, wrap = add total bytes.Length
                            total <- next
                            let mutable counterWrap = wrap
                            match channel with
                            | Stdout -> counterWrap <- stdout.Observe(bytes.Length) || counterWrap
                            | Stderr -> counterWrap <- stderr.Observe(bytes.Length) || counterWrap
                            | _ ->
                                let nextEvidence, overflow = add evidence bytes.Length
                                evidence <- nextEvidence
                                counterWrap <- counterWrap || overflow
                            if channel = TerminalEvidence then
                                let nextTerminal, overflow = add terminal bytes.Length
                                terminal <- nextTerminal
                                counterWrap <- counterWrap || overflow
                            else
                                let nextOrdinary, overflow = add ordinary bytes.Length
                                ordinary <- nextOrdinary
                                counterWrap <- counterWrap || overflow
                            let currentViolation =
                                if counterWrap then Some "output-counter-limit"
                                elif bytes.Length > policy.ReadChunkBytes then Some "output-chunk-limit"
                                elif total > policy.TotalBytes then Some "output-byte-limit"
                                elif channel = TerminalEvidence && terminal > int64 policy.TerminalReserveBytes then Some "output-terminal-limit"
                                elif channel <> TerminalEvidence && ordinary > policy.TotalBytes - int64 policy.TerminalReserveBytes then Some "output-byte-limit"
                                else None
                            Option.iter fail currentViolation
                            match currentViolation, first, channel with
                            | None, _, TerminalEvidence -> Accepted
                            | _, Some diagnostic, _ -> RetireRequired diagnostic
                            | None, None, _ ->
                                match channel with Stdout -> stdout.Retain bytes | Stderr -> stderr.Retain bytes | _ -> ()
                                Accepted
                            | Some code, None, _ -> RetireRequired { Code = code; Message = "Output admission requires retirement."; Evidence = None })
                    member _.Snapshot() =
                        lock gate (fun () ->
                            { Stdout = stdout.Snapshot(); Stderr = stderr.Snapshot()
                              ChargedTotal = total; EvidenceBytes = evidence; TerminalEvidenceBytes = terminal; FirstFailure = first }) }
