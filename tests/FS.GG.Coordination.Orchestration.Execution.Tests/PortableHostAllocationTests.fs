namespace FS.GG.Coordination.Orchestration.Execution.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
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
    member _.``supplier sizing matches shared cross-language exact-byte boundary vectors``() =
        // These are source qualification fixtures, not installed/native acceptance evidence.
        let path = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "contracts", "portable-workspace", "v1", "supplier-evidence-sizing-vectors.json")
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let b64 size byteValue =
            Convert.ToBase64String(Array.create size byteValue).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        for row in document.RootElement.GetProperty("cases").EnumerateArray() do
            let number (name: string) = row.GetProperty(name).GetInt32()
            let kind = row.GetProperty("kind").GetString()
            let events () = row.GetProperty("events").EnumerateArray() |> Seq.map _.GetInt32() |> Seq.toList
            let result =
                match kind with
                | "base64" -> PortableSupplierProtocol.base64UrlBytes (number "raw")
                | "envelope" -> PortableSupplierProtocol.envelopeBytes (number "payload") (number "signature")
                | "genesis" -> PortableSupplierProtocol.genesisBytes (number "creation") (number "reservation") (number "overhead")
                | "response" -> PortableSupplierProtocol.responseBytes (row.GetProperty("genesis").GetBoolean()) (number "metadata") (events ())
                | _ -> failwith "unknown shared vector kind"
            if row.GetProperty("expected").ValueKind = JsonValueKind.Null then
                Assert.True(Result.isError result, row.GetProperty("name").GetString())
            else
                let actual = match result with Ok value -> value | Error reason -> failwith reason
                Assert.Equal(number "expected", actual)
                if kind = "envelope" then
                    let wire = sprintf "{\"payload\":\"%s\",\"signature\":\"%s\"}\n" (b64 (number "payload") 120uy) (b64 (number "signature") 115uy)
                    let bytes = Encoding.ASCII.GetBytes wire
                    Assert.Equal(actual, bytes.Length)
                    Assert.Equal(row.GetProperty("wireSha256").GetString(), Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant())
                elif kind = "response" then
                    let encodedEvents = events () |> List.map (fun length -> "\"" + b64 length 101uy + "\"") |> String.concat ","
                    let wire = "{\"events\":[" + encodedEvents + "],\"m\":\"" + String.replicate (number "metadata" - 9) "x" + "\"}\n"
                    Assert.Equal(actual, Encoding.ASCII.GetByteCount wire)
                    Assert.True(PortableSupplierProtocol.envelopeBytes actual PortableSupplierProtocol.MaximumSignatureBytes |> Result.isOk)

    [<Fact>]
    member _.``supplier effects require their own exact unconsumed grants and isolated hold profile``() =
        let state =
            { Profile = SignedHoldV1Profile; IntentDigest = "intent"; ReservationDigest = None; OperationId = None
              OriginalDeadline = at 90.; DurableIntent = true; DurableOriginals = false
              StoppedAndIdentityUnchanged = false; SourcesUnchanged = true
              DurableAuthenticatedReceiverAcceptance = false; LaunchIntentOrStartAlreadyRecorded = false
              OriginalWorkAndReservesFit = true; CapacityReserved = true; ProtocolFits = true; Retirement = None }
        let grant effect (selected: PortableSupplierEffectState) =
            { GrantId = "exact-one-use"; Effect = effect; IntentDigest = selected.IntentDigest
              ReservationDigest = selected.ReservationDigest; OperationId = selected.OperationId
              NotBefore = start; NotAfter = at 80.; ConsumedOrUncertain = false }
        let admit effect selected admission = PortableSupplierEffectPolicy.admit (at 10.) effect selected admission
        Assert.Equal(HostEligible, admit CreateStoppedAllocation state (EvidenceKnown(grant CreateStoppedAllocation state)))
        for changed in [ { state with Profile = LegacyInstalledProfile }; { state with DurableIntent = false }
                         { state with ProtocolFits = false }; { state with CapacityReserved = false } ] do
            Assert.NotEqual(HostEligible, admit CreateStoppedAllocation changed (EvidenceKnown(grant CreateStoppedAllocation changed)))
        let prepared =
            { state with ReservationDigest = Some "reservation"; OperationId = Some "operation"; DurableOriginals = true
                         StoppedAndIdentityUnchanged = true; DurableAuthenticatedReceiverAcceptance = true }
        Assert.NotEqual(HostEligible, admit StartOriginalAllocation prepared (EvidenceMissing "ACK is not effect admission"))
        Assert.Equal(HostEligible, admit StartOriginalAllocation prepared (EvidenceKnown(grant StartOriginalAllocation prepared)))
        for changed in [ { prepared with DurableAuthenticatedReceiverAcceptance = false }; { prepared with SourcesUnchanged = false }
                         { prepared with LaunchIntentOrStartAlreadyRecorded = true }; { prepared with OriginalWorkAndReservesFit = false } ] do
            Assert.NotEqual(HostEligible, admit StartOriginalAllocation changed (EvidenceKnown(grant StartOriginalAllocation changed)))
        let retiring = { prepared with Retirement = Some(PortableHostAllocation.retirement (at 70.) reservation pendingOuter) }
        Assert.Equal(HostEligible, admit RetireOriginalAllocation retiring (EvidenceKnown(grant RetireOriginalAllocation retiring)))
        for changedGrant in [ grant StartOriginalAllocation retiring
                              { grant RetireOriginalAllocation retiring with ConsumedOrUncertain = true }
                              { grant RetireOriginalAllocation retiring with OperationId = Some "other" }
                              { grant RetireOriginalAllocation retiring with NotAfter = at 10. } ] do
            Assert.NotEqual(HostEligible, admit RetireOriginalAllocation retiring (EvidenceKnown changedGrant))
        Assert.NotEqual(HostEligible, admit RetireOriginalAllocation prepared (EvidenceKnown(grant RetireOriginalAllocation prepared)))
        Assert.True(PortableSupplierEffectPolicy.settle SignedHoldV1Profile |> Result.isOk)
        Assert.True(PortableSupplierEffectPolicy.settle LegacyInstalledProfile |> Result.isError)

    [<Fact>]
    member _.``supplier ingress authenticates bytes before admitting exact original pins``() =
        let bytes = Encoding.UTF8.GetBytes "controlled authenticated host readback"
        let identity = { Issuer = "controlled-host"; ContainerId = "controlled-cid"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let evidence =
            { Identity = identity; Reservation = reservation; CollectedAt = at 10.
              ReceiptDigest = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
              Lifecycle = PortableHostAllocation.emptyLifecycle }
        let verifier = { new IPortableHostSupplierVerifier with member _.Authenticate _ = Ok evidence }
        let admit selected pins input now =
            PortableHostSupplierIngress.authenticate selected pins reservation now (TimeSpan.FromSeconds 5.) input
        let verified = admit verifier identity bytes (at 12.)
        Assert.True(Result.isOk verified)
        match verified with
        | Ok value ->
            Assert.Equal(evidence, value.Evidence)
            Assert.True(value.IsCurrent(at 15.))
            Assert.False(value.IsCurrent(at 16.))
            Assert.False(value.IsCurrent(at 11.))
        | Error reason -> failwith reason
        let rejecting = { new IPortableHostSupplierVerifier with member _.Authenticate _ = Error "unsealed status" }
        let throwing = { new IPortableHostSupplierVerifier with member _.Authenticate _ = failwith "transport lost" }
        for selected in [ rejecting; throwing ] do
            Assert.True(admit selected identity bytes (at 12.) |> Result.isError)
        for pins in
            [ { identity with Issuer = "other" }; { identity with ContainerId = "other" }
              { identity with AllocationDevice = 6UL }; { identity with AllocationInode = 18UL } ] do
            Assert.True(admit verifier pins bytes (at 12.) |> Result.isError)
        for input in [ Array.empty; Array.create 65537 0uy; Encoding.UTF8.GetBytes "changed" ] do
            Assert.True(admit verifier identity input (at 12.) |> Result.isError)
        for now in [ at 9.; at 16. ] do
            Assert.True(admit verifier identity bytes now |> Result.isError)
        for changed in
            [ { evidence with Reservation = { reservation with HostLeaseExpiresAt = at 100. } }
              { evidence with Lifecycle = { evidence.Lifecycle with Termination = EvidenceKnown(observation 11. ()) } }
              { evidence with Lifecycle = { evidence.Lifecycle with Termination = EvidenceKnown { observation 9. () with Binding = { binding with WorkerId = "other" } } } } ] do
            let substituted = { new IPortableHostSupplierVerifier with member _.Authenticate _ = Ok changed }
            Assert.True(admit substituted identity bytes (at 12.) |> Result.isError)

    [<Fact>]
    member _.``supplier verifier cannot mutate caller bytes to change the authenticated digest``() =
        let bytes = Encoding.UTF8.GetBytes "original"
        let original = Array.copy bytes
        let identity = { Issuer = "controlled-host"; ContainerId = "controlled-cid"; AllocationDevice = 5UL; AllocationInode = 17UL }
        let verifier =
            { new IPortableHostSupplierVerifier with
                member _.Authenticate input =
                    input[0] <- 0uy
                    Ok { Identity = identity; Reservation = reservation; CollectedAt = at 1.
                         ReceiptDigest = Convert.ToHexString(SHA256.HashData input).ToLowerInvariant()
                         Lifecycle = PortableHostAllocation.emptyLifecycle } }
        Assert.True(PortableHostSupplierIngress.authenticate verifier identity reservation (at 1.) (TimeSpan.FromSeconds 5.) bytes |> Result.isError)
        Assert.True(bytes = original)

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
