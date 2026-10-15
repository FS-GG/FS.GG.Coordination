namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution
open Xunit

// All external ports are inert. Every case drives the production Executor and its disk journal.
type private HostExecutorRunner() =
    let mutable runs = 0
    let mutable cleans = 0
    let mutable recoveries = 0
    let mutable requests = []
    let mutable cleanupRequests = []
    let mutable cleanup: PortableProcessRequest -> Task<bool> = fun _ -> Task.FromResult true
    let mutable afterReservation: unit -> unit = ignore
    let mutable observation =
        { ExecutionStarted = true; ExitCode = Some 0; StandardOutput = Array.empty; StandardError = Array.empty
          CancellationRequested = false; TerminationObserved = true; Interrupted = false
          OutputLimitExceeded = false; OutputComplete = true; SourceTree = None; SnapshotSha256 = None
          RuntimeIdentity = Some "inert-runner"; ContainerIdentity = Some "synthetic-container"
          VerificationObserved = true; VerificationOutput = Some Array.empty
          VerificationCustodyLimitExceeded = false; Refusal = None }
    member _.Runs = runs
    member _.Cleans = cleans
    member _.Recoveries = recoveries
    member _.Requests = requests
    member _.CleanupRequests = cleanupRequests
    member _.Cleanup with set value = cleanup <- value
    member _.AfterReservation with set value = afterReservation <- value
    member _.Observation with get () = observation and set value = observation <- value
    interface IPortableProcessRunner with
        member _.RunAsync(request, _) =
            Interlocked.Increment(&runs) |> ignore
            requests <- request :: requests
            afterReservation ()
            Task.FromResult observation
        member _.RecoverAsync(_, _) =
            Interlocked.Increment(&recoveries) |> ignore
            failwith "host recovery must not invoke an effect-capable recovery port"
        member _.CleanupAsync(request, _) =
            Interlocked.Increment(&cleans) |> ignore
            cleanupRequests <- request :: cleanupRequests
            cleanup request

type private HostExecutorFixture() =
    let root = Path.Combine(Path.GetTempPath(), "fsgg-inert-host-" + Guid.NewGuid().ToString("N"))
    let start = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let mutable now = start
    let image = "ghcr.io/fs-gg/inert@sha256:" + String.replicate 32 "ab"
    let hash = String.replicate 64 "b"
    let profile: PortableWorkspaceProfile =
        { ProfileId = "inert-host"; Revision = 1UL; WorkspaceScope = "inert-workspace"
          SourceRevision = String.replicate 20 "cd"; QualifiedImage = image
          Components = [ { Id = "sample"; Language = "python"; WorkingDirectory = "sample"
                           Toolchain = { Id = "python"; Version = "3.13" }
                           EntryPoints = { Build = "sample-build"; Test = "sample-test"; Lint = None; Artifact = None } } ]
          ProductBuild = "product-build"; ProductTest = "product-test"; ProductJourney = "product-journey"
          MaximumRuntimeSeconds = 60UL; MaximumOutputBytes = 4096UL }
    let command: PortableWorkspaceCommand =
        { CommandId = Guid.NewGuid(); IdempotencyId = "inert-idempotency"; WorkspaceScope = profile.WorkspaceScope
          ProfileId = profile.ProfileId; ProfileRevision = profile.Revision; SourceRevision = profile.SourceRevision
          ExpectedWorkflowRevision = 1UL; FenceGeneration = 1UL; CausationId = None
          Deadline = start.AddSeconds 100.; Operation = "build"; ComponentId = Some "sample" }
    let authority: PortableWorkspaceAuthority =
        { WorkspaceScope = profile.WorkspaceScope; WorkflowRevision = 1UL; FenceGeneration = 1UL; ObservedAt = start }
    let operation: PortableReviewedOperation =
        { EntryPoint = "sample-build"; OperationIdentity = "build"; ComponentId = Some "sample"
          WorkingDirectory = "sample"; QualifiedImage = image; RequiredToolchains = [ "python", "3.13" ]
          Executable = "/inert/never-executed"; Arguments = []; VerificationIdentity = "inert-verification"
          VerificationPath = "verification.json"; VerificationSha256 = hash; RecipeSha256 = hash }
    let policy: PortableExecutorPolicy =
        { WorkspaceRoot = root; WorkspaceScope = profile.WorkspaceScope; SourceRevision = profile.SourceRevision
          QualifiedImage = image; MaximumRuntimeSeconds = 60UL; MaximumOutputBytes = 4096UL; Operations = [ operation ]
          Runtime = { GitExecutable = "/inert/git"; TarExecutable = "/inert/tar"; PodmanExecutable = "/inert/podman"
                      PodmanGlobalArguments = []; StateRoot = Path.Combine(root, "state")
                      ContainerPath = "/usr/bin:/bin"; ContainerUser = "32768:32768"
                      HostEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/bin:/bin" ]
                      ContainerEnvironment = Map [ "HOME", "/tmp"; "PATH", "/usr/bin:/bin" ]
                      MaximumSnapshotBytes = 4096UL; TerminationGrace = TimeSpan.FromSeconds 5. } }
    let manifest = { Digest = "synthetic-selected-manifest"; MemberCount = 1UL; TotalBytes = 10UL }
    let reservation =
        { Binding = { Command = command; QualifiedImage = image; AllocationId = "synthetic-allocation"
                      WorkerId = "synthetic-worker"; AllocationReceiptDigest = "synthetic-allocation-receipt" }
          AdmittedAt = start; HostLeaseExpiresAt = start.AddSeconds 90.; MaximumRuntime = TimeSpan.FromSeconds 60.
          ExportReserve = TimeSpan.FromSeconds 10.; CleanupReserve = TimeSpan.FromSeconds 10.
          Selection = SelectedOutputs manifest }
    let runner = HostExecutorRunner()
    member _.Root = root
    member _.Start = start
    member _.At seconds = start.AddSeconds seconds
    member _.Now with get () = now and set value = now <- value
    member _.Runner = runner
    member _.Profile = profile
    member _.Command = command
    member _.Authority = authority
    member _.Policy = policy
    member _.Reservation = reservation
    member _.Manifest = manifest
    member _.Executor(?selected: PortableHostReservation, ?legacy: bool) =
        if defaultArg legacy false then PortableWorkspaceExecutor.Executor(policy, runner, clock = (fun () -> now))
        else PortableWorkspaceExecutor.Executor(policy, runner, clock = (fun () -> now), hostReservation = defaultArg selected reservation)
    member _.Journal = Directory.GetFiles(Path.Combine(policy.Runtime.StateRoot, "journal-v1"), "*.bin") |> Array.exactlyOne
    member _.Observation seconds value =
        { Binding = reservation.Binding; CollectedAt = start.AddSeconds seconds; ReceiptDigest = "synthetic-independent-receipt"; Value = value }
    interface IDisposable with
        member _.Dispose() = if Directory.Exists root then Directory.Delete(root, true)

module private HostExecutorAssertions =
    let wait (task: Task<'a>) = task.GetAwaiter().GetResult()
    let ok = function Ok value -> value | Error reason -> failwith reason
    let receipt = function Completed value | Duplicate value -> value | other -> failwithf "Expected receipt, got %A" other
    let refused = function Refused _ -> () | other -> failwithf "Expected refusal, got %A" other
    let execute (fixture: HostExecutorFixture) (executor: PortableWorkspaceExecutor.Executor) =
        executor.ExecuteAsync(fixture.Authority, fixture.Profile, fixture.Command, CancellationToken.None) |> wait
    let recover (fixture: HostExecutorFixture) (executor: PortableWorkspaceExecutor.Executor) =
        executor.RecoverAsync(fixture.Profile, fixture.Command, CancellationToken.None) |> wait
    let state (fixture: HostExecutorFixture) (executor: PortableWorkspaceExecutor.Executor) =
        executor.ReadHostState(fixture.Profile, fixture.Command) |> ok
    let export (fixture: HostExecutorFixture) (executor: PortableWorkspaceExecutor.Executor) evidence =
        executor.ReconcileHost(fixture.Profile, fixture.Command, { PortableHostAllocation.emptyLifecycle with Export = evidence }) |> ok |> ignore
    let ready fixture executor =
        export fixture executor (EvidenceKnown(fixture.Observation 10. (DurableManifestReadback fixture.Manifest)))
        fixture.Now <- fixture.At 20.

open HostExecutorAssertions

type PortableHostExecutorTests() =
    [<Fact>]
    member _.``restart merges authenticated uncertain or retired effects before cleanup``() =
        for field in [ "inner-unknown"; "outer-unknown"; "outer-known" ] do
            use fixture = new HostExecutorFixture()
            let executor = fixture.Executor()
            execute fixture executor |> receipt |> ignore
            ready fixture executor
            let journal = fixture.Journal
            let lifecycle =
                match field with
                | "inner-unknown" -> { PortableHostAllocation.emptyLifecycle with InnerCleanup = EvidenceUnknown "original request uncertain" }
                | "outer-unknown" -> { PortableHostAllocation.emptyLifecycle with OuterRetirement = EvidenceUnknown "original retirement uncertain" }
                | _ -> { PortableHostAllocation.emptyLifecycle with OuterRetirement = EvidenceKnown(fixture.Observation 20. ()) }
            let bytes = Encoding.UTF8.GetBytes field
            let identity = { Issuer = "controlled-host"; ContainerId = "controlled-cid"; AllocationDevice = 5UL; AllocationInode = 17UL }
            let verifier =
                { new IPortableHostSupplierVerifier with
                    member _.Authenticate _ =
                        Ok { Identity = identity; Reservation = fixture.Reservation; CollectedAt = fixture.Now
                             ReceiptDigest = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant(); Lifecycle = lifecycle } }
            let evidence = PortableHostSupplierIngress.authenticate verifier identity fixture.Reservation fixture.Now (TimeSpan.FromSeconds 5.) bytes |> ok
            let caller = PortableWorkspaceExecutor.SupplierExecutor.Create(fixture.Policy, fixture.Runner, evidence, (fun () -> fixture.Now)) |> ok
            caller.RecoverAsync(fixture.Profile, CancellationToken.None) |> wait |> receipt |> ignore
            caller.ExecuteAsync(fixture.Authority, fixture.Profile, CancellationToken.None) |> wait |> receipt |> ignore
            Assert.Equal(1, fixture.Runner.Runs)
            Assert.Equal(0, fixture.Runner.Cleans)
            Assert.Equal(journal, fixture.Journal)
            let state = caller.ReadHostState fixture.Profile |> ok
            match field with
            | "inner-unknown" -> Assert.Equal(lifecycle.InnerCleanup, state.Lifecycle.InnerCleanup)
            | _ -> Assert.Equal(lifecycle.OuterRetirement, state.Lifecycle.OuterRetirement)

    [<Fact>]
    member _.``supplier freshness is checked after durable reservation at the actual runner handoff``() =
        use fixture = new HostExecutorFixture()
        let bytes = Encoding.UTF8.GetBytes "controlled admission before preparation"
        let identity = { Issuer = "controlled-host"; ContainerId = "controlled-cid"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let verifier =
            { new IPortableHostSupplierVerifier with
                member _.Authenticate _ =
                    Ok { Identity = identity; Reservation = fixture.Reservation; CollectedAt = fixture.Start
                         ReceiptDigest = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                         Lifecycle = PortableHostAllocation.emptyLifecycle } }
        let evidence = PortableHostSupplierIngress.authenticate verifier identity fixture.Reservation fixture.Start (TimeSpan.FromSeconds 5.) bytes |> ok
        let clock () =
            let root = Path.Combine(fixture.Policy.Runtime.StateRoot, "journal-v1")
            if Directory.Exists root && Directory.GetFiles(root, "*.bin").Length > 0 then fixture.At 10.
            else fixture.Start
        let caller = PortableWorkspaceExecutor.SupplierExecutor.Create(fixture.Policy, fixture.Runner, evidence, clock) |> ok
        caller.ExecuteAsync(fixture.Authority, fixture.Profile, CancellationToken.None) |> wait |> refused
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(fixture.Reservation, (caller.ReadHostState fixture.Profile |> ok).Reservation)

    [<Fact>]
    member _.``supplier expiry after cleanup intent preserves unknown without crossing cleanup port``() =
        use fixture = new HostExecutorFixture()
        let original = fixture.Executor()
        execute fixture original |> receipt |> ignore
        ready fixture original
        let beforeIntent = File.ReadAllBytes fixture.Journal
        let current () = File.ReadAllBytes fixture.Journal = beforeIntent
        let executor =
            PortableWorkspaceExecutor.Executor(fixture.Policy, fixture.Runner, clock = (fun () -> fixture.Now),
                                               hostReservation = fixture.Reservation, hostEvidenceCurrent = current)
        recover fixture executor |> receipt |> ignore
        Assert.Equal(0, fixture.Runner.Cleans)
        match (state fixture executor).Lifecycle.InnerCleanup with
        | EvidenceUnknown _ -> ()
        | other -> failwithf "Expected retained cleanup intent, got %A" other
        recover fixture executor |> receipt |> ignore
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``authenticated supplier caller preserves the production journal and both cleanup layers``() =
        use fixture = new HostExecutorFixture()
        let bytes = Encoding.UTF8.GetBytes "controlled transport evidence"
        let identity = { Issuer = "controlled-host"; ContainerId = "controlled-cid"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let authenticate selectedIdentity lifecycle =
            let verifier =
                { new IPortableHostSupplierVerifier with
                    member _.Authenticate _ =
                        Ok { Identity = selectedIdentity; Reservation = fixture.Reservation; CollectedAt = fixture.Now
                             ReceiptDigest = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                             Lifecycle = lifecycle } }
            PortableHostSupplierIngress.authenticate verifier selectedIdentity fixture.Reservation fixture.Now (TimeSpan.FromSeconds 5.) bytes |> ok
        let create evidence =
            PortableWorkspaceExecutor.SupplierExecutor.Create(fixture.Policy, fixture.Runner, evidence, (fun () -> fixture.Now)) |> ok
        let initial = authenticate identity PortableHostAllocation.emptyLifecycle
        let caller = create initial
        caller.ExecuteAsync(fixture.Authority, fixture.Profile, CancellationToken.None) |> wait |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        fixture.Now <- fixture.At 10.
        // Stale evidence cannot authorize cleanup, and reconstruction never renews it.
        caller.RecoverAsync(fixture.Profile, CancellationToken.None) |> wait |> refused
        Assert.True(PortableWorkspaceExecutor.SupplierExecutor.Create(fixture.Policy, fixture.Runner, initial, (fun () -> fixture.Now)) |> Result.isError)
        let exported =
            { PortableHostAllocation.emptyLifecycle with
                Export = EvidenceKnown(fixture.Observation 10. (DurableManifestReadback fixture.Manifest)) }
        let changed = authenticate { identity with AllocationInode = 18UL } exported
        Assert.True(caller.Reconcile(fixture.Profile, changed) |> Result.isError)
        let fresh = authenticate identity exported
        caller.Reconcile(fixture.Profile, fresh) |> ok |> ignore
        let reconstructed = create fresh
        reconstructed.RecoverAsync(fixture.Profile, CancellationToken.None) |> wait |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Runs)
        Assert.Equal(1, fixture.Runner.Cleans)
        let current = reconstructed.ReadHostState fixture.Profile |> ok
        Assert.Equal(fixture.Reservation, current.Reservation)
        Assert.NotEqual(EvidenceKnown (), (PortableHostAllocation.retirement fixture.Now fixture.Reservation current.Lifecycle).ReservationRelease)
        let retired =
            { exported with OuterRetirement = EvidenceKnown(fixture.Observation 10. ()) }
        let terminal = reconstructed.Reconcile(fixture.Profile, authenticate identity retired) |> ok
        Assert.Equal(EvidenceKnown (), (PortableHostAllocation.retirement fixture.Now fixture.Reservation terminal.Lifecycle).ReservationRelease)
        Assert.Equal(1, fixture.Runner.Cleans)

    [<Fact>]
    member _.``authenticated supplier observations cannot relaunch after original journal loss``() =
        use fixture = new HostExecutorFixture()
        let bytes = Encoding.UTF8.GetBytes "controlled existing termination"
        let identity = { Issuer = "controlled-host"; ContainerId = "controlled-cid"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let verifier =
            { new IPortableHostSupplierVerifier with
                member _.Authenticate _ =
                    Ok { Identity = identity; Reservation = fixture.Reservation; CollectedAt = fixture.Now
                         ReceiptDigest = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                         Lifecycle = { PortableHostAllocation.emptyLifecycle with Termination = EvidenceKnown(fixture.Observation 0. ()) } } }
        let evidence = PortableHostSupplierIngress.authenticate verifier identity fixture.Reservation fixture.Now (TimeSpan.FromSeconds 5.) bytes |> ok
        let caller = PortableWorkspaceExecutor.SupplierExecutor.Create(fixture.Policy, fixture.Runner, evidence, (fun () -> fixture.Now)) |> ok
        caller.ExecuteAsync(fixture.Authority, fixture.Profile, CancellationToken.None) |> wait |> refused
        Assert.Equal(0, fixture.Runner.Runs)

    [<Fact>]
    member _.``actual runner uses original deadline and restart retains exact reservation``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        Assert.Equal(fixture.At 60., fixture.Runner.Requests.Head.Deadline)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(fixture.Reservation, (state fixture (fixture.Executor())).Reservation)
        recover fixture (fixture.Executor()) |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``expired or insufficient original window refuses before runner``() =
        use fixture = new HostExecutorFixture()
        fixture.Now <- fixture.At 60.
        execute fixture (fixture.Executor()) |> refused
        let invalid = { fixture.Reservation with CleanupReserve = TimeSpan.FromSeconds 85. }
        fixture.Now <- fixture.Start
        execute fixture (fixture.Executor(selected = invalid)) |> refused
        Assert.Equal(0, fixture.Runner.Runs)

    [<Fact>]
    member _.``all immutable reservation substitutions refuse and preserve original bytes``() =
        use fixture = new HostExecutorFixture()
        execute fixture (fixture.Executor()) |> receipt |> ignore
        let original = File.ReadAllBytes fixture.Journal
        let reservation = fixture.Reservation
        let candidates =
            [ { reservation with Binding = { reservation.Binding with AllocationId = "other" } }
              { reservation with Binding = { reservation.Binding with WorkerId = "other" } }
              { reservation with Binding = { reservation.Binding with AllocationReceiptDigest = "other" } }
              { reservation with Binding = { reservation.Binding with QualifiedImage = "other" } }
              { reservation with HostLeaseExpiresAt = fixture.At 91. }
              { reservation with AdmittedAt = fixture.At 1. }
              { reservation with MaximumRuntime = TimeSpan.FromSeconds 59. }
              { reservation with ExportReserve = TimeSpan.FromSeconds 9. }
              { reservation with CleanupReserve = TimeSpan.FromSeconds 9. }
              { reservation with Selection = NoSelectedOutputs } ]
        for candidate in candidates do
            execute fixture (fixture.Executor(selected = candidate)) |> refused
            Assert.True((original = File.ReadAllBytes fixture.Journal))
        execute fixture (fixture.Executor(legacy = true)) |> refused
        Assert.True((original = File.ReadAllBytes fixture.Journal))

    [<Fact>]
    member _.``legacy v1 remains readable and cannot become host authorization``() =
        use fixture = new HostExecutorFixture()
        execute fixture (fixture.Executor(legacy = true)) |> receipt |> ignore
        let original = File.ReadAllBytes fixture.Journal
        execute fixture (fixture.Executor()) |> refused
        recover fixture (fixture.Executor(legacy = true)) |> receipt |> ignore
        Assert.True((original = File.ReadAllBytes fixture.Journal))
        Assert.Equal(1, fixture.Runner.Runs)

    [<Fact>]
    member _.``missing partial wrong late destination only and unbound exports preserve output``() =
        let cases (fixture: HostExecutorFixture) =
            [ EvidenceMissing "destination ready only"
              EvidenceUnknown "partial transfer"
              EvidenceKnown(fixture.Observation 10. (DurableManifestReadback { fixture.Manifest with TotalBytes = 9UL }))
              EvidenceKnown(fixture.Observation 10. (DurableManifestReadback { fixture.Manifest with Digest = "other" }))
              EvidenceKnown(fixture.Observation 90. (DurableManifestReadback fixture.Manifest))
              EvidenceKnown(fixture.Observation 10. HostCensusConfirmsZeroSelection)
              EvidenceKnown { fixture.Observation 10. (DurableManifestReadback fixture.Manifest) with Binding = { fixture.Reservation.Binding with AllocationId = "other" } } ]
        use template = new HostExecutorFixture()
        for index in 0 .. (cases template).Length - 1 do
            use fixture = new HostExecutorFixture()
            let executor = fixture.Executor()
            execute fixture executor |> receipt |> ignore
            export fixture executor ((cases fixture)[index])
            fixture.Now <- fixture.At 20.
            recover fixture (fixture.Executor()) |> receipt |> ignore
            Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``zero selection needs independent census``() =
        use fixture = new HostExecutorFixture()
        let zero = { fixture.Reservation with Selection = NoSelectedOutputs }
        let executor = fixture.Executor(selected = zero)
        execute fixture executor |> receipt |> ignore
        recover fixture executor |> receipt |> ignore
        Assert.Equal(0, fixture.Runner.Cleans)
        export fixture executor (EvidenceKnown(fixture.Observation 10. HostCensusConfirmsZeroSelection))
        fixture.Now <- fixture.At 20.
        recover fixture executor |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)

    [<Fact>]
    member _.``ordered export and termination allow one cleanup while outer release stays held``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        recover fixture executor |> receipt |> ignore
        execute fixture (fixture.Executor()) |> receipt |> ignore
        recover fixture (fixture.Executor()) |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)
        Assert.Equal(fixture.At 90., fixture.Runner.CleanupRequests.Head.Deadline)
        let current = state fixture executor
        Assert.True(current.Receipt.Value.CleanupCompleted)
        Assert.NotEqual(EvidenceKnown (), (PortableHostAllocation.retirement fixture.Now current.Reservation current.Lifecycle).ReservationRelease)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``false or exception cleanup persists uncertainty across restart``(throws: bool) =
        use fixture = new HostExecutorFixture()
        fixture.Runner.Cleanup <- fun _ -> if throws then failwith "synthetic callback loss" else Task.FromResult false
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        recover fixture executor |> receipt |> ignore
        for _ in 1 .. 2 do recover fixture (fixture.Executor()) |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)
        match (state fixture executor).Lifecycle.InnerCleanup with EvidenceUnknown _ -> () | other -> failwithf "%A" other

    [<Fact>]
    member _.``reentrant callback sees durable Unknown before destructive call``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        fixture.Runner.Cleanup <- fun _ ->
            let original = state fixture (fixture.Executor())
            match original.Lifecycle.InnerCleanup with EvidenceUnknown _ -> () | other -> failwithf "%A" other
            execute fixture (fixture.Executor()) |> receipt |> ignore
            Task.FromResult true
        recover fixture executor |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)

    [<Fact>]
    member _.``concurrent duplicate cannot replay incomplete callback``() =
        use fixture = new HostExecutorFixture()
        let completion = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        fixture.Runner.Cleanup <- fun _ -> completion.Task
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        let pending = executor.RecoverAsync(fixture.Profile, fixture.Command, CancellationToken.None)
        execute fixture (fixture.Executor()) |> receipt |> ignore
        recover fixture (fixture.Executor()) |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)
        Assert.False(pending.IsCompleted)
        completion.SetResult false
        pending |> wait |> receipt |> ignore
        recover fixture (fixture.Executor()) |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)

    [<Fact>]
    member _.``termination missing and insufficient cleanup reserve cannot call cleanup``() =
        use fixture = new HostExecutorFixture()
        fixture.Runner.Observation <- { fixture.Runner.Observation with TerminationObserved = false; Interrupted = true }
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        recover fixture executor |> receipt |> ignore
        Assert.Equal(0, fixture.Runner.Cleans)
        executor.ReconcileHost(fixture.Profile, fixture.Command,
            { PortableHostAllocation.emptyLifecycle with Termination = EvidenceKnown(fixture.Observation 15. ()) }) |> ok |> ignore
        fixture.Now <- fixture.At 81.
        recover fixture executor |> receipt |> ignore
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``late independent reconciliation settles original unknown without replay or renewed deadline``() =
        use fixture = new HostExecutorFixture()
        fixture.Runner.Cleanup <- fun _ -> Task.FromResult false
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        recover fixture executor |> receipt |> ignore
        fixture.Now <- fixture.At 120.
        let incoming =
            { PortableHostAllocation.emptyLifecycle with InnerCleanup = EvidenceKnown(fixture.Observation 21. ());
                                                        OuterRetirement = EvidenceKnown(fixture.Observation 110. ()) }
        let reconciled = executor.ReconcileHost(fixture.Profile, fixture.Command, incoming) |> ok
        Assert.Equal(EvidenceKnown (), (PortableHostAllocation.retirement fixture.Now reconciled.Reservation reconciled.Lifecycle).ReservationRelease)
        recover fixture (fixture.Executor()) |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)
        Assert.Equal(1, fixture.Runner.Runs)

    [<Fact>]
    member _.``first workload failure survives cleanup and sticky conflicts survive restart``() =
        use fixture = new HostExecutorFixture()
        fixture.Runner.Observation <- { fixture.Runner.Observation with ExitCode = Some 7; VerificationObserved = false }
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        let completed = recover fixture executor |> receipt
        Assert.Equal("execution-failed", completed.Result.Error.Value.Code)
        Assert.Equal(Some "execution-failed", (state fixture executor).Lifecycle.FirstFailure)
        let conflict = { PortableHostAllocation.emptyLifecycle with Export = EvidenceKnown(fixture.Observation 11. (DurableManifestReadback fixture.Manifest)) }
        executor.ReconcileHost(fixture.Profile, fixture.Command, conflict) |> ok |> ignore
        let restarted = state fixture (fixture.Executor())
        Assert.Contains("export", restarted.Lifecycle.ReconciliationConflicts)
        Assert.NotEqual(EvidenceKnown (), (PortableHostAllocation.retirement fixture.Now restarted.Reservation restarted.Lifecycle).ReservationRelease)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``truncated and unsupported journal refuse and preserve original bytes``(unsupported: bool) =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        if unsupported then
            use stream = File.Create fixture.Journal
            use writer = new BinaryWriter(stream)
            writer.Write "fsgg.portable-execution-host-journal/999"
        else File.WriteAllBytes(fixture.Journal, [| 255uy |])
        let original = File.ReadAllBytes fixture.Journal
        execute fixture (fixture.Executor()) |> refused
        recover fixture (fixture.Executor()) |> refused
        Assert.True((original = File.ReadAllBytes fixture.Journal))
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``locked journal refuses effect and retains original evidence``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        let original = File.ReadAllBytes fixture.Journal
        use held = new FileStream(Path.ChangeExtension(fixture.Journal, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        recover fixture executor |> refused
        Assert.True((original = File.ReadAllBytes fixture.Journal))
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``cleanup completion with lost durable acknowledgement never permits retry``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        let mutable held: FileStream option = None
        fixture.Runner.Cleanup <- fun _ ->
            held <- Some(new FileStream(Path.ChangeExtension(fixture.Journal, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Task.FromResult true
        try
            recover fixture executor |> refused
        finally
            held |> Option.iter (fun stream -> stream.Dispose())
        let restarted = fixture.Executor()
        match (state fixture restarted).Lifecycle.InnerCleanup with EvidenceUnknown _ -> () | other -> failwithf "%A" other
        recover fixture restarted |> receipt |> ignore
        Assert.Equal(1, fixture.Runner.Cleans)
        let observed = { PortableHostAllocation.emptyLifecycle with InnerCleanup = EvidenceKnown(fixture.Observation 21. ()) }
        fixture.Now <- fixture.At 22.
        let reconciled = restarted.ReconcileHost(fixture.Profile, fixture.Command, observed) |> ok
        Assert.True(reconciled.Receipt.Value.CleanupCompleted)
        let duplicate = recover fixture (fixture.Executor()) |> receipt
        Assert.True(duplicate.CleanupCompleted)
        Assert.Equal(1, fixture.Runner.Cleans)

    [<Fact>]
    member _.``lost workload settlement permits independently gated cleanup without replay or invented receipt``() =
        use fixture = new HostExecutorFixture()
        let mutable held: FileStream option = None
        fixture.Runner.AfterReservation <- fun () ->
            // RunAsync is reached only after the real executor has durably reserved the running record.
            held <- Some(new FileStream(Path.ChangeExtension(fixture.Journal, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        try
            execute fixture (fixture.Executor()) |> refused
        finally
            held |> Option.iter (fun stream -> stream.Dispose())
        let restarted = fixture.Executor()
        let before = state fixture restarted
        Assert.True(before.Receipt.IsNone)
        match recover fixture restarted with PendingDuplicate id -> Assert.Equal(fixture.Command.CommandId, id) | other -> failwithf "%A" other
        Assert.Equal(0, fixture.Runner.Cleans)
        fixture.Now <- fixture.At 20.
        let evidence =
            { PortableHostAllocation.emptyLifecycle with
                Termination = EvidenceKnown(fixture.Observation 10. ())
                Export = EvidenceKnown(fixture.Observation 11. (DurableManifestReadback fixture.Manifest)) }
        restarted.ReconcileHost(fixture.Profile, fixture.Command, evidence) |> ok |> ignore
        for executor in [ restarted; fixture.Executor() ] do
            match recover fixture executor with PendingDuplicate id -> Assert.Equal(fixture.Command.CommandId, id) | other -> failwithf "%A" other
        let after = state fixture (fixture.Executor())
        Assert.True(after.Receipt.IsNone)
        match after.Lifecycle.WorkloadExit with EvidenceKnown _ -> failwith "workload exit must not be invented" | _ -> ()
        match after.Lifecycle.InnerCleanup with EvidenceKnown _ -> () | other -> failwithf "%A" other
        Assert.Equal(1, fixture.Runner.Runs)
        Assert.Equal(1, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``successful callback cannot project completed cleanup through conflicting lifecycle``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        execute fixture executor |> receipt |> ignore
        ready fixture executor
        fixture.Runner.Cleanup <- fun _ ->
            let conflict =
                { PortableHostAllocation.emptyLifecycle with
                    Export = EvidenceKnown(fixture.Observation 12. (DurableManifestReadback { fixture.Manifest with Digest = "conflicting-manifest" })) }
            executor.ReconcileHost(fixture.Profile, fixture.Command, conflict) |> ok |> ignore
            Task.FromResult true
        let duplicate = recover fixture executor |> receipt
        Assert.False(duplicate.CleanupCompleted)
        let restarted = state fixture (fixture.Executor())
        Assert.False(restarted.Receipt.Value.CleanupCompleted)
        Assert.Contains("export", restarted.Lifecycle.ReconciliationConflicts)
        Assert.Equal(1, fixture.Runner.Cleans)

    [<Fact>]
    member _.``later conflicting inner observation clears receipt projection without changing workload result``() =
        use fixture = new HostExecutorFixture()
        let executor = fixture.Executor()
        let original = execute fixture executor |> receipt
        ready fixture executor
        let cleaned = recover fixture executor |> receipt
        Assert.True(cleaned.CleanupCompleted)
        fixture.Now <- fixture.At 22.
        let conflict = { PortableHostAllocation.emptyLifecycle with InnerCleanup = EvidenceKnown(fixture.Observation 21. ()) }
        let reconciled = executor.ReconcileHost(fixture.Profile, fixture.Command, conflict) |> ok
        Assert.False(reconciled.Receipt.Value.CleanupCompleted)
        Assert.Equal(original.Result, reconciled.Receipt.Value.Result)
        let duplicate = recover fixture (fixture.Executor()) |> receipt
        Assert.False(duplicate.CleanupCompleted)
        Assert.Equal(1, fixture.Runner.Cleans)
