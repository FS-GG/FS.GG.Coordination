namespace FS.GG.Execution.Contracts

open System
open System.IO
open System.Text

[<RequireQualifiedAccess>]
module Validation =
    let limits =
        { CodeBytes = 64; MessageBytes = 4096; ReferenceBytes = 2048; CollectionEntries = 64
          RequestBytes = 256 * 1024; RequestEntries = 4096; ResultMetadataBytes = 64 * 1024; FirstCauseReserveBytes = 2048; CaptureAllocationBytes = 256 * 1024 }

    let private error field = { Code = "invalid-value"; Field = field }
    let private bytes (value: string) = if isNull value then Int32.MaxValue else Encoding.UTF8.GetByteCount value
    let private text field maximum value =
        if String.IsNullOrWhiteSpace value || bytes value > maximum || value.IndexOf('\000') >= 0 then [ error field ] else []
    let private hex value =
        not (isNull value) && value.Length = 64 && (value |> Seq.forall Uri.IsHexDigit)
    let private digest field value = if hex value then [] else [ error field ]
    let private entries field values =
        if isNull (box values) || (values |> List.truncate (limits.CollectionEntries + 1) |> List.length) > limits.CollectionEntries then [ error field ] else []
    let private evidence (value: EvidenceRef) =
        text "evidence.kind" limits.CodeBytes value.Kind
        @ text "evidence.reference" limits.ReferenceBytes value.Reference
        @ (value.Digest |> Option.map (digest "evidence.digest") |> Option.defaultValue [])
    let diagnostic (value: Diagnostic) =
        text "diagnostic.code" limits.CodeBytes value.Code
        @ text "diagnostic.message" limits.MessageBytes value.Message
        @ (value.Evidence |> Option.map evidence |> Option.defaultValue [])
    let sanitizeDiagnostic (command: CommandSpec) (value: Diagnostic) =
        // Only values known in the prepared request can be redacted here. Inherited runtime
        // secrets must be resolved/redacted at the backend boundary before observation.
        let overrides =
            match command.Environment with
            | Inherit values | Allowlist(_, values) -> values |> Map.toList |> List.choose (snd)
            | Replace values -> values |> Map.toList |> List.map snd
        let secrets = command.Arguments @ overrides |> List.filter (String.IsNullOrEmpty >> not) |> List.distinct |> List.sortByDescending String.length
        let redact maximum (text: string) =
            let builder = StringBuilder(min maximum text.Length)
            let mutable offset = 0
            let mutable omitted = false
            while offset < text.Length && not omitted do
                let matched = secrets |> List.tryFind (fun secret -> secret.Length <= text.Length - offset && String.CompareOrdinal(text,offset,secret,0,secret.Length) = 0)
                let segment, consumed = match matched with Some secret -> "[redacted]", secret.Length | None -> string text[offset], 1
                if builder.Length + segment.Length > maximum then omitted <- true
                else builder.Append(segment) |> ignore; offset <- offset + consumed
            let result = builder.ToString()
            if omitted || Encoding.UTF8.GetByteCount result > maximum then "Redacted diagnostic detail omitted." else result
        { value with Message = redact limits.MessageBytes value.Message
                     Evidence = value.Evidence |> Option.map (fun evidence -> { evidence with Reference = redact limits.ReferenceBytes evidence.Reference }) }
    let generation (value: ProcessGeneration) =
        text "process.platform" limits.CodeBytes value.Platform
        @ text "process.scope" limits.ReferenceBytes value.ScopeId
        @ text "process.birth" limits.ReferenceBytes value.BirthToken
        @ (if value.Pid <= 0 then [ error "process.pid" ] else [])
    let private stamp (value: ObservationStamp) =
        text "observation.scope" limits.ReferenceBytes value.Scope @ evidence value.Provenance
        @ (value.Generation |> Option.map generation |> Option.defaultValue [])
        @ (match value.At with
           | Some at when at.Domain.Id = Guid.Empty || at.Domain.Frequency <= 0L || at.Ticks < 0L -> [ error "observation.clock" ]
           | _ -> [])
    let observation value =
        match value with
        | Known(_, at) | Absent at -> stamp at
        | Pending d | Unavailable d | Unknown d | Invalid d | LimitExceeded d -> diagnostic d
    let identity (value: OperationIdentity) =
        (if value.InvocationId = Guid.Empty then [ error "identity.invocation" ] else [])
        @ digest "identity.source" value.SourceDigest
        @ text "identity.profile" limits.CodeBytes value.ProfileId
        @ digest "identity.profileDigest" value.ProfileDigest
        @ (match value.Execution with
           | Some binding when binding.AssignmentId = Guid.Empty || binding.AttemptId = Guid.Empty || binding.Generation < 0L -> [ error "identity.execution" ]
           | _ -> [])
    let capture (value: CapturePolicy) =
        let retained = int64 value.StdoutPrefixBytes + int64 value.StdoutTailBytes + int64 value.StderrPrefixBytes + int64 value.StderrTailBytes
        [ if value.TotalBytes <= 0L || value.TerminalReserveBytes < 0 || int64 value.TerminalReserveBytes >= value.TotalBytes then error "capture.total"
          if min (min value.StdoutPrefixBytes value.StdoutTailBytes) (min value.StderrPrefixBytes value.StderrTailBytes) < 0 then error "capture.storage"
          if retained > int64 limits.CaptureAllocationBytes || retained > value.TotalBytes - int64 value.TerminalReserveBytes then error "capture.allocation"
          if value.ReadChunkBytes <= 0 || value.ReadChunkBytes > limits.RequestBytes then error "capture.chunk"
          if value.MaximumFrameBytes <= 0 || value.MaximumFrameBytes > limits.RequestBytes then error "capture.frame"
          if value.MaximumIncompleteLineBytes <= 0 || value.MaximumIncompleteLineBytes > value.MaximumFrameBytes then error "capture.line" ]
    let private assurance field = function
        | Sampled interval when interval <= TimeSpan.Zero -> [ error field ]
        | _ -> []
    let private resources (value: ResourceLimits) =
        let positive field (v: RequestedLimit<int64> option) =
            v |> Option.map (fun v -> (if v.Value <= 0L then [ error field ] else []) @ assurance field v.Assurance) |> Option.defaultValue []
        let count field (v: RequestedLimit<int> option) =
            v |> Option.map (fun v -> (if v.Value <= 0 then [ error field ] else []) @ assurance field v.Assurance) |> Option.defaultValue []
        positive "resources.memory" value.MemoryBytes
        @ count "resources.kernelTasks" value.MaxKernelTasks
        @ count "resources.ownedProcesses" value.MaxOwnedProcesses
        @ count "resources.clrProcesses" value.MaxClrProcesses
        @ (match value.CpuQuota with
           | Some v -> (if v.Value.Quota <= TimeSpan.Zero || v.Value.Period <= TimeSpan.Zero then [ error "resources.cpuQuota" ] else []) @ assurance "resources.cpuQuota" v.Assurance
           | None -> [])
        @ (match value.CpuAffinity with
           | Some ids when ids.IsEmpty || ids.Length > limits.CollectionEntries || List.exists (fun i -> i < 0) ids || (Set.ofList ids).Count <> ids.Length -> [ error "resources.affinity" ]
           | _ -> [])
    let private commandCore (value: CommandSpec) =
        let safe field v = if isNull v || v.IndexOf('\000') >= 0 then [ error field ] else []
        let environment =
            match value.Environment with
            | Inherit overrides -> [], overrides
            | Allowlist(names, overrides) -> names, overrides
            | Replace values -> [], values |> Map.map (fun _ v -> Some v)
        let names, overrides = environment
        let envNames = overrides |> Map.toList |> List.map fst
        let envErrors =
            [ for name in names @ envNames do
                yield! text "environment.name" limits.ReferenceBytes name
                if not (isNull name) && name.Contains('=') then yield error "environment.name"
              if (names |> List.map (fun n -> if isNull n then "" else n.ToUpperInvariant()) |> Set.ofList).Count <> names.Length then yield error "environment.duplicates"
              if (envNames |> List.map (fun n -> n.ToUpperInvariant()) |> Set.ofList).Count <> envNames.Length then yield error "environment.duplicates"
              for KeyValue(_, v) in overrides do
                match v with Some s -> yield! safe "environment.value" s | None -> () ]
        let size =
            int64 (bytes value.Executable) + int64 (bytes value.WorkingDirectory)
            + (value.Arguments |> List.sumBy (fun s -> int64 (bytes s) + 1L))
            + (names |> List.sumBy (fun s -> int64 (bytes s) + 1L))
            + (overrides |> Map.toList |> List.sumBy (fun (k,v) -> int64 (bytes k) + (v |> Option.map (bytes >> int64) |> Option.defaultValue 0L) + 2L))
            + (match value.Input with Closed -> 0L | Bytes memory -> int64 memory.Length)
        text "command.executable" limits.ReferenceBytes value.Executable
        @ text "command.cwd" limits.ReferenceBytes value.WorkingDirectory
        @ [ if not (Path.IsPathFullyQualified value.Executable) then error "command.executable"
            if not (Path.IsPathFullyQualified value.WorkingDirectory) then error "command.cwd"
            if size > int64 limits.RequestBytes then error "command.bytes"
            let captureAllocation = int64 value.Capture.StdoutPrefixBytes + int64 value.Capture.StdoutTailBytes + int64 value.Capture.StderrPrefixBytes + int64 value.Capture.StderrTailBytes
            let inputBytes = match value.Input with Closed -> 0L | Bytes memory -> int64 memory.Length
            if inputBytes + captureAllocation > int64 limits.RequestBytes then error "command.allocation"
            if value.Resources.MemoryBytes |> Option.exists (fun memory -> inputBytes + captureAllocation > memory.Value) then error "command.memory-allocation"
            if value.Arguments.Length > limits.RequestEntries || names.Length > limits.RequestEntries || overrides.Count > limits.RequestEntries then error "command.entries"
            if (value.Capabilities |> List.map _.Capability |> Set.ofList).Count <> value.Capabilities.Length then error "capabilities.duplicates" ]
        @ (value.Arguments |> List.collect (safe "command.argv"))
        @ envErrors @ entries "capabilities" value.Capabilities @ capture value.Capture @ resources value.Resources
    let command (value: CommandSpec) =
        // Refuse cardinality before byte scans or allocation; no error includes raw argv/env.
        let bounded values = not (isNull (box values)) && (values |> List.truncate (limits.RequestEntries + 1) |> List.length) <= limits.RequestEntries
        try
            let environmentBounded =
                match value.Environment with
                | Inherit overrides -> not (isNull (box overrides)) && overrides.Count <= limits.RequestEntries
                | Allowlist(names, overrides) -> bounded names && not (isNull (box overrides)) && overrides.Count <= limits.RequestEntries
                | Replace values -> not (isNull (box values)) && values.Count <= limits.RequestEntries
            if not (bounded value.Arguments) || not environmentBounded || isNull (box value.Capabilities) || value.Capabilities.Length > limits.CollectionEntries then [ error "command.entries" ]
            else commandCore value
        with
        | :? ArgumentException | :? NullReferenceException | :? OverflowException -> [ error "command.structure" ]
    let private cleanupCore (value: CleanupResult) =
        entries "cleanup.actions" value.RequestedActions @ (value.RequestedActions |> List.collect diagnostic)
        @ entries "cleanup.unresolved" value.Unresolved @ (value.Unresolved |> List.collect generation)
        @ observation value.LeaderTermination @ observation value.WorkloadEmpty @ observation value.OwnedChildrenReaped @ observation value.LocalTasks
        @ (match value.LocalTasks with Known(TaskUnknown d,_) -> diagnostic d | _ -> [])
        @ (match value.Custody with
           | RetainedByOwner owner -> text "owner.id" limits.CodeBytes owner.OwnerId @ evidence owner.Evidence
           | Released at -> stamp at
           | AuthorityUnavailable d -> diagnostic d
           | NoEffect -> [])
        @ [ if value.OmittedUnresolved < 0L then error "cleanup.omitted"
            if value.Disposition = Complete && (not value.Unresolved.IsEmpty || value.OmittedUnresolved <> 0L) then error "cleanup.complete" ]
    let cleanup (value: CleanupResult) =
        try
            let errors = entries "cleanup.actions" value.RequestedActions @ entries "cleanup.unresolved" value.Unresolved
            if not errors.IsEmpty then errors else cleanupCore value
        with
        | :? ArgumentException | :? NullReferenceException | :? OverflowException -> [ error "cleanup.structure" ]
    let private stream (prefix: int) (tail: int) (value: StreamCapture) =
        let t = value.Totals
        observation value.Reader
        @ (match value.Reader with Known(StoppedWithoutEof d,_) | Known(ReadFailed d,_) -> diagnostic d | _ -> [])
        @ [ if value.Prefix.Length > prefix || value.Tail.Length > tail then error "output.storage"
            if t.Observed < 0L || t.Charged <> t.Observed || t.Retained < 0L || t.Retained <> int64 value.Prefix.Length + int64 value.Tail.Length || t.Retained > t.Observed || t.Omitted <> t.Observed - t.Retained then error "output.totals" ]
    let output (policy: CapturePolicy) (value: OutputSnapshot) =
        let out = value.Stdout.Totals.Charged
        let err = value.Stderr.Totals.Charged
        stream policy.StdoutPrefixBytes policy.StdoutTailBytes value.Stdout
        @ stream policy.StderrPrefixBytes policy.StderrTailBytes value.Stderr
        @ (value.FirstFailure |> Option.map diagnostic |> Option.defaultValue [])
        @ [ if value.TerminalEvidenceBytes < 0L || value.TerminalEvidenceBytes > value.EvidenceBytes then error "output.terminal-ledger"
            if value.EvidenceBytes < 0L || value.ChargedTotal < 0L || out > Int64.MaxValue - err || out + err > Int64.MaxValue - value.EvidenceBytes || value.ChargedTotal <> out + err + value.EvidenceBytes then error "output.ledger"
            if value.FirstFailure.IsNone && (value.ChargedTotal > policy.TotalBytes || value.ChargedTotal - value.TerminalEvidenceBytes > policy.TotalBytes - int64 policy.TerminalReserveBytes || value.TerminalEvidenceBytes > int64 policy.TerminalReserveBytes) then error "output.overflow" ]
    let capability (value: CapabilityObservation) =
        observation value.Obtained
        @ (match value.Obtained with Known(e,_) -> evidence e | _ -> [])
    let obtainedLimit (requested: ResourceLimits) (value: ObtainedLimit) =
        let expected =
            match value.Name with
            | "memory" -> requested.MemoryBytes |> Option.map (fun r -> "bytes", string r.Value, r.Assurance)
            | "kernelTasks" -> requested.MaxKernelTasks |> Option.map (fun r -> "kernel-tasks", string r.Value, r.Assurance)
            | "ownedProcesses" -> requested.MaxOwnedProcesses |> Option.map (fun r -> "process-generations", string r.Value, r.Assurance)
            | "clrProcesses" -> requested.MaxClrProcesses |> Option.map (fun r -> "classified-clr-hosts", string r.Value, r.Assurance)
            | "cpuQuota" -> requested.CpuQuota |> Option.map (fun r -> "ticks-per-period", $"{r.Value.Quota.Ticks}/{r.Value.Period.Ticks}", r.Assurance)
            | "cpuAffinity" -> requested.CpuAffinity |> Option.map (fun r -> "cpu-ids", String.Join(",", r), Enforced)
            | _ -> None
        text "limit.name" limits.CodeBytes value.Name @ text "limit.unit" limits.CodeBytes value.Unit
        @ text "limit.requested" limits.ReferenceBytes value.Requested @ observation value.Actual @ observation value.Assurance
        @ (match value.Actual with Known(s,_) -> text "limit.actual" limits.ReferenceBytes s | _ -> [])
        @ (match value.Assurance with Known(a,_) -> assurance "limit.assurance" a | _ -> [])
        @ (match expected with
           | None -> [ error "limit.requestJoin" ]
           | Some(unit, amount, expectedAssurance) ->
               let join = if value.Unit <> unit || value.Requested <> amount then [ error "limit.requestJoin" ] else []
               let actual =
                   match value.Actual with
                   | Known(actual,_) when value.Name = "cpuQuota" ->
                       let parts = actual.Split('/')
                       let requestedParts = amount.Split('/')
                       match parts, requestedParts with
                       | [| q; p |], [| rq; rp |] ->
                           match Int64.TryParse q, Int64.TryParse p, Int64.TryParse rq, Int64.TryParse rp with
                           | (true,q),(true,p),(true,rq),(true,rp) when q > 0L && p > 0L && bigint q * bigint rp <= bigint rq * bigint p -> []
                           | _ -> [ error "limit.actual" ]
                       | _ -> [ error "limit.actual" ]
                   | Known(actual,_) when value.Name = "cpuAffinity" ->
                       if actual = amount then [] else [ error "limit.actual" ]
                   | Known(actual,_) ->
                       match Int64.TryParse actual, Int64.TryParse amount with
                       | (true,a),(true,r) when a > 0L && a <= r -> []
                       | _ -> [ error "limit.actual" ]
                   | _ -> []
               let mechanism =
                   match expectedAssurance,value.Assurance with
                   | Enforced, Known(Sampled _,_) -> [ error "limit.enforcement" ]
                   | Sampled requestedInterval, Known(Sampled actualInterval,_) when actualInterval > requestedInterval -> [ error "limit.sampling" ]
                   | _ -> []
               join @ actual @ mechanism)
