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
        { Code = code; Message = if message.Length > 900 then message.Substring(0,900) + " [detail omitted]" else message; Evidence = None }
    let require (condition: bool) (code: string) = if not condition then invalidOp code
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
    let mutable stdoutTask: Task = Task.CompletedTask
    let mutable stderrTask: Task = Task.CompletedTask
    let mutable exitTask: Task = Task.CompletedTask
    let observerErrors = ResizeArray<string>()
    let addObserverError (code: string) = lock gate (fun () -> if observerErrors.Count < 16 then observerErrors.Add code)
    let applyAt (at: ClockStamp) (event: LifecycleEvent) =
        lock gate (fun () ->
            match Lifecycle.apply at event state with
            | Ok next -> state <- next; true
            | Error _ -> if observerErrors.Count < 16 then observerErrors.Add "contract-observation-refused"; false)
    let apply (event: LifecycleEvent) = lock gate (fun () -> applyAt (clock.Now()) event)
    let snapshot () = lock gate (fun () -> Lifecycle.snapshot state)
    let stampAt (at: ClockStamp) : ObservationStamp =
        { At = Some at; Scope = identity.InvocationId.ToString("D"); Generation = generation; Provenance = owner.Evidence }
    let known (value: 'T) : Observation<'T> = Known(value,stampAt(clock.Now()))
    let recordFailure (diagnostic: Diagnostic) =
        lock gate (fun () ->
            let current = Lifecycle.snapshot state
            let at = clock.Now()
            applyAt at (FailureObserved { Phase = current.Phase; Diagnostic = diagnostic; At = Some at }) |> ignore)
    let firstFailure (code: string) = recordFailure (Support.diagnostic code)
    let localSettled () = released || (started && stdoutTask.IsCompleted && stderrTask.IsCompleted && exitTask.IsCompleted)
    let updateCleanup () =
        if started && not released && localSettled() && childProcess.HasExited then
            let prior = (snapshot()).Cleanup
            apply (CleanupObserved { prior with Disposition = Incomplete; LeaderTermination = known true; LocalTasks = known Settled
                                                WorkloadEmpty = Unknown(Support.diagnostic "whole-workload-not-established")
                                                OwnedChildrenReaped = Unknown(Support.diagnostic "descendant-reaping-not-established") }) |> ignore
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
    let start () =
        Support.require (not attempted && not released) "start-already-consumed"
        attempted <- true
        let at = clock.Now()
        Support.require (applyAt at BeginLaunch) "begin-refused"
        let prior = (snapshot()).Cleanup
        Support.require (applyAt at (CleanupObserved { prior with Custody = RetainedByOwner owner; LocalTasks = Known(Outstanding,stampAt at) })) "retained-owner-refused"
        Support.require (applyAt at (CapabilitiesObtained [ { Capability = BoundedReturnWithRetainedCustody; Obtained = Known(owner.Evidence,stampAt at) } ])) "capability-refused"
        Support.require (applyAt at TargetReleaseRequested) "target-release-refused"
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
            if not (childProcess.Start()) then
                ambiguous <- true
                apply (LaunchObserved(Ambiguous(Support.diagnostic "fixture-start-ambiguous"))) |> ignore
            else
                started <- true
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
                match command.Input with
                | Bytes value ->
                    childProcess.StandardInput.BaseStream.Write(value.Span)
                    childProcess.StandardInput.Close()
                | Closed -> childProcess.StandardInput.Close()
        with
        | :? Win32Exception as error when not started && error.NativeErrorCode = 2 ->
            apply (LaunchObserved(Refused(Support.diagnostic "missing-executable"))) |> ignore
            let prior = (snapshot()).Cleanup
            apply (CleanupObserved { prior with Custody = NoEffect; LocalTasks = known Settled }) |> ignore
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
            apply ReturnRequested |> ignore
            return snapshot()
        }
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
                else start()
            boundedObserve cancellation
        member _.ObserveAsync cancellation = boundedObserve cancellation
        member _.RequestStopAsync(reason, _) =
            match Budget.clip (clock.Now()) Cleanup TimeSpan.MaxValue budget with
            | Ok _ -> apply (StopRequested reason) |> ignore; Task.FromResult((snapshot()).Stop)
            | Error _ -> Task.FromResult(StopRefused(Support.diagnostic "original-time-expired"))
        member _.TryRelease() =
            if released then ResourcesReleased
            elif ambiguous || (started && not localSettled()) then ReleaseRefused(Support.diagnostic "original-local-obligations-pending")
            elif started then
                updateCleanup()
                match (snapshot()).Cleanup.LocalTasks,(snapshot()).Cleanup.LeaderTermination with
                | Known(Settled,_),Known(true,_) ->
                    let prior = (snapshot()).Cleanup
                    if apply (CleanupObserved { prior with Custody = Released(stampAt(clock.Now())) }) then
                        childProcess.Dispose()
                        released <- true
                        ResourcesReleased
                    else ReleaseRefused(Support.diagnostic "original-cleanup-proof-refused")
                | _ -> ReleaseRefused(Support.diagnostic "original-local-proof-missing")
            else
                childProcess.Dispose()
                released <- true
                ResourcesReleased
