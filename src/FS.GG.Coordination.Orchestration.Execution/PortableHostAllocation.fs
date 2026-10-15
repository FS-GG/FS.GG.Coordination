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
        if metadata < 4 || metadata > MaximumResponseMetadataBytes || events.IsEmpty || events.Length > MaximumEvents
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
