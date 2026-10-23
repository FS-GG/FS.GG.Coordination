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

// New source-journal cases never invoke any process, authenticator executable or host effect.
type PortableHostExecutorTests_SupplierAdmissionJournal() =
    [<Fact>]
    member _.``source draft shares existing key and survives restart only as durability unknown``() =
        use fixture = new HostExecutorFixture()
        let original = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage
        let state = PortableSupplierAdmission.beginIntent original |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        let status = journal.Stage(state, None) |> SupplierAdmissionFixtures.ok
        Assert.Contains("commit-unknown", status)
        let read, durability = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot).Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(state, read)
        match durability with EvidenceUnknown _ -> () | other -> failwithf "Expected Unknown, got %A" other
        fixture.Executor().ExecuteAsync(fixture.Authority, fixture.Profile, fixture.Command, CancellationToken.None)
        |> HostExecutorAssertions.wait |> HostExecutorAssertions.refused
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``source revision conflict and unavailable effect phases preserve journal bytes``() =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        let original = File.ReadAllBytes fixture.Journal
        Assert.True(journal.Stage({ state with Revision = 2UL }, Some 999UL) |> Result.isError)
        Assert.True(journal.Stage({ state with Revision = 2UL; Phase = SupplierAckPrepared }, Some 1UL) |> Result.isError)
        Assert.True(journal.Stage({ state with Revision = 2UL; Phase = SupplierHostExecution }, Some 1UL) |> Result.isError)
        Assert.True((File.ReadAllBytes fixture.Journal = original))
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``v2 pending challenge roundtrip is exact and restart abandons it without authority``() =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        let challenged = SupplierAdmissionFixtures.pending state
        journal.Stage(challenged, Some state.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let read, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(challenged, read)
        let abandoned = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) read |> SupplierAdmissionFixtures.ok
        journal.Stage(abandoned, Some read.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let restored, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(abandoned, restored)
        Assert.Equal(None, restored.Challenge)
        Assert.Contains((SupplierWireV1, read.Challenge.Value.ChallengeId, read.Challenge.Value.Digest, "abandoned"), restored.ChallengeLedger)
        Assert.Equal("abandoned", restored.ChallengeLedger.Head |> fun (_, _, _, value) -> value)
        Assert.True(PortableSupplierAdmission.prepareAck restored |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost restored |> Result.isError)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Theory>]
    [<InlineData("drop", false)>]
    [<InlineData("replace", false)>]
    [<InlineData("mutate", false)>]
    [<InlineData("wrong-id", false)>]
    [<InlineData("wrong-digest", false)>]
    [<InlineData("drop", true)>]
    [<InlineData("replace", true)>]
    member _.``pending challenge cannot disappear without its exact ledger entry``(change: string, changeEpoch: bool) =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let challenge =
            { SupplierAdmissionFixtures.challenge state with
                ChallengeId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                BytesBase64 = SupplierAdmissionFixtures.b64 "pending-challenge"
                Digest = SupplierAdmissionFixtures.domain "challenge" "pending-challenge" }
        let pending = PortableSupplierAdmission.issueChallenge challenge state |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        journal.Stage(pending, Some state.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let originalBytes = File.ReadAllBytes fixture.Journal
        let epoch = if changeEpoch then Guid.Parse "22222222-2222-2222-2222-222222222222" else pending.Original.ReceiverEpoch
        let replacement =
            { challenge with
                ChallengeId = Guid.Parse "33333333-3333-3333-3333-333333333333"
                ReceiverEpoch = epoch
                BytesBase64 = SupplierAdmissionFixtures.b64 "replacement-challenge"
                Digest = SupplierAdmissionFixtures.domain "challenge" "replacement-challenge" }
        let candidate =
            { pending with
                Revision = pending.Revision + 1UL
                Original = { pending.Original with ReceiverEpoch = epoch }
                Challenge =
                    match change with
                    | "replace" -> Some(WireV1 replacement)
                    | "mutate" -> Some(WireV1 { challenge with Purpose = "changed-purpose" })
                    | _ -> None
                ChallengeLedger =
                    match change with
                    | "wrong-id" -> [ SupplierWireV1, replacement.ChallengeId, challenge.Digest, "consumed" ]
                    | "wrong-digest" -> [ SupplierWireV1, challenge.ChallengeId, replacement.Digest, "abandoned" ]
                    | _ -> [] }
        // Each candidate is individually valid; the persisted predecessor makes the transition invalid.
        PortableSupplierAdmission.validate candidate |> SupplierAdmissionFixtures.ok |> ignore
        Assert.True(journal.Stage(candidate, Some pending.Revision) |> Result.isError)
        Assert.True(File.ReadAllBytes fixture.Journal = originalBytes)
        let restored, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(pending, restored)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Theory>]
    [<InlineData("unchanged")>]
    [<InlineData("consumed")>]
    [<InlineData("abandoned")>]
    member _.``pending challenge may remain exact or enter the ledger with its exact identity``(disposition: string) =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let pending = SupplierAdmissionFixtures.pending state
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        journal.Stage(pending, Some state.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let candidate =
            { pending with
                Revision = pending.Revision + 1UL
                Challenge = if disposition = "unchanged" then pending.Challenge else None
                ChallengeLedger =
                    if disposition = "unchanged" then pending.ChallengeLedger
                    else (SupplierWireV1, pending.Challenge.Value.ChallengeId, pending.Challenge.Value.Digest, disposition) :: pending.ChallengeLedger }
        journal.Stage(candidate, Some pending.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let restored, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(candidate, restored)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``truncated or oversized supplier record refuses and preserves original bytes``(oversized: bool) =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        let original = File.ReadAllBytes fixture.Journal
        let damaged = if oversized then Array.zeroCreate<byte> (PortableSupplierAdmission.MaximumSupplierBytes + 1) else original[..original.Length - 2]
        File.WriteAllBytes(fixture.Journal, damaged)
        Assert.True(journal.Read fixture.Command |> Result.isError)
        Assert.True(journal.Stage(state, None) |> Result.isError)
        Assert.True((File.ReadAllBytes fixture.Journal = damaged))
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``old v1 journal remains byte exact and cannot be migrated from present supplier claims``() =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        let path = fixture.Journal
        use memory = new MemoryStream()
        use writer = new BinaryWriter(memory, Encoding.UTF8, true)
        writer.Write "fsgg.portable-execution-journal/1"
        writer.Write 1uy
        writer.Write(fixture.Command.CommandId.ToString("D"))
        writer.Write "original-v1-binding"
        writer.Write "original-v1-container"
        writer.Flush()
        let old = memory.ToArray()
        File.WriteAllBytes(path, old)
        Assert.True(journal.Read fixture.Command |> Result.isError)
        Assert.True(journal.Stage(state, None) |> Result.isError)
        Assert.True((File.ReadAllBytes path = old))
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``exclusive existing key lock refuses source rewrite without losing original pending state``() =
        use fixture = new HostExecutorFixture()
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(fixture.Policy.Runtime.StateRoot)
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        let old = File.ReadAllBytes fixture.Journal
        let challenged = SupplierAdmissionFixtures.pending state
        use held = new FileStream(Path.ChangeExtension(fixture.Journal, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        Assert.True(journal.Stage(challenged, Some 1UL) |> Result.isError)
        Assert.True((File.ReadAllBytes fixture.Journal = old))
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

// Frozen by the protected 6970fd73a6fecafde61d403713c16fb5b0f7aa63 writer, before the /3 codec.
module private SupplierJournalCompatibilityFixtures =
    let pending = Convert.FromBase64String "KmZzZ2cucG9ydGFibGUtZXhlY3V0aW9uLXN1cHBsaWVyLWpvdXJuYWwvMjUAAABzb3VyY2Utb25seS11bnF1YWxpZmllZC1hdXRoZW50aWNhdGlvbi1hbmQtZHVyYWJpbGl0eQIAAAAAAAAAABwCAABleUp6WTJobGJXRWlPaUptYzJkbkxuZHZjbXR6Y0dGalpTNWpiMjF0WVc1a0x6RWlMQ0pqYjIxdFlXNWtTV1FpT2lJeE1URXhNVEV4TVMweE1URXhMVEV4TVRFdE1URXhNUzB4TVRFeE1URXhNVEV4TVRFaUxDSnBaR1Z0Y0c5MFpXNWplVWxrSWpvaWFXNWxjblF0YVdSbGJYQnZkR1Z1WTNraUxDSjNiM0pyYzNCaFkyVlRZMjl3WlNJNkltbHVaWEowTFhkdmNtdHpjR0ZqWlNJc0luQnliMlpwYkdWSlpDSTZJbWx1WlhKMExXaHZjM1FpTENKd2NtOW1hV3hsVW1WMmFYTnBiMjRpT2lJeElpd2ljMjkxY21ObFVtVjJhWE5wYjI0aU9pSmpaR05rWTJSalpHTmtZMlJqWkdOa1kyUmpaR05rWTJSalpHTmtZMlJqWkdOa1kyUmpaR05rSWl3aVpYaHdaV04wWldSWGIzSnJabXh2ZDFKbGRtbHphVzl1SWpvaU1TSXNJbVpsYm1ObFIyVnVaWEpoZEdsdmJpSTZJakVpTENKa1pXRmtiR2x1WlNJNklqSXdNall0TURFdE1ERlVNREE2TURFNk5EQXVNREF3TURBd1dpSXNJbTl3WlhKaGRHbHZiaUk2SW1KMWFXeGtJaXdpWTI5dGNHOXVaVzUwU1dRaU9pSnpZVzF3YkdVaWZRPT1bAAAAZ2hjci5pby9mcy1nZy9pbmVydEBzaGEyNTY6YWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYiQAAABjM2x1ZEdobGRHbGpMVzl5YVdkcGJtRnNMV2x1ZEdWdWRBPT1AAAAANDExZDY3MTEzOTA2NWJmZmU0NDllMDY3YjQ4YmYwY2Y1NDU2ZThlNjI0MDcxNDc5MDdmYWVlNjk5ZGZhM2JjZUAAAABhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhKAAAAGJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmIkAAAAMjIyMjIyMjItMjIyMi0yMjIyLTIyMjItMjIyMjIyMjIyMjIyASQAAAAzMzMzMzMzMy0zMzMzLTMzMzMtMzMzMy0zMzMzMzMzMzMzMzNAAAAANWEyYTVmNDZhZjc4ZTRiNDI5NjJkODMzNjJlOWI1N2I4MTliMTRhMGVmM2VkYTkwYTg4YWJmZjc3ZjBiODJlNBwAAABabkp2ZW1WdUxYZHBjbVV0WTJoaGJHeGxibWRsCAAAAGNyZWF0aW9uJAAAADIyMjIyMjIyLTIyMjItMjIyMi0yMjIyLTIyMjIyMjIyMjIyMoCWkLXISN4IAAAAAAAAAAAAo9nGyEjeCAAAAAAAAAAAZAAAAAAAAADoAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="
    let abandoned = Convert.FromBase64String "KmZzZ2cucG9ydGFibGUtZXhlY3V0aW9uLXN1cHBsaWVyLWpvdXJuYWwvMjUAAABzb3VyY2Utb25seS11bnF1YWxpZmllZC1hdXRoZW50aWNhdGlvbi1hbmQtZHVyYWJpbGl0eQMAAAAAAAAAABwCAABleUp6WTJobGJXRWlPaUptYzJkbkxuZHZjbXR6Y0dGalpTNWpiMjF0WVc1a0x6RWlMQ0pqYjIxdFlXNWtTV1FpT2lJeE1URXhNVEV4TVMweE1URXhMVEV4TVRFdE1URXhNUzB4TVRFeE1URXhNVEV4TVRFaUxDSnBaR1Z0Y0c5MFpXNWplVWxrSWpvaWFXNWxjblF0YVdSbGJYQnZkR1Z1WTNraUxDSjNiM0pyYzNCaFkyVlRZMjl3WlNJNkltbHVaWEowTFhkdmNtdHpjR0ZqWlNJc0luQnliMlpwYkdWSlpDSTZJbWx1WlhKMExXaHZjM1FpTENKd2NtOW1hV3hsVW1WMmFYTnBiMjRpT2lJeElpd2ljMjkxY21ObFVtVjJhWE5wYjI0aU9pSmpaR05rWTJSalpHTmtZMlJqWkdOa1kyUmpaR05rWTJSalpHTmtZMlJqWkdOa1kyUmpaR05rSWl3aVpYaHdaV04wWldSWGIzSnJabXh2ZDFKbGRtbHphVzl1SWpvaU1TSXNJbVpsYm1ObFIyVnVaWEpoZEdsdmJpSTZJakVpTENKa1pXRmtiR2x1WlNJNklqSXdNall0TURFdE1ERlVNREE2TURFNk5EQXVNREF3TURBd1dpSXNJbTl3WlhKaGRHbHZiaUk2SW1KMWFXeGtJaXdpWTI5dGNHOXVaVzUwU1dRaU9pSnpZVzF3YkdVaWZRPT1bAAAAZ2hjci5pby9mcy1nZy9pbmVydEBzaGEyNTY6YWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYiQAAABjM2x1ZEdobGRHbGpMVzl5YVdkcGJtRnNMV2x1ZEdWdWRBPT1AAAAANDExZDY3MTEzOTA2NWJmZmU0NDllMDY3YjQ4YmYwY2Y1NDU2ZThlNjI0MDcxNDc5MDdmYWVlNjk5ZGZhM2JjZUAAAABhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhKAAAAGJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmIkAAAANDQ0NDQ0NDQtNDQ0NC00NDQ0LTQ0NDQtNDQ0NDQ0NDQ0NDQ0AAEAAAAkAAAAMzMzMzMzMzMtMzMzMy0zMzMzLTMzMzMtMzMzMzMzMzMzMzMzQAAAADVhMmE1ZjQ2YWY3OGU0YjQyOTYyZDgzMzYyZTliNTdiODE5YjE0YTBlZjNlZGE5MGE4OGFiZmY3N2YwYjgyZTQJAAAAYWJhbmRvbmVkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="
    let consumed = Convert.FromBase64String "KmZzZ2cucG9ydGFibGUtZXhlY3V0aW9uLXN1cHBsaWVyLWpvdXJuYWwvMjUAAABzb3VyY2Utb25seS11bnF1YWxpZmllZC1hdXRoZW50aWNhdGlvbi1hbmQtZHVyYWJpbGl0eQMAAAAAAAAAABwCAABleUp6WTJobGJXRWlPaUptYzJkbkxuZHZjbXR6Y0dGalpTNWpiMjF0WVc1a0x6RWlMQ0pqYjIxdFlXNWtTV1FpT2lJeE1URXhNVEV4TVMweE1URXhMVEV4TVRFdE1URXhNUzB4TVRFeE1URXhNVEV4TVRFaUxDSnBaR1Z0Y0c5MFpXNWplVWxrSWpvaWFXNWxjblF0YVdSbGJYQnZkR1Z1WTNraUxDSjNiM0pyYzNCaFkyVlRZMjl3WlNJNkltbHVaWEowTFhkdmNtdHpjR0ZqWlNJc0luQnliMlpwYkdWSlpDSTZJbWx1WlhKMExXaHZjM1FpTENKd2NtOW1hV3hsVW1WMmFYTnBiMjRpT2lJeElpd2ljMjkxY21ObFVtVjJhWE5wYjI0aU9pSmpaR05rWTJSalpHTmtZMlJqWkdOa1kyUmpaR05rWTJSalpHTmtZMlJqWkdOa1kyUmpaR05rSWl3aVpYaHdaV04wWldSWGIzSnJabXh2ZDFKbGRtbHphVzl1SWpvaU1TSXNJbVpsYm1ObFIyVnVaWEpoZEdsdmJpSTZJakVpTENKa1pXRmtiR2x1WlNJNklqSXdNall0TURFdE1ERlVNREE2TURFNk5EQXVNREF3TURBd1dpSXNJbTl3WlhKaGRHbHZiaUk2SW1KMWFXeGtJaXdpWTI5dGNHOXVaVzUwU1dRaU9pSnpZVzF3YkdVaWZRPT1bAAAAZ2hjci5pby9mcy1nZy9pbmVydEBzaGEyNTY6YWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYiQAAABjM2x1ZEdobGRHbGpMVzl5YVdkcGJtRnNMV2x1ZEdWdWRBPT1AAAAANDExZDY3MTEzOTA2NWJmZmU0NDllMDY3YjQ4YmYwY2Y1NDU2ZThlNjI0MDcxNDc5MDdmYWVlNjk5ZGZhM2JjZUAAAABhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhKAAAAGJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmIkAAAAMjIyMjIyMjItMjIyMi0yMjIyLTIyMjItMjIyMjIyMjIyMjIyAAEAAAAkAAAAMzMzMzMzMzMtMzMzMy0zMzMzLTMzMzMtMzMzMzMzMzMzMzMzQAAAADVhMmE1ZjQ2YWY3OGU0YjQyOTYyZDgzMzYyZTliNTdiODE5YjE0YTBlZjNlZGE5MGE4OGFiZmY3N2YwYjgyZTQIAAAAY29uc3VtZWQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"

type PortableHostExecutorTests_SupplierJournalV3() =
    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``frozen v2 pending reads without rewrite and CAS matches frozen old successor``(consumed: bool) =
        use fixture = new HostExecutorFixture()
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        let initial = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntent |> SupplierAdmissionFixtures.ok
        journal.Stage(initial, None) |> SupplierAdmissionFixtures.ok |> ignore
        File.WriteAllBytes(fixture.Journal, SupplierJournalCompatibilityFixtures.pending)
        let read, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(SupplierJournalV2, read.JournalVersion)
        Assert.Equal(Some SupplierWireV1, read.Challenge |> Option.map _.Kind)
        Assert.True(File.ReadAllBytes fixture.Journal = SupplierJournalCompatibilityFixtures.pending)
        let successor =
            if consumed then SupplierNativeProofFixtures.dispose "consumed" read
            else PortableSupplierAdmission.abandonChallenge (Guid.Parse "44444444-4444-4444-4444-444444444444") read |> SupplierAdmissionFixtures.ok
        journal.Stage(successor, Some read.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let expected = if consumed then SupplierJournalCompatibilityFixtures.consumed else SupplierJournalCompatibilityFixtures.abandoned
        Assert.True(File.ReadAllBytes fixture.Journal = expected)
        let reread, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(successor, reread)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Theory>]
    [<InlineData("empty")>]
    [<InlineData("wire")>]
    [<InlineData("native")>]
    member _.``explicit v3 survives actual read CAS and restart for every pending variant``(kind: string) =
        use fixture = new HostExecutorFixture()
        let initial = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntentV3 |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        journal.Stage(initial, None) |> SupplierAdmissionFixtures.ok |> ignore
        let read, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(initial, read)
        let candidate =
            match kind with
            | "wire" -> SupplierAdmissionFixtures.pending read
            | "native" -> SupplierNativeProofFixtures.pending read
            | _ -> { read with Revision = read.Revision + 1UL }
        journal.Stage(candidate, Some read.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let bytes = File.ReadAllBytes fixture.Journal
        let restored, durability = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(candidate, restored)
        Assert.True(File.ReadAllBytes fixture.Journal = bytes)
        match durability with EvidenceUnknown _ -> () | _ -> failwith "source durability must remain Unknown"
        use memory = new MemoryStream(bytes)
        use reader = new BinaryReader(memory, Encoding.UTF8)
        let schema = reader.ReadString()
        Assert.Equal("fsgg.portable-execution-supplier-journal/3", schema)
        // Frozen legacy schema dispatch, not a claim about a deployed legacy binary.
        let frozenOldReaderRecognizes = function
            | "fsgg.portable-execution-supplier-journal/2"
            | "fsgg.portable-execution-host-journal/1"
            | "fsgg.portable-execution-journal/1" -> true
            | _ -> false
        Assert.False(frozenOldReaderRecognizes schema)
        let restarted = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) restored |> SupplierAdmissionFixtures.ok
        journal.Stage(restarted, Some restored.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let final, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(SupplierJournalV3, final.JournalVersion)
        Assert.Equal(restarted, final)
        Assert.Equal(None, final.Challenge)
        Assert.Equal(initial.Original.Command.Deadline, final.Original.Command.Deadline)
        fixture.Executor().ExecuteAsync(fixture.Authority, fixture.Profile, fixture.Command, CancellationToken.None)
        |> HostExecutorAssertions.wait |> HostExecutorAssertions.refused
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``both version changes and fresh replacement refuse without changing bytes``(v3: bool) =
        use fixture = new HostExecutorFixture()
        let original = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage
        let state = (if v3 then PortableSupplierAdmission.beginIntentV3 original else PortableSupplierAdmission.beginIntent original) |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        journal.Stage(state, None) |> SupplierAdmissionFixtures.ok |> ignore
        let bytes = File.ReadAllBytes fixture.Journal
        let other = if v3 then SupplierJournalV2 else SupplierJournalV3
        for candidate, expected in
            [ { state with JournalVersion = other; Revision = 2UL }, Some 1UL
              { state with JournalVersion = SupplierJournalV3 }, None
              { state with Revision = 2UL }, Some 999UL ] do
            Assert.True(journal.Stage(candidate, expected) |> Result.isError)
            Assert.True(File.ReadAllBytes fixture.Journal = bytes)
        if not v3 then
            let nativeState = SupplierNativeProofFixtures.pending { state with JournalVersion = SupplierJournalV3 }
            let nativeV2 = { nativeState with JournalVersion = SupplierJournalV2 }
            let historyV2 = { SupplierNativeProofFixtures.dispose "abandoned" nativeState with JournalVersion = SupplierJournalV2; Revision = 2UL }
            for candidate in [ nativeV2; historyV2 ] do
                Assert.True(journal.Stage(candidate, Some state.Revision) |> Result.isError)
                Assert.True(File.ReadAllBytes fixture.Journal = bytes)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``v3 cannot replace occupied execution or host v1 originals``(host: bool) =
        use fixture = new HostExecutorFixture()
        if host then execute fixture (fixture.Executor()) |> receipt |> ignore
        else execute fixture (fixture.Executor(legacy = true)) |> receipt |> ignore
        let bytes = File.ReadAllBytes fixture.Journal
        let state = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntentV3 |> SupplierAdmissionFixtures.ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        Assert.True(journal.Stage(state, None) |> Result.isError)
        Assert.True(File.ReadAllBytes fixture.Journal = bytes)

    [<Theory>]
    [<InlineData("kind")>]
    [<InlineData("id")>]
    [<InlineData("digest")>]
    [<InlineData("status")>]
    [<InlineData("drop")>]
    member _.``native pending disposal requires exact kind nonce digest and disposition``(change: string) =
        use fixture = new HostExecutorFixture()
        let initial = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntentV3 |> SupplierAdmissionFixtures.ok
        let pending = SupplierNativeProofFixtures.pending initial
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        journal.Stage(initial, None) |> SupplierAdmissionFixtures.ok |> ignore
        journal.Stage(pending, Some initial.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let bytes = File.ReadAllBytes fixture.Journal
        let challenge = pending.Challenge.Value
        let ledger =
            if change = "drop" then []
            else [ (if change = "kind" then SupplierWireV1 else SupplierNativeProofV1),
                   (if change = "id" then Guid.NewGuid() else challenge.ChallengeId),
                   (if change = "digest" then String.replicate 64 "a" else challenge.Digest),
                   (if change = "status" then "unknown" else "abandoned") ]
        let invalid = { pending with Revision = pending.Revision + 1UL; Challenge = None; ChallengeLedger = ledger }
        Assert.True(journal.Stage(invalid, Some pending.Revision) |> Result.isError)
        Assert.True(File.ReadAllBytes fixture.Journal = bytes)
        let preserved = { pending with Revision = pending.Revision + 1UL }
        journal.Stage(preserved, Some pending.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let disposed = SupplierNativeProofFixtures.dispose "consumed" preserved
        journal.Stage(disposed, Some preserved.Revision) |> SupplierAdmissionFixtures.ok |> ignore
        let restored, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(disposed, restored)

    [<Theory>]
    [<InlineData("pending-tag")>]
    [<InlineData("optional-tag")>]
    [<InlineData("uuid")>]
    [<InlineData("history-tag")>]
    [<InlineData("history-negative")>]
    [<InlineData("history-overflow")>]
    [<InlineData("disposition")>]
    [<InlineData("base64")>]
    [<InlineData("field-negative")>]
    [<InlineData("field-overflow")>]
    [<InlineData("schema")>]
    [<InlineData("truncated")>]
    [<InlineData("trailing")>]
    [<InlineData("oversized")>]
    member _.``malformed v3 tags counts fields and framing refuse without replacing original``(damage: string) =
        use fixture = new HostExecutorFixture()
        let initial = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntentV3 |> SupplierAdmissionFixtures.ok
        let first = SupplierAdmissionFixtures.pending initial
        let abandoned = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) first |> SupplierAdmissionFixtures.ok
        let pending = SupplierAdmissionFixtures.pending abandoned
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        for state, expected in [initial, None; first, Some initial.Revision; abandoned, Some first.Revision; pending, Some abandoned.Revision] do
            journal.Stage(state, expected) |> SupplierAdmissionFixtures.ok |> ignore
        let original = File.ReadAllBytes fixture.Journal
        use memory = new MemoryStream(original)
        use reader = new BinaryReader(memory, Encoding.UTF8)
        let text () =
            let n = reader.ReadInt32()
            let offset = int memory.Position
            reader.ReadBytes n |> ignore
            offset
        reader.ReadString() |> ignore
        let fieldLength = int memory.Position
        text () |> ignore
        reader.ReadUInt64() |> ignore
        reader.ReadByte() |> ignore
        let originalFields = [ for _ in 1..7 -> text () ]
        Assert.True(reader.ReadBoolean())
        let pendingTag = int memory.Position
        reader.ReadByte() |> ignore
        text () |> ignore
        text () |> ignore
        let base64Offset = text ()
        text () |> ignore
        text () |> ignore
        for _ in 1..6 do reader.ReadInt64() |> ignore
        Assert.False(reader.ReadBoolean())
        let historyCount = int memory.Position
        Assert.Equal(1, reader.ReadInt32())
        let historyTag = int memory.Position
        reader.ReadByte() |> ignore
        text () |> ignore
        text () |> ignore
        let disposition = text ()
        let damaged = Array.copy original
        let setInt offset value = Array.Copy(BitConverter.GetBytes(value: int), 0, damaged, offset, 4)
        let damaged =
            match damage with
            | "pending-tag" -> damaged[pendingTag] <- 2uy; damaged
            | "optional-tag" -> damaged[pendingTag - 1] <- 2uy; damaged
            | "uuid" -> damaged[List.last originalFields] <- byte 'A'; damaged
            | "history-tag" -> damaged[historyTag] <- 255uy; damaged
            | "history-negative" -> setInt historyCount -1; damaged
            | "history-overflow" -> setInt historyCount 129; damaged
            | "disposition" -> damaged[disposition] <- byte 'x'; damaged
            | "base64" -> damaged[base64Offset] <- byte '!'; damaged
            | "field-negative" -> setInt fieldLength -1; damaged
            | "field-overflow" -> setInt fieldLength Int32.MaxValue; damaged
            | "schema" -> damaged[42] <- byte '9'; damaged
            | "truncated" -> damaged[..damaged.Length - 2]
            | "trailing" -> Array.append damaged [| 0uy |]
            | "oversized" -> Array.zeroCreate<byte> (PortableSupplierAdmission.MaximumSupplierBytes + 1)
            | _ -> failwith "unknown control"
        File.WriteAllBytes(fixture.Journal, damaged)
        Assert.True(journal.Read fixture.Command |> Result.isError)
        Assert.True(journal.Stage(initial, None) |> Result.isError)
        Assert.True(journal.Stage({ pending with Revision = pending.Revision + 1UL }, Some pending.Revision) |> Result.isError)
        Assert.True(File.ReadAllBytes fixture.Journal = damaged)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``v3 disposed native history permits a distinct wire nonce and cannot be mutated``() =
        use fixture = new HostExecutorFixture()
        let initial = SupplierAdmissionFixtures.originalFor fixture.Command fixture.Profile.QualifiedImage |> PortableSupplierAdmission.beginIntentV3 |> SupplierAdmissionFixtures.ok
        let native = SupplierNativeProofFixtures.pending initial
        let disposed = SupplierNativeProofFixtures.dispose "consumed" native
        let wire = SupplierAdmissionFixtures.pending disposed
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        for state, expected in [initial, None; native, Some initial.Revision; disposed, Some native.Revision; wire, Some disposed.Revision] do
            journal.Stage(state, expected) |> SupplierAdmissionFixtures.ok |> ignore
        let bytes = File.ReadAllBytes fixture.Journal
        let read, _ = journal.Read fixture.Command |> SupplierAdmissionFixtures.ok
        Assert.Equal(wire, read)
        Assert.Equal(Some SupplierWireV1, read.Challenge |> Option.map _.Kind)
        let _, id, digest, disposition = read.ChallengeLedger.Head
        for history in [ []; [ SupplierWireV1, id, digest, disposition ]; [ SupplierNativeProofV1, id, digest, "abandoned" ] ] do
            Assert.True(journal.Stage({ read with Revision = read.Revision + 1UL; ChallengeLedger = history }, Some read.Revision) |> Result.isError)
            Assert.True(File.ReadAllBytes fixture.Journal = bytes)

/// Disk/source tests only: every successful stage remains durability-Unknown.
type PortableHostExecutorTests_SupplierJournalV4() =
    [<Fact>]
    member _.``fresh v4 stages reopens and fences the same owned journal without a commit witness``() =
        use fixture = new HostExecutorFixture()
        let initial = SupplierCheckpointFixtures.initial ()
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        let staged = journal.StageV4(initial, None, None)
        match staged with SupplierSourceReadback value -> Assert.Equal(initial, value) | other -> failwithf "%A" other
        let reopened = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        let read, durability = reopened.Read initial.Original.Command |> SupplierCheckpointFixtures.ok
        Assert.Equal(initial, read)
        match durability with EvidenceUnknown _ -> () | _ -> failwith "source read cannot prove durability"
        let pending = PortableSupplierAdmission.issueChallenge (SupplierCheckpointFixtures.challenge initial) read |> SupplierCheckpointFixtures.ok
        Assert.Equal(SupplierSourceConflict, reopened.StageV4(pending, Some 1UL, Some 2UL))
        match reopened.StageV4(pending, Some 1UL, Some 1UL) with
        | SupplierSourceReadback value -> Assert.Equal(pending, value)
        | other -> failwithf "%A" other
        Assert.Equal(SupplierSourceConflict, reopened.StageV4(pending, Some 1UL, Some 1UL))
        Assert.Equal(SupplierSourceConflict, reopened.StageV4(initial, None, None))
        Assert.True(reopened.Stage(pending, Some 1UL) |> Result.isError)
        let cold, _ = reopened.Read initial.Original.Command |> SupplierCheckpointFixtures.ok
        Assert.Equal(pending, cold)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``controlled genesis commits each revision through same store and reopens exact checkpoint bytes``() =
        use fixture = new HostExecutorFixture()
        let initial = SupplierCheckpointFixtures.initial ()
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        let stage state expected =
            let fence = expected
            match journal.StageV4(state, expected, fence) with
            | SupplierSourceReadback actual -> Assert.Equal(state, actual)
            | other -> failwithf "%A" other
            let cold, durability = journal.Read state.Original.Command |> SupplierCheckpointFixtures.ok
            Assert.Equal(state, cold)
            match durability with EvidenceUnknown _ -> () | _ -> failwith "source cannot prove durability"
        stage initial None
        let creationPair = SupplierCheckpointFixtures.exchange "creation" initial
        let pendingCreation = PortableSupplierAdmission.issueChallenge creationPair.Challenge initial |> SupplierCheckpointFixtures.ok
        stage pendingCreation (Some 1UL)
        let creation = SupplierCheckpointFixtures.pinCreation initial
        stage creation (Some 2UL)
        let genesisPair = SupplierCheckpointFixtures.exchange "genesis" creation
        let pendingGenesis = PortableSupplierAdmission.issueChallenge genesisPair.Challenge creation |> SupplierCheckpointFixtures.ok
        stage pendingGenesis (Some 3UL)
        let genesis = SupplierCheckpointFixtures.acceptGenesis creation
        stage genesis (Some 4UL)
        let reopened = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        let cold, _ = reopened.Read genesis.Original.Command |> SupplierCheckpointFixtures.ok
        Assert.Equal(genesis.CheckpointCustody.Value.GenesisCheckpointBase64, cold.CheckpointCustody.Value.GenesisCheckpointBase64)
        Assert.Equal(SupplierSourceConflict, reopened.StageV4(genesis, Some 4UL, Some 4UL))
        Assert.True(PortableSupplierAdmission.prepareAck cold |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost cold |> Result.isError)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``v4 CAS requires exact persisted issuance and current accepted stamps despite self consistent checkpoint bytes``() =
        let restamp acceptedRevision currentRevision (state: PortableSupplierAdmissionState) =
            let custody = state.CheckpointCustody.Value
            let checkpoint = Convert.FromBase64String custody.GenesisCheckpointBase64.Value |> PortableSupplierCheckpoint.parseGenesis |> SupplierCheckpointFixtures.ok
            let originalCommon = PortableSupplierCheckpoint.genesisCommon checkpoint
            let common =
                { originalCommon with
                    ExpectedJournalRevision = acceptedRevision - 1UL; JournalRevision = acceptedRevision
                    ExpectedStoreFence = acceptedRevision - 1UL; StoreFence = acceptedRevision }
            use doc = SupplierCheckpointFixtures.journey ()
            let event = doc.RootElement.GetProperty("genesisEvent").GetString() |> SupplierCheckpointFixtures.urlBytes
            use envelope = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(event))
            let payload = envelope.RootElement.GetProperty("payload").GetString() |> SupplierCheckpointFixtures.urlBytes
            let changed = PortableSupplierCheckpoint.createGenesis common common.ObservedHead (SupplierCheckpointFixtures.domain "genesis" payload) |> SupplierCheckpointFixtures.ok
            { state with Revision = currentRevision
                         CheckpointCustody = Some { custody with StoreFence = currentRevision;
                                                                 WireExchanges = custody.WireExchanges |> List.map (fun e -> { e with AcceptedRevision = acceptedRevision; AcceptedStoreFence = acceptedRevision });
                                                                 GenesisCheckpointBase64 = Some(PortableSupplierCheckpoint.serializeGenesis changed |> Convert.ToBase64String) } }
        for skippedIssuance in [true;false] do
            use fixture = new HostExecutorFixture()
            let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
            let stage state expected =
                match journal.StageV4(state,expected,expected) with SupplierSourceReadback _ -> () | other -> failwithf "%A" other
            let initial = SupplierCheckpointFixtures.initial ()
            stage initial None
            let creationPair = SupplierCheckpointFixtures.exchange "creation" initial
            let pendingCreation = PortableSupplierAdmission.issueChallenge creationPair.Challenge initial |> SupplierCheckpointFixtures.ok
            stage pendingCreation (Some 1UL)
            let creation = SupplierCheckpointFixtures.pinCreation initial
            stage creation (Some 2UL)
            let expected,unchanged =
                if skippedIssuance then 3UL,creation
                else
                    let pair = SupplierCheckpointFixtures.exchange "genesis" creation
                    let pending = PortableSupplierAdmission.issueChallenge pair.Challenge creation |> SupplierCheckpointFixtures.ok
                    stage pending (Some 3UL)
                    4UL,pending
            let admitted = SupplierCheckpointFixtures.acceptGenesis creation
            let forged = restamp 4UL (expected + 1UL) admitted
            Assert.True(PortableSupplierAdmission.validate forged |> Result.isOk)
            Assert.Equal(SupplierSourceConflict,journal.StageV4(forged,Some expected,Some expected))
            let cold,_ = journal.Read initial.Original.Command |> SupplierCheckpointFixtures.ok
            Assert.Equal(unchanged,cold)
            Assert.Equal(0,fixture.Runner.Runs)
            Assert.Equal(0,fixture.Runner.Cleans)
            Assert.Equal(0,fixture.Runner.Recoveries)

    [<Fact>]
    member _.``wire pair join failures retain supplied bytes and first cause through same store reopen``() =
        use corpus = SupplierCheckpointFixtures.journey ()
        for creationInput in [true;false] do
            use fixture = new HostExecutorFixture()
            let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
            let stage (state: PortableSupplierAdmissionState) =
                let expected = if state.Revision = 1UL then None else Some(state.Revision - 1UL)
                match journal.StageV4(state,expected,expected) with
                | SupplierSourceReadback actual -> Assert.Equal(state,actual)
                | other -> failwithf "%A" other
                state
            let initial = SupplierCheckpointFixtures.initial () |> stage
            let ready =
                if creationInput then initial
                else
                    let pair = SupplierCheckpointFixtures.exchange "creation" initial
                    PortableSupplierAdmission.issueChallenge pair.Challenge initial |> SupplierCheckpointFixtures.ok |> stage |> ignore
                    SupplierCheckpointFixtures.pinCreation initial |> stage
            let kind,name = if creationInput then SupplierCreation,"creation" else SupplierGenesis,"genesis"
            let pair = SupplierCheckpointFixtures.exchange name ready
            let pending = PortableSupplierAdmission.issueChallenge pair.Challenge ready |> SupplierCheckpointFixtures.ok |> stage
            let verified = SupplierCheckpointFixtures.verifyControlledFrom corpus.RootElement kind pair pending
            // The exact response is valid; its supplied paired result is another request envelope.
            let mismatched = { pair with ResultBase64 = pair.RequestBase64 }
            let accept = if creationInput then PortableSupplierAdmission.pinCreationV4 else PortableSupplierAdmission.acceptWireV4
            let held =
                match accept verified mismatched pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending |> SupplierCheckpointFixtures.ok with
                | SupplierCheckpointHeld value -> stage value
                | _ -> failwith "failed paired join must retain a hold"
            let cold,durability = journal.Read pending.Original.Command |> SupplierCheckpointFixtures.ok
            Assert.Equal(held,cold)
            match durability with EvidenceUnknown _ -> () | _ -> failwith "source read cannot qualify durability"
            Assert.Equal(Some "supplier-context-refused",cold.FirstFailure)
            Assert.Equal(pending.Challenge,cold.Challenge)
            let rejected = cold.CheckpointCustody.Value.RejectedExchanges |> List.exactlyOne
            Assert.Equal(mismatched.RequestBase64,rejected.RequestBase64)
            Assert.Equal<string list>([mismatched.ResultBase64;mismatched.EvidenceBase64],rejected.FramesBase64)
            let abandoned = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) cold |> SupplierCheckpointFixtures.ok |> stage
            let reopened,_ = journal.Read abandoned.Original.Command |> SupplierCheckpointFixtures.ok
            Assert.Equal(abandoned,reopened)
            Assert.Equal(cold.FirstFailure,reopened.FirstFailure)
            Assert.Equal<PortableSupplierRejectedExchange list>(cold.CheckpointCustody.Value.RejectedExchanges,reopened.CheckpointCustody.Value.RejectedExchanges)
            Assert.True(reopened.Challenge.IsNone)
            Assert.True(PortableSupplierAdmission.prepareAck reopened |> Result.isError)
            Assert.True(PortableSupplierAdmission.reserveHost reopened |> Result.isError)
            Assert.Equal(0,fixture.Runner.Runs)
            Assert.Equal(0,fixture.Runner.Cleans)
            Assert.Equal(0,fixture.Runner.Recoveries)

    [<Fact>]
    member _.``native pending anchors cannot be swapped and first rejected frames survive abandonment and cold reopen``() =
        use corpus = SupplierCheckpointFixtures.journey ()
        for row in corpus.RootElement.GetProperty("outputJourneys").EnumerateArray() do
            use fixture = new HostExecutorFixture()
            let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
            let stage (state: PortableSupplierAdmissionState) =
                let expected = if state.Revision = 1UL then None else Some(state.Revision - 1UL)
                match journal.StageV4(state,expected,expected) with
                | SupplierSourceReadback actual -> Assert.Equal(state,actual)
                | other -> failwithf "%A" other
                let cold,durability = journal.Read state.Original.Command |> SupplierCheckpointFixtures.ok
                Assert.Equal(state,cold)
                match durability with EvidenceUnknown _ -> () | _ -> failwith "source read cannot qualify durability"
                cold
            let initial = SupplierCheckpointFixtures.initialFrom row |> stage
            let creationPair = SupplierCheckpointFixtures.exchangeFrom row "creation" initial
            PortableSupplierAdmission.issueChallenge creationPair.Challenge initial |> SupplierCheckpointFixtures.ok |> stage |> ignore
            let creation = SupplierCheckpointFixtures.pinCreationFrom row initial |> stage
            let genesisPair = SupplierCheckpointFixtures.exchangeFrom row "genesis" creation
            PortableSupplierAdmission.issueChallenge genesisPair.Challenge creation |> SupplierCheckpointFixtures.ok |> stage |> ignore
            let genesis = SupplierCheckpointFixtures.acceptGenesisFrom row creation |> stage
            let appendPair = SupplierCheckpointFixtures.exchangeFrom row "append" genesis
            PortableSupplierAdmission.issueChallenge appendPair.Challenge genesis |> SupplierCheckpointFixtures.ok |> stage |> ignore
            let appended = SupplierCheckpointFixtures.acceptAppendFrom row genesis |> stage
            let issued = SupplierCheckpointFixtures.issueNativeFrom row appended
            let configuration = issued.CheckpointCustody.Value.NativeConfiguration
            let scope = PortableSupplierOriginalNativeConfiguration.scopeBytes configuration
            let clock = PortableSupplierOriginalNativeConfiguration.clockBytes configuration
            let destination = PortableSupplierOriginalNativeConfiguration.destinationBytes configuration |> Option.map (fun bytes -> bytes, SupplierCheckpointFixtures.hash bytes)
            let replacement = PortableSupplierOriginalNativeConfiguration.create issued.Original 2UL
                                    scope (SupplierCheckpointFixtures.hash scope) clock (SupplierCheckpointFixtures.hash clock) destination
                              |> SupplierCheckpointFixtures.ok
            let rebound = { issued with CheckpointCustody = Some { issued.CheckpointCustody.Value with NativeConfiguration = replacement } }
            // Self-consistent source data still cannot replace the independently retained original selection.
            Assert.True(PortableSupplierAdmission.validate rebound |> Result.isOk)
            Assert.Equal(SupplierSourceConflict,journal.StageV4(rebound,Some appended.Revision,Some appended.CheckpointCustody.Value.StoreFence))
            let unchanged,_ = journal.Read appended.Original.Command |> SupplierCheckpointFixtures.ok
            Assert.Equal(appended,unchanged)
            let pending = issued |> stage
            let custody = pending.CheckpointCustody.Value
            let anchor = custody.PendingNativeAnchor.Value
            for changed in
                [ { anchor with ChallengeKind = SupplierWireV1 }
                  { anchor with ChallengeId = Guid.NewGuid() }
                  { anchor with ChallengeDigest = String.replicate 64 "f" }
                  { anchor with Head = genesis.Head.Value }
                  { anchor with Sequence = 0UL } ] do
                Assert.True(PortableSupplierAdmission.validate { pending with CheckpointCustody = Some { custody with PendingNativeAnchor = Some changed } } |> Result.isError)
            Assert.True(PortableSupplierAdmission.validate { pending with CheckpointCustody = Some { custody with PendingNativeAnchor = None } } |> Result.isError)
            let exchange = SupplierCheckpointFixtures.nativeFrom row pending
            let held =
                match PortableSupplierAdmission.quarantineExchangeV4 pending.Original pending.Revision custody.StoreFence
                        "supplier-exchange-unknown" exchange.RequestBase64 [exchange.ResultBase64;exchange.ProofEnvelopeBase64]
                        None (Some exchange.ControllerBundleBase64) pending |> SupplierCheckpointFixtures.ok with
                | SupplierCheckpointHeld value -> stage value
                | _ -> failwith "uncertain native exchange must remain held"
            Assert.Equal(pending.Challenge,held.Challenge)
            Assert.Equal(custody.PendingNativeAnchor,held.CheckpointCustody.Value.PendingNativeAnchor)
            let first = held.CheckpointCustody.Value.RejectedExchanges |> List.exactlyOne
            Assert.Equal("supplier-exchange-unknown",first.Reason)
            Assert.Equal<string list>([exchange.ResultBase64;exchange.ProofEnvelopeBase64],first.FramesBase64)
            let abandoned = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) held |> SupplierCheckpointFixtures.ok |> stage
            Assert.True(abandoned.Challenge.IsNone)
            Assert.True(abandoned.CheckpointCustody.Value.PendingNativeAnchor.IsNone)
            Assert.Equal(held.FirstFailure,abandoned.FirstFailure)
            Assert.Equal<Set<string>>(held.Conflicts,abandoned.Conflicts)
            Assert.Equal<PortableSupplierRejectedExchange list>(held.CheckpointCustody.Value.RejectedExchanges,abandoned.CheckpointCustody.Value.RejectedExchanges)
            Assert.True(PortableSupplierAdmission.prepareAck abandoned |> Result.isError)
            Assert.True(PortableSupplierAdmission.reserveHost abandoned |> Result.isError)
            Assert.Equal(0,fixture.Runner.Runs)
            Assert.Equal(0,fixture.Runner.Cleans)
            Assert.Equal(0,fixture.Runner.Recoveries)

    [<Fact>]
    member _.``v4 sticky held transition persists original pending and first cause through reopen and explicit abandonment``() =
        use fixture = new HostExecutorFixture()
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        let stage state expected =
            match journal.StageV4(state,expected,expected) with SupplierSourceReadback _ -> () | other -> failwithf "%A" other
        let initial = SupplierCheckpointFixtures.initial ()
        stage initial None
        let creationPair = SupplierCheckpointFixtures.exchange "creation" initial
        let pendingCreation = PortableSupplierAdmission.issueChallenge creationPair.Challenge initial |> SupplierCheckpointFixtures.ok
        stage pendingCreation (Some 1UL)
        let creation = SupplierCheckpointFixtures.pinCreation initial
        stage creation (Some 2UL)
        let pair = SupplierCheckpointFixtures.exchange "genesis" creation
        let pending = PortableSupplierAdmission.issueChallenge pair.Challenge creation |> SupplierCheckpointFixtures.ok
        stage pending (Some 3UL)
        use doc = SupplierCheckpointFixtures.journey ()
        let original = SupplierCheckpointFixtures.observationFrom doc.RootElement SupplierGenesis pair pending
        let changed = { original with Identity = { original.Identity with ContainerId = String.replicate 64 "f" } }
        let controlled = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok changed }
        let verified = PortableSupplierAdmission.verify controlled changed.CollectedAt 2L pending (Convert.FromBase64String pair.EvidenceBase64) |> SupplierCheckpointFixtures.ok
        let held =
            match PortableSupplierAdmission.acceptWireV4 verified pair pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending |> SupplierCheckpointFixtures.ok with
            | SupplierCheckpointHeld state -> state
            | _ -> failwith "changed original must remain held"
        Assert.True(held.FirstFailure.IsSome)
        Assert.Equal(pending.Challenge,held.Challenge)
        Assert.Equal<(PortableSupplierChallengeKind * Guid * string * string) list>(pending.ChallengeLedger,held.ChallengeLedger)
        Assert.Equal(pending.Pinned,held.Pinned)
        stage held (Some 4UL)
        let cold,_ = journal.Read initial.Original.Command |> SupplierCheckpointFixtures.ok
        Assert.Equal(held,cold)
        let cleared = { held with Revision = 6UL; FirstFailure = None; Conflicts = Set.empty; CheckpointCustody = Some { held.CheckpointCustody.Value with StoreFence = 6UL } }
        Assert.Equal(SupplierSourceConflict,journal.StageV4(cleared,Some 5UL,Some 5UL))
        let abandoned = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) cold |> SupplierCheckpointFixtures.ok
        Assert.Equal(held.FirstFailure,abandoned.FirstFailure)
        Assert.Equal<Set<string>>(held.Conflicts,abandoned.Conflicts)
        Assert.True(abandoned.Challenge.IsNone)
        Assert.Contains((SupplierWireV1,pair.Challenge.ChallengeId,pair.Challenge.Digest,"abandoned"),abandoned.ChallengeLedger)
        stage abandoned (Some 5UL)
        let reopened,_ = journal.Read initial.Original.Command |> SupplierCheckpointFixtures.ok
        Assert.Equal(abandoned,reopened)
        Assert.True(PortableSupplierAdmission.prepareAck reopened |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost reopened |> Result.isError)
        Assert.Equal(0,fixture.Runner.Runs)
        Assert.Equal(0,fixture.Runner.Cleans)
        Assert.Equal(0,fixture.Runner.Recoveries)

    [<Fact>]
    member _.``zero and sixteen selected outputs retain original configuration and native join through every CAS and cold reopen``() =
        use corpus = SupplierCheckpointFixtures.journey ()
        for row in corpus.RootElement.GetProperty("outputJourneys").EnumerateArray() do
            use fixture = new HostExecutorFixture()
            let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
            let stage (state: PortableSupplierAdmissionState) =
                let previous = if state.Revision = 1UL then None else Some(state.Revision - 1UL)
                match journal.StageV4(state,previous,previous) with
                | SupplierSourceReadback actual -> Assert.Equal(state,actual)
                | other -> failwithf "%s revision %d: %A" (row.GetProperty("name").GetString()) state.Revision other
                let reopened = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
                let cold,durability = reopened.Read state.Original.Command |> SupplierCheckpointFixtures.ok
                Assert.Equal(state,cold)
                match durability with EvidenceUnknown _ -> () | _ -> failwith "source cannot prove durability"
                cold
            let initial = SupplierCheckpointFixtures.initialFrom row |> stage
            let creationPair = SupplierCheckpointFixtures.exchangeFrom row "creation" initial
            PortableSupplierAdmission.issueChallenge creationPair.Challenge initial |> SupplierCheckpointFixtures.ok |> stage |> ignore
            let creation = SupplierCheckpointFixtures.pinCreationFrom row initial |> stage
            let genesisPair = SupplierCheckpointFixtures.exchangeFrom row "genesis" creation
            PortableSupplierAdmission.issueChallenge genesisPair.Challenge creation |> SupplierCheckpointFixtures.ok |> stage |> ignore
            let genesis = SupplierCheckpointFixtures.acceptGenesisFrom row creation |> stage
            Assert.True(PortableSupplierAdmission.issueZeroNativeProofChallengeV4 genesis.Original genesis.Revision genesis.CheckpointCustody.Value.StoreFence
                            (Guid.Parse "88888888-8888-8888-8888-888888888888") genesis.Original.Command.Deadline genesis.Original.Command.Deadline 1L 100L genesis |> Result.isError)
            let appendPair = SupplierCheckpointFixtures.exchangeFrom row "append" genesis
            PortableSupplierAdmission.issueChallenge appendPair.Challenge genesis |> SupplierCheckpointFixtures.ok |> stage |> ignore
            let appended = SupplierCheckpointFixtures.acceptAppendFrom row genesis |> stage
            let pending = SupplierCheckpointFixtures.issueNativeFrom row appended |> stage
            Assert.Equal(8UL,pending.Revision)
            Assert.Equal(appended.Head,Some pending.CheckpointCustody.Value.PendingNativeAnchor.Value.Head)
            Assert.Equal(1UL,pending.CheckpointCustody.Value.PendingNativeAnchor.Value.Sequence)
            let completed = SupplierCheckpointFixtures.acceptNativeFrom row pending |> stage
            Assert.Equal(9UL,completed.Revision)
            Assert.Equal(initial.CheckpointCustody.Value.NativeConfiguration,completed.CheckpointCustody.Value.NativeConfiguration)
            Assert.Equal(creation.Pinned,completed.Pinned)
            Assert.True(completed.Challenge.IsNone)
            Assert.True(completed.CheckpointCustody.Value.PendingNativeAnchor.IsNone)
            Assert.Single(completed.CheckpointCustody.Value.NativeAcquisitions) |> ignore
            let bytes = Convert.FromBase64String completed.CheckpointCustody.Value.OutputCheckpointBase64.Value
            let output = PortableSupplierCheckpoint.parseOutput bytes |> SupplierCheckpointFixtures.ok
            let common = PortableSupplierCheckpoint.outputCommon output
            let proof = PortableSupplierCheckpoint.output output
            Assert.Equal(completed.Head.Value,common.ObservedHead)
            Assert.Equal(9UL,common.JournalRevision)
            Assert.Equal(8UL,common.ExpectedJournalRevision)
            Assert.Equal(completed.Original.ReceiverEpoch,proof.NativeReceiverEpoch)
            Assert.Equal(SupplierCheckpointFixtures.hash (row.GetProperty("native").GetProperty("receipt").GetString() |> SupplierCheckpointFixtures.urlBytes),proof.NativeReceiptSha256)
            match row.GetProperty("name").GetString(),proof.Selection with
            | "zero",CheckpointNone -> ()
            | "selected-sixteen",CheckpointSelected(_,selection) -> Assert.Equal(16UL,selection.MemberCount)
            | _ -> failwith "exact selection checkpoint refused"
            Assert.True(PortableSupplierAdmission.prepareAck completed |> Result.isError)
            Assert.True(PortableSupplierAdmission.reserveHost completed |> Result.isError)
            Assert.Equal(0,fixture.Runner.Runs)
            Assert.Equal(0,fixture.Runner.Cleans)
            Assert.Equal(0,fixture.Runner.Recoveries)

/// Explicit public fixture data only. This adapter never signs or constructs production authority.
module private ControlledGenesisSourceExport =
    let private variables =
        [ "FSGG_V3_GENESIS_EXPORT_INPUT"; "FSGG_V3_GENESIS_EXPORT_SHA256"
          "FSGG_V3_GENESIS_EXPORT_OUTPUT"; "FSGG_V3_GENESIS_EXPORT_DRAFTED_AT" ]
    let requested () = variables |> List.exists (fun name -> not (isNull (Environment.GetEnvironmentVariable name)))
    let private refuse code = invalidOp ("genesis-source-export-" + code)
    let private ok (value: Result<'a,string>) : 'a =
        match value with Ok result -> result | Error reason -> refuse (reason |> Seq.truncate 160 |> Seq.toArray |> String)
    let private text (row: System.Text.Json.JsonElement) (name: string) =
        let selected = match row.TryGetProperty name with true,value -> value | _ -> refuse ("missing-" + name)
        let value = selected.GetString()
        if String.IsNullOrEmpty value then refuse "missing-text"
        value
    let private date (value: string) =
        let format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"
        let parsed = DateTimeOffset.ParseExact(value,format,Globalization.CultureInfo.InvariantCulture,
                                              Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal)
        if parsed.ToString(format,Globalization.CultureInfo.InvariantCulture) <> value then refuse "timestamp"
        parsed
    let private unsigned (row: System.Text.Json.JsonElement) (name: string) =
        let value = text row name
        let parsed = UInt64.Parse(value,Globalization.NumberStyles.None,Globalization.CultureInfo.InvariantCulture)
        if parsed.ToString(Globalization.CultureInfo.InvariantCulture) <> value then refuse "unsigned"
        parsed
    let private signed (row: System.Text.Json.JsonElement) (name: string) =
        let value = text row name
        let parsed = Int64.Parse(value,Globalization.NumberStyles.None,Globalization.CultureInfo.InvariantCulture)
        if parsed.ToString(Globalization.CultureInfo.InvariantCulture) <> value then refuse "signed"
        parsed
    let private optionText (row: System.Text.Json.JsonElement) (name: string) =
        if row.GetProperty(name).ValueKind = System.Text.Json.JsonValueKind.Null then None else Some(text row name)
    let private absolute (value: string) =
        if not (Path.IsPathFullyQualified value) || Path.GetFullPath value <> value then refuse "absolute-path"
        value
    let private roles =
        [ "original-intent",OriginalIntentSignature; "create-effect-grant",EffectGrantSignature
          "receiver-command",ReceiverCommandSignature; "host-command-result",HostCommandResultSignature
          "native-proof",NativeProofSignature; "creation",SupplierCreationSignature; "genesis",SupplierGenesisSignature
          "supplier-event",SupplierEventSignature; "supplier-response",SupplierResponseSignature ]
    let private uniqueObjects (root: System.Text.Json.JsonElement) =
        let rec visit (value: System.Text.Json.JsonElement) =
            match value.ValueKind with
            | System.Text.Json.JsonValueKind.Object ->
                let mutable seen = Set.empty
                for property in value.EnumerateObject() do
                    if seen.Contains property.Name then refuse "duplicate-property"
                    seen <- seen.Add property.Name
                    visit property.Value
            | System.Text.Json.JsonValueKind.Array -> for item in value.EnumerateArray() do visit item
            | _ -> ()
        visit root
    let private oneZero (rows: System.Text.Json.JsonElement) =
        match rows.EnumerateArray() |> Seq.filter (fun row -> text row "name" = "zero") |> Seq.toList with
        | [row] -> row
        | _ -> refuse "unique-zero-input"
    let private closePreservingFirst (stream: FileStream) (operation: FileStream -> 'a) : 'a =
        let result =
            try Ok(operation stream)
            with error -> Error(Runtime.ExceptionServices.ExceptionDispatchInfo.Capture error)
        let closeFailure =
            try stream.Dispose(); None
            with error -> Some(Runtime.ExceptionServices.ExceptionDispatchInfo.Capture error)
        match result,closeFailure with
        | Error first,_ | Ok _,Some first -> first.Throw(); Unchecked.defaultof<'a>
        | Ok value,None -> value
    let private writeNew (path: string) (bytes: byte array) =
        closePreservingFirst (new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) (fun stream ->
            File.SetUnixFileMode(path,UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            stream.Write(bytes,0,bytes.Length)
            stream.Flush(true))
    let private requireExactFile (path: string) (expected: byte array) =
        closePreservingFirst (new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) (fun stream ->
            if stream.Length <> int64 expected.Length then refuse "output-length-readback"
            let actual = Array.zeroCreate<byte> expected.Length
            stream.ReadExactly(actual.AsSpan())
            if stream.ReadByte() <> -1 || actual <> expected then refuse "output-byte-readback")
    let private publishReceipt (output: string) (bytes: byte array) =
        let partial = Path.Combine(output,"receipt.partial")
        writeNew partial bytes
        requireExactFile partial bytes
        File.Move(partial,Path.Combine(output,"receipt.json"),false)

    let private commandFrom (expected: System.Text.Json.JsonElement) =
        let commandRow = expected.GetProperty("expectedCommand")
        let command: PortableWorkspaceCommand =
            { CommandId = Guid.ParseExact(text commandRow "command_id","D"); IdempotencyId = text commandRow "idempotency_id"
              WorkspaceScope = text commandRow "workspace_scope"; ProfileId = text commandRow "profile_id"
              ProfileRevision = unsigned commandRow "profile_revision"; SourceRevision = text commandRow "source_revision"
              ExpectedWorkflowRevision = unsigned commandRow "expected_workflow_revision"; FenceGeneration = unsigned commandRow "fence_generation"
              CausationId = optionText commandRow "causation_id"
              Deadline = date (text commandRow "deadline"); Operation = text commandRow "operation"
              ComponentId = optionText commandRow "component_id" }
        command

    let runExplicit (input: string) (hash: string) (output: string) (draftedAt: string) =
        let mutable stage = "configuration"
        let mutable ownedOutput: string option = None
        let mutable expectedHash = "unselected"
        try
            let values = [input;hash;output;draftedAt] |> List.map (fun value ->
                if String.IsNullOrEmpty value then refuse "all-export-inputs-required"
                value)
            let input,hash,output,draftedAt =
                match values with
                | [input;hash;output;draftedAt] -> absolute input,hash,absolute output,date draftedAt
                | _ -> refuse "export-configuration"
            if hash.Length <> 64 || (hash |> Seq.exists (fun c -> not ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))) then refuse "sha256"
            expectedHash <- hash
            if OperatingSystem.IsWindows() then refuse "linux-private-output-required"
            if File.Exists output || Directory.Exists output then refuse "output-already-exists"
            // CreateNew also prevents two adapters from claiming the same requested output.
            let claim = new FileStream(output + ".export-owner",FileMode.CreateNew,FileAccess.Write,FileShare.None)
            let receiptBytes = closePreservingFirst claim (fun _ ->
                File.SetUnixFileMode(output + ".export-owner",UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                if File.Exists output || Directory.Exists output then refuse "output-race"
                Directory.CreateDirectory(output,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
                ownedOutput <- Some output
                stage <- "freeze-input"
                let frozen =
                    closePreservingFirst (new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read)) (fun stream ->
                        if stream.Length <= 0L || stream.Length > int64 PortableSupplierAdmission.MaximumRecordBytes then refuse "input-byte-bound"
                        let bytes = Array.zeroCreate<byte> (int stream.Length)
                        stream.ReadExactly(bytes.AsSpan())
                        if stream.ReadByte() <> -1 || SupplierCheckpointFixtures.hash bytes <> hash then refuse "input-sha256"
                        bytes)
                stage <- "explicit-selection"
                let options = System.Text.Json.JsonDocumentOptions(MaxDepth = 32)
                use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(frozen),options)
                let root = document.RootElement
                uniqueObjects root
                if text root "schema" <> "fsgg.receiver-checkpoint-signed-source-journey-inputs/1"
                   || root.GetProperty("productionEnrollment").GetBoolean() || root.GetProperty("nativeAcceptance").GetBoolean() then refuse "source-input-kind"
                let row = oneZero (root.GetProperty("outputJourneys"))
                let expected = oneZero (root.GetProperty("controlledSelections"))
                let expectedNames = roles |> List.map fst |> Set.ofList
                for name in ["publicRolePins"] do
                    if (root.GetProperty(name).EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) <> expectedNames then refuse "nine-role-pins"
                if (expected.GetProperty("policyRoles").EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) <> expectedNames then refuse "nine-role-policies"
                let command = commandFrom expected
                let exact (key: string) = text row key |> SupplierCheckpointFixtures.urlBytes
                let original: PortableSupplierAdmissionOriginal =
                    { Command = command; QualifiedImage = text expected "qualifiedImage"
                      IntentBase64 = Convert.ToBase64String(exact "originalIntent"); IntentDigest = text expected "originalIntentDigest"
                      TrustPolicyDigest = text expected "signaturePolicyDigest"; SupplierSourceRevision = text expected "supplierSourceRevision"
                      ReceiverEpoch = Guid.ParseExact(text expected "initialReceiverEpoch","D") }
                let config = expected.GetProperty("nativeConfiguration")
                let configBytes (name: string) = text config name |> SupplierCheckpointFixtures.urlBytes
                let destination =
                    if config.GetProperty("destination").ValueKind = System.Text.Json.JsonValueKind.Null then
                        if config.GetProperty("destinationSha256").ValueKind <> System.Text.Json.JsonValueKind.Null then refuse "destination-pair"
                        None
                    else Some(configBytes "destination",text config "destinationSha256")
                let configuration = PortableSupplierOriginalNativeConfiguration.create original (unsigned expected "nativeConfigurationRevision")
                                        (configBytes "scope") (text config "scopeSha256") (configBytes "clock") (text config "clockSha256") destination |> ok
                let bindingRow = expected.GetProperty("hostBinding")
                let binding: PortableHostBinding =
                    { Command = command; QualifiedImage = original.QualifiedImage; AllocationId = text bindingRow "allocationId"
                      WorkerId = text bindingRow "workerId"; AllocationReceiptDigest = text bindingRow "allocationReceiptDigest" }
                let selection (at: string) =
                    let at = date at
                    let policies: PortableSupplierControlledRolePolicy list =
                        roles |> List.map (fun (name,role) ->
                            let policy = expected.GetProperty("policyRoles").GetProperty(name)
                            if text policy "role" <> name || text policy "revocation" <> "not-revoked" then refuse "role-selection"
                            { Role = role; PublicKeyBlob = root.GetProperty("publicRolePins").GetProperty(name).GetProperty("publicKeyBlobBase64").GetString() |> Convert.FromBase64String
                              Principal = text policy "principal"; PolicyBytes = text policy "policyBytesBase64" |> Convert.FromBase64String
                              ExpectedPolicySha256 = text policy "policySha256"; PolicyRevision = text policy "policyRevision"
                              ValidFrom = date (text policy "policyValidFrom"); ValidUntil = date (text policy "policyValidUntil")
                              RevocationCheckedAt = at; Revocation = ControlledNotRevoked })
                    let domain = Guid.ParseExact(text expected "receiverClockDomain","D")
                    let current: PortableSupplierControlledCurrentObservation =
                        { VerifiedAt = at; SelectionObservedAt = at; SelectionValidUntil = date (text expected "selectionValidUntil")
                          MaximumObservationAge = TimeSpan.FromTicks(signed expected "maximumObservationAgeTicks")
                          ReceiverMonotonic = signed expected "receiverMonotonic"; IssuedReceiverClockDomain = domain; CurrentReceiverClockDomain = domain
                          HostClock = PortableSupplierOriginalNativeConfiguration.clock configuration }
                    PortableSupplierAdmission.createControlledSourceSelection original (text expected "originalIntentEnvelopeSha256")
                        (Guid.ParseExact(text expected "expectedOperationId","D")) binding configuration policies current |> ok
                stage <- "begin-original"
                let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(Path.Combine(output,"source-journal"))
                let mutable state = PortableSupplierAdmission.beginIntentV4 original (exact "originalIntentEnvelope") configuration |> ok
                let requireStage = function SupplierSourceReadback _ -> () | _ -> refuse "stage-not-source-readback"
                journal.StageV4(state,None,None) |> requireStage
                for name in ["creation";"genesis"] do
                    stage <- name + "-challenge"
                    let fields = row.GetProperty(name)
                    let bytes (key: string) = text fields key |> SupplierCheckpointFixtures.urlBytes
                    let bounds = expected.GetProperty("exchangeReceiverMonotonicBounds").GetProperty(name)
                    let challenge = SupplierCheckpointFixtures.challengeFrom row name state
                                        (signed bounds "issuedMonotonic") (signed bounds "deadlineMonotonic")
                    let pending = PortableSupplierAdmission.issueChallenge challenge state |> ok
                    journal.StageV4(pending,Some state.Revision,Some state.CheckpointCustody.Value.StoreFence) |> requireStage
                    let exchange = if name = "creation" then ControlledCreationExchange(bytes "request",[bytes "result";bytes "evidence"],exact "reservation")
                                   else ControlledGenesisExchange(bytes "request",[bytes "result";bytes "evidence"])
                    stage <- name + "-real-verification"
                    let current = selection (text (expected.GetProperty("warmVerificationTimes")) name)
                    let outcome = PortableSupplierAdmission.verifyAndAcceptControlledV4 current pending.Original pending.Revision
                                        pending.CheckpointCustody.Value.StoreFence pending exchange |> ok
                    let transition = match outcome with AuthenticatedControlledSource value -> value | HeldControlledSource _ -> refuse "held-source"
                    journal.StageControlledV4 transition |> requireStage
                    state <- transition.State
                stage <- "locked-cold-read"
                let coldAt = date (text expected "coldVerificationTime")
                let readback,durability = journal.ReadControlledV4(selection (text expected "coldVerificationTime"),state.Original,state.Revision,state.CheckpointCustody.Value.StoreFence) |> ok
                match durability with EvidenceUnknown _ -> () | _ -> refuse "unexpected-durability"
                let retained = readback.State
                let reservation = retained.Pinned.Value.OriginalReservation
                match reservation.Selection with NoSelectedOutputs -> () | _ -> refuse "zero-selection-required"
                if draftedAt < coldAt || draftedAt < reservation.AdmittedAt || draftedAt >= reservation.HostLeaseExpiresAt
                   || draftedAt >= original.Command.Deadline || draftedAt >= date (text expected "selectionValidUntil") then refuse "draft-original-window"
                let checkpointBytes = retained.CheckpointCustody.Value.GenesisCheckpointBase64.Value |> Convert.FromBase64String
                let checkpoint = PortableSupplierCheckpoint.parseGenesis checkpointBytes |> ok
                if PortableSupplierCheckpoint.serializeGenesis checkpoint <> checkpointBytes
                   || (PortableSupplierCheckpoint.genesisCommon checkpoint).Provenance <> "controlled-test" then refuse "checkpoint-byte-join"
                let drafts = PortableSupplierAdmission.draftControlledAckPayloadsV4 readback draftedAt |> ok
                let ackBytes = drafts.GenesisPayload
                stage <- "write-exact-outputs"
                let checkpointPath = Path.Combine(output,"genesis-checkpoint.c1")
                let ackPath = Path.Combine(output,"genesis-ack-payload.c1")
                writeNew checkpointPath checkpointBytes
                writeNew ackPath ackBytes
                requireExactFile checkpointPath checkpointBytes
                requireExactFile ackPath ackBytes
                let common = PortableSupplierCheckpoint.genesisCommon checkpoint
                let receipt =
                    {| outcome = "success"; sourceOnly = true; durabilityUnknown = true; noProduction = true; provenance = "controlled-test"
                       inputSha256 = hash; checkpointBytes = checkpointBytes.Length; checkpointSha256 = SupplierCheckpointFixtures.hash checkpointBytes
                       ackPayloadBytes = ackBytes.Length; ackPayloadSha256 = SupplierCheckpointFixtures.hash ackBytes
                       journalKeySha256 = common.JournalKeySha256; originalIntentDigest = original.IntentDigest
                       originalReceiverEpoch = original.ReceiverEpoch.ToString("D"); journalRevision = retained.Revision
                       storeFence = retained.CheckpointCustody.Value.StoreFence; draftedAt = draftedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",Globalization.CultureInfo.InvariantCulture) |}
                stage <- "publish-success-receipt"
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes receipt)
            publishReceipt output receiptBytes
        with error ->
            let cause =
                if error.Message.StartsWith("genesis-source-export-",StringComparison.Ordinal) then
                    error.Message.Substring(0,min 192 error.Message.Length)
                else error.GetType().Name
            let failure =
                {| outcome = "failure"; sourceOnly = true; durabilityUnknown = true; noProduction = true
                   inputSha256 = expectedHash; firstCauseStage = stage; firstCauseType = error.GetType().Name; firstCauseCode = cause |}
            let bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes failure
            match ownedOutput with
            | Some output ->
                try writeNew (Path.Combine(output,"failure-receipt.json")) bytes with _ -> ()
            | None -> ()
            try Console.Error.WriteLine(Encoding.UTF8.GetString bytes) with _ -> ()
            reraise ()

    let run () =
        // Fixed external ABI only; normal regression calls the same explicit core without
        // mutating process-wide environment or replacing any verifier/store dependency.
        match variables |> List.map Environment.GetEnvironmentVariable with
        | [input;hash;output;draftedAt] -> runExplicit input hash output draftedAt
        | _ -> refuse "export-configuration"

    let assertPublicRegression (original: byte array) =
        Assert.Equal("f647882aa4b1949f03df81255812e731dfa71077a717760f8df4e49bfe3ecd9d",SupplierCheckpointFixtures.hash original)
        let owned = Path.Combine(Path.GetTempPath(),"fsgg-explicit-source-export-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(owned,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
        let mutable completed = false
        try
            use originalDocument = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(original))
            let expected = oneZero (originalDocument.RootElement.GetProperty("controlledSelections"))
            let command = commandFrom expected
            let draftedAt = "2026-01-01T00:00:09.000000Z"
            let inputCopy (variant: string) =
                let node = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString original)
                let selected =
                    node["controlledSelections"].AsArray()
                    |> Seq.filter (fun item -> item["name"].GetValue<string>() = "zero")
                    |> Seq.exactlyOne |> fun item -> item.AsObject()
                selected["exchangeReceiverMonotonicBounds"] <-
                    System.Text.Json.Nodes.JsonNode.Parse("""{"creation":{"issuedMonotonic":"0","deadlineMonotonic":"3"},"genesis":{"issuedMonotonic":"1","deadlineMonotonic":"4"}}""")
                let bounds = selected["exchangeReceiverMonotonicBounds"].AsObject()
                let creationBounds = bounds["creation"].AsObject()
                let genesisBounds = bounds["genesis"].AsObject()
                match variant with
                | "valid" -> ()
                | "missing-bound" -> creationBounds.Remove("issuedMonotonic") |> ignore
                | "noncanonical-bound" -> genesisBounds["deadlineMonotonic"] <- System.Text.Json.Nodes.JsonValue.Create<string>("04")
                | "expired-monotonic" -> creationBounds["deadlineMonotonic"] <- System.Text.Json.Nodes.JsonValue.Create<string>("2")
                | "foreign-operation" -> selected["expectedOperationId"] <- System.Text.Json.Nodes.JsonValue.Create<string>("99999999-9999-4999-8999-999999999999")
                | _ -> failwith "unknown public export input variant"
                // Only unsigned independent selection metadata changes. Every signed original,
                // public key/policy preimage and recorded signature stays byte-identical.
                let baseline = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString original)
                for field in ["outputJourneys";"publicRolePins";"signatures"] do
                    Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(baseline[field],node[field]))
                let bytes = Encoding.UTF8.GetBytes(node.ToJsonString())
                let path = Path.Combine(owned,variant + ".json")
                File.WriteAllBytes(path,bytes)
                path,SupplierCheckpointFixtures.hash bytes
            let input,inputHash = inputCopy "valid"
            let output = Path.Combine(owned,"success")
            runExplicit input inputHash output draftedAt
            let checkpointBytes = File.ReadAllBytes(Path.Combine(output,"genesis-checkpoint.c1"))
            let ackBytes = File.ReadAllBytes(Path.Combine(output,"genesis-ack-payload.c1"))
            use receipt = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"receipt.json")))
            let row = receipt.RootElement
            Assert.Equal("success",text row "outcome")
            for flag in ["sourceOnly";"durabilityUnknown";"noProduction"] do Assert.True(row.GetProperty(flag).GetBoolean())
            Assert.Equal("controlled-test",text row "provenance")
            Assert.Equal(inputHash,text row "inputSha256")
            Assert.Equal(draftedAt,text row "draftedAt")
            Assert.Equal(checkpointBytes.Length,row.GetProperty("checkpointBytes").GetInt32())
            Assert.Equal(ackBytes.Length,row.GetProperty("ackPayloadBytes").GetInt32())
            Assert.Equal(SupplierCheckpointFixtures.hash checkpointBytes,text row "checkpointSha256")
            Assert.Equal(SupplierCheckpointFixtures.hash ackBytes,text row "ackPayloadSha256")
            let checkpoint = PortableSupplierCheckpoint.parseGenesis checkpointBytes |> ok
            Assert.True((checkpointBytes = PortableSupplierCheckpoint.serializeGenesis checkpoint))
            let common = PortableSupplierCheckpoint.genesisCommon checkpoint
            Assert.Equal("controlled-test",common.Provenance)
            Assert.Equal(text expected "originalIntentDigest",common.IntentDigest)
            Assert.Equal(Guid.ParseExact(text expected "expectedOperationId","D"),common.OperationId)
            Assert.Equal(text row "journalKeySha256",common.JournalKeySha256)
            Assert.Equal(text expected "initialReceiverEpoch",text row "originalReceiverEpoch")
            Assert.Equal(common.JournalRevision,row.GetProperty("journalRevision").GetUInt64())
            Assert.Equal(common.StoreFence,row.GetProperty("storeFence").GetUInt64())
            use ack = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(ackBytes))
            let payload = ack.RootElement
            Assert.Equal<Set<string>>(Set.ofList ["schema";"kind";"accepted_at";"correlation_id";"genesis_head";"intent_digest";"operation_id";"receiver_journal_digest";"reservation_digest"],
                                      payload.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            Assert.Equal("fsgg.v3.receiver-acceptance/1",text payload "schema")
            Assert.Equal("original-genesis-accepted",text payload "kind")
            Assert.Equal(draftedAt,text payload "accepted_at")
            Assert.Equal(common.ObservedHead,text payload "genesis_head")
            Assert.Equal(common.IntentDigest,text payload "intent_digest")
            Assert.Equal(common.OperationId.ToString("D"),text payload "operation_id")
            Assert.Equal(common.ReservationDigest,text payload "reservation_digest")
            Assert.Equal(SupplierCheckpointFixtures.hash checkpointBytes,text payload "receiver_journal_digest")
            let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal(Path.Combine(output,"source-journal"))
            let retained,durability = journal.Read command |> ok
            match durability with EvidenceUnknown _ -> () | _ -> failwith "public export cannot qualify durability"
            Assert.True((checkpointBytes = Convert.FromBase64String retained.CheckpointCustody.Value.GenesisCheckpointBase64.Value))
            Assert.True(retained.CheckpointCustody.Value.OutputCheckpointBase64.IsNone)
            Assert.Equal(retained.Revision,row.GetProperty("journalRevision").GetUInt64())
            Assert.Equal(retained.CheckpointCustody.Value.StoreFence,row.GetProperty("storeFence").GetUInt64())
            Assert.True(PortableSupplierAdmission.prepareAck retained |> Result.isError)
            Assert.True(PortableSupplierAdmission.reserveHost retained |> Result.isError)
            Assert.Equal<Set<string>>(Set.ofList ["genesis-checkpoint.c1";"genesis-ack-payload.c1"],
                                      Directory.GetFiles(output,"*.c1") |> Seq.map (fun (path: string) -> Path.GetFileName path) |> Set.ofSeq)
            Assert.False(File.Exists(Path.Combine(output,"failure-receipt.json")))
            Assert.False(File.Exists(Path.Combine(output,"receipt.partial")))
            let refuseCase (name: string) (caseInput: string) (caseHash: string) (at: string)
                           (stage: string) (errorType: string) (cause: string) =
                let target = Path.Combine(owned,name)
                let error = Assert.ThrowsAny<Exception>(Action(fun () -> runExplicit caseInput caseHash target at))
                Assert.Equal(errorType,error.GetType().Name)
                let code = if error.Message.StartsWith("genesis-source-export-",StringComparison.Ordinal) then error.Message else error.GetType().Name
                Assert.Equal(cause,code)
                Assert.False(File.Exists(Path.Combine(target,"receipt.json")))
                Assert.False(File.Exists(Path.Combine(target,"genesis-checkpoint.c1")))
                Assert.False(File.Exists(Path.Combine(target,"genesis-ack-payload.c1")))
                if stage = "configuration" then Assert.False(Directory.Exists target)
                else
                    use failure = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(target,"failure-receipt.json")))
                    let failed = failure.RootElement
                    Assert.Equal("failure",text failed "outcome")
                    Assert.Equal(stage,text failed "firstCauseStage")
                    Assert.Equal(errorType,text failed "firstCauseType")
                    Assert.Equal(cause,text failed "firstCauseCode")
                    Assert.Equal(caseHash,text failed "inputSha256")
                    for flag in ["sourceOnly";"durabilityUnknown";"noProduction"] do Assert.True(failed.GetProperty(flag).GetBoolean())
                    if stage = "freeze-input" then Assert.False(Directory.Exists(Path.Combine(target,"source-journal")))
            // Nine refused invocations, including a repeated successful destination below.
            refuseCase "missing-setting" null inputHash draftedAt "configuration" "InvalidOperationException" "genesis-source-export-all-export-inputs-required"
            refuseCase "wrong-hash" input (String.replicate 64 "0") draftedAt "freeze-input" "InvalidOperationException" "genesis-source-export-input-sha256"
            let oversized = Path.Combine(owned,"oversized.json")
            File.WriteAllBytes(oversized,Array.zeroCreate<byte> (PortableSupplierAdmission.MaximumRecordBytes + 1))
            refuseCase "oversized" oversized inputHash draftedAt "freeze-input" "InvalidOperationException" "genesis-source-export-input-byte-bound"
            for variant,stage,errorType,cause in
                [ "missing-bound","creation-challenge","InvalidOperationException","genesis-source-export-missing-issuedMonotonic"
                  "noncanonical-bound","genesis-challenge","InvalidOperationException","genesis-source-export-signed"
                  "expired-monotonic","creation-real-verification","InvalidOperationException","genesis-source-export-held-source"
                  "foreign-operation","creation-real-verification","InvalidOperationException","genesis-source-export-held-source" ] do
                let changed,changedHash = inputCopy variant
                refuseCase ("refused-" + variant) changed changedHash draftedAt stage errorType cause
            refuseCase "draft-deadline" input inputHash (text (expected.GetProperty("expectedCommand")) "deadline")
                "locked-cold-read" "InvalidOperationException" "genesis-source-export-draft-original-window"
            // An already published receipt is preserved, not counted as new success.
            let before = Directory.GetFiles(output,"*",SearchOption.AllDirectories) |> Array.map (fun path -> path,File.ReadAllBytes path)
            let repeated = Assert.Throws<InvalidOperationException>(Action(fun () -> runExplicit input inputHash output draftedAt))
            Assert.Equal("genesis-source-export-output-already-exists",repeated.Message)
            for path,bytes in before do Assert.True((bytes = File.ReadAllBytes path))
            Assert.False(File.Exists(Path.Combine(output,"failure-receipt.json")))
            Assert.Equal("f647882aa4b1949f03df81255812e731dfa71077a717760f8df4e49bfe3ecd9d",SupplierCheckpointFixtures.hash original)
            completed <- true
        finally
            // Preserve failed public-control files and its first exception; cleanup follows
            // only complete assertions, and a cleanup failure then fails the test honestly.
            if completed then Directory.Delete(owned,true)

/// Real OpenSSH fixture signatures feed the maintained paired factory and existing owned store.
/// Every selected policy/clock/root remains explicitly controlled TEST data.
type PortableHostExecutorTests_ControlledAuthenticatedSource() =
    [<Fact>]
    member this.``real signed zero and selected output journeys use controlled independent selection and cold reverify without an arbitrary authenticator``() =
        if ControlledGenesisSourceExport.requested () then ControlledGenesisSourceExport.run ()
        else this.RunMaintainedJourneys()

    member private _.RunMaintainedJourneys() =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "joined signed source fixture inputs missing"
            let path = Path.Combine(directory.FullName,"contracts","portable-workspace","v1","receiver-checkpoint-v2-signed-source-journeys.json")
            if File.Exists path then File.ReadAllBytes path else find directory.Parent
        let publicBytes = find (DirectoryInfo(AppContext.BaseDirectory))
        use corpus = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(publicBytes))
        let root = corpus.RootElement
        Assert.False(root.GetProperty("productionEnrollment").GetBoolean())
        Assert.False(root.GetProperty("nativeAcceptance").GetBoolean())
        Assert.Equal(2,root.GetProperty("outputJourneys").GetArrayLength())
        Assert.Equal(2,root.GetProperty("controlledSelections").GetArrayLength())
        Assert.Equal(44,root.GetProperty("signatures").GetArrayLength())
        let names = root.GetProperty("outputJourneys").EnumerateArray() |> Seq.map (fun row -> row.GetProperty("name").GetString()) |> Set.ofSeq
        Assert.Equal<Set<string>>(Set.ofList ["zero";"selected-sixteen"],names)
        let roles =
            ["original-intent",OriginalIntentSignature;"create-effect-grant",EffectGrantSignature;"receiver-command",ReceiverCommandSignature
             "host-command-result",HostCommandResultSignature;"native-proof",NativeProofSignature;"creation",SupplierCreationSignature
             "genesis",SupplierGenesisSignature;"supplier-event",SupplierEventSignature;"supplier-response",SupplierResponseSignature]
        let date (value: string) = DateTimeOffset.Parse(value,Globalization.CultureInfo.InvariantCulture)
        for row in root.GetProperty("outputJourneys").EnumerateArray() do
            use fixture = new HostExecutorFixture()
            let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
            let initial = SupplierCheckpointFixtures.initialFrom row
            let expected = root.GetProperty("controlledSelections").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("name").GetString() = row.GetProperty("name").GetString())
            Assert.Equal(expected.GetProperty("originalIntentDigest").GetString(),initial.Original.IntentDigest)
            Assert.Equal(expected.GetProperty("signaturePolicyDigest").GetString(),initial.Original.TrustPolicyDigest)
            let binding =
                { Command = initial.Original.Command; QualifiedImage = initial.Original.QualifiedImage
                  AllocationId = expected.GetProperty("hostBinding").GetProperty("allocationId").GetString()
                  WorkerId = expected.GetProperty("hostBinding").GetProperty("workerId").GetString()
                  AllocationReceiptDigest = expected.GetProperty("hostBinding").GetProperty("allocationReceiptDigest").GetString() }
            let selectionWith changePolicies changeCurrent at revoke =
                let at = date at
                let policies =
                    roles |> List.map (fun (name,role) ->
                        let selected = expected.GetProperty("policyRoles").GetProperty(name)
                        let pin = root.GetProperty("publicRolePins").GetProperty(name).GetProperty("publicKeyBlobBase64").GetString() |> Convert.FromBase64String
                        { Role = role; PublicKeyBlob = pin; Principal = selected.GetProperty("principal").GetString()
                          PolicyBytes = selected.GetProperty("policyBytesBase64").GetString() |> Convert.FromBase64String
                          ExpectedPolicySha256 = selected.GetProperty("policySha256").GetString(); PolicyRevision = selected.GetProperty("policyRevision").GetString()
                          ValidFrom = date (selected.GetProperty("policyValidFrom").GetString()); ValidUntil = date (selected.GetProperty("policyValidUntil").GetString())
                          RevocationCheckedAt = at; Revocation = if revoke && role = SupplierCreationSignature then ControlledRevoked else ControlledNotRevoked })
                let domain = Guid.Parse(expected.GetProperty("receiverClockDomain").GetString())
                let current =
                    { VerifiedAt = at; SelectionObservedAt = at; SelectionValidUntil = date (expected.GetProperty("selectionValidUntil").GetString())
                      MaximumObservationAge = TimeSpan.FromTicks(Int64.Parse(expected.GetProperty("maximumObservationAgeTicks").GetString()))
                      ReceiverMonotonic = 2L; IssuedReceiverClockDomain = domain; CurrentReceiverClockDomain = domain
                      HostClock = PortableSupplierOriginalNativeConfiguration.clock initial.CheckpointCustody.Value.NativeConfiguration }
                let captured =
                    PortableSupplierAdmission.createControlledSourceSelection initial.Original (expected.GetProperty("originalIntentEnvelopeSha256").GetString())
                        (Guid.Parse(expected.GetProperty("expectedOperationId").GetString())) binding initial.CheckpointCustody.Value.NativeConfiguration (changePolicies policies) (changeCurrent current)
                    |> SupplierCheckpointFixtures.ok
                // Every successful journey also proves mutable caller arrays were copied at selection capture.
                for policy in policies do
                    Array.fill policy.PublicKeyBlob 0 policy.PublicKeyBlob.Length 0uy
                    Array.fill policy.PolicyBytes 0 policy.PolicyBytes.Length 0uy
                captured
            let selection at revoke = selectionWith id id at revoke
            let exerciseStoreCuts (current: PortableSupplierControlledSourceSelection) (transition: PortableSupplierAuthenticatedSourceTransition) =
                let pending,_ = journal.Read transition.Original.Command |> SupplierCheckpointFixtures.ok
                // The failed Stage cannot manufacture readback. A later explicit fresh read may
                // authenticate persisted source bytes, independently of commit/durability evidence.
                for point in [PortableWorkspaceExecutor.BeforeSourceWrite; PortableWorkspaceExecutor.BeforeSourceFileFlush
                              PortableWorkspaceExecutor.BeforeSourceReplacement; PortableWorkspaceExecutor.AfterSourceReplacementBeforeReadback
                              PortableWorkspaceExecutor.BeforeSourceColdReopen; PortableWorkspaceExecutor.BeforeSourceDirectorySync
                              PortableWorkspaceExecutor.AfterSourceDirectorySyncBeforeReadback; PortableWorkspaceExecutor.SimulatedSourceDirectorySyncError] do
                    use failedFixture = new HostExecutorFixture()
                    let sourceDirectory = Path.Combine(fixture.Policy.Runtime.StateRoot,"journal-v1")
                    let failedDirectory = Path.Combine(failedFixture.Policy.Runtime.StateRoot,"journal-v1")
                    Directory.CreateDirectory failedDirectory |> ignore
                    let sourcePath = Directory.GetFiles(sourceDirectory,"*.bin") |> Array.exactlyOne
                    let failedPath = Path.Combine(failedDirectory,Path.GetFileName sourcePath)
                    let before = File.ReadAllBytes sourcePath
                    File.WriteAllBytes(failedPath,before)
                    let failed = PortableWorkspaceExecutor.SupplierAdmissionJournal.CreateControlledFailureTest(failedFixture.Policy.Runtime.StateRoot,point)
                    Assert.Equal(SupplierSourceCommitUnknown,failed.StageControlledV4 transition)
                    let replaced = List.contains point [PortableWorkspaceExecutor.AfterSourceReplacementBeforeReadback; PortableWorkspaceExecutor.BeforeSourceColdReopen;
                                                       PortableWorkspaceExecutor.BeforeSourceDirectorySync; PortableWorkspaceExecutor.AfterSourceDirectorySyncBeforeReadback;
                                                       PortableWorkspaceExecutor.SimulatedSourceDirectorySyncError]
                    let fresh = PortableWorkspaceExecutor.SupplierAdmissionJournal failedFixture.Policy.Runtime.StateRoot
                    let retained,durability = fresh.Read pending.Original.Command |> SupplierCheckpointFixtures.ok
                    Assert.Equal((if replaced then transition.State else pending),retained)
                    Assert.Equal(pending.Original,retained.Original)
                    Assert.Equal(pending.FirstFailure,retained.FirstFailure)
                    match durability with EvidenceUnknown _ -> () | _ -> failwith "failure cannot qualify directory durability"
                    if replaced then
                        let authenticated,authenticatedDurability = fresh.ReadControlledV4(current,retained.Original,retained.Revision,retained.CheckpointCustody.Value.StoreFence) |> SupplierCheckpointFixtures.ok
                        Assert.Equal(retained,authenticated.State)
                        match authenticatedDurability with EvidenceUnknown _ -> () | _ -> failwith "locked authentication cannot qualify durability"
                        Assert.Equal(SupplierSourceConflict,fresh.StageControlledV4 transition)
                    else
                        Assert.True((before = File.ReadAllBytes failedPath))
                        Assert.True(fresh.ReadControlledV4(current,retained.Original,retained.Revision,retained.CheckpointCustody.Value.StoreFence) |> Result.isError)
                    Assert.True(PortableSupplierAdmission.prepareAck retained |> Result.isError)
                    Assert.True(PortableSupplierAdmission.reserveHost retained |> Result.isError)
                    Assert.Empty(Directory.GetFiles(failedDirectory,"*.tmp"))
                    Assert.Equal(0,failedFixture.Runner.Runs)
                    Assert.Equal(0,failedFixture.Runner.Cleans)
                    Assert.Equal(0,failedFixture.Runner.Recoveries)
            let assertInertAckDrafts (readback: PortableSupplierAuthenticatedSourceReadback) =
                let retained = readback.State
                let custody = retained.CheckpointCustody.Value
                let draftedAt = DateTimeOffset.Parse(expected.GetProperty("coldVerificationTime").GetString(),Globalization.CultureInfo.InvariantCulture)
                let drafts = PortableSupplierAdmission.draftControlledAckPayloadsV4 readback draftedAt |> SupplierCheckpointFixtures.ok
                let genesisBytes = custody.GenesisCheckpointBase64.Value |> Convert.FromBase64String
                let genesis = PortableSupplierCheckpoint.parseGenesis genesisBytes |> SupplierCheckpointFixtures.ok
                let common = PortableSupplierCheckpoint.genesisCommon genesis
                let payload = drafts.GenesisPayload
                use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(payload))
                let root = document.RootElement
                use intent = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(Convert.FromBase64String retained.Original.IntentBase64))
                Assert.Equal(9,root.EnumerateObject() |> Seq.length)
                Assert.Equal("fsgg.v3.receiver-acceptance/1",root.GetProperty("schema").GetString())
                Assert.Equal("original-genesis-accepted",root.GetProperty("kind").GetString())
                Assert.Equal(intent.RootElement.GetProperty("correlation_id").GetString(),root.GetProperty("correlation_id").GetString())
                Assert.Equal(common.ObservedHead,root.GetProperty("genesis_head").GetString())
                Assert.Equal(common.IntentDigest,root.GetProperty("intent_digest").GetString())
                Assert.Equal(common.OperationId.ToString("D"),root.GetProperty("operation_id").GetString())
                Assert.Equal(common.ReservationDigest,root.GetProperty("reservation_digest").GetString())
                Assert.Equal(SupplierCheckpointFixtures.hash genesisBytes,root.GetProperty("receiver_journal_digest").GetString())
                Assert.Equal(draftedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",Globalization.CultureInfo.InvariantCulture),root.GetProperty("accepted_at").GetString())
                Assert.Equal(10uy,payload[payload.Length - 1])
                payload[0] <- 0uy
                Assert.Equal(123uy,drafts.GenesisPayload[0])
                match custody.OutputCheckpointBase64,drafts.OutputPayload with
                | None,None -> ()
                | Some encoded,Some outputBytes ->
                    let checkpointBytes = Convert.FromBase64String encoded
                    let checkpoint = PortableSupplierCheckpoint.parseOutput checkpointBytes |> SupplierCheckpointFixtures.ok
                    let common = PortableSupplierCheckpoint.outputCommon checkpoint
                    let output = PortableSupplierCheckpoint.output checkpoint
                    use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(outputBytes))
                    let root = document.RootElement
                    Assert.Equal(11,root.EnumerateObject() |> Seq.length)
                    Assert.Equal("fsgg.v3.export-readback/1",root.GetProperty("schema").GetString())
                    Assert.Equal(common.ObservedHead,root.GetProperty("observed_head").GetString())
                    Assert.Equal(common.OperationId.ToString("D"),root.GetProperty("operation_id").GetString())
                    Assert.Equal(common.ReservationDigest,root.GetProperty("reservation_digest").GetString())
                    Assert.Equal(common.SelectionDigest,root.GetProperty("selection_digest").GetString())
                    Assert.Equal(SupplierCheckpointFixtures.hash checkpointBytes,root.GetProperty("receiver_journal_digest").GetString())
                    Assert.NotEqual(root.GetProperty("receiver_journal_digest").GetString(),SupplierCheckpointFixtures.hash genesisBytes)
                    Assert.Equal(draftedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",Globalization.CultureInfo.InvariantCulture),root.GetProperty("readback_at").GetString())
                    match output.Selection with
                    | CheckpointNone ->
                        Assert.Equal("zero-selection-census-accepted",root.GetProperty("kind").GetString())
                        for key in ["manifest_digest";"member_count";"total_bytes"] do Assert.Equal(System.Text.Json.JsonValueKind.Null,root.GetProperty(key).ValueKind)
                    | CheckpointSelected(_,manifest) ->
                        Assert.Equal("durable-output-readback",root.GetProperty("kind").GetString())
                        Assert.Equal(manifest.Digest,root.GetProperty("manifest_digest").GetString())
                        Assert.Equal(string manifest.MemberCount,root.GetProperty("member_count").GetString())
                        Assert.Equal(string manifest.TotalBytes,root.GetProperty("total_bytes").GetString())
                    Assert.Equal(10uy,outputBytes[outputBytes.Length - 1])
                    outputBytes[0] <- 0uy
                    Assert.Equal(123uy,drafts.OutputPayload.Value[0])
                | _ -> failwith "draft presence must match retained checkpoint custody"
                for invalid in [draftedAt.AddTicks 1L; draftedAt.ToOffset(TimeSpan.FromHours 1.)] do
                    Assert.True(PortableSupplierAdmission.draftControlledAckPayloadsV4 readback invalid |> Result.isError)
                Assert.Equal(retained,readback.State)
                Assert.True(PortableSupplierAdmission.prepareAck retained |> Result.isError)
                Assert.True(PortableSupplierAdmission.reserveHost retained |> Result.isError)
            match journal.StageV4(initial,None,None) with SupplierSourceReadback _ -> () | other -> failwithf "%A" other
            let mutable state = initial
            for name in ["creation";"genesis"] do
                let pair = SupplierCheckpointFixtures.exchangeFrom row name state
                let pending = PortableSupplierAdmission.issueChallenge pair.Challenge state |> SupplierCheckpointFixtures.ok
                match journal.StageV4(pending,Some state.Revision,Some state.CheckpointCustody.Value.StoreFence) with
                | SupplierSourceReadback _ -> () | other -> failwithf "%A" other
                let bytes key = row.GetProperty(name).GetProperty(key: string).GetString() |> SupplierCheckpointFixtures.urlBytes
                let exchange =
                    if name = "creation" then ControlledCreationExchange(bytes "request",[bytes "result";bytes "evidence"],row.GetProperty("reservation").GetString() |> SupplierCheckpointFixtures.urlBytes)
                    else ControlledGenesisExchange(bytes "request",[bytes "result";bytes "evidence"])
                let at = expected.GetProperty("warmVerificationTimes").GetProperty(name).GetString()
                if name = "creation" then
                    for reservation in [null; [||]; Array.zeroCreate<byte> 6001] do
                        let invalid = ControlledCreationExchange(bytes "request",[bytes "result";bytes "evidence"],reservation)
                        Assert.True(PortableSupplierAdmission.verifyAndAcceptControlledV4 (selection at false) pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending invalid |> Result.isError)
                if name = "genesis" then
                    let custody = pending.CheckpointCustody.Value
                    let creation = custody.CreationExchange.Value
                    let originalResult = Convert.FromBase64String creation.ResultBase64
                    use envelope = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(originalResult))
                    let payload = envelope.RootElement.GetProperty("payload").GetString()
                    let invalidResult = Encoding.ASCII.GetBytes("{\"payload\":\"" + payload + "\",\"signature\":\"b3BhcXVlLXN0cnVjdHVyYWwtb25seQ\"}\n")
                    let bundle = Convert.FromBase64String creation.ControllerBundleBase64 |> Encoding.ASCII.GetString
                    let changedBundle = bundle.Replace(SupplierCheckpointFixtures.hash originalResult,SupplierCheckpointFixtures.hash invalidResult) |> Encoding.ASCII.GetBytes
                    let changedCreation = { creation with ResultBase64 = Convert.ToBase64String invalidResult; ControllerBundleBase64 = Convert.ToBase64String changedBundle }
                    let structuralPredecessor = { pending with CheckpointCustody = Some { custody with CreationExchange = Some changedCreation } }
                    Assert.True(PortableSupplierAdmission.validate structuralPredecessor |> Result.isOk)
                    let denied = PortableSupplierAdmission.verifyAndAcceptControlledV4 (selection at false) structuralPredecessor.Original structuralPredecessor.Revision custody.StoreFence structuralPredecessor exchange |> SupplierCheckpointFixtures.ok
                    match denied with HeldControlledSource _ -> () | _ -> failwith "valid fresh pair cannot authenticate an invalid retained result signature"
                let accepted = PortableSupplierAdmission.verifyAndAcceptControlledV4 (selection at false) pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending exchange |> SupplierCheckpointFixtures.ok
                let transition = match accepted with AuthenticatedControlledSource value -> value | HeldControlledSource value -> failwithf "signed source held: %A" value.FirstFailure
                exerciseStoreCuts (selection at false) transition
                match journal.StageControlledV4 transition with SupplierSourceReadback _ -> () | other -> failwithf "%A" other
                Assert.Equal(SupplierSourceConflict,journal.StageControlledV4 transition)
                state <- transition.State
                if name = "genesis" then
                    PortableSupplierAdmission.reverifyControlledSourceV4 (selection at false) state
                    |> SupplierCheckpointFixtures.ok |> assertInertAckDrafts
            // Continue the same real signed DAG through output wire and native acquisition.
            let stageTransition current outcome =
                let transition = match outcome with AuthenticatedControlledSource value -> value | HeldControlledSource held -> failwithf "signed output source held: %A" held.FirstFailure
                exerciseStoreCuts current transition
                match journal.StageControlledV4 transition with SupplierSourceReadback _ -> () | other -> failwithf "%A" other
                Assert.Equal(SupplierSourceConflict,journal.StageControlledV4 transition)
                let reopened,_ = journal.Read transition.State.Original.Command |> SupplierCheckpointFixtures.ok
                Assert.Equal(transition.State,reopened)
                reopened
            let exact name key = row.GetProperty(name: string).GetProperty(key: string).GetString() |> SupplierCheckpointFixtures.urlBytes
            for name in ["unchanged-genesis";"append";"unchanged-proof"] do
                let before = state
                let pair = SupplierCheckpointFixtures.exchangeFrom row name state
                let pending = PortableSupplierAdmission.issueChallenge pair.Challenge state |> SupplierCheckpointFixtures.ok
                match journal.StageV4(pending,Some state.Revision,Some state.CheckpointCustody.Value.StoreFence) with
                | SupplierSourceReadback _ -> () | other -> failwithf "%A" other
                let input = ControlledOutputWireExchange(exact name "request",[exact name "result";exact name "evidence"])
                let current = selection (expected.GetProperty("warmVerificationTimes").GetProperty(name).GetString()) false
                if name = "append" then
                    // Seventeen event rows exceed the closed response cap before inner signatures.
                    use envelope = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(exact name "evidence"))
                    let responseBytes = envelope.RootElement.GetProperty("payload").GetString() |> SupplierCheckpointFixtures.urlBytes
                    use response = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(responseBytes))
                    let events = response.RootElement.GetProperty("events")
                    let tooMany = "[" + String.concat "," (List.replicate 17 (events[0].GetRawText())) + "]"
                    let changed = (Encoding.ASCII.GetString responseBytes).Replace(events.GetRawText(),tooMany) |> Encoding.ASCII.GetBytes
                    let url (bytes: byte array) = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                    let invalid = Encoding.ASCII.GetBytes("{\"payload\":\"" + url changed + "\",\"signature\":\"" + envelope.RootElement.GetProperty("signature").GetString() + "\"}\n")
                    let excessive = ControlledOutputWireExchange(exact name "request",[exact name "result";invalid])
                    match PortableSupplierAdmission.verifyAndAcceptControlledV4 current pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending excessive |> SupplierCheckpointFixtures.ok with
                    | HeldControlledSource held -> Assert.True(held.FirstFailure.IsSome)
                    | _ -> failwith "oversized event census authenticated"
                state <- PortableSupplierAdmission.verifyAndAcceptControlledV4 current pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending input
                         |> SupplierCheckpointFixtures.ok |> stageTransition current
                if name <> "append" then
                    Assert.Equal<string list>(before.EventsBase64,state.EventsBase64)
                    Assert.Equal(before.Head,state.Head)
                    Assert.Equal(before.Sequence,state.Sequence)
            let pending = SupplierCheckpointFixtures.issueNativeFrom row state
            match journal.StageV4(pending,Some state.Revision,Some state.CheckpointCustody.Value.StoreFence) with
            | SupplierSourceReadback _ -> () | other -> failwithf "%A" other
            match pending.Challenge.Value with
            | NativeSelectedProofV2 challenge ->
                // Sixteen selected members do not imply sixteen events or a pre-acquisition receipt hash.
                match pending.Pinned.Value.OriginalReservation.Selection with
                | SelectedOutputs selected -> Assert.Equal(16UL,selected.MemberCount)
                | _ -> failwith "selected native issuance lost manifest selection"
                Assert.DoesNotContain("native_receipt_sha256",Convert.FromBase64String challenge.BytesBase64 |> Encoding.ASCII.GetString)
            | NativeProofV1 challenge -> Assert.Equal(SupplierCheckpointFixtures.hash (exact "native" "receipt"),challenge.Context.NativeReceiptSha256)
            | _ -> failwith "native challenge kind"
            let nativeSelection = selection (expected.GetProperty("warmVerificationTimes").GetProperty("native").GetString()) false
            let nativeInput = ControlledNativeAcquisitionExchange(exact "native" "request",[exact "native" "result";exact "native" "proof"])
            let mismatched = ControlledNativeAcquisitionExchange(exact "native" "request",[exact "append" "result";exact "native" "proof"])
            let denied = PortableSupplierAdmission.verifyAndAcceptControlledV4 nativeSelection pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending mismatched |> SupplierCheckpointFixtures.ok
            match denied with
            | HeldControlledSource held ->
                Assert.True(held.FirstFailure.IsSome)
                let rejected = held.CheckpointCustody.Value.RejectedExchanges |> List.exactlyOne
                Assert.Equal<string list>([Convert.ToBase64String(exact "append" "result");Convert.ToBase64String(exact "native" "proof")],rejected.FramesBase64)
                let again = PortableSupplierAdmission.verifyAndAcceptControlledV4 nativeSelection held.Original held.Revision held.CheckpointCustody.Value.StoreFence held nativeInput |> SupplierCheckpointFixtures.ok
                match again with HeldControlledSource sticky -> Assert.Equal(held,sticky) | _ -> failwith "held input acquired a success wrapper"
            | _ -> failwith "mismatched signed native pair authenticated"
            state <- PortableSupplierAdmission.verifyAndAcceptControlledV4 nativeSelection pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending nativeInput
                     |> SupplierCheckpointFixtures.ok |> stageTransition nativeSelection
            Assert.True(state.CheckpointCustody.Value.OutputCheckpointBase64.IsSome)
            Assert.Single(state.CheckpointCustody.Value.NativeAcquisitions) |> ignore
            let cold,durability = journal.Read state.Original.Command |> SupplierCheckpointFixtures.ok
            Assert.Equal(state,cold)
            match durability with EvidenceUnknown _ -> () | _ -> failwith "controlled authentication cannot qualify durability"
            let at = expected.GetProperty("coldVerificationTime").GetString()
            let current = selection at false
            let fence = cold.CheckpointCustody.Value.StoreFence
            let readback,authenticatedDurability = journal.ReadControlledV4(current,cold.Original,cold.Revision,fence) |> SupplierCheckpointFixtures.ok
            Assert.Equal(cold,readback.State)
            assertInertAckDrafts readback
            match authenticatedDurability with EvidenceUnknown _ -> () | _ -> failwith "locked cold authentication cannot qualify durability"
            let foreignOriginal = { cold.Original with ReceiverEpoch = Guid.Empty }
            Assert.True(journal.ReadControlledV4(current,foreignOriginal,cold.Revision,fence) |> Result.isError)
            Assert.True(journal.ReadControlledV4(current,cold.Original,cold.Revision - 1UL,fence) |> Result.isError)
            Assert.True(journal.ReadControlledV4(current,cold.Original,cold.Revision,fence - 1UL) |> Result.isError)
            use missingFixture = new HostExecutorFixture()
            let missingJournal = PortableWorkspaceExecutor.SupplierAdmissionJournal missingFixture.Policy.Runtime.StateRoot
            Assert.True(missingJournal.ReadControlledV4(current,cold.Original,cold.Revision,fence) |> Result.isError)
            Assert.Equal(0,missingFixture.Runner.Runs)
            Assert.Equal(0,missingFixture.Runner.Cleans)
            Assert.Equal(0,missingFixture.Runner.Recoveries)
            let directory = Path.Combine(fixture.Policy.Runtime.StateRoot,"journal-v1")
            let recordPath = Directory.GetFiles(directory,"*.bin") |> Array.exactlyOne
            let lockPath = Directory.GetFiles(directory,"*.lock") |> Array.exactlyOne
            let deniedWhileLocked =
                use owner = new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)
                journal.ReadControlledV4(current,cold.Original,cold.Revision,fence)
            Assert.True(deniedWhileLocked |> Result.isError)
            let unlocked,_ = journal.ReadControlledV4(current,cold.Original,cold.Revision,fence) |> SupplierCheckpointFixtures.ok
            Assert.Equal(cold,unlocked.State)
            let recordBytes = File.ReadAllBytes recordPath
            Assert.True(recordBytes[0] < 128uy)
            // An overlong BinaryReader length prefix decodes to the same typed state, but
            // is not the exact canonical record selected by the existing serializer.
            let overlongHeader = Array.concat [ [| recordBytes[0] ||| 128uy; 0uy |]; recordBytes[1..] ]
            for equivalent,damaged in
                [ false,recordBytes[..recordBytes.Length - 2]
                  false,Array.append recordBytes [| 0uy |]
                  false,Array.append [| 0uy |] recordBytes[1..]
                  true,overlongHeader ] do
                use damagedFixture = new HostExecutorFixture()
                let damagedDirectory = Path.Combine(damagedFixture.Policy.Runtime.StateRoot,"journal-v1")
                Directory.CreateDirectory damagedDirectory |> ignore
                let damagedPath = Path.Combine(damagedDirectory,Path.GetFileName recordPath)
                File.WriteAllBytes(damagedPath,damaged)
                let damagedJournal = PortableWorkspaceExecutor.SupplierAdmissionJournal damagedFixture.Policy.Runtime.StateRoot
                if equivalent then
                    let structural,_ = damagedJournal.Read cold.Original.Command |> SupplierCheckpointFixtures.ok
                    Assert.Equal(cold,structural)
                Assert.True(damagedJournal.ReadControlledV4(current,cold.Original,cold.Revision,fence) |> Result.isError)
                Assert.True((damaged = File.ReadAllBytes damagedPath))
                Assert.Empty(Directory.GetFiles(damagedDirectory,"*.tmp"))
                Assert.Equal(0,damagedFixture.Runner.Runs)
                Assert.Equal(0,damagedFixture.Runner.Cleans)
                Assert.Equal(0,damagedFixture.Runner.Recoveries)
            let custody = cold.CheckpointCustody.Value
            let checkpoint = custody.OutputCheckpointBase64.Value |> Convert.FromBase64String |> Encoding.ASCII.GetString
            let relabelled = checkpoint.Replace("\"controlled-test\"","\"qualified-production\"") |> Encoding.ASCII.GetBytes |> Convert.ToBase64String
            let structuralRelabel = { cold with CheckpointCustody = Some { custody with OutputCheckpointBase64 = Some relabelled } }
            Assert.True(PortableSupplierAdmission.validate structuralRelabel |> Result.isOk)
            Assert.True(PortableSupplierAdmission.reverifyControlledSourceV4 (selection at false) structuralRelabel |> Result.isError)
            let pinned = cold.Pinned.Value
            let binding = pinned.OriginalReservation.Binding
            for changed in
                [ { pinned with Identity = { pinned.Identity with Issuer = "foreign-controlled-principal" } }
                  { pinned with OriginalReservation = { pinned.OriginalReservation with Binding = { binding with AllocationId = "foreign-allocation" } } }
                  { pinned with OriginalReservation = { pinned.OriginalReservation with Binding = { binding with WorkerId = "foreign-worker" } } }
                  { pinned with OriginalReservation = { pinned.OriginalReservation with Binding = { binding with AllocationReceiptDigest = "foreign-receipt" } } } ] do
                let structural = { cold with Pinned = Some changed }
                Assert.True(PortableSupplierAdmission.validate structural |> Result.isOk)
                Assert.True(PortableSupplierAdmission.reverifyControlledSourceV4 (selection at false) structural |> Result.isError)
            Assert.True(journal.ReadControlledV4(selection at true,cold.Original,cold.Revision,fence) |> Result.isError)
            let observations: (PortableSupplierControlledCurrentObservation -> PortableSupplierControlledCurrentObservation) list =
                [ (fun current -> { current with SelectionObservedAt = current.VerifiedAt.AddTicks 10L })
                  (fun current -> { current with SelectionObservedAt = current.VerifiedAt.AddSeconds(-31.) })
                  (fun current -> { current with SelectionValidUntil = current.VerifiedAt })
                  (fun current -> { current with CurrentReceiverClockDomain = Guid.Empty })
                  (fun current -> { current with ReceiverMonotonic = -1L })
                  (fun current -> { current with HostClock = { current.HostClock with Revision = current.HostClock.Revision + 1UL } }) ]
            for observation in observations do
                Assert.True(journal.ReadControlledV4(selectionWith id observation at false,cold.Original,cold.Revision,fence) |> Result.isError)
            for policyChange in
                [ List.filter (fun (policy: PortableSupplierControlledRolePolicy) -> policy.Role <> NativeProofSignature)
                  List.map (fun (policy: PortableSupplierControlledRolePolicy) -> if policy.Role = NativeProofSignature then { policy with Revocation = ControlledRevocationUnavailable } else policy) ] do
                Assert.True(journal.ReadControlledV4(selectionWith policyChange id at false,cold.Original,cold.Revision,fence) |> Result.isError)
            Assert.True(PortableSupplierAdmission.prepareAck readback.State |> Result.isError)
            Assert.True(PortableSupplierAdmission.reserveHost readback.State |> Result.isError)
            Assert.Equal(0,fixture.Runner.Runs)
            Assert.Equal(0,fixture.Runner.Cleans)
            Assert.Equal(0,fixture.Runner.Recoveries)

        ControlledGenesisSourceExport.assertPublicRegression publicBytes


/// Actual prospective codec controls. No transport, CLR child, Podman or filesystem effect port.
type PortableHostExecutorTests_PortableHostExportSourceTests(output: Xunit.Abstractions.ITestOutputHelper) =
    let ok = HostExecutorAssertions.ok
    let context (fixture: HostExecutorFixture) =
        let manifest = PortableSupplierManifest.create [{ Path = "result.txt"; Bytes = 0UL; Sha256 = String.replicate 64 "a" }] |> ok
        let command = { fixture.Command with CommandId = Guid.Parse("10000000-0000-0000-0000-000000000001") }
        let reservation = { fixture.Reservation with
                                Binding = { fixture.Reservation.Binding with Command = command; AllocationReceiptDigest = String.replicate 64 "b" }
                                Selection = SelectedOutputs(PortableSupplierManifest.summary manifest) }
        let root path device inode = { Path = path; Device = device; Inode = inode; OwnerUid = 1000UL; OwnerGid = 1000UL }
        { ExportOperationId = Guid.Parse("20000000-0000-0000-0000-000000000001"); Reservation = reservation
          ManifestBytes = PortableSupplierManifest.serialize manifest
          Clock = { HostBootId = Guid.Parse("30000000-0000-0000-0000-000000000001"); AdmittedMonotonicNanoseconds = 1000000000UL
                    ExportDeadlineMonotonicNanoseconds = 81000000000UL; LeaseDeadlineMonotonicNanoseconds = 91000000000UL }
          SourceRoot = root "/retiring/selected" 5UL 18UL; RetiringRoots = [root "/retiring" 5UL 17UL]
          DestinationRoot = root "/durable" 6UL 19UL }

    [<Fact>]
    member _.``full private original codec roundtrips without mutating or omitting reservation fields``() =
        use fixture = new HostExecutorFixture()
        let input = context fixture
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        let restored = PortableWorkspaceExecutor.HostExportSource.parseOriginal original.CanonicalBytes |> ok
        output.WriteLine("INERT_CONTEXT_BASE64=" + Convert.ToBase64String original.CanonicalBytes)
        output.WriteLine("INERT_CONTEXT_DIGEST=" + original.Digest)
        Assert.Equal(input, restored.Context)
        Assert.Equal(original.Digest, restored.Digest)
        Assert.Equal<byte>(original.BindingBytes, restored.BindingBytes)
        Assert.Equal<byte>(original.ReservationBytes, restored.ReservationBytes)
        let raw = original.CanonicalBytes
        let mutableManifest = original.Context.ManifestBytes
        mutableManifest[0] <- 0uy
        input.ManifestBytes[0] <- 0uy
        raw[0] <- 0uy
        Assert.Equal(original.Digest, restored.Digest)
        Assert.True(PortableWorkspaceExecutor.HostExportSource.parseOriginal original.CanonicalBytes |> Result.isOk)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``new source context refuses false scope clock selection and old-original substitution``() =
        use fixture = new HostExecutorFixture()
        let good = context fixture
        let invalid = [
            { good with ExportOperationId = Guid.Empty }
            { good with ManifestBytes = [||] }
            { good with Reservation = { good.Reservation with Selection = NoSelectedOutputs } }
            { good with Clock = { good.Clock with HostBootId = Guid.Empty } }
            { good with Clock = { good.Clock with LeaseDeadlineMonotonicNanoseconds = 92000000000UL } }
            { good with Clock = { good.Clock with ExportDeadlineMonotonicNanoseconds = 81000000001UL } }
            { good with DestinationRoot = { good.DestinationRoot with Device = 5UL } }
            { good with SourceRoot = { good.SourceRoot with Path = "/foreign" } }
            { good with DestinationRoot = { good.DestinationRoot with Path = "/retiring/destination" } }
            { good with RetiringRoots = [] }
            { good with RetiringRoots = good.RetiringRoots @ good.RetiringRoots }
            { good with Reservation = { good.Reservation with Binding = { good.Reservation.Binding with AllocationReceiptDigest = "caller-claim" } } } ]
        for value in invalid do Assert.True(PortableWorkspaceExecutor.HostExportSource.createOriginal value |> Result.isError)
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal good |> ok
        Assert.True(PortableWorkspaceExecutor.HostExportSource.originalClockCurrent good.Clock.HostBootId 1000000000UL original)
        Assert.False(PortableWorkspaceExecutor.HostExportSource.originalClockCurrent good.Clock.HostBootId 81000000000UL original)
        Assert.False(PortableWorkspaceExecutor.HostExportSource.originalClockCurrent Guid.Empty 1000000000UL original)
        Assert.False(PortableWorkspaceExecutor.HostExportSource.originalClockCurrent good.Clock.HostBootId 999999999UL original)
        Assert.Equal(0, fixture.Runner.Runs)

    [<Fact>]
    member _.``canonical original reader refuses truncation trailing bytes and nonminimal binary length``() =
        use fixture = new HostExecutorFixture()
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal (context fixture) |> ok
        let raw = original.CanonicalBytes
        // BinaryReader can decode a finite nonminimal 7-bit string prefix; exact canonical bytes still refuse.
        Assert.True(raw[0] < 128uy)
        let nonminimal = Array.concat [[|raw[0] ||| 128uy; 0uy|]; raw[1..]]
        for bytes in [raw[..raw.Length-2]; Array.append raw [|0uy|]; nonminimal; Array.zeroCreate 65537] do
            Assert.True(PortableWorkspaceExecutor.HostExportSource.parseOriginal bytes |> Result.isError)
        Assert.True(PortableWorkspaceExecutor.HostExportSource.parseOriginal raw |> Result.isOk)

    [<Fact>]
    member _.``source attempt uses existing lock journal and retains consumed intent across cold reopen without activating runner``() =
        use fixture = new HostExecutorFixture()
        let input = context fixture
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        let identity = { Issuer = "inert-owning-adapter"; ContainerId = "inert-container"
                         AllocationDevice = 5UL; AllocationInode = 17UL }
        let authenticate lifecycle =
            let raw = Encoding.UTF8.GetBytes "inert independently authenticated fixture"
            let evidence = { Identity = identity; Reservation = input.Reservation; CollectedAt = fixture.Start
                             ReceiptDigest = Convert.ToHexString(SHA256.HashData raw).ToLowerInvariant(); Lifecycle = lifecycle }
            let verifier = { new IPortableHostSupplierVerifier with member _.Authenticate _ = Ok evidence }
            PortableHostSupplierIngress.authenticate verifier identity input.Reservation fixture.Start (TimeSpan.FromMinutes 1.) raw |> ok
        let journal = PortableWorkspaceExecutor.HostExportSourceJournal(fixture.Policy.Runtime.StateRoot)
        let initial = authenticate PortableHostAllocation.emptyLifecycle
        let _, durability = journal.Begin(original, initial, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> ok
        match durability with EvidenceUnknown _ -> () | _ -> failwith "not directory qualified"
        let originalBytes = File.ReadAllBytes fixture.Journal
        Assert.True(journal.Begin(original, initial, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> Result.isError)
        Assert.Equal<byte>(originalBytes, File.ReadAllBytes fixture.Journal)
        Assert.True(journal.PrepareAttempt(original, initial, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> Result.isError)
        Assert.Equal<byte>(originalBytes, File.ReadAllBytes fixture.Journal)
        let termination = { Binding = input.Reservation.Binding; CollectedAt = fixture.Start; ReceiptDigest = String.replicate 64 "c"; Value = () }
        let stopped = authenticate { PortableHostAllocation.emptyLifecycle with Termination = EvidenceKnown termination; FirstFailure = Some "original workload Unknown" }
        let attempt, _ = journal.PrepareAttempt(original, stopped, fixture.Start, input.Clock.HostBootId, 1000000001UL) |> ok
        let beforeReplay = File.ReadAllBytes fixture.Journal
        let cold = PortableWorkspaceExecutor.HostExportSourceJournal(fixture.Policy.Runtime.StateRoot)
        let state, unknown = cold.Read original |> ok
        Assert.Equal<byte>(attempt, state.AttemptBytes.Value)
        Assert.Equal(Some "original workload Unknown", state.Lifecycle.FirstFailure)
        Assert.True(cold.PrepareAttempt(original, stopped, fixture.Start, input.Clock.HostBootId, 1000000002UL) |> Result.isError)
        Assert.Equal<byte>(beforeReplay, File.ReadAllBytes fixture.Journal)
        Assert.True(cold.ObserveCopy(original, String.replicate 64 "0", [|1uy|]) |> Result.isError)
        let digest = Convert.ToHexString(SHA256.HashData attempt).ToLowerInvariant()
        let local, _ = cold.ObserveCopy(original, digest, [|1uy;2uy|]) |> ok
        match local.Lifecycle.Export with EvidenceKnown _ -> failwith "local copy promoted" | _ -> ()
        let copyBytes = File.ReadAllBytes fixture.Journal
        Assert.True(cold.ObserveCopy(original, digest, [|3uy|]) |> Result.isError)
        Assert.Equal<byte>(copyBytes, File.ReadAllBytes fixture.Journal)
        let receivingRaw = [|99uy;98uy|]
        let manifest = match input.Reservation.Selection with SelectedOutputs value -> value | _ -> failwith "selection"
        let exported = { Binding = input.Reservation.Binding; CollectedAt = fixture.Start; ReceiptDigest = String.replicate 64 "d"; Value = DurableManifestReadback manifest }
        let incoming = { Host = { Identity = identity; Reservation = input.Reservation; CollectedAt = fixture.Start
                                  ReceiptDigest = Convert.ToHexString(SHA256.HashData receivingRaw).ToLowerInvariant()
                                  Lifecycle = { PortableHostAllocation.emptyLifecycle with Termination = EvidenceKnown termination; Export = EvidenceKnown exported } }
                         ContextDigest = original.Digest; AttemptDigest = digest
                         CopyObservationDigest = Convert.ToHexString(SHA256.HashData [|1uy;2uy|]).ToLowerInvariant()
                         DestinationRoot = input.DestinationRoot; Manifest = manifest
                         WriterTerminationReceiptDigest = termination.ReceiptDigest; ReceivingCommitReceiptDigest = exported.ReceiptDigest
                         HostBootId = input.Clock.HostBootId; ObservedMonotonicNanoseconds = 1000000003UL }
        let receivingVerifier = { new IPortableHostExportReceivingVerifier with member _.Authenticate _ = Ok incoming }
        let receiving = PortableWorkspaceExecutor.HostExportSource.authenticateReceiving receivingVerifier identity original attempt [|1uy;2uy|]
                            fixture.Start (TimeSpan.FromMinutes 1.) receivingRaw |> ok
        let recorded, _ = cold.ObserveReceiving(original, receiving, fixture.Start) |> ok
        Assert.Equal(Some "original workload Unknown", recorded.Lifecycle.FirstFailure)
        Assert.Equal(EvidenceKnown exported, recorded.Lifecycle.Export)
        let reread, durability = PortableWorkspaceExecutor.HostExportSourceJournal(fixture.Policy.Runtime.StateRoot).Read original |> ok
        Assert.Equal(recorded.Lifecycle, reread.Lifecycle)
        Assert.Equal<byte>(receivingRaw, reread.ReceivingObservationBytes.Value)
        match durability with EvidenceUnknown _ -> () | _ -> failwith "cold data became durable auth"
        let outcome = fixture.Executor(selected = input.Reservation).ExecuteAsync(fixture.Authority, fixture.Profile, input.Reservation.Binding.Command, CancellationToken.None).GetAwaiter().GetResult()
        match outcome with Refused _ -> () | _ -> failwith "source-only journal activated executor"
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)
        Assert.Equal(0, fixture.Runner.Recoveries)

    [<Fact>]
    member _.``receiving ingress authenticates full original destination manifest clock and both retained observation digests``() =
        use fixture = new HostExecutorFixture()
        let input = context fixture
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        let identity = { Issuer = "inert-receiver-owner"; ContainerId = "inert-container"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let raw, attempt, copied = [|10uy;11uy|], [|12uy|], [|13uy|]
        let digest (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
        let manifest = match input.Reservation.Selection with SelectedOutputs value -> value | _ -> failwith "selection"
        let termination = { Binding = input.Reservation.Binding; CollectedAt = fixture.Start; ReceiptDigest = String.replicate 64 "c"; Value = () }
        let exported = { Binding = input.Reservation.Binding; CollectedAt = fixture.Start; ReceiptDigest = String.replicate 64 "d"; Value = DurableManifestReadback manifest }
        let good = { Host = { Identity = identity; Reservation = input.Reservation; CollectedAt = fixture.Start; ReceiptDigest = digest raw
                              Lifecycle = { PortableHostAllocation.emptyLifecycle with Termination = EvidenceKnown termination; Export = EvidenceKnown exported } }
                     ContextDigest = original.Digest; AttemptDigest = digest attempt; CopyObservationDigest = digest copied
                     DestinationRoot = input.DestinationRoot; Manifest = manifest; WriterTerminationReceiptDigest = termination.ReceiptDigest
                     ReceivingCommitReceiptDigest = exported.ReceiptDigest; HostBootId = input.Clock.HostBootId; ObservedMonotonicNanoseconds = 1000000001UL }
        let authenticate value =
            let verifier = { new IPortableHostExportReceivingVerifier with member _.Authenticate _ = Ok value }
            PortableWorkspaceExecutor.HostExportSource.authenticateReceiving verifier identity original attempt copied fixture.Start (TimeSpan.FromMinutes 1.) raw
        let accepted = authenticate good |> ok
        Assert.Equal(good, accepted.Evidence)
        let changed = accepted.Bytes
        changed[0] <- 0uy
        Assert.Equal<byte>(raw, accepted.Bytes)
        for bad in [
            { good with ContextDigest = String.replicate 64 "0" }
            { good with AttemptDigest = String.replicate 64 "0" }
            { good with CopyObservationDigest = String.replicate 64 "0" }
            { good with DestinationRoot = { good.DestinationRoot with Inode = 999UL } }
            { good with Manifest = { good.Manifest with TotalBytes = 1UL } }
            { good with HostBootId = Guid.NewGuid() }
            { good with ObservedMonotonicNanoseconds = input.Clock.ExportDeadlineMonotonicNanoseconds }
            { good with WriterTerminationReceiptDigest = String.replicate 64 "0" }
            { good with ReceivingCommitReceiptDigest = String.replicate 64 "0" }
            { good with Host = { good.Host with ReceiptDigest = String.replicate 64 "0" } }
            { good with Host = { good.Host with Reservation = { input.Reservation with MaximumRuntime = TimeSpan.FromSeconds 59. } } }
            { good with Host = { good.Host with Lifecycle = { good.Host.Lifecycle with Termination = EvidenceUnknown "not observed" } } }
            { good with Host = { good.Host with Lifecycle = { good.Host.Lifecycle with Export = EvidenceUnknown "local copy only" } } }
        ] do Assert.True(authenticate bad |> Result.isError)
        let refusing = { new IPortableHostExportReceivingVerifier with member _.Authenticate _ = Error "not authenticated" }
        Assert.True(PortableWorkspaceExecutor.HostExportSource.authenticateReceiving refusing identity original attempt copied fixture.Start (TimeSpan.FromMinutes 1.) raw |> Result.isError)
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``fresh source original cannot upgrade or replace occupied legacy host journal bytes``() =
        use fixture = new HostExecutorFixture()
        fixture.Executor().ExecuteAsync(fixture.Authority, fixture.Profile, fixture.Command, CancellationToken.None).GetAwaiter().GetResult() |> ignore
        let old = File.ReadAllBytes fixture.Journal
        let input = context fixture
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        let identity = { Issuer = "inert-owner"; ContainerId = "inert-container"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let raw = [|1uy|]
        let evidence = { Identity = identity; Reservation = input.Reservation; CollectedAt = fixture.Start
                         ReceiptDigest = Convert.ToHexString(SHA256.HashData raw).ToLowerInvariant(); Lifecycle = PortableHostAllocation.emptyLifecycle }
        let verifier = { new IPortableHostSupplierVerifier with member _.Authenticate _ = Ok evidence }
        let authenticated = PortableHostSupplierIngress.authenticate verifier identity input.Reservation fixture.Start (TimeSpan.FromMinutes 1.) raw |> ok
        let journal = PortableWorkspaceExecutor.HostExportSourceJournal(fixture.Policy.Runtime.StateRoot)
        Assert.True(journal.Begin(original, authenticated, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> Result.isError)
        Assert.Equal<byte>(old, File.ReadAllBytes fixture.Journal)
        Assert.True(journal.Read original |> Result.isError)
        Assert.Equal<byte>(old, File.ReadAllBytes fixture.Journal)
        let legacy = fixture.Executor().ReadHostState(fixture.Profile, fixture.Command) |> ok
        Assert.Equal(fixture.Reservation, legacy.Reservation)
        Assert.Equal<byte>(old, File.ReadAllBytes fixture.Journal)

    [<Theory>]
    [<InlineData(0, false)>]
    [<InlineData(1, false)>]
    [<InlineData(2, false)>]
    [<InlineData(3, false)>]
    [<InlineData(4, false)>]
    [<InlineData(0, true)>]
    [<InlineData(1, true)>]
    [<InlineData(2, true)>]
    [<InlineData(3, true)>]
    [<InlineData(4, true)>]
    [<InlineData(5, false)>]
    [<InlineData(6, false)>]
    [<InlineData(7, false)>]
    [<InlineData(5, true)>]
    [<InlineData(6, true)>]
    [<InlineData(7, true)>]
    member _.``source write first failure retains old bytes or consumed new intent without replay or effects``(cut: int, duringAttempt: bool) =
        use fixture = new HostExecutorFixture()
        let input = context fixture
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        let identity = { Issuer = "inert-owner"; ContainerId = "inert-container"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let authenticate lifecycle =
            let raw = [|42uy|]
            let evidence = { Identity = identity; Reservation = input.Reservation; CollectedAt = fixture.Start
                             ReceiptDigest = Convert.ToHexString(SHA256.HashData raw).ToLowerInvariant(); Lifecycle = lifecycle }
            let verifier = { new IPortableHostSupplierVerifier with member _.Authenticate _ = Ok evidence }
            PortableHostSupplierIngress.authenticate verifier identity input.Reservation fixture.Start (TimeSpan.FromMinutes 1.) raw |> ok
        let initial = authenticate PortableHostAllocation.emptyLifecycle
        let termination = { Binding = input.Reservation.Binding; CollectedAt = fixture.Start; ReceiptDigest = String.replicate 64 "c"; Value = () }
        let stopped = authenticate { PortableHostAllocation.emptyLifecycle with Termination = EvidenceKnown termination }
        let normal = PortableWorkspaceExecutor.HostExportSourceJournal(fixture.Policy.Runtime.StateRoot)
        if duringAttempt then normal.Begin(original, initial, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> ok |> ignore
        let previous = if duringAttempt then Some(File.ReadAllBytes fixture.Journal) else None
        let point = match cut with
                    | 0 -> PortableWorkspaceExecutor.BeforeSourceWrite
                    | 1 -> PortableWorkspaceExecutor.BeforeSourceFileFlush
                    | 2 -> PortableWorkspaceExecutor.BeforeSourceReplacement
                    | 3 -> PortableWorkspaceExecutor.AfterSourceReplacementBeforeReadback
                    | 4 -> PortableWorkspaceExecutor.BeforeSourceColdReopen
                    | 5 -> PortableWorkspaceExecutor.BeforeSourceDirectorySync
                    | 6 -> PortableWorkspaceExecutor.AfterSourceDirectorySyncBeforeReadback
                    | _ -> PortableWorkspaceExecutor.SimulatedSourceDirectorySyncError
        let failing = PortableWorkspaceExecutor.HostExportSourceJournal.CreateControlledFailureTest(fixture.Policy.Runtime.StateRoot, point)
        if duringAttempt then
            Assert.True(failing.PrepareAttempt(original, stopped, fixture.Start, input.Clock.HostBootId, 1000000001UL) |> Result.isError)
        else Assert.True(failing.Begin(original, initial, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> Result.isError)
        if cut < 3 then
            match previous with
            | Some old -> Assert.Equal<byte>(old, File.ReadAllBytes fixture.Journal)
            | None -> Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Policy.Runtime.StateRoot, "journal-v1"), "*.bin"))
        else
            let retained, durability = normal.Read original |> ok
            Assert.Equal(duringAttempt, retained.AttemptBytes.IsSome)
            match durability with EvidenceUnknown _ -> () | _ -> failwith "lost source return promoted durability"
            let originalBytes = File.ReadAllBytes fixture.Journal
            if duringAttempt then Assert.True(normal.PrepareAttempt(original, stopped, fixture.Start, input.Clock.HostBootId, 1000000002UL) |> Result.isError)
            else Assert.True(normal.Begin(original, initial, fixture.Start, input.Clock.HostBootId, 1000000000UL) |> Result.isError)
            Assert.Equal<byte>(originalBytes, File.ReadAllBytes fixture.Journal)
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Policy.Runtime.StateRoot, "journal-v1"), "*.tmp"))
        Assert.Equal(0, fixture.Runner.Runs)
        Assert.Equal(0, fixture.Runner.Cleans)

    [<Fact>]
    member _.``new codec preserves valid nonASCII exactly and refuses every invalid surrogate field``() =
        use fixture = new HostExecutorFixture()
        let input = context fixture
        let unicodeBinding =
            { input.Reservation.Binding with
                QualifiedImage = input.Reservation.Binding.QualifiedImage + "é"
                AllocationId = "allocation-λ"
                WorkerId = "worker-🙂" }
        let unicode =
            { input with
                Reservation = { input.Reservation with Binding = unicodeBinding }
                SourceRoot = { input.SourceRoot with Path = "/retiring/é" }
                DestinationRoot = { input.DestinationRoot with Path = "/durable/日本" } }
        let original = PortableWorkspaceExecutor.HostExportSource.createOriginal unicode |> ok
        let restored = PortableWorkspaceExecutor.HostExportSource.parseOriginal original.CanonicalBytes |> ok
        Assert.Equal(unicode, restored.Context)
        Assert.Equal<byte>(original.CanonicalBytes, restored.CanonicalBytes)
        for broken in [ string (char 0xD800); string (char 0xDC00) ] do
            for bad in [
                { input with SourceRoot = { input.SourceRoot with Path = "/retiring/" + broken } }
                { input with DestinationRoot = { input.DestinationRoot with Path = "/durable/" + broken } }
                { input with RetiringRoots = [{ input.RetiringRoots.Head with Path = "/" + broken }]; SourceRoot = { input.SourceRoot with Path = "/" + broken + "/source" } }
                { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with QualifiedImage = "image-" + broken } } }
                { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with AllocationId = "allocation-" + broken } } }
                { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with WorkerId = "worker-" + broken } } }
            ] do Assert.True(PortableWorkspaceExecutor.HostExportSource.createOriginal bad |> Result.isError)
        let malformed = original.CanonicalBytes
        let index = [0..malformed.Length-2] |> List.find (fun n -> malformed[n] = 0xC3uy && malformed[n+1] = 0xA9uy)
        malformed[index] <- 0xC0uy
        malformed[index+1] <- 0xAFuy
        Assert.True(PortableWorkspaceExecutor.HostExportSource.parseOriginal malformed |> Result.isError)

    [<Fact>]
    member _.``new codec preflights individual and aggregate encoding before allocation and admits exact64KiB boundary``() =
        use fixture = new HostExecutorFixture()
        let input = context fixture
        let huge = String.replicate 65537 "x"
        let large = String.replicate 23000 "x"
        for bad in [
            { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with QualifiedImage = huge } } }
            { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with AllocationId = huge } } }
            { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with WorkerId = huge } } }
            { input with Reservation = { input.Reservation with Binding = { input.Reservation.Binding with QualifiedImage = large; AllocationId = large; WorkerId = large } } }
        ] do Assert.True(PortableWorkspaceExecutor.HostExportSource.createOriginal bad |> Result.isError)
        let initial = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        // Crossing the BinaryWriter 128-byte string-prefix boundary adds one prefix byte too.
        let mutable padding = 65536 - initial.CanonicalBytes.Length
        let padded count =
            let binding = { input.Reservation.Binding with QualifiedImage = input.Reservation.Binding.QualifiedImage + String.replicate count "x" }
            { input with Reservation = { input.Reservation with Binding = binding } }
        let prefixBytes length = if length < 128 then 1 elif length < 16384 then 2 else 3
        padding <- padding - (prefixBytes (input.Reservation.Binding.QualifiedImage.Length + padding) - prefixBytes input.Reservation.Binding.QualifiedImage.Length)
        let exact = PortableWorkspaceExecutor.HostExportSource.createOriginal (padded padding) |> ok
        Assert.Equal(65536, exact.CanonicalBytes.Length)
        Assert.Equal((padded padding), (PortableWorkspaceExecutor.HostExportSource.parseOriginal exact.CanonicalBytes |> ok).Context)
        Assert.True(PortableWorkspaceExecutor.HostExportSource.createOriginal (padded (padding+1)) |> Result.isError)


// These source controls exercise actual Linux descriptor operations when run under a
// separately admitted native test profile. They do not simulate power loss or qualify storage.
type PortableHostExecutorTests_SourceDirectoryDescriptorTests() =
    let noCut () = ()

    [<Fact>]
    member _.``source file rename sync and read stay on the pinned directory after pathname substitution``() =
        if OperatingSystem.IsLinux() then
            let root = Path.Combine(Path.GetTempPath(), "source-directory-" + Guid.NewGuid().ToString("N"))
            let journal = Path.Combine(root, "journal-v1")
            let moved = Path.Combine(root, "held-journal")
            Directory.CreateDirectory journal |> ignore
            try
                let path = Path.Combine(journal, "key.bin")
                use owner = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                use source = new PortableSourceJournalIO.Context(journal, path, owner.SafeFileHandle)
                Directory.Move(journal, moved)
                Directory.CreateDirectory journal |> ignore
                let expected = [|1uy;2uy;3uy|]
                source.Write(expected, noCut, noCut, noCut, noCut, noCut)
                use cold = source.OpenRead() |> Option.defaultWith (fun () -> failwith "held source missing")
                let actual = Array.zeroCreate<byte> expected.Length
                cold.ReadExactly(actual.AsSpan())
                Assert.Equal<byte>(expected, actual)
                Assert.Equal(-1, cold.ReadByte())
                Assert.Equal<byte>(expected, File.ReadAllBytes(Path.Combine(moved, "key.bin")))
                Assert.Empty(Directory.GetFileSystemEntries journal)
            finally Directory.Delete(root, true)

    [<Fact>]
    member _.``replaced lock entry refuses source reads and writes before a temporary file``() =
        if OperatingSystem.IsLinux() then
            let root = Path.Combine(Path.GetTempPath(), "source-lock-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory root |> ignore
            try
                let path = Path.Combine(root, "key.bin")
                let lockPath = Path.ChangeExtension(path, ".lock")
                use owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                use source = new PortableSourceJournalIO.Context(root, path, owner.SafeFileHandle)
                File.Move(lockPath, lockPath + ".old")
                File.WriteAllBytes(lockPath, [|9uy|])
                Assert.Throws<IOException>(fun () -> source.OpenRead() |> ignore) |> ignore
                Assert.Throws<IOException>(fun () -> source.Write([|1uy|], noCut, noCut, noCut, noCut, noCut)) |> ignore
                Assert.False(File.Exists path)
                Assert.Empty(Directory.GetFiles(root, "*.tmp"))
                Assert.Equal<byte>([|9uy|], File.ReadAllBytes lockPath)
            finally Directory.Delete(root, true)

/// Same original C/R signatures, newly captured TEST destination policy, and real executing verifier.
/// No signed fixture is rewritten and no native root or production authority is asserted.
type PortableHostExecutorTests_OriginalSourceComposition() =
    let ok = SupplierCheckpointFixtures.ok
    let date (s: string) = DateTimeOffset.Parse(s, Globalization.CultureInfo.InvariantCulture)
    let hash bytes = SHA256.HashData(bytes: byte array) |> Convert.ToHexString |> _.ToLowerInvariant()
    let corpus () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "signed source corpus missing"
            let path = Path.Combine(directory.FullName,"contracts","portable-workspace","v1","receiver-checkpoint-v2-signed-source-journeys.json")
            if File.Exists path then System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(File.ReadAllBytes path)) else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory))
    let roles = ["original-intent",OriginalIntentSignature;"create-effect-grant",EffectGrantSignature;"receiver-command",ReceiverCommandSignature
                 "host-command-result",HostCommandResultSignature;"native-proof",NativeProofSignature;"creation",SupplierCreationSignature
                 "genesis",SupplierGenesisSignature;"supplier-event",SupplierEventSignature;"supplier-response",SupplierResponseSignature]
    let selection (root: System.Text.Json.JsonElement) (expected: System.Text.Json.JsonElement) (initial: PortableSupplierAdmissionState) at =
        let at = date at
        let policies = roles |> List.map (fun (name,role) ->
            let pin = expected.GetProperty("policyRoles").GetProperty(name)
            { Role = role; PublicKeyBlob = root.GetProperty("publicRolePins").GetProperty(name).GetProperty("publicKeyBlobBase64").GetString() |> Convert.FromBase64String
              Principal = pin.GetProperty("principal").GetString(); PolicyBytes = pin.GetProperty("policyBytesBase64").GetString() |> Convert.FromBase64String
              ExpectedPolicySha256 = pin.GetProperty("policySha256").GetString(); PolicyRevision = pin.GetProperty("policyRevision").GetString()
              ValidFrom = date (pin.GetProperty("policyValidFrom").GetString()); ValidUntil = date (pin.GetProperty("policyValidUntil").GetString())
              RevocationCheckedAt = at; Revocation = ControlledNotRevoked })
        let binding = { Command = initial.Original.Command; QualifiedImage = initial.Original.QualifiedImage
                        AllocationId = expected.GetProperty("hostBinding").GetProperty("allocationId").GetString()
                        WorkerId = expected.GetProperty("hostBinding").GetProperty("workerId").GetString()
                        AllocationReceiptDigest =
                            // Explicit controlled TEST allocation-binding bytes; no recipe is
                            // declared for the production field. In particular this is neither
                            // raw creation-receipt SHA nor fsgg.v3.creation-receipt/1.
                            Encoding.ASCII.GetBytes("fsgg.controlled-test-allocation-binding/1\n") |> hash }
        let domain = Guid.Parse(expected.GetProperty("receiverClockDomain").GetString())
        let current = { VerifiedAt = at; SelectionObservedAt = at; SelectionValidUntil = date (expected.GetProperty("selectionValidUntil").GetString())
                        MaximumObservationAge = TimeSpan.FromTicks(Int64.Parse(expected.GetProperty("maximumObservationAgeTicks").GetString()))
                        ReceiverMonotonic = 2L; IssuedReceiverClockDomain = domain; CurrentReceiverClockDomain = domain
                        HostClock = PortableSupplierOriginalNativeConfiguration.clock initial.CheckpointCustody.Value.NativeConfiguration }
        PortableSupplierAdmission.createControlledSourceSelection initial.Original (expected.GetProperty("originalIntentEnvelopeSha256").GetString())
            (Guid.Parse(expected.GetProperty("expectedOperationId").GetString())) binding initial.CheckpointCustody.Value.NativeConfiguration policies current |> ok

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``same-key composite preserves complete signed supplier and continuation with independent CAS``(differentDestination: bool) =
        use fixture = new HostExecutorFixture()
        use data = corpus ()
        let root = data.RootElement
        let row = root.GetProperty("outputJourneys").EnumerateArray() |> Seq.find (fun x -> x.GetProperty("name").GetString() = "selected-sixteen")
        let expected = root.GetProperty("controlledSelections").EnumerateArray() |> Seq.find (fun x -> x.GetProperty("name").GetString() = "selected-sixteen")
        let originalInitial = SupplierCheckpointFixtures.initialFrom row
        // Explicit fresh TEST configuration through the maintained constructor. C/R signed bytes
        // do not contain this independently selected native policy; they are reverified unchanged.
        let initial =
            if not differentDestination then originalInitial
            else
                let config = originalInitial.CheckpointCustody.Value.NativeConfiguration
                let destination = Encoding.ASCII.GetBytes "{\"disposition\":\"retain-outside-original-cleanup\",\"owner_gid\":\"1000\",\"owner_uid\":\"1000\",\"profile\":\"signed-hold-v1\",\"revision\":\"1\",\"role\":\"selected-output-destination\",\"root\":{\"device\":\"2\",\"filesystem_uuid\":\"11111111-1111-1111-1111-111111111111\",\"inode\":\"99\"},\"schema\":\"fsgg.v3.native-destination-policy/1\"}\n"
                let configured = PortableSupplierOriginalNativeConfiguration.create originalInitial.Original 1UL
                                     (PortableSupplierOriginalNativeConfiguration.scopeBytes config) (hash (PortableSupplierOriginalNativeConfiguration.scopeBytes config))
                                     (PortableSupplierOriginalNativeConfiguration.clockBytes config) (hash (PortableSupplierOriginalNativeConfiguration.clockBytes config))
                                     (Some(destination, hash destination)) |> ok
                PortableSupplierAdmission.beginIntentV4 originalInitial.Original
                    (Convert.FromBase64String originalInitial.CheckpointCustody.Value.OriginalIntentEnvelopeBase64) configured |> ok
        let journal = PortableWorkspaceExecutor.SupplierAdmissionJournal fixture.Policy.Runtime.StateRoot
        match journal.StageV4(initial,None,None) with SupplierSourceReadback _ -> () | x -> failwithf "%A" x
        let mutable state = initial
        for name in ["creation";"genesis"] do
            let pair = SupplierCheckpointFixtures.exchangeFrom row name state
            let pending = PortableSupplierAdmission.issueChallenge pair.Challenge state |> ok
            match journal.StageV4(pending,Some state.Revision,Some state.CheckpointCustody.Value.StoreFence) with SupplierSourceReadback _ -> () | x -> failwithf "%A" x
            let bytes key = row.GetProperty(name).GetProperty(key: string).GetString() |> SupplierCheckpointFixtures.urlBytes
            let exchange = if name = "creation" then ControlledCreationExchange(bytes "request",[bytes "result";bytes "evidence"],row.GetProperty("reservation").GetString() |> SupplierCheckpointFixtures.urlBytes)
                           else ControlledGenesisExchange(bytes "request",[bytes "result";bytes "evidence"])
            let current = selection root expected initial (expected.GetProperty("warmVerificationTimes").GetProperty(name).GetString())
            let accepted = PortableSupplierAdmission.verifyAndAcceptControlledV4 current pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending exchange |> ok
            let transition = match accepted with AuthenticatedControlledSource x -> x | HeldControlledSource x -> failwithf "held %A" x.FirstFailure
            match journal.StageControlledV4 transition with SupplierSourceReadback _ -> () | x -> failwithf "%A" x
            state <- transition.State
        let current = selection root expected initial (expected.GetProperty("warmVerificationTimes").GetProperty("genesis").GetString())
        use receipt = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(row.GetProperty("native").GetProperty("receipt").GetString() |> SupplierCheckpointFixtures.urlBytes))
        let manifest = receipt.RootElement.GetProperty("manifest_bytes").GetString() |> SupplierCheckpointFixtures.urlBytes
        let reservation = state.Pinned.Value.OriginalReservation
        let rootPin path device inode = { Path = path; Device = device; Inode = inode; OwnerUid = 1000UL; OwnerGid = 1000UL }
        let input = { ExportOperationId = Guid.Parse "99999999-9999-9999-9999-999999999999"; Reservation = reservation; ManifestBytes = manifest
                      Clock = { HostBootId = Guid.Parse "11111111-1111-1111-1111-111111111111"; AdmittedMonotonicNanoseconds = 2000000000UL
                                ExportDeadlineMonotonicNanoseconds = 170000000000UL; LeaseDeadlineMonotonicNanoseconds = 180000000000UL }
                      SourceRoot = rootPin "/controlled/original" 1UL 1UL; RetiringRoots = [rootPin "/controlled/original" 1UL 1UL]
                      DestinationRoot = rootPin "/controlled/destination" 2UL 99UL }
        let exportOriginal = PortableWorkspaceExecutor.HostExportSource.createOriginal input |> ok
        let owner = PortableWorkspaceExecutor.OriginalSourceJournal fixture.Policy.Runtime.StateRoot
        let path = Directory.GetFiles(Path.Combine(fixture.Policy.Runtime.StateRoot,"journal-v1"),"*.bin") |> Array.exactlyOne
        let before = File.ReadAllBytes path
        if differentDestination then
            // Each fault attempt owns a fresh private fixture. Post-rename ambiguity permits only
            // same-original cold read, never a resend inferred from old bytes or rollback.
            let cuts = [PortableWorkspaceExecutor.BeforeSourceWrite; PortableWorkspaceExecutor.BeforeSourceFileFlush
                        PortableWorkspaceExecutor.BeforeSourceReplacement; PortableWorkspaceExecutor.AfterSourceReplacementBeforeReadback
                        PortableWorkspaceExecutor.BeforeSourceDirectorySync; PortableWorkspaceExecutor.AfterSourceDirectorySyncBeforeReadback
                        PortableWorkspaceExecutor.BeforeSourceColdReopen; PortableWorkspaceExecutor.SimulatedSourceDirectorySyncError]
            for cut in cuts do
                use cutFixture = new HostExecutorFixture()
                let directory = Path.Combine(cutFixture.Policy.Runtime.StateRoot,"journal-v1")
                Directory.CreateDirectory directory |> ignore
                let record = Path.Combine(directory,Path.GetFileName path)
                File.WriteAllBytes(record,before)
                let failing = PortableWorkspaceExecutor.OriginalSourceJournal.CreateControlledFailureTest(cutFixture.Policy.Runtime.StateRoot,cut)
                Assert.True(failing.AttachControlled(current,state.Original,state.Revision,state.CheckpointCustody.Value.StoreFence,exportOriginal) |> Result.isError)
                let preRename = List.contains cut [PortableWorkspaceExecutor.BeforeSourceWrite; PortableWorkspaceExecutor.BeforeSourceFileFlush; PortableWorkspaceExecutor.BeforeSourceReplacement]
                if preRename then Assert.Equal<byte>(before,File.ReadAllBytes record)
                else
                    let reopened = PortableWorkspaceExecutor.OriginalSourceJournal(cutFixture.Policy.Runtime.StateRoot)
                    let partial = reopened.ReadControlled(current,state.Original,{ Revision = 1UL; Fence = 1UL }) |> ok
                    Assert.Equal(state,partial.Supplier)
                    match partial.NativeCustody with EvidenceUnknown _ -> () | _ -> failwith "fault readback upgraded durability/custody"
                Assert.Empty(Directory.GetFiles(directory,"*.tmp"))
                Assert.Equal(0,cutFixture.Runner.Runs)
        let attached = owner.AttachControlled(current,state.Original,state.Revision,state.CheckpointCustody.Value.StoreFence,exportOriginal)
        if not differentDestination then
            Assert.True(Result.isError attached)
            Assert.Equal<byte>(before,File.ReadAllBytes path)
        else
            let snapshot = attached |> ok
            Assert.Equal(state,snapshot.Supplier)
            match snapshot.NativeCustody with EvidenceUnknown _ -> () | _ -> failwith "source upgraded native custody"
            Assert.Equal<byte>(exportOriginal.CanonicalBytes,snapshot.Export.Value.Original.CanonicalBytes)
            Assert.Equal(1UL,snapshot.Cursor.Revision)
            let exact = File.ReadAllBytes path
            Assert.True(owner.AttachControlled(current,state.Original,state.Revision,state.CheckpointCustody.Value.StoreFence,exportOriginal) |> Result.isError)
            Assert.True(owner.ReadControlled(current,state.Original,{snapshot.Cursor with Fence = 2UL}) |> Result.isError)
            Assert.Equal<byte>(exact,File.ReadAllBytes path)
            let cold = owner.ReadControlled(current,state.Original,snapshot.Cursor) |> ok
            Assert.Equal(state,cold.Supplier)
            Assert.Equal<byte>(exportOriginal.CanonicalBytes,cold.Export.Value.Original.CanonicalBytes)
            // No authenticated termination exists in this original. Source cannot invent retention
            // or consume an attempt from writer exit, copied archive or manager-empty alone.
            Assert.True(owner.PrepareControlledReaderAttempt(current,state.Original,cold.Cursor,exportOriginal,input.Clock.HostBootId,7000000000UL) |> Result.isError)
            Assert.Equal<byte>(exact,File.ReadAllBytes path)
            let pair = SupplierCheckpointFixtures.exchangeFrom row "unchanged-genesis" state
            let pending = PortableSupplierAdmission.issueChallenge pair.Challenge state |> ok
            Assert.Equal(SupplierSourceConflict,journal.StageV4(pending,Some state.Revision,Some state.CheckpointCustody.Value.StoreFence))
            let at = expected.GetProperty("warmVerificationTimes").GetProperty("unchanged-genesis").GetString()
            let nextSelection = selection root expected initial at
            let nextCursor = owner.StageControlledPending(nextSelection,pending,state.Revision,state.CheckpointCustody.Value.StoreFence,cold.Cursor) |> ok
            let bytes key = row.GetProperty("unchanged-genesis").GetProperty(key: string).GetString() |> SupplierCheckpointFixtures.urlBytes
            let accepted = PortableSupplierAdmission.verifyAndAcceptControlledV4 nextSelection pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending
                               (ControlledOutputWireExchange(bytes "request",[bytes "result";bytes "evidence"])) |> ok
            let transition = match accepted with AuthenticatedControlledSource x -> x | HeldControlledSource x -> failwithf "held %A" x.FirstFailure
            let advanced = owner.StageControlledSupplier(nextSelection,transition,nextCursor) |> ok
            Assert.Equal(3UL,advanced.Cursor.Revision)
            Assert.Equal(transition.State,advanced.Supplier)
            Assert.Equal<byte>(exportOriginal.CanonicalBytes,advanced.Export.Value.Original.CanonicalBytes)
            Assert.Equal(None,advanced.Export.Value.AttemptBytes)
            Assert.True(PortableSupplierAdmission.prepareAck advanced.Supplier |> Result.isError)
            Assert.True(PortableSupplierAdmission.reserveHost advanced.Supplier |> Result.isError)
        Assert.Equal(1,Directory.GetFiles(Path.Combine(fixture.Policy.Runtime.StateRoot,"journal-v1"),"*.bin").Length)
        Assert.Equal(0,fixture.Runner.Runs)
        Assert.Equal(0,fixture.Runner.Cleans)
