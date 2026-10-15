namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open FS.GG.Coordination.Orchestration.Execution
open Xunit

module private HostFixtures =
    let start = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let at seconds = start.AddSeconds seconds
    let manifest = { Digest = "synthetic-manifest-a"; MemberCount = 2UL; TotalBytes = 12UL }
    let binding =
        {
            Command =
                {
                    CommandId = Guid.Parse "00000000-0000-0000-0000-000000000001"
                    IdempotencyId = "synthetic-operation"
                    WorkspaceScope = "synthetic-workspace"
                    ProfileId = "synthetic-profile"
                    ProfileRevision = 1UL
                    SourceRevision = "synthetic-source"
                    ExpectedWorkflowRevision = 2UL
                    FenceGeneration = 3UL
                    CausationId = None
                    Deadline = at 100.
                    Operation = "build"
                    ComponentId = None
                }
            QualifiedImage = "synthetic-image"
            AllocationId = "synthetic-allocation"
            WorkerId = "synthetic-worker"
            AllocationReceiptDigest = "synthetic-allocation-receipt"
        }
    let reservation =
        {
            Binding = binding
            AdmittedAt = start
            HostLeaseExpiresAt = at 90.
            MaximumRuntime = TimeSpan.FromSeconds 80.
            ExportReserve = TimeSpan.FromSeconds 10.
            CleanupReserve = TimeSpan.FromSeconds 10.
            Selection = SelectedOutputs manifest
        }
    let observation seconds value =
        { Binding = binding; CollectedAt = at seconds; ReceiptDigest = "synthetic-receipt"; Value = value }
    let complete =
        {
            PortableHostAllocation.emptyLifecycle with
                WorkloadExit = EvidenceKnown (observation 50. 0)
                Termination = EvidenceKnown (observation 50. ())
                Export = EvidenceKnown (observation 60. (DurableManifestReadback manifest))
                InnerCleanup = EvidenceKnown (observation 65. ())
                OuterRetirement = EvidenceKnown (observation 70. ())
        }
    let pendingDeletion =
        { complete with
            InnerCleanup = EvidenceMissing "inner cleanup not requested"
            OuterRetirement = EvidenceMissing "outer retirement not requested" }
    let pendingOuter = { complete with OuterRetirement = EvidenceMissing "outer retirement not requested" }

open HostFixtures

type PortableHostAllocationTests() =
    [<Fact>]
    member _.``execution retains both original bounds and reserves export and cleanup``() =
        Assert.Equal(Ok (at 70.), PortableHostAllocation.executionDeadline start reservation reservation)
        let shorterCommand = { reservation with Binding = { binding with Command = { binding.Command with Deadline = at 60. } } }
        Assert.Equal(Ok (at 40.), PortableHostAllocation.executionDeadline start shorterCommand shorterCommand)
        let runtime = { reservation with MaximumRuntime = TimeSpan.FromSeconds 5. }
        Assert.Equal(Ok (at 5.), PortableHostAllocation.executionDeadline start runtime runtime)

    [<Fact>]
    member _.``recovery cannot substitute any original binding or extend original limits``() =
        let commands =
            [ { binding.Command with CommandId = Guid.Parse "00000000-0000-0000-0000-000000000002" }
              { binding.Command with IdempotencyId = "other" }
              { binding.Command with WorkspaceScope = "other" }
              { binding.Command with ProfileId = "other" }
              { binding.Command with ProfileRevision = 2UL }
              { binding.Command with SourceRevision = "other" }
              { binding.Command with ExpectedWorkflowRevision = 4UL }
              { binding.Command with FenceGeneration = 4UL }
              { binding.Command with Deadline = at 110. } ]
        let bindings =
            [ yield! commands |> List.map (fun command -> { binding with Command = command })
              { binding with AllocationId = "other" }
              { binding with WorkerId = "other" }
              { binding with QualifiedImage = "other" }
              { binding with AllocationReceiptDigest = "other" } ]
        let candidates =
            [ yield! bindings |> List.map (fun candidate -> { reservation with Binding = candidate })
              { reservation with AdmittedAt = at 1. }
              { reservation with HostLeaseExpiresAt = at 120. }
              { reservation with ExportReserve = TimeSpan.FromSeconds 1. }
              { reservation with CleanupReserve = TimeSpan.FromSeconds 1. }
              { reservation with MaximumRuntime = TimeSpan.FromSeconds 100. }
              { reservation with Selection = NoSelectedOutputs } ]
        for candidate in candidates do
            Assert.True(PortableHostAllocation.executionDeadline start reservation candidate |> Result.isError)

    [<Fact>]
    member _.``expired invalid and overflowing intervals refuse without renewal``() =
        for now in [ at -1.; at 70.; at 90.; at 100. ] do
            Assert.True(PortableHostAllocation.executionDeadline now reservation reservation |> Result.isError)
        let invalid =
            [ { reservation with HostLeaseExpiresAt = start }
              { reservation with CleanupReserve = TimeSpan.FromSeconds 80. }
              { reservation with ExportReserve = TimeSpan.MaxValue }
              { reservation with MaximumRuntime = TimeSpan.Zero }
              { reservation with ExportReserve = TimeSpan.Zero }
              { reservation with CleanupReserve = TimeSpan.FromSeconds -1. }
              { reservation with Selection = SelectedOutputs { manifest with MemberCount = 0UL } }
              { reservation with Binding = { binding with WorkerId = "" } } ]
        for candidate in invalid do
            Assert.True(PortableHostAllocation.executionDeadline start candidate candidate |> Result.isError)

    [<Fact>]
    member _.``complete manifest readback gates both cleanup layers and independent release``() =
        let decision = PortableHostAllocation.retirement (at 70.) reservation complete
        Assert.NotEqual(HostEligible, decision.OutputDeletion)
        Assert.NotEqual(HostEligible, decision.OuterCleanup)
        Assert.Equal(EvidenceKnown (), decision.ReservationRelease)
        for lifecycle in
            [ PortableHostAllocation.reconcileLifecycle complete PortableHostAllocation.emptyLifecycle
              PortableHostAllocation.reconcileLifecycle PortableHostAllocation.emptyLifecycle complete ] do
            let repeated = PortableHostAllocation.retirement (at 70.) reservation lifecycle
            Assert.NotEqual(HostEligible, repeated.OutputDeletion)
            Assert.NotEqual(HostEligible, repeated.OuterCleanup)
            Assert.Equal(EvidenceKnown (), repeated.ReservationRelease)
        let pending = PortableHostAllocation.retirement (at 70.) reservation pendingDeletion
        Assert.Equal(HostEligible, pending.OutputDeletion)
        Assert.NotEqual(HostEligible, pending.OuterCleanup)
        Assert.NotEqual(EvidenceKnown (), pending.ReservationRelease)
        let outer = PortableHostAllocation.retirement (at 70.) reservation pendingOuter
        Assert.NotEqual(HostEligible, outer.OutputDeletion)
        Assert.Equal(HostEligible, outer.OuterCleanup)
        Assert.NotEqual(EvidenceKnown (), outer.ReservationRelease)

    [<Fact>]
    member _.``partial mismatched late future and unbound export never permits deletion``() =
        let exports =
            [ EvidenceMissing "destination ready only"
              EvidenceUnknown "transfer incomplete"
              EvidenceKnown (observation 60. (DurableManifestReadback { manifest with Digest = "other" }))
              EvidenceKnown (observation 60. (DurableManifestReadback { manifest with MemberCount = 1UL }))
              EvidenceKnown (observation 60. (DurableManifestReadback { manifest with TotalBytes = 11UL }))
              EvidenceKnown (observation 90. (DurableManifestReadback manifest))
              EvidenceKnown (observation 101. (DurableManifestReadback manifest))
              EvidenceKnown (observation 71. (DurableManifestReadback manifest))
              EvidenceKnown { observation 60. (DurableManifestReadback manifest) with Binding = { binding with AllocationId = "other" } }
              EvidenceKnown { observation 60. (DurableManifestReadback manifest) with ReceiptDigest = "" }
              EvidenceKnown (observation 60. HostCensusConfirmsZeroSelection) ]
        for export in exports do
            let decision = PortableHostAllocation.retirement (at 70.) reservation { pendingDeletion with Export = export }
            Assert.NotEqual(HostEligible, decision.OutputDeletion)
            let outer = PortableHostAllocation.retirement (at 70.) reservation { pendingOuter with Export = export }
            Assert.NotEqual(HostEligible, outer.OuterCleanup)
            let closure = PortableHostAllocation.retirement (at 70.) reservation { complete with Export = export }
            Assert.NotEqual(EvidenceKnown (), closure.ReservationRelease)

    [<Fact>]
    member _.``zero selection requires independent host census``() =
        let zero = { reservation with Selection = NoSelectedOutputs }
        let pending = PortableHostAllocation.retirement (at 70.) zero PortableHostAllocation.emptyLifecycle
        Assert.NotEqual(HostEligible, pending.OutputDeletion)
        let census = { pendingDeletion with Export = EvidenceKnown (observation 60. HostCensusConfirmsZeroSelection) }
        Assert.Equal(HostEligible, (PortableHostAllocation.retirement (at 70.) zero census).OutputDeletion)
        Assert.NotEqual(HostEligible, (PortableHostAllocation.retirement (at 70.) zero pendingDeletion).OutputDeletion)

    [<Fact>]
    member _.``worker exit inner cleanup and expired lease cannot establish outer retirement``() =
        for receipt in [ EvidenceMissing "not requested"; EvidenceUnknown "worker lost; completion receipt missing" ] do
            let lifecycle = { complete with OuterRetirement = receipt }
            for now in [ at 70.; at 120. ] do
                match (PortableHostAllocation.retirement now reservation lifecycle).ReservationRelease with
                | EvidenceUnknown _ -> ()
                | other -> failwithf "Expected unresolved original reservation, got %A" other

    [<Fact>]
    member _.``cleanup needs time and termination but late retirement readback does not renew admission``() =
        for now in [ at 81.; at 90.; at 120. ] do
            Assert.NotEqual(HostEligible, (PortableHostAllocation.retirement now reservation pendingDeletion).OutputDeletion)
            Assert.NotEqual(HostEligible, (PortableHostAllocation.retirement now reservation pendingOuter).OuterCleanup)
        let pending = { pendingDeletion with Termination = EvidenceUnknown "exit is not termination" }
        Assert.NotEqual(HostEligible, (PortableHostAllocation.retirement (at 70.) reservation pending).OutputDeletion)
        let late = { complete with OuterRetirement = EvidenceKnown (observation 120. ()) }
        let decision = PortableHostAllocation.retirement (at 120.) reservation late
        Assert.NotEqual(HostEligible, decision.OuterCleanup)
        Assert.Equal(EvidenceKnown (), decision.ReservationRelease)

    [<Fact>]
    member _.``cleanup before export or outer retirement before inner cleanup remains unresolved``() =
        for lifecycle in
            [ { complete with InnerCleanup = EvidenceKnown (observation 59. ()) }
              { complete with OuterRetirement = EvidenceKnown (observation 64. ()) }
              { complete with Termination = EvidenceKnown (observation 66. ()) }
              { complete with OuterRetirement = EvidenceKnown { observation 70. () with Binding = { binding with WorkerId = "other" } } } ] do
            Assert.NotEqual(EvidenceKnown (), (PortableHostAllocation.retirement (at 70.) reservation lifecycle).ReservationRelease)

    [<Fact>]
    member _.``duplicates preserve evidence conflicts retain uncertainty and first failure survives success``() =
        Assert.Equal(complete, PortableHostAllocation.reconcileLifecycle complete complete)
        let conflict = { pendingDeletion with Export = EvidenceKnown (observation 61. (DurableManifestReadback manifest)) }
        let merged = PortableHostAllocation.reconcileLifecycle pendingDeletion conflict
        let reordered = PortableHostAllocation.reconcileLifecycle conflict pendingDeletion
        Assert.Equal(merged.Export, reordered.Export)
        let recovered = PortableHostAllocation.reconcileLifecycle merged pendingDeletion
        Assert.NotEqual(HostEligible, (PortableHostAllocation.retirement (at 70.) reservation recovered).OutputDeletion)
        let failed = { complete with FirstFailure = Some "first failure" }
        let retained = PortableHostAllocation.reconcileLifecycle failed { complete with FirstFailure = Some "later failure" }
        Assert.Equal(Some "first failure", retained.FirstFailure)
        Assert.NotEqual(HostEligible, (PortableHostAllocation.retirement (at 70.) reservation retained).OutputDeletion)
        Assert.Equal(EvidenceKnown (), (PortableHostAllocation.retirement (at 70.) reservation retained).ReservationRelease)
        let failedBeforeCleanup = { pendingDeletion with FirstFailure = retained.FirstFailure }
        Assert.Equal(HostEligible, (PortableHostAllocation.retirement (at 70.) reservation failedBeforeCleanup).OutputDeletion)

    [<Fact>]
    member _.``original missing observations can settle without treating first uncertainty as a permanent conflict``() =
        let settled = PortableHostAllocation.reconcileLifecycle PortableHostAllocation.emptyLifecycle complete
        Assert.Equal(complete, settled)
        Assert.Equal(EvidenceKnown (), (PortableHostAllocation.retirement (at 70.) reservation settled).ReservationRelease)

    [<Fact>]
    member _.``unknown original cleanup effects block replay and missing updates cannot clear them``() =
        let uncertainInner = { pendingDeletion with InnerCleanup = EvidenceUnknown "request sent; response lost" }
        let uncertainOuter = { pendingOuter with OuterRetirement = EvidenceUnknown "request sent; response lost" }
        let uncertainOuterBeforeInner = { pendingDeletion with OuterRetirement = EvidenceUnknown "outer effect may have happened" }
        for original in [ uncertainInner; uncertainOuter; uncertainOuterBeforeInner ] do
            for lifecycle in [ original; PortableHostAllocation.reconcileLifecycle original PortableHostAllocation.emptyLifecycle;
                               PortableHostAllocation.reconcileLifecycle PortableHostAllocation.emptyLifecycle original ] do
                let decision = PortableHostAllocation.retirement (at 70.) reservation lifecycle
                Assert.NotEqual(HostEligible, decision.OutputDeletion)
                Assert.NotEqual(HostEligible, decision.OuterCleanup)
                Assert.NotEqual(EvidenceKnown (), decision.ReservationRelease)
        // Original-identity readback can settle an uncertain effect; it grants no replay.
        let observed = PortableHostAllocation.reconcileLifecycle uncertainOuter complete
        let settled = PortableHostAllocation.retirement (at 70.) reservation observed
        Assert.NotEqual(HostEligible, settled.OutputDeletion)
        Assert.NotEqual(HostEligible, settled.OuterCleanup)
        Assert.Equal(EvidenceKnown (), settled.ReservationRelease)

    [<Fact>]
    member _.``future past unbound and reordered cleanup receipts never become retry permission``() =
        let invalidInner =
            [ observation 71. (); observation -1. (); observation 59. ();
              { observation 65. () with Binding = { binding with AllocationId = "other" } };
              { observation 65. () with ReceiptDigest = "" } ]
        for receipt in invalidInner do
            let lifecycle = { pendingOuter with InnerCleanup = EvidenceKnown receipt }
            let decision = PortableHostAllocation.retirement (at 70.) reservation lifecycle
            Assert.NotEqual(HostEligible, decision.OutputDeletion)
            Assert.NotEqual(HostEligible, decision.OuterCleanup)
            Assert.NotEqual(EvidenceKnown (), decision.ReservationRelease)
        let invalidOuter =
            [ observation 71. (); observation -1. (); observation 64. ();
              { observation 70. () with Binding = { binding with WorkerId = "other" } };
              { observation 70. () with ReceiptDigest = "" } ]
        for receipt in invalidOuter do
            for basis in [ complete; pendingDeletion ] do
                let lifecycle = { basis with OuterRetirement = EvidenceKnown receipt }
                let decision = PortableHostAllocation.retirement (at 70.) reservation lifecycle
                Assert.NotEqual(HostEligible, decision.OutputDeletion)
                Assert.NotEqual(HostEligible, decision.OuterCleanup)
                Assert.NotEqual(EvidenceKnown (), decision.ReservationRelease)

    [<Fact>]
    member _.``future or pre-admission termination cannot authorize either next cleanup action``() =
        for receipt in [ observation 71. (); observation -1. () ] do
            let deletion = PortableHostAllocation.retirement (at 70.) reservation { pendingDeletion with Termination = EvidenceKnown receipt }
            let outer = PortableHostAllocation.retirement (at 70.) reservation { pendingOuter with Termination = EvidenceKnown receipt }
            Assert.NotEqual(HostEligible, deletion.OutputDeletion)
            Assert.NotEqual(HostEligible, outer.OuterCleanup)
            Assert.NotEqual(EvidenceKnown (), outer.ReservationRelease)
