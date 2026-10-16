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
