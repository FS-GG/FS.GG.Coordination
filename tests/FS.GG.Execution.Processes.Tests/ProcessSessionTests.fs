namespace FS.GG.Execution.Processes.Tests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Execution.Contracts
open FS.GG.Execution.Processes

module ProcessSessionTests =
    open Samples
    let private bytes (capture: StreamCapture) = Array.append (capture.Prefix.ToArray()) (capture.Tail.ToArray())
    let private text capture = Encoding.UTF8.GetString(bytes capture)
    let private eof = function Known(Eof,_) -> true | _ -> false
    let private exited = function Known(Exited code,_) -> Some code | _ -> None
    let private observeUntil (clock: IMonotonicClock) budget predicate (session: IProcessSession) =
        let mutable result = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        while not (predicate result) do
            let delay = Budget.clip (clock.Now()) Cleanup (TimeSpan.FromMilliseconds 5.) budget |> ok
            Assert.True(delay > TimeSpan.Zero, "original observation budget expired")
            Task.Delay(delay).GetAwaiter().GetResult()
            result <- session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        result
    let private settle clock budget (session: IProcessSession) =
        let result =
            observeUntil clock budget
                (fun result -> match result.Cleanup.LocalTasks with Known(Settled,_) -> true | _ -> false)
                session
        Assert.Equal(ResourcesReleased,session.TryRelease())
        result
    let private run command =
        let session,budget,liveClock = prepare command
        let initial = session.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
        let result = settle liveClock budget session
        Assert.Equal(Budget.describe budget,Budget.describe result.OriginalBudget)
        Assert.True(eof result.Stdout.Reader && eof result.Stderr.Reader)
        Assert.True(result.FirstFailure.IsNone)
        Assert.Equal(Some 0,exited result.Exit)
        Assert.Equal(Incomplete,result.Cleanup.Disposition)
        Assert.True(match result.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
        initial,result

    [<Fact>]
    let ``literal argv preserves empty spaces quotes unicode and shell text`` () =
        let values = [ ""; "literal space"; "quote\"and\\slash"; "café"; "$(never-executed)" ]
        let _,result = run { command with Arguments = [ "-c"; "printf '<%s>' \"$@\""; "fixture" ] @ values }
        Assert.Equal(values |> List.map (fun value -> "<" + value + ">") |> String.concat "",text result.Stdout)

    [<Fact>]
    let ``inherit allowlist and replace preserve override removals`` () =
        let name = "FSGG_PROC_TEST_" + Guid.NewGuid().ToString("N")
        Environment.SetEnvironmentVariable(name,"inherited")
        try
            let script = "printf '%s|%s|%s' \"${" + name + "-absent}\" \"${REMOVE-absent}\" \"${SET-absent}\""
            let cases =
                [ Inherit(Map.ofList [ "REMOVE",None; "SET",Some "set-value" ]),"inherited|absent|set-value"
                  Allowlist([name],Map.ofList [ "REMOVE",None; "SET",Some "set-value" ]),"inherited|absent|set-value"
                  Allowlist([name],Map.ofList [ name,None ]),"absent|absent|absent"
                  Replace(Map.ofList [ "SET","set-value" ]),"absent|absent|set-value" ]
            for environment,expected in cases do
                let _,result = run { command with Arguments = [ "-c"; script ]; Environment = environment }
                Assert.Equal(expected,text result.Stdout)
        finally Environment.SetEnvironmentVariable(name,null)

    [<Fact>]
    let ``stdin is copied before launch and closed independently`` () =
        let payload = [| for value in 0 .. 255 -> byte value |]
        let session,budget,liveClock = prepare { command with Executable = "/bin/cat"; Arguments = []; Input = Bytes(ReadOnlyMemory<byte>(payload)) }
        Array.Fill(payload,0uy)
        session.RunAsync(CancellationToken.None).GetAwaiter().GetResult() |> ignore
        let result = settle liveClock budget session
        Assert.Equal<byte>([| for value in 0 .. 255 -> byte value |],bytes result.Stdout)
        let _,closed = run { command with Executable = "/bin/cat"; Arguments = []; Input = Closed }
        Assert.Empty(bytes closed.Stdout)

    [<Fact>]
    let ``exit is observed independently of an inherited pipe writer`` () =
        let gatePath = Path.Combine(Path.GetTempPath(), "fsgg-proc-observe-" + Guid.NewGuid().ToString("N"))
        let writerGate = gatePath + ".writer"
        let script =
            "printf ready; while [ ! -f \"$1\" ]; do :; done; "
            + "(expired=0; trap 'expired=1' CHLD; /bin/sleep 2 & timer=$!; "
            + "while [ ! -f \"$2\" ] && [ \"$expired\" -eq 0 ]; do :; done; "
            + "if [ \"$expired\" -eq 0 ]; then kill \"$timer\" 2>/dev/null || :; fi; "
            + "wait \"$timer\" 2>/dev/null || :) & exit 0"
        let session,budget,liveClock =
            prepareWith
                { command with Arguments = [ "-c"; script; "fixture"; gatePath; writerGate ] }
                (clock()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.)
        // A passive prepared observation must neither terminalize NoEffect nor consume Run.
        let preparedObservation = session.ObserveAsync(CancellationToken.None)
        Assert.True(preparedObservation.IsCompleted)
        Assert.Equal(Prepared, preparedObservation.Result.Phase)
        Assert.Equal(NotStarted, preparedObservation.Result.Launch)
        Assert.Equal(NotRequested, preparedObservation.Result.Stop)
        let running = session.RunAsync(CancellationToken.None)
        let concurrentRun = session.RunAsync(CancellationToken.None)
        let cleanupOwned () =
            // Actual local obligations can remain after both bounded Run tasks return.
            File.WriteAllText(writerGate, "release-writer")
            if not running.IsCompleted || not concurrentRun.IsCompleted then
                session.RequestStopAsync(CallerCancellation, CancellationToken.None).GetAwaiter().GetResult() |> ignore
                Task.WhenAll(running, concurrentRun).GetAwaiter().GetResult() |> ignore
            settle liveClock budget session |> ignore
            if File.Exists gatePath then File.Delete gatePath
            if File.Exists writerGate then File.Delete writerGate
        try
            let active = observeUntil liveClock budget (fun result -> text result.Stdout = "ready") session
            Assert.True(exited active.Exit |> Option.isNone)
            Assert.False(running.IsCompleted)
            Assert.False(concurrentRun.IsCompleted)
            Assert.Equal(NotRequested, active.Stop)
            Assert.True(match active.Cleanup.Custody with RetainedByOwner _ -> true | _ -> false)
            let observations = [| for _ in 1 .. 8 -> session.ObserveAsync(CancellationToken.None) |]
            Assert.True(observations |> Array.forall _.IsCompleted)
            Assert.True(observations |> Array.forall (fun result -> result.Result.Identity = session.Identity))
            File.WriteAllText(gatePath, "release")
            let early = observeUntil liveClock budget (fun result -> exited result.Exit = Some 0) session
            Assert.False(eof early.Stdout.Reader && eof early.Stderr.Reader)
            Assert.False(running.IsCompleted)
            Assert.Equal(NotRequested, early.Stop)
            Assert.True(match session.TryRelease() with ReleaseRefused diagnostic -> diagnostic.Code = "local-obligations-pending" | _ -> false)
            File.WriteAllText(writerGate, "release-writer")
            let initial = running.GetAwaiter().GetResult()
            let concurrentResult = concurrentRun.GetAwaiter().GetResult()
            let final = settle liveClock budget session
            Assert.Equal(initial.Identity, concurrentResult.Identity)
            Assert.Equal(initial.Launch, concurrentResult.Launch)
            Assert.Equal("ready", text final.Stdout)
            Assert.Equal(initial.Identity, final.Identity)
            Assert.True(eof final.Stdout.Reader && eof final.Stderr.Reader)
            Assert.True(match final.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
        with original ->
            try cleanupOwned ()
            with secondary -> original.Data["ProcessObservationCleanupFailure"] <- secondary
            reraise()
        cleanupOwned ()

    [<Fact>]
    let ``concurrent runs share one launch and snapshots cannot mutate retained capture`` () =
        let path = Path.Combine(Path.GetTempPath(),"fsgg-proc-launch-" + Guid.NewGuid().ToString("N"))
        let session,budget,liveClock = prepare { command with Arguments = [ "-c"; "printf x >> \"$1\"; printf original"; "fixture"; path ] }
        try
            let calls = [| for _ in 1 .. 8 -> session.RunAsync(CancellationToken.None) |]
            Task.WhenAll(calls).GetAwaiter().GetResult() |> ignore
            let first = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
            Assert.Equal("x",File.ReadAllText path)
            let copy = first.Stdout.Prefix.ToArray()
            Array.Fill(copy,0uy)
            Assert.Equal("original",text (session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult().Stdout))
            settle liveClock budget session |> ignore
        finally if File.Exists path then File.Delete path

    [<Fact>]
    let ``output overflow charges both channels before bounded retention and preserves first cause`` () =
        let session,budget,liveClock = prepare { command with Arguments = [ "-c"; "while :; do printf '012345678901234567890123456789'; printf 'stderr-bytes' >&2; done" ] }
        let initial = session.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
        let final = settle liveClock budget session
        Assert.True(initial.FirstFailure.IsSome)
        Assert.Equal(initial.FirstFailure,final.FirstFailure)
        Assert.True(final.ChargedTotalBytes > command.Capture.TotalBytes - int64 command.Capture.TerminalReserveBytes)
        Assert.True(final.Stdout.Totals.Retained <= int64 (command.Capture.StdoutPrefixBytes + command.Capture.StdoutTailBytes))
        Assert.True(final.Stderr.Totals.Retained <= int64 (command.Capture.StderrPrefixBytes + command.Capture.StderrTailBytes))
        Assert.True(match final.Stop with Requested(OutputLimit,_) -> true | _ -> false)

    [<Fact>]
    let ``deadline and caller cancellation retain pending custody until real local settlement`` () =
        for cancel in [false;true] do
            let session,budget,liveClock = prepareWith { command with Arguments = [ "-c"; "while :; do :; done" ] } (clock()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.5)
            use cancellation = new CancellationTokenSource()
            let running = session.RunAsync(cancellation.Token)
            if cancel then
                observeUntil liveClock budget (fun result -> match result.Launch with Started _ -> true | _ -> false) session |> ignore
                cancellation.Cancel()
                let observation = session.ObserveAsync(cancellation.Token)
                Assert.True(observation.IsCompleted)
                Assert.True(match observation.Result.Stop with Requested(CallerCancellation,_) -> true | _ -> false)
            let initial = running.GetAwaiter().GetResult()
            Assert.Equal(Budget.describe budget,Budget.describe initial.OriginalBudget)
            Assert.True(match initial.Stop with Requested(CallerCancellation,_) when cancel -> true | Requested(WorkDeadline,_) when not cancel -> true | _ -> false)
            Assert.True(match initial.Cleanup.Custody with RetainedByOwner _ -> true | _ -> false)
            let final = settle liveClock budget session
            Assert.True(exited final.Exit |> Option.isSome)
            Assert.True(eof final.Stdout.Reader && eof final.Stderr.Reader)

    [<Fact>]
    let ``missing executable has confirmed no effect without echoing command secrets`` () =
        let absent = Path.Combine(Path.GetTempPath(),"fsgg-proc-absent-" + Guid.NewGuid().ToString("N"))
        let session,budget,liveClock = prepare { command with Executable = absent; Arguments = [ "private-argument" ]; Environment = Replace(Map.ofList [ "SECRET","private-environment" ]) }
        let initial = session.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
        let final = settle liveClock budget session
        Assert.True(match final.Launch with Refused diagnostic -> diagnostic.Code = "missing-executable" | _ -> false)
        Assert.Equal(NoEffect,final.Cleanup.Custody)
        Assert.Equal(initial.FirstFailure,final.FirstFailure)
        let failure = final.FirstFailure.Value
        Assert.DoesNotContain("private-",failure.Diagnostic.Message)

    [<Fact>]
    let ``actual stdin failure stays bounded without exposing request secrets`` () =
        let payload = Array.create (192 * 1024) (byte 's')
        // No reader can drain this pipe: report its actual capacity before closing
        // stdin. When the admitted input exceeds that capacity, even a parent write
        // that starts before this child runs cannot complete successfully.
        let script = "import fcntl,os,time; capacity=fcntl.fcntl(0,fcntl.F_GETPIPE_SZ); os.write(1,(str(capacity)+'\\n').encode('ascii')); os.close(0); time.sleep(0.1)"
        let session,budget,liveClock =
            prepare { command with Executable = "/usr/bin/python3"; Arguments = [ "-c"; script ]; Input = Bytes(ReadOnlyMemory<byte>(payload)) }
        session.RunAsync(CancellationToken.None).GetAwaiter().GetResult() |> ignore
        let final = settle liveClock budget session
        let parsed,capacity = Int32.TryParse((text final.Stdout).Trim())
        Assert.True(parsed, "closed-reader fixture must report its actual stdin pipe capacity")
        Assert.True(capacity > 0 && capacity < payload.Length, "admitted input must exceed the observed stdin pipe capacity")
        Assert.True(final.FirstFailure.IsSome, "the observed closed reader must cause the bounded stdin write to fail")
        Assert.Equal("bcl-stdin-write-failed",final.FirstFailure.Value.Diagnostic.Code)
        Assert.Equal("BCL session observation.",final.FirstFailure.Value.Diagnostic.Message)
        Assert.True(match final.Stop with Requested(SinkFailure,_) -> true | _ -> false)

    [<Fact>]
    let ``late settlement never grants a new release window`` () =
        let live = clock()
        let clockGate = obj()
        let mutable expired = false
        let mutable callsAfterExpired = 0
        let mutable original: BudgetDescription option = None
        let clock =
            { new IMonotonicClock with
                member _.Now() =
                    lock clockGate (fun () ->
                        if expired then
                            callsAfterExpired <- callsAfterExpired + 1
                            { Domain = original.Value.Domain; Ticks = original.Value.TotalExpiresAt }
                        else live.Now()) }
        let session,budget,liveClock = prepareWith { command with Arguments = [ "-c"; "printf before; (/bin/sleep 0.7) & exit 0" ] } clock (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.5)
        original <- Some(Budget.describe budget)
        let initial = session.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
        Assert.Equal(Some 0,exited initial.Exit)
        Assert.False(eof initial.Stdout.Reader && eof initial.Stderr.Reader)
        lock clockGate (fun () -> expired <- true)
        // The existing admitted outer test process owns this finite fixture and
        // retained session until its terminal readback; no new clock is granted.
        Task.Delay(TimeSpan.FromMilliseconds 350.).GetAwaiter().GetResult()
        let decision = session.TryRelease()
        Assert.True(match decision with ReleaseRefused diagnostic -> diagnostic.Code = "original-time-expired" | _ -> false)
        Assert.True(callsAfterExpired > 0)
        let final = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        Assert.Equal(initial.Exit,final.Exit)
        Assert.Equal(initial.Stdout.Reader,final.Stdout.Reader)
        Assert.Equal(initial.Cleanup.Custody,final.Cleanup.Custody)
        Assert.Equal(Budget.describe budget,Budget.describe final.OriginalBudget)
        // The admitted outer test owner is responsible for later administrative
        // resource retirement. The interface deliberately has no fresh-admission API.

    [<Fact>]
    let ``opt in tree retirement stops an observed child while local release stays separate`` () =
        let marker = Path.Combine(Path.GetTempPath(),"fsgg-proc-tree-" + Guid.NewGuid().ToString("N"))
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 6.) (TimeSpan.FromSeconds 5.) |> ok
        let selected = { command with Arguments = [ "-c"; "/bin/sleep 30 & child=$!; printf '%s' \"$child\" > \"$1\"; wait"; "fixture"; marker ] }
        let factory = ProcessSessions.createFactoryWithRetirement RetirementPolicy.BestEffortProcessTree
        let session = factory.Prepare(selected,identity(),budget,liveClock,owner) |> ok
        let running = session.RunAsync(CancellationToken.None)
        let mutable localReleased = false
        try
            let initial = running.GetAwaiter().GetResult()
            Assert.True(File.Exists marker,"tree fixture must actually create its descendant witness")
            let childPid = Int32.Parse(File.ReadAllText marker)
            let final = settle liveClock budget session
            localReleased <- true
            let executing =
                try
                    let stat = File.ReadAllText(sprintf "/proc/%d/stat" childPid)
                    let state = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ')[0]
                    state <> "Z" && state <> "X"
                with :? FileNotFoundException | :? DirectoryNotFoundException -> false
            Assert.False(executing,"observed fixture child must no longer execute before local acceptance")
            Assert.True(match initial.Stop with Requested(WorkDeadline,_) -> true | _ -> false)
            Assert.True(final.Cleanup.RequestedActions |> List.exists (fun action -> action.Code = "bcl-tree-stop-committed"))
            Assert.True(match final.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
            Assert.True(match final.Cleanup.OwnedChildrenReaped with Unknown _ -> true | _ -> false)
            Assert.Equal(Incomplete,final.Cleanup.Disposition)
            Assert.Equal(initial.FirstFailure,final.FirstFailure)
        with original ->
            // The admitted test owner retains actual session/task if this fixture fails.
            original.Data["RetainedTreeSession"] <- session
            original.Data["RetainedTreeRunTask"] <- running
            original.Data["RetainedTreeMarker"] <- marker
            reraise()
        if localReleased then File.Delete marker

    [<Fact>]
    let ``delayed tree leaf retains real stop task and refuses release until it settles`` () =
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 6.) (TimeSpan.FromSeconds 5.) |> ok
        use entered = new ManualResetEventSlim(false)
        use allowAction = new ManualResetEventSlim(false)
        let mutable retainedTarget: System.Diagnostics.Process option = None
        let action (target: System.Diagnostics.Process) tree =
            retainedTarget <- Some target
            Assert.True(tree)
            entered.Set()
            let remaining = Budget.clip (liveClock.Now()) Cleanup TimeSpan.MaxValue budget |> ok
            if not (allowAction.Wait remaining) then raise (TimeoutException("Original action gate expired."))
            target.Kill(true)
        let factory = ProcessSessions.createFactoryForTesting RetirementPolicy.BestEffortProcessTree action
        let session = factory.Prepare({ command with Arguments = [ "-c"; "while :; do :; done" ] },identity(),budget,liveClock,owner) |> ok
        let running = session.RunAsync(CancellationToken.None)
        try
            observeUntil liveClock budget (fun result -> match result.Launch with Started _ -> true | _ -> false) session |> ignore
            session.RequestStopAsync(CallerCancellation,CancellationToken.None).GetAwaiter().GetResult() |> ignore
            let remaining = Budget.clip (liveClock.Now()) Cleanup TimeSpan.MaxValue budget |> ok
            Assert.True(entered.Wait remaining,"actual stop leaf entry must precede passive observation")
            let passive = session.ObserveAsync(CancellationToken.None)
            Assert.True(passive.IsCompleted,"synchronous tree action must not hold the observation gate")
            Assert.True(match passive.Result.Cleanup.LocalTasks with Known(Outstanding,_) -> true | _ -> false)
            Assert.True(match session.TryRelease() with ReleaseRefused diagnostic -> diagnostic.Code = "local-obligations-pending" | _ -> false)
            allowAction.Set()
            let initial = running.GetAwaiter().GetResult()
            let final = settle liveClock budget session
            Assert.Equal(initial.FirstFailure,final.FirstFailure)
            Assert.True(match final.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
        with original ->
            allowAction.Set()
            original.Data["RetainedTreeSession"] <- session
            original.Data["RetainedTreeRunTask"] <- running
            original.Data["RetainedTreeActionProcess"] <- retainedTarget
            reraise()

    [<Fact>]
    let ``failed tree leaf preserves first cause and needs independently owned retirement`` () =
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 6.) (TimeSpan.FromSeconds 5.) |> ok
        let mutable retainedTarget: System.Diagnostics.Process option = None
        let mutable actions = 0
        let action (target: System.Diagnostics.Process) tree =
            Assert.True(tree)
            retainedTarget <- Some target
            actions <- actions + 1
            raise (IOException("Controlled native leaf failure."))
        let factory = ProcessSessions.createFactoryForTesting RetirementPolicy.BestEffortProcessTree action
        let session = factory.Prepare({ command with Arguments = [ "-c"; "while :; do :; done" ] },identity(),budget,liveClock,owner) |> ok
        let running = session.RunAsync(CancellationToken.None)
        let hasFailure (result: ExecutionResult) =
            (result.FirstFailure |> Option.exists (fun failure -> failure.Diagnostic.Code = "bcl-tree-stop-failed"))
            || (result.AdditionalFailures |> List.exists (fun failure -> failure.Diagnostic.Code = "bcl-tree-stop-failed"))
        try
            observeUntil liveClock budget (fun result -> match result.Launch with Started _ -> true | _ -> false) session |> ignore
            session.RequestStopAsync(CallerCancellation,CancellationToken.None).GetAwaiter().GetResult() |> ignore
            let failed = observeUntil liveClock budget hasFailure session
            Assert.Equal(1,actions)
            Assert.True(match session.TryRelease() with ReleaseRefused _ -> true | _ -> false)
            Assert.True(match failed.Cleanup.LocalTasks with Known(Outstanding,_) -> true | _ -> false)
            // This admitted outer fixture owner retains the exact callback Process.
            // It retires that fixture independently; the consumed inner action is never retried.
            let remaining = Budget.clip (liveClock.Now()) Cleanup TimeSpan.MaxValue budget |> ok
            Assert.True(remaining > TimeSpan.Zero)
            let target = retainedTarget |> Option.defaultWith (fun () -> failwith "Missing original leaf Process.")
            if not target.HasExited then target.Kill(true)
            running.GetAwaiter().GetResult() |> ignore
            let final = settle liveClock budget session
            Assert.Equal(1,actions)
            Assert.True(hasFailure final)
            Assert.Equal(failed.FirstFailure,final.FirstFailure)
            Assert.True(match final.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)
        with original ->
            original.Data["RetainedFailedTreeSession"] <- session
            original.Data["RetainedFailedTreeRunTask"] <- running
            original.Data["RetainedFailedTreeActionProcess"] <- retainedTarget
            reraise()

module TextProcessSessionTests =
    open Samples
    let private decoded (capture: TextStreamCapture) = String.concat "" capture.Chunks
    let private fixture = Path.Combine(AppContext.BaseDirectory,"Fixtures","fixture.py")
    let private request maximum arguments : TextCommandSpec =
        { Executable = "/usr/bin/python3"; Arguments = fixture :: arguments; WorkingDirectory = Path.GetTempPath()
          Environment = Replace Map.empty; Input = Bytes(ReadOnlyMemory<byte>([|byte 'G'|]))
          TextCapture = { MaximumDecodedUtf8Bytes = maximum; TerminalReserveBytes = 2048 }
          Capabilities = command.Capabilities; Resources = resources }
    let private prepareText request total reserve =
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) total reserve |> ok
        let session = ProcessSessions.createTextFactoryWithRetirement(RetirementPolicy.BestEffortProcessTree).Prepare(request,identity(),budget,liveClock,owner) |> ok
        session,budget,liveClock
    let private settleText liveClock budget (session: ITextProcessSession) =
        let mutable result = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        while not (match result.Process.Cleanup.LocalTasks with Known(Settled,_) -> true | _ -> false) do
            let delay = Budget.clip ((liveClock: IMonotonicClock).Now()) Cleanup (TimeSpan.FromMilliseconds 5.) budget |> ok
            Assert.True(delay > TimeSpan.Zero,"original text cleanup budget expired")
            Task.Delay(delay).GetAwaiter().GetResult()
            result <- session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        Assert.Equal(ResourcesReleased,session.TryRelease())
        Assert.Equal(ResourcesReleased,session.TryRelease())
        session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
    let private runText maximum arguments =
        let session,budget,liveClock = prepareText (request maximum arguments) (TimeSpan.FromSeconds 8.) (TimeSpan.FromSeconds 5.)
        let running = session.RunAsync(CancellationToken.None)
        running.GetAwaiter().GetResult() |> ignore
        let result = settleText liveClock budget session
        Assert.True(running.IsCompleted)
        Assert.Equal(Budget.describe budget,Budget.describe result.Process.OriginalBudget)
        Assert.True(result.Text.SnapshotComplete)
        result
    let private successful result =
        Assert.True(result.Process.FirstFailure.IsNone,sprintf "%A" result.Process.FirstFailure)
        Assert.True(result.Text.FirstFailure.IsNone)
        Assert.Equal(NotRequested,result.Process.Stop)
        Assert.True((match result.Process.Exit with Known(Exited 0,_) -> true | _ -> false),sprintf "exit=%A stderr=%s" result.Process.Exit (decoded result.Text.Stderr))
        Assert.True(match result.Process.Stdout.Reader,result.Process.Stderr.Reader with Known(Eof,_),Known(Eof,_) -> true | _ -> false)
        Assert.True(match result.Process.Cleanup.WorkloadEmpty with Unknown _ -> true | _ -> false)

    [<Theory>]
    [<InlineData("text-large-stdout",300010,0)>]
    [<InlineData("text-large-stderr",10,300000)>]
    [<InlineData("text-large-mixed",150010,150000)>]
    [<InlineData("text-exact",200000,200000)>]
    let ``text mode retains real large reports with one shared cap`` (mode: string) stdoutCount stderrCount =
        let result = runText 400000L [mode]
        successful result
        Assert.Equal(stdoutCount,(decoded result.Text.Stdout).Length)
        Assert.Equal(stderrCount,(decoded result.Text.Stderr).Length)
        Assert.Equal(int64(stdoutCount+stderrCount),result.Text.ChargedDecodedUtf8Bytes)
        Assert.Equal(0L,result.Text.Stdout.OmittedDecodedUtf8Bytes)
        Assert.Equal(0L,result.Text.Stderr.OmittedDecodedUtf8Bytes)
        Assert.Equal(0L,result.Process.Stdout.Totals.Retained)
        Assert.Equal(result.Text.RawTransportBytes,result.Process.ChargedTotalBytes)
        Assert.Equal(result.Text.Stdout.RawTransportBytes,result.Process.Stdout.Totals.Omitted)
        Assert.True(result.Text.Stdout.Chunks.Length + result.Text.Stderr.Chunks.Length <= (stdoutCount+stderrCount+1023)/1024+2)

    [<Fact>]
    let ``shared text overflow is authoritative and remains first cause`` () =
        let result = runText 400000L ["text-over"]
        Assert.Equal("text-decoded-limit",result.Text.FirstFailure.Value.Code)
        Assert.Equal("text-decoded-limit",result.Process.FirstFailure.Value.Diagnostic.Code)
        Assert.True(match result.Process.Stop with Requested(OutputLimit,_) -> true | _ -> false)
        Assert.True(result.Text.ChargedDecodedUtf8Bytes > 400000L)
        Assert.True(result.Text.Stdout.AdmittedDecodedUtf8Bytes + result.Text.Stderr.AdmittedDecodedUtf8Bytes <= 400000L)
        Assert.True(result.Text.Stdout.OmittedDecodedUtf8Bytes + result.Text.Stderr.OmittedDecodedUtf8Bytes > 0L)

    [<Theory>]
    [<InlineData("utf8")>]
    [<InlineData("utf8-bom")>]
    [<InlineData("utf16le")>]
    [<InlineData("utf16be")>]
    [<InlineData("utf32le")>]
    [<InlineData("utf32be")>]
    let ``text reader preserves BOM detected encodings and multibyte text`` mode =
        let result = runText 400000L ["text-encoding";mode]
        successful result
        Assert.Equal("café😀\n",decoded result.Text.Stdout)
        Assert.Equal(10L,result.Text.ChargedDecodedUtf8Bytes)

    [<Fact>]
    let ``malformed and truncated UTF8 uses existing replacement fallback`` () =
        let result = runText 400000L ["text-encoding";"malformed"]
        successful result
        Assert.Equal("A�B�",decoded result.Text.Stdout)
        Assert.Equal(8L,result.Text.ChargedDecodedUtf8Bytes)

    [<Fact>]
    let ``UTF32 transport worst case is separately bounded from decoded capture`` () =
        let result = runText 400000L ["text-encoding";"utf32-large"]
        successful result
        Assert.Equal(String('X',300000),decoded result.Text.Stdout)
        Assert.Equal(300000L,result.Text.ChargedDecodedUtf8Bytes)
        Assert.Equal(1200004L,result.Text.RawTransportBytes)
        Assert.True(result.Text.RawTransportBytes < result.Bounds.MaximumRawTransportBytes)

    [<Fact>]
    let ``large declared cap does not allocate its maximum before tiny output`` () =
        let session,budget,liveClock = prepareText (request (int64 Int32.MaxValue) ["text-encoding";"utf8"]) (TimeSpan.FromSeconds 8.) (TimeSpan.FromSeconds 5.)
        let prepared = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
        Assert.Empty(prepared.Text.Stdout.Chunks)
        Assert.Empty(prepared.Text.Stderr.Chunks)
        Assert.Equal(32768L,prepared.Bounds.PreparedCaptureBytes)
        session.RunAsync(CancellationToken.None).GetAwaiter().GetResult() |> ignore
        let result = settleText liveClock budget session
        successful result
        Assert.Single(result.Text.Stdout.Chunks) |> ignore
        Assert.Equal("café😀\n",decoded result.Text.Stdout)

    [<Fact>]
    let ``concurrent text runs share launch and separate invocations reset charge`` () =
        for _ in 1 .. 2 do
            let session,budget,liveClock = prepareText (request 400000L ["text-large-stdout"]) (TimeSpan.FromSeconds 8.) (TimeSpan.FromSeconds 5.)
            let tasks = [| for _ in 1 .. 4 -> session.RunAsync(CancellationToken.None) |]
            Task.WhenAll(tasks).GetAwaiter().GetResult() |> ignore
            let result = settleText liveClock budget session
            successful result
            Assert.Equal(300010L,result.Text.ChargedDecodedUtf8Bytes)
            Assert.True(tasks |> Array.forall(fun task -> task.Result.Process.Identity = result.Process.Identity))

    [<Fact>]
    let ``short read charges preserve split surrogate fallback while retention coalesces`` () =
        let request,bounds = Validation.textCommand (request 400000L []) |> ok
        let sink = BoundedOutput.create request.Capture |> ok
        let capture = BoundedText.CaptureState(bounds,sink)
        let result = Lifecycle.prepare request (identity()) (Budget.create ((clock()).Now()) (TimeSpan.FromSeconds 8.) (TimeSpan.FromSeconds 5.) |> ok) |> ok |> Lifecycle.snapshot
        let pair = "😀".ToCharArray()
        Assert.Equal(Accepted,capture.Admit(Stdout,[|pair[0]|],1))
        let early = capture.Snapshot result
        Assert.Equal(Accepted,capture.Admit(Stdout,[|pair[1]|],1))
        for _ in 1 .. 2048 do Assert.Equal(Accepted,capture.Admit(Stderr,[|'a'|],1))
        let final = capture.Snapshot result
        Assert.Equal(6L,final.Stdout.AdmittedDecodedUtf8Bytes)
        Assert.Equal("😀",decoded final.Stdout)
        Assert.Equal(1,early.Stdout.Chunks.Head.Length)
        Assert.Equal(2,final.Stderr.Chunks.Length)
        Assert.Equal(2054L,final.ChargedDecodedUtf8Bytes)

    [<Fact>]
    let ``terminal reserve survives text overflow without changing first cause`` () =
        let request,bounds = Validation.textCommand (request 1L []) |> ok
        let sink = BoundedOutput.create request.Capture |> ok
        let capture = BoundedText.CaptureState(bounds,sink)
        Assert.True(match capture.Admit(Stdout,[|'a';'b'|],2) with RetireRequired cause -> cause.Code = "text-decoded-limit" | _ -> false)
        for _ in 1 .. 2 do Assert.Equal(Accepted,capture.RecordTransport(TerminalEvidence,ReadOnlyMemory<byte>(Array.zeroCreate 1024)))
        Assert.Equal(2048L,capture.RawSnapshot().TerminalEvidenceBytes)
        Assert.Equal("text-decoded-limit",capture.RawSnapshot().FirstFailure.Value.Code)

    [<Theory>]
    [<InlineData("ordinary-deadline",false)>]
    [<InlineData("ordinary-cancel",true)>]
    [<InlineData("ordinary-heldpipe",false)>]
    let ``text readers preserve work versus pipe EOF and caller stop custody`` mode cancel =
        let session,budget,liveClock = prepareText (request 400000L [mode]) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.5)
        use cancellation = new CancellationTokenSource()
        let running = session.RunAsync(cancellation.Token)
        if cancel then
            let mutable ready = false
            while not ready do
                let observed = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
                ready <- not observed.Text.Stdout.Chunks.IsEmpty
                if not ready then
                    let delay = Budget.clip (liveClock.Now()) Work (TimeSpan.FromMilliseconds 5.) budget |> ok
                    Task.Delay(delay).GetAwaiter().GetResult()
            cancellation.Cancel()
            session.ObserveAsync(cancellation.Token).GetAwaiter().GetResult() |> ignore
        let initial = running.GetAwaiter().GetResult()
        Assert.True(match initial.Process.Stop with Requested(CallerCancellation,_) when cancel -> true | Requested(WorkDeadline,_) when not cancel -> true | _ -> false)
        Assert.True(match initial.Process.Cleanup.Custody with RetainedByOwner _ -> true | _ -> false)
        if mode = "ordinary-heldpipe" then
            Assert.True(match initial.Process.Exit with Known(Exited 0,_) -> true | _ -> false)
            Assert.False(match initial.Process.Stdout.Reader with Known(Eof,_) -> true | _ -> false)
            Assert.True(match session.TryRelease() with ReleaseRefused _ -> true | _ -> false)
        let final = settleText liveClock budget session
        Assert.Equal(initial.Process.Stop,final.Process.Stop)
        Assert.Equal(initial.Process.FirstFailure,final.Process.FirstFailure)
        Assert.Equal(decoded initial.Text.Stdout,decoded final.Text.Stdout)
        Assert.Equal(Budget.describe budget,Budget.describe final.Process.OriginalBudget)

    [<Theory>]
    [<InlineData(1029L,true)>]
    [<InlineData(1028L,false)>]
    let ``actual StreamReader boundary charges split surrogate and cap plus one`` maximum accepted =
        let result = runText maximum ["text-surrogate"]
        Assert.Equal(1029L,result.Text.ChargedDecodedUtf8Bytes)
        Assert.Equal(1027L,result.Text.RawTransportBytes)
        if accepted then
            successful result
            Assert.Equal(String('A',1023)+"😀",decoded result.Text.Stdout)
        else
            Assert.Equal("text-decoded-limit",result.Text.FirstFailure.Value.Code)
            Assert.Equal("text-decoded-limit",result.Process.FirstFailure.Value.Diagnostic.Code)
            Assert.True(match result.Process.Stop with Requested(OutputLimit,_) -> true | _ -> false)
            Assert.Equal(1026L,result.Text.Stdout.AdmittedDecodedUtf8Bytes)
            Assert.Equal(1024,(decoded result.Text.Stdout).Length)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``GHz clock positive floor zero keeps raw and text run pending until real expiry`` useText =
        let domain = { Id = Guid.NewGuid(); Frequency = 1000000000L }
        let clockGate = obj()
        let mutable ticks = 1000L
        let liveClock = { new IMonotonicClock with member _.Now() = lock clockGate (fun () -> { Domain = domain; Ticks = ticks }) }
        let setTicks value = lock clockGate (fun () -> ticks <- value)
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.) |> ok
        let description = Budget.describe budget
        let selected = request 400000L ["ordinary-deadline"]
        let running,observe,release,readRun,retainedSession =
            if useText then
                let session = ProcessSessions.createTextFactoryWithRetirement(RetirementPolicy.BestEffortProcessTree).Prepare(selected,identity(),budget,liveClock,owner) |> ok
                setTicks (description.WorkExpiresAt - 1L)
                let actual = session.RunAsync(CancellationToken.None)
                (actual :> Task),(fun () -> session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult().Process),
                    session.TryRelease,(fun () -> actual.GetAwaiter().GetResult().Process),box session
            else
                let raw: CommandSpec =
                    { command with Executable = selected.Executable; Arguments = selected.Arguments; WorkingDirectory = selected.WorkingDirectory
                                   Environment = selected.Environment; Input = selected.Input }
                let session = ProcessSessions.createFactoryWithRetirement(RetirementPolicy.BestEffortProcessTree).Prepare(raw,identity(),budget,liveClock,owner) |> ok
                setTicks (description.WorkExpiresAt - 1L)
                let actual = session.RunAsync(CancellationToken.None)
                (actual :> Task),(fun () -> session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()),
                    session.TryRelease,(fun () -> actual.GetAwaiter().GetResult()),box session
        let cleanup () =
            // The admitted test owner advances only its original fixture clock to
            // the original expiry and retains actual native/session tasks throughout.
            setTicks description.WorkExpiresAt
            Assert.True(running.Wait(TimeSpan.FromSeconds 2.),"finite GHz fixture Run did not return at original expiry")
            running.GetAwaiter().GetResult()
            let watchdog = System.Diagnostics.Stopwatch.StartNew()
            let mutable result = observe()
            while not (match result.Cleanup.LocalTasks with Known(Settled,_) -> true | _ -> false) do
                Assert.True(watchdog.Elapsed < TimeSpan.FromSeconds 2.,"finite GHz fixture cleanup did not settle")
                Task.Delay(TimeSpan.FromMilliseconds 5.).GetAwaiter().GetResult()
                result <- observe()
            Assert.Equal(ResourcesReleased,release())
        try
            let remainder = Budget.remaining (liveClock.Now()) budget |> ok
            Assert.False(remainder.WorkExpired)
            Assert.Equal(TimeSpan.Zero,remainder.WorkRemaining)
            Assert.Equal(NotRequested,observe().Stop)
            Assert.False(running.IsCompleted,"active positive sub-TimeSpan remainder must not complete bounded Run")
            setTicks description.WorkExpiresAt
            Assert.True(running.Wait(TimeSpan.FromSeconds 2.),"finite GHz Run did not return at original expiry")
            let final = readRun()
            Assert.True(match final.Stop with Requested(WorkDeadline,_) -> true | _ -> false)
            Assert.Equal(description,Budget.describe final.OriginalBudget)
            cleanup()
        with original ->
            try cleanup()
            with secondary -> original.Data["GHzFixtureCleanupFailure"] <- secondary
            original.Data["RetainedGHzSession"] <- retainedSession
            original.Data["RetainedGHzRunTask"] <- running
            reraise()
