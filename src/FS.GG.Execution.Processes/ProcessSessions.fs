namespace FS.GG.Execution.Processes

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open FS.GG.Execution.Contracts

[<RequireQualifiedAccess>]
type RetirementPolicy =
    | DirectChild
    | BestEffortProcessTree

[<RequireQualifiedAccess>]
module ProcessSessions =
    let private note code = { Code = code; Message = "BCL session observation."; Evidence = None }
    let private error code field = { Code = code; Field = field }

    // Public factories expose no callbacks, registry or detached reaper.
    // Friend tests can replace only this retained instance's native retirement leaf.
    // Every asynchronous BCL operation and its Process remain fields of that object.
    type private Session(command: CommandSpec, identity: OperationIdentity, budget: ExecutionBudget,
                         clock: IMonotonicClock, owner: OwnerReference, preparedAt: ClockStamp, initial: LifecycleState, sink: IBoundedByteSink, retirement: RetirementPolicy, retire: Process -> bool -> unit) =
        let gate = obj()
        let mutable state = initial
        let mutable runConsumed = false
        let mutable released = false
        let mutable resourcesDisposed = false
        let mutable child: Process option = None
        let mutable generation: ProcessGeneration option = None
        let mutable launchAttempted = false
        let mutable ambiguous = false
        let mutable leaderExited = false
        let mutable launchDone = true
        let mutable stdoutDone = true
        let mutable stderrDone = true
        let mutable inputDone = true
        let mutable exitDone = true
        let mutable stopDone = true
        let mutable stopConsumed = false
        let mutable launchTask: Task option = None
        let mutable stdoutTask: Task option = None
        let mutable stderrTask: Task option = None
        let mutable inputTask: Task option = None
        let mutable exitTask: Task option = None
        let mutable stopTask: Task option = None
        let mutable privateFailure: Failure option = None
        let mutable omittedPrivateFailures = 0L
        let mutable lastAcceptedClockTick = preparedAt.Ticks

        let increment value = if value = Int64.MaxValue then value else value + 1L
        let remember code =
            let failure = { Phase = (Lifecycle.snapshot state).Phase; Diagnostic = note code; At = None }
            if privateFailure.IsNone then privateFailure <- Some failure
            else omittedPrivateFailures <- increment omittedPrivateFailures
        // Every caller holds gate. Passive observations and every subsequent effect
        // share one accepted clock watermark; discarding a snapshot transition cannot
        // discard the monotonic history that constrains a later launch or stop.
        let now () =
            try
                let at = clock.Now()
                match Budget.remaining at budget with
                | Ok _ when at.Ticks >= lastAcceptedClockTick ->
                    lastAcceptedClockTick <- at.Ticks
                    Ok at
                | _ -> remember "clock-observation-refused"; Error()
            with _ -> remember "clock-observation-failed"; Error()
        let applyAt at event =
            match Lifecycle.apply at event state with
            | Ok next -> state <- next; true
            | Error _ ->
                match event with
                | FailureObserved failure -> remember failure.Diagnostic.Code
                | LaunchObserved(Refused diagnostic) | LaunchObserved(Ambiguous diagnostic) -> remember diagnostic.Code
                | ReaderObserved(_,Known(ReadFailed diagnostic,_)) -> remember diagnostic.Code
                | OutputObserved output -> output.FirstFailure |> Option.iter (fun diagnostic -> remember diagnostic.Code)
                | _ -> ()
                remember "contract-observation-refused"
                false
        let apply event =
            match now() with Ok at -> applyAt at event | Error _ -> false
        let snapshot () =
            let result = Lifecycle.snapshot state
            match privateFailure with
            | None -> result
            | Some failure ->
                // Contracts reserve first-cause space. Later private failures are counted,
                // not appended as unbounded exception text or fabricated positive proof.
                let count = if result.FirstFailure.IsSome then increment omittedPrivateFailures else omittedPrivateFailures
                { result with FirstFailure = result.FirstFailure |> Option.orElse (Some failure)
                              OmittedFailures = if count > Int64.MaxValue - result.OmittedFailures then Int64.MaxValue else result.OmittedFailures + count }
        let stamp at =
            { At = Some at; Scope = identity.InvocationId.ToString("D"); Generation = generation; Provenance = owner.Evidence }
        let allSettled () =
            launchDone && stdoutDone && stderrDone && inputDone && exitDone && stopDone
            && ([launchTask;stdoutTask;stderrTask;inputTask;exitTask;stopTask] |> List.choose id |> List.forall _.IsCompleted)
        let budgetAt at = Budget.remaining at budget
        let cleanupAt at custody =
            let previous = (Lifecycle.snapshot state).Cleanup
            let proof = stamp at
            let noEffect = not launchAttempted && not ambiguous
            { previous with
                Disposition = if noEffect && allSettled() then Complete else Incomplete
                LeaderTermination = if leaderExited then Known(true,proof) else Unknown(note "leader-termination-unproved")
                WorkloadEmpty = Unknown(note "workload-empty-unsupported")
                OwnedChildrenReaped = Unknown(note "owned-child-reaping-unsupported")
                LocalTasks = Known((if allSettled() then Settled else Outstanding),proof)
                Unresolved = if leaderExited then [] else generation |> Option.toList
                OmittedUnresolved = 0L
                Custody = custody }
        let refreshCleanup () =
            match now() with
            | Ok at ->
                match budgetAt at with
                | Ok view when not view.TotalExpired && not released ->
                    let custody = if not launchAttempted && not ambiguous && launchDone then NoEffect else RetainedByOwner owner
                    applyAt at (CleanupObserved(cleanupAt at custody)) |> ignore
                | _ -> () // Late actual settlement remains private; no new cleanup authority.
            | Error _ -> ()
        let recordFailure code =
            match now() with
            | Ok at -> applyAt at (FailureObserved { Phase = (Lifecycle.snapshot state).Phase; Diagnostic = note code; At = Some at }) |> ignore
            | Error _ -> remember code
        let runTask (work: unit -> Task) = Task.Run(Func<Task>(work))

        let stopWorker () : Task =
            task {
                try
                    try
                        let target =
                            lock gate (fun () ->
                                match now(),child with
                                | Ok at,Some targetProcess ->
                                    match budgetAt at with
                                    | Ok view when not view.TotalExpired && not leaderExited && not ambiguous ->
                                        let code = if retirement = RetirementPolicy.DirectChild then "bcl-leader-stop-requested" else "bcl-tree-policy-requested"
                                        if applyAt at (RetirementActionRequested(note code)) then Some targetProcess else None
                                    | _ -> None
                                | _ -> None)
                        match target with
                        | Some targetProcess ->
                            // The retained Process is never reopened from a numeric PID.
                            // Tree traversal is synchronous/noncancellable and remains this exact
                            // retained StopTask. Return/exit never establish whole-tree completion.
                            match retirement with
                            | RetirementPolicy.DirectChild ->
                                if not targetProcess.HasExited then retire targetProcess false
                            | RetirementPolicy.BestEffortProcessTree ->
                                if targetProcess.HasExited then
                                    lock gate (fun () -> apply (RetirementActionRequested(note "bcl-tree-leader-exited-unavailable")) |> ignore)
                                else
                                    let committed =
                                        lock gate (fun () ->
                                            match now() with
                                            | Ok at ->
                                                match budgetAt at with
                                                | Ok view when not view.TotalExpired ->
                                                    applyAt at (RetirementActionRequested(note "bcl-tree-stop-committed"))
                                                | _ -> false
                                            | Error _ -> false)
                                    if committed then retire targetProcess true
                        | None -> ()
                    with _ -> lock gate (fun () -> recordFailure (if retirement = RetirementPolicy.DirectChild then "bcl-leader-stop-failed" else "bcl-tree-stop-failed"))
                finally
                    lock gate (fun () -> stopDone <- true; refreshCleanup())
            } :> Task
        let ensureStop () =
            let requested = match (Lifecycle.snapshot state).Stop with Requested _ -> true | _ -> false
            if requested && not stopConsumed && launchDone && launchAttempted && not ambiguous && leaderExited
               && retirement = RetirementPolicy.BestEffortProcessTree then
                match now() with
                | Ok at ->
                    match budgetAt at with
                    | Ok view when not view.TotalExpired ->
                        stopConsumed <- true
                        applyAt at (RetirementActionRequested(note "bcl-tree-leader-exited-unavailable")) |> ignore
                    | _ -> ()
                | Error _ -> ()
            if requested && not stopConsumed && launchDone && launchAttempted && not ambiguous && not leaderExited then
                match now() with
                | Ok at ->
                    match budgetAt at with
                    | Ok view when not view.TotalExpired ->
                        stopConsumed <- true
                        stopDone <- false
                        stopTask <- Some(runTask stopWorker)
                    | _ -> ()
                | Error _ -> ()
        let stop reason =
            match now() with
            | Ok at ->
                match budgetAt at with
                | Ok view when not view.TotalExpired ->
                    if applyAt at (StopRequested reason) then ensureStop()
                | _ -> ()
            | Error _ -> ()

        let pump channel (stream: Stream) : Task =
            task {
                try
                    try
                        let buffer = Array.zeroCreate<byte> command.Capture.ReadChunkBytes
                        let mutable reading = true
                        while reading do
                            let! count = stream.ReadAsync(buffer.AsMemory()).AsTask()
                            lock gate (fun () ->
                                if count = 0 then
                                    reading <- false
                                    match now() with
                                    | Ok at -> applyAt at (ReaderObserved(channel,Known(Eof,stamp at))) |> ignore
                                    | Error _ -> ()
                                else
                                    // Shared charge is committed before either channel retains bytes.
                                    let admission = sink.Append(channel,ReadOnlyMemory<byte>(buffer,0,count))
                                    apply (OutputObserved(sink.Snapshot())) |> ignore
                                    match admission with RetireRequired _ -> stop OutputLimit | Accepted -> ())
                    with _ ->
                        lock gate (fun () ->
                            match now() with
                            | Ok at -> applyAt at (ReaderObserved(channel,Known(ReadFailed(note "bcl-reader-failed"),stamp at))) |> ignore
                            | Error _ -> ()
                            stop SinkFailure)
                finally
                    lock gate (fun () ->
                        if channel = Stdout then stdoutDone <- true else stderrDone <- true
                        refreshCleanup())
            } :> Task
        let writeInput (stream: Stream) : Task =
            task {
                try
                    try
                        match command.Input with
                        | Closed -> ()
                        | Bytes bytes ->
                            let mayWrite =
                                lock gate (fun () ->
                                    match now() with
                                    | Ok at ->
                                        match budgetAt at with
                                        | Ok view when not view.WorkExpired && (Lifecycle.snapshot state).Stop = NotRequested -> true
                                        | Ok _ -> stop WorkDeadline; false
                                        | Error _ -> remember "stdin-clock-refused"; false
                                    | Error _ -> false)
                            if mayWrite then do! stream.WriteAsync(bytes).AsTask()
                    with _ -> lock gate (fun () -> recordFailure "bcl-stdin-write-failed"; stop SinkFailure)
                finally
                    try stream.Close()
                    with _ -> lock gate (fun () -> recordFailure "bcl-stdin-close-failed")
                    lock gate (fun () -> inputDone <- true; refreshCleanup())
            } :> Task
        let waitExit (targetProcess: Process) : Task =
            task {
                try
                    try
                        do! targetProcess.WaitForExitAsync(CancellationToken.None)
                        let exitCode = targetProcess.ExitCode
                        lock gate (fun () ->
                            leaderExited <- true
                            match now() with
                            | Ok at -> applyAt at (ExitObserved(Known(Exited exitCode,stamp at))) |> ignore
                            | Error _ -> ())
                    with _ -> lock gate (fun () -> recordFailure "bcl-exit-observation-failed")
                finally
                    lock gate (fun () -> exitDone <- true; refreshCleanup())
            } :> Task
        let startInfo () =
            let info = ProcessStartInfo(command.Executable)
            info.UseShellExecute <- false
            info.RedirectStandardInput <- true
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.WorkingDirectory <- command.WorkingDirectory
            for argument in command.Arguments do info.ArgumentList.Add argument
            match command.Environment with
            | Inherit overrides ->
                for KeyValue(name,value) in overrides do
                    match value with Some text -> info.Environment[name] <- text | None -> info.Environment.Remove name |> ignore
            | Allowlist(names,overrides) ->
                let inherited = names |> List.choose (fun name -> match Environment.GetEnvironmentVariable name with null -> None | value -> Some(name,value))
                info.Environment.Clear()
                for name,value in inherited do info.Environment[name] <- value
                for KeyValue(name,value) in overrides do
                    match value with Some text -> info.Environment[name] <- text | None -> info.Environment.Remove name |> ignore
            | Replace values ->
                info.Environment.Clear()
                for KeyValue(name,value) in values do info.Environment[name] <- value
            info

        let launchWorker (cancellation: CancellationToken) () : Task =
            task {
                let mutable startConfirmed = false
                try
                    try
                        let targetProcess = new Process(StartInfo = startInfo())
                        let permitted =
                            lock gate (fun () ->
                                child <- Some targetProcess
                                match now() with
                                | Ok at ->
                                    match budgetAt at with
                                    | Ok view when not view.WorkExpired && not cancellation.IsCancellationRequested && (Lifecycle.snapshot state).Stop = NotRequested ->
                                        let retained = cleanupAt at (RetainedByOwner owner)
                                        let capability = { Capability = BoundedReturnWithRetainedCustody; Obtained = Known(owner.Evidence,stamp at) }
                                        let capabilities = capability :: (command.Capabilities |> List.choose (fun requirement ->
                                            if requirement.Capability = BoundedReturnWithRetainedCustody then None
                                            else Some { Capability = requirement.Capability; Obtained = Unavailable(note "unsupported-capability") }))
                                        if applyAt at (CleanupObserved retained) && applyAt at (CapabilitiesObtained capabilities) && applyAt at TargetReleaseRequested then
                                            launchAttempted <- true
                                            true
                                        else false
                                    | Ok _ -> stop (if cancellation.IsCancellationRequested then CallerCancellation else WorkDeadline); false
                                    | Error _ -> remember "launch-clock-refused"; false
                                | Error _ -> false)
                        if permitted then
                            // Native Start runs outside the observation lock. A concurrent stop
                            // after commitment retains this in-flight attempt, then retires it;
                            // it is never mislabeled NoEffect or retried as another launch.
                            if targetProcess.Start() then
                                startConfirmed <- true
                                lock gate (fun () ->
                                    let actual = { Platform = "bcl-linux"; ScopeId = identity.InvocationId.ToString("D"); Pid = targetProcess.Id; BirthToken = "retained-process:" + Guid.NewGuid().ToString("N") }
                                    generation <- Some actual
                                    apply (LaunchObserved(Started actual)) |> ignore
                                    stdoutDone <- false; stderrDone <- false; inputDone <- false; exitDone <- false
                                    stdoutTask <- Some(runTask (fun () -> pump Stdout targetProcess.StandardOutput.BaseStream))
                                    stderrTask <- Some(runTask (fun () -> pump Stderr targetProcess.StandardError.BaseStream))
                                    inputTask <- Some(runTask (fun () -> writeInput targetProcess.StandardInput.BaseStream))
                                    exitTask <- Some(runTask (fun () -> waitExit targetProcess)))
                            else
                                lock gate (fun () ->
                                    ambiguous <- true
                                    apply (LaunchObserved(Ambiguous(note "bcl-launch-unconfirmed"))) |> ignore
                                    recordFailure "bcl-launch-unconfirmed")
                    with
                    | :? Win32Exception as error when error.NativeErrorCode = 2 && not startConfirmed ->
                        lock gate (fun () ->
                            launchAttempted <- false
                            apply (LaunchObserved(Refused(note "missing-executable"))) |> ignore
                            recordFailure "missing-executable")
                    | _ ->
                        lock gate (fun () ->
                            ambiguous <- launchAttempted
                            if launchAttempted then apply (LaunchObserved(Ambiguous(note "bcl-launch-ambiguous"))) |> ignore
                            recordFailure "bcl-launch-failed")
                finally
                    lock gate (fun () -> launchDone <- true; ensureStop(); refreshCleanup())
            } :> Task

        let boundedObserve useCleanup (cancellation: CancellationToken) : Task<ExecutionResult> =
            task {
                let mutable observing = true
                while observing do
                    let delay =
                        lock gate (fun () ->
                            if cancellation.IsCancellationRequested && not released then stop CallerCancellation
                            match now() with
                            | Ok at ->
                                match budgetAt at with
                                | Ok view ->
                                    if view.WorkExpired && not (allSettled()) && (Lifecycle.snapshot state).Stop = NotRequested then stop WorkDeadline
                                    ensureStop()
                                    refreshCleanup()
                                    let stopping = (Lifecycle.snapshot state).Stop <> NotRequested
                                    if released || allSettled() || view.TotalExpired || (not useCleanup && (view.WorkExpired || stopping)) || privateFailure.IsSome then None
                                    else
                                        Budget.clip at (if useCleanup && stopping then Cleanup else Work) (TimeSpan.FromMilliseconds 5.) budget |> Result.toOption
                                | Error _ -> remember "observation-clock-refused"; None
                            | Error _ -> None)
                    match delay with
                    | Some duration when duration > TimeSpan.Zero -> do! Task.Delay duration
                    | _ -> observing <- false
                return lock gate (fun () ->
                    refreshCleanup()
                    apply ReturnRequested |> ignore
                    snapshot())
            }

        let observeNow (cancellation: CancellationToken) : Task<ExecutionResult> =
            let observed =
                lock gate (fun () ->
                    if cancellation.IsCancellationRequested && not released then stop CallerCancellation
                    match now() with
                    | Ok at ->
                        // Validate against the reducer's accepted clock history without committing
                        // ReturnRequested: an observation must not close a prepared/in-flight launch.
                        match Lifecycle.apply at ReturnRequested state, budgetAt at with
                        | Ok _, Ok view ->
                            if view.WorkExpired && not (allSettled()) && (Lifecycle.snapshot state).Stop = NotRequested then
                                stop WorkDeadline
                            ensureStop()
                            // Prepared NoEffect is not terminal cleanup. Refresh only once Run or
                            // an explicit stop has consumed the launch/retirement decision.
                            if runConsumed || (Lifecycle.snapshot state).Stop <> NotRequested then
                                refreshCleanup()
                        | _ -> remember "observation-clock-refused"
                    | Error _ -> ()
                    snapshot())
            Task.FromResult observed

        interface IProcessSession with
            member _.Identity = identity
            member _.Owner = owner
            member _.RunAsync cancellation =
                lock gate (fun () ->
                    if not runConsumed && not released then
                        runConsumed <- true
                        if cancellation.IsCancellationRequested then stop CallerCancellation
                        match now() with
                        | Ok at ->
                            match budgetAt at with
                            | Ok view when not view.WorkExpired && (Lifecycle.snapshot state).Stop = NotRequested ->
                                if applyAt at BeginLaunch then
                                    launchDone <- false
                                    launchTask <- Some(runTask (launchWorker cancellation))
                            | Ok _ -> if (Lifecycle.snapshot state).Stop = NotRequested then stop WorkDeadline
                            | Error _ -> remember "launch-clock-refused"
                        | Error _ -> ())
                boundedObserve false cancellation
            member _.ObserveAsync cancellation = observeNow cancellation
            member _.RequestStopAsync(reason,cancellation) =
                let observed =
                    lock gate (fun () ->
                        match now() with
                        | Ok at ->
                            match budgetAt at with
                            | Ok view when not view.TotalExpired && not released ->
                                stop (if cancellation.IsCancellationRequested then CallerCancellation else reason)
                                (snapshot()).Stop
                            | _ -> StopRefused(note "original-time-expired")
                        | Error _ -> StopRefused(note "clock-observation-failed"))
                Task.FromResult observed
            member _.TryRelease() =
                lock gate (fun () ->
                    if released then ResourcesReleased
                    else
                        match now() with
                        | Ok at ->
                            match budgetAt at with
                            | Ok view when not view.TotalExpired ->
                                let retainedTasks = [launchTask;stdoutTask;stderrTask;inputTask;exitTask;stopTask] |> List.choose id
                                let tasksComplete = retainedTasks |> List.forall _.IsCompleted
                                if not (allSettled()) || not tasksComplete || ambiguous || (launchAttempted && not leaderExited) then
                                    ReleaseRefused(note "local-obligations-pending")
                                elif privateFailure.IsSome then ReleaseRefused(note "local-observation-refused")
                                else
                                    let proof = stamp at
                                    let custody = if not launchAttempted then NoEffect else Released proof
                                    // Validate the prospective proof before any release effect.
                                    match Lifecycle.apply at (CleanupObserved(cleanupAt at custody)) state with
                                    | Error _ -> remember "local-proof-refused"; ReleaseRefused(note "local-proof-refused")
                                    | Ok _ ->
                                        try
                                            if not resourcesDisposed then
                                                child |> Option.iter _.Dispose()
                                                resourcesDisposed <- true
                                            // Stamp actual disposal afterward. A clock/error/expiry here
                                            // refuses proof; it never backdates release or grants time.
                                            match now() with
                                            | Ok after ->
                                                match budgetAt after with
                                                | Ok remaining when not remaining.TotalExpired ->
                                                    let actual = stamp after
                                                    let actualCustody = if not launchAttempted then NoEffect else Released actual
                                                    if applyAt after (CleanupObserved(cleanupAt after actualCustody)) then
                                                        released <- true
                                                        ResourcesReleased
                                                    else ReleaseRefused(note "local-proof-refused")
                                                | _ -> ReleaseRefused(note "original-time-expired")
                                            | Error _ -> ReleaseRefused(note "clock-observation-failed")
                                        with _ -> remember "bcl-resource-release-failed"; ReleaseRefused(note "bcl-resource-release-failed")
                            | _ -> ReleaseRefused(note "original-time-expired")
                        | Error _ -> ReleaseRefused(note "clock-observation-failed"))

    let private createFactoryWithAction retirement (retire: Process -> bool -> unit) =
        { new IProcessSessionFactory with
            member _.Prepare(command,identity,budget,clock,owner) =
                try
                    let policyErrors =
                        [ if obj.ReferenceEquals(retirement,null) then error "invalid-retirement-policy" "retirement"
                          if obj.ReferenceEquals(retire,null) then error "invalid-retirement-action" "retirement" ]
                    let requestErrors = policyErrors @ Validation.command command @ Validation.identity identity
                    if not requestErrors.IsEmpty then Error requestErrors
                    else
                        let unsupported =
                            command.Capabilities |> List.choose (fun requirement ->
                                if requirement.Required && requirement.Capability <> BoundedReturnWithRetainedCustody then Some(error "unsupported-capability" "capabilities") else None)
                        let resources = command.Resources
                        let resourceErrors =
                            [ if resources.MemoryBytes.IsSome then error "unsupported-resource" "resources.memory"
                              if resources.CpuQuota.IsSome then error "unsupported-resource" "resources.cpuQuota"
                              if resources.CpuAffinity.IsSome then error "unsupported-resource" "resources.cpuAffinity"
                              if resources.MaxKernelTasks.IsSome then error "unsupported-resource" "resources.kernelTasks"
                              if resources.MaxOwnedProcesses.IsSome then error "unsupported-resource" "resources.ownedProcesses"
                              if resources.MaxClrProcesses.IsSome then error "unsupported-resource" "resources.clrProcesses" ]
                        let ownerErrors =
                            Validation.cleanup
                                { Disposition = Incomplete; RequestedActions = []; LeaderTermination = Unknown(note "unstarted")
                                  WorkloadEmpty = Unknown(note "unsupported"); OwnedChildrenReaped = Unknown(note "unsupported")
                                  LocalTasks = Unknown(note "unstarted"); Unresolved = []; OmittedUnresolved = 0L; Custody = RetainedByOwner owner }
                        let preparedAt,clockErrors =
                            try
                                let at = clock.Now()
                                match Budget.remaining at budget with
                                | Ok view when not view.WorkExpired -> Some at,[]
                                | _ -> None,[error "invalid-live-budget" "budget"]
                            with _ -> None,[error "invalid-live-clock" "clock"]
                        let errors = ownerErrors @ unsupported @ resourceErrors @ clockErrors
                                     @ (if OperatingSystem.IsLinux() then [] else [error "unsupported-platform" "platform"])
                        if not errors.IsEmpty then Error errors
                        else
                            // Copy caller-owned input before retaining it; all other request collections
                            // are immutable validated F# values, and capture allocates only after refusal.
                            let copied = { command with Input = match command.Input with Closed -> Closed | Bytes bytes -> Bytes(ReadOnlyMemory<byte>(bytes.ToArray())) }
                            match Lifecycle.prepare copied identity budget,BoundedOutput.create copied.Capture,preparedAt with
                            | Ok initial,Ok sink,Some at -> Ok(Session(copied,identity,budget,clock,owner,at,initial,sink,retirement,retire) :> IProcessSession)
                            | Error errors,_,_ | _,Error errors,_ -> Error errors
                            | _,_,None -> Error [error "invalid-live-clock" "clock"]
                with
                | :? ArgumentException | :? NullReferenceException | :? OverflowException -> Error [error "invalid-request" "request.structure"] }

    let private bclRetire (target: Process) tree =
        if tree then target.Kill(true) else target.Kill()

    let internal createFactoryForTesting retirement retire = createFactoryWithAction retirement retire

    let createFactoryWithRetirement retirement = createFactoryWithAction retirement bclRetire

    let createFactory () = createFactoryWithRetirement RetirementPolicy.DirectChild
