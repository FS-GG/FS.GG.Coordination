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

/// Inert regular-file content commitment; it conveys no filesystem or authentication evidence.
type PortableSupplierManifestMember =
    { Bytes: uint64
      Path: string
      Sha256: string }

/// Canonical opt-in companion only. Legacy scalar manifests are never upgraded by this type.
type PortableSupplierManifest =
    private | CanonicalSupplierManifest of PortableSupplierManifestMember list * PortableHostExportManifest

[<RequireQualifiedAccess>]
module PortableSupplierManifest =
    [<Literal>]
    let Schema = "fsgg.v3.selected-manifest/1"
    [<Literal>]
    let MaximumBytes = 6000

    let private encode (members: PortableSupplierManifestMember list) =
        let entries =
            members |> List.map (fun m ->
                "{\"bytes\":\"" + m.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\",\"path\":\"" + m.Path + "\",\"sha256\":\"" + m.Sha256 + "\"}")
            |> String.concat ","
        System.Text.Encoding.ASCII.GetBytes("{\"members\":[" + entries + "],\"schema\":\"" + Schema + "\"}\n")

    /// Validate caller values without sorting, truncation or path normalization.
    let create (members: PortableSupplierManifestMember list) =
        let validPath (path: string) =
            not (isNull path) && path.Length > 0 && path.Length <= 128
            && (let segments = path.Split('/')
                segments.Length <= 8
                && (segments |> Array.forall (fun segment ->
                    segment <> "." && segment <> ".."
                    && System.Text.RegularExpressions.Regex.IsMatch(segment, "\\A[A-Za-z0-9._-]+\\z"))))
        let validHash (value: string) =
            not (isNull value) && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z")
        if isNull (box members) || members.Length < 1 || members.Length > 16 then Error "manifest-member-count"
        elif members |> List.exists (fun m -> isNull (box m) || not (validPath m.Path) || not (validHash m.Sha256)) then
            Error "manifest-member"
        else
            let paths = members |> List.map _.Path
            let ordered = paths |> List.pairwise |> List.forall (fun (a,b) -> StringComparer.Ordinal.Compare(a,b) < 0)
            let folded = paths |> List.map (fun path -> path.ToUpperInvariant())
            let collision = (folded |> Set.ofList |> Set.count) <> folded.Length
            let ancestor = paths |> List.exists (fun parent -> paths |> List.exists (fun child -> child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)))
            let mutable total = 0UL
            let mutable overflow = false
            for m in members do
                if UInt64.MaxValue - total < m.Bytes then overflow <- true
                else total <- total + m.Bytes
            if not ordered || collision || ancestor then Error "manifest-path-join"
            elif overflow then Error "manifest-total-overflow"
            else
                let raw = encode members
                if raw.Length > MaximumBytes then Error "manifest-size"
                else
                    let domain = System.Text.Encoding.ASCII.GetBytes("fsgg.v3.manifest/1\000")
                    let digest = SHA256.HashData(Array.append domain raw) |> Convert.ToHexString |> _.ToLowerInvariant()
                    Ok (CanonicalSupplierManifest(members, { Digest = digest; MemberCount = uint64 members.Length; TotalBytes = total }))

    /// Returns a fresh buffer; caller mutation cannot change the validated value.
    let serialize (CanonicalSupplierManifest(members, _)) = encode members
    let summary (CanonicalSupplierManifest(_, value)) = value
    let members (CanonicalSupplierManifest(values, _)) = values

    /// Strict C1: exact compact sorted ASCII JSON and one LF, no alternate encodings.
    let parse (input: byte array) =
        if isNull input || input.Length < 1 || input.Length > MaximumBytes then Error "manifest-size"
        else
            let raw = Array.copy input
            if raw |> Array.exists (fun b -> b > 127uy) then Error "manifest-ascii"
            else
                try
                    use document = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(raw), System.Text.Json.JsonDocumentOptions(MaxDepth = 8))
                    let fields names (value: System.Text.Json.JsonElement) =
                        value.ValueKind = System.Text.Json.JsonValueKind.Object
                        && (value.EnumerateObject() |> Seq.map _.Name |> Seq.toList |> List.sort) = List.sort names
                    let root = document.RootElement
                    if not (fields ["members"; "schema"] root)
                       || root.GetProperty("schema").ValueKind <> System.Text.Json.JsonValueKind.String
                       || root.GetProperty("schema").GetString() <> Schema
                       || root.GetProperty("members").ValueKind <> System.Text.Json.JsonValueKind.Array then
                        Error "manifest-fields"
                    else
                        let mutable invalid = false
                        let values =
                            root.GetProperty("members").EnumerateArray()
                            |> Seq.map (fun value ->
                                if not (fields ["bytes"; "path"; "sha256"] value)
                                   || (["bytes"; "path"; "sha256"] |> List.exists (fun key -> value.GetProperty(key).ValueKind <> System.Text.Json.JsonValueKind.String)) then
                                    invalid <- true
                                    { Bytes = 0UL; Path = ""; Sha256 = "" }
                                else
                                    let text = value.GetProperty("bytes").GetString()
                                    let mutable size = 0UL
                                    if not (System.Text.RegularExpressions.Regex.IsMatch(text, "\\A(0|[1-9][0-9]*)\\z"))
                                       || not (UInt64.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, &size)) then invalid <- true
                                    { Bytes = size; Path = value.GetProperty("path").GetString(); Sha256 = value.GetProperty("sha256").GetString() })
                            |> Seq.toList
                        if invalid then Error "manifest-member-fields"
                        else
                            create values |> Result.bind (fun value ->
                                if serialize value = raw then Ok value else Error "manifest-noncanonical")
                with
                | :? System.Text.Json.JsonException -> Error "manifest-json"
                | :? InvalidOperationException -> Error "manifest-fields"
                | :? ArgumentException -> Error "manifest-fields"

    /// Optional exact bytes join only: none supplies no census; selected supplies no durable readback.
    let validateSelection selection raw =
        match selection, raw with
        | NoSelectedOutputs, None -> Ok None
        | NoSelectedOutputs, Some _ -> Error "none-with-manifest"
        | SelectedOutputs _, None -> Error "selected-without-manifest"
        | SelectedOutputs expected, Some bytes ->
            parse bytes |> Result.bind (fun value ->
                if summary value = expected then Ok (Some value) else Error "manifest-selection-mismatch")

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

/// Closed canonical companion primitives. These helpers create data, never verified capabilities.
module private PortableSupplierC1 =
    open System.Text.Json
    let fail () = invalidOp "supplier-companion-refused"
    let hash (raw: byte array) = Convert.ToHexString(SHA256.HashData raw).ToLowerInvariant()
    let domain name raw = Array.append (System.Text.Encoding.ASCII.GetBytes("fsgg.v3." + name + "/1\000")) raw |> hash
    let quoted (value: string) =
        if isNull value then fail ()
        let text = System.Text.StringBuilder("\"")
        for c in value do
            match c with
            | '"' -> text.Append("\\\"") |> ignore
            | '\\' -> text.Append("\\\\") |> ignore
            | c when int c >= 32 && int c <= 126 -> text.Append c |> ignore
            | _ -> fail ()
        text.Append('"').ToString()
    let objectBytes (fields: (string * string) list) =
        let ordered = fields |> List.sortWith (fun (a,_) (b,_) -> StringComparer.Ordinal.Compare(a,b))
        if (ordered |> List.map fst |> Set.ofList |> Set.count) <> ordered.Length then fail ()
        System.Text.Encoding.ASCII.GetBytes("{" + (ordered |> List.map (fun (k,v) -> quoted k + ":" + v) |> String.concat ",") + "}\n")
    let rec canonical (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            objectBytes (value.EnumerateObject() |> Seq.map (fun p -> p.Name, canonical p.Value) |> Seq.toList)
            |> System.Text.Encoding.ASCII.GetString |> fun s -> s.TrimEnd('\n')
        | JsonValueKind.Array -> "[" + (value.EnumerateArray() |> Seq.map canonical |> String.concat ",") + "]"
        | JsonValueKind.String -> quoted (value.GetString())
        | JsonValueKind.Null -> "null"
        | _ -> fail ()
    let parse maximum (input: byte array) decode =
        try
            if isNull input || input.Length = 0 || input.Length > maximum then fail ()
            let snapshot = Array.copy input
            if snapshot |> Array.exists (fun b -> b > 127uy) then fail ()
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte>(snapshot), JsonDocumentOptions(MaxDepth = 16))
            if System.Text.Encoding.ASCII.GetBytes(canonical doc.RootElement + "\n") <> snapshot then fail ()
            Ok(decode doc.RootElement)
        with _ -> Error "supplier-companion-refused"
    let fields names (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then fail ()
        let actual = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if List.sort actual <> List.sort names then fail ()
    let text maximum (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.String then fail ()
        let s = value.GetString()
        if s.Length = 0 || s.Length > maximum || (s |> Seq.exists (fun c -> int c < 32 || int c > 126)) then fail ()
        s
    let digest value =
        let s = text 64 value
        if s.Length <> 64 || (s |> Seq.exists (fun c -> not ("0123456789abcdef".Contains c))) then fail ()
        s
    let u64 value =
        let s = text 20 value
        let mutable n = 0UL
        if not (System.Text.RegularExpressions.Regex.IsMatch(s, "\\A(0|[1-9][0-9]*)\\z"))
           || not (UInt64.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, &n)) then fail ()
        n
    let uuid value =
        let s = text 36 value
        let g = Guid.ParseExact(s, "D")
        if g = Guid.Empty || g.ToString("D") <> s then fail ()
        g
    let utc value =
        let s = text 27 value
        let t = DateTimeOffset.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal)
        if t.Offset <> TimeSpan.Zero || t.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", Globalization.CultureInfo.InvariantCulture) <> s then fail ()
        t
    let optional decode (value: JsonElement) = if value.ValueKind = JsonValueKind.Null then None else Some(decode value)
    let number (value: uint64) = quoted (value.ToString(Globalization.CultureInfo.InvariantCulture))
    let option encode = function None -> "null" | Some v -> encode v

/// Immutable structural checkpoint fields, all recomputed by the retained-exchange caller.
/// A parsed instance is neither an authenticated candidate nor a commit witness.
type PortableSupplierCheckpointCommon =
    { Provenance: string
      JournalKeySha256: string
      OriginalReceiverEpoch: Guid
      ReceiverEpoch: Guid
      OperationId: Guid
      IntentDigest: string
      CommandDigest: string
      OriginalIntentEnvelopeSha256: string
      CreationCoreDigest: string
      CreationReceiptDigest: string
      ReservationDigest: string
      SelectionDigest: string
      TrustPolicyDigest: string
      ReceiverSourceRevision: string
      SupplierSourceRevision: string
      SupplierSourceDigest: string
      CommandFenceGeneration: uint64
      ExpectedJournalRevision: uint64
      JournalRevision: uint64
      ExpectedStoreFence: uint64
      StoreFence: uint64
      PredecessorCheckpointSha256: string option
      PreviousHead: string option
      ObservedHead: string
      AcceptedSequence: uint64
      AcceptedRequestEnvelopeSha256: string
      AcceptedResultEnvelopeSha256: string
      AcceptedResponseEnvelopeSha256: string
      AcceptedChallengeDigest: string
      AcceptedChallengeId: Guid
      AcceptedChallengePurpose: string
      VerificationBundleSha256: string
      ControllerVerificationBundleSha256: string }

type PortableSupplierCheckpointSelection =
    | CheckpointNone
    | CheckpointSelected of manifestSha256: string * manifest: PortableHostExportManifest

type PortableSupplierCheckpointOutput =
    { GenesisCheckpointSha256: string
      Selection: PortableSupplierCheckpointSelection
      ProofSignedEnvelopeSha256: string
      ProofPayloadDigest: string
      NativeReceiptSha256: string
      NativeProofEnvelopeSha256: string
      NativeRequestEnvelopeSha256: string
      NativeResultEnvelopeSha256: string
      NativeChallengeDigest: string
      NativeChallengeId: Guid
      NativeReceiverEpoch: Guid
      NativeControllerVerificationBundleSha256: string }

type PortableSupplierGenesisCheckpoint =
    private | StructuralGenesisCheckpoint of PortableSupplierCheckpointCommon * string * string

type PortableSupplierOutputCheckpoint =
    private | StructuralOutputCheckpoint of PortableSupplierCheckpointCommon * PortableSupplierCheckpointOutput

[<RequireQualifiedAccess>]
module PortableSupplierCheckpoint =
    open PortableSupplierC1
    [<Literal>]
    let MaximumBytes = 16384
    let private commonFields c =
        [ "schema", quoted "" // replaced by the closed variant
          "profile", quoted "signed-hold-v1"
          "provenance", quoted c.Provenance
          "journal_key_sha256", quoted c.JournalKeySha256
          "journal_schema", quoted "fsgg.portable-execution-supplier-journal/4"
          "original_receiver_epoch", quoted (c.OriginalReceiverEpoch.ToString("D"))
          "receiver_epoch", quoted (c.ReceiverEpoch.ToString("D"))
          "operation_id", quoted (c.OperationId.ToString("D"))
          "intent_digest", quoted c.IntentDigest
          "command_digest", quoted c.CommandDigest
          "original_intent_envelope_sha256", quoted c.OriginalIntentEnvelopeSha256
          "creation_core_digest", quoted c.CreationCoreDigest
          "creation_receipt_digest", quoted c.CreationReceiptDigest
          "reservation_digest", quoted c.ReservationDigest
          "selection_digest", quoted c.SelectionDigest
          "trust_policy_digest", quoted c.TrustPolicyDigest
          "receiver_source_revision", quoted c.ReceiverSourceRevision
          "supplier_source_revision", quoted c.SupplierSourceRevision
          "supplier_source_digest", quoted c.SupplierSourceDigest
          "command_fence_generation", number c.CommandFenceGeneration
          "expected_journal_revision", number c.ExpectedJournalRevision
          "journal_revision", number c.JournalRevision
          "expected_store_fence", number c.ExpectedStoreFence
          "store_fence", number c.StoreFence
          "predecessor_checkpoint_sha256", option quoted c.PredecessorCheckpointSha256
          "previous_head", option quoted c.PreviousHead
          "observed_head", quoted c.ObservedHead
          "accepted_sequence", number c.AcceptedSequence
          "accepted_request_envelope_sha256", quoted c.AcceptedRequestEnvelopeSha256
          "accepted_result_envelope_sha256", quoted c.AcceptedResultEnvelopeSha256
          "accepted_response_envelope_sha256", quoted c.AcceptedResponseEnvelopeSha256
          "accepted_challenge_digest", quoted c.AcceptedChallengeDigest
          "accepted_challenge_id", quoted (c.AcceptedChallengeId.ToString("D"))
          "accepted_challenge_purpose", quoted c.AcceptedChallengePurpose
          "verification_bundle_sha256", quoted c.VerificationBundleSha256
          "controller_verification_bundle_sha256", quoted c.ControllerVerificationBundleSha256 ]
    let private encode schema common extra =
        (commonFields common |> List.filter (fun (k,_) -> k <> "schema")) @ ["schema", quoted schema] @ extra |> objectBytes
    let serializeGenesis (StructuralGenesisCheckpoint(c,head,payload)) =
        encode "fsgg.v3.receiver-genesis-checkpoint/2" c ["genesis_head", quoted head; "genesis_payload_digest", quoted payload]
    let serializeOutput (StructuralOutputCheckpoint(c,o)) =
        let selected = match o.Selection with
                       | CheckpointNone -> ["proof_kind", quoted "none"; "manifest_sha256", "null"; "manifest_digest", "null"; "member_count", "null"; "total_bytes", "null"]
                       | CheckpointSelected(raw,m) -> ["proof_kind", quoted "selected"; "manifest_sha256", quoted raw; "manifest_digest", quoted m.Digest; "member_count", number m.MemberCount; "total_bytes", number m.TotalBytes]
        encode "fsgg.v3.receiver-output-checkpoint/2" c
            (["genesis_checkpoint_sha256", quoted o.GenesisCheckpointSha256
              "proof_signed_envelope_sha256", quoted o.ProofSignedEnvelopeSha256
              "proof_payload_digest", quoted o.ProofPayloadDigest
              "native_receipt_sha256", quoted o.NativeReceiptSha256
              "native_proof_envelope_sha256", quoted o.NativeProofEnvelopeSha256
              "native_request_envelope_sha256", quoted o.NativeRequestEnvelopeSha256
              "native_result_envelope_sha256", quoted o.NativeResultEnvelopeSha256
              "native_challenge_digest", quoted o.NativeChallengeDigest
              "native_challenge_id", quoted (o.NativeChallengeId.ToString("D"))
              "native_receiver_epoch", quoted (o.NativeReceiverEpoch.ToString("D"))
              "native_controller_verification_bundle_sha256", quoted o.NativeControllerVerificationBundleSha256] @ selected)
    let private readCommon (root: System.Text.Json.JsonElement) =
        let get (name: string) = root.GetProperty name
        let str name = get name |> text 128
        let dig name = get name |> digest
        let num name = get name |> u64
        let uid name = get name |> uuid
        if str "profile" <> "signed-hold-v1" || str "journal_schema" <> "fsgg.portable-execution-supplier-journal/4"
           || not (List.contains (str "provenance") ["controlled-test"; "qualified-production"]) then fail ()
        for key in ["receiver_source_revision"; "supplier_source_revision"] do
            if not (System.Text.RegularExpressions.Regex.IsMatch(str key, "\\A[A-Za-z0-9_-]{1,128}\\z")) then fail ()
        let c =
            { Provenance = str "provenance"; JournalKeySha256 = dig "journal_key_sha256"
              OriginalReceiverEpoch = uid "original_receiver_epoch"; ReceiverEpoch = uid "receiver_epoch"; OperationId = uid "operation_id"
              IntentDigest = dig "intent_digest"; CommandDigest = dig "command_digest"; OriginalIntentEnvelopeSha256 = dig "original_intent_envelope_sha256"
              CreationCoreDigest = dig "creation_core_digest"; CreationReceiptDigest = dig "creation_receipt_digest"
              ReservationDigest = dig "reservation_digest"; SelectionDigest = dig "selection_digest"; TrustPolicyDigest = dig "trust_policy_digest"
              ReceiverSourceRevision = str "receiver_source_revision"; SupplierSourceRevision = str "supplier_source_revision"; SupplierSourceDigest = dig "supplier_source_digest"
              CommandFenceGeneration = num "command_fence_generation"; ExpectedJournalRevision = num "expected_journal_revision"; JournalRevision = num "journal_revision"
              ExpectedStoreFence = num "expected_store_fence"; StoreFence = num "store_fence"
              PredecessorCheckpointSha256 = get "predecessor_checkpoint_sha256" |> optional digest; PreviousHead = get "previous_head" |> optional digest
              ObservedHead = dig "observed_head"; AcceptedSequence = num "accepted_sequence"
              AcceptedRequestEnvelopeSha256 = dig "accepted_request_envelope_sha256"; AcceptedResultEnvelopeSha256 = dig "accepted_result_envelope_sha256"
              AcceptedResponseEnvelopeSha256 = dig "accepted_response_envelope_sha256"; AcceptedChallengeDigest = dig "accepted_challenge_digest"
              AcceptedChallengeId = uid "accepted_challenge_id"; AcceptedChallengePurpose = str "accepted_challenge_purpose"
              VerificationBundleSha256 = dig "verification_bundle_sha256"; ControllerVerificationBundleSha256 = dig "controller_verification_bundle_sha256" }
        if c.ExpectedJournalRevision = UInt64.MaxValue || c.JournalRevision <> c.ExpectedJournalRevision + 1UL
           || c.ExpectedStoreFence = UInt64.MaxValue || c.StoreFence <> c.ExpectedStoreFence + 1UL
           || c.ExpectedJournalRevision = 0UL || c.ExpectedStoreFence = 0UL then fail ()
        c
    let parseGenesis raw =
        parse MaximumBytes raw (fun root ->
            let c = readCommon root
            let head = root.GetProperty("genesis_head") |> digest
            let payload = root.GetProperty("genesis_payload_digest") |> digest
            let value = StructuralGenesisCheckpoint(c,head,payload)
            fields ((commonFields c |> List.map fst) @ ["genesis_head"; "genesis_payload_digest"]) root
            if root.GetProperty("schema").GetString() <> "fsgg.v3.receiver-genesis-checkpoint/2"
               || c.PreviousHead.IsSome || c.PredecessorCheckpointSha256.IsSome || c.AcceptedSequence <> 0UL
               || c.AcceptedChallengePurpose <> "genesis-readback" || c.ObservedHead <> head || serializeGenesis value <> raw then fail ()
            value)
    let parseOutput raw =
        parse MaximumBytes raw (fun root ->
            let c = readCommon root
            let dig (key: string) = root.GetProperty(key) |> digest
            let kind = root.GetProperty("proof_kind") |> text 8
            let selection =
                match kind with
                | "none" ->
                    for key in ["manifest_sha256"; "manifest_digest"; "member_count"; "total_bytes"] do
                        if root.GetProperty(key).ValueKind <> System.Text.Json.JsonValueKind.Null then fail ()
                    CheckpointNone
                | "selected" ->
                    let m = { Digest = dig "manifest_digest"; MemberCount = root.GetProperty("member_count") |> u64; TotalBytes = root.GetProperty("total_bytes") |> u64 }
                    if m.MemberCount = 0UL || m.MemberCount > 16UL then fail ()
                    CheckpointSelected(dig "manifest_sha256",m)
                | _ -> fail ()
            let o =
                { GenesisCheckpointSha256 = dig "genesis_checkpoint_sha256"; Selection = selection
                  ProofSignedEnvelopeSha256 = dig "proof_signed_envelope_sha256"; ProofPayloadDigest = dig "proof_payload_digest"
                  NativeReceiptSha256 = dig "native_receipt_sha256"; NativeProofEnvelopeSha256 = dig "native_proof_envelope_sha256"
                  NativeRequestEnvelopeSha256 = dig "native_request_envelope_sha256"; NativeResultEnvelopeSha256 = dig "native_result_envelope_sha256"
                  NativeChallengeDigest = dig "native_challenge_digest"; NativeChallengeId = root.GetProperty("native_challenge_id") |> uuid
                  NativeReceiverEpoch = root.GetProperty("native_receiver_epoch") |> uuid
                  NativeControllerVerificationBundleSha256 = dig "native_controller_verification_bundle_sha256" }
            let value = StructuralOutputCheckpoint(c,o)
            let specific = ["genesis_checkpoint_sha256"; "proof_kind"; "proof_signed_envelope_sha256"; "proof_payload_digest"; "manifest_sha256"; "manifest_digest"; "member_count"; "total_bytes"; "native_receipt_sha256"; "native_proof_envelope_sha256"; "native_request_envelope_sha256"; "native_result_envelope_sha256"; "native_challenge_digest"; "native_challenge_id"; "native_receiver_epoch"; "native_controller_verification_bundle_sha256"]
            fields ((commonFields c |> List.map fst) @ specific) root
            if root.GetProperty("schema").GetString() <> "fsgg.v3.receiver-output-checkpoint/2"
               || c.PredecessorCheckpointSha256 <> Some o.GenesisCheckpointSha256 || c.PreviousHead.IsNone
               || not (List.contains c.AcceptedChallengePurpose ["state-readback"; "retirement-readback"])
               || c.ReceiverEpoch <> o.NativeReceiverEpoch || serializeOutput value <> raw then fail ()
            value)
    /// Structural constructors still perform the same closed decode. They grant no authority.
    let createGenesis common head payload =
        try serializeGenesis (StructuralGenesisCheckpoint(common,head,payload)) |> parseGenesis
        with _ -> Error "supplier-companion-refused"
    let createOutput common output =
        try serializeOutput (StructuralOutputCheckpoint(common,output)) |> parseOutput
        with _ -> Error "supplier-companion-refused"
    let genesisCommon (StructuralGenesisCheckpoint(c,_,_)) = c
    let outputCommon (StructuralOutputCheckpoint(c,_)) = c
    let output (StructuralOutputCheckpoint(_,o)) = o
    let genesisHash value = serializeGenesis value |> hash
    let outputHash value = serializeOutput value |> hash

/// Structural retained bundle bytes. Deserialization never rehydrates an authenticated observation.
type PortableSupplierReceiverVerificationBundle = private | StructuralReceiverBundle of string
type PortableSupplierControllerVerificationBundle = private | StructuralControllerBundle of string

[<RequireQualifiedAccess>]
module PortableSupplierVerificationBundle =
    open PortableSupplierC1
    let private observationFields discriminator =
        ["role"; "envelope_sha256"; discriminator; "payload_digest"; "policy_digest"; "policy_revision"
         "issuer_principal"; "public_key_fingerprint"; "namespace"; "key_algorithm"; "message_hash_algorithm"
         "verifier_binary_digest"; "verifier_profile_digest"; "verified_at"; "policy_valid_from"
         "policy_valid_until"; "revocation_checked_at"; "revocation_status"]
    let private receiverRoles =
        ["creation", "creation"; "genesis", "genesis"; "supplier-event", "event"; "supplier-response", "response"]
    let private controllerRoles =
        ["receiver-command", "raw-sha256"; "create-effect-grant", "raw-sha256"; "start-effect-grant", "raw-sha256"
         "retire-effect-grant", "raw-sha256"; "receiver-genesis-ack", "domain-receiver-acceptance"
         "receiver-export-readback", "domain-export-readback"; "host-command-result", "raw-sha256"
         "original-intent", "raw-sha256"; "native-proof", "raw-sha256"]
    let private decode (schema: string) (maximumCount: int) (discriminator: string) (roles: (string * string) list) (root: System.Text.Json.JsonElement) =
        fields ["schema"; "provenance"; "observations"] root
        if root.GetProperty("schema").GetString() <> schema
           || not (List.contains (root.GetProperty("provenance") |> text 20) ["controlled-test"; "qualified-production"])
           || root.GetProperty("observations").ValueKind <> System.Text.Json.JsonValueKind.Array then fail ()
        let observations = root.GetProperty("observations").EnumerateArray() |> Seq.toList
        if observations.Length = 0 || observations.Length > maximumCount then fail ()
        let keys =
            observations |> List.map (fun row ->
                fields (observationFields discriminator) row
                let role = row.GetProperty("role") |> text 32
                let payload = row.GetProperty(discriminator) |> text 32
                if List.tryFind (fun (name,_) -> name = role) roles <> Some(role,payload) then fail ()
                for key in ["envelope_sha256"; "payload_digest"; "policy_digest"; "verifier_binary_digest"; "verifier_profile_digest"] do
                    row.GetProperty(key) |> digest |> ignore
                for key,maximum in ["policy_revision",128; "issuer_principal",256; "public_key_fingerprint",128; "namespace",128; "key_algorithm",64; "message_hash_algorithm",64] do
                    row.GetProperty(key) |> text maximum |> ignore
                for key in ["verified_at"; "policy_valid_from"; "policy_valid_until"; "revocation_checked_at"] do
                    row.GetProperty(key) |> utc |> ignore
                if row.GetProperty("revocation_status").GetString() <> "not-revoked" then fail ()
                role, row.GetProperty("envelope_sha256").GetString())
        if keys |> List.pairwise |> List.exists (fun ((a,b),(c,d)) ->
            let order = StringComparer.Ordinal.Compare(a,c)
            order > 0 || (order = 0 && StringComparer.Ordinal.Compare(b,d) >= 0)) then fail ()
        System.Text.Encoding.ASCII.GetString(objectBytes ["schema", quoted schema; "provenance", canonical (root.GetProperty("provenance")); "observations", canonical (root.GetProperty("observations"))])
    /// Preserve the existing four-role /1 data codec; policy facts remain untrusted here.
    let parseReceiver raw =
        parse 32768 raw (fun root -> StructuralReceiverBundle(decode "fsgg.v3.receiver-verification-bundle/1" 18 "payload_domain" receiverRoles root))
    /// Explicit /2 adds original-intent/native-proof to the seven companion roles.
    let parseController raw =
        parse 8192 raw (fun root -> StructuralControllerBundle(decode "fsgg.v3.controller-verification-bundle/2" 4 "payload_digest_kind" controllerRoles root))
    let serializeReceiver (StructuralReceiverBundle raw) = System.Text.Encoding.ASCII.GetBytes raw
    let serializeController (StructuralControllerBundle raw) = System.Text.Encoding.ASCII.GetBytes raw

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
