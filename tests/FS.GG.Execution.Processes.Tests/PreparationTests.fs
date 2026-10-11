namespace FS.GG.Execution.Processes.Tests

open System
open System.IO
open System.Diagnostics
open System.Threading
open Xunit
open FS.GG.Execution.Contracts
open FS.GG.Execution.Processes

module Samples =
    let ok = function Ok value -> value | Error error -> failwithf "Unexpected refusal: %A" error
    let evidence = { Kind = "test-owner"; Reference = "test:process-session"; Digest = None }
    let owner = { OwnerId = "process-tests"; Evidence = evidence }
    let identity () =
        { InvocationId = Guid.NewGuid(); Execution = None; LaunchOrdinal = 0UL
          SourceDigest = String.replicate 64 "a"; ProfileId = "bcl-routine-test"; ProfileDigest = String.replicate 64 "b" }
    let resources = { MemoryBytes = None; CpuQuota = None; CpuAffinity = None; MaxKernelTasks = None; MaxOwnedProcesses = None; MaxClrProcesses = None }
    let capture =
        { TotalBytes = 4096L; TerminalReserveBytes = 256; StdoutPrefixBytes = 1024; StdoutTailBytes = 256
          StderrPrefixBytes = 1024; StderrTailBytes = 256; ReadChunkBytes = 512; MaximumFrameBytes = 1024; MaximumIncompleteLineBytes = 1024 }
    let command =
        { Executable = "/bin/sh"; Arguments = [ "-c"; "exit 0" ]; WorkingDirectory = Path.GetTempPath()
          Environment = Replace Map.empty; Input = Closed; Capture = capture
          Capabilities = [ { Capability = BoundedReturnWithRetainedCustody; Required = true } ]; Resources = resources }
    let clock () =
        let domain = { Id = Guid.NewGuid(); Frequency = Stopwatch.Frequency }
        { new IMonotonicClock with member _.Now() = { Domain = domain; Ticks = Stopwatch.GetTimestamp() } }
    let prepareWith (command: CommandSpec) clock total reserve =
        let budget = Budget.create ((clock: IMonotonicClock).Now()) total reserve |> ok
        let session = ProcessSessions.createFactory().Prepare(command,identity(),budget,clock,owner) |> ok
        session,budget,clock
    let prepare command = prepareWith command (clock()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.)

module PreparationTests =
    open Samples

    [<Fact>]
    let ``required unsupported capabilities refuse before a session exists`` () =
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.) |> ok
        let unsupported =
            [ StableLeaderIdentity; OwnedDescendantObservation; ContainmentBeforeTarget; PidNamespaceIsolation
              UserNamespaceIsolation; MountNamespaceIsolation; NetworkIsolation; WorkloadEmptyObservation
              OwnedChildReaping; AggregateMemoryLimit; AggregateCpuQuota; KernelTaskLimit; OwnerSurvivalCleanup ]
        for capability in unsupported do
            let input = { command with Capabilities = [ { Capability = capability; Required = true } ] }
            Assert.True(ProcessSessions.createFactory().Prepare(input,identity(),budget,liveClock,owner) |> Result.isError)

    [<Fact>]
    let ``unsupported resources refuse both enforced and sampled requests`` () =
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.) |> ok
        for assurance in [ Enforced; Sampled(TimeSpan.FromMilliseconds 10.) ] do
            let requests =
                [ { resources with MemoryBytes = Some { Value = 1024L; Assurance = assurance } }
                  { resources with CpuQuota = Some { Value = { Quota = TimeSpan.FromMilliseconds 10.; Period = TimeSpan.FromMilliseconds 100. }; Assurance = assurance } }
                  { resources with MaxKernelTasks = Some { Value = 1; Assurance = assurance } }
                  { resources with MaxOwnedProcesses = Some { Value = 1; Assurance = assurance } }
                  { resources with MaxClrProcesses = Some { Value = 1; Assurance = assurance } } ]
            for limits in requests do
                Assert.True(ProcessSessions.createFactory().Prepare({ command with Resources = limits },identity(),budget,liveClock,owner) |> Result.isError)
        Assert.True(ProcessSessions.createFactory().Prepare({ command with Resources = { resources with CpuAffinity = Some [0] } },identity(),budget,liveClock,owner) |> Result.isError)

    [<Fact>]
    let ``literal command input capture owner and clock validation precede allocation and launch`` () =
        let liveClock = clock()
        let budget = Budget.create (liveClock.Now()) (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.) |> ok
        let invalid =
            [ { command with Executable = "sh" }
              { command with Arguments = [ "nul\000argument" ] }
              { command with Input = Bytes(ReadOnlyMemory<byte>(Array.zeroCreate (Validation.limits.RequestBytes + 1))) }
              { command with Capture = { capture with StdoutPrefixBytes = Int32.MaxValue } } ]
        for input in invalid do
            Assert.True(ProcessSessions.createFactory().Prepare(input,identity(),budget,liveClock,owner) |> Result.isError)
        Assert.True(ProcessSessions.createFactory().Prepare(command,identity(),budget,liveClock,{ owner with OwnerId = "" }) |> Result.isError)
        Assert.True(ProcessSessions.createFactory().Prepare(command,identity(),budget,clock(),owner) |> Result.isError)

    [<Fact>]
    let ``pre-cancellation repeated runs and release never launch a prepared session`` () =
        let session,budget,_ = prepare command
        let first = session.RunAsync(CancellationToken(true)).GetAwaiter().GetResult()
        let second = session.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
        Assert.Equal(NotStarted,first.Launch)
        Assert.Equal(NotStarted,second.Launch)
        Assert.Equal(Budget.describe budget,Budget.describe second.OriginalBudget)
        Assert.Equal(NoEffect,first.Cleanup.Custody)
        Assert.Equal(ResourcesReleased,session.TryRelease())
        Assert.Equal(NotStarted,session.RunAsync(CancellationToken.None).GetAwaiter().GetResult().Launch)

    [<Fact>]
    let ``stop before run and expired work cannot acquire a fresh launch window`` () =
        let session,_,_ = prepare command
        session.RequestStopAsync(CallerCancellation,CancellationToken.None).GetAwaiter().GetResult() |> ignore
        Assert.Equal(NotStarted,session.RunAsync(CancellationToken.None).GetAwaiter().GetResult().Launch)
        let domain = { Id = Guid.NewGuid(); Frequency = 1000L }
        let mutable ticks = 1000L
        let clock = { new IMonotonicClock with member _.Now() = { Domain = domain; Ticks = ticks } }
        let expired,budget,_ = prepareWith command clock (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.)
        ticks <- 2000L
        let passiveExpired = expired.ObserveAsync(CancellationToken.None)
        Assert.True(passiveExpired.IsCompleted)
        Assert.Equal(NotStarted, passiveExpired.Result.Launch)
        let result = expired.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
        Assert.Equal(NotStarted,result.Launch)
        Assert.Equal(Budget.describe budget,Budget.describe result.OriginalBudget)
        Assert.True(match result.Stop with Requested(WorkDeadline,_) -> true | _ -> false)

    [<Fact>]
    let ``passive observation validates original clock without granting a lease`` () =
        for fault in [ "regression"; "domain" ] do
            for predecessor in [ "prepare"; "observe" ] do
                for nextCall in [ "observe"; "run"; "stop"; "cancel-observe" ] do
                    let domain = { Id = Guid.NewGuid(); Frequency = 1000L }
                    let mutable observedDomain = domain
                    let mutable ticks = 1000L
                    let mutable reads = 0
                    let liveClock =
                        { new IMonotonicClock with
                            member _.Now() =
                                reads <- reads + 1
                                if reads = 2 then ticks <- 1100L
                                { Domain = observedDomain; Ticks = ticks } }
                    let session,budget,_ = prepareWith command liveClock (TimeSpan.FromSeconds 3.) (TimeSpan.FromSeconds 2.)
                    if predecessor = "observe" then
                        let first = session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
                        Assert.Equal(Prepared, first.Phase)
                        Assert.Equal(NotStarted, first.Launch)
                    if fault = "regression" then ticks <- 1099L
                    else observedDomain <- { domain with Id = Guid.NewGuid() }
                    let result =
                        match nextCall with
                        | "run" -> session.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
                        | "stop" ->
                            let stop = session.RequestStopAsync(CallerCancellation, CancellationToken.None).GetAwaiter().GetResult()
                            Assert.True(match stop with StopRefused _ -> true | _ -> false)
                            session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
                        | "cancel-observe" -> session.ObserveAsync(CancellationToken(true)).GetAwaiter().GetResult()
                        | _ -> session.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult()
                    Assert.Equal(NotStarted, result.Launch)
                    Assert.Equal(NotRequested, result.Stop)
                    Assert.Equal(Budget.describe budget, Budget.describe result.OriginalBudget)
                    Assert.Equal("clock-observation-refused", result.FirstFailure.Value.Diagnostic.Code)
                    // No native launch was requested. The admitted test owner retains this
                    // refused local session through terminal readback; no fresh lease is granted.
