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
        let path = Path.Combine(AppContext.BaseDirectory, "Contracts", "supplier-evidence-sizing-vectors.json")
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
        Assert.True((bytes = original))

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

// Synthetic trusted adapter is confined to this test assembly. No signer, process or host exists.
module SupplierAdmissionFixtures =
    let ok = function Ok value -> value | Error reason -> failwith reason
    let at = HostFixtures.at
    let b64 (value: string) = Encoding.ASCII.GetBytes value |> Convert.ToBase64String
    let domain kind (value: string) =
        Array.append (Encoding.ASCII.GetBytes("fsgg.v3." + kind + "/1\000")) (Encoding.ASCII.GetBytes value)
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let originalFor command image : PortableSupplierAdmissionOriginal =
        { Command = command; QualifiedImage = image; IntentBase64 = b64 "synthetic-original-intent"
          IntentDigest = domain "intent" "synthetic-original-intent"; TrustPolicyDigest = String.replicate 64 "a"
          SupplierSourceRevision = String.replicate 40 "b"; ReceiverEpoch = Guid.NewGuid() }
    let original () = originalFor HostFixtures.binding.Command HostFixtures.binding.QualifiedImage
    let initial () = original () |> PortableSupplierAdmission.beginIntent |> ok
    let challenge (state: PortableSupplierAdmissionState) =
        let bytes = "synthetic-challenge-" + Guid.NewGuid().ToString("D")
        { ChallengeId = Guid.NewGuid(); Digest = domain "challenge" bytes; BytesBase64 = b64 bytes
          Purpose = if state.Phase = SupplierIntentStored then "creation" elif state.Head.IsNone then "genesis-readback" else "state-readback"
          ReceiverEpoch = state.Original.ReceiverEpoch; IssuedAt = at 1.; NotAfter = at 30.
          IssuedMonotonic = 100L; DeadlineMonotonic = 1000L; ExpectedHead = state.Head }
    let pending state = PortableSupplierAdmission.issueChallenge (challenge state) state |> ok
    let observation kind (state: PortableSupplierAdmissionState) : PortableSupplierTranscriptObservation =
        let challenge = match state.Challenge.Value with WireV1 value -> value | _ -> failwith "wire fixture required"
        let event = "synthetic-event-" + Guid.NewGuid().ToString("D")
        { Kind = kind; IntentDigest = state.Original.IntentDigest; TrustPolicyDigest = state.Original.TrustPolicyDigest
          ChallengeDigest = challenge.Digest; Purpose = challenge.Purpose
          OriginalReservation = { HostFixtures.reservation with Binding = { HostFixtures.binding with Command = state.Original.Command; QualifiedImage = state.Original.QualifiedImage } }
          Identity = { Issuer = "controlled-fake-only"; ContainerId = "synthetic-original-cid"; AllocationDevice = 2UL; AllocationInode = 3UL }
          OuterImageId = String.replicate 64 "c"; SupplierSourceRevision = state.Original.SupplierSourceRevision
          CreationBase64 = b64 "synthetic-core"; CreationReceiptBase64 = b64 "synthetic-creation-receipt"
          ReservationBase64 = b64 "synthetic-reservation"
          ResponseBase64 = b64 ("synthetic-response-" + challenge.ChallengeId.ToString("D"))
          EventsBase64 = if kind = SupplierCreation then [] elif kind = SupplierUnchanged then [List.last state.EventsBase64] else [b64 event]
          FromHead = state.Head
          ToHead = if kind = SupplierCreation then None elif kind = SupplierUnchanged then state.Head else Some(domain "signed-envelope" event)
          Sequence = if kind = SupplierAppend then state.Sequence + 1UL else state.Sequence
          CollectedAt = at 2. }
    let verify value state =
        let adapter = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok value }
        let raw = Convert.FromBase64String(if value.Kind = SupplierCreation then value.CreationReceiptBase64 else value.ResponseBase64)
        PortableSupplierAdmission.verify adapter (at 3.) 200L state raw |> ok
    let accept kind state =
        let pending = pending state
        PortableSupplierAdmission.accept (verify (observation kind pending) pending) pending |> ok
    let genesis () = initial () |> accept SupplierCreation |> accept SupplierGenesis

type PortableHostAllocationTests_SupplierAdmission() =
    [<Fact>]
    member _.``creation and genesis source transitions preserve independent originals without effects``() =
        let original = SupplierAdmissionFixtures.initial ()
        let created = SupplierAdmissionFixtures.accept SupplierCreation original
        let genesis = SupplierAdmissionFixtures.accept SupplierGenesis created
        Assert.Equal(original.Original, genesis.Original)
        Assert.Equal(SupplierGenesisAccepted, genesis.Phase)
        Assert.Equal(2, genesis.ChallengeLedger.Length)
        Assert.Equal(1, genesis.EventsBase64.Length)
        Assert.True(PortableSupplierAdmission.prepareAck genesis |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost genesis |> Result.isError)

    [<Fact>]
    member _.``fresh outer unchanged response consumes new challenge and preserves exact nested event``() =
        let original = SupplierAdmissionFixtures.genesis ()
        let fresh = SupplierAdmissionFixtures.accept SupplierUnchanged original
        Assert.Equal(original.Head, fresh.Head)
        Assert.Equal<string list>(original.EventsBase64, fresh.EventsBase64)
        Assert.NotEqual<string>(List.last original.ResponsesBase64, List.last fresh.ResponsesBase64)
        Assert.Equal(original.ChallengeLedger.Length + 1, fresh.ChallengeLedger.Length)
        let pending = SupplierAdmissionFixtures.pending fresh
        let altered = { SupplierAdmissionFixtures.observation SupplierUnchanged pending with EventsBase64 = [SupplierAdmissionFixtures.b64 "different-signature"] }
        let held = PortableSupplierAdmission.accept (SupplierAdmissionFixtures.verify altered pending) pending |> SupplierAdmissionFixtures.ok
        Assert.True(held.FirstFailure.IsSome)
        Assert.Equal(fresh.Head, held.Head)
        Assert.Equal<string list>(fresh.EventsBase64, held.EventsBase64)

    [<Fact>]
    member _.``verified result cannot commit after revision challenge or original pins change``() =
        let pending = SupplierAdmissionFixtures.initial () |> SupplierAdmissionFixtures.pending
        let value = SupplierAdmissionFixtures.observation SupplierCreation pending
        let verified = SupplierAdmissionFixtures.verify value pending
        Assert.True(PortableSupplierAdmission.accept verified { pending with Revision = pending.Revision + 1UL } |> Result.isError)
        let next = PortableSupplierAdmission.accept verified pending |> SupplierAdmissionFixtures.ok
        Assert.True(PortableSupplierAdmission.accept verified next |> Result.isError)
        let after = SupplierAdmissionFixtures.pending next
        let changed = { SupplierAdmissionFixtures.observation SupplierGenesis after with Identity = { value.Identity with AllocationInode = 99UL } }
        let held = PortableSupplierAdmission.accept (SupplierAdmissionFixtures.verify changed after) after |> SupplierAdmissionFixtures.ok
        Assert.True(held.FirstFailure.IsSome)
        Assert.True(held.Conflicts.Count > 0)
        Assert.Equal(next.Pinned.Value.Identity, held.Pinned.Value.Identity)

    [<Fact>]
    member _.``trusted adapter buffer changes exceptions and wrong original projection refuse``() =
        let pending = SupplierAdmissionFixtures.initial () |> SupplierAdmissionFixtures.pending
        let value = SupplierAdmissionFixtures.observation SupplierCreation pending
        let original = Convert.FromBase64String value.CreationReceiptBase64
        let expected = Array.copy original
        let adapter = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate bytes = bytes[0] <- 0uy; Ok value }
        Assert.True(PortableSupplierAdmission.verify adapter (SupplierAdmissionFixtures.at 3.) 200L pending original |> Result.isOk)
        Assert.True((original = expected))
        let throws = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = failwith "controlled fake failure" }
        Assert.True(PortableSupplierAdmission.verify throws (SupplierAdmissionFixtures.at 3.) 200L pending original |> Result.isError)
        for changed in [ { value with OuterImageId = "sha256:" + value.OuterImageId }
                         { value with TrustPolicyDigest = String.replicate 64 "d" }
                         { value with IntentDigest = String.replicate 64 "d" }
                         { value with OriginalReservation = { value.OriginalReservation with Binding = { value.OriginalReservation.Binding with Command = { pending.Original.Command with FenceGeneration = 999UL } } } } ] do
            let fake = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok changed }
            Assert.True(PortableSupplierAdmission.verify fake (SupplierAdmissionFixtures.at 3.) 200L pending original |> Result.isError)

    [<Fact>]
    member _.``restart abandons original monotonic challenge and does not rewrite original intent bytes``() =
        let state = SupplierAdmissionFixtures.initial () |> SupplierAdmissionFixtures.pending
        let verified = SupplierAdmissionFixtures.verify (SupplierAdmissionFixtures.observation SupplierCreation state) state
        let restarted = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) state |> SupplierAdmissionFixtures.ok
        Assert.Equal(state.Original.IntentBase64, restarted.Original.IntentBase64)
        Assert.Equal(state.Original.Command, restarted.Original.Command)
        Assert.Equal("abandoned", restarted.ChallengeLedger.Head |> fun (_, _, _, value) -> value)
        Assert.True(PortableSupplierAdmission.accept verified restarted |> Result.isError)
        let fresh = SupplierAdmissionFixtures.pending restarted
        let fake = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok(SupplierAdmissionFixtures.observation SupplierCreation fresh) }
        let bytes = Convert.FromBase64String (SupplierAdmissionFixtures.observation SupplierCreation fresh).CreationReceiptBase64
        Assert.True(PortableSupplierAdmission.verify fake (SupplierAdmissionFixtures.at 3.) 1000L fresh bytes |> Result.isError)

    [<Fact>]
    member _.``capacity overflow and gap retain first failure and original head``() =
        let original = SupplierAdmissionFixtures.genesis ()
        let pending = SupplierAdmissionFixtures.pending original
        let fork = { SupplierAdmissionFixtures.observation SupplierAppend pending with FromHead = Some(String.replicate 64 "e") }
        let held = PortableSupplierAdmission.accept (SupplierAdmissionFixtures.verify fork pending) pending |> SupplierAdmissionFixtures.ok
        Assert.Equal(original.Head, held.Head)
        Assert.True(held.FirstFailure.IsSome)
        Assert.True(PortableSupplierAdmission.issueChallenge (SupplierAdmissionFixtures.challenge held) held |> Result.isError)
        Assert.True(PortableSupplierAdmission.validate { original with EventsBase64 = List.replicate 1025 (SupplierAdmissionFixtures.b64 "x") } |> Result.isError)
        Assert.True(PortableSupplierAdmission.validate { original with ChallengeLedger = List.replicate 129 (SupplierWireV1, Guid.NewGuid(), String.replicate 64 "a", "consumed") } |> Result.isError)
        Assert.True(PortableSupplierAdmission.issueChallenge (SupplierAdmissionFixtures.challenge original) { original with Revision = UInt64.MaxValue } |> Result.isError)

    [<Fact>]
    member _.``retained transcript byte boundary refuses one extra byte without truncating originals``() =
        let original = SupplierAdmissionFixtures.initial ()
        let intentLength = Convert.FromBase64String(original.Original.IntentBase64).Length
        let remaining = PortableSupplierAdmission.MaximumTranscriptBytes - intentLength
        let bytes n = Array.create<byte> n 120uy |> Convert.ToBase64String
        let full, remainder = remaining / 65536, remaining % 65536
        let responses = List.replicate full (bytes 65536) @ [bytes remainder]
        let boundary = { original with ResponsesBase64 = responses }
        Assert.True(PortableSupplierAdmission.validate boundary |> Result.isOk)
        let overflow = { boundary with ResponsesBase64 = List.take full responses @ [bytes (remainder + 1)] }
        Assert.True(PortableSupplierAdmission.validate overflow |> Result.isError)
        Assert.Equal(original.Original, overflow.Original)

    [<Fact>]
    member _.``purpose and kind bind to pending challenge and implemented source phase``() =
        let pending = SupplierAdmissionFixtures.initial () |> SupplierAdmissionFixtures.pending
        let value = SupplierAdmissionFixtures.observation SupplierCreation pending
        let raw = Convert.FromBase64String value.CreationReceiptBase64
        for changed in [ { value with Purpose = "state-readback" }; { value with Kind = SupplierUnchanged } ] do
            let adapter = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok changed }
            Assert.True(PortableSupplierAdmission.verify adapter (SupplierAdmissionFixtures.at 3.) 200L pending raw |> Result.isError)
        let genesis = SupplierAdmissionFixtures.genesis () |> SupplierAdmissionFixtures.pending
        let unchanged = SupplierAdmissionFixtures.observation SupplierUnchanged genesis
        let response = Convert.FromBase64String unchanged.ResponseBase64
        for purpose in ["creation"; "genesis-readback"; "unknown"] do
            let adapter = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok { unchanged with Purpose = purpose } }
            Assert.True(PortableSupplierAdmission.verify adapter (SupplierAdmissionFixtures.at 3.) 200L genesis response |> Result.isError)
        let adapter = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok unchanged }
        Assert.True(PortableSupplierAdmission.verify adapter (SupplierAdmissionFixtures.at 3.) 200L { genesis with Phase = SupplierIntentStored } response |> Result.isError)

    [<Fact>]
    member _.``serialized pinned copies and pending challenge all count toward transcript cap``() =
        let genesis = SupplierAdmissionFixtures.genesis () |> SupplierAdmissionFixtures.pending
        let p = genesis.Pinned.Value
        let decoded value = Convert.FromBase64String(value).Length
        let existing =
            [genesis.Original.IntentBase64; p.CreationBase64; p.CreationReceiptBase64; p.ReservationBase64; p.ResponseBase64; genesis.Challenge.Value.BytesBase64]
            @ p.EventsBase64 @ genesis.EventsBase64 @ genesis.ResponsesBase64
            |> List.sumBy decoded
        let remaining = PortableSupplierAdmission.MaximumTranscriptBytes - existing
        let bytes n = Array.create<byte> n 120uy |> Convert.ToBase64String
        let full, remainder = remaining / 65536, remaining % 65536
        let appended = List.replicate full (bytes 65536) @ [bytes remainder]
        let boundary = { genesis with ResponsesBase64 = genesis.ResponsesBase64 @ appended }
        Assert.True(PortableSupplierAdmission.validate boundary |> Result.isOk)
        let tooMuch = { boundary with ResponsesBase64 = genesis.ResponsesBase64 @ List.replicate full (bytes 65536) @ [bytes (remainder + 1)] }
        Assert.True(PortableSupplierAdmission.validate tooMuch |> Result.isError)
        // Both serialized copies of a nested event are deliberately charged separately.
        let changedPinned = { p with EventsBase64 = p.EventsBase64 @ [SupplierAdmissionFixtures.b64 "one-more-copy"] }
        Assert.True(PortableSupplierAdmission.validate { boundary with Pinned = Some changedPinned } |> Result.isError)
        let expandedChallenge = String.replicate 45000 "x"
        let wire = match genesis.Challenge.Value with WireV1 value -> value | _ -> failwith "wire fixture required"
        let changedPending = { wire with BytesBase64 = SupplierAdmissionFixtures.b64 expandedChallenge; Digest = SupplierAdmissionFixtures.domain "challenge" expandedChallenge }
        Assert.True(PortableSupplierAdmission.validate { boundary with Challenge = Some(WireV1 changedPending) } |> Result.isError)

// Caller assertions and canonical bytes only; no real host, nonce generator or clock is enrolled.
module SupplierNativeProofFixtures =
    let hash (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let native (state: PortableSupplierAdmissionState) : PortableSupplierNativeProofChallenge =
        let context =
            { Original = state.Original; OperationId = Guid.Parse "55555555-5555-5555-5555-555555555555"
              ReservationDigest = String.replicate 64 "c"; ProofEventHead = String.replicate 64 "d"
              NativeReceiptSha256 = String.replicate 64 "e"; HostLeaseNotAfter = SupplierAdmissionFixtures.at 90. }
        let id = Guid.Parse "66666666-6666-6666-6666-666666666666"
        let text =
            sprintf "{\"challenge_id\":\"%s\",\"intent_digest\":\"%s\",\"issued_at\":\"2026-01-01T00:00:01.000000Z\",\"native_receipt_sha256\":\"%s\",\"not_after\":\"2026-01-01T00:00:30.000000Z\",\"operation_id\":\"%s\",\"profile\":\"signed-hold-v1\",\"proof_event_head\":\"%s\",\"receiver_epoch\":\"%s\",\"reservation_digest\":\"%s\",\"schema\":\"fsgg.v3.native-proof-challenge/1\"}\n"
                (id.ToString("D")) state.Original.IntentDigest context.NativeReceiptSha256 (context.OperationId.ToString("D"))
                context.ProofEventHead (state.Original.ReceiverEpoch.ToString("D")) context.ReservationDigest
        let bytes = Encoding.ASCII.GetBytes text
        { Context = context; ChallengeId = id; Digest = hash bytes; BytesBase64 = Convert.ToBase64String bytes
          ReceiverEpoch = state.Original.ReceiverEpoch; IssuedAt = SupplierAdmissionFixtures.at 1.
          NotAfter = SupplierAdmissionFixtures.at 30.; IssuedMonotonic = 100L; DeadlineMonotonic = 1000L }
    let initial () = SupplierAdmissionFixtures.original () |> PortableSupplierAdmission.beginIntentV3 |> SupplierAdmissionFixtures.ok
    let pending state =
        let challenge = native state
        PortableSupplierAdmission.issueNativeProofChallenge challenge.Context challenge state |> SupplierAdmissionFixtures.ok
    let dispose disposition (state: PortableSupplierAdmissionState) =
        let pending = state.Challenge.Value
        { state with Revision = state.Revision + 1UL; Challenge = None
                     ChallengeLedger = (pending.Kind, pending.ChallengeId, pending.Digest, disposition) :: state.ChallengeLedger }

type PortableHostAllocationTests_TypedSupplierChallenge() =
    [<Fact>]
    member _.``native raw digest and exact C1 remain distinct from wire authentication``() =
        let state = SupplierNativeProofFixtures.initial ()
        let native = SupplierNativeProofFixtures.native state
        let pending = SupplierNativeProofFixtures.pending state
        Assert.Equal(Some(NativeProofV1 native), pending.Challenge)
        Assert.NotEqual(native.Context.OperationId, state.Original.Command.CommandId)
        let mutable calls = 0
        let authenticator = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = calls <- calls + 1; Error "must not run" }
        Assert.True(PortableSupplierAdmission.verify authenticator (SupplierAdmissionFixtures.at 3.) 200L pending [| 1uy |] |> Result.isError)
        Assert.Equal(0, calls)
        Assert.True(PortableSupplierAdmission.prepareAck pending |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost pending |> Result.isError)
        let text = Convert.FromBase64String native.BytesBase64 |> Encoding.ASCII.GetString
        let wireHash = SupplierAdmissionFixtures.domain "challenge" text
        Assert.NotEqual(wireHash, native.Digest)
        Assert.True(PortableSupplierAdmission.issueNativeProofChallenge native.Context { native with Digest = wireHash } state |> Result.isError)
        let wire = { SupplierAdmissionFixtures.challenge state with BytesBase64 = native.BytesBase64; Digest = native.Digest }
        Assert.True(PortableSupplierAdmission.issueChallenge wire state |> Result.isError)

    [<Fact>]
    member _.``native exact canonical bytes refuse alternate encodings and context changes``() =
        let state = SupplierNativeProofFixtures.initial ()
        let native = SupplierNativeProofFixtures.native state
        let text = Convert.FromBase64String native.BytesBase64 |> Encoding.ASCII.GetString
        let damaged =
            [ text.TrimEnd('\n'); text + "\n"; text.Replace("signed-hold-v1", "other-profile")
              text.Replace("000000Z", "000Z"); text.Replace("{", "{\"unknown\":\"x\", ")
              text.Replace("{", "{\"schema\":\"duplicate\","); text.Replace("\"profile\":\"signed-hold-v1\"", "\"profile\":1")
              text.Replace("\"challenge_id\":", " \"challenge_id\":"); text.Replace("fsgg.v3.native-proof-challenge/1", "fsgg.v3.challenge/1") ]
        for value in damaged do
            let bytes = Encoding.ASCII.GetBytes value
            let changed = { native with BytesBase64 = Convert.ToBase64String bytes; Digest = SupplierNativeProofFixtures.hash bytes }
            Assert.True(PortableSupplierAdmission.issueNativeProofChallenge native.Context changed state |> Result.isError)
        for context in
            [ { native.Context with OperationId = Guid.NewGuid() }; { native.Context with ReservationDigest = String.replicate 64 "f" }
              { native.Context with ProofEventHead = String.replicate 64 "f" }; { native.Context with NativeReceiptSha256 = String.replicate 64 "f" }
              { native.Context with Original = { state.Original with IntentDigest = String.replicate 64 "f" } } ] do
            Assert.True(PortableSupplierAdmission.issueNativeProofChallenge context native state |> Result.isError)
        for changed in
            [ { native with ReceiverEpoch = Guid.NewGuid() }; { native with ChallengeId = Guid.Empty }
              { native with NotAfter = SupplierAdmissionFixtures.at 101. }; { native with IssuedAt = native.IssuedAt.AddTicks 1L }
              { native with Context = { native.Context with HostLeaseNotAfter = SupplierAdmissionFixtures.at 20. } }
              { native with IssuedMonotonic = -1L }; { native with DeadlineMonotonic = 100L }
              { native with BytesBase64 = Convert.ToBase64String(Array.zeroCreate<byte> 45001) } ] do
            Assert.True(PortableSupplierAdmission.issueNativeProofChallenge changed.Context changed state |> Result.isError)

    [<Theory>]
    [<InlineData(false, "consumed")>]
    [<InlineData(false, "abandoned")>]
    [<InlineData(true, "consumed")>]
    [<InlineData(true, "abandoned")>]
    member _.``shared history rejects nonce reuse across both kinds``(nativeFirst: bool, disposition: string) =
        let state = SupplierNativeProofFixtures.initial ()
        let native = SupplierNativeProofFixtures.native state
        let wire = { SupplierAdmissionFixtures.challenge state with ChallengeId = native.ChallengeId }
        let pending =
            if nativeFirst then SupplierNativeProofFixtures.pending state
            else PortableSupplierAdmission.issueChallenge wire state |> SupplierAdmissionFixtures.ok
        let disposed = SupplierNativeProofFixtures.dispose disposition pending
        PortableSupplierAdmission.validate disposed |> SupplierAdmissionFixtures.ok |> ignore
        Assert.True(PortableSupplierAdmission.issueChallenge wire disposed |> Result.isError)
        Assert.True(PortableSupplierAdmission.issueNativeProofChallenge native.Context native disposed |> Result.isError)
        let changedKind = if nativeFirst then SupplierWireV1 else SupplierNativeProofV1
        let collision = { disposed with ChallengeLedger = (changedKind, native.ChallengeId, native.Digest, disposition) :: disposed.ChallengeLedger }
        Assert.True(PortableSupplierAdmission.validate collision |> Result.isError)

    [<Fact>]
    member _.``native restart retains abandoned exact identity and original bounds``() =
        let pending = SupplierNativeProofFixtures.initial () |> SupplierNativeProofFixtures.pending
        let native = match pending.Challenge.Value with NativeProofV1 value -> value | _ -> failwith "native expected"
        let restarted = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) pending |> SupplierAdmissionFixtures.ok
        Assert.Contains((SupplierNativeProofV1, native.ChallengeId, native.Digest, "abandoned"), restarted.ChallengeLedger)
        Assert.Equal(None, restarted.Challenge)
        Assert.Equal(pending.Original.Command.Deadline, restarted.Original.Command.Deadline)
        Assert.True(PortableSupplierAdmission.abandonChallenge Guid.Empty pending |> Result.isError)
        Assert.True(PortableSupplierAdmission.abandonChallenge pending.Original.ReceiverEpoch pending |> Result.isError)
        Assert.True(PortableSupplierAdmission.issueNativeProofChallenge native.Context native restarted |> Result.isError)
        Assert.True(PortableSupplierAdmission.validate { pending with JournalVersion = SupplierJournalV2 } |> Result.isError)
        Assert.True(PortableSupplierAdmission.validate { restarted with JournalVersion = SupplierJournalV2 } |> Result.isError)

    [<Fact>]
    member _.``native proof event is explicit and can precede the latest wire head``() =
        let genesis = { SupplierAdmissionFixtures.genesis () with JournalVersion = SupplierJournalV3 }
        let native = SupplierNativeProofFixtures.native genesis
        Assert.NotEqual(genesis.Head, Some native.Context.ProofEventHead)
        let pending = PortableSupplierAdmission.issueNativeProofChallenge native.Context native genesis |> SupplierAdmissionFixtures.ok
        Assert.Equal(genesis.Head, pending.Head)
        Assert.True(PortableSupplierAdmission.issueChallenge (SupplierAdmissionFixtures.challenge pending) pending |> Result.isError)
        Assert.True(PortableSupplierAdmission.issueNativeProofChallenge native.Context native pending |> Result.isError)

/// Shared exact bytes exercise the actual companion codec, independently of the Python qualifier.
type PortableHostAllocationTests_SelectedManifest() =
    let ok = function Ok value -> value | Error reason -> failwith reason
    let corpusPath () =
        // The hosted test runs in the source checkout; no new project-content wildcard is needed.
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "selected-manifest corpus absent from source checkout"
            else
                let path = Path.Combine(directory.FullName, "contracts", "portable-workspace", "v1", "supplier-selected-manifest-v1-vectors.json")
                if File.Exists path then path else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory))

    [<Fact>]
    member _.``selected manifest matches immutable exact bytes and refusal corpus``() =
        use corpus = JsonDocument.Parse(File.ReadAllBytes(corpusPath ()))
        for row in corpus.RootElement.GetProperty("cases").EnumerateArray() do
            let text (key: string) = row.GetProperty(key).GetString()
            let raw =
                if row.GetProperty("bytesBase64").ValueKind = JsonValueKind.Null then None
                else Some (Convert.FromBase64String(text "bytesBase64"))
            let selectionElement = row.GetProperty("selection")
            let selection =
                if selectionElement.GetProperty("kind").GetString() = "none" then NoSelectedOutputs
                else
                    SelectedOutputs
                        { Digest = selectionElement.GetProperty("digest").GetString()
                          MemberCount = UInt64.Parse(selectionElement.GetProperty("member_count").GetString())
                          TotalBytes = UInt64.Parse(selectionElement.GetProperty("total_bytes").GetString()) }
            for bytes in raw |> Option.toList do
                Assert.Equal(text "sha256", Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant())
                Assert.Equal(text "byteCount", string bytes.Length)
            let result = PortableSupplierManifest.validateSelection selection raw
            Assert.True((Result.isOk result = row.GetProperty("structuralAccept").GetBoolean()), text "name")
            match result with
            | Ok (Some value) ->
                let bytes = raw.Value
                Assert.True((bytes = PortableSupplierManifest.serialize value), text "name")
                let summary = PortableSupplierManifest.summary value
                let expected = row.GetProperty("summary")
                Assert.Equal(expected.GetProperty("digest").GetString(), summary.Digest)
                Assert.Equal(expected.GetProperty("member_count").GetString(), string summary.MemberCount)
                Assert.Equal(expected.GetProperty("total_bytes").GetString(), string summary.TotalBytes)
                let recreated = PortableSupplierManifest.members value |> PortableSupplierManifest.create |> ok
                Assert.True((bytes = PortableSupplierManifest.serialize recreated))
                // Both inbound and returned buffers are caller-owned; neither can mutate retained data.
                bytes.[0] <- 0uy
                let outbound = PortableSupplierManifest.serialize value
                outbound.[0] <- 0uy
                Assert.Equal(123uy, (PortableSupplierManifest.serialize value).[0])
                Assert.Equal(summary, PortableSupplierManifest.summary value)
            | _ -> ()

    [<Fact>]
    member _.``manifest parsing supplies neither durable export nor census ack or handoff``() =
        let memberValue = { Bytes = 0UL; Path = "a"; Sha256 = String.replicate 64 "a" }
        let value = PortableSupplierManifest.create [memberValue] |> ok
        let selected = SelectedOutputs (PortableSupplierManifest.summary value)
        Assert.True(PortableSupplierManifest.validateSelection selected (Some (PortableSupplierManifest.serialize value)) |> Result.isOk)
        Assert.True(PortableSupplierManifest.validateSelection NoSelectedOutputs None |> Result.isOk)
        for selection in [selected; NoSelectedOutputs] do
            let original = { HostFixtures.reservation with Selection = selection }
            let decision = PortableHostAllocation.retirement (HostFixtures.at 70.) original PortableHostAllocation.emptyLifecycle
            Assert.NotEqual(HostEligible, decision.OutputDeletion)
            Assert.NotEqual(EvidenceKnown (), decision.ReservationRelease)
        let original = SupplierAdmissionFixtures.initial ()
        Assert.True(PortableSupplierAdmission.prepareAck original |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost original |> Result.isError)

/// Closed lexical controls only. Complete authenticated caller/store journeys are separate tests.
type PortableHostAllocationTests_ReceiverCheckpointCodec() =
    let ok = function Ok value -> value | Error error -> failwith error
    let corpus () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "checkpoint corpus missing"
            let path = Path.Combine(directory.FullName, "contracts", "portable-workspace", "v1", "receiver-checkpoint-v2-vectors.json")
            if File.Exists path then File.ReadAllBytes path else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory)) |> fun raw -> JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
    let hash bytes = SHA256.HashData(bytes: byte array) |> Convert.ToHexString |> _.ToLowerInvariant()

    [<Fact>]
    member _.``checkpoint v2 exact lexical vectors retain structural classification``() =
        use doc = corpus ()
        Assert.False(doc.RootElement.GetProperty("authentication").GetBoolean())
        Assert.False(doc.RootElement.GetProperty("semanticJourney").GetBoolean())
        for row in doc.RootElement.GetProperty("cases").EnumerateArray() do
            let raw = row.GetProperty("bytes").GetString() |> Encoding.ASCII.GetBytes
            let expected = row.GetProperty("structuralAccept").GetBoolean()
            let name = row.GetProperty("name").GetString()
            if row.GetProperty("kind").GetString() = "genesis" then
                let parsed = PortableSupplierCheckpoint.parseGenesis raw
                Assert.True((Result.isOk parsed = expected), name)
                match parsed with
                | Ok value ->
                    Assert.True((raw = PortableSupplierCheckpoint.serializeGenesis value), name)
                    Assert.Equal(row.GetProperty("sha256").GetString(), PortableSupplierCheckpoint.genesisHash value)
                    raw.[0] <- 0uy
                    let returned = PortableSupplierCheckpoint.serializeGenesis value
                    returned.[0] <- 0uy
                    Assert.Equal(123uy, (PortableSupplierCheckpoint.serializeGenesis value).[0])
                    Assert.Equal("controlled-test", (PortableSupplierCheckpoint.genesisCommon value).Provenance)
                | _ -> ()
            else
                let parsed = PortableSupplierCheckpoint.parseOutput raw
                Assert.True((Result.isOk parsed = expected), name)
                match parsed with
                | Ok value ->
                    Assert.True((raw = PortableSupplierCheckpoint.serializeOutput value), name)
                    Assert.Equal(row.GetProperty("sha256").GetString(), PortableSupplierCheckpoint.outputHash value)
                    raw.[0] <- 0uy
                    Assert.Equal(123uy, (PortableSupplierCheckpoint.serializeOutput value).[0])
                | _ -> ()

    [<Fact>]
    member _.``checkpoint commitments distinguish exact raw and existing domain preimages``() =
        use doc = corpus ()
        let rows = doc.RootElement.GetProperty("cases").EnumerateArray() |> Seq.toArray
        let raw = rows.[0].GetProperty("bytes").GetString() |> Encoding.ASCII.GetBytes
        let parsed = PortableSupplierCheckpoint.parseGenesis raw |> ok
        let common = PortableSupplierCheckpoint.genesisCommon parsed
        let preimages = doc.RootElement.GetProperty("preimages")
        let exact key = preimages.GetProperty(key: string).GetString() |> Convert.FromBase64String
        let domain name raw = Array.append (Encoding.ASCII.GetBytes("fsgg.v3." + name + "/1\000")) raw |> hash
        Assert.Equal(domain "intent" (exact "originalIntent"), common.IntentDigest)
        Assert.Equal(domain "creation" (exact "creationCore"), common.CreationCoreDigest)
        Assert.Equal(domain "creation-receipt" (exact "creationReceipt"), common.CreationReceiptDigest)
        Assert.Equal(domain "reservation" (exact "reservation"), common.ReservationDigest)
        Assert.Equal(hash (exact "originalIntentEnvelope"), common.OriginalIntentEnvelopeSha256)
        Assert.Equal(hash (exact "request"), common.AcceptedRequestEnvelopeSha256)
        Assert.Equal(hash (exact "result"), common.AcceptedResultEnvelopeSha256)
        Assert.Equal(hash (exact "response"), common.AcceptedResponseEnvelopeSha256)
        Assert.NotEqual(hash (exact "originalIntent"), common.IntentDigest)
        Assert.True(PortableSupplierAdmission.prepareAck (SupplierAdmissionFixtures.initial ()) |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost (SupplierAdmissionFixtures.initial ()) |> Result.isError)

/// Public lexical fixture bytes only; the opaque signature is never called authenticated.
module SupplierCheckpointFixtures =
    let ok = function Ok value -> value | Error error -> failwith error
    let hash (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let domain name bytes = Array.append (Encoding.ASCII.GetBytes("fsgg.v3." + name + "/1\000")) bytes |> hash
    let corpus () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "checkpoint corpus missing"
            let path = Path.Combine(directory.FullName, "contracts", "portable-workspace", "v1", "receiver-checkpoint-v2-vectors.json")
            if File.Exists path then File.ReadAllBytes path else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory)) |> fun raw -> JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
    let initial () =
        use doc = corpus ()
        let preimages = doc.RootElement.GetProperty("preimages")
        let bytes name = preimages.GetProperty(name: string).GetString() |> Convert.FromBase64String
        use intent = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes "originalIntent"))
        let value = intent.RootElement
        let command: PortableWorkspaceCommand =
            { CommandId = Guid.Parse "11111111-1111-1111-1111-111111111111"
              IdempotencyId = "sample"; WorkspaceScope = "sample"; ProfileId = "sample"; ProfileRevision = 1UL
              SourceRevision = value.GetProperty("requested_source_revision").GetString()
              ExpectedWorkflowRevision = 1UL; FenceGeneration = 1UL; CausationId = None
              Deadline = DateTimeOffset.Parse(value.GetProperty("command_deadline").GetString(), Globalization.CultureInfo.InvariantCulture)
              Operation = "sample"; ComponentId = None }
        let original =
            { Command = command; QualifiedImage = value.GetProperty("qualified_inner_image").GetString()
              IntentBase64 = Convert.ToBase64String(bytes "originalIntent"); IntentDigest = domain "intent" (bytes "originalIntent")
              TrustPolicyDigest = String.replicate 64 "a"; SupplierSourceRevision = command.SourceRevision
              ReceiverEpoch = Guid.Parse "22222222-2222-2222-2222-222222222222" }
        PortableSupplierAdmission.beginIntentV4 original (bytes "originalIntentEnvelope") |> ok
    let challenge (state: PortableSupplierAdmissionState) =
        let issued = DateTimeOffset.Parse("2026-01-01T00:00:00.000000Z", Globalization.CultureInfo.InvariantCulture)
        let exact = Encoding.ASCII.GetBytes(
            "{\"challenge_id\":\"33333333-3333-3333-3333-333333333333\",\"challenge_nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"correlation_id\":\"11111111-1111-1111-1111-111111111111\",\"intent_digest\":\""
            + state.Original.IntentDigest + "\",\"issued_at\":\"2026-01-01T00:00:00.000000Z\",\"not_after\":\"2026-01-01T00:00:30.000000Z\",\"schema\":\"fsgg.v3.creation-challenge/1\"}\n")
        { ChallengeId = Guid.Parse "33333333-3333-3333-3333-333333333333"; Digest = domain "challenge" exact
          BytesBase64 = Convert.ToBase64String exact; Purpose = "creation"; ReceiverEpoch = state.Original.ReceiverEpoch
          IssuedAt = issued; NotAfter = issued.AddSeconds 30.; IssuedMonotonic = 1L; DeadlineMonotonic = 100L; ExpectedHead = None }

    let journey () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "controlled checkpoint journey missing"
            let path = Path.Combine(directory.FullName, "contracts", "portable-workspace", "v1", "receiver-checkpoint-v2-controlled-journey.json")
            if File.Exists path then File.ReadAllBytes path else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory)) |> fun raw -> JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
    let urlBytes (text: string) = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + String.replicate ((4 - text.Length % 4) % 4) "=")
    let exchangeFrom (root: JsonElement) (name: string) (state: PortableSupplierAdmissionState) =
        let row = root.GetProperty name
        let exact key = row.GetProperty(key: string).GetString() |> urlBytes
        let challengeBytes = exact "challenge"
        use challengeDoc = JsonDocument.Parse(ReadOnlyMemory<byte>(challengeBytes))
        let value = challengeDoc.RootElement
        let date key = DateTimeOffset.Parse(value.GetProperty(key: string).GetString(), Globalization.CultureInfo.InvariantCulture)
        let challenge: PortableSupplierAdmissionChallenge =
            { ChallengeId = Guid.Parse(value.GetProperty("challenge_id").GetString()); Digest = domain "challenge" challengeBytes
              BytesBase64 = Convert.ToBase64String challengeBytes; Purpose = row.GetProperty("purpose").GetString()
              ReceiverEpoch = state.Original.ReceiverEpoch; IssuedAt = date "issued_at"; NotAfter = date "not_after"
              IssuedMonotonic = 1L; DeadlineMonotonic = 100L; ExpectedHead = state.Head }
        { AcceptedRevision = 0UL; AcceptedStoreFence = 0UL; Challenge = challenge
          RequestBase64 = exact "request" |> Convert.ToBase64String; ResultBase64 = exact "result" |> Convert.ToBase64String
          EvidenceBase64 = exact "evidence" |> Convert.ToBase64String
          ReceiverBundleBase64 = exact "receiverBundle" |> Convert.ToBase64String
          ControllerBundleBase64 = exact "controllerBundle" |> Convert.ToBase64String }
    let observationFrom (root: JsonElement) kind (exchange: PortableSupplierRetainedWireExchange) (state: PortableSupplierAdmissionState) =
        let exact key = root.GetProperty(key: string).GetString() |> urlBytes
        use c = JsonDocument.Parse(ReadOnlyMemory<byte>(exact "creationCore"))
        use r = JsonDocument.Parse(ReadOnlyMemory<byte>(exact "reservation"))
        let core = c.RootElement
        let reservation = r.RootElement
        let time key = DateTimeOffset.Parse(reservation.GetProperty(key: string).GetString(), Globalization.CultureInfo.InvariantCulture)
        let ticks key = Int64.Parse(reservation.GetProperty(key: string).GetString(), Globalization.CultureInfo.InvariantCulture) |> TimeSpan.FromTicks
        let pinned: PortableHostReservation =
            { Binding = { HostFixtures.binding with Command = state.Original.Command; QualifiedImage = state.Original.QualifiedImage }
              AdmittedAt = time "admitted_at"; HostLeaseExpiresAt = time "host_lease_expires_at"
              MaximumRuntime = ticks "maximum_runtime_ticks"; ExportReserve = ticks "export_reserve_ticks"
              CleanupReserve = ticks "cleanup_reserve_ticks"; Selection = NoSelectedOutputs }
        let event = exact "genesisEvent"
        let at = root.GetProperty(if kind = SupplierCreation then "creation" else "genesis").GetProperty("collectedAt").GetString()
        { Kind = kind; IntentDigest = state.Original.IntentDigest; TrustPolicyDigest = state.Original.TrustPolicyDigest
          ChallengeDigest = exchange.Challenge.Digest; Purpose = exchange.Challenge.Purpose; OriginalReservation = pinned
          Identity = { Issuer = "controlled-source-only"; ContainerId = core.GetProperty("cid").GetString()
                       AllocationDevice = UInt64.Parse(core.GetProperty("allocation_device").GetString())
                       AllocationInode = UInt64.Parse(core.GetProperty("allocation_inode").GetString()) }
          OuterImageId = core.GetProperty("image_id").GetString(); SupplierSourceRevision = state.Original.SupplierSourceRevision
          CreationBase64 = exact "creationCore" |> Convert.ToBase64String
          CreationReceiptBase64 = exact "creationReceipt" |> Convert.ToBase64String
          ReservationBase64 = exact "reservation" |> Convert.ToBase64String; ResponseBase64 = exchange.EvidenceBase64
          EventsBase64 = if kind = SupplierCreation then [] else [Convert.ToBase64String event]
          FromHead = None; ToHead = if kind = SupplierCreation then None else Some(domain "signed-envelope" event)
          Sequence = 0UL; CollectedAt = DateTimeOffset.Parse(at, Globalization.CultureInfo.InvariantCulture) }
    let verifyControlledFrom root kind exchange state =
        let value = observationFrom root kind exchange state
        let adapter = { new IPortableSupplierTranscriptAuthenticator with member _.Authenticate _ = Ok value }
        PortableSupplierAdmission.verify adapter value.CollectedAt 2L state (Convert.FromBase64String exchange.EvidenceBase64) |> ok
    let pinCreationFrom root (state: PortableSupplierAdmissionState) =
        let pair = exchangeFrom root "creation" state
        let pending = PortableSupplierAdmission.issueChallenge pair.Challenge state |> ok
        let verified = verifyControlledFrom root SupplierCreation pair pending
        match PortableSupplierAdmission.pinCreationV4 verified pair pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending |> ok with
        | SupplierCheckpointData value -> value
        | SupplierCheckpointHeld _ -> failwith "controlled creation held"
    let acceptGenesisFrom root (state: PortableSupplierAdmissionState) =
        let pair = exchangeFrom root "genesis" state
        let pending = PortableSupplierAdmission.issueChallenge pair.Challenge state |> ok
        let verified = verifyControlledFrom root SupplierGenesis pair pending
        match PortableSupplierAdmission.acceptWireV4 verified pair pending.Original pending.Revision pending.CheckpointCustody.Value.StoreFence pending |> ok with
        | SupplierCheckpointData value -> value
        | SupplierCheckpointHeld _ -> failwith "controlled genesis held"

    let exchange name state =
        use doc = journey ()
        exchangeFrom doc.RootElement name state
    let pinCreation state =
        use doc = journey ()
        pinCreationFrom doc.RootElement state
    let acceptGenesis state =
        use doc = journey ()
        acceptGenesisFrom doc.RootElement state

type PortableHostAllocationTests_ReceiverCheckpointSource() =
    [<Fact>]
    member _.``fresh v4 retains original bytes and separate fence without creating a witness``() =
        let initial = SupplierCheckpointFixtures.initial ()
        Assert.Equal(SupplierJournalV4, initial.JournalVersion)
        Assert.Equal(1UL, initial.CheckpointCustody.Value.StoreFence)
        Assert.True(initial.CheckpointCustody.Value.GenesisCheckpointBase64.IsNone)
        Assert.True(initial.CheckpointCustody.Value.OutputCheckpointBase64.IsNone)
        let pending = PortableSupplierAdmission.issueChallenge (SupplierCheckpointFixtures.challenge initial) initial |> SupplierCheckpointFixtures.ok
        Assert.Equal(2UL, pending.Revision)
        Assert.Equal(2UL, pending.CheckpointCustody.Value.StoreFence)
        let restarted = PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) pending |> SupplierCheckpointFixtures.ok
        Assert.Equal(3UL, restarted.CheckpointCustody.Value.StoreFence)
        Assert.Equal(initial.Original.ReceiverEpoch, restarted.CheckpointCustody.Value.OriginalReceiverEpoch)
        Assert.Single(restarted.CheckpointCustody.Value.AbandonedChallenges) |> ignore
        Assert.True(PortableSupplierAdmission.prepareAck restarted |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost restarted |> Result.isError)

    [<Fact>]
    member _.``v4 refuses copied legacy state omitted custody and changed originals``() =
        let initial = SupplierCheckpointFixtures.initial ()
        let invalid =
            [ { initial with CheckpointCustody = None }
              { initial with Head = Some(String.replicate 64 "f") }
              { initial with Sequence = 1UL }
              { initial with EventsBase64 = [initial.Original.IntentBase64] }
              { initial with ResponsesBase64 = [initial.Original.IntentBase64] }
              { initial with CheckpointCustody = Some { initial.CheckpointCustody.Value with OriginalReceiverEpoch = Guid.NewGuid() } }
              { initial with Challenge = Some(WireV1(SupplierCheckpointFixtures.challenge initial)) }
              { initial with CheckpointCustody = Some { initial.CheckpointCustody.Value with StoreFence = 2UL } }
              { initial with Original = { initial.Original with IntentDigest = String.replicate 64 "f" } }
              { initial with JournalVersion = SupplierJournalV3 }
              { SupplierAdmissionFixtures.initial () with JournalVersion = SupplierJournalV4 } ]
        for state in invalid do Assert.True(PortableSupplierAdmission.validate state |> Result.isError)
        let malformed = { SupplierCheckpointFixtures.challenge initial with BytesBase64 = Convert.ToBase64String [| 1uy |] }
        Assert.True(PortableSupplierAdmission.issueChallenge malformed initial |> Result.isError)
        Assert.True(PortableSupplierAdmission.abandonChallenge (Guid.NewGuid()) initial |> Result.isError)
        Assert.True(PortableSupplierAdmission.issueSelectedNativeProofChallengeV4 initial.Original initial.Revision 1UL
                        (Guid.NewGuid()) DateTimeOffset.UtcNow (DateTimeOffset.UtcNow.AddSeconds 1.) 1L 2L initial |> Result.isError)

/// Lexical receipt controls are separate from authenticated prefix/output acceptance.
type PortableHostAllocationTests_NativeReceiptCodec() =
    [<Fact>]
    member _.``native receipt exact closed codecs preserve structural classification and immutable bytes``() =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "native receipt corpus missing"
            let path = Path.Combine(directory.FullName, "contracts", "portable-workspace", "v1", "native-receipt-v1-vectors.json")
            if File.Exists path then File.ReadAllBytes path else find directory.Parent
        use doc = find (DirectoryInfo(AppContext.BaseDirectory)) |> fun raw -> JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
        Assert.False(doc.RootElement.GetProperty("authentication").GetBoolean())
        Assert.False(doc.RootElement.GetProperty("nativeAcceptance").GetBoolean())
        Assert.False(doc.RootElement.GetProperty("semanticJourney").GetBoolean())
        for row in doc.RootElement.GetProperty("cases").EnumerateArray() do
            let bytes = row.GetProperty("bytes").GetString() |> Encoding.ASCII.GetBytes
            let expected = row.GetProperty("structuralAccept").GetBoolean()
            let parsed = PortableSupplierNativeReceipt.parse bytes
            Assert.True((Result.isOk parsed = expected), row.GetProperty("name").GetString())
            match parsed with
            | Ok receipt ->
                Assert.True(bytes = PortableSupplierNativeReceipt.serialize receipt)
                Assert.Equal(row.GetProperty("sha256").GetString(), PortableSupplierNativeReceipt.rawSha256 receipt)
                bytes.[0] <- 0uy
                let copy = PortableSupplierNativeReceipt.serialize receipt
                copy.[0] <- 0uy
                Assert.Equal(123uy, (PortableSupplierNativeReceipt.serialize receipt).[0])
            | _ -> ()

/// Full retained structural originals/pairs, still using an explicitly controlled test adapter.
type PortableHostAllocationTests_ReceiverCheckpointControlledJourney() =
    [<Fact>]
    member _.``controlled genesis caller retains full paired bytes and recomputes checkpoint on cold validation``() =
        use doc = SupplierCheckpointFixtures.journey ()
        Assert.False(doc.RootElement.GetProperty("authentication").GetBoolean())
        Assert.False(doc.RootElement.GetProperty("durability").GetBoolean())
        let initial = SupplierCheckpointFixtures.initial ()
        let creation = SupplierCheckpointFixtures.pinCreation initial
        Assert.Equal(3UL, creation.Revision)
        let genesis = SupplierCheckpointFixtures.acceptGenesis creation
        Assert.Equal(5UL, genesis.Revision)
        Assert.Equal(5UL, genesis.CheckpointCustody.Value.StoreFence)
        Assert.True(genesis.CheckpointCustody.Value.GenesisCheckpointBase64.IsSome)
        Assert.True(genesis.CheckpointCustody.Value.OutputCheckpointBase64.IsNone)
        Assert.Equal(genesis, PortableSupplierAdmission.validate genesis |> SupplierCheckpointFixtures.ok)
        let checkpoint = Convert.FromBase64String genesis.CheckpointCustody.Value.GenesisCheckpointBase64.Value |> PortableSupplierCheckpoint.parseGenesis |> SupplierCheckpointFixtures.ok
        Assert.Equal(genesis.Head.Value, (PortableSupplierCheckpoint.genesisCommon checkpoint).ObservedHead)
        Assert.Equal(0UL, (PortableSupplierCheckpoint.genesisCommon checkpoint).AcceptedSequence)
        let custody = genesis.CheckpointCustody.Value
        for invalid in
            [ { genesis with EventsBase64 = [] }
              { genesis with CheckpointCustody = Some { custody with CreationExchange = None } }
              { genesis with CheckpointCustody = Some { custody with WireExchanges = [] } }
              { genesis with CheckpointCustody = Some { custody with GenesisCheckpointBase64 = None } }
              { genesis with CheckpointCustody = Some { custody with StoreFence = 4UL } } ] do
            Assert.True(PortableSupplierAdmission.validate invalid |> Result.isError)
        Assert.True(PortableSupplierAdmission.prepareAck genesis |> Result.isError)
        Assert.True(PortableSupplierAdmission.reserveHost genesis |> Result.isError)

    [<Fact>]
    member _.``rehash complete paired original and temporal mutations refuse the actual controlled caller``() =
        use doc = SupplierCheckpointFixtures.journey ()
        for row in doc.RootElement.GetProperty("negativeJourneys").EnumerateArray() do
            Assert.False(row.GetProperty("expectedSourceAccept").GetBoolean())
            let accepted =
                try
                    SupplierCheckpointFixtures.initial ()
                    |> SupplierCheckpointFixtures.pinCreationFrom row
                    |> SupplierCheckpointFixtures.acceptGenesisFrom row
                    |> PortableSupplierAdmission.validate |> Result.isOk
                with _ -> false
            Assert.False(accepted, row.GetProperty("caseName").GetString())
