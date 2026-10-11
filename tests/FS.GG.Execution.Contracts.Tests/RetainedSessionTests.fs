namespace FS.GG.Execution.Contracts.Tests
open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Execution.Contracts

// A conformance fake owns only counters/state, never native authority or a background worker.
// Passing these controls cannot qualify a backend's ownership or deadline behavior.
type private RetainedSession(command, identity, budget, clock: IMonotonicClock, owner: OwnerReference) =
    let mutable state = Lifecycle.prepare command identity budget |> Samples.ok
    let mutable released = false
    let mutable started = false
    let mutable starts = 0
    let apply event =
        state <- Lifecycle.apply (clock.Now()) event state |> Samples.ok
    let returnSnapshot () =
        apply ReturnRequested
        Lifecycle.snapshot state
    member _.Starts = starts
    member _.SettleLocalResources() =
        if started then
            apply (ReaderObserved(Stdout,Samples.known Eof))
            apply (ReaderObserved(Stderr,Samples.known Eof))
            apply (ExitObserved(Samples.known(Exited 0)))
            let previous = (Lifecycle.snapshot state).Cleanup
            apply (CleanupObserved { previous with Disposition = Incomplete; LeaderTermination = Samples.known true; LocalTasks = Samples.known Settled })
    interface IProcessSession with
        member _.Identity = identity
        member _.Owner = owner
        member _.RunAsync cancellation =
            if cancellation.IsCancellationRequested && not started then apply (StopRequested CallerCancellation)
            if not started && not cancellation.IsCancellationRequested && not released then
                match Budget.clip (clock.Now()) Work TimeSpan.MaxValue budget with
                | Error _ -> ()
                | Ok _ ->
                    apply BeginLaunch
                    let previous = (Lifecycle.snapshot state).Cleanup
                    apply (CleanupObserved { previous with Custody = RetainedByOwner owner; LocalTasks = Samples.knownBefore Outstanding })
                    apply (CapabilitiesObtained [ { Capability = BoundedReturnWithRetainedCustody; Obtained = Samples.knownBefore Samples.evidence } ])
                    apply TargetReleaseRequested
                    apply (LaunchObserved(Started Samples.generation))
                    started <- true
                    starts <- starts + 1
            Task.FromResult(returnSnapshot())
        member _.ObserveAsync _ = Task.FromResult(returnSnapshot())
        member _.RequestStopAsync(reason, _) =
            match Budget.clip (clock.Now()) Cleanup TimeSpan.MaxValue budget with
            | Error _ -> Task.FromResult(StopRefused(Samples.diagnostic "original-time-expired"))
            | Ok _ -> apply (StopRequested reason); Task.FromResult((Lifecycle.snapshot state).Stop)
        member _.TryRelease() =
            let snapshot = Lifecycle.snapshot state
            let settled = match snapshot.Cleanup.LocalTasks with Known(Settled,_) -> true | _ -> false
            let terminal = match snapshot.Cleanup.LeaderTermination with Known(true,_) -> true | _ -> false
            if not started || (settled && terminal) then
                if started then apply (CleanupObserved { snapshot.Cleanup with Custody = Released Samples.stamp })
                released <- true
                ResourcesReleased
            else ReleaseRefused(Samples.diagnostic "local-obligations-pending")

module RetainedSessionTests =
    open Samples
    let private create clock =
        let command = { command with Capabilities = [ { Capability = BoundedReturnWithRetainedCustody; Required = true }; { Capability = WorkloadEmptyObservation; Required = false } ] }
        RetainedSession(command,identity,budget,clock,{ OwnerId = "fixture-owner"; Evidence = evidence })

    [<Fact>]
    let ``caller retains prepared fake before bounded return and pending local release refuses`` () =
        let owner = create { new IMonotonicClock with member _.Now() = now 100L }
        let session = owner :> IProcessSession
        let original = session.RunAsync(CancellationToken.None).Result
        Assert.Equal(1,owner.Starts)
        Assert.Equal(Retiring,original.Phase)
        Assert.True(match session.TryRelease() with ReleaseRefused _ -> true | _ -> false)
        session.RunAsync(CancellationToken.None).Result |> ignore
        Assert.Equal(1,owner.Starts)

    [<Fact>]
    let ``local release does not establish unknown whole-workload cleanup`` () =
        let owner = create { new IMonotonicClock with member _.Now() = now 100L }
        let session = owner :> IProcessSession
        session.RunAsync(CancellationToken.None).Result |> ignore
        owner.SettleLocalResources()
        Assert.Equal(ResourcesReleased,session.TryRelease())
        let result = session.ObserveAsync(CancellationToken.None).Result
        Assert.Equal(Incomplete,result.Cleanup.Disposition)
        match result.Cleanup.WorkloadEmpty with Unknown _ -> () | _ -> failwith "Unknown scope erased"

    [<Fact>]
    let ``expired fake session returns without new launch time or implicit retirement`` () =
        let mutable ticks = 100L
        let owner = create { new IMonotonicClock with member _.Now() = now ticks }
        let session = owner :> IProcessSession
        session.RunAsync(CancellationToken.None).Result |> ignore
        ticks <- 10101L
        let result = session.ObserveAsync(CancellationToken.None).Result
        Assert.Equal(10100L,(Budget.describe result.OriginalBudget).TotalExpiresAt)
        Assert.True(match session.TryRelease() with ReleaseRefused _ -> true | _ -> false)
        Assert.True(match session.RequestStopAsync(WorkDeadline,CancellationToken.None).Result with StopRefused _ -> true | _ -> false)
        Assert.Equal(1,owner.Starts)

    [<Fact>]
    let ``prestart cancellation has no fake workload and release is no-effect`` () =
        let owner = create { new IMonotonicClock with member _.Now() = now 100L }
        let session = owner :> IProcessSession
        session.RunAsync(CancellationToken(true)).Result |> ignore
        Assert.Equal(0,owner.Starts)
        Assert.Equal(ResourcesReleased,session.TryRelease())
