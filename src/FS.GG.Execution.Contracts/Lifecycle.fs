namespace FS.GG.Execution.Contracts
open System
open System.Text

type LifecycleState =
    private
        { Command: CommandSpec; Result: ExecutionResult; LastTick: int64
          LaunchConsumed: bool; TargetReleased: bool; TargetReleaseAt: int64 option; Returned: bool; CleanupWasComplete: bool
          StdoutTerminal: ReaderState option; StderrTerminal: ReaderState option
          ExitTerminal: Observation<ProcessExit> option }

type LifecycleEvent =
    | BeginLaunch
    | TargetReleaseRequested
    | LaunchObserved of LaunchObservation
    | CapabilitiesObtained of CapabilityObservation list
    | LimitObserved of ObtainedLimit
    | ExitObserved of Observation<ProcessExit>
    | StopRequested of StopReason
    | ReaderObserved of OutputChannel * Observation<ReaderState>
    | OutputObserved of OutputSnapshot
    | RetirementActionRequested of Diagnostic
    | CleanupObserved of CleanupResult
    | FailureObserved of Failure
    | ReturnRequested

[<RequireQualifiedAccess>]
module Lifecycle =
    let private refused field = Error [ { Code = "lifecycle-refused"; Field = field } ]
    let private note code = { Code = code; Message = "Observation not established."; Evidence = None }
    let private empty =
        { Prefix = ReadOnlyMemory<byte>.Empty; Tail = ReadOnlyMemory<byte>.Empty
          Totals = { Observed = 0L; Charged = 0L; Retained = 0L; Omitted = 0L }
          Reader = Pending(note "reader-pending") }
    let private addFailure failure (result: ExecutionResult) =
        match result.FirstFailure with
        | None -> { result with FirstFailure = Some failure }
        | Some _ when result.AdditionalFailures.Length < Validation.limits.CollectionEntries ->
            { result with AdditionalFailures = result.AdditionalFailures @ [ failure ] }
        | Some _ -> { result with OmittedFailures = if result.OmittedFailures = Int64.MaxValue then Int64.MaxValue else result.OmittedFailures + 1L }
    let prepare (command: CommandSpec) identity budget =
        let errors = Validation.command command @ Validation.identity identity
        if not errors.IsEmpty then Error errors
        else
            let input = match command.Input with Closed -> Closed | Bytes memory -> Bytes(ReadOnlyMemory(memory.ToArray()))
            let result =
                { Identity = identity; Phase = Prepared; Launch = NotStarted; Exit = Pending(note "exit-pending")
                  Stop = NotRequested; Stdout = empty; Stderr = empty; ChargedTotalBytes = 0L; EvidenceBytes = 0L; TerminalEvidenceBytes = 0L
                  Capabilities = []; Limits = []; Violations = []; FirstFailure = None; AdditionalFailures = []; OmittedFailures = 0L; OmittedDiagnosticDetails = 0L
                  Cleanup = { Disposition = CleanupUnknown; RequestedActions = []; LeaderTermination = Pending(note "termination-pending")
                              WorkloadEmpty = Unknown(note "workload-unknown"); OwnedChildrenReaped = Unknown(note "reaping-unknown")
                              LocalTasks = Unknown(note "tasks-unknown"); Unresolved = []; OmittedUnresolved = 0L; Custody = NoEffect }
                  OriginalBudget = budget }
            Ok { Command = { command with Input = input }; Result = result; LastTick = (Budget.describe budget).StartedAt
                 LaunchConsumed = false; TargetReleased = false; TargetReleaseAt = None; Returned = false; CleanupWasComplete = false; StdoutTerminal = None; StderrTerminal = None; ExitTerminal = None }
    let private copyCapture (capture: StreamCapture) =
        { capture with Prefix = ReadOnlyMemory(capture.Prefix.ToArray()); Tail = ReadOnlyMemory(capture.Tail.ToArray()) }
    let snapshot state = { state.Result with Stdout = copyCapture state.Result.Stdout; Stderr = copyCapture state.Result.Stderr }
    let private positive = function Known(true,_) -> true | _ -> false
    let private capabilityObtained capability (now: ClockStamp) (result: ExecutionResult) =
        result.Capabilities |> List.exists (fun item -> item.Capability = capability && (match item.Obtained with Known(_,stamp) -> stamp.At = Some now | _ -> false))
    let private capabilityRequired capability state =
        state.Command.Capabilities |> List.exists (fun item -> item.Capability = capability && item.Required)
    let private settled = function Known(Settled,_) -> true | _ -> false
    let private knownReader = function Known(Eof,_) | Known(StoppedWithoutEof _,_) | Known(ReadFailed _,_) -> true | _ -> false
    let private retainExit oldValue newValue =
        // Exact-generation exit is irreversible; the original stamp stays with the fact.
        match oldValue with Known _ -> oldValue | _ -> newValue
    let private operationScope (state: LifecycleState) = state.Result.Identity.InvocationId.ToString("D")
    let private processGeneration (state: LifecycleState) = match state.Result.Launch with Started g -> Some g | _ -> None
    let private proofErrors (state: LifecycleState) (now: ClockStamp) (fresh: bool) (expected: ProcessGeneration option) observationValue =
        match observationValue with
        | Known(_, stamp) | Absent stamp ->
            [ if stamp.Scope <> operationScope state then { Code = "observation-scope-refused"; Field = "observation.scope" }
              if stamp.Generation <> expected then { Code = "observation-generation-refused"; Field = "observation.generation" }
              match stamp.At with
              | Some at ->
                  let minimum = if expected.IsSome then (state.TargetReleaseAt |> Option.defaultValue (Budget.describe state.Result.OriginalBudget).StartedAt) else (Budget.describe state.Result.OriginalBudget).StartedAt
                  if at.Domain <> now.Domain || at.Ticks > now.Ticks || at.Ticks < minimum || at.Ticks >= (Budget.describe state.Result.OriginalBudget).TotalExpiresAt then
                      yield { Code = "observation-clock-refused"; Field = "observation.at" }
                  elif fresh && at.Ticks <> now.Ticks then
                      yield { Code = "observation-stale-refused"; Field = "observation.at" }
              | None -> yield { Code = "observation-clock-required"; Field = "observation.at" } ]
        | _ -> []
    let private phaseAdvance current proposed =
        // Work cannot reopen after retirement/return, and terminal settlement cannot regress.
        match current, proposed with
        | Terminal, _ -> Terminal
        | Retiring, Terminal -> Terminal
        | Retiring, _ -> Retiring
        | Running, Prepared | Running, Starting -> Running
        | Starting, Prepared -> Starting
        | _ -> proposed
    let private increment value = if value = Int64.MaxValue then value else value + 1L
    let private omitCount value count = if count > Int64.MaxValue - value then Int64.MaxValue else value + count
    let private measureMetadata (result: ExecutionResult) =
        // Conservative upper bound includes fixed record/enum/framing allowance and JSON escaping.
        let mutable total = 4096L
        let text (s: string) = total <- total + int64 (Encoding.UTF8.GetByteCount s) * 6L + 64L
        let evidence (e: EvidenceRef) = text e.Kind; text e.Reference; Option.iter text e.Digest
        let diagnostic (d: Diagnostic) = text d.Code; text d.Message; Option.iter evidence d.Evidence
        let stamp (s: ObservationStamp) =
            total <- total + 512L
            text s.Scope; evidence s.Provenance
            s.Generation |> Option.iter (fun g -> total <- total + 128L; text g.Platform; text g.ScopeId; text g.BirthToken)
        let obs visit = function
            | Known(v,s) -> stamp s; visit v
            | Absent s -> stamp s
            | Pending d | Unknown d | Unavailable d | Invalid d | LimitExceeded d -> diagnostic d
        let generation (g: ProcessGeneration) = text g.Platform; text g.ScopeId; text g.BirthToken
        let failure (f: Failure) = total <- total + 512L; diagnostic f.Diagnostic
        text result.Identity.SourceDigest; text result.Identity.ProfileId; text result.Identity.ProfileDigest
        match result.Launch with Refused d | Ambiguous d -> diagnostic d | Started g -> generation g | _ -> ()
        match result.Stop with StopRefused d -> diagnostic d | _ -> ()
        obs ignore result.Exit
        let reader = function StoppedWithoutEof d | ReadFailed d -> diagnostic d | _ -> ()
        obs reader result.Stdout.Reader; obs reader result.Stderr.Reader
        result.Capabilities |> List.iter (fun c -> obs evidence c.Obtained)
        result.Limits |> List.iter (fun l -> text l.Name; text l.Unit; text l.Requested; obs text l.Actual; obs ignore l.Assurance)
        result.Violations |> List.iter diagnostic
        Option.iter failure result.FirstFailure; List.iter failure result.AdditionalFailures
        List.iter diagnostic result.Cleanup.RequestedActions
        obs ignore result.Cleanup.LeaderTermination; obs ignore result.Cleanup.WorkloadEmpty; obs ignore result.Cleanup.OwnedChildrenReaped
        obs (function TaskUnknown d -> diagnostic d | _ -> ()) result.Cleanup.LocalTasks
        List.iter generation result.Cleanup.Unresolved
        match result.Cleanup.Custody with
        | RetainedByOwner owner -> text owner.OwnerId; evidence owner.Evidence
        | Released s -> stamp s
        | AuthorityUnavailable d -> diagnostic d
        | NoEffect -> ()
        total
    let metadataBytes state = measureMetadata state.Result
    let private insertFailure command failure (result: ExecutionResult) =
        let failure = { failure with Diagnostic = Validation.sanitizeDiagnostic command failure.Diagnostic }
        let next = addFailure failure result
        if measureMetadata next <= int64 Validation.limits.ResultMetadataBytes then next
        else
            match result.FirstFailure with
            | Some _ -> { result with OmittedFailures = increment result.OmittedFailures }
            | None ->
                // Ordinary updates reserve space for this fixed-size minimal cause.
                let compact = { failure with Diagnostic = { failure.Diagnostic with Message = "First-cause message/evidence omitted by metadata bound."; Evidence = None } }
                { result with FirstFailure = Some compact; OmittedDiagnosticDetails = increment result.OmittedDiagnosticDetails }
    let private compactObservation value =
        let compact (d: Diagnostic) = { d with Message = "Omitted."; Evidence = None }
        match value with
        | Pending d -> Pending(compact d)
        | Unavailable d -> Unavailable(compact d)
        | Unknown d -> Unknown(compact d)
        | Invalid d -> Invalid(compact d)
        | LimitExceeded d -> LimitExceeded(compact d)
        | _ -> value
    let private compactFailureBundle (result: ExecutionResult) =
        let mutable omissions = 0L
        let diagnostic (value: Diagnostic) =
            let compact = { value with Message = "Omitted."; Evidence = None }
            if compact <> value then omissions <- increment omissions
            compact
        let observation value =
            match value with
            | Pending d -> Pending(diagnostic d) | Unknown d -> Unknown(diagnostic d)
            | Unavailable d -> Unavailable(diagnostic d) | Invalid d -> Invalid(diagnostic d)
            | LimitExceeded d -> LimitExceeded(diagnostic d)
            | _ -> value
        let cleanup = result.Cleanup
        let next =
            { result with
                Exit = observation result.Exit
                Stdout = { result.Stdout with Reader = observation result.Stdout.Reader }
                Stderr = { result.Stderr with Reader = observation result.Stderr.Reader }
                Launch = match result.Launch with Refused d -> Refused(diagnostic d) | Ambiguous d -> Ambiguous(diagnostic d) | other -> other
                Stop = match result.Stop with StopRefused d -> StopRefused(diagnostic d) | other -> other
                Capabilities = result.Capabilities |> List.map (fun c -> { c with Obtained = observation c.Obtained })
                Limits = result.Limits |> List.map (fun l -> { l with Actual = observation l.Actual; Assurance = observation l.Assurance })
                FirstFailure = result.FirstFailure |> Option.map (fun f -> { f with Diagnostic = diagnostic f.Diagnostic })
                AdditionalFailures = result.AdditionalFailures |> List.map (fun f -> { f with Diagnostic = diagnostic f.Diagnostic })
                Violations = result.Violations |> List.map diagnostic
                Cleanup = { cleanup with LeaderTermination = observation cleanup.LeaderTermination
                                         WorkloadEmpty = observation cleanup.WorkloadEmpty
                                         OwnedChildrenReaped = observation cleanup.OwnedChildrenReaped
                                         LocalTasks = observation cleanup.LocalTasks
                                         RequestedActions = cleanup.RequestedActions |> List.map diagnostic
                                         Custody = match cleanup.Custody with AuthorityUnavailable d -> AuthorityUnavailable(diagnostic d) | other -> other } }
        { next with OmittedDiagnosticDetails = omitCount next.OmittedDiagnosticDetails omissions }
    let private omitSecondaryProof (result: ExecutionResult) =
        // Positive secondary metadata is not restored from history when its proof cannot fit.
        // A missing public proof is Unknown, never a shorter invented Known provenance.
        let mutable omissions = 0L
        let observation value =
            match value with
            | Known _ | Absent _ -> omissions <- increment omissions; Unknown(note "proof-omitted-by-metadata")
            | _ -> value
        let next =
            { result with
                Capabilities = result.Capabilities |> List.map (fun c -> { c with Obtained = observation c.Obtained })
                Limits = result.Limits |> List.map (fun l -> { l with Actual = observation l.Actual; Assurance = observation l.Assurance }) }
        { next with OmittedDiagnosticDetails = omitCount next.OmittedDiagnosticDetails omissions }
    let private diagnosticProjection (result: ExecutionResult) =
        // A failure-bearing incoming cleanup can exceed the result ceiling even
        // after all textual diagnostics are compacted. Report missing proof as
        // uncertainty; never create shorter positive provenance or stale proof.
        let mutable omissions = 0L
        let observation value =
            match value with
            | Known _ | Absent _ -> omissions <- increment omissions; Unknown(note "proof-omitted-by-metadata")
            | _ -> value
        let reader value =
            match value with
            | Known(ReadFailed d,_) | Known(StoppedWithoutEof d,_) ->
                omissions <- increment omissions
                Unknown { d with Message = "Omitted."; Evidence = None }
            | _ -> observation value
        let cleanup = result.Cleanup
        let custody =
            match cleanup.Custody with
            | RetainedByOwner _ | Released _ | NoEffect -> omissions <- increment omissions; AuthorityUnavailable(note "custody-proof-omitted-by-metadata")
            | other -> other
        let projected =
            { result with
                Exit = observation result.Exit
                Stdout = { result.Stdout with Reader = reader result.Stdout.Reader }
                Stderr = { result.Stderr with Reader = reader result.Stderr.Reader }
                AdditionalFailures = []; OmittedFailures = omitCount result.OmittedFailures (int64 result.AdditionalFailures.Length)
                Cleanup = { cleanup with LeaderTermination = observation cleanup.LeaderTermination
                                         WorkloadEmpty = observation cleanup.WorkloadEmpty
                                         OwnedChildrenReaped = observation cleanup.OwnedChildrenReaped
                                         LocalTasks = observation cleanup.LocalTasks
                                         Custody = custody; Unresolved = []
                                         OmittedUnresolved = omitCount cleanup.OmittedUnresolved (int64 cleanup.Unresolved.Length)
                                         Disposition = if cleanup.Disposition = Complete then Incomplete else cleanup.Disposition } }
        { projected with OmittedDiagnosticDetails = omitCount projected.OmittedDiagnosticDetails omissions }
    let private recordViolation command now (origin: ExecutionPhase) (diagnostic: Diagnostic) (result: ExecutionResult) =
        let next = insertFailure command { Phase = origin; Diagnostic = diagnostic; At = Some now } result
        let next = { next with Phase = phaseAdvance next.Phase Retiring }
        let withViolation = { next with Violations = next.Violations @ [ Validation.sanitizeDiagnostic command diagnostic ] }
        if next.Violations.Length < Validation.limits.CollectionEntries && measureMetadata withViolation <= int64 Validation.limits.ResultMetadataBytes then withViolation
        else
            let compact = { diagnostic with Message = "Violation detail omitted by metadata bound."; Evidence = None }
            let compactResult = { next with Violations = next.Violations @ [ compact ]; OmittedDiagnosticDetails = increment next.OmittedDiagnosticDetails }
            if next.Violations.Length < Validation.limits.CollectionEntries && measureMetadata compactResult <= int64 Validation.limits.ResultMetadataBytes then compactResult
            else { next with OmittedDiagnosticDetails = increment next.OmittedDiagnosticDetails }
    let private applyCore (now: ClockStamp) event (state: LifecycleState) =
        match Budget.remaining now state.Result.OriginalBudget with
        | Error _ -> refused "clock.domain-or-start"
        | Ok _ when now.Ticks < state.LastTick -> refused "clock.regression"
        | Ok view ->
            let result = state.Result
            let isWork = match event with BeginLaunch | TargetReleaseRequested -> true | _ -> false
            if (view.WorkExpired || state.Returned || result.Phase = Retiring || result.Phase = Terminal) && isWork then refused "work.closed"
            elif view.TotalExpired && event <> ReturnRequested then refused "budget.total-expired"
            else
                let finish (next: LifecycleState) =
                    let limit = Validation.limits.ResultMetadataBytes - (if next.Result.FirstFailure.IsNone then Validation.limits.FirstCauseReserveBytes else 0)
                    let rec fit (reported: ExecutionResult) =
                        if measureMetadata reported <= int64 limit then Some reported
                        elif reported.FirstFailure.IsSome && not reported.Cleanup.RequestedActions.IsEmpty then
                            // Bounded secondary action evidence yields to the original cause/current
                            // proof. Count each omitted diagnostic record; never discard violations.
                            let actions = reported.Cleanup.RequestedActions |> List.rev |> List.tail |> List.rev
                            fit { reported with Cleanup = { reported.Cleanup with RequestedActions = actions }; OmittedDiagnosticDetails = increment reported.OmittedDiagnosticDetails }
                        else None
                    let reported =
                        match fit next.Result with
                        | Some value -> Some value
                        | None when next.Result.FirstFailure.IsSome ->
                            let compact = compactFailureBundle next.Result
                            match fit compact with
                            | Some value -> Some value
                            | None ->
                                let secondary = omitSecondaryProof compact
                                match fit secondary with
                                | Some value -> Some value
                                | None ->
                                    let rec boundedProjection projected =
                                        match fit projected with
                                        | Some value -> Some value
                                        | None when projected.Violations.Length > 1 ->
                                            let retained = projected.Violations |> List.rev |> List.tail |> List.rev
                                            boundedProjection { projected with Violations = retained; OmittedDiagnosticDetails = increment projected.OmittedDiagnosticDetails }
                                        | None -> None
                                    boundedProjection (diagnosticProjection secondary)
                        | None -> None
                    match reported with
                    | None -> refused "result.metadata-limit"
                    | Some reported -> Ok { next with LastTick = now.Ticks; Result = { reported with Phase = phaseAdvance result.Phase reported.Phase } }
                let update nextResult = finish { state with Result = nextResult }
                let validated errors next = if errors.IsEmpty then next() else Error errors
                match event with
                | BeginLaunch ->
                    if state.LaunchConsumed || result.Stop <> NotRequested then refused "launch.already-consumed-or-stopped"
                    else finish { state with LaunchConsumed = true; Result = { result with Phase = Starting } }
                | TargetReleaseRequested ->
                    let missing = state.Command.Capabilities |> List.exists (fun r -> r.Required && not (capabilityObtained r.Capability now result))
                    let requestedLimits =
                        [ if state.Command.Resources.MemoryBytes.IsSome then "memory"
                          if state.Command.Resources.CpuQuota.IsSome then "cpuQuota"
                          if state.Command.Resources.CpuAffinity.IsSome then "cpuAffinity"
                          if state.Command.Resources.MaxKernelTasks.IsSome then "kernelTasks"
                          if state.Command.Resources.MaxOwnedProcesses.IsSome then "ownedProcesses"
                          if state.Command.Resources.MaxClrProcesses.IsSome then "clrProcesses" ]
                    let retainedOwnerMissing =
                        capabilityRequired BoundedReturnWithRetainedCustody state
                        && (match result.Cleanup.Custody with RetainedByOwner _ -> false | _ -> true)
                    let limitsMissing = requestedLimits |> List.exists (fun name -> result.Limits |> List.exists (fun l -> l.Name = name && (match l.Actual,l.Assurance with Known(_,a),Known(_,b) -> a = b && a.At = Some now | _ -> false)) |> not)
                    if not state.LaunchConsumed || state.TargetReleased || missing || limitsMissing || retainedOwnerMissing || result.Stop <> NotRequested then refused "target.release-prerequisites"
                    else
                        let custody = match result.Cleanup.Custody with RetainedByOwner _ as retained -> retained | _ -> AuthorityUnavailable(note "authority-unreported")
                        finish { state with TargetReleased = true; TargetReleaseAt = Some now.Ticks; Result = { result with Cleanup = { result.Cleanup with Custody = custody } } }
                | LaunchObserved launch ->
                    let compatible =
                        match result.Launch,launch with
                        | NotStarted, (Started _ | Refused _ | Ambiguous _) -> true
                        | Ambiguous _, (Started _ | Refused _ | Ambiguous _) -> true
                        | Started old, Started next -> old = next
                        | Refused old, Refused next -> old = next
                        | _ -> false
                    if not state.LaunchConsumed || not compatible then refused "launch.observation"
                    else
                        let errors =
                            match launch with
                            | Started g -> Validation.generation g @ (if g.ScopeId <> operationScope state then [ { Code = "process-scope-refused"; Field = "process.scope" } ] else [])
                            | Refused d | Ambiguous d -> Validation.diagnostic d
                            | NotStarted -> [ { Code = "invalid-value"; Field = "launch" } ]
                        if (match launch with Started _ -> not state.TargetReleased | _ -> false) then refused "launch.before-target-release"
                        else validated errors (fun () -> update { result with Launch = launch; Phase = match launch with Started _ -> Running | _ -> Retiring })
                | CapabilitiesObtained values ->
                    if (values |> List.truncate (Validation.limits.CollectionEntries + 1) |> List.length) > Validation.limits.CollectionEntries || (values |> List.map _.Capability |> Set.ofList).Count <> values.Length then refused "capabilities.duplicate-or-limit"
                    else validated (List.collect Validation.capability values @ (values |> List.collect (fun c -> proofErrors state now true (processGeneration state) c.Obtained))) (fun () ->
                        let current = values |> List.fold (fun old value -> value :: (old |> List.filter (fun prior -> prior.Capability <> value.Capability))) result.Capabilities
                        if current.Length > Validation.limits.CollectionEntries then refused "capabilities.limit"
                        else
                            let next = { result with Capabilities = current }
                            let limit = Validation.limits.ResultMetadataBytes - (if result.FirstFailure.IsNone then Validation.limits.FirstCauseReserveBytes else 0)
                            let next =
                                if measureMetadata next <= int64 limit then next
                                else
                                    let compact = current |> List.map (fun c -> { c with Obtained = compactObservation c.Obtained })
                                    let omissions = List.zip current compact |> List.filter (fun (old, next) -> old <> next) |> List.length |> int64
                                    { next with Capabilities = compact; OmittedDiagnosticDetails = omitCount next.OmittedDiagnosticDetails omissions }
                            let violated = values |> List.choose (fun v -> match v.Obtained with LimitExceeded d -> Some d | _ -> None)
                            update (violated |> List.fold (fun next d -> recordViolation state.Command now result.Phase d next) next))
                | LimitObserved value ->
                    let actualStamp = match value.Actual with Known(_,s) -> Some s | _ -> None
                    let assuranceStamp = match value.Assurance with Known(_,s) -> Some s | _ -> None
                    let bundleErrors = if actualStamp.IsSome && assuranceStamp.IsSome && actualStamp <> assuranceStamp then [ { Code = "limit-bundle-refused"; Field = "limit.provenance" } ] else []
                    validated (Validation.obtainedLimit state.Command.Resources value @ bundleErrors @ proofErrors state now true (processGeneration state) value.Actual @ proofErrors state now true (processGeneration state) value.Assurance) (fun () ->
                        // One latest whole bundle: incomplete values cannot inherit old proof.
                        let next = { result with Limits = value :: (result.Limits |> List.filter (fun old -> old.Name <> value.Name)) }
                        let limit = Validation.limits.ResultMetadataBytes - (if result.FirstFailure.IsNone then Validation.limits.FirstCauseReserveBytes else 0)
                        let next =
                            if measureMetadata next <= int64 limit then next
                            else
                                let compact = { value with Actual = compactObservation value.Actual; Assurance = compactObservation value.Assurance }
                                let omissions = (if compact.Actual <> value.Actual then 1L else 0L) + (if compact.Assurance <> value.Assurance then 1L else 0L)
                                { next with Limits = compact :: (result.Limits |> List.filter (fun old -> old.Name <> value.Name)); OmittedDiagnosticDetails = omitCount next.OmittedDiagnosticDetails omissions }
                        let violated =
                            [ match value.Actual with LimitExceeded d -> yield d | _ -> ()
                              match value.Assurance with LimitExceeded d -> yield d | _ -> () ]
                        update (violated |> List.fold (fun next d -> recordViolation state.Command now result.Phase d next) next))
                | ExitObserved value ->
                    let positiveWithoutGeneration = match value with Known _ -> (processGeneration state).IsNone | _ -> false
                    if positiveWithoutGeneration || (not state.TargetReleased && result.Launch <> NotStarted && (match result.Launch with Refused _ -> false | _ -> true)) then refused "exit.without-generation"
                    else validated (Validation.observation value @ proofErrors state now false (processGeneration state) value) (fun () ->
                        match state.ExitTerminal,value with
                        | Some(Known(old,_)), Known(next,_) when old <> next -> refused "exit.contradiction"
                        | _ ->
                            let terminal = match state.ExitTerminal,value with None,Known _ -> Some value | _ -> state.ExitTerminal
                            let observed =
                                match value,state.ExitTerminal with
                                | Known _,Some original -> original
                                | _ -> retainExit result.Exit value
                            finish { state with ExitTerminal = terminal; Result = { result with Exit = observed; Phase = Retiring } })
                | StopRequested reason ->
                    let stop = match result.Stop with NotRequested -> Requested(reason, now) | previous -> previous
                    update { result with Stop = stop; Phase = Retiring }
                | ReaderObserved(channel, value) ->
                    let nested = match value with Known(StoppedWithoutEof d,_) | Known(ReadFailed d,_) -> Validation.diagnostic d | _ -> []
                    let knownWithoutGeneration = match value with Known _ -> (processGeneration state).IsNone | _ -> false
                    if knownWithoutGeneration then refused "reader.without-generation"
                    else validated (Validation.observation value @ nested @ proofErrors state now false (processGeneration state) value) (fun () ->
                        let terminal = match channel with Stdout -> state.StdoutTerminal | Stderr -> state.StderrTerminal | _ -> None
                        let incoming = match value with Known(v,_) -> Some v | _ -> None
                        let isTerminal = function Eof | StoppedWithoutEof _ | ReadFailed _ -> true | _ -> false
                        let contradicted = match terminal,incoming with Some old,Some next -> old <> next | _ -> false
                        let observed =
                            let old = match channel with Stdout -> result.Stdout.Reader | Stderr -> result.Stderr.Reader | _ -> Pending(note "invalid-channel")
                            match old with Known(v,_) when isTerminal v -> old | _ -> value
                        let next =
                            match channel with
                            | Stdout -> Some { result with Stdout = { result.Stdout with Reader = observed } }
                            | Stderr -> Some { result with Stderr = { result.Stderr with Reader = observed } }
                            | _ -> None
                        if contradicted then refused "reader.terminal-contradiction"
                        else
                            match next with
                            | None -> refused "reader.channel"
                            | Some next ->
                                let terminal = match terminal,incoming with None,Some v when isTerminal v -> Some v | _ -> terminal
                                let nextState =
                                    match channel with Stdout -> { state with StdoutTerminal = terminal } | Stderr -> { state with StderrTerminal = terminal } | _ -> state
                                let originalLimit = Validation.limits.ResultMetadataBytes - (if result.FirstFailure.IsNone then Validation.limits.FirstCauseReserveBytes else 0)
                                let next =
                                    if measureMetadata next <= int64 originalLimit then next
                                    else
                                        // Retain the bounded per-reader terminal outcome privately; publish
                                        // explicit omitted evidence without allowing a reader retry.
                                        let omitted =
                                            match value with
                                            | Known(ReadFailed d,_) | Known(StoppedWithoutEof d,_) -> Unknown { d with Message = "Omitted."; Evidence = None }
                                            | Known _ | Absent _ -> Unknown(note "reader-proof-omitted-by-metadata")
                                            | other -> compactObservation other
                                        let omittedResult = match channel with Stdout -> { next with Stdout = { next.Stdout with Reader = omitted } } | _ -> { next with Stderr = { next.Stderr with Reader = omitted } }
                                        { omittedResult with OmittedDiagnosticDetails = increment omittedResult.OmittedDiagnosticDetails }
                                match value with
                                | Known(ReadFailed d,_) when (match state.StdoutTerminal,state.StderrTerminal,channel with Some(ReadFailed _),_,Stdout | _,Some(ReadFailed _),Stderr -> false | _ -> true) ->
                                    let failed = insertFailure state.Command { Phase = result.Phase; Diagnostic = d; At = Some now } next
                                    finish { nextState with Result = { failed with Phase = Retiring; Stop = match failed.Stop with NotRequested -> Requested(SinkFailure,now) | old -> old } }
                                | _ -> finish { nextState with Result = next })
                | OutputObserved value ->
                    validated (Validation.output state.Command.Capture value) (fun () ->
                        if value.ChargedTotal < result.ChargedTotalBytes || value.EvidenceBytes < result.EvidenceBytes || value.TerminalEvidenceBytes < result.TerminalEvidenceBytes || value.Stdout.Totals.Observed < result.Stdout.Totals.Observed || value.Stderr.Totals.Observed < result.Stderr.Totals.Observed then refused "output.regression"
                        else
                            let next = { result with Stdout = { (copyCapture value.Stdout) with Reader = result.Stdout.Reader }; Stderr = { (copyCapture value.Stderr) with Reader = result.Stderr.Reader }
                                                     ChargedTotalBytes = value.ChargedTotal; EvidenceBytes = value.EvidenceBytes; TerminalEvidenceBytes = value.TerminalEvidenceBytes }
                            match value.FirstFailure with
                            | None -> update next
                            | Some d ->
                                let failed = insertFailure state.Command { Phase = result.Phase; Diagnostic = d; At = Some now } next
                                update { failed with Phase = Retiring; Stop = match failed.Stop with NotRequested -> Requested(OutputLimit,now) | old -> old })
                | RetirementActionRequested diagnostic ->
                    validated (Validation.diagnostic diagnostic) (fun () ->
                        if result.Cleanup.RequestedActions.Length >= Validation.limits.CollectionEntries then refused "cleanup.actions-limit"
                        else update { result with Phase = Retiring; Cleanup = { result.Cleanup with RequestedActions = result.Cleanup.RequestedActions @ [ Validation.sanitizeDiagnostic state.Command diagnostic ] } })
                | CleanupObserved value ->
                    let expected = processGeneration state
                    let releasedProof = match value.Custody with Released stamp -> proofErrors state now true expected (Absent stamp) | _ -> []
                    let scopeErrors = value.Unresolved |> List.truncate (Validation.limits.CollectionEntries + 1) |> List.collect (fun g -> if g.ScopeId = operationScope state then [] else [ { Code = "process-scope-refused"; Field = "cleanup.unresolved" } ])
                    validated (Validation.cleanup value @ scopeErrors @ releasedProof @ proofErrors state now true expected value.LeaderTermination @ proofErrors state now true expected value.WorkloadEmpty @ proofErrors state now true expected value.OwnedChildrenReaped @ proofErrors state now true expected value.LocalTasks) (fun () ->
                        // Latest complete evidence bundle; never combine old emptiness/termination.
                        let noEffect = match result.Launch with Refused _ -> true | NotStarted -> not state.TargetReleased | _ -> false
                        let complete =
                            if noEffect then value.Custody = NoEffect && settled value.LocalTasks
                            else positive value.LeaderTermination && settled value.LocalTasks && knownReader result.Stdout.Reader && knownReader result.Stderr.Reader
                                 && (not (capabilityRequired WorkloadEmptyObservation state) || positive value.WorkloadEmpty)
                                 && (not (capabilityRequired OwnedChildReaping state) || positive value.OwnedChildrenReaped)
                        let custodyAllowed =
                            match value.Custody with
                            | NoEffect -> noEffect
                            | Released _ -> settled value.LocalTasks && (noEffect || (positive value.LeaderTermination && knownReader result.Stdout.Reader && knownReader result.Stderr.Reader))
                            | _ -> true
                        if not custodyAllowed || (value.Disposition = Complete && not complete) || (state.CleanupWasComplete && value.Disposition <> Complete) then refused "cleanup.proof"
                        else
                            let phase = if value.Disposition = Complete then Terminal elif result.Phase = Prepared || result.Phase = Starting then result.Phase else Retiring
                            let next = { result with Cleanup = value; Phase = phase }
                            let violated =
                                [ match value.LeaderTermination with LimitExceeded d -> yield d | _ -> ()
                                  match value.WorkloadEmpty with LimitExceeded d -> yield d | _ -> ()
                                  match value.OwnedChildrenReaped with LimitExceeded d -> yield d | _ -> ()
                                  match value.LocalTasks with LimitExceeded d -> yield d | _ -> () ]
                            finish { state with CleanupWasComplete = state.CleanupWasComplete || value.Disposition = Complete
                                                Result = violated |> List.fold (fun next d -> recordViolation state.Command now result.Phase d next) next })
                | FailureObserved failure ->
                    let clockError = match failure.At with Some at when at.Domain <> now.Domain || at.Ticks > now.Ticks || at.Ticks < (Budget.describe result.OriginalBudget).StartedAt -> [ { Code = "failure-clock-refused"; Field = "failure.at" } ] | _ -> []
                    validated (Validation.diagnostic failure.Diagnostic @ clockError) (fun () ->
                        update { (insertFailure state.Command failure result) with Phase = Retiring })
                | ReturnRequested ->
                    finish { state with Returned = true; Result = { result with Phase = if result.Cleanup.Disposition = Complete then Terminal else Retiring } }

    let apply now event state =
        try applyCore now event state
        with
        | :? ArgumentException | :? NullReferenceException | :? OverflowException ->
            Error [ { Code = "invalid-event"; Field = "event.structure" } ]
