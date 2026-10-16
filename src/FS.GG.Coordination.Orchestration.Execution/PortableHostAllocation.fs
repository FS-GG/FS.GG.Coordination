namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Security.Cryptography

/// Original identities supplied by a provenance-checking adapter, never inferred from a path.
type PortableHostBinding =
    {
        Command: PortableWorkspaceCommand
        QualifiedImage: string
        AllocationId: string
        WorkerId: string
        AllocationReceiptDigest: string
    }

type PortableHostExportManifest =
    {
        Digest: string
        MemberCount: uint64
        TotalBytes: uint64
    }

type PortableHostExportSelection =
    | NoSelectedOutputs
    | SelectedOutputs of PortableHostExportManifest

/// An inert decision input. Receipt strings identify evidence; these types do not authenticate it.
type PortableHostReservation =
    {
        Binding: PortableHostBinding
        AdmittedAt: DateTimeOffset
        HostLeaseExpiresAt: DateTimeOffset
        MaximumRuntime: TimeSpan
        ExportReserve: TimeSpan
        CleanupReserve: TimeSpan
        Selection: PortableHostExportSelection
    }

type PortableHostObservation<'value> =
    {
        Binding: PortableHostBinding
        CollectedAt: DateTimeOffset
        ReceiptDigest: string
        Value: 'value
    }

type PortableHostExportCompletion =
    | DurableManifestReadback of PortableHostExportManifest
    | HostCensusConfirmsZeroSelection

type PortableHostLifecycle =
    {
        WorkloadExit: PortableEvidence<PortableHostObservation<int>>
        Termination: PortableEvidence<PortableHostObservation<unit>>
        /// Missing is unrequested; attempted cleanup with absent completion evidence is Unknown.
        InnerCleanup: PortableEvidence<PortableHostObservation<unit>>
        Export: PortableEvidence<PortableHostObservation<PortableHostExportCompletion>>
        /// Missing is unrequested; attempted retirement with absent completion evidence is Unknown.
        OuterRetirement: PortableEvidence<PortableHostObservation<unit>>
        FirstFailure: string option
        ReconciliationConflicts: Set<string>
    }

type PortableHostDecision =
    | HostEligible
    | HostHeld of reason: string

type PortableHostRetirementDecision =
    {
        OutputDeletion: PortableHostDecision
        OuterCleanup: PortableHostDecision
        ReservationRelease: PortableEvidence<unit>
    }

/// Independently pinned supplier identity. A status document cannot establish these pins.
type PortableHostSupplierIdentity =
    { Issuer: string
      ContainerId: string
      AllocationDevice: uint64
      AllocationInode: uint64 }

/// The verifier must authenticate the entire original reservation and lifecycle together.
/// ReceiptDigest is SHA-256 of the exact authenticated input bytes, not an authentication flag.
type PortableHostSupplierEvidence =
    { Identity: PortableHostSupplierIdentity
      Reservation: PortableHostReservation
      CollectedAt: DateTimeOffset
      ReceiptDigest: string
      Lifecycle: PortableHostLifecycle }

/// Trusted host-owner transport port. Implementations must establish issuer/custody independently
/// before projecting input bytes. Parsing unsealed JSON or accepting a claimed issuer is insufficient.
/// No production implementation is implied by this interface.
type IPortableHostSupplierVerifier =
    abstract Authenticate: byte array -> Result<PortableHostSupplierEvidence, string>

/// Only the ingress validator can construct this value. It conveys evidence, never effect authority.
type PortableAuthenticatedHostEvidence =
    private
    | AuthenticatedHostEvidence of PortableHostSupplierEvidence * DateTimeOffset * TimeSpan
    member this.Evidence = let (AuthenticatedHostEvidence(value, _, _)) = this in value
    member this.IsCurrent(now: DateTimeOffset) =
        let (AuthenticatedHostEvidence(_, verifiedAt, maximumAge)) = this
        now >= verifiedAt && now - this.Evidence.CollectedAt <= maximumAge

[<RequireQualifiedAccess>]
module PortableHostAllocation =
    /// Initial state before any cleanup request. Missing means unrequested, not a lost receipt.
    /// An attempted effect without an authentic completion receipt must be represented as Unknown.
    let emptyLifecycle =
        {
            WorkloadExit = EvidenceMissing "workload exit not observed"
            Termination = EvidenceUnknown "termination not observed"
            InnerCleanup = EvidenceMissing "inner cleanup not requested"
            Export = EvidenceMissing "durable export or host census not observed"
            OuterRetirement = EvidenceMissing "outer retirement not requested"
            FirstFailure = None
            ReconciliationConflicts = Set.empty
        }

    let private present value = not (String.IsNullOrWhiteSpace value)

    let private validBinding (binding: PortableHostBinding) =
        binding.Command.CommandId <> Guid.Empty
        && ([ binding.Command.IdempotencyId; binding.Command.WorkspaceScope;
              binding.Command.ProfileId; binding.Command.SourceRevision;
              binding.QualifiedImage; binding.AllocationId; binding.WorkerId;
              binding.AllocationReceiptDigest ] |> List.forall present)

    let private validSelection = function
        | NoSelectedOutputs -> true
        | SelectedOutputs manifest -> present manifest.Digest && manifest.MemberCount > 0UL

    // Tick differences avoid overflowing DateTimeOffset or TimeSpan on untrusted intervals.
    let private window (reservation: PortableHostReservation) =
        let deadline = min reservation.Binding.Command.Deadline reservation.HostLeaseExpiresAt
        let available = (deadline - reservation.AdmittedAt).Ticks
        let exportTicks = reservation.ExportReserve.Ticks
        let cleanupTicks = reservation.CleanupReserve.Ticks
        if not (validBinding reservation.Binding) || not (validSelection reservation.Selection) then
            Error "invalid identity or export selection"
        elif reservation.MaximumRuntime <= TimeSpan.Zero || exportTicks <= 0L || cleanupTicks <= 0L then
            Error "runtime and export/cleanup reserves must be positive"
        elif available <= 0L || exportTicks >= available || cleanupTicks >= available - exportTicks then
            Error "original lease/deadline has no execution window after reserves"
        else
            let executionTicks = min reservation.MaximumRuntime.Ticks (available - exportTicks - cleanupTicks)
            Ok (reservation.AdmittedAt.AddTicks executionTicks)

    /// A repeated admission must present the entire original reservation, including original dates.
    let executionDeadline (now: DateTimeOffset) (original: PortableHostReservation) (candidate: PortableHostReservation) =
        if original <> candidate then Error "original reservation identity or limits changed"
        else
            window original
            |> Result.bind (fun deadline ->
                if now < original.AdmittedAt || now >= deadline then Error "original execution window is unavailable"
                else Ok deadline)

    let private observationValid (reservation: PortableHostReservation) (observation: PortableHostObservation<'value>) =
        observation.Binding = reservation.Binding
        && present observation.ReceiptDigest
        && observation.CollectedAt >= reservation.AdmittedAt

    let private known (reservation: PortableHostReservation) = function
        | EvidenceKnown observation -> observationValid reservation observation
        | EvidenceMissing _ | EvidenceUnknown _ -> false

    let private exportComplete (reservation: PortableHostReservation) (now: DateTimeOffset) = function
        | EvidenceKnown observation when observationValid reservation observation
                                            && observation.CollectedAt <= now
                                            && observation.CollectedAt < reservation.HostLeaseExpiresAt
                                            && observation.CollectedAt < reservation.Binding.Command.Deadline ->
            match reservation.Selection, observation.Value with
            | SelectedOutputs expected, DurableManifestReadback actual -> expected = actual
            | NoSelectedOutputs, HostCensusConfirmsZeroSelection -> true
            | _ -> false
        | _ -> false

    /// Computes permissions only; it never deletes, stops, renews, releases or writes a journal.
    let retirement (now: DateTimeOffset) (reservation: PortableHostReservation) (lifecycle: PortableHostLifecycle) =
        let exported = exportComplete reservation now lifecycle.Export
        let exportCollectedAt =
            match lifecycle.Export with
            | EvidenceKnown observation -> observation.CollectedAt
            | _ -> DateTimeOffset.MaxValue
        let observedAfterExport = function
            | EvidenceKnown observation ->
                observationValid reservation observation
                && observation.CollectedAt >= exportCollectedAt
                && observation.CollectedAt <= now
            | _ -> false
        let terminated =
            match lifecycle.Termination with
            | EvidenceKnown observation -> known reservation lifecycle.Termination && observation.CollectedAt <= now
            | _ -> false
        let innerAfterTermination =
            match lifecycle.Termination, lifecycle.InnerCleanup with
            | EvidenceKnown termination, EvidenceKnown inner -> inner.CollectedAt >= termination.CollectedAt
            | _ -> false
        let cleanupPreconditions =
            match window reservation with
            | Error reason -> HostHeld reason
            | Ok _ when not lifecycle.ReconciliationConflicts.IsEmpty -> HostHeld "conflicting lifecycle evidence remains unresolved"
            | Ok _ when not exported -> HostHeld "complete timely export or independent zero-selection census required"
            | Ok _ when not terminated -> HostHeld "termination not observed"
            | Ok _ when now < reservation.AdmittedAt
                        || now >= reservation.HostLeaseExpiresAt
                        || now >= reservation.Binding.Command.Deadline -> HostHeld "original cleanup window expired"
            | Ok _ when (min reservation.HostLeaseExpiresAt reservation.Binding.Command.Deadline) - now < reservation.CleanupReserve ->
                HostHeld "insufficient original cleanup reserve"
            | Ok _ -> HostEligible
        let outputDeletion =
            match cleanupPreconditions, lifecycle.InnerCleanup, lifecycle.OuterRetirement with
            | HostEligible, EvidenceMissing _, EvidenceMissing _ -> HostEligible
            | HostEligible, EvidenceKnown _, _ -> HostHeld "inner cleanup already observed; do not replay"
            | HostEligible, EvidenceUnknown _, _ -> HostHeld "original inner cleanup effect is uncertain; do not replay"
            | HostEligible, _, _ -> HostHeld "outer retirement observed or uncertain; do not delete outputs"
            | held, _, _ -> held
        let outerCleanup =
            match cleanupPreconditions, lifecycle.OuterRetirement with
            | HostEligible, EvidenceMissing _ when observedAfterExport lifecycle.InnerCleanup && innerAfterTermination -> HostEligible
            | HostEligible, EvidenceMissing _ -> HostHeld "inner cleanup after export and termination not observed"
            | HostEligible, EvidenceKnown _ -> HostHeld "outer retirement already observed; do not replay"
            | HostEligible, EvidenceUnknown _ -> HostHeld "original outer retirement effect is uncertain; do not replay"
            | held, _ -> held
        let release =
            let retirementAfterInner =
                match lifecycle.InnerCleanup, lifecycle.OuterRetirement with
                | EvidenceKnown inner, EvidenceKnown outer -> outer.CollectedAt >= inner.CollectedAt
                | _ -> false
            // Late independent readback can establish actual retirement, without retroactive admission.
            if Result.isOk (window reservation) && exported && terminated
               && lifecycle.ReconciliationConflicts.IsEmpty
               && innerAfterTermination
               && observedAfterExport lifecycle.InnerCleanup
               && observedAfterExport lifecycle.OuterRetirement && retirementAfterInner then EvidenceKnown ()
            else EvidenceUnknown "original allocation retirement remains unresolved"
        { OutputDeletion = outputDeletion; OuterCleanup = outerCleanup; ReservationRelease = release }

    let private reconcile original incoming =
        match original, incoming with
        | EvidenceKnown left, EvidenceKnown right when left = right -> original
        | EvidenceKnown _, EvidenceKnown _ -> EvidenceUnknown "conflicting observations require original-identity reconciliation"
        | EvidenceUnknown _, EvidenceKnown _ -> incoming
        | EvidenceKnown _, EvidenceUnknown _ -> original
        | EvidenceUnknown _, _ -> original
        | _, EvidenceUnknown _ -> incoming
        | EvidenceKnown _, EvidenceMissing _ -> original
        | EvidenceMissing _, EvidenceKnown _ -> incoming
        | EvidenceMissing _, EvidenceMissing _ -> original

    /// Duplicate receipts are idempotent. Conflicts and first failure survive later success.
    let reconcileLifecycle (original: PortableHostLifecycle) (incoming: PortableHostLifecycle) =
        let conflict field left right =
            match left, right with
            | EvidenceKnown first, EvidenceKnown second when first <> second -> Set.singleton field
            | _ -> Set.empty
        {
            WorkloadExit = reconcile original.WorkloadExit incoming.WorkloadExit
            Termination = reconcile original.Termination incoming.Termination
            InnerCleanup = reconcile original.InnerCleanup incoming.InnerCleanup
            Export = reconcile original.Export incoming.Export
            OuterRetirement = reconcile original.OuterRetirement incoming.OuterRetirement
            FirstFailure = original.FirstFailure |> Option.orElse incoming.FirstFailure
            ReconciliationConflicts =
                [ original.ReconciliationConflicts; incoming.ReconciliationConflicts
                  conflict "workload-exit" original.WorkloadExit incoming.WorkloadExit
                  conflict "termination" original.Termination incoming.Termination
                  conflict "inner-cleanup" original.InnerCleanup incoming.InnerCleanup
                  conflict "export" original.Export incoming.Export
                  conflict "outer-retirement" original.OuterRetirement incoming.OuterRetirement ]
                |> Set.unionMany
        }

[<RequireQualifiedAccess>]
module PortableHostSupplierIngress =
    /// Validate authenticated host readback against independently retained original pins.
    /// The verifier owns authentication; this function owns identity, freshness and completeness.
    let authenticate (verifier: IPortableHostSupplierVerifier) (identity: PortableHostSupplierIdentity)
                     (original: PortableHostReservation) (now: DateTimeOffset) (maximumAge: TimeSpan)
                     (bytes: byte array) =
        let refusal = Error "portable-host-supplier-evidence-refused"
        let present value = not (String.IsNullOrWhiteSpace value)
        if isNull (box verifier) || isNull (box identity) || isNull (box original)
           || isNull bytes || bytes.Length = 0 || bytes.Length > 65536
           || not (present identity.Issuer && present identity.ContainerId)
           || identity.AllocationInode = 0UL || maximumAge <= TimeSpan.Zero
           || maximumAge > TimeSpan.FromMinutes 5. then refusal
        else
            try
                // The verifier cannot change the caller's buffer or race the digest computation.
                let snapshot = Array.copy bytes
                let digest = Convert.ToHexString(SHA256.HashData snapshot).ToLowerInvariant()
                match verifier.Authenticate(Array.copy snapshot) with
                | Error _ -> refusal
                | Ok evidence ->
                    let validObservation (observation: PortableHostObservation<'value>) =
                        observation.Binding = original.Binding
                        && present observation.ReceiptDigest
                        && observation.CollectedAt >= original.AdmittedAt
                        && observation.CollectedAt <= evidence.CollectedAt
                    let valid = function
                        | EvidenceKnown observation -> validObservation observation
                        | EvidenceMissing _ | EvidenceUnknown _ -> true
                    let lifecycle = evidence.Lifecycle
                    if evidence.Identity <> identity || evidence.Reservation <> original
                       || evidence.ReceiptDigest <> digest
                       || evidence.CollectedAt < original.AdmittedAt || evidence.CollectedAt > now
                       || now - evidence.CollectedAt > maximumAge
                       || not (valid lifecycle.WorkloadExit && valid lifecycle.Termination
                               && valid lifecycle.InnerCleanup && valid lifecycle.Export && valid lifecycle.OuterRetirement)
                       || (PortableHostAllocation.executionDeadline original.AdmittedAt original original |> Result.isError) then
                        refusal
                    else Ok(AuthenticatedHostEvidence(evidence, now, maximumAge))
            with _ -> refusal

/// Inactive shared sizing contract. These checks do not parse C1, verify signatures or admit effects.
[<RequireQualifiedAccess>]
module PortableSupplierProtocol =
    [<Literal>]
    let MaximumEnvelopeBytes = 65536
    [<Literal>]
    let MaximumPayloadBytes = 45000
    [<Literal>]
    let MaximumSignatureBytes = 2048
    [<Literal>]
    let MaximumCreationBytes = 6000
    [<Literal>]
    let MaximumReservationBytes = 6000
    [<Literal>]
    let MaximumGenesisBytes = 18000
    [<Literal>]
    let MaximumEventBytes = 12000
    [<Literal>]
    let MaximumResponseMetadataBytes = 6000
    [<Literal>]
    let MaximumEvents = 16

    let base64UrlBytes length =
        if length < 0 || length > MaximumEnvelopeBytes then Error "supplier-size-refused"
        else Ok(length / 3 * 4 + (match length % 3 with 0 -> 0 | 1 -> 2 | _ -> 3))

    /// Exact C1 {payload,signature} syntax, including its terminal LF, adds 30 ASCII bytes.
    let envelopeBytes payload signature =
        if payload <= 0 || payload > MaximumPayloadBytes || signature <= 0 || signature > MaximumSignatureBytes then
            Error "supplier-size-refused"
        else
            match base64UrlBytes payload, base64UrlBytes signature with
            | Ok left, Ok right when left + right + 30 <= MaximumEnvelopeBytes -> Ok(left + right + 30)
            | _ -> Error "supplier-size-refused"

    /// overhead is the exact genesis bytes excluding the embedded C/R bytes, never an estimate.
    let genesisBytes creation reservation overhead =
        if creation <= 0 || creation > MaximumCreationBytes || reservation <= 0 || reservation > MaximumReservationBytes
           || overhead <= 0 || overhead > MaximumResponseMetadataBytes then Error "supplier-size-refused"
        else
            let total = creation + reservation + overhead
            if total > MaximumGenesisBytes then Error "supplier-size-refused" else Ok total

    /// Metadata is the exact nonempty C1 response object without its events member, including LF.
    /// events contains base64url strings of exact signed envelopes. The inserted member adds
    /// 12 bytes plus quotes, commas and base64url bytes; all nested bytes count again in the outer envelope.
    let responseBytes genesis metadata (events: int list) =
        if metadata < 9 || metadata > MaximumResponseMetadataBytes || events.IsEmpty || events.Length > MaximumEvents
           || (genesis && events.Length <> 1) then Error "supplier-size-refused"
        else
            let eventMaximum = if genesis then MaximumGenesisBytes else MaximumEventBytes
            let envelopeMaximum = envelopeBytes eventMaximum MaximumSignatureBytes
            let encoded = events |> List.map (fun length ->
                match envelopeMaximum with
                | Ok maximum when length > 0 && length <= maximum -> base64UrlBytes length
                | _ -> Error "supplier-size-refused")
            if encoded |> List.exists Result.isError then Error "supplier-size-refused"
            else
                let total = metadata + 12 + (events.Length - 1) + (encoded |> List.sumBy (function Ok size -> size + 2 | _ -> 0))
                if total > MaximumPayloadBytes then Error "supplier-size-refused" else Ok total

type PortableSupplierProfile = LegacyInstalledProfile | SignedHoldV1Profile
type PortableSupplierEffect = CreateStoppedAllocation | StartOriginalAllocation | RetireOriginalAllocation

/// An inert projection of independently verified operation admission, not an authentication token.
type PortableSupplierEffectGrant =
    { GrantId: string
      Effect: PortableSupplierEffect
      IntentDigest: string
      ReservationDigest: string option
      OperationId: string option
      NotBefore: DateTimeOffset
      NotAfter: DateTimeOffset
      ConsumedOrUncertain: bool }

type PortableSupplierEffectState =
    { Profile: PortableSupplierProfile
      IntentDigest: string
      ReservationDigest: string option
      OperationId: string option
      OriginalDeadline: DateTimeOffset
      DurableIntent: bool
      DurableOriginals: bool
      StoppedAndIdentityUnchanged: bool
      SourcesUnchanged: bool
      DurableAuthenticatedReceiverAcceptance: bool
      LaunchIntentOrStartAlreadyRecorded: bool
      OriginalWorkAndReservesFit: bool
      CapacityReserved: bool
      ProtocolFits: bool
      Retirement: PortableHostRetirementDecision option }

[<RequireQualifiedAccess>]
module PortableSupplierEffectPolicy =
    /// Pure predicates only. Production must authenticate/consume the exact grant and persist intent
    /// under the existing operation lock immediately before its effect. No installed caller exists.
    let admit now effect (state: PortableSupplierEffectState) (admission: PortableEvidence<PortableSupplierEffectGrant>) =
        let present value = not (String.IsNullOrWhiteSpace value)
        let boundIdentity =
            (state.ReservationDigest |> Option.exists present)
            && (state.OperationId |> Option.exists present)
        let matches (grant: PortableSupplierEffectGrant) =
            present grant.GrantId && present state.IntentDigest
            && grant.Effect = effect && grant.IntentDigest = state.IntentDigest
            && grant.ReservationDigest = state.ReservationDigest && grant.OperationId = state.OperationId
            && not grant.ConsumedOrUncertain && now >= grant.NotBefore && now < grant.NotAfter
            && now < state.OriginalDeadline
        match state.Profile, admission with
        | SignedHoldV1Profile, EvidenceKnown grant when matches grant && state.ProtocolFits && state.CapacityReserved && state.DurableIntent ->
            match effect with
            | CreateStoppedAllocation when state.ReservationDigest.IsNone && state.OperationId.IsNone
                                           && state.SourcesUnchanged && state.OriginalWorkAndReservesFit -> HostEligible
            | StartOriginalAllocation when boundIdentity && state.DurableOriginals && state.StoppedAndIdentityUnchanged
                                           && state.SourcesUnchanged && state.DurableAuthenticatedReceiverAcceptance
                                           && not state.LaunchIntentOrStartAlreadyRecorded && state.OriginalWorkAndReservesFit -> HostEligible
            | RetireOriginalAllocation when boundIdentity && state.DurableOriginals && state.StoppedAndIdentityUnchanged
                                            && state.SourcesUnchanged
                                            && (state.Retirement |> Option.exists (fun decision -> decision.OuterCleanup = HostEligible)) -> HostEligible
            | _ -> HostHeld "supplier effect prerequisites unresolved"
        | _ -> HostHeld "exact fresh one-use effect admission and isolated signed profile required"

    /// Stop/settlement preserves data, regardless of start failure, loss, expiry or reconciliation.
    /// Deletion always requires a separate RetireOriginalAllocation decision and effect admission.
    let settle profile =
        match profile with
        | SignedHoldV1Profile -> Ok "stop and observe only; retain allocation and selected data"
        | LegacyInstalledProfile -> Error "legacy profile excluded; preserve its installed source and behavior"

/// Source-only receiver admission. These snapshots cannot authorize a runner, ACK or host effect.
type PortableSupplierAdmissionPhase =
    | SupplierIntentStored | SupplierCreationPinned | SupplierAwaitingGenesis
    | SupplierGenesisAccepted | SupplierAckPrepared | SupplierAwaitingActivation | SupplierHostExecution

type PortableSupplierAdmissionOriginal =
    { Command: PortableWorkspaceCommand
      QualifiedImage: string
      IntentBase64: string
      IntentDigest: string
      TrustPolicyDigest: string
      SupplierSourceRevision: string
      ReceiverEpoch: Guid }

type PortableSupplierAdmissionChallenge =
    { ChallengeId: Guid
      Digest: string
      BytesBase64: string
      Purpose: string
      ReceiverEpoch: Guid
      IssuedAt: DateTimeOffset
      NotAfter: DateTimeOffset
      IssuedMonotonic: int64
      DeadlineMonotonic: int64
      ExpectedHead: string option }

type PortableSupplierJournalVersion = SupplierJournalV2 | SupplierJournalV3
type PortableSupplierChallengeKind = SupplierWireV1 | SupplierNativeProofV1

/// Inert expected joins supplied by a caller; no host identity is inferred from a workspace command.
type PortableSupplierNativeProofContext =
    { Original: PortableSupplierAdmissionOriginal
      OperationId: Guid
      ReservationDigest: string
      ProofEventHead: string
      NativeReceiptSha256: string
      HostLeaseNotAfter: DateTimeOffset }

/// Exact canonical C1 bytes and raw SHA256, without an authentication or effect witness.
type PortableSupplierNativeProofChallenge =
    { Context: PortableSupplierNativeProofContext
      ChallengeId: Guid
      Digest: string
      BytesBase64: string
      ReceiverEpoch: Guid
      IssuedAt: DateTimeOffset
      NotAfter: DateTimeOffset
      IssuedMonotonic: int64
      DeadlineMonotonic: int64 }

type PortableSupplierPendingChallenge =
    | WireV1 of PortableSupplierAdmissionChallenge
    | NativeProofV1 of PortableSupplierNativeProofChallenge
    member this.Kind = match this with WireV1 _ -> SupplierWireV1 | NativeProofV1 _ -> SupplierNativeProofV1
    member this.ChallengeId = match this with WireV1 c -> c.ChallengeId | NativeProofV1 c -> c.ChallengeId
    member this.Digest = match this with WireV1 c -> c.Digest | NativeProofV1 c -> c.Digest
    member this.BytesBase64 = match this with WireV1 c -> c.BytesBase64 | NativeProofV1 c -> c.BytesBase64

type PortableSupplierTranscriptKind = SupplierCreation | SupplierGenesis | SupplierAppend | SupplierUnchanged

/// Contract of the trusted adapter, not an implementation of signature/schema verification.
/// All canonical bytes, signatures, full original C/R and lifecycle joins must be checked together.
type PortableSupplierTranscriptObservation =
    { Kind: PortableSupplierTranscriptKind
      IntentDigest: string
      TrustPolicyDigest: string
      ChallengeDigest: string
      Purpose: string
      OriginalReservation: PortableHostReservation
      Identity: PortableHostSupplierIdentity
      OuterImageId: string
      SupplierSourceRevision: string
      CreationBase64: string
      CreationReceiptBase64: string
      ReservationBase64: string
      ResponseBase64: string
      EventsBase64: string list
      FromHead: string option
      ToHead: string option
      Sequence: uint64
      CollectedAt: DateTimeOffset }

type IPortableSupplierTranscriptAuthenticator =
    abstract Authenticate: exactEnvelope: byte array -> Result<PortableSupplierTranscriptObservation, string>

/// Only the validating factory can construct this source witness. No production projection exists.
type PortableSupplierVerifiedTranscript =
    private | SupplierVerifiedTranscript of uint64 * string * PortableSupplierTranscriptObservation
    member this.ExpectedRevision = let (SupplierVerifiedTranscript(revision, _, _)) = this in revision
    member this.EnvelopeSha256 = let (SupplierVerifiedTranscript(_, digest, _)) = this in digest
    member this.Observation = let (SupplierVerifiedTranscript(_, _, value)) = this in value

type PortableSupplierAdmissionState =
    { JournalVersion: PortableSupplierJournalVersion
      Revision: uint64
      Original: PortableSupplierAdmissionOriginal
      Phase: PortableSupplierAdmissionPhase
      Challenge: PortableSupplierPendingChallenge option
      ChallengeLedger: (PortableSupplierChallengeKind * Guid * string * string) list
      Pinned: PortableSupplierTranscriptObservation option
      Head: string option
      Sequence: uint64
      EventsBase64: string list
      ResponsesBase64: string list
      CheckpointBase64: string option
      AckDraftBase64: string option
      FirstFailure: string option
      Conflicts: Set<string> }

[<RequireQualifiedAccess>]
module PortableSupplierAdmission =
    [<Literal>]
    let MaximumRecordBytes = 1048576
    [<Literal>]
    let MaximumSupplierBytes = 524288
    [<Literal>]
    let MaximumTranscriptBytes = 393216

    let private digestBytes (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let private domain kind (bytes: byte array) =
        Array.append (System.Text.Encoding.ASCII.GetBytes("fsgg.v3." + kind + "/1\000")) bytes |> digestBytes
    let private digest (value: string) =
        not (isNull value) && value.Length = 64 && (value |> Seq.forall (fun c -> "0123456789abcdef".Contains c))
    let private present (value: string) = not (String.IsNullOrWhiteSpace value) && value.Length <= 4096
    let private raw maximum (value: string) =
        if isNull value || value.Length > ((maximum + 2) / 3) * 4 then Error "supplier-admission-size"
        else
            try
                let bytes = Convert.FromBase64String value
                if bytes.Length = 0 || bytes.Length > maximum || Convert.ToBase64String bytes <> value then
                    Error "supplier-admission-bytes"
                else Ok bytes
            with :? FormatException -> Error "supplier-admission-bytes"
    let private validRaw maximum value = raw maximum value |> Result.isOk

    let private utcMicroseconds (value: DateTimeOffset) = value.Offset = TimeSpan.Zero && value.Ticks % 10L = 0L
    let private nativeBytes (challenge: PortableSupplierNativeProofChallenge) =
        let context = challenge.Context
        let date (value: DateTimeOffset) = value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", Globalization.CultureInfo.InvariantCulture)
        // Every interpolated value is a validated UUID, digest or fixed-format UTC value.
        System.Text.Encoding.ASCII.GetBytes(
            "{\"challenge_id\":\"" + challenge.ChallengeId.ToString("D")
            + "\",\"intent_digest\":\"" + context.Original.IntentDigest
            + "\",\"issued_at\":\"" + date challenge.IssuedAt
            + "\",\"native_receipt_sha256\":\"" + context.NativeReceiptSha256
            + "\",\"not_after\":\"" + date challenge.NotAfter
            + "\",\"operation_id\":\"" + context.OperationId.ToString("D")
            + "\",\"profile\":\"signed-hold-v1\",\"proof_event_head\":\"" + context.ProofEventHead
            + "\",\"receiver_epoch\":\"" + challenge.ReceiverEpoch.ToString("D")
            + "\",\"reservation_digest\":\"" + context.ReservationDigest
            + "\",\"schema\":\"fsgg.v3.native-proof-challenge/1\"}\n")

    let private validNative (original: PortableSupplierAdmissionOriginal) (challenge: PortableSupplierNativeProofChallenge) =
        let context = challenge.Context
        context.Original = original && context.OperationId <> Guid.Empty
        && digest context.ReservationDigest && digest context.ProofEventHead && digest context.NativeReceiptSha256
        && utcMicroseconds context.HostLeaseNotAfter
        && challenge.ChallengeId <> Guid.Empty && challenge.ReceiverEpoch = original.ReceiverEpoch
        && utcMicroseconds challenge.IssuedAt && utcMicroseconds challenge.NotAfter
        && challenge.IssuedAt < challenge.NotAfter && challenge.NotAfter <= original.Command.Deadline
        && challenge.NotAfter <= context.HostLeaseNotAfter
        && challenge.IssuedMonotonic >= 0L && challenge.DeadlineMonotonic > challenge.IssuedMonotonic
        && digest challenge.Digest
        && (match raw 45000 challenge.BytesBase64 with
            | Ok bytes -> bytes = nativeBytes challenge && digestBytes bytes = challenge.Digest
            | _ -> false)
    let private sameOriginal (original: PortableSupplierAdmissionOriginal) (value: PortableSupplierTranscriptObservation) =
        value.IntentDigest = original.IntentDigest && value.TrustPolicyDigest = original.TrustPolicyDigest
        && value.OriginalReservation.Binding.Command = original.Command
        && value.OriginalReservation.Binding.QualifiedImage = original.QualifiedImage
        && value.SupplierSourceRevision = original.SupplierSourceRevision
    let private transcriptBytes (state: PortableSupplierAdmissionState) =
        let pinned =
            state.Pinned
            |> Option.map (fun p -> [p.CreationBase64; p.CreationReceiptBase64; p.ReservationBase64; p.ResponseBase64] @ p.EventsBase64)
            |> Option.defaultValue []
        [state.Original.IntentBase64] @ pinned @ (state.Challenge |> Option.map (fun p -> p.BytesBase64) |> Option.toList)
        @ state.EventsBase64 @ state.ResponsesBase64
        @ (state.CheckpointBase64 |> Option.toList) @ (state.AckDraftBase64 |> Option.toList)
    /// Validate bounded immutable source data. It is never a durable/authentication/effect predicate.
    let validate (state: PortableSupplierAdmissionState) =
        try
            if isNull (box state) || isNull (box state.Original) then Error "supplier-admission-state-refused"
            else
                let decoded = transcriptBytes state |> List.map (raw 65536)
                let total = decoded |> List.sumBy (function Ok bytes -> int64 bytes.Length | Error _ -> int64 MaximumRecordBytes)
                let validLedger =
                    state.ChallengeLedger |> List.forall (fun (kind, id, hash, status) ->
                        (state.JournalVersion = SupplierJournalV3 || kind = SupplierWireV1)
                        && id <> Guid.Empty && digest hash && (status = "consumed" || status = "abandoned"))
                let uniqueIds = state.ChallengeLedger |> List.map (fun (_, id, _, _) -> id) |> Set.ofList
                let validChallenge =
                    state.Challenge |> Option.forall (fun pending ->
                        let valid =
                            match pending with
                            | NativeProofV1 challenge -> state.JournalVersion = SupplierJournalV3 && validNative state.Original challenge
                            | WireV1 challenge ->
                                challenge.ChallengeId <> Guid.Empty && digest challenge.Digest
                                && challenge.ReceiverEpoch = state.Original.ReceiverEpoch && challenge.ExpectedHead = state.Head
                                && challenge.IssuedAt < challenge.NotAfter && challenge.NotAfter <= state.Original.Command.Deadline
                                && challenge.IssuedMonotonic >= 0L && challenge.DeadlineMonotonic > challenge.IssuedMonotonic
                                && (match raw 45000 challenge.BytesBase64 with Ok bytes -> domain "challenge" bytes = challenge.Digest | _ -> false)
                        valid && not (uniqueIds.Contains pending.ChallengeId))
                if state.Revision = 0UL
                   || not (present state.Original.Command.WorkspaceScope && present state.Original.Command.IdempotencyId)
                   || state.Original.ReceiverEpoch = Guid.Empty || not (digest state.Original.IntentDigest && digest state.Original.TrustPolicyDigest)
                   || not (present state.Original.QualifiedImage && present state.Original.SupplierSourceRevision)
                   || state.EventsBase64.Length > 1024 || state.ChallengeLedger.Length > 128 || not validLedger
                   || uniqueIds.Count <> state.ChallengeLedger.Length || not validChallenge
                   || state.ResponsesBase64.Length > 128 || state.Conflicts.Count > 1024
                   || (state.Conflicts |> Seq.exists (present >> not))
                   || (state.FirstFailure |> Option.exists (present >> not))
                   || (decoded |> List.exists Result.isError) || total > int64 MaximumTranscriptBytes
                   || (state.Head |> Option.exists (digest >> not))
                   || (state.Pinned |> Option.exists (sameOriginal state.Original >> not)) then Error "supplier-admission-state-refused"
                else
                    match raw 45000 state.Original.IntentBase64 with
                    | Ok bytes when domain "intent" bytes = state.Original.IntentDigest -> Ok state
                    | _ -> Error "supplier-admission-state-refused"
        with _ -> Error "supplier-admission-state-refused"

    let private beginIntentCore (original: PortableSupplierAdmissionOriginal) =
        let state =
            { JournalVersion = SupplierJournalV2; Revision = 1UL; Original = original; Phase = SupplierIntentStored; Challenge = None
              ChallengeLedger = []; Pinned = None; Head = None; Sequence = 0UL; EventsBase64 = []
              ResponsesBase64 = []; CheckpointBase64 = None; AckDraftBase64 = None
              FirstFailure = None; Conflicts = Set.empty }
        match raw 45000 original.IntentBase64 with
        | Ok bytes when domain "intent" bytes = original.IntentDigest -> validate state
        | _ -> Error "supplier-admission-original-intent-refused"

    let private next (state: PortableSupplierAdmissionState) =
        if state.Revision = UInt64.MaxValue then Error "supplier-admission-revision-overflow"
        else Ok { state with Revision = state.Revision + 1UL }

    let private issueChallengeCore (challenge: PortableSupplierAdmissionChallenge) (state: PortableSupplierAdmissionState) =
        let expectedPurpose = if state.Phase = SupplierIntentStored then "creation" elif state.Head.IsNone then "genesis-readback" else "state-readback"
        if state.FirstFailure.IsSome || not state.Conflicts.IsEmpty || state.Challenge.IsSome
           || state.ChallengeLedger.Length >= 128 || challenge.ChallengeId = Guid.Empty
           || (state.ChallengeLedger |> List.exists (fun (_, id, _, _) -> id = challenge.ChallengeId))
           || challenge.ReceiverEpoch <> state.Original.ReceiverEpoch || challenge.ExpectedHead <> state.Head
           || challenge.Purpose <> expectedPurpose && not (expectedPurpose = "state-readback" && challenge.Purpose = "retirement-readback")
           || challenge.IssuedAt >= challenge.NotAfter || challenge.NotAfter > state.Original.Command.Deadline
           || challenge.IssuedMonotonic < 0L || challenge.DeadlineMonotonic <= challenge.IssuedMonotonic then
            Error "supplier-admission-challenge-refused"
        else
            match raw 45000 challenge.BytesBase64 with
            | Ok bytes when domain "challenge" bytes = challenge.Digest ->
                next { state with Challenge = Some(WireV1 challenge)
                                  Phase = if state.Phase = SupplierCreationPinned then SupplierAwaitingGenesis else state.Phase }
                |> Result.bind validate
            | _ -> Error "supplier-admission-challenge-bytes-refused"

    /// The trusted dependency is explicitly injected. This source supplies no production adapter.
    let private verifyCore (authenticator: IPortableSupplierTranscriptAuthenticator) now monotonic
               (state: PortableSupplierAdmissionState) (envelope: byte array) =
        let refusal = Error "supplier-admission-verification-refused"
        if isNull (box authenticator) || isNull envelope || envelope.Length = 0 || envelope.Length > 65536 then refusal
        else
            match state.Challenge with
            | None | Some(NativeProofV1 _) -> refusal
            | Some(WireV1 challenge) when now < challenge.IssuedAt || now >= challenge.NotAfter
                                  || monotonic < challenge.IssuedMonotonic || monotonic >= challenge.DeadlineMonotonic
                                  || state.FirstFailure.IsSome || not state.Conflicts.IsEmpty -> refusal
            | Some(WireV1 challenge) ->
                try
                    let snapshot = Array.copy envelope
                    match authenticator.Authenticate(Array.copy snapshot) with
                    | Error _ -> refusal
                    | Ok value ->
                        let sourceBytes = if value.Kind = SupplierCreation then value.CreationReceiptBase64 else value.ResponseBase64
                        let identity = value.Identity
                        let purposeMatches =
                            value.Purpose = challenge.Purpose
                            && (match value.Kind, state.Phase, challenge.Purpose with
                                | SupplierCreation, SupplierIntentStored, "creation" -> true
                                | SupplierGenesis, SupplierAwaitingGenesis, "genesis-readback" -> true
                                | (SupplierAppend | SupplierUnchanged), SupplierGenesisAccepted, ("state-readback" | "retirement-readback") -> true
                                | _ -> false)
                        let valid = purposeMatches && sameOriginal state.Original value && value.ChallengeDigest = challenge.Digest
                                    && value.CollectedAt >= challenge.IssuedAt && value.CollectedAt <= now
                                    && now - value.CollectedAt <= TimeSpan.FromSeconds 30.
                                    && present identity.Issuer && present identity.ContainerId && identity.AllocationInode <> 0UL
                                    && digest value.OuterImageId && validRaw 6000 value.CreationBase64
                                    && validRaw 18761 value.CreationReceiptBase64 && validRaw 6000 value.ReservationBase64
                                    && value.EventsBase64.Length <= 16
                                    && (value.EventsBase64 |> List.forall (validRaw 26761))
                        match raw 65536 sourceBytes with
                        | Ok bytes when valid && bytes = snapshot ->
                            Ok(SupplierVerifiedTranscript(state.Revision, digestBytes snapshot, value))
                        | _ -> refusal
                with _ -> refusal

    let private hold reason conflict state =
        next { state with FirstFailure = state.FirstFailure |> Option.orElse (Some reason)
                          Conflicts = state.Conflicts.Add conflict }
        |> Result.bind validate

    /// No-effect reducer. Even a successful source transition is not ACK or execution authority.
    let private acceptCore (verified: PortableSupplierVerifiedTranscript) (state: PortableSupplierAdmissionState) =
        let value = verified.Observation
        if verified.ExpectedRevision <> state.Revision then Error "supplier-admission-stale-revision"
        elif not (sameOriginal state.Original value) then Error "supplier-admission-originals-changed"
        else
            match state.Challenge with
            | None -> Error "supplier-admission-no-pending-challenge"
            | Some(NativeProofV1 _) -> Error "supplier-admission-no-pending-wire-challenge"
            | Some(WireV1 challenge) when value.ChallengeDigest <> challenge.Digest || value.Purpose <> challenge.Purpose -> Error "supplier-admission-challenge-changed"
            | Some(WireV1 challenge) ->
                let pinnedMatches = state.Pinned |> Option.forall (fun pinned ->
                    pinned.Identity = value.Identity && pinned.OuterImageId = value.OuterImageId
                    && pinned.CreationBase64 = value.CreationBase64 && pinned.CreationReceiptBase64 = value.CreationReceiptBase64
                    && pinned.ReservationBase64 = value.ReservationBase64 && pinned.OriginalReservation = value.OriginalReservation)
                if not pinnedMatches then hold "supplier original pins changed" verified.EnvelopeSha256 state
                else
                    let consumed = (SupplierWireV1, challenge.ChallengeId, challenge.Digest, "consumed") :: state.ChallengeLedger
                    let candidate phase head sequence events =
                        next { state with Phase = phase; Challenge = None; ChallengeLedger = consumed
                                          Pinned = Some value; Head = head; Sequence = sequence; EventsBase64 = events
                                          ResponsesBase64 = (if value.Kind = SupplierCreation then state.ResponsesBase64 else state.ResponsesBase64 @ [value.ResponseBase64]) }
                        |> Result.bind validate
                    match value.Kind, state.Phase with
                    | SupplierCreation, SupplierIntentStored when value.FromHead.IsNone && value.ToHead.IsNone && value.EventsBase64.IsEmpty ->
                        candidate SupplierCreationPinned None 0UL []
                    | SupplierGenesis, SupplierAwaitingGenesis when value.FromHead.IsNone && value.Sequence = 0UL && value.EventsBase64.Length = 1 ->
                        match raw 26761 value.EventsBase64.Head with
                        | Ok bytes when value.ToHead = Some(domain "signed-envelope" bytes) ->
                            candidate SupplierGenesisAccepted value.ToHead 0UL value.EventsBase64
                        | _ -> Error "supplier-admission-genesis-head-refused"
                    | SupplierUnchanged, SupplierGenesisAccepted when state.Head.IsSome && value.FromHead = state.Head && value.ToHead = state.Head
                                                 && value.Sequence = state.Sequence && value.EventsBase64 = (state.EventsBase64 |> List.rev |> List.truncate 1 |> List.rev) ->
                        candidate state.Phase state.Head state.Sequence state.EventsBase64
                    | SupplierAppend, SupplierGenesisAccepted when state.Head.IsSome && value.FromHead = state.Head && not value.EventsBase64.IsEmpty
                                              && state.Sequence <= UInt64.MaxValue - uint64 value.EventsBase64.Length
                                              && value.Sequence = state.Sequence + uint64 value.EventsBase64.Length ->
                        match raw 18761 (List.last value.EventsBase64) with
                        | Ok bytes when value.ToHead = Some(domain "signed-envelope" bytes) ->
                            candidate state.Phase value.ToHead value.Sequence (state.EventsBase64 @ value.EventsBase64)
                        | _ -> Error "supplier-admission-append-head-refused"
                    | _ -> hold "supplier gap, fork or unsupported phase" verified.EnvelopeSha256 state

    /// A restart cannot revive an outstanding challenge in a new monotonic clock domain.
    let private abandonChallengeCore newEpoch (state: PortableSupplierAdmissionState) =
        if newEpoch = Guid.Empty || newEpoch = state.Original.ReceiverEpoch then Error "supplier-admission-epoch-refused"
        else
            let ledger =
                match state.Challenge with
                | None -> state.ChallengeLedger
                | Some challenge -> (challenge.Kind, challenge.ChallengeId, challenge.Digest, "abandoned") :: state.ChallengeLedger
            next { state with Original = { state.Original with ReceiverEpoch = newEpoch }; Challenge = None; ChallengeLedger = ledger }
            |> Result.bind validate

    let beginIntent original =
        try beginIntentCore original with _ -> Error "supplier-admission-original-refused"
    /// Explicit fresh-original selection. Stage still refuses an occupied journal key.
    let beginIntentV3 original = beginIntent original |> Result.map (fun state -> { state with JournalVersion = SupplierJournalV3 })
    let issueChallenge challenge state =
        try issueChallengeCore challenge state with _ -> Error "supplier-admission-challenge-refused"
    /// Caller assertions only: this stores no authenticated proof or production clock/nonce policy.
    let issueNativeProofChallenge (expectedContext: PortableSupplierNativeProofContext)
                                  (challenge: PortableSupplierNativeProofChallenge) (state: PortableSupplierAdmissionState) =
        try
            if state.JournalVersion <> SupplierJournalV3 || state.Challenge.IsSome
               || state.FirstFailure.IsSome || not state.Conflicts.IsEmpty
               || state.ChallengeLedger.Length >= 128 || challenge.Context <> expectedContext
               || (state.ChallengeLedger |> List.exists (fun (_, id, _, _) -> id = challenge.ChallengeId))
               || not (validNative state.Original challenge) then Error "supplier-native-proof-challenge-refused"
            else next { state with Challenge = Some(NativeProofV1 challenge) } |> Result.bind validate
        with _ -> Error "supplier-native-proof-challenge-refused"
    let verify authenticator now monotonic state envelope =
        try verifyCore authenticator now monotonic state envelope with _ -> Error "supplier-admission-verification-refused"
    let accept verified state =
        try acceptCore verified state with _ -> Error "supplier-admission-result-refused"
    let abandonChallenge epoch state =
        try abandonChallengeCore epoch state with _ -> Error "supplier-admission-restart-refused"

    /// Durability is unqualified in this source slice. Never prepare a usable ACK or hand off work.
    let prepareAck (_state: PortableSupplierAdmissionState) : Result<byte array, string> = Error "supplier-admission-durability-unavailable"
    let reserveHost (_state: PortableSupplierAdmissionState) : Result<unit, string> = Error "supplier-admission-production-handoff-unavailable"
