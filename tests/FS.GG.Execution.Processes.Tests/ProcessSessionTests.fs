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
        let payload = Array.create 32768 (byte 's')
        let session,budget,liveClock = prepare { command with Arguments = [ "-c"; "exec 0<&-; /bin/sleep 0.1" ]; Input = Bytes(ReadOnlyMemory<byte>(payload)) }
        session.RunAsync(CancellationToken.None).GetAwaiter().GetResult() |> ignore
        let final = settle liveClock budget session
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
