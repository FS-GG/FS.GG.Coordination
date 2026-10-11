namespace FS.GG.Execution.Contracts.Conformance

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open FS.GG.Execution.Contracts

module Support =
    let diagnostic (code: string) : Diagnostic = { Code = code; Message = "Finite fixture observation."; Evidence = None }
    let exceptionDiagnostic (code: string) (error: exn) : Diagnostic =
        let message = error.GetType().Name + ": " + error.Message
        { Code = code; Message = (if message.Length > 900 then message.Substring(0,900) + " [detail omitted]" else message); Evidence = None }
    let require (condition: bool) (code: string) = if not condition then invalidOp code
    let workReleaseDecision (view: Result<BudgetView,BudgetFailure>) (cancelled: bool) : Result<unit,string> =
        if cancelled then Error "caller-cancelled"
        else
            match view with
            | Ok value when not value.WorkExpired && not value.TotalExpired && value.WorkRemaining > TimeSpan.Zero -> Ok ()
            | Ok _ -> Error "work-deadline"
            | Error _ -> Error "startup-budget-refused"
    let currentCleanup (prior: CleanupResult) (stamp: ObservationStamp) (leader: bool option) (local: TaskState) (custody: CustodyState) : CleanupResult =
        { prior with Disposition = Incomplete; Custody = custody
                     LeaderTermination = match leader with Some value -> Known(value,stamp) | None -> Unknown(diagnostic "leader-not-observed")
                     LocalTasks = Known(local,stamp)
                     WorkloadEmpty = Unknown(diagnostic "whole-workload-not-established")
                     OwnedChildrenReaped = Unknown(diagnostic "descendant-reaping-not-established") }
    let ok (value: Result<'T,'E>) : 'T = match value with Ok result -> result | Error _ -> invalidOp "contract-refused"
    let birth (pid: int) =
        let raw = File.ReadAllText($"/proc/{pid}/stat")
        let fields = raw.Substring(raw.LastIndexOf(')') + 2).Split(' ',StringSplitOptions.RemoveEmptyEntries)
        require (fields.Length > 19) "birth-shape"
        fields[19]
    let readerLabel (value: Observation<ReaderState>) =
        match value with
        | Known(Eof,_) -> "eof"
        | Known(ReadFailed _,_) -> "failed"
        | Known(StoppedWithoutEof _,_) -> "stopped"
        | Known(Reading,_) -> "reading"
        | _ -> "unknown"
    let exitLabel (value: Observation<ProcessExit>) =
        match value with Known(Exited code,_) -> $"exited:{code}" | Known(Signaled signal,_) -> $"signaled:{signal}" | _ -> "unknown"

// Test-only retained lease. No native kill, registry, background reaper or IDisposable path.
// The independently admitted outer owner remains responsible for its demonstrated native scope.
type FixtureSession(command: CommandSpec, identity: OperationIdentity, owner: OwnerReference, failedSink: bool) =
    let domain: ClockDomain = { Id = Guid.NewGuid(); Frequency = Stopwatch.Frequency }
    let clock = { new IMonotonicClock with member _.Now() = { Domain = domain; Ticks = Stopwatch.GetTimestamp() } }
    let budget = Budget.create (clock.Now()) (TimeSpan.FromSeconds 3.0) (TimeSpan.FromSeconds 2.5) |> Support.ok
    let gate = obj()
    let mutable state = Lifecycle.prepare command identity budget |> Support.ok
    let sink = BoundedOutput.create command.Capture |> Support.ok
    let childProcess = new Process()
    let mutable attempted = false
    let mutable started = false
    let mutable ambiguous = false
    let mutable released = false
    let mutable generation: ProcessGeneration option = None
    let mutable helperStartGateTick = -1L
    let mutable helperStartedTick = -1L
    let mutable targetReleaseGateTick = -1L
    let mutable targetReleasedTick = -1L
    let mutable targetWasUncancelled = false
    let mutable initialReturnTick = -1L
    let mutable initialReturnPending = false
    let mutable stdoutTask: Task = Task.CompletedTask
    let mutable stderrTask: Task = Task.CompletedTask
    let mutable exitTask: Task = Task.CompletedTask
    let observerErrors = ResizeArray<string>()
    let addObserverError (code: string) = lock gate (fun () -> if observerErrors.Count < 16 then observerErrors.Add code)
    let applyAt (at: ClockStamp) (event: LifecycleEvent) =
        lock gate (fun () ->
            match Lifecycle.apply at event state with
            | Ok next -> state <- next; true
            | Error _ ->
                if observerErrors.Count < 16 then observerErrors.Add "contract-observation-refused"
                false)
    let apply (event: LifecycleEvent) = lock gate (fun () -> applyAt (clock.Now()) event)
    let snapshot () = lock gate (fun () -> Lifecycle.snapshot state)
    let stampAt (at: ClockStamp) : ObservationStamp =
        { At = Some at; Scope = identity.InvocationId.ToString("D"); Generation = generation; Provenance = owner.Evidence }
    let known (value: 'T) : Observation<'T> = Known(value,stampAt(clock.Now()))
    let recordFailureAt (at: ClockStamp) (diagnostic: Diagnostic) =
        let current = Lifecycle.snapshot state
        applyAt at (FailureObserved { Phase = current.Phase; Diagnostic = diagnostic; At = Some at }) |> ignore
    let recordFailure (diagnostic: Diagnostic) = lock gate (fun () -> recordFailureAt (clock.Now()) diagnostic)
    let firstFailure (code: string) = recordFailure (Support.diagnostic code)
    let localSettled () = released || (started && stdoutTask.IsCompleted && stderrTask.IsCompleted && exitTask.IsCompleted)
    let updateCleanup () =
        lock gate (fun () ->
            if started && not released && localSettled() && childProcess.HasExited then
                let at = clock.Now()
                let prior = (Lifecycle.snapshot state).Cleanup
                let fresh = Support.currentCleanup prior (stampAt at) (Some true) Settled prior.Custody
                applyAt at (CleanupObserved fresh) |> ignore)
    let releaseProbe () : ReleaseResult * ClockStamp * bool =
        lock gate (fun () ->
            let pending = started && not (localSettled())
            let leaderTerminated = started && not released && not ambiguous && not pending && childProcess.HasExited
            let at = clock.Now()
            let decision =
                if released then ResourcesReleased
                elif ambiguous || pending then ReleaseRefused(Support.diagnostic "original-local-obligations-pending")
                elif started then
                    // Reobserve the whole mutable bundle at this decision tick; no old
                    // positive cleanup evidence is borrowed for a fresh release event.
                    if leaderTerminated then
                        let stamp = stampAt at
                        let prior = (Lifecycle.snapshot state).Cleanup
                        let fresh = Support.currentCleanup prior stamp (Some true) Settled (Released stamp)
                        if applyAt at (CleanupObserved fresh) then
                            childProcess.Dispose()
                            released <- true
                            ResourcesReleased
                        else ReleaseRefused(Support.diagnostic "original-cleanup-proof-refused")
                    else ReleaseRefused(Support.diagnostic "original-local-proof-missing")
                else
                    childProcess.Dispose()
                    released <- true
                    ResourcesReleased
            decision,at,pending)
    let allowEffectAt (at: ClockStamp) (cancellation: CancellationToken) =
        match Support.workReleaseDecision (Budget.remaining at budget) cancellation.IsCancellationRequested with
        | Ok () -> true
        | Error code ->
            recordFailureAt at (Support.diagnostic code)
            applyAt at (StopRequested(if code = "caller-cancelled" then CallerCancellation else WorkDeadline)) |> ignore
            false
    let pump (channel: OutputChannel) (stream: Stream) : Task =
        task {
            let buffer = Array.zeroCreate<byte> command.Capture.ReadChunkBytes
            let mutable reading = true
            let mutable injected = false
            try
                while reading do
                    let! count = stream.ReadAsync(buffer.AsMemory()).AsTask()
                    if count = 0 then
                        reading <- false
                        apply (ReaderObserved(channel,known Eof)) |> ignore
                    else
                        let bytes = ReadOnlyMemory<byte>(buffer,0,count)
                        lock gate (fun () ->
                            let admission = sink.Append(channel,bytes)
                            apply (OutputObserved(sink.Snapshot())) |> ignore
                            match admission with
                            | Accepted -> ()
                            | RetireRequired _ -> apply (StopRequested OutputLimit) |> ignore)
                        if failedSink && channel = Stdout && not injected then
                            injected <- true
                            // A real finite disposed-stream write fails outside the bounded core sink.
                            use closed = new MemoryStream()
                            closed.Dispose()
                            try
                                do! closed.WriteAsync(bytes).AsTask()
                                firstFailure "sink-injection-did-not-fail"
                            with :? ObjectDisposedException as error ->
                                recordFailure (Support.exceptionDiagnostic "sink-write-failed" error)
                                apply (StopRequested SinkFailure) |> ignore
            with error ->
                apply (ReaderObserved(channel,known(ReadFailed(Support.exceptionDiagnostic "fixture-reader-failed" error)))) |> ignore
        } :> Task
    let start (cancellation: CancellationToken) =
        Support.require (not attempted && not released) "start-already-consumed"
        attempted <- true
        let at = clock.Now()
        Support.require (applyAt at BeginLaunch) "begin-refused"
        let info = ProcessStartInfo(command.Executable)
        info.UseShellExecute <- false
        info.WorkingDirectory <- command.WorkingDirectory
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in command.Arguments do info.ArgumentList.Add argument
        match command.Environment with
        | Replace values ->
            info.Environment.Clear()
            for KeyValue(key,value) in values do info.Environment[key] <- value
        | _ -> invalidOp "fixture-environment-must-be-literal-replace"
        childProcess.StartInfo <- info
        try
            let mayStart =
                lock gate (fun () ->
                    let current = clock.Now()
                    if allowEffectAt current cancellation then
                        let stamp = stampAt current
                        let prior = (Lifecycle.snapshot state).Cleanup
                        let fresh = Support.currentCleanup prior stamp None Outstanding (RetainedByOwner owner)
                        Support.require (applyAt current (CleanupObserved fresh)) "retained-owner-refused"
                        Support.require (applyAt current (CapabilitiesObtained [ { Capability = BoundedReturnWithRetainedCustody; Obtained = Known(owner.Evidence,stamp) } ])) "capability-refused"
                        Support.require (applyAt current TargetReleaseRequested) "helper-release-refused"
                        helperStartGateTick <- current.Ticks
                        true
                    else
                        applyAt current (LaunchObserved(Refused(Support.diagnostic "helper-launch-not-released"))) |> ignore
                        let prior = (Lifecycle.snapshot state).Cleanup
                        applyAt current (CleanupObserved(Support.currentCleanup prior (stampAt current) None Settled NoEffect)) |> ignore
                        false)
            if mayStart then
                // Process.Start releases only the finite gated helper. G is a separate
                // actual target-output release and gets its own current budget check.
                let startResult =
                    lock gate (fun () ->
                        let current = clock.Now()
                        if allowEffectAt current cancellation then
                            helperStartGateTick <- current.Ticks
                            Some(childProcess.Start())
                        else
                            applyAt current (LaunchObserved(Refused(Support.diagnostic "helper-launch-not-released"))) |> ignore
                            let prior = (Lifecycle.snapshot state).Cleanup
                            applyAt current (CleanupObserved(Support.currentCleanup prior (stampAt current) None Settled NoEffect)) |> ignore
                            None)
                match startResult with
                | None -> ()
                | Some false ->
                    ambiguous <- true
                    apply (LaunchObserved(Ambiguous(Support.diagnostic "fixture-start-ambiguous"))) |> ignore
                | Some true ->
                    started <- true
                    helperStartedTick <- (clock.Now()).Ticks
                    let actual: ProcessGeneration = { Platform = "linux"; ScopeId = identity.InvocationId.ToString("D"); Pid = childProcess.Id; BirthToken = Support.birth childProcess.Id }
                    generation <- Some actual
                    Support.require (apply (LaunchObserved(Started actual))) "started-observation-refused"
                    apply (ReaderObserved(Stdout,known Reading)) |> ignore
                    apply (ReaderObserved(Stderr,known Reading)) |> ignore
                    stdoutTask <- pump Stdout childProcess.StandardOutput.BaseStream
                    stderrTask <- pump Stderr childProcess.StandardError.BaseStream
                    exitTask <- (task {
                        try
                            do! childProcess.WaitForExitAsync()
                            apply (ExitObserved(known(Exited childProcess.ExitCode))) |> ignore
                        with _ -> addObserverError "fixture-exit-observation-failed"
                    } :> Task)
                    lock gate (fun () ->
                        // The retained Process handle must still observe this original
                        // live leader, and the descriptive birth must still join it.
                        let live = not childProcess.HasExited && Support.birth childProcess.Id = actual.BirthToken
                        let current = clock.Now()
                        if not live then recordFailureAt current (Support.diagnostic "helper-not-live-at-target-release")
                        elif allowEffectAt current cancellation then
                            targetReleaseGateTick <- current.Ticks
                            targetWasUncancelled <- true
                            match command.Input with
                            | Bytes value ->
                                childProcess.StandardInput.BaseStream.Write(value.Span)
                                targetReleasedTick <- (clock.Now()).Ticks
                            | Closed -> ()
                        // Denied G closes the finite helper gate and keeps all already
                        // started tasks/handles in this lease for original cleanup.
                        childProcess.StandardInput.Close())
        with
        | :? Win32Exception as error when not started && error.NativeErrorCode = 2 ->
            lock gate (fun () ->
                let current = clock.Now()
                applyAt current (LaunchObserved(Refused(Support.diagnostic "missing-executable"))) |> ignore
                let prior = (Lifecycle.snapshot state).Cleanup
                applyAt current (CleanupObserved(Support.currentCleanup prior (stampAt current) None Settled NoEffect)) |> ignore)
        | error ->
            ambiguous <- true
            if not started then apply (LaunchObserved(Ambiguous(Support.exceptionDiagnostic "fixture-start-unclassified" error))) |> ignore
            recordFailure (Support.exceptionDiagnostic "fixture-launch-or-release-failed" error)
    let boundedObserve (cancellation: CancellationToken) : Task<ExecutionResult> =
        task {
            let mutable observing = started && not ambiguous && not released
            while observing do
                updateCleanup()
                let current = snapshot()
                let view = Budget.remaining (clock.Now()) budget |> Support.ok
                if localSettled() then observing <- false
                elif cancellation.IsCancellationRequested then
                    apply (StopRequested CallerCancellation) |> ignore
                    firstFailure "caller-cancelled"
                    observing <- false
                elif view.WorkExpired then
                    // Positive leader exit distinguishes the inherited-pipe case from child deadline.
                    match current.Exit with
                    | Known _ -> ()
                    | _ -> apply (StopRequested WorkDeadline) |> ignore; firstFailure "work-deadline"
                    observing <- false
                elif current.FirstFailure.IsSome then observing <- false
                else
                    match Budget.clip (clock.Now()) Work (TimeSpan.FromMilliseconds 5.0) budget with
                    | Ok delay -> do! Task.Delay(delay)
                    | Error _ -> observing <- false
            return lock gate (fun () ->
                let at = clock.Now()
                applyAt at ReturnRequested |> ignore
                if initialReturnTick < 0L then
                    initialReturnTick <- at.Ticks
                    initialReturnPending <- started && not (localSettled())
                Lifecycle.snapshot state)
        }
    member _.Now() = clock.Now()
    member _.InitialReturnTick = initialReturnTick
    member _.InitialReturnPending = initialReturnPending
    member _.Generation = generation
    member _.HelperStartGateTick = helperStartGateTick
    member _.HelperStartedTick = helperStartedTick
    member _.TargetReleaseGateTick = targetReleaseGateTick
    member _.TargetReleasedTick = targetReleasedTick
    member _.TargetWasUncancelled = targetWasUncancelled
    member _.ProbeRelease() = releaseProbe()
    member _.MetadataBytes = lock gate (fun () -> Lifecycle.metadataBytes state)
    member _.Budget = budget
    member _.ObserverErrors = lock gate (fun () -> observerErrors.ToArray())
    member _.Started = started
    member _.LocalSettled = localSettled()
    member _.RetireWithinOriginalBudgetAsync() : Task =
        task {
            if started then
                match Budget.clip (clock.Now()) Cleanup TimeSpan.MaxValue budget with
                | Ok remaining ->
                    try do! Task.WhenAll([|stdoutTask;stderrTask;exitTask|]).WaitAsync(remaining)
                    with :? TimeoutException -> addObserverError "original-cleanup-expired"
                    updateCleanup()
                | Error _ -> addObserverError "original-cleanup-expired"
        } :> Task
    interface IProcessSession with
        member _.Identity = identity
        member _.Owner = owner
        member _.RunAsync cancellation =
            if not attempted && not released then
                if cancellation.IsCancellationRequested then
                    attempted <- true
                    apply (StopRequested CallerCancellation) |> ignore
                else start cancellation
            boundedObserve cancellation
        member _.ObserveAsync cancellation = boundedObserve cancellation
        member _.RequestStopAsync(reason, _) =
            match Budget.clip (clock.Now()) Cleanup TimeSpan.MaxValue budget with
            | Ok _ -> apply (StopRequested reason) |> ignore; Task.FromResult((snapshot()).Stop)
            | Error _ -> Task.FromResult(StopRefused(Support.diagnostic "original-time-expired"))
        member _.TryRelease() =
            let decision,_,_ = releaseProbe()
            decision

// The production path supplies its own original caller budget/clock and retains
// the returned real session. It does not import the bridge's procfs/gated-start
// implementation, failed-sink injection or qualification claims.
module ProductionFixture =
    let prepare (command: CommandSpec) identity owner total reserve =
        let domain = { Id = Guid.NewGuid(); Frequency = Stopwatch.Frequency }
        let clock = { new IMonotonicClock with member _.Now() = { Domain = domain; Ticks = Stopwatch.GetTimestamp() } }
        let budget = Budget.create (clock.Now()) total reserve |> Support.ok
        let session = FS.GG.Execution.Processes.ProcessSessions.createFactory().Prepare(command,identity,budget,clock,owner) |> Support.ok
        session,clock,budget
