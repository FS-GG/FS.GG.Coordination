namespace FS.GG.Execution.Contracts
open System
open System.Text

type LifecycleState = private
    { Command: CommandSpec; Result: ExecutionResult; LastTick: int64
      LaunchConsumed: bool; TargetReleased: bool; Returned: bool }

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
                  Stop = NotRequested; Stdout = empty; Stderr = empty; ChargedTotalBytes = 0L; EvidenceBytes = 0L
                  Capabilities = []; Limits = []; Violations = []; FirstFailure = None; AdditionalFailures = []; OmittedFailures = 0L
                  Cleanup = { Disposition = CleanupUnknown; RequestedActions = []; LeaderTermination = Pending(note "termination-pending")
                              WorkloadEmpty = Unknown(note "workload-unknown"); OwnedChildrenReaped = Unknown(note "reaping-unknown")
                              LocalTasks = Unknown(note "tasks-unknown"); Unresolved = []; OmittedUnresolved = 0L; Custody = NoEffect }
                  OriginalBudget = budget }
            Ok { Command = { command with Input = input }; Result = result; LastTick = (Budget.describe budget).StartedAt
                 LaunchConsumed = false; TargetReleased = false; Returned = false }
    let private copyCapture (capture: StreamCapture) =
        { capture with Prefix = ReadOnlyMemory(capture.Prefix.ToArray()); Tail = ReadOnlyMemory(capture.Tail.ToArray()) }
    let snapshot state = { state.Result with Stdout = copyCapture state.Result.Stdout; Stderr = copyCapture state.Result.Stderr }
    let private positive = function Known(true,_) -> true | _ -> false
    let private capabilityObtained capability result =
        result.Capabilities |> List.exists (fun item -> item.Capability = capability && (match item.Obtained with Known _ -> true | _ -> false))
    let private capabilityRequired capability state =
        state.Command.Capabilities |> List.exists (fun item -> item.Capability = capability && item.Required)
    let private settled = function Known(Settled,_) -> true | _ -> false
    let private knownReader = function Known(Eof,_) | Known(StoppedWithoutEof _,_) | Known(ReadFailed _,_) -> true | _ -> false
    let private retainKnown oldValue newValue =
        match oldValue,newValue with
        | Known _, Known _ -> newValue
        | Known _, _ -> oldValue
        | Absent _, Unknown _ | Absent _, Pending _ | Absent _, Unavailable _ -> oldValue
        | _, _ -> newValue
    let private clockErrors now observationValue =
        match observationValue with
        | Known(_, stamp) | Absent stamp ->
            match stamp.At with
            | Some at when at.Domain <> now.Domain || at.Ticks > now.Ticks -> [ { Code = "observation-clock-refused"; Field = "observation.at" } ]
            | _ -> []
        | _ -> []
    let private metadataBytes (result: ExecutionResult) =
        // Conservative upper bound includes fixed record/enum/framing allowance and JSON escaping.
        let mutable total = 4096L
        let text (s: string) = total <- total + int64 (Encoding.UTF8.GetByteCount s) * 6L + 64L
        let evidence (e: EvidenceRef) = text e.Kind; text e.Reference; Option.iter text e.Digest
        let diagnostic (d: Diagnostic) = text d.Code; text d.Message; Option.iter evidence d.Evidence
        let stamp (s: ObservationStamp) = text s.Scope; evidence s.Provenance
        let obs visit = function
            | Known(v,s) -> stamp s; visit v
            | Absent s -> stamp s
            | Pending d | Unknown d | Unavailable d | Invalid d | LimitExceeded d -> diagnostic d
        let generation (g: ProcessGeneration) = text g.Platform; text g.ScopeId; text g.BirthToken
        let failure (f: Failure) = diagnostic f.Diagnostic
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
                    if metadataBytes next.Result > int64 Validation.limits.ResultMetadataBytes then refused "result.metadata-limit"
                    else Ok { next with LastTick = now.Ticks }
                let update nextResult = finish { state with Result = nextResult }
                let validated errors next = if errors.IsEmpty then next() else Error errors
                match event with
                | BeginLaunch ->
                    if state.LaunchConsumed || result.Stop <> NotRequested then refused "launch.already-consumed-or-stopped"
                    else finish { state with LaunchConsumed = true; Result = { result with Phase = Starting } }
                | TargetReleaseRequested ->
                    let missing = state.Command.Capabilities |> List.exists (fun r -> r.Required && not (capabilityObtained r.Capability result))
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
                    let limitsMissing = requestedLimits |> List.exists (fun name -> result.Limits |> List.exists (fun l -> l.Name = name && (match l.Actual,l.Assurance with Known _,Known _ -> true | _ -> false)) |> not)
                    if not state.LaunchConsumed || state.TargetReleased || missing || limitsMissing || retainedOwnerMissing || result.Stop <> NotRequested then refused "target.release-prerequisites"
                    else
                        let custody = match result.Cleanup.Custody with RetainedByOwner _ as retained -> retained | _ -> AuthorityUnavailable(note "authority-unreported")
                        finish { state with TargetReleased = true; Result = { result with Cleanup = { result.Cleanup with Custody = custody } } }
                | LaunchObserved launch ->
                    if not state.LaunchConsumed || result.Launch <> NotStarted then refused "launch.observation"
                    else
                        let errors = match launch with Started g -> Validation.generation g | Refused d | Ambiguous d -> Validation.diagnostic d | NotStarted -> [ { Code = "invalid-value"; Field = "launch" } ]
                        if (match launch with Started _ -> not state.TargetReleased | _ -> false) then refused "launch.before-target-release"
                        else validated errors (fun () -> update { result with Launch = launch; Phase = match launch with Started _ -> Running | _ -> Retiring })
                | CapabilitiesObtained values ->
                    if state.TargetReleased || (values |> List.truncate (Validation.limits.CollectionEntries + 1) |> List.length) > Validation.limits.CollectionEntries || (values |> List.map _.Capability |> Set.ofList).Count <> values.Length then refused "capabilities.closed-or-duplicate"
                    else validated (List.collect Validation.capability values @ (values |> List.collect (fun c -> clockErrors now c.Obtained))) (fun () ->
                        let merged = values |> List.fold (fun current value ->
                            match current |> List.tryFind (fun old -> old.Capability = value.Capability) with
                            | Some old -> { value with Obtained = retainKnown old.Obtained value.Obtained } :: (current |> List.filter (fun old -> old.Capability <> value.Capability))
                            | None -> value :: current) result.Capabilities
                        if merged.Length > Validation.limits.CollectionEntries then refused "capabilities.limit" else update { result with Capabilities = merged })
                | LimitObserved value ->
                    if state.TargetReleased then refused "limits.closed"
                    else validated (Validation.obtainedLimit state.Command.Resources value @ clockErrors now value.Actual @ clockErrors now value.Assurance) (fun () ->
                        let previous = result.Limits |> List.tryFind (fun old -> old.Name = value.Name)
                        let joined = match previous with Some old -> { value with Actual = retainKnown old.Actual value.Actual; Assurance = retainKnown old.Assurance value.Assurance } | None -> value
                        let current = result.Limits |> List.filter (fun old -> old.Name <> value.Name)
                        update { result with Limits = joined :: current })
                | ExitObserved value ->
                    if not state.TargetReleased then refused "exit.before-release"
                    else validated (Validation.observation value @ clockErrors now value) (fun () ->
                        match result.Exit,value with
                        | Known(old,_), Known(next,_) when old <> next -> refused "exit.contradiction"
                        | _ -> update { result with Exit = retainKnown result.Exit value; Phase = Retiring })
                | StopRequested reason ->
                    let stop = match result.Stop with NotRequested -> Requested(reason, now) | previous -> previous
                    update { result with Stop = stop; Phase = Retiring }
                | ReaderObserved(channel, value) ->
                    let nested = match value with Known(StoppedWithoutEof d,_) | Known(ReadFailed d,_) -> Validation.diagnostic d | _ -> []
                    validated (Validation.observation value @ nested @ clockErrors now value) (fun () ->
                        let retain old =
                            match old,value with Known(Eof,_), Known(next,_) when next <> Eof -> None | _ -> Some(retainKnown old value)
                        match channel with
                        | Stdout -> match retain result.Stdout.Reader with Some v -> update { result with Stdout = { result.Stdout with Reader = v } } | _ -> refused "reader.eof-regression"
                        | Stderr -> match retain result.Stderr.Reader with Some v -> update { result with Stderr = { result.Stderr with Reader = v } } | _ -> refused "reader.eof-regression"
                        | _ -> refused "reader.channel")
                | OutputObserved value ->
                    validated (Validation.output state.Command.Capture value) (fun () ->
                        if value.ChargedTotal < result.ChargedTotalBytes || value.EvidenceBytes < result.EvidenceBytes || value.Stdout.Totals.Observed < result.Stdout.Totals.Observed || value.Stderr.Totals.Observed < result.Stderr.Totals.Observed then refused "output.regression"
                        else
                            let next = { result with Stdout = { (copyCapture value.Stdout) with Reader = result.Stdout.Reader }; Stderr = { (copyCapture value.Stderr) with Reader = result.Stderr.Reader }
                                                     ChargedTotalBytes = value.ChargedTotal; EvidenceBytes = value.EvidenceBytes }
                            match value.FirstFailure with
                            | None -> update next
                            | Some d ->
                                let failed = addFailure { Phase = result.Phase; Diagnostic = Validation.sanitizeDiagnostic state.Command d; At = Some now } next
                                update { failed with Phase = Retiring; Stop = match failed.Stop with NotRequested -> Requested(OutputLimit,now) | old -> old })
                | RetirementActionRequested diagnostic ->
                    validated (Validation.diagnostic diagnostic) (fun () ->
                        if result.Cleanup.RequestedActions.Length >= Validation.limits.CollectionEntries then refused "cleanup.actions-limit"
                        else update { result with Phase = Retiring; Cleanup = { result.Cleanup with RequestedActions = result.Cleanup.RequestedActions @ [ Validation.sanitizeDiagnostic state.Command diagnostic ] } })
                | CleanupObserved value ->
                    let value = { value with LeaderTermination = retainKnown result.Cleanup.LeaderTermination value.LeaderTermination
                                             WorkloadEmpty = retainKnown result.Cleanup.WorkloadEmpty value.WorkloadEmpty
                                             OwnedChildrenReaped = retainKnown result.Cleanup.OwnedChildrenReaped value.OwnedChildrenReaped }
                    validated (Validation.cleanup value @ clockErrors now value.LeaderTermination @ clockErrors now value.WorkloadEmpty @ clockErrors now value.OwnedChildrenReaped @ clockErrors now value.LocalTasks) (fun () ->
                        let noEffect = (not state.TargetReleased && match result.Launch with NotStarted | Refused _ -> true | _ -> false)
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
                        if not custodyAllowed || (value.Disposition = Complete && not complete) || (result.Cleanup.Disposition = Complete && value.Disposition <> Complete) then refused "cleanup.proof"
                        else
                            let phase = if value.Disposition = Complete then Terminal elif result.Phase = Prepared || result.Phase = Starting then result.Phase else Retiring
                            update { result with Cleanup = value; Phase = phase })
                | FailureObserved failure ->
                    let clockError = match failure.At with Some at when at.Domain <> now.Domain || at.Ticks > now.Ticks || at.Ticks < (Budget.describe result.OriginalBudget).StartedAt -> [ { Code = "failure-clock-refused"; Field = "failure.at" } ] | _ -> []
                    validated (Validation.diagnostic failure.Diagnostic @ clockError) (fun () ->
                        let failure = { failure with Diagnostic = Validation.sanitizeDiagnostic state.Command failure.Diagnostic }
                        let next = { (addFailure failure result) with Phase = if result.Phase = Terminal then Terminal else Retiring }
                        if metadataBytes next <= int64 Validation.limits.ResultMetadataBytes then update next
                        else
                            match result.FirstFailure with
                            | Some _ ->
                                // Secondary reporting/cleanup cannot displace the first cause.
                                let omitted = if result.OmittedFailures = Int64.MaxValue then Int64.MaxValue else result.OmittedFailures + 1L
                                update { result with OmittedFailures = omitted; Phase = if result.Phase = Terminal then Terminal else Retiring }
                            | None ->
                                // Keep original cause code/phase/time; explicitly mark omitted message/evidence.
                                let compact = { failure with Diagnostic = { failure.Diagnostic with Message = "Diagnostic detail omitted by result metadata bound."; Evidence = None } }
                                update { result with FirstFailure = Some compact; OmittedFailures = 1L; Phase = Retiring })
                | ReturnRequested ->
                    finish { state with Returned = true; Result = { result with Phase = if result.Cleanup.Disposition = Complete then Terminal else Retiring } }

    let apply now event state =
        try applyCore now event state
        with
        | :? ArgumentException | :? NullReferenceException | :? OverflowException ->
            Error [ { Code = "invalid-event"; Field = "event.structure" } ]
