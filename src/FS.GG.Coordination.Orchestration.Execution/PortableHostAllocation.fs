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

    let b64url maximum (value: System.Text.Json.JsonElement) =
        let encoded = text (((maximum + 2) / 3) * 4) value
        if encoded |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')) then fail ()
        let decoded = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + String.replicate ((4 - encoded.Length % 4) % 4) "=")
        if decoded.Length = 0 || decoded.Length > maximum
           || Convert.ToBase64String(decoded).TrimEnd('=').Replace('+','-').Replace('/','_') <> encoded then fail ()
        decoded
    let document maximum raw = parse maximum raw (fun root -> root.Clone()) |> Result.defaultWith invalidOp
    let payload maximumEnvelope maximumPayload raw =
        parse maximumEnvelope raw (fun root ->
            fields ["payload"; "signature"] root
            let bytes = root.GetProperty("payload") |> b64url maximumPayload
            root.GetProperty("signature") |> b64url 2048 |> ignore
            bytes, document maximumPayload bytes) |> Result.defaultWith invalidOp
    let nestedBytes (root: System.Text.Json.JsonElement) =
        System.Text.Encoding.ASCII.GetBytes(canonical root + "\n")

/// Fixed guards for the unchanged wire-v1 families; no caller-selected schema or authority.
module private PortableSupplierWireShape =
    open System.Text.Json
    open PortableSupplierC1
    let private accepts action = try action (); true with _ -> false
    let rec check_body_effect_unknown (row: JsonElement) =
        fields ["effect"; "first_failure_digest"; "observed_at"; "original_attempt_digest"] row
        do
            let child = row.GetProperty("effect")
            if child.ValueKind <> JsonValueKind.String || not (List.contains (child.GetString()) ["create-stopped"; "start-original"; "retire-original"]) then fail ()
        do
            let child = row.GetProperty("first_failure_digest")
            check_digest child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("original_attempt_digest")
            check_digest child
    and check_body_export_manifest (row: JsonElement) =
        fields ["manifest_digest"; "member_count"; "observed_at"; "total_bytes"] row
        do
            let child = row.GetProperty("manifest_digest")
            check_digest child
        do
            let child = row.GetProperty("member_count")
            check_positive_u64 child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("total_bytes")
            check_u64 child
    and check_body_failure (row: JsonElement) =
        fields ["code"; "first_failure_digest"; "observed_at"; "original_attempt_digest"] row
        do
            let child = row.GetProperty("code")
            check_id128 child
        do
            let child = row.GetProperty("first_failure_digest")
            check_digest child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("original_attempt_digest")
            check_digest child
    and check_body_inner_cleanup_intent (row: JsonElement) =
        fields ["cid"; "observed_at"; "receipt_digest"] row
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
    and check_body_inner_cleanup_observed (row: JsonElement) =
        fields ["cid"; "observed_at"; "receipt_digest"] row
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
    and check_body_launch_intent (row: JsonElement) =
        fields ["grant_digest"; "original_cid"; "original_image_id"; "original_inode"; "recorded_at"] row
        do
            let child = row.GetProperty("grant_digest")
            check_digest child
        do
            let child = row.GetProperty("original_cid")
            check_outer_cid child
        do
            let child = row.GetProperty("original_image_id")
            check_outer_image_id child
        do
            let child = row.GetProperty("original_inode")
            check_positive_u64 child
        do
            let child = row.GetProperty("recorded_at")
            check_utc child
    and check_body_lease_amendment (row: JsonElement) =
        fields ["amendment_id"; "authority_ref"; "effective_at"; "new_expiry"; "old_expiry"; "previous_lease_digest"; "previous_lease_kind"] row
        do
            let child = row.GetProperty("amendment_id")
            check_uuid child
        do
            let child = row.GetProperty("authority_ref")
            check_host_ref child
        do
            let child = row.GetProperty("effective_at")
            check_utc child
        do
            let child = row.GetProperty("new_expiry")
            check_utc child
        do
            let child = row.GetProperty("old_expiry")
            check_utc child
        do
            let child = row.GetProperty("previous_lease_digest")
            check_digest child
        do
            let child = row.GetProperty("previous_lease_kind")
            if child.ValueKind <> JsonValueKind.String || not (List.contains (child.GetString()) ["creation-receipt"; "lease-amendment"]) then fail ()
    and check_body_outer_retire_intent (row: JsonElement) =
        fields ["cid"; "grant_digest"; "observed_at"; "receipt_digest"] row
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("grant_digest")
            check_digest child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
    and check_body_outer_retire_observed (row: JsonElement) =
        fields ["cid"; "grant_digest"; "observed_at"; "receipt_digest"] row
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("grant_digest")
            check_digest child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
    and check_body_start_invoked (row: JsonElement) =
        fields ["grant_digest"; "original_cid"; "original_image_id"; "original_inode"; "recorded_at"] row
        do
            let child = row.GetProperty("grant_digest")
            check_digest child
        do
            let child = row.GetProperty("original_cid")
            check_outer_cid child
        do
            let child = row.GetProperty("original_image_id")
            check_outer_image_id child
        do
            let child = row.GetProperty("original_inode")
            check_positive_u64 child
        do
            let child = row.GetProperty("recorded_at")
            check_utc child
    and check_body_termination (row: JsonElement) =
        fields ["cid"; "observed_at"; "receipt_digest"] row
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
    and check_body_unit_activation_observed (row: JsonElement) =
        fields ["activation_at"; "boot_id"; "cid"; "receipt_digest"; "unit_invocation_id"; "unit_name"] row
        do
            let child = row.GetProperty("activation_at")
            check_utc child
        do
            let child = row.GetProperty("boot_id")
            check_uuid child
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
        do
            let child = row.GetProperty("unit_invocation_id")
            check_unit_invocation_id child
        do
            let child = row.GetProperty("unit_name")
            check_unit_name child
    and check_body_workload_exit (row: JsonElement) =
        fields ["command_id"; "exit_code"; "observed_at"; "receipt_digest"] row
        do
            let child = row.GetProperty("command_id")
            check_uuid child
        do
            let child = row.GetProperty("exit_code")
            check_exit_i32 child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
    and check_body_zero_selection_census (row: JsonElement) =
        fields ["observed_at"; "receipt_digest"; "selection_digest"] row
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("receipt_digest")
            check_digest child
        do
            let child = row.GetProperty("selection_digest")
            check_digest child
    and check_command (row: JsonElement) =
        fields ["causation_id"; "command_id"; "component_id"; "deadline"; "expected_workflow_revision"; "fence_generation"; "idempotency_id"; "operation"; "profile_id"; "profile_revision"; "source_revision"; "workspace_scope"] row
        do
            let child = row.GetProperty("causation_id")
            let matches =
                [
                  (fun () ->
                      check_id128 child
                  )
                  (fun () ->
                      if child.ValueKind <> JsonValueKind.Null then fail ()
                  )
                ] |> List.filter accepts |> List.length
            if matches <> 1 then fail ()
        do
            let child = row.GetProperty("command_id")
            check_uuid child
        do
            let child = row.GetProperty("component_id")
            let matches =
                [
                  (fun () ->
                      check_id128 child
                  )
                  (fun () ->
                      if child.ValueKind <> JsonValueKind.Null then fail ()
                  )
                ] |> List.filter accepts |> List.length
            if matches <> 1 then fail ()
        do
            let child = row.GetProperty("deadline")
            check_utc child
        do
            let child = row.GetProperty("expected_workflow_revision")
            check_u64 child
        do
            let child = row.GetProperty("fence_generation")
            check_u64 child
        do
            let child = row.GetProperty("idempotency_id")
            check_id128 child
        do
            let child = row.GetProperty("operation")
            check_id128 child
        do
            let child = row.GetProperty("profile_id")
            check_id128 child
        do
            let child = row.GetProperty("profile_revision")
            check_u64 child
        do
            let child = row.GetProperty("source_revision")
            check_source128 child
        do
            let child = row.GetProperty("workspace_scope")
            check_id128 child
    and check_creation_challenge (row: JsonElement) =
        fields ["challenge_id"; "challenge_nonce"; "correlation_id"; "intent_digest"; "issued_at"; "not_after"; "schema"] row
        do
            let child = row.GetProperty("challenge_id")
            check_uuid child
        do
            let child = row.GetProperty("challenge_nonce")
            check_nonce256 child
        do
            let child = row.GetProperty("correlation_id")
            check_uuid child
        do
            let child = row.GetProperty("intent_digest")
            check_digest child
        do
            let child = row.GetProperty("issued_at")
            check_utc child
        do
            let child = row.GetProperty("not_after")
            check_utc child
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.creation-challenge/1" then fail ()
    and check_creation_core (row: JsonElement) =
        fields ["allocation_device"; "allocation_fs_uuid"; "allocation_inode"; "boot_id"; "challenge_digest"; "cid"; "correlation_id"; "created_at"; "host_lease_authority_ref"; "host_lease_expires_at"; "host_lease_origin_at"; "host_maximum_runtime_cap_ticks"; "host_min_cleanup_reserve_ticks"; "host_min_export_reserve_ticks"; "image_id"; "intent_digest"; "operation_id"; "pool_id"; "profile"; "schema"; "stopped_at"; "supplier_source_digest"; "supplier_source_revision"; "unit_config_digest"; "unit_file_digest"; "unit_name"] row
        do
            let child = row.GetProperty("allocation_device")
            check_u64 child
        do
            let child = row.GetProperty("allocation_fs_uuid")
            check_uuid child
        do
            let child = row.GetProperty("allocation_inode")
            check_positive_u64 child
        do
            let child = row.GetProperty("boot_id")
            check_uuid child
        do
            let child = row.GetProperty("challenge_digest")
            check_digest child
        do
            let child = row.GetProperty("cid")
            check_outer_cid child
        do
            let child = row.GetProperty("correlation_id")
            check_uuid child
        do
            let child = row.GetProperty("created_at")
            check_utc child
        do
            let child = row.GetProperty("host_lease_authority_ref")
            check_host_ref child
        do
            let child = row.GetProperty("host_lease_expires_at")
            check_utc child
        do
            let child = row.GetProperty("host_lease_origin_at")
            check_utc child
        do
            let child = row.GetProperty("host_maximum_runtime_cap_ticks")
            check_ticks child
        do
            let child = row.GetProperty("host_min_cleanup_reserve_ticks")
            check_ticks child
        do
            let child = row.GetProperty("host_min_export_reserve_ticks")
            check_ticks child
        do
            let child = row.GetProperty("image_id")
            check_outer_image_id child
        do
            let child = row.GetProperty("intent_digest")
            check_digest child
        do
            let child = row.GetProperty("operation_id")
            check_uuid child
        do
            let child = row.GetProperty("pool_id")
            check_id128 child
        do
            let child = row.GetProperty("profile")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "signed-hold-v1" then fail ()
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.creation-core/1" then fail ()
        do
            let child = row.GetProperty("stopped_at")
            check_utc child
        do
            let child = row.GetProperty("supplier_source_digest")
            check_digest child
        do
            let child = row.GetProperty("supplier_source_revision")
            check_source128 child
        do
            let child = row.GetProperty("unit_config_digest")
            check_digest child
        do
            let child = row.GetProperty("unit_file_digest")
            check_digest child
        do
            let child = row.GetProperty("unit_name")
            check_unit_name child
    and check_digest (row: JsonElement) =
        let value = text 64 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$")) then fail ()
    and check_effect_admission (row: JsonElement) =
        let matches =
            [
              (fun () ->
                  fields ["effect"; "expected_head"; "grant_id"; "intent_digest"; "issued_at"; "not_after"; "not_before"; "operation_id"; "reservation_digest"; "schema"; "scope_digest"; "source_revision"] row
                  do
                      let child = row.GetProperty("effect")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "create-stopped" then fail ()
                  do
                      let child = row.GetProperty("expected_head")
                      check_null child
                  do
                      let child = row.GetProperty("grant_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("intent_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("issued_at")
                      check_utc child
                  do
                      let child = row.GetProperty("not_after")
                      check_utc child
                  do
                      let child = row.GetProperty("not_before")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_null child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_null child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.effect-admission/1" then fail ()
                  do
                      let child = row.GetProperty("scope_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("source_revision")
                      check_source128 child
              )
              (fun () ->
                  fields ["effect"; "expected_head"; "grant_id"; "intent_digest"; "issued_at"; "not_after"; "not_before"; "operation_id"; "reservation_digest"; "schema"; "scope_digest"; "source_revision"] row
                  do
                      let child = row.GetProperty("effect")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "start-original" then fail ()
                  do
                      let child = row.GetProperty("expected_head")
                      check_digest child
                  do
                      let child = row.GetProperty("grant_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("intent_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("issued_at")
                      check_utc child
                  do
                      let child = row.GetProperty("not_after")
                      check_utc child
                  do
                      let child = row.GetProperty("not_before")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.effect-admission/1" then fail ()
                  do
                      let child = row.GetProperty("scope_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("source_revision")
                      check_source128 child
              )
              (fun () ->
                  fields ["effect"; "expected_head"; "grant_id"; "intent_digest"; "issued_at"; "not_after"; "not_before"; "operation_id"; "reservation_digest"; "schema"; "scope_digest"; "source_revision"] row
                  do
                      let child = row.GetProperty("effect")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "retire-original" then fail ()
                  do
                      let child = row.GetProperty("expected_head")
                      check_digest child
                  do
                      let child = row.GetProperty("grant_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("intent_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("issued_at")
                      check_utc child
                  do
                      let child = row.GetProperty("not_after")
                      check_utc child
                  do
                      let child = row.GetProperty("not_before")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.effect-admission/1" then fail ()
                  do
                      let child = row.GetProperty("scope_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("source_revision")
                      check_source128 child
              )
            ] |> List.filter accepts |> List.length
        if matches <> 1 then fail ()
    and check_exit_i32 (row: JsonElement) =
        let value = text 11 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^(?:0|[1-9][0-9]{0,8}|1[0-9]{9}|20[0-9]{8}|21[0-3][0-9]{7}|214[0-6][0-9]{6}|2147[0-3][0-9]{5}|21474[0-7][0-9]{4}|214748[0-2][0-9]{3}|2147483[0-5][0-9]{2}|21474836[0-3][0-9]|214748364[0-7]|-(?:[1-9][0-9]{0,8}|1[0-9]{9}|20[0-9]{8}|21[0-3][0-9]{7}|214[0-6][0-9]{6}|2147[0-3][0-9]{5}|21474[0-7][0-9]{4}|214748[0-2][0-9]{3}|2147483[0-5][0-9]{2}|21474836[0-3][0-9]|214748364[0-8]))$")) then fail ()
    and check_export_readback (row: JsonElement) =
        let matches =
            [
              (fun () ->
                  fields ["kind"; "manifest_digest"; "member_count"; "observed_head"; "operation_id"; "readback_at"; "receiver_journal_digest"; "reservation_digest"; "schema"; "selection_digest"; "total_bytes"] row
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "durable-output-readback" then fail ()
                  do
                      let child = row.GetProperty("manifest_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("member_count")
                      check_positive_u64 child
                  do
                      let child = row.GetProperty("observed_head")
                      check_digest child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("readback_at")
                      check_utc child
                  do
                      let child = row.GetProperty("receiver_journal_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.export-readback/1" then fail ()
                  do
                      let child = row.GetProperty("selection_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("total_bytes")
                      check_u64 child
              )
              (fun () ->
                  fields ["kind"; "manifest_digest"; "member_count"; "observed_head"; "operation_id"; "readback_at"; "receiver_journal_digest"; "reservation_digest"; "schema"; "selection_digest"; "total_bytes"] row
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "zero-selection-census-accepted" then fail ()
                  do
                      let child = row.GetProperty("manifest_digest")
                      check_null child
                  do
                      let child = row.GetProperty("member_count")
                      check_null child
                  do
                      let child = row.GetProperty("observed_head")
                      check_digest child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("readback_at")
                      check_utc child
                  do
                      let child = row.GetProperty("receiver_journal_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.export-readback/1" then fail ()
                  do
                      let child = row.GetProperty("selection_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("total_bytes")
                      check_null child
              )
            ] |> List.filter accepts |> List.length
        if matches <> 1 then fail ()
    and check_genesis (row: JsonElement) =
        fields ["creation_core"; "creation_core_digest"; "creation_receipt_digest"; "observed_at"; "predecessor_head"; "reservation"; "reservation_digest"; "schema"; "sequence"] row
        do
            let child = row.GetProperty("creation_core")
            check_creation_core child
        do
            let child = row.GetProperty("creation_core_digest")
            check_digest child
        do
            let child = row.GetProperty("creation_receipt_digest")
            check_digest child
        do
            let child = row.GetProperty("observed_at")
            check_utc child
        do
            let child = row.GetProperty("predecessor_head")
            if child.ValueKind <> JsonValueKind.Null then fail ()
        do
            let child = row.GetProperty("reservation")
            check_reservation child
        do
            let child = row.GetProperty("reservation_digest")
            check_digest child
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.genesis/1" then fail ()
        do
            let child = row.GetProperty("sequence")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "0" then fail ()
    and check_host_ref (row: JsonElement) =
        let value = text 4096 row
        if value.Length < 1 then fail ()
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[ -~]+$")) then fail ()
    and check_id128 (row: JsonElement) =
        let value = text 128 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")) then fail ()
    and check_nonce256 (row: JsonElement) =
        let value = text 43 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}$")) then fail ()
        let decoded = b64url 65536 row
        if decoded.Length <> 32 then fail ()
    and check_null (row: JsonElement) =
        if row.ValueKind <> JsonValueKind.Null then fail ()
    and check_outer_cid (row: JsonElement) =
        let value = text 64 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$")) then fail ()
    and check_outer_image_id (row: JsonElement) =
        let value = text 64 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$")) then fail ()
    and check_positive_u64 (row: JsonElement) =
        let value = text 20 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^(?:[1-9][0-9]{0,18}|1[0-7][0-9]{18}|18[0-3][0-9]{17}|184[0-3][0-9]{16}|1844[0-5][0-9]{15}|18446[0-6][0-9]{14}|184467[0-3][0-9]{13}|1844674[0-3][0-9]{12}|184467440[0-6][0-9]{10}|1844674407[0-2][0-9]{9}|18446744073[0-6][0-9]{8}|1844674407370[0-8][0-9]{6}|18446744073709[0-4][0-9]{5}|184467440737095[0-4][0-9]{4}|18446744073709550[0-9]{3}|18446744073709551[0-5][0-9]{2}|1844674407370955160[0-9]{1}|1844674407370955161[0-4][0-9]{0}|18446744073709551615)$")) then fail ()
        let count = u64 row
        if count = 0UL then fail ()
    and check_qualified_inner_image (row: JsonElement) =
        let value = text 256 row
        if value.Length < 72 then fail ()
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[^ ].*@sha256:[0-9a-f]{64}$")) then fail ()
    and check_receiver_acceptance (row: JsonElement) =
        fields ["accepted_at"; "correlation_id"; "genesis_head"; "intent_digest"; "kind"; "operation_id"; "receiver_journal_digest"; "reservation_digest"; "schema"] row
        do
            let child = row.GetProperty("accepted_at")
            check_utc child
        do
            let child = row.GetProperty("correlation_id")
            check_uuid child
        do
            let child = row.GetProperty("genesis_head")
            check_digest child
        do
            let child = row.GetProperty("intent_digest")
            check_digest child
        do
            let child = row.GetProperty("kind")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "original-genesis-accepted" then fail ()
        do
            let child = row.GetProperty("operation_id")
            check_uuid child
        do
            let child = row.GetProperty("receiver_journal_digest")
            check_digest child
        do
            let child = row.GetProperty("reservation_digest")
            check_digest child
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.receiver-acceptance/1" then fail ()
    and check_receiver_intent (row: JsonElement) =
        fields ["command_deadline"; "command_digest"; "correlation_id"; "created_at"; "intent_id"; "qualified_inner_image"; "requested_cleanup_reserve_ticks"; "requested_export_reserve_ticks"; "requested_host_lease_not_after"; "requested_maximum_runtime_ticks"; "requested_profile"; "requested_source_revision"; "schema"; "selection"] row
        do
            let child = row.GetProperty("command_deadline")
            check_utc child
        do
            let child = row.GetProperty("command_digest")
            check_digest child
        do
            let child = row.GetProperty("correlation_id")
            check_uuid child
        do
            let child = row.GetProperty("created_at")
            check_utc child
        do
            let child = row.GetProperty("intent_id")
            check_uuid child
        do
            let child = row.GetProperty("qualified_inner_image")
            check_qualified_inner_image child
        do
            let child = row.GetProperty("requested_cleanup_reserve_ticks")
            check_ticks child
        do
            let child = row.GetProperty("requested_export_reserve_ticks")
            check_ticks child
        do
            let child = row.GetProperty("requested_host_lease_not_after")
            check_utc child
        do
            let child = row.GetProperty("requested_maximum_runtime_ticks")
            check_ticks child
        do
            let child = row.GetProperty("requested_profile")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "signed-hold-v1" then fail ()
        do
            let child = row.GetProperty("requested_source_revision")
            check_source128 child
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.receiver-intent/1" then fail ()
        do
            let child = row.GetProperty("selection")
            check_selection child
    and check_reservation (row: JsonElement) =
        fields ["admitted_at"; "cleanup_reserve_ticks"; "command"; "correlation_id"; "creation_receipt_digest"; "export_reserve_ticks"; "host_lease_expires_at"; "intent_digest"; "maximum_runtime_ticks"; "operation_id"; "original_deadline"; "qualified_inner_image"; "receiver_source_revision"; "schema"; "selection"; "worker_id"] row
        do
            let child = row.GetProperty("admitted_at")
            check_utc child
        do
            let child = row.GetProperty("cleanup_reserve_ticks")
            check_ticks child
        do
            let child = row.GetProperty("command")
            check_command child
        do
            let child = row.GetProperty("correlation_id")
            check_uuid child
        do
            let child = row.GetProperty("creation_receipt_digest")
            check_digest child
        do
            let child = row.GetProperty("export_reserve_ticks")
            check_ticks child
        do
            let child = row.GetProperty("host_lease_expires_at")
            check_utc child
        do
            let child = row.GetProperty("intent_digest")
            check_digest child
        do
            let child = row.GetProperty("maximum_runtime_ticks")
            check_ticks child
        do
            let child = row.GetProperty("operation_id")
            check_uuid child
        do
            let child = row.GetProperty("original_deadline")
            check_utc child
        do
            let child = row.GetProperty("qualified_inner_image")
            check_qualified_inner_image child
        do
            let child = row.GetProperty("receiver_source_revision")
            check_source128 child
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.reservation/1" then fail ()
        do
            let child = row.GetProperty("selection")
            check_selection child
        do
            let child = row.GetProperty("worker_id")
            check_id128 child
    and check_response_challenge (row: JsonElement) =
        fields ["challenge_id"; "challenge_nonce"; "issued_at"; "known_head"; "not_after"; "operation_id"; "purpose"; "reservation_digest"; "schema"] row
        do
            let child = row.GetProperty("challenge_id")
            check_uuid child
        do
            let child = row.GetProperty("challenge_nonce")
            check_nonce256 child
        do
            let child = row.GetProperty("issued_at")
            check_utc child
        do
            let child = row.GetProperty("known_head")
            let matches =
                [
                  (fun () ->
                      check_digest child
                  )
                  (fun () ->
                      if child.ValueKind <> JsonValueKind.Null then fail ()
                  )
                ] |> List.filter accepts |> List.length
            if matches <> 1 then fail ()
        do
            let child = row.GetProperty("not_after")
            check_utc child
        do
            let child = row.GetProperty("operation_id")
            check_uuid child
        do
            let child = row.GetProperty("purpose")
            if child.ValueKind <> JsonValueKind.String || not (List.contains (child.GetString()) ["genesis-readback"; "state-readback"; "retirement-readback"]) then fail ()
        do
            let child = row.GetProperty("reservation_digest")
            check_digest child
        do
            let child = row.GetProperty("schema")
            if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.response-challenge/1" then fail ()
    and check_selection (row: JsonElement) =
        let matches =
            [
              (fun () ->
                  fields ["kind"] row
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "none" then fail ()
              )
              (fun () ->
                  fields ["digest"; "kind"; "member_count"; "total_bytes"] row
                  do
                      let child = row.GetProperty("digest")
                      check_digest child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "selected" then fail ()
                  do
                      let child = row.GetProperty("member_count")
                      check_positive_u64 child
                  do
                      let child = row.GetProperty("total_bytes")
                      check_u64 child
              )
            ] |> List.filter accepts |> List.length
        if matches <> 1 then fail ()
    and check_signed_envelope (row: JsonElement) =
        fields ["payload"; "signature"] row
        do
            let child = row.GetProperty("payload")
            let value = text 60000 child
            if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) then fail ()
            let decoded = b64url 65536 child
            if decoded.Length = 0 then fail ()
        do
            let child = row.GetProperty("signature")
            let value = text 2731 child
            if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) then fail ()
            let decoded = b64url 65536 child
            if decoded.Length = 0 then fail ()
    and check_source128 (row: JsonElement) =
        let value = text 128 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,128}$")) then fail ()
    and check_supplier_event (row: JsonElement) =
        let matches =
            [
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_lease_amendment child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "lease-amendment" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_launch_intent child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "launch-intent" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_start_invoked child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "start-invoked" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_unit_activation_observed child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "unit-activation-observed" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_workload_exit child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "workload-exit" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_termination child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "termination" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_inner_cleanup_intent child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "inner-cleanup-intent" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_inner_cleanup_observed child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "inner-cleanup-observed" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_export_manifest child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "export-manifest" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_zero_selection_census child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "zero-selection-census" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_outer_retire_intent child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "outer-retire-intent" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_outer_retire_observed child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "outer-retire-observed" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_effect_unknown child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "effect-unknown" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
              (fun () ->
                  fields ["body"; "kind"; "occurred_at"; "operation_id"; "predecessor_head"; "reservation_digest"; "schema"; "sequence"] row
                  do
                      let child = row.GetProperty("body")
                      check_body_failure child
                  do
                      let child = row.GetProperty("kind")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "failure" then fail ()
                  do
                      let child = row.GetProperty("occurred_at")
                      check_utc child
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("predecessor_head")
                      check_digest child
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-event/1" then fail ()
                  do
                      let child = row.GetProperty("sequence")
                      check_positive_u64 child
              )
            ] |> List.filter accepts |> List.length
        if matches <> 1 then fail ()
    and check_supplier_response (row: JsonElement) =
        let matches =
            [
              (fun () ->
                  fields ["challenge_digest"; "collected_at"; "events"; "from_head"; "mode"; "operation_id"; "purpose"; "reservation_digest"; "schema"; "to_head"] row
                  do
                      let child = row.GetProperty("challenge_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("collected_at")
                      check_utc child
                  do
                      let child = row.GetProperty("events")
                      if child.ValueKind <> JsonValueKind.Array then fail ()
                      let items = child.EnumerateArray() |> Seq.toList
                      if items.Length < 1 || items.Length > 1 then fail ()
                      for item in items do
                          let value = text 35682 item
                          if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) then fail ()
                          let decoded = b64url 65536 item
                          if decoded.Length = 0 then fail ()
                  do
                      let child = row.GetProperty("from_head")
                      check_null child
                  do
                      let child = row.GetProperty("mode")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "genesis" then fail ()
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("purpose")
                      if child.ValueKind <> JsonValueKind.String || not (List.contains (child.GetString()) ["genesis-readback"; "state-readback"; "retirement-readback"]) then fail ()
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-response/1" then fail ()
                  do
                      let child = row.GetProperty("to_head")
                      check_digest child
              )
              (fun () ->
                  fields ["challenge_digest"; "collected_at"; "events"; "from_head"; "mode"; "operation_id"; "purpose"; "reservation_digest"; "schema"; "to_head"] row
                  do
                      let child = row.GetProperty("challenge_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("collected_at")
                      check_utc child
                  do
                      let child = row.GetProperty("events")
                      if child.ValueKind <> JsonValueKind.Array then fail ()
                      let items = child.EnumerateArray() |> Seq.toList
                      if items.Length < 1 || items.Length > 16 then fail ()
                      for item in items do
                          let value = text 25015 item
                          if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) then fail ()
                          let decoded = b64url 65536 item
                          if decoded.Length = 0 then fail ()
                  do
                      let child = row.GetProperty("from_head")
                      check_digest child
                  do
                      let child = row.GetProperty("mode")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "append" then fail ()
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("purpose")
                      if child.ValueKind <> JsonValueKind.String || not (List.contains (child.GetString()) ["genesis-readback"; "state-readback"; "retirement-readback"]) then fail ()
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-response/1" then fail ()
                  do
                      let child = row.GetProperty("to_head")
                      check_digest child
              )
              (fun () ->
                  fields ["challenge_digest"; "collected_at"; "events"; "from_head"; "mode"; "operation_id"; "purpose"; "reservation_digest"; "schema"; "to_head"] row
                  do
                      let child = row.GetProperty("challenge_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("collected_at")
                      check_utc child
                  do
                      let child = row.GetProperty("events")
                      if child.ValueKind <> JsonValueKind.Array then fail ()
                      let items = child.EnumerateArray() |> Seq.toList
                      if items.Length < 1 || items.Length > 1 then fail ()
                      for item in items do
                          let value = text 35682 item
                          if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) then fail ()
                          let decoded = b64url 65536 item
                          if decoded.Length = 0 then fail ()
                  do
                      let child = row.GetProperty("from_head")
                      check_digest child
                  do
                      let child = row.GetProperty("mode")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "unchanged" then fail ()
                  do
                      let child = row.GetProperty("operation_id")
                      check_uuid child
                  do
                      let child = row.GetProperty("purpose")
                      if child.ValueKind <> JsonValueKind.String || not (List.contains (child.GetString()) ["genesis-readback"; "state-readback"; "retirement-readback"]) then fail ()
                  do
                      let child = row.GetProperty("reservation_digest")
                      check_digest child
                  do
                      let child = row.GetProperty("schema")
                      if child.ValueKind <> JsonValueKind.String || child.GetString() <> "fsgg.v3.supplier-response/1" then fail ()
                  do
                      let child = row.GetProperty("to_head")
                      check_digest child
              )
            ] |> List.filter accepts |> List.length
        if matches <> 1 then fail ()
    and check_ticks (row: JsonElement) =
        let value = text 19 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^(?:[1-9][0-9]{0,17}|[1-8][0-9]{18}|9[0-1][0-9]{17}|92[0-1][0-9]{16}|922[0-2][0-9]{15}|9223[0-2][0-9]{14}|92233[0-6][0-9]{13}|922337[0-1][0-9]{12}|92233720[0-2][0-9]{10}|922337203[0-5][0-9]{9}|9223372036[0-7][0-9]{8}|92233720368[0-4][0-9]{7}|922337203685[0-3][0-9]{6}|9223372036854[0-6][0-9]{5}|92233720368547[0-6][0-9]{4}|922337203685477[0-4][0-9]{3}|9223372036854775[0-7][0-9]{2}|922337203685477580[0-6][0-9]{0}|9223372036854775807)$")) then fail ()
    and check_u64 (row: JsonElement) =
        let value = text 20 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^(?:0|[1-9][0-9]{0,18}|1[0-7][0-9]{18}|18[0-3][0-9]{17}|184[0-3][0-9]{16}|1844[0-5][0-9]{15}|18446[0-6][0-9]{14}|184467[0-3][0-9]{13}|1844674[0-3][0-9]{12}|184467440[0-6][0-9]{10}|1844674407[0-2][0-9]{9}|18446744073[0-6][0-9]{8}|1844674407370[0-8][0-9]{6}|18446744073709[0-4][0-9]{5}|184467440737095[0-4][0-9]{4}|18446744073709550[0-9]{3}|18446744073709551[0-5][0-9]{2}|1844674407370955160[0-9]{1}|1844674407370955161[0-4][0-9]{0}|18446744073709551615)$")) then fail ()
        u64 row |> ignore
    and check_unit_invocation_id (row: JsonElement) =
        let value = text 32 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{32}$")) then fail ()
    and check_unit_name (row: JsonElement) =
        let value = text 256 row
        if value.Length < 1 then fail ()
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[^/\\\\\\s]+$")) then fail ()
    and check_utc (row: JsonElement) =
        let value = text 27 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}Z$")) then fail ()
        utc row |> ignore
    and check_uuid (row: JsonElement) =
        let value = text 36 row
        if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")) then fail ()
        uuid row |> ignore
        if row.ValueKind = JsonValueKind.String && row.GetString() = "00000000-0000-0000-0000-000000000000" then fail ()

/// Immutable structural checkpoint fields, all recomputed by the retained-exchange caller.
/// A parsed instance is neither an authenticated candidate nor a commit witness.
/// Native receipt data only. Parsing never proves a filesystem observation or signature.
type PortableSupplierNativeReceipt = private | StructuralNativeReceipt of string

[<RequireQualifiedAccess>]
module PortableSupplierNativeReceipt =
    open System.Text.Json
    open PortableSupplierC1
    [<Literal>]
    let MaximumBytes = 32768
    let private root (row: JsonElement) =
        fields ["device"; "inode"; "filesystem_uuid"] row
        for key in ["device"; "inode"] do row.GetProperty(key) |> u64 |> ignore
        row.GetProperty("filesystem_uuid") |> uuid |> ignore
    let private path (row: JsonElement) =
        let value = row |> text 128
        PortableSupplierManifest.create [{ Path = value; Bytes = 0UL; Sha256 = String.replicate 64 "0" }]
        |> Result.defaultWith invalidOp |> ignore
        value
    let private orderedPaths (rows: JsonElement list) =
        let paths = rows |> List.map (fun row -> path (row.GetProperty("path")))
        if paths |> List.pairwise |> List.exists (fun (a,b) -> StringComparer.Ordinal.Compare(a,b) >= 0) then fail ()
        if (paths |> List.map _.ToUpperInvariant() |> Set.ofList |> Set.count) <> paths.Length then fail ()
        paths
    let private fileIdentity (row: JsonElement) =
        for key in ["device"; "inode"; "uid"; "gid"; "nlink"] do row.GetProperty(key) |> u64 |> ignore
        if row.GetProperty("nlink").GetString() <> "1" then fail ()
    let private decode (value: JsonElement) =
        let common =
            ["schema"; "profile"; "operation_id"; "intent_digest"; "creation_core_digest"; "reservation_digest"
             "selection_digest"; "scope_policy_digest"; "source_root"; "observed_at"; "clock_snapshot_sha256"]
        let schema = value.GetProperty("schema") |> text 64
        let selected = schema = "fsgg.v3.native-export-readback/1"
        let extra =
            if selected then
                ["manifest_bytes"; "manifest_digest"; "manifest_sha256"; "member_count"; "total_bytes"
                 "destination_policy_digest"; "destination_root"; "readback_members"]
            elif schema = "fsgg.v3.native-zero-census/1" then ["selected_member_count"; "selected_total_bytes"; "inventory"]
            else fail ()
        fields (common @ extra) value
        if value.GetProperty("profile").GetString() <> "signed-hold-v1" then fail ()
        value.GetProperty("operation_id") |> uuid |> ignore
        for key in ["intent_digest"; "creation_core_digest"; "reservation_digest"; "selection_digest"; "scope_policy_digest"; "clock_snapshot_sha256"] do
            value.GetProperty(key) |> digest |> ignore
        value.GetProperty("source_root") |> root
        value.GetProperty("observed_at") |> utc |> ignore
        if selected then
            value.GetProperty("destination_root") |> root
            value.GetProperty("destination_policy_digest") |> digest |> ignore
            let exact = value.GetProperty("manifest_bytes") |> b64url 6000
            let manifest = PortableSupplierManifest.parse exact |> Result.defaultWith invalidOp
            let summary = PortableSupplierManifest.summary manifest
            if (value.GetProperty("manifest_digest") |> digest) <> summary.Digest
               || (value.GetProperty("manifest_sha256") |> digest) <> hash exact
               || (value.GetProperty("member_count") |> u64) <> summary.MemberCount
               || (value.GetProperty("total_bytes") |> u64) <> summary.TotalBytes then fail ()
            let array = value.GetProperty("readback_members")
            if array.ValueKind <> JsonValueKind.Array || array.GetArrayLength() > 16 then fail ()
            let rows = array.EnumerateArray() |> Seq.toList
            orderedPaths rows |> ignore
            let actual =
                rows |> List.map (fun row ->
                    fields ["path"; "bytes"; "sha256"; "device"; "inode"; "uid"; "gid"; "nlink"] row
                    fileIdentity row
                    { Path = path (row.GetProperty("path")); Bytes = row.GetProperty("bytes") |> u64
                      Sha256 = row.GetProperty("sha256") |> digest })
            if actual <> PortableSupplierManifest.members manifest then fail ()
        else
            if value.GetProperty("selected_member_count").GetString() <> "0"
               || value.GetProperty("selected_total_bytes").GetString() <> "0" then fail ()
            let array = value.GetProperty("inventory")
            if array.ValueKind <> JsonValueKind.Array || array.GetArrayLength() > 64 then fail ()
            let rows = array.EnumerateArray() |> Seq.toList
            orderedPaths rows |> ignore
            for row in rows do
                match row.GetProperty("kind").GetString() with
                | "regular-file" ->
                    fields ["path"; "kind"; "scope_class"; "device"; "inode"; "uid"; "gid"; "nlink"; "bytes"; "sha256"] row
                    fileIdentity row
                    row.GetProperty("bytes") |> u64 |> ignore
                    row.GetProperty("sha256") |> digest |> ignore
                    if row.GetProperty("scope_class").GetString() <> "excluded-engine" then fail ()
                | "directory" ->
                    fields ["path"; "kind"; "scope_class"; "device"; "inode"; "uid"; "gid"] row
                    for key in ["device"; "inode"; "uid"; "gid"] do row.GetProperty(key) |> u64 |> ignore
                    if row.GetProperty("scope_class").GetString() <> "structural" then fail ()
                | _ -> fail ()
        StructuralNativeReceipt(System.Text.Encoding.ASCII.GetString(objectBytes (value.EnumerateObject() |> Seq.map (fun p -> p.Name,canonical p.Value) |> Seq.toList)))
    let parse bytes = PortableSupplierC1.parse MaximumBytes bytes decode
    let serialize (StructuralNativeReceipt value) = System.Text.Encoding.ASCII.GetBytes value
    let rawSha256 receipt = serialize receipt |> hash

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

/// A fixed signing purpose, independent of the enrolled key and current policy decision.
type PortableSupplierSignatureRole =
    | OriginalIntentSignature | EffectGrantSignature | ReceiverCommandSignature
    | GenesisAckSignature | OutputAckSignature | HostCommandResultSignature | NativeProofSignature
    | SupplierCreationSignature | SupplierGenesisSignature | SupplierEventSignature | SupplierResponseSignature

/// Cryptographic byte verification only; no enrollment, policy, native or durable authority.
type PortableSupplierSignatureVerification =
    private | CryptographicSupplierSignature of PortableSupplierSignatureRole * string * string

[<RequireQualifiedAccess>]
module PortableSupplierSshsig =
    let signingNamespace = function
        | OriginalIntentSignature -> "fsgg-v3-original-intent-v1"
        | EffectGrantSignature -> "fsgg-v3-effect-grant-v1"
        | ReceiverCommandSignature -> "fsgg-v3-host-command-v1"
        | GenesisAckSignature -> "fsgg-v3-genesis-ack-v1"
        | OutputAckSignature -> "fsgg-v3-output-ack-v1"
        | HostCommandResultSignature -> "fsgg-v3-host-command-result-v1"
        | NativeProofSignature -> "fsgg-v3-native-proof-v1"
        | SupplierCreationSignature -> "fsgg-v3-supplier-creation-v1"
        | SupplierGenesisSignature -> "fsgg-v3-supplier-genesis-v1"
        | SupplierEventSignature -> "fsgg-v3-supplier-event-v1"
        | SupplierResponseSignature -> "fsgg-v3-supplier-response-v1"
    let private ascii (value: string) = System.Text.Encoding.ASCII.GetBytes value
    let private sshString (value: byte array) =
        let result = Array.zeroCreate<byte> (4 + value.Length)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0,4), uint32 value.Length)
        value.CopyTo(result,4)
        result
    let private reader (bytes: byte array) =
        let mutable position = 0
        let take length =
            if length < 0 || length > bytes.Length - position then PortableSupplierC1.fail ()
            let value = bytes.AsSpan(position,length).ToArray()
            position <- position + length
            value
        let word () = take 4 |> fun value -> System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan())
        let field maximum =
            let length = word ()
            if length > uint32 maximum then PortableSupplierC1.fail ()
            take (int length)
        let complete () = if position <> bytes.Length then PortableSupplierC1.fail ()
        take,word,field,complete
    let private armor (bytes: byte array) =
        if bytes.Length = 0 || bytes.Length > 2048 || (bytes |> Array.exists (fun b -> b > 127uy)) then PortableSupplierC1.fail ()
        let text = System.Text.Encoding.ASCII.GetString bytes
        let header,footer = "-----BEGIN SSH SIGNATURE-----\n","-----END SSH SIGNATURE-----\n"
        if not (text.StartsWith(header,StringComparison.Ordinal)) || not (text.EndsWith(footer,StringComparison.Ordinal)) then PortableSupplierC1.fail ()
        let middle = text.Substring(header.Length,text.Length-header.Length-footer.Length)
        if middle.Length = 0 || not (middle.EndsWith("\n",StringComparison.Ordinal)) then PortableSupplierC1.fail ()
        let lines = middle.Substring(0,middle.Length-1).Split('\n')
        if lines.Length = 0 || (lines |> Array.mapi (fun i line -> line.Length = 0 || line.Length > 70 || (i < lines.Length-1 && line.Length <> 70)) |> Array.exists id) then PortableSupplierC1.fail ()
        let encoded = String.concat "" lines
        let binary = Convert.FromBase64String encoded
        if Convert.ToBase64String binary <> encoded then PortableSupplierC1.fail ()
        let canonical =
            let encoded = Convert.ToBase64String binary
            let body = [for start in 0 .. 70 .. encoded.Length-1 -> encoded.Substring(start,min 70 (encoded.Length-start))] |> String.concat "\n"
            header + body + "\n" + footer
        if text <> canonical then PortableSupplierC1.fail ()
        binary
    /// Exact pin bytes are independently selected by the caller's policy; none are inferred from the signature.
    /// This primitive does not validate a family/original, construct a bundle, or select a current policy.
    let verify role (expectedSshPublicKeyBlob: byte array) (exactEnvelope: byte array) =
        try
            if isNull expectedSshPublicKeyBlob || expectedSshPublicKeyBlob.Length <> 51 then PortableSupplierC1.fail ()
            let pin = Array.copy expectedSshPublicKeyBlob
            if isNull exactEnvelope || exactEnvelope.Length = 0 || exactEnvelope.Length > 65536 then PortableSupplierC1.fail ()
            let snapshot = Array.copy exactEnvelope
            let payload,_ = PortableSupplierC1.payload 65536 45000 snapshot
            let envelope = PortableSupplierC1.document 65536 snapshot
            let signature = envelope.GetProperty("signature") |> PortableSupplierC1.b64url 2048 |> armor
            let take,word,field,complete = reader signature
            if take 6 <> ascii "SSHSIG" || word () <> 1u then PortableSupplierC1.fail ()
            let publicKey = field 128
            let purpose = field 128
            let reserved = field 0
            let algorithm = field 16
            let signatureBlob = field 128
            complete ()
            if publicKey <> pin || purpose <> ascii (signingNamespace role) || reserved.Length <> 0 || algorithm <> ascii "sha512" then PortableSupplierC1.fail ()
            let _,_,keyField,keyComplete = reader publicKey
            if keyField 32 <> ascii "ssh-ed25519" then PortableSupplierC1.fail ()
            let key = keyField 32
            keyComplete ()
            let _,_,signatureField,signatureComplete = reader signatureBlob
            if signatureField 32 <> ascii "ssh-ed25519" then PortableSupplierC1.fail ()
            let rawSignature = signatureField 64
            signatureComplete ()
            if key.Length <> 32 || rawSignature.Length <> 64 then PortableSupplierC1.fail ()
            if not (Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(ReadOnlySpan<byte>(key))) then PortableSupplierC1.fail ()
            let preimage =
                [ascii "SSHSIG"; sshString purpose; sshString reserved; sshString algorithm; sshString (SHA512.HashData payload)] |> Array.concat
            let verifier = Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(key)
            if not (verifier.Verify(Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Algorithm.Ed25519, null,
                                    ReadOnlySpan<byte>(preimage), ReadOnlySpan<byte>(rawSignature))) then PortableSupplierC1.fail ()
            let canonicalEnvelope = PortableSupplierC1.objectBytes (envelope.EnumerateObject() |> Seq.map (fun p -> p.Name,PortableSupplierC1.canonical p.Value) |> Seq.toList)
            Ok(CryptographicSupplierSignature(role,Convert.ToBase64String canonicalEnvelope,Convert.ToBase64String pin))
        with _ -> Error "supplier-signature-refused"
    let role (CryptographicSupplierSignature(value,_,_)) = value
    let envelope (CryptographicSupplierSignature(_,value,_)) = Convert.FromBase64String value
    let publicKeyFingerprint (CryptographicSupplierSignature(_,_,key)) =
        "SHA256:" + (Convert.FromBase64String key |> SHA256.HashData |> Convert.ToBase64String).TrimEnd('=')

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

/// Original configuration data, never a native root, policy or clock witness.
type PortableSupplierNativeRootIdentity =
    { Device: uint64; Inode: uint64; FilesystemUuid: Guid }
type PortableSupplierNativeSourceRoot = CreationAllocationRoot | IndependentlyPinnedRoot of PortableSupplierNativeRootIdentity
type PortableSupplierNativeScopePolicy =
    { Revision: uint64; SourceRoot: PortableSupplierNativeSourceRoot; OwnerUid: uint64; OwnerGid: uint64
      ExcludedEnginePaths: string list }
type PortableSupplierNativeDestinationPolicy =
    { Revision: uint64; Root: PortableSupplierNativeRootIdentity; OwnerUid: uint64; OwnerGid: uint64 }
type PortableSupplierOriginalClockSnapshot =
    { Revision: uint64; ClockId: string; HostBootId: Guid; MonotonicDomainId: Guid; MonotonicFrequency: int64
      AnchorTicks: int64; AnchorUtc: DateTimeOffset; MaximumErrorTicks: int64
      ValidFrom: DateTimeOffset; ValidUntil: DateTimeOffset }
type PortableSupplierOriginalNativeConfiguration =
    private
        { OriginalIntentDigest: string; SignaturePolicyDigest: string; InitialReceiverEpoch: Guid; ConfigurationRevision: uint64
          ScopePolicyBase64: string; ExpectedScopePolicySha256: string
          ClockSnapshotBase64: string; ExpectedClockSnapshotSha256: string
          DestinationPolicyBase64: string option; ExpectedDestinationPolicySha256: string option }

[<RequireQualifiedAccess>]
module PortableSupplierOriginalNativeConfiguration =
    open System.Text.Json
    open PortableSupplierC1
    let private positive value = let n = u64 value in if n = 0UL then fail () else n
    let private root value =
        fields ["device"; "inode"; "filesystem_uuid"] value
        { Device = value.GetProperty("device") |> u64; Inode = value.GetProperty("inode") |> positive
          FilesystemUuid = value.GetProperty("filesystem_uuid") |> uuid }
    let private profile schema role (value: JsonElement) =
        if value.GetProperty("schema").GetString() <> schema || value.GetProperty("profile").GetString() <> "signed-hold-v1"
           || value.GetProperty("role").GetString() <> role then fail ()
    let private path value =
        let text = text 128 value
        PortableSupplierManifest.create [{ Path = text; Bytes = 0UL; Sha256 = String.replicate 64 "0" }] |> Result.defaultWith invalidOp |> ignore
        text
    let pathsConflict (a: string) (b: string) =
        String.Equals(a,b,StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "/",StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/",StringComparison.OrdinalIgnoreCase)
    let private decodeScope value =
        fields ["schema"; "profile"; "role"; "revision"; "source_root_binding"; "owner_uid"; "owner_gid"; "enumeration"; "excluded_engine"] value
        profile "fsgg.v3.native-scope-policy/1" "native-source" value
        if value.GetProperty("enumeration").GetString() <> "entire-source-root" then fail ()
        let binding = value.GetProperty("source_root_binding")
        let source =
            match binding.GetProperty("kind").GetString() with
            | "creation-allocation" -> fields ["kind"] binding; CreationAllocationRoot
            | "pinned" -> fields ["kind"; "root"] binding; IndependentlyPinnedRoot(root (binding.GetProperty("root")))
            | _ -> fail ()
        let exclusions = value.GetProperty("excluded_engine")
        if exclusions.ValueKind <> JsonValueKind.Array || exclusions.GetArrayLength() > 64 then fail ()
        let paths =
            exclusions.EnumerateArray() |> Seq.map (fun row ->
                fields ["path"; "disposition"] row
                if row.GetProperty("disposition").GetString() <> "discard-after-original-settlement" then fail ()
                row.GetProperty("path") |> path) |> Seq.toList
        if paths |> List.pairwise |> List.exists (fun (a,b) -> StringComparer.Ordinal.Compare(a,b) >= 0) then fail ()
        for index,a in List.indexed paths do
            if paths |> List.skip (index+1) |> List.exists (pathsConflict a) then fail ()
        { Revision = value.GetProperty("revision") |> positive; SourceRoot = source
          OwnerUid = value.GetProperty("owner_uid") |> u64; OwnerGid = value.GetProperty("owner_gid") |> u64
          ExcludedEnginePaths = paths }
    let private decodeDestination value =
        fields ["schema"; "profile"; "role"; "revision"; "root"; "owner_uid"; "owner_gid"; "disposition"] value
        profile "fsgg.v3.native-destination-policy/1" "selected-output-destination" value
        if value.GetProperty("disposition").GetString() <> "retain-outside-original-cleanup" then fail ()
        { Revision = value.GetProperty("revision") |> positive; Root = root (value.GetProperty("root"))
          OwnerUid = value.GetProperty("owner_uid") |> u64; OwnerGid = value.GetProperty("owner_gid") |> u64 }
    let private decodeClock value =
        fields ["schema"; "profile"; "role"; "revision"; "clock_id"; "host_boot_id"; "monotonic_domain_id"; "monotonic_frequency"
                "anchor_ticks"; "anchor_utc"; "maximum_error_100ns_ticks"; "valid_from"; "valid_until"] value
        profile "fsgg.v3.original-clock-snapshot/1" "native-observation" value
        let bounded key =
            let n = value.GetProperty(key: string) |> u64
            if n > uint64 Int64.MaxValue then fail ()
            int64 n
        let clock =
            { Revision = value.GetProperty("revision") |> positive; ClockId = value.GetProperty("clock_id") |> text 128
              HostBootId = value.GetProperty("host_boot_id") |> uuid; MonotonicDomainId = value.GetProperty("monotonic_domain_id") |> uuid
              MonotonicFrequency = bounded "monotonic_frequency"; AnchorTicks = bounded "anchor_ticks"
              AnchorUtc = value.GetProperty("anchor_utc") |> utc; MaximumErrorTicks = bounded "maximum_error_100ns_ticks"
              ValidFrom = value.GetProperty("valid_from") |> utc; ValidUntil = value.GetProperty("valid_until") |> utc }
        if clock.MonotonicFrequency <= 0L || clock.ValidFrom > clock.AnchorUtc || clock.AnchorUtc >= clock.ValidUntil
           || clock.AnchorUtc.Ticks < clock.MaximumErrorTicks || DateTimeOffset.MaxValue.Ticks - clock.AnchorUtc.Ticks < clock.MaximumErrorTicks then fail ()
        clock
    let scopeBytes value = Convert.FromBase64String value.ScopePolicyBase64
    let clockBytes value = Convert.FromBase64String value.ClockSnapshotBase64
    let destinationBytes value = value.DestinationPolicyBase64 |> Option.map Convert.FromBase64String
    let scope value = scopeBytes value |> document 8192 |> decodeScope
    let clock value = clockBytes value |> document 4096 |> decodeClock
    let destination value = destinationBytes value |> Option.map (document 2048 >> decodeDestination)
    let metadata value = value.OriginalIntentDigest,value.SignaturePolicyDigest,value.InitialReceiverEpoch,value.ConfigurationRevision
    let expectedHashes value = value.ExpectedScopePolicySha256,value.ExpectedClockSnapshotSha256,value.ExpectedDestinationPolicySha256
    /// These expected hashes must come from independent admission selection, never the native receipt.
    /// This factory constructs historical source data only; it supplies no current-policy/clock witness.
    let create (original: PortableSupplierAdmissionOriginal) revision (scopeInput: byte array) expectedScope
               (clockInput: byte array) expectedClock (destinationInput: (byte array * string) option) =
        try
            if revision = 0UL || original.ReceiverEpoch = Guid.Empty
               || isNull scopeInput || scopeInput.Length = 0 || scopeInput.Length > 8192
               || isNull clockInput || clockInput.Length = 0 || clockInput.Length > 4096
               || (destinationInput |> Option.exists (fun (raw,_) -> isNull raw || raw.Length = 0 || raw.Length > 2048)) then fail ()
            let scopeRaw = Array.copy scopeInput
            let clockRaw = Array.copy clockInput
            let destinationRaw = destinationInput |> Option.map (fun (bytes,expected) -> Array.copy bytes,expected)
            if hash scopeRaw <> expectedScope || hash clockRaw <> expectedClock
               || (destinationRaw |> Option.exists (fun (raw,expected) -> hash raw <> expected)) then fail ()
            let parsedScope = document 8192 scopeRaw |> decodeScope
            let parsedClock = document 4096 clockRaw |> decodeClock
            destinationRaw |> Option.iter (fun (raw,_) -> document 2048 raw |> decodeDestination |> ignore)
            let intent = Convert.FromBase64String original.IntentBase64 |> document 6000
            let createdAt = intent.GetProperty("created_at") |> utc
            if createdAt < parsedClock.ValidFrom || createdAt >= parsedClock.ValidUntil then fail ()
            let selection = intent.GetProperty("selection")
            let selected = selection.GetProperty("kind").GetString() = "selected"
            if selected <> destinationRaw.IsSome then fail ()
            // Full M is joined once retained with the creation pair; exclusions cannot substitute for it.
            let metadataBytes =
                objectBytes ["original_intent_digest",quoted original.IntentDigest; "signature_policy_digest",quoted original.TrustPolicyDigest
                             "initial_receiver_epoch",quoted (original.ReceiverEpoch.ToString("D")); "configuration_revision",number revision
                             "expected_scope_policy_sha256",quoted expectedScope; "expected_clock_snapshot_sha256",quoted expectedClock
                             "expected_destination_policy_sha256",option quoted (destinationRaw |> Option.map snd)]
            let total = int64 scopeRaw.Length + int64 clockRaw.Length + int64 metadataBytes.Length + (destinationRaw |> Option.map (fst >> Array.length >> int64) |> Option.defaultValue 0L)
            if total > 16384L || parsedScope.Revision = 0UL then fail ()
            Ok { OriginalIntentDigest = original.IntentDigest; SignaturePolicyDigest = original.TrustPolicyDigest
                 InitialReceiverEpoch = original.ReceiverEpoch; ConfigurationRevision = revision
                 ScopePolicyBase64 = Convert.ToBase64String scopeRaw; ExpectedScopePolicySha256 = expectedScope
                 ClockSnapshotBase64 = Convert.ToBase64String clockRaw; ExpectedClockSnapshotSha256 = expectedClock
                 DestinationPolicyBase64 = destinationRaw |> Option.map (fst >> Convert.ToBase64String)
                 ExpectedDestinationPolicySha256 = destinationRaw |> Option.map snd }
        with _ -> Error "supplier-native-configuration-refused"
    let validate (original: PortableSupplierAdmissionOriginal) initialEpoch value =
        let intent,policy,epoch,revision = metadata value
        if intent <> original.IntentDigest || policy <> original.TrustPolicyDigest || epoch <> initialEpoch then Error "supplier-native-configuration-original-refused"
        else
            let destination =
                match destinationBytes value,value.ExpectedDestinationPolicySha256 with
                | None,None -> Ok None
                | Some bytes,Some hash -> Ok(Some(bytes,hash))
                | _ -> Error "supplier-native-configuration-destination-refused"
            destination |> Result.bind (fun destination ->
                create { original with ReceiverEpoch = initialEpoch } revision (scopeBytes value) value.ExpectedScopePolicySha256
                       (clockBytes value) value.ExpectedClockSnapshotSha256 destination)
            |> Result.bind (fun captured -> if captured = value then Ok value else Error "supplier-native-configuration-refused")

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

type PortableSupplierJournalVersion = SupplierJournalV2 | SupplierJournalV3 | SupplierJournalV4
type PortableSupplierChallengeKind = SupplierWireV1 | SupplierNativeProofV1 | SupplierSelectedNativeProofV2

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

/// Selected acquisition is anchored before the receiver knows the native receipt hash.
type PortableSupplierSelectedNativeProofContext =
    { Original: PortableSupplierAdmissionOriginal
      OperationId: Guid
      ReservationDigest: string
      ProofEventHead: string
      HostLeaseNotAfter: DateTimeOffset
      ExpectedCurrentHead: string
      ExpectedCurrentSequence: uint64 }

type PortableSupplierSelectedNativeProofChallenge =
    { Context: PortableSupplierSelectedNativeProofContext
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
    | NativeSelectedProofV2 of PortableSupplierSelectedNativeProofChallenge
    member this.Kind = match this with WireV1 _ -> SupplierWireV1 | NativeProofV1 _ -> SupplierNativeProofV1 | NativeSelectedProofV2 _ -> SupplierSelectedNativeProofV2
    member this.ChallengeId = match this with WireV1 c -> c.ChallengeId | NativeProofV1 c -> c.ChallengeId | NativeSelectedProofV2 c -> c.ChallengeId
    member this.Digest = match this with WireV1 c -> c.Digest | NativeProofV1 c -> c.Digest | NativeSelectedProofV2 c -> c.Digest
    member this.BytesBase64 = match this with WireV1 c -> c.BytesBase64 | NativeProofV1 c -> c.BytesBase64 | NativeSelectedProofV2 c -> c.BytesBase64

/// /4 issuance context only; this is not an additional pending slot or nonce ledger.
type PortableSupplierNativeIssuanceAnchor =
    { ChallengeKind: PortableSupplierChallengeKind
      ChallengeId: Guid
      ChallengeDigest: string
      Head: string
      Sequence: uint64 }

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

/// Exact accepted controller bytes retained in the existing supplier record, not a witness.
type PortableSupplierRetainedWireExchange =
    { AcceptedRevision: uint64
      AcceptedStoreFence: uint64
      Challenge: PortableSupplierAdmissionChallenge
      RequestBase64: string
      ResultBase64: string
      EvidenceBase64: string
      ReceiverBundleBase64: string
      ControllerBundleBase64: string }

type PortableSupplierRetainedNativeAcquisition =
    { AcceptedRevision: uint64
      AcceptedStoreFence: uint64
      Challenge: PortableSupplierPendingChallenge
      IssuanceAnchor: PortableSupplierNativeIssuanceAnchor
      RequestBase64: string
      ResultBase64: string
      ProofEnvelopeBase64: string
      ReceiptBase64: string
      ControllerBundleBase64: string }

/// Bounded first-failure input custody. These bytes need not be authenticated or schema-valid.
type PortableSupplierRejectedExchange =
    { AcceptedRevision: uint64
      AcceptedStoreFence: uint64
      Challenge: PortableSupplierPendingChallenge
      NativeAnchor: PortableSupplierNativeIssuanceAnchor option
      RequestBase64: string
      FramesBase64: string list
      ReceiverBundleBase64: string option
      ControllerBundleBase64: string option
      Reason: string }

/// Fresh-only /4 custody. Its fence is data and cannot establish durable ownership.
type PortableSupplierCheckpointCustody =
    { OriginalReceiverEpoch: Guid
      StoreFence: uint64
      OriginalIntentEnvelopeBase64: string
      NativeConfiguration: PortableSupplierOriginalNativeConfiguration
      PendingNativeAnchor: PortableSupplierNativeIssuanceAnchor option
      CreationExchange: PortableSupplierRetainedWireExchange option
      WireExchanges: PortableSupplierRetainedWireExchange list
      NativeAcquisitions: PortableSupplierRetainedNativeAcquisition list
      RejectedExchanges: PortableSupplierRejectedExchange list
      AbandonedChallenges: PortableSupplierPendingChallenge list
      GenesisCheckpointBase64: string option
      OutputCheckpointBase64: string option }

type PortableSupplierAdmissionState =
    { JournalVersion: PortableSupplierJournalVersion
      Revision: uint64
      CheckpointCustody: PortableSupplierCheckpointCustody option
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

/// Source data and a sticky hold remain distinct from later private authenticated candidates.
type PortableSupplierStructuralTransition =
    | SupplierCheckpointData of PortableSupplierAdmissionState
    | SupplierCheckpointHeld of PortableSupplierAdmissionState

/// Source readback only: none of these values can satisfy an ACK or host-effect gate.
type PortableSupplierSourceStage =
    | SupplierSourceReadback of PortableSupplierAdmissionState
    | SupplierSourceConflict
    | SupplierSourceCommitUnknown

type PortableSupplierControlledRevocation = ControlledNotRevoked | ControlledRevoked | ControlledRevocationUnavailable

/// Independently fixed test policy data. This is never production enrollment.
type PortableSupplierControlledRolePolicy =
    { Role: PortableSupplierSignatureRole
      PublicKeyBlob: byte array
      Principal: string
      PolicyBytes: byte array
      ExpectedPolicySha256: string
      PolicyRevision: string
      ValidFrom: DateTimeOffset
      ValidUntil: DateTimeOffset
      RevocationCheckedAt: DateTimeOffset
      Revocation: PortableSupplierControlledRevocation }

type PortableSupplierControlledCurrentObservation =
    { VerifiedAt: DateTimeOffset
      SelectionObservedAt: DateTimeOffset
      SelectionValidUntil: DateTimeOffset
      MaximumObservationAge: TimeSpan
      ReceiverMonotonic: int64
      IssuedReceiverClockDomain: Guid
      CurrentReceiverClockDomain: Guid
      HostClock: PortableSupplierOriginalClockSnapshot }

type private PortableSupplierCapturedRolePolicy =
    { Role: PortableSupplierSignatureRole; PublicKeyBase64: string; Principal: string
      PolicyBase64: string; PolicySha256: string; PolicyRevision: string
      ValidFrom: DateTimeOffset; ValidUntil: DateTimeOffset; RevocationCheckedAt: DateTimeOffset
      Revocation: PortableSupplierControlledRevocation }

type PortableSupplierControlledSourceSelection =
    private
        { Original: PortableSupplierAdmissionOriginal
          OriginalEnvelopeSha256: string
          ExpectedOperationId: Guid
          HostBinding: PortableHostBinding
          NativeConfiguration: PortableSupplierOriginalNativeConfiguration
          RolePolicies: PortableSupplierCapturedRolePolicy list
          Current: PortableSupplierControlledCurrentObservation }

/// Closed paired bytes, never an incoming verified observation or bundle.
type PortableSupplierControlledSourceExchange =
    | ControlledCreationExchange of request: byte array * frames: byte array list * reservation: byte array
    | ControlledGenesisExchange of request: byte array * frames: byte array list

/// Successful real signature/semantic verification against controlled TEST selection only.
/// The immutable state is retained privately; this value cannot construct an ACK or runtime witness.
type PortableSupplierAuthenticatedSourceTransition =
    private | ControlledSourceTransition of PortableSupplierAdmissionOriginal * uint64 * uint64 * PortableSupplierAdmissionState
    member this.Original = let (ControlledSourceTransition(original,_,_,_)) = this in original
    member this.ExpectedRevision = let (ControlledSourceTransition(_,revision,_,_)) = this in revision
    member this.ExpectedFence = let (ControlledSourceTransition(_,_,fence,_)) = this in fence
    member this.State = let (ControlledSourceTransition(_,_,_,state)) = this in state

type PortableSupplierControlledSourceOutcome =
    | AuthenticatedControlledSource of PortableSupplierAuthenticatedSourceTransition
    | HeldControlledSource of PortableSupplierAdmissionState

type PortableSupplierAuthenticatedSourceReadback =
    private | ControlledSourceReadback of PortableSupplierAdmissionState
    member this.State = let (ControlledSourceReadback state) = this in state

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
    let private selectedNativeBytes (challenge: PortableSupplierSelectedNativeProofChallenge) =
        let context = challenge.Context
        let date (value: DateTimeOffset) = value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", Globalization.CultureInfo.InvariantCulture)
        let q = PortableSupplierC1.quoted
        PortableSupplierC1.objectBytes
            [ "schema", q "fsgg.v3.native-proof-challenge/2"; "profile", q "signed-hold-v1"
              "proof_kind", q "selected"; "operation_id", q (context.OperationId.ToString("D"))
              "intent_digest", q context.Original.IntentDigest; "reservation_digest", q context.ReservationDigest
              "proof_event_head", q context.ProofEventHead; "receiver_epoch", q (challenge.ReceiverEpoch.ToString("D"))
              "challenge_id", q (challenge.ChallengeId.ToString("D")); "issued_at", q (date challenge.IssuedAt)
              "not_after", q (date challenge.NotAfter) ]

    let private validSelectedNative (original: PortableSupplierAdmissionOriginal)
                                    (challenge: PortableSupplierSelectedNativeProofChallenge) =
        let context = challenge.Context
        context.Original = original && context.OperationId <> Guid.Empty
        && digest context.ReservationDigest && digest context.ProofEventHead && digest context.ExpectedCurrentHead
        && context.ExpectedCurrentSequence > 0UL && utcMicroseconds context.HostLeaseNotAfter
        && challenge.ChallengeId <> Guid.Empty && challenge.ReceiverEpoch = original.ReceiverEpoch
        && utcMicroseconds challenge.IssuedAt && utcMicroseconds challenge.NotAfter
        && challenge.IssuedAt < challenge.NotAfter && challenge.NotAfter <= original.Command.Deadline
        && challenge.NotAfter <= context.HostLeaseNotAfter
        && challenge.IssuedMonotonic >= 0L && challenge.DeadlineMonotonic > challenge.IssuedMonotonic
        && digest challenge.Digest
        && (match raw 45000 challenge.BytesBase64 with
            | Ok bytes -> bytes = selectedNativeBytes challenge && digestBytes bytes = challenge.Digest
            | _ -> false)

    let private sameOriginal (original: PortableSupplierAdmissionOriginal) (value: PortableSupplierTranscriptObservation) =
        value.IntentDigest = original.IntentDigest && value.TrustPolicyDigest = original.TrustPolicyDigest
        && value.OriginalReservation.Binding.Command = original.Command
        && value.OriginalReservation.Binding.QualifiedImage = original.QualifiedImage
        && value.SupplierSourceRevision = original.SupplierSourceRevision
    let private originalWireCommand (command: PortableWorkspaceCommand) =
        let date = command.Deadline.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", Globalization.CultureInfo.InvariantCulture)
        PortableSupplierC1.objectBytes
            ["command_id", PortableSupplierC1.quoted (command.CommandId.ToString("D"))
             "idempotency_id", PortableSupplierC1.quoted command.IdempotencyId
             "workspace_scope", PortableSupplierC1.quoted command.WorkspaceScope
             "profile_id", PortableSupplierC1.quoted command.ProfileId
             "profile_revision", PortableSupplierC1.number command.ProfileRevision
             "source_revision", PortableSupplierC1.quoted command.SourceRevision
             "expected_workflow_revision", PortableSupplierC1.number command.ExpectedWorkflowRevision
             "fence_generation", PortableSupplierC1.number command.FenceGeneration
             "causation_id", PortableSupplierC1.option PortableSupplierC1.quoted command.CausationId
             "deadline", PortableSupplierC1.quoted date
             "operation", PortableSupplierC1.quoted command.Operation
             "component_id", PortableSupplierC1.option PortableSupplierC1.quoted command.ComponentId]

    let private sourceOriginal (original: PortableSupplierAdmissionOriginal) encoded =
        let signed = raw 65536 encoded |> Result.defaultWith invalidOp
        let bytes, intent = PortableSupplierC1.payload 65536 6000 signed
        let get (key: string) = intent.GetProperty key
        PortableSupplierC1.fields
            ["command_deadline"; "command_digest"; "correlation_id"; "created_at"; "intent_id"; "qualified_inner_image"
             "requested_cleanup_reserve_ticks"; "requested_export_reserve_ticks"; "requested_host_lease_not_after"
             "requested_maximum_runtime_ticks"; "requested_profile"; "requested_source_revision"; "schema"; "selection"] intent
        PortableSupplierWireShape.check_receiver_intent intent
        let originalBytes = raw 6000 original.IntentBase64 |> Result.defaultWith invalidOp
        if bytes <> originalBytes || get("schema").GetString() <> "fsgg.v3.receiver-intent/1"
           || get("requested_profile").GetString() <> "signed-hold-v1"
           || (get "command_digest" |> PortableSupplierC1.digest) <> domain "command" (originalWireCommand original.Command)
           || (get "command_deadline" |> PortableSupplierC1.utc) <> original.Command.Deadline
           || get("qualified_inner_image").GetString() <> original.QualifiedImage
           || get("requested_source_revision").GetString() <> original.SupplierSourceRevision then PortableSupplierC1.fail ()
        for key in ["correlation_id"; "intent_id"] do get key |> PortableSupplierC1.uuid |> ignore
        for key in ["created_at"; "requested_host_lease_not_after"] do get key |> PortableSupplierC1.utc |> ignore
        for key in ["requested_maximum_runtime_ticks"; "requested_export_reserve_ticks"; "requested_cleanup_reserve_ticks"] do get key |> PortableSupplierC1.u64 |> ignore
        intent

    let private sourceWireChallenge (state: PortableSupplierAdmissionState) (challenge: PortableSupplierAdmissionChallenge) =
        let bytes = raw 6000 challenge.BytesBase64 |> Result.defaultWith invalidOp
        let row = PortableSupplierC1.document 6000 bytes
        let get (key: string) = row.GetProperty key
        let intent = raw 6000 state.Original.IntentBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.document 6000
        if challenge.ChallengeId = Guid.Empty || challenge.ReceiverEpoch = Guid.Empty
           || domain "challenge" bytes <> challenge.Digest
           || (get "challenge_id" |> PortableSupplierC1.uuid) <> challenge.ChallengeId
           || (get "issued_at" |> PortableSupplierC1.utc) <> challenge.IssuedAt
           || (get "not_after" |> PortableSupplierC1.utc) <> challenge.NotAfter
           || challenge.IssuedAt >= challenge.NotAfter || challenge.NotAfter > state.Original.Command.Deadline
           || challenge.IssuedMonotonic < 0L || challenge.DeadlineMonotonic <= challenge.IssuedMonotonic
           || (get "challenge_nonce" |> PortableSupplierC1.b64url 32).Length <> 32 then PortableSupplierC1.fail ()
        if challenge.Purpose = "creation" then
            PortableSupplierWireShape.check_creation_challenge row
            PortableSupplierC1.fields ["challenge_id"; "challenge_nonce"; "correlation_id"; "intent_digest"; "issued_at"; "not_after"; "schema"] row
            if get("schema").GetString() <> "fsgg.v3.creation-challenge/1"
               || get("intent_digest").GetString() <> state.Original.IntentDigest
               || get("correlation_id").GetString() <> intent.GetProperty("correlation_id").GetString()
               || challenge.ExpectedHead.IsSome then PortableSupplierC1.fail ()
        else
            PortableSupplierWireShape.check_response_challenge row
            PortableSupplierC1.fields ["challenge_id"; "challenge_nonce"; "issued_at"; "known_head"; "not_after"; "operation_id"; "purpose"; "reservation_digest"; "schema"] row
            let pinned = state.Pinned |> Option.defaultWith PortableSupplierC1.fail
            let creation = raw 6000 pinned.CreationBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.document 6000
            let reservation = raw 6000 pinned.ReservationBase64 |> Result.defaultWith invalidOp
            if get("schema").GetString() <> "fsgg.v3.response-challenge/1"
               || get("purpose").GetString() <> challenge.Purpose
               || not (List.contains challenge.Purpose ["genesis-readback"; "state-readback"; "retirement-readback"])
               || (get "known_head" |> PortableSupplierC1.optional PortableSupplierC1.digest) <> challenge.ExpectedHead
               || get("operation_id").GetString() <> creation.GetProperty("operation_id").GetString()
               || get("reservation_digest").GetString() <> domain "reservation" reservation then PortableSupplierC1.fail ()
        row

    let private bundleCoverage controller (encoded: string) required =
        let bytes = raw (if controller then 8192 else 32768) encoded |> Result.defaultWith invalidOp
        if controller then PortableSupplierVerificationBundle.parseController bytes |> Result.defaultWith invalidOp |> ignore
        else PortableSupplierVerificationBundle.parseReceiver bytes |> Result.defaultWith invalidOp |> ignore
        let root = PortableSupplierC1.document (if controller then 8192 else 32768) bytes
        let rows = root.GetProperty("observations").EnumerateArray() |> Seq.toList
        if rows.Length <> List.length required then PortableSupplierC1.fail ()
        let namespaces =
            ["original-intent", "fsgg-v3-original-intent-v1"; "receiver-command", "fsgg-v3-host-command-v1"
             "host-command-result", "fsgg-v3-host-command-result-v1"; "native-proof", "fsgg-v3-native-proof-v1"
             "creation", "fsgg-v3-supplier-creation-v1"; "genesis", "fsgg-v3-supplier-genesis-v1"
             "supplier-event", "fsgg-v3-supplier-event-v1"; "supplier-response", "fsgg-v3-supplier-response-v1"]
            |> Map.ofList
        for role, exact, payloadDomain in required do
            let payload, _ = PortableSupplierC1.payload 65536 45000 exact
            let expectedDigest = if payloadDomain = "raw-sha256" then digestBytes payload else domain payloadDomain payload
            let matches = rows |> List.filter (fun row -> row.GetProperty("role").GetString() = role && row.GetProperty("envelope_sha256").GetString() = digestBytes exact)
            match matches with
            | [row] when row.GetProperty("payload_digest").GetString() = expectedDigest
                         && row.GetProperty("namespace").GetString() = namespaces[role]
                         && row.GetProperty("key_algorithm").GetString() = "ssh-ed25519"
                         && row.GetProperty("message_hash_algorithm").GetString() = "sha512" -> ()
            | _ -> PortableSupplierC1.fail ()
        root

    let private sourceWirePair (state: PortableSupplierAdmissionState) (e: PortableSupplierRetainedWireExchange) =
        let custody = state.CheckpointCustody |> Option.defaultWith PortableSupplierC1.fail
        let request = raw 65536 e.RequestBase64 |> Result.defaultWith invalidOp
        let result = raw 4096 e.ResultBase64 |> Result.defaultWith invalidOp
        let evidence = raw 65536 e.EvidenceBase64 |> Result.defaultWith invalidOp
        let _, command = PortableSupplierC1.payload 65536 45000 request
        let _, response = PortableSupplierC1.payload 4096 896 result
        PortableSupplierC1.fields ["schema"; "profile"; "request_id"; "kind"; "receiver_policy_digest"; "receiver_epoch"; "body"] command
        PortableSupplierC1.fields ["schema"; "request_digest"; "operation_id"; "result"; "reason"; "checkpoint_digest"; "evidence_kind"; "evidence_digest"; "challenge_digest"; "collected_at"] response
        if command.GetProperty("schema").GetString() <> "fsgg.v3.host-command/1"
           || command.GetProperty("profile").GetString() <> "signed-hold-v1"
           || response.GetProperty("schema").GetString() <> "fsgg.v3.host-command-result/1"
           || response.GetProperty("request_digest").GetString() <> digestBytes request
           || response.GetProperty("evidence_digest").GetString() <> digestBytes evidence
           || response.GetProperty("challenge_digest").GetString() <> e.Challenge.Digest
           || response.GetProperty("reason").GetString() <> "ok"
           || (command.GetProperty("receiver_epoch") |> PortableSupplierC1.uuid) <> e.Challenge.ReceiverEpoch then PortableSupplierC1.fail ()
        command.GetProperty("request_id") |> PortableSupplierC1.uuid |> ignore
        response.GetProperty("operation_id") |> PortableSupplierC1.uuid |> ignore
        response.GetProperty("checkpoint_digest") |> PortableSupplierC1.digest |> ignore
        let collected = response.GetProperty("collected_at") |> PortableSupplierC1.utc
        if collected < e.Challenge.IssuedAt || collected >= e.Challenge.NotAfter then PortableSupplierC1.fail ()
        let signedOriginal = raw 65536 custody.OriginalIntentEnvelopeBase64 |> Result.defaultWith invalidOp
        let controller = bundleCoverage true e.ControllerBundleBase64 ["original-intent",signedOriginal,"raw-sha256"; "receiver-command",request,"raw-sha256"; "host-command-result",result,"raw-sha256"]
        let requestObservation = controller.GetProperty("observations").EnumerateArray() |> Seq.find (fun o -> o.GetProperty("role").GetString() = "receiver-command")
        if (command.GetProperty("receiver_policy_digest") |> PortableSupplierC1.digest) <> requestObservation.GetProperty("policy_digest").GetString() then PortableSupplierC1.fail ()
        let body = command.GetProperty("body")
        let kind = command.GetProperty("kind").GetString()
        let challengeRow = sourceWireChallenge state e.Challenge
        let challengeKey = if e.Challenge.Purpose = "creation" then "creation_challenge" else "response_challenge"
        if (body.GetProperty(challengeKey) |> PortableSupplierC1.b64url 6000) <> (raw 6000 e.Challenge.BytesBase64 |> Result.defaultWith invalidOp) then PortableSupplierC1.fail ()
        if e.Challenge.Purpose = "creation" then
            PortableSupplierC1.fields ["intent"; "creation_challenge"; "create_grant"; "selection_manifest"] body
            if kind <> "create-stopped" || response.GetProperty("result").GetString() <> "committed"
               || response.GetProperty("evidence_kind").GetString() <> "creation"
               || (body.GetProperty("intent") |> PortableSupplierC1.b64url 6000) <> (raw 6000 state.Original.IntentBase64 |> Result.defaultWith invalidOp) then PortableSupplierC1.fail ()
            body.GetProperty("create_grant") |> PortableSupplierC1.b64url 10761 |> ignore
        else
            let expectedFields =
                match kind with
                | "bind-original" -> ["operation_id"; "intent_digest"; "creation_receipt_digest"; "reservation"; "response_challenge"]
                | "collect" -> ["intent_digest"; "operation_id"; "reservation_digest"; "response_challenge"]
                | "settle-observe" -> ["intent_digest"; "operation_id"; "reservation_digest"; "expected_head"; "response_challenge"]
                | "start-original" -> ["intent_digest"; "operation_id"; "reservation_digest"; "expected_head"; "response_challenge"; "start_grant"; "receiver_acceptance"]
                | "retire-original" -> ["intent_digest"; "operation_id"; "reservation_digest"; "expected_head"; "response_challenge"; "retire_grant"; "export_readback"]
                | _ -> PortableSupplierC1.fail ()
            PortableSupplierC1.fields expectedFields body
            if not (List.contains (response.GetProperty("result").GetString()) ["observed"; "committed"])
               || response.GetProperty("evidence_kind").GetString() <> "supplier-response"
               || body.GetProperty("intent_digest").GetString() <> state.Original.IntentDigest
               || body.GetProperty("operation_id").GetString() <> challengeRow.GetProperty("operation_id").GetString()
               || response.GetProperty("operation_id").GetString() <> challengeRow.GetProperty("operation_id").GetString() then PortableSupplierC1.fail ()
            if e.Challenge.Purpose = "genesis-readback" && kind <> "bind-original" && kind <> "collect" then PortableSupplierC1.fail ()
        command, response

    let private sourceSelection (row: System.Text.Json.JsonElement) =
        PortableSupplierWireShape.check_selection row
        match row.GetProperty("kind").GetString() with
        | "none" -> NoSelectedOutputs
        | "selected" ->
            SelectedOutputs { Digest = row.GetProperty("digest") |> PortableSupplierC1.digest
                              MemberCount = row.GetProperty("member_count") |> PortableSupplierC1.u64
                              TotalBytes = row.GetProperty("total_bytes") |> PortableSupplierC1.u64 }
        | _ -> PortableSupplierC1.fail ()

    let private sourceOriginals (state: PortableSupplierAdmissionState) =
        let custody = state.CheckpointCustody |> Option.defaultWith PortableSupplierC1.fail
        let intent = sourceOriginal state.Original custody.OriginalIntentEnvelopeBase64
        let pinned = state.Pinned |> Option.defaultWith PortableSupplierC1.fail
        let coreBytes = raw 6000 pinned.CreationBase64 |> Result.defaultWith invalidOp
        let receiptBytes = raw 18761 pinned.CreationReceiptBase64 |> Result.defaultWith invalidOp
        let reservationBytes = raw 6000 pinned.ReservationBase64 |> Result.defaultWith invalidOp
        let core = PortableSupplierC1.document 6000 coreBytes
        let reservation = PortableSupplierC1.document 6000 reservationBytes
        PortableSupplierWireShape.check_creation_core core
        PortableSupplierWireShape.check_reservation reservation
        let receiptPayload, _ = PortableSupplierC1.payload 18761 6000 receiptBytes
        if receiptPayload <> coreBytes || core.GetProperty("intent_digest").GetString() <> state.Original.IntentDigest
           || reservation.GetProperty("intent_digest").GetString() <> state.Original.IntentDigest
           || reservation.GetProperty("creation_receipt_digest").GetString() <> domain "creation-receipt" receiptBytes
           || PortableSupplierC1.nestedBytes (reservation.GetProperty("command")) <> originalWireCommand state.Original.Command
           || core.GetProperty("operation_id").GetString() <> reservation.GetProperty("operation_id").GetString()
           || core.GetProperty("correlation_id").GetString() <> intent.GetProperty("correlation_id").GetString()
           || reservation.GetProperty("correlation_id").GetString() <> intent.GetProperty("correlation_id").GetString()
           || core.GetProperty("supplier_source_revision").GetString() <> state.Original.SupplierSourceRevision
           || reservation.GetProperty("receiver_source_revision").GetString() <> state.Original.Command.SourceRevision
           || core.GetProperty("profile").GetString() <> "signed-hold-v1"
           || reservation.GetProperty("qualified_inner_image").GetString() <> state.Original.QualifiedImage
           || PortableSupplierC1.nestedBytes (intent.GetProperty("selection")) <> PortableSupplierC1.nestedBytes (reservation.GetProperty("selection")) then PortableSupplierC1.fail ()
        for left,right in ["requested_maximum_runtime_ticks","maximum_runtime_ticks"; "requested_export_reserve_ticks","export_reserve_ticks"; "requested_cleanup_reserve_ticks","cleanup_reserve_ticks"] do
            if intent.GetProperty(left).GetString() <> reservation.GetProperty(right).GetString() then PortableSupplierC1.fail ()
        let date key = reservation.GetProperty(key: string) |> PortableSupplierC1.utc
        let count key = reservation.GetProperty(key: string) |> PortableSupplierC1.u64
        let original = pinned.OriginalReservation
        if original.AdmittedAt <> date "admitted_at" || original.HostLeaseExpiresAt <> date "host_lease_expires_at"
           || original.Binding.Command.Deadline <> date "original_deadline"
           || original.Selection <> sourceSelection (reservation.GetProperty("selection"))
           || original.MaximumRuntime.Ticks < 0L || uint64 original.MaximumRuntime.Ticks <> count "maximum_runtime_ticks"
           || original.ExportReserve.Ticks < 0L || uint64 original.ExportReserve.Ticks <> count "export_reserve_ticks"
           || original.CleanupReserve.Ticks < 0L || uint64 original.CleanupReserve.Ticks <> count "cleanup_reserve_ticks"
           || pinned.Identity.ContainerId <> core.GetProperty("cid").GetString()
           || pinned.Identity.AllocationDevice <> (core.GetProperty("allocation_device") |> PortableSupplierC1.u64)
           || pinned.Identity.AllocationInode <> (core.GetProperty("allocation_inode") |> PortableSupplierC1.u64)
           || pinned.OuterImageId <> core.GetProperty("image_id").GetString()
           || (PortableHostAllocation.executionDeadline original.AdmittedAt original original |> Result.isError) then PortableSupplierC1.fail ()
        let creation = custody.CreationExchange |> Option.defaultWith PortableSupplierC1.fail
        if creation.EvidenceBase64 <> pinned.CreationReceiptBase64
           || core.GetProperty("challenge_digest").GetString() <> creation.Challenge.Digest
           || core.GetProperty("created_at").GetString() > core.GetProperty("stopped_at").GetString()
           || (core.GetProperty("created_at") |> PortableSupplierC1.utc) < creation.Challenge.IssuedAt
           || (core.GetProperty("created_at") |> PortableSupplierC1.utc) > creation.Challenge.NotAfter
           || (core.GetProperty("stopped_at") |> PortableSupplierC1.utc) > original.AdmittedAt
           || (core.GetProperty("host_lease_origin_at") |> PortableSupplierC1.utc) > (core.GetProperty("created_at") |> PortableSupplierC1.utc)
           || (core.GetProperty("host_lease_expires_at") |> PortableSupplierC1.utc) <> original.HostLeaseExpiresAt
           || original.HostLeaseExpiresAt > (intent.GetProperty("requested_host_lease_not_after") |> PortableSupplierC1.utc)
           || (core.GetProperty("stopped_at") |> PortableSupplierC1.utc) >= original.HostLeaseExpiresAt
           || count "maximum_runtime_ticks" > (core.GetProperty("host_maximum_runtime_cap_ticks") |> PortableSupplierC1.u64)
           || count "export_reserve_ticks" < (core.GetProperty("host_min_export_reserve_ticks") |> PortableSupplierC1.u64)
           || count "cleanup_reserve_ticks" < (core.GetProperty("host_min_cleanup_reserve_ticks") |> PortableSupplierC1.u64) then PortableSupplierC1.fail ()
        let request, result = sourceWirePair state creation
        let creationCollected = result.GetProperty("collected_at") |> PortableSupplierC1.utc
        if (core.GetProperty("stopped_at") |> PortableSupplierC1.utc) > creationCollected
           || pinned.Kind <> SupplierCreation || pinned.ChallengeDigest <> creation.Challenge.Digest || pinned.Purpose <> "creation"
           || pinned.ResponseBase64 <> creation.EvidenceBase64 || not pinned.EventsBase64.IsEmpty
           || pinned.FromHead.IsSome || pinned.ToHead.IsSome || pinned.Sequence <> 0UL || pinned.CollectedAt <> creationCollected then PortableSupplierC1.fail ()
        let manifest = request.GetProperty("body").GetProperty("selection_manifest") |> PortableSupplierC1.optional (PortableSupplierC1.b64url 6000)
        let selectedManifest = PortableSupplierManifest.validateSelection original.Selection manifest |> Result.defaultWith invalidOp
        let configuration = custody.NativeConfiguration
        let scope = PortableSupplierOriginalNativeConfiguration.scope configuration
        let clock = PortableSupplierOriginalNativeConfiguration.clock configuration
        if clock.HostBootId <> (core.GetProperty("boot_id") |> PortableSupplierC1.uuid)
           || clock.ValidFrom > original.AdmittedAt || clock.ValidUntil < min original.Binding.Command.Deadline original.HostLeaseExpiresAt then PortableSupplierC1.fail ()
        selectedManifest |> Option.iter (fun value ->
            for memberValue in PortableSupplierManifest.members value do
                if scope.ExcludedEnginePaths |> List.exists (PortableSupplierOriginalNativeConfiguration.pathsConflict memberValue.Path) then PortableSupplierC1.fail ())
        if result.GetProperty("operation_id").GetString() <> core.GetProperty("operation_id").GetString() then PortableSupplierC1.fail ()
        bundleCoverage false creation.ReceiverBundleBase64 ["creation",receiptBytes,"creation"] |> ignore
        intent, core, reservation, coreBytes, receiptBytes, reservationBytes

    let private checkpointCommon (state: PortableSupplierAdmissionState) (exchange: PortableSupplierRetainedWireExchange)
                                 revision fence predecessor provenance =
        let _,core,reservation,coreBytes,receiptBytes,reservationBytes = sourceOriginals state
        let custody = state.CheckpointCustody |> Option.defaultWith PortableSupplierC1.fail
        let signedResponse = raw 65536 exchange.EvidenceBase64 |> Result.defaultWith invalidOp
        let _, response = PortableSupplierC1.payload 65536 45000 signedResponse
        let event = response.GetProperty("events").EnumerateArray() |> Seq.last |> PortableSupplierC1.b64url 26761
        let _, last = PortableSupplierC1.payload 26761 18000 event
        { Provenance = provenance
          JournalKeySha256 = System.Text.Encoding.UTF8.GetBytes(state.Original.Command.WorkspaceScope + "\n" + state.Original.Command.IdempotencyId) |> digestBytes
          OriginalReceiverEpoch = custody.OriginalReceiverEpoch; ReceiverEpoch = exchange.Challenge.ReceiverEpoch
          OperationId = core.GetProperty("operation_id") |> PortableSupplierC1.uuid
          IntentDigest = state.Original.IntentDigest; CommandDigest = domain "command" (originalWireCommand state.Original.Command)
          OriginalIntentEnvelopeSha256 = raw 65536 custody.OriginalIntentEnvelopeBase64 |> Result.defaultWith invalidOp |> digestBytes
          CreationCoreDigest = domain "creation" coreBytes; CreationReceiptDigest = domain "creation-receipt" receiptBytes
          ReservationDigest = domain "reservation" reservationBytes
          SelectionDigest = PortableSupplierC1.nestedBytes (reservation.GetProperty("selection")) |> domain "selection"
          TrustPolicyDigest = state.Original.TrustPolicyDigest
          ReceiverSourceRevision = reservation.GetProperty("receiver_source_revision").GetString()
          SupplierSourceRevision = core.GetProperty("supplier_source_revision").GetString()
          SupplierSourceDigest = core.GetProperty("supplier_source_digest") |> PortableSupplierC1.digest
          CommandFenceGeneration = state.Original.Command.FenceGeneration
          ExpectedJournalRevision = revision - 1UL; JournalRevision = revision; ExpectedStoreFence = fence - 1UL; StoreFence = fence
          PredecessorCheckpointSha256 = predecessor; PreviousHead = exchange.Challenge.ExpectedHead
          ObservedHead = response.GetProperty("to_head") |> PortableSupplierC1.digest
          AcceptedSequence = last.GetProperty("sequence") |> PortableSupplierC1.u64
          AcceptedRequestEnvelopeSha256 = raw 65536 exchange.RequestBase64 |> Result.defaultWith invalidOp |> digestBytes
          AcceptedResultEnvelopeSha256 = raw 4096 exchange.ResultBase64 |> Result.defaultWith invalidOp |> digestBytes
          AcceptedResponseEnvelopeSha256 = digestBytes signedResponse
          AcceptedChallengeDigest = exchange.Challenge.Digest; AcceptedChallengeId = exchange.Challenge.ChallengeId
          AcceptedChallengePurpose = exchange.Challenge.Purpose
          VerificationBundleSha256 = raw 32768 exchange.ReceiverBundleBase64 |> Result.defaultWith invalidOp |> digestBytes
          ControllerVerificationBundleSha256 = raw 8192 exchange.ControllerBundleBase64 |> Result.defaultWith invalidOp |> digestBytes }

    let private selectedProofContext (state: PortableSupplierAdmissionState) =
        let _,core,reservation,_,_,reservationBytes = sourceOriginals state
        let selection = sourceSelection (reservation.GetProperty("selection"))
        let manifest =
            match selection with
            | SelectedOutputs value -> value
            | NoSelectedOutputs -> PortableSupplierC1.fail ()
        let events =
            state.EventsBase64 |> List.skip 1 |> List.choose (fun encoded ->
                let exact = raw 26761 encoded |> Result.defaultWith invalidOp
                let _,event = PortableSupplierC1.payload 26761 12000 exact
                if event.GetProperty("kind").GetString() = "export-manifest" then Some(exact,event) else None)
        let exact,event = match events with [one] -> one | _ -> PortableSupplierC1.fail ()
        let body = event.GetProperty("body")
        if body.GetProperty("manifest_digest").GetString() <> manifest.Digest
           || (body.GetProperty("member_count") |> PortableSupplierC1.u64) <> manifest.MemberCount
           || (body.GetProperty("total_bytes") |> PortableSupplierC1.u64) <> manifest.TotalBytes then PortableSupplierC1.fail ()
        { Original = state.Original
          OperationId = core.GetProperty("operation_id") |> PortableSupplierC1.uuid
          ReservationDigest = domain "reservation" reservationBytes
          ProofEventHead = domain "signed-envelope" exact
          HostLeaseNotAfter = state.Pinned.Value.OriginalReservation.HostLeaseExpiresAt
          ExpectedCurrentHead = state.Head |> Option.defaultWith PortableSupplierC1.fail
          ExpectedCurrentSequence = state.Sequence }

    let private zeroProofContext (state: PortableSupplierAdmissionState) =
        let _,core,reservation,_,_,reservationBytes = sourceOriginals state
        if sourceSelection (reservation.GetProperty("selection")) <> NoSelectedOutputs then PortableSupplierC1.fail ()
        let proofs =
            state.EventsBase64 |> List.skip 1 |> List.choose (fun encoded ->
                let exact = raw 26761 encoded |> Result.defaultWith invalidOp
                let _,event = PortableSupplierC1.payload 26761 12000 exact
                if event.GetProperty("kind").GetString() = "zero-selection-census" then Some(exact,event) else None)
        let exact,event = match proofs with [one] -> one | _ -> PortableSupplierC1.fail ()
        let body = event.GetProperty("body")
        if body.GetProperty("selection_digest").GetString() <> domain "selection" (PortableSupplierC1.nestedBytes (reservation.GetProperty("selection"))) then PortableSupplierC1.fail ()
        { Original = state.Original; OperationId = core.GetProperty("operation_id") |> PortableSupplierC1.uuid
          ReservationDigest = domain "reservation" reservationBytes; ProofEventHead = domain "signed-envelope" exact
          NativeReceiptSha256 = body.GetProperty("receipt_digest") |> PortableSupplierC1.digest
          HostLeaseNotAfter = state.Pinned.Value.OriginalReservation.HostLeaseExpiresAt }

    let private nativeAnchorMatches (state: PortableSupplierAdmissionState) (challenge: PortableSupplierPendingChallenge)
                                    (anchor: PortableSupplierNativeIssuanceAnchor) =
        challenge.Kind <> SupplierWireV1 && anchor.ChallengeKind = challenge.Kind
        && anchor.ChallengeId = challenge.ChallengeId && anchor.ChallengeDigest = challenge.Digest
        && Some anchor.Head = state.Head && anchor.Sequence = state.Sequence
        && digest anchor.Head

    let private sourceNativePair (state: PortableSupplierAdmissionState) (e: PortableSupplierRetainedNativeAcquisition) =
        if not (nativeAnchorMatches state e.Challenge e.IssuanceAnchor) then PortableSupplierC1.fail ()
        let original,operation,reservation,proofHead,expectedReceipt,epoch,id,challengeDigest,challengeBytes,issued,notAfter,selected =
            match e.Challenge with
            | WireV1 _ -> PortableSupplierC1.fail ()
            | NativeProofV1 challenge ->
                if not (validNative state.Original challenge) || challenge.Context <> zeroProofContext state then PortableSupplierC1.fail ()
                let c = challenge.Context
                c.Original,c.OperationId,c.ReservationDigest,c.ProofEventHead,Some c.NativeReceiptSha256,
                challenge.ReceiverEpoch,challenge.ChallengeId,challenge.Digest,challenge.BytesBase64,challenge.IssuedAt,challenge.NotAfter,false
            | NativeSelectedProofV2 challenge ->
                if not (validSelectedNative state.Original challenge) || challenge.Context <> selectedProofContext state then PortableSupplierC1.fail ()
                let c = challenge.Context
                c.Original,c.OperationId,c.ReservationDigest,c.ProofEventHead,None,
                challenge.ReceiverEpoch,challenge.ChallengeId,challenge.Digest,challenge.BytesBase64,challenge.IssuedAt,challenge.NotAfter,true
        let _,core,reservationRow,coreBytes,_,reservationBytes = sourceOriginals state
        if original <> state.Original || operation <> (core.GetProperty("operation_id") |> PortableSupplierC1.uuid)
           || reservation <> domain "reservation" reservationBytes then PortableSupplierC1.fail ()
        let proof =
            state.EventsBase64 |> List.skip 1 |> List.map (fun encoded -> raw 26761 encoded |> Result.defaultWith invalidOp)
            |> List.filter (fun exact -> domain "signed-envelope" exact = proofHead)
        let proofEnvelope = match proof with [one] -> one | _ -> PortableSupplierC1.fail ()
        let proofPayload,proofEvent = PortableSupplierC1.payload 26761 12000 proofEnvelope
        let proofBody = proofEvent.GetProperty("body")
        if proofEvent.GetProperty("kind").GetString() <> (if selected then "export-manifest" else "zero-selection-census") then PortableSupplierC1.fail ()
        let selection = sourceSelection (reservationRow.GetProperty("selection"))
        match selected,selection with true,SelectedOutputs _ | false,NoSelectedOutputs -> () | _ -> PortableSupplierC1.fail ()
        let request = raw 65536 e.RequestBase64 |> Result.defaultWith invalidOp
        let result = raw 4096 e.ResultBase64 |> Result.defaultWith invalidOp
        let proof = raw 65536 e.ProofEnvelopeBase64 |> Result.defaultWith invalidOp
        let receipt = raw 32768 e.ReceiptBase64 |> Result.defaultWith invalidOp
        PortableSupplierNativeReceipt.parse receipt |> Result.defaultWith invalidOp |> ignore
        let receiptRow = PortableSupplierC1.document 32768 receipt
        let proofReceipt,_ = PortableSupplierC1.payload 65536 32768 proof
        if receipt <> proofReceipt then PortableSupplierC1.fail ()
        let _,command = PortableSupplierC1.payload 65536 45000 request
        let _,response = PortableSupplierC1.payload 4096 896 result
        PortableSupplierC1.fields ["schema"; "profile"; "request_id"; "kind"; "receiver_policy_digest"; "receiver_epoch"; "body"] command
        PortableSupplierC1.fields ["schema"; "request_digest"; "operation_id"; "result"; "reason"; "checkpoint_digest"; "evidence_kind"; "evidence_digest"; "challenge_digest"; "collected_at"] response
        if command.GetProperty("schema").GetString() <> (if selected then "fsgg.v3.host-command/2" else "fsgg.v3.host-command/1")
           || command.GetProperty("profile").GetString() <> "signed-hold-v1" || command.GetProperty("kind").GetString() <> "read-native-proof"
           || (command.GetProperty("receiver_epoch") |> PortableSupplierC1.uuid) <> epoch
           || response.GetProperty("schema").GetString() <> "fsgg.v3.host-command-result/1"
           || response.GetProperty("result").GetString() <> "observed" || response.GetProperty("reason").GetString() <> "ok"
           || response.GetProperty("evidence_kind").GetString() <> "native-proof"
           || response.GetProperty("evidence_digest").GetString() <> digestBytes proof
           || response.GetProperty("request_digest").GetString() <> digestBytes request
           || response.GetProperty("challenge_digest").GetString() <> challengeDigest
           || (response.GetProperty("operation_id") |> PortableSupplierC1.uuid) <> operation then PortableSupplierC1.fail ()
        command.GetProperty("request_id") |> PortableSupplierC1.uuid |> ignore
        response.GetProperty("checkpoint_digest") |> PortableSupplierC1.digest |> ignore
        let collected = response.GetProperty("collected_at") |> PortableSupplierC1.utc
        if collected < issued || collected >= notAfter then PortableSupplierC1.fail ()
        let body = command.GetProperty("body")
        PortableSupplierC1.fields
            (["operation_id"; "intent_digest"; "reservation_digest"; "proof_event_head"; "challenge"]
             @ [if selected then "proof_kind" else "native_receipt_sha256"]) body
        if (body.GetProperty("operation_id") |> PortableSupplierC1.uuid) <> operation
           || body.GetProperty("intent_digest").GetString() <> original.IntentDigest
           || body.GetProperty("reservation_digest").GetString() <> reservation
           || body.GetProperty("proof_event_head").GetString() <> proofHead
           || PortableSupplierC1.nestedBytes (body.GetProperty("challenge")) <> (raw 45000 challengeBytes |> Result.defaultWith invalidOp) then PortableSupplierC1.fail ()
        if selected then
            if body.GetProperty("proof_kind").GetString() <> "selected" then PortableSupplierC1.fail ()
        else
            if Some(body.GetProperty("native_receipt_sha256") |> PortableSupplierC1.digest) <> expectedReceipt
               || Some(proofBody.GetProperty("receipt_digest") |> PortableSupplierC1.digest) <> expectedReceipt
               || Some(digestBytes receipt) <> expectedReceipt then PortableSupplierC1.fail ()
        let observations = bundleCoverage true e.ControllerBundleBase64
                                ["receiver-command",request,"raw-sha256"; "host-command-result",result,"raw-sha256"; "native-proof",proof,"raw-sha256"]
        let requestObservation = observations.GetProperty("observations").EnumerateArray() |> Seq.find (fun row -> row.GetProperty("role").GetString() = "receiver-command")
        if (command.GetProperty("receiver_policy_digest") |> PortableSupplierC1.digest) <> requestObservation.GetProperty("policy_digest").GetString()
           || (receiptRow.GetProperty("operation_id") |> PortableSupplierC1.uuid) <> operation
           || receiptRow.GetProperty("intent_digest").GetString() <> original.IntentDigest
           || receiptRow.GetProperty("creation_core_digest").GetString() <> domain "creation" coreBytes
           || receiptRow.GetProperty("reservation_digest").GetString() <> reservation
           || receiptRow.GetProperty("selection_digest").GetString() <> domain "selection" (PortableSupplierC1.nestedBytes (reservationRow.GetProperty("selection"))) then PortableSupplierC1.fail ()
        let observed = receiptRow.GetProperty("observed_at") |> PortableSupplierC1.utc
        if observed < state.Pinned.Value.OriginalReservation.AdmittedAt || observed > collected
           || observed >= original.Command.Deadline || observed >= state.Pinned.Value.OriginalReservation.HostLeaseExpiresAt
           || observed <> (proofBody.GetProperty("observed_at") |> PortableSupplierC1.utc) then PortableSupplierC1.fail ()
        if selected then
            let creationBody =
                let _,creationRequest = raw 65536 state.CheckpointCustody.Value.CreationExchange.Value.RequestBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.payload 65536 45000
                creationRequest.GetProperty("body")
            let exactManifest = creationBody.GetProperty("selection_manifest") |> PortableSupplierC1.b64url 6000
            if (receiptRow.GetProperty("manifest_bytes") |> PortableSupplierC1.b64url 6000) <> exactManifest then PortableSupplierC1.fail ()
            for key in ["manifest_digest"; "member_count"; "total_bytes"] do
                if receiptRow.GetProperty(key).GetString() <> proofBody.GetProperty(key).GetString() then PortableSupplierC1.fail ()
        let config = state.CheckpointCustody.Value.NativeConfiguration
        let scope = PortableSupplierOriginalNativeConfiguration.scope config
        let clock = PortableSupplierOriginalNativeConfiguration.clock config
        let expectedScope,expectedClock,expectedDestination = PortableSupplierOriginalNativeConfiguration.expectedHashes config
        let sourceRoot =
            match scope.SourceRoot with
            | IndependentlyPinnedRoot root -> root
            | CreationAllocationRoot ->
                { Device = core.GetProperty("allocation_device") |> PortableSupplierC1.u64
                  Inode = core.GetProperty("allocation_inode") |> PortableSupplierC1.u64
                  FilesystemUuid = core.GetProperty("allocation_fs_uuid") |> PortableSupplierC1.uuid }
        if sourceRoot.Inode = 0UL then PortableSupplierC1.fail ()
        let matchesRoot (expected: PortableSupplierNativeRootIdentity) (row: System.Text.Json.JsonElement) =
            (row.GetProperty("device") |> PortableSupplierC1.u64) = expected.Device
            && (row.GetProperty("inode") |> PortableSupplierC1.u64) = expected.Inode
            && (row.GetProperty("filesystem_uuid") |> PortableSupplierC1.uuid) = expected.FilesystemUuid
        if receiptRow.GetProperty("scope_policy_digest").GetString() <> expectedScope
           || receiptRow.GetProperty("clock_snapshot_sha256").GetString() <> expectedClock
           || not (matchesRoot sourceRoot (receiptRow.GetProperty("source_root")))
           || clock.HostBootId <> (core.GetProperty("boot_id") |> PortableSupplierC1.uuid) then PortableSupplierC1.fail ()
        // These are structural interval checks against declared uncertainty only, not clock qualification.
        if observed.Ticks < clock.MaximumErrorTicks || DateTimeOffset.MaxValue.Ticks - observed.Ticks < clock.MaximumErrorTicks then PortableSupplierC1.fail ()
        let earliest = observed.AddTicks(-clock.MaximumErrorTicks)
        let latest = observed.AddTicks(clock.MaximumErrorTicks)
        if earliest < state.Pinned.Value.OriginalReservation.AdmittedAt || earliest < clock.ValidFrom
           || latest >= min original.Command.Deadline state.Pinned.Value.OriginalReservation.HostLeaseExpiresAt
           || latest > collected || latest >= clock.ValidUntil then PortableSupplierC1.fail ()
        let entries key = receiptRow.GetProperty(key: string).EnumerateArray() |> Seq.toList
        if selected then
            let destination = PortableSupplierOriginalNativeConfiguration.destination config |> Option.defaultWith PortableSupplierC1.fail
            if Some(receiptRow.GetProperty("destination_policy_digest").GetString()) <> expectedDestination
               || destination.Root = sourceRoot || not (matchesRoot destination.Root (receiptRow.GetProperty("destination_root"))) then PortableSupplierC1.fail ()
            for row in entries "readback_members" do
                if (row.GetProperty("uid") |> PortableSupplierC1.u64) <> destination.OwnerUid
                   || (row.GetProperty("gid") |> PortableSupplierC1.u64) <> destination.OwnerGid then PortableSupplierC1.fail ()
        else
            if expectedDestination.IsSome || (PortableSupplierOriginalNativeConfiguration.destination config |> Option.isSome) then PortableSupplierC1.fail ()
            let inventory = entries "inventory"
            let directories =
                inventory |> List.choose (fun row -> if row.GetProperty("kind").GetString() = "directory" then Some(row.GetProperty("path").GetString()) else None) |> Set.ofList
            for row in inventory do
                if (row.GetProperty("uid") |> PortableSupplierC1.u64) <> scope.OwnerUid
                   || (row.GetProperty("gid") |> PortableSupplierC1.u64) <> scope.OwnerGid then PortableSupplierC1.fail ()
                let path = row.GetProperty("path").GetString()
                if row.GetProperty("kind").GetString() = "regular-file" && not (List.contains path scope.ExcludedEnginePaths) then PortableSupplierC1.fail ()
                let segments = path.Split('/')
                for length in 1 .. segments.Length-1 do
                    let parent = String.Join("/",segments,0,length)
                    if not (directories.Contains parent) then PortableSupplierC1.fail ()
        receiptRow,proofEnvelope,proofPayload,id,epoch

    let private outputCheckpoint (state: PortableSupplierAdmissionState) (exchange: PortableSupplierRetainedNativeAcquisition) provenance =
        let custody = state.CheckpointCustody.Value
        let genesisBytes = raw 16384 custody.GenesisCheckpointBase64.Value |> Result.defaultWith invalidOp
        let genesisHash = digestBytes genesisBytes
        let receipt,proofEnvelope,proofPayload,id,epoch = sourceNativePair state exchange
        let wireCommon = checkpointCommon state (List.last custody.WireExchanges) exchange.AcceptedRevision exchange.AcceptedStoreFence (Some genesisHash) provenance
        let common = { wireCommon with ReceiverEpoch = epoch }
        let selection =
            match exchange.Challenge with
            | NativeSelectedProofV2 _ ->
                let exact = receipt.GetProperty("manifest_bytes") |> PortableSupplierC1.b64url 6000
                let manifest = PortableSupplierManifest.parse exact |> Result.defaultWith invalidOp
                CheckpointSelected(digestBytes exact, PortableSupplierManifest.summary manifest)
            | NativeProofV1 _ -> CheckpointNone
            | WireV1 _ -> PortableSupplierC1.fail ()
        let output =
            { GenesisCheckpointSha256 = genesisHash; Selection = selection
              ProofSignedEnvelopeSha256 = digestBytes proofEnvelope; ProofPayloadDigest = domain "event" proofPayload
              NativeReceiptSha256 = raw 32768 exchange.ReceiptBase64 |> Result.defaultWith invalidOp |> digestBytes
              NativeProofEnvelopeSha256 = raw 65536 exchange.ProofEnvelopeBase64 |> Result.defaultWith invalidOp |> digestBytes
              NativeRequestEnvelopeSha256 = raw 65536 exchange.RequestBase64 |> Result.defaultWith invalidOp |> digestBytes
              NativeResultEnvelopeSha256 = raw 4096 exchange.ResultBase64 |> Result.defaultWith invalidOp |> digestBytes
              NativeChallengeDigest = exchange.Challenge.Digest; NativeChallengeId = id; NativeReceiverEpoch = epoch
              NativeControllerVerificationBundleSha256 = raw 8192 exchange.ControllerBundleBase64 |> Result.defaultWith invalidOp |> digestBytes }
        PortableSupplierCheckpoint.createOutput common output |> Result.defaultWith invalidOp

    let private validateCustodyReferences (state: PortableSupplierAdmissionState) (c: PortableSupplierCheckpointCustody) =
        sourceOriginal state.Original c.OriginalIntentEnvelopeBase64 |> ignore
        PortableSupplierOriginalNativeConfiguration.validate state.Original c.OriginalReceiverEpoch c.NativeConfiguration |> Result.defaultWith invalidOp |> ignore
        if c.RejectedExchanges.Length > 1 || c.RejectedExchanges.IsEmpty <> state.FirstFailure.IsNone then PortableSupplierC1.fail ()
        for rejected in c.RejectedExchanges do
            match rejected.Challenge,rejected.NativeAnchor with
            | WireV1 _,None -> ()
            | (NativeProofV1 _ | NativeSelectedProofV2 _),Some anchor when nativeAnchorMatches state rejected.Challenge anchor -> ()
            | _ -> PortableSupplierC1.fail ()
            if Some rejected.Reason <> state.FirstFailure || not (present rejected.Reason)
               || rejected.FramesBase64.Length > 2 || state.Conflicts.IsEmpty then PortableSupplierC1.fail ()
            if rejected.AcceptedRevision < 2UL || rejected.AcceptedRevision > state.Revision || rejected.AcceptedStoreFence <> rejected.AcceptedRevision then PortableSupplierC1.fail ()
            match state.Challenge with
            | Some pending when pending = rejected.Challenge -> ()
            | None when List.contains rejected.Challenge c.AbandonedChallenges
                        && List.contains (rejected.Challenge.Kind,rejected.Challenge.ChallengeId,rejected.Challenge.Digest,"abandoned") state.ChallengeLedger -> ()
            | _ -> PortableSupplierC1.fail ()
            raw 65536 rejected.RequestBase64 |> Result.defaultWith invalidOp |> ignore
            for frame in rejected.FramesBase64 do
                if frame <> "" then raw 65536 frame |> Result.defaultWith invalidOp |> ignore
            rejected.ReceiverBundleBase64 |> Option.iter (fun value -> raw 32768 value |> Result.defaultWith invalidOp |> ignore)
            rejected.ControllerBundleBase64 |> Option.iter (fun value -> raw 8192 value |> Result.defaultWith invalidOp |> ignore)
            match rejected.Challenge with
            | WireV1 challenge -> sourceWireChallenge state challenge |> ignore
            | NativeProofV1 challenge -> if not (validNative { state.Original with ReceiverEpoch = challenge.ReceiverEpoch } challenge) then PortableSupplierC1.fail ()
            | NativeSelectedProofV2 challenge -> if not (validSelectedNative { state.Original with ReceiverEpoch = challenge.ReceiverEpoch } challenge) then PortableSupplierC1.fail ()
        if state.CheckpointBase64.IsSome || state.AckDraftBase64.IsSome then PortableSupplierC1.fail ()
        let wire = (c.CreationExchange |> Option.toList) @ c.WireExchanges
        let committed =
            (wire |> List.map (fun e -> SupplierWireV1,e.Challenge.ChallengeId,e.Challenge.Digest,e.AcceptedRevision,e.AcceptedStoreFence))
            @ (c.NativeAcquisitions |> List.map (fun e -> e.Challenge.Kind,e.Challenge.ChallengeId,e.Challenge.Digest,e.AcceptedRevision,e.AcceptedStoreFence))
        for kind,id,digest,revision,fence in committed do
            if revision < 2UL || revision > state.Revision || fence <> revision
               || not (List.contains (kind,id,digest,"consumed") state.ChallengeLedger) then PortableSupplierC1.fail ()
        let revisions = committed |> List.map (fun (_,_,_,r,_) -> r)
        if Set.count (Set.ofList revisions) <> revisions.Length then PortableSupplierC1.fail ()
        for kind,id,digest,status in state.ChallengeLedger do
            if status = "consumed" then
                if not (committed |> List.exists (fun (k,i,d,_,_) -> (k,i,d) = (kind,id,digest))) then PortableSupplierC1.fail ()
            elif not (c.AbandonedChallenges |> List.exists (fun p -> (p.Kind,p.ChallengeId,p.Digest) = (kind,id,digest))) then PortableSupplierC1.fail ()
        for abandoned in c.AbandonedChallenges do
            if not (List.contains (abandoned.Kind,abandoned.ChallengeId,abandoned.Digest,"abandoned") state.ChallengeLedger) then PortableSupplierC1.fail ()
            match abandoned with
            | WireV1 challenge -> sourceWireChallenge state challenge |> ignore
            | NativeProofV1 challenge ->
                if not (validNative { state.Original with ReceiverEpoch = challenge.ReceiverEpoch } challenge) then PortableSupplierC1.fail ()
            | NativeSelectedProofV2 challenge ->
                if not (validSelectedNative { state.Original with ReceiverEpoch = challenge.ReceiverEpoch } challenge) then PortableSupplierC1.fail ()
        if state.Pinned.IsSome <> c.CreationExchange.IsSome then PortableSupplierC1.fail ()
        match state.Challenge,c.PendingNativeAnchor with
        | (None | Some(WireV1 _)),None -> ()
        | Some((NativeProofV1 _ | NativeSelectedProofV2 _) as challenge),Some anchor when nativeAnchorMatches state challenge anchor -> ()
        | _ -> PortableSupplierC1.fail ()
        match state.Challenge with
        | Some(NativeProofV1 challenge) ->
            if challenge.Context <> zeroProofContext state then PortableSupplierC1.fail ()
        | Some(NativeSelectedProofV2 challenge) ->
            if challenge.Context <> selectedProofContext state then PortableSupplierC1.fail ()
        | Some(WireV1 challenge) -> sourceWireChallenge state challenge |> ignore
        | _ -> ()
        if state.Pinned.IsSome then
            let _,core,reservation,_,receiptBytes,reservationBytes = sourceOriginals state
            let mutable head = None
            let mutable sequence = 0UL
            let mutable retained = []
            let mutable seenStart = false
            let mutable previousLease: byte array option = None
            let mutable leaseExpiry = core.GetProperty("host_lease_expires_at") |> PortableSupplierC1.utc
            let admitted = reservation.GetProperty("admitted_at") |> PortableSupplierC1.utc
            let originalLimit = min state.Original.Command.Deadline state.Pinned.Value.OriginalReservation.HostLeaseExpiresAt
            let mutable priorRevision = c.CreationExchange.Value.AcceptedRevision
            for exchange in c.WireExchanges do
                if exchange.AcceptedRevision <= priorRevision then PortableSupplierC1.fail ()
                priorRevision <- exchange.AcceptedRevision
                let command,result = sourceWirePair state exchange
                let _, response = raw 65536 exchange.EvidenceBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.payload 65536 45000
                PortableSupplierWireShape.check_supplier_response response
                let get (key: string) = response.GetProperty key
                let actual = get "events" |> fun array -> array.EnumerateArray() |> Seq.map (PortableSupplierC1.b64url 26761) |> Seq.toList
                let mode = get("mode").GetString()
                if (get "from_head" |> PortableSupplierC1.optional PortableSupplierC1.digest) <> head
                   || exchange.Challenge.ExpectedHead <> head || get("challenge_digest").GetString() <> exchange.Challenge.Digest
                   || get("purpose").GetString() <> exchange.Challenge.Purpose
                   || get("operation_id").GetString() <> core.GetProperty("operation_id").GetString()
                   || get("reservation_digest").GetString() <> domain "reservation" reservationBytes then PortableSupplierC1.fail ()
                let collected = get "collected_at" |> PortableSupplierC1.utc
                if collected < exchange.Challenge.IssuedAt || collected >= exchange.Challenge.NotAfter
                   || collected > (result.GetProperty("collected_at") |> PortableSupplierC1.utc) then PortableSupplierC1.fail ()
                let body = command.GetProperty("body")
                if command.GetProperty("kind").GetString() = "bind-original" then
                    if (body.GetProperty("reservation") |> PortableSupplierC1.b64url 6000) <> reservationBytes
                       || body.GetProperty("creation_receipt_digest").GetString() <> domain "creation-receipt" receiptBytes then PortableSupplierC1.fail ()
                else
                    if body.GetProperty("reservation_digest").GetString() <> domain "reservation" reservationBytes then PortableSupplierC1.fail ()
                    if command.GetProperty("kind").GetString() <> "collect" && (body.GetProperty("expected_head") |> PortableSupplierC1.digest |> Some) <> head then PortableSupplierC1.fail ()
                let newEvents = if mode = "unchanged" then [] else actual
                if mode = "unchanged" then
                    if retained.IsEmpty || actual <> [List.last retained] then PortableSupplierC1.fail ()
                for exact in newEvents do
                    let bytes,event = PortableSupplierC1.payload 26761 18000 exact
                    if head.IsNone then
                        PortableSupplierWireShape.check_genesis event
                        if mode <> "genesis" || actual.Length <> 1 || bytes.Length > 18000
                           || PortableSupplierC1.nestedBytes (event.GetProperty("creation_core")) <> (raw 6000 state.Pinned.Value.CreationBase64 |> Result.defaultWith invalidOp)
                           || PortableSupplierC1.nestedBytes (event.GetProperty("reservation")) <> reservationBytes
                           || event.GetProperty("creation_core_digest").GetString() <> domain "creation" (PortableSupplierC1.nestedBytes (event.GetProperty("creation_core")))
                           || event.GetProperty("creation_receipt_digest").GetString() <> domain "creation-receipt" receiptBytes
                           || event.GetProperty("reservation_digest").GetString() <> domain "reservation" reservationBytes then PortableSupplierC1.fail ()
                        let observed = event.GetProperty("observed_at") |> PortableSupplierC1.utc
                        if observed < admitted || observed > collected || observed >= originalLimit then PortableSupplierC1.fail ()
                        sequence <- 0UL
                    else
                        PortableSupplierWireShape.check_supplier_event event
                        if mode <> "append" || bytes.Length > 12000 || sequence = UInt64.MaxValue
                           || event.GetProperty("predecessor_head").GetString() <> head.Value
                           || (event.GetProperty("sequence") |> PortableSupplierC1.u64) <> sequence + 1UL
                           || event.GetProperty("operation_id").GetString() <> core.GetProperty("operation_id").GetString()
                           || event.GetProperty("reservation_digest").GetString() <> domain "reservation" reservationBytes then PortableSupplierC1.fail ()
                        let kind = event.GetProperty("kind").GetString()
                        let body = event.GetProperty("body")
                        let occurred = event.GetProperty("occurred_at") |> PortableSupplierC1.utc
                        if occurred < admitted || occurred > collected || occurred >= originalLimit then PortableSupplierC1.fail ()
                        if List.contains kind ["launch-intent"; "start-invoked"] then
                            for field,originalField in ["original_cid","cid"; "original_image_id","image_id"; "original_inode","allocation_inode"] do
                                if body.GetProperty(field).GetString() <> core.GetProperty(originalField).GetString() then PortableSupplierC1.fail ()
                        if List.contains kind ["termination"; "inner-cleanup-intent"; "inner-cleanup-observed"; "outer-retire-intent"; "outer-retire-observed"]
                           && body.GetProperty("cid").GetString() <> core.GetProperty("cid").GetString() then PortableSupplierC1.fail ()
                        if kind = "workload-exit" && body.GetProperty("command_id").GetString() <> state.Original.Command.CommandId.ToString("D") then PortableSupplierC1.fail ()
                        if kind = "unit-activation-observed" then
                            if not seenStart then PortableSupplierC1.fail ()
                            for field in ["unit_name"; "boot_id"; "cid"] do
                                if body.GetProperty(field).GetString() <> core.GetProperty(field).GetString() then PortableSupplierC1.fail ()
                        if kind = "start-invoked" then seenStart <- true
                        if kind = "zero-selection-census" then
                            if sourceSelection (reservation.GetProperty("selection")) <> NoSelectedOutputs
                               || body.GetProperty("selection_digest").GetString() <> domain "selection" (PortableSupplierC1.nestedBytes (reservation.GetProperty("selection"))) then PortableSupplierC1.fail ()
                        if kind = "export-manifest" then
                            match sourceSelection (reservation.GetProperty("selection")) with
                            | NoSelectedOutputs -> PortableSupplierC1.fail ()
                            | SelectedOutputs manifest ->
                                if body.GetProperty("manifest_digest").GetString() <> manifest.Digest
                                   || (body.GetProperty("member_count") |> PortableSupplierC1.u64) <> manifest.MemberCount
                                   || (body.GetProperty("total_bytes") |> PortableSupplierC1.u64) <> manifest.TotalBytes then PortableSupplierC1.fail ()
                        if kind = "lease-amendment" then
                            let expectedKind,expectedDigest =
                                match previousLease with
                                | None -> "creation-receipt", domain "creation-receipt" receiptBytes
                                | Some previous -> "lease-amendment", domain "lease-amendment" previous
                            let newExpiry = body.GetProperty("new_expiry") |> PortableSupplierC1.utc
                            if body.GetProperty("previous_lease_kind").GetString() <> expectedKind
                               || body.GetProperty("previous_lease_digest").GetString() <> expectedDigest
                               || (body.GetProperty("old_expiry") |> PortableSupplierC1.utc) <> leaseExpiry
                               || newExpiry <= leaseExpiry then PortableSupplierC1.fail ()
                            previousLease <- Some bytes
                            leaseExpiry <- newExpiry
                        sequence <- sequence + 1UL
                    head <- Some(domain "signed-envelope" exact)
                    retained <- retained @ [exact]
                if (get "to_head" |> PortableSupplierC1.digest |> Some) <> head then PortableSupplierC1.fail ()
                let first = retained.Head
                let requiredEvents = actual |> List.filter (fun exact -> exact <> first) |> List.map (fun exact -> "supplier-event",exact,"event")
                bundleCoverage false exchange.ReceiverBundleBase64
                    (["creation",receiptBytes,"creation"; "genesis",first,"genesis"; "supplier-response",(raw 65536 exchange.EvidenceBase64 |> Result.defaultWith invalidOp),"response"] @ requiredEvents) |> ignore
            if retained <> (state.EventsBase64 |> List.map (fun text -> raw 26761 text |> Result.defaultWith invalidOp))
               || head <> state.Head || sequence <> state.Sequence
               || (c.WireExchanges |> List.map _.EvidenceBase64) <> state.ResponsesBase64 then PortableSupplierC1.fail ()
            match c.GenesisCheckpointBase64, c.WireExchanges with
            | None, [] ->
                if state.Phase <> SupplierCreationPinned && state.Phase <> SupplierAwaitingGenesis then PortableSupplierC1.fail ()
            | Some encoded, first::_ ->
                let bytes = raw 16384 encoded |> Result.defaultWith invalidOp
                let genesis = PortableSupplierCheckpoint.parseGenesis bytes |> Result.defaultWith invalidOp
                let common = checkpointCommon state first first.AcceptedRevision first.AcceptedStoreFence None (PortableSupplierCheckpoint.genesisCommon genesis).Provenance
                let eg = raw 26761 state.EventsBase64.Head |> Result.defaultWith invalidOp
                let g,_ = PortableSupplierC1.payload 26761 18000 eg
                let expected = PortableSupplierCheckpoint.createGenesis common (domain "signed-envelope" eg) (domain "genesis" g) |> Result.defaultWith invalidOp
                if PortableSupplierCheckpoint.serializeGenesis expected <> bytes || state.Phase <> SupplierGenesisAccepted then PortableSupplierC1.fail ()
            | _ -> PortableSupplierC1.fail ()
        elif not c.WireExchanges.IsEmpty || c.GenesisCheckpointBase64.IsSome || state.Phase <> SupplierIntentStored
             || state.Head.IsSome || state.Sequence <> 0UL || not state.EventsBase64.IsEmpty || not state.ResponsesBase64.IsEmpty then PortableSupplierC1.fail ()
        if state.Revision = 1UL && (state.Original.ReceiverEpoch <> c.OriginalReceiverEpoch || state.Challenge.IsSome
                                   || not state.ChallengeLedger.IsEmpty || not c.AbandonedChallenges.IsEmpty
                                   || state.FirstFailure.IsSome || not state.Conflicts.IsEmpty) then PortableSupplierC1.fail ()
        match c.NativeAcquisitions,c.OutputCheckpointBase64 with
        | [],None -> ()
        | [acquisition],Some encoded ->
            if state.Challenge.IsSome || c.PendingNativeAnchor.IsSome || c.GenesisCheckpointBase64.IsNone || c.WireExchanges.IsEmpty
               || acquisition.AcceptedRevision <= (List.last c.WireExchanges).AcceptedRevision then PortableSupplierC1.fail ()
            let bytes = raw 16384 encoded |> Result.defaultWith invalidOp
            let parsed = PortableSupplierCheckpoint.parseOutput bytes |> Result.defaultWith invalidOp
            let expected = outputCheckpoint state acquisition (PortableSupplierCheckpoint.outputCommon parsed).Provenance
            if PortableSupplierCheckpoint.serializeOutput expected <> bytes then PortableSupplierC1.fail ()
        | _ -> PortableSupplierC1.fail ()

    let private transcriptBytes (state: PortableSupplierAdmissionState) =
        let pinned =
            state.Pinned
            |> Option.map (fun p -> [p.CreationBase64; p.CreationReceiptBase64; p.ReservationBase64; p.ResponseBase64] @ p.EventsBase64)
            |> Option.defaultValue []
        let custody =
            state.CheckpointCustody |> Option.map (fun c ->
                let exchange (e: PortableSupplierRetainedWireExchange) =
                    [e.Challenge.BytesBase64; e.RequestBase64; e.ResultBase64; e.EvidenceBase64; e.ReceiverBundleBase64; e.ControllerBundleBase64]
                [c.OriginalIntentEnvelopeBase64
                 PortableSupplierOriginalNativeConfiguration.scopeBytes c.NativeConfiguration |> Convert.ToBase64String
                 PortableSupplierOriginalNativeConfiguration.clockBytes c.NativeConfiguration |> Convert.ToBase64String]
                @ (PortableSupplierOriginalNativeConfiguration.destinationBytes c.NativeConfiguration |> Option.map Convert.ToBase64String |> Option.toList)
                @ (c.CreationExchange |> Option.map exchange |> Option.defaultValue [])
                @ (c.WireExchanges |> List.collect exchange)
                @ (c.NativeAcquisitions |> List.collect (fun e -> [e.Challenge.BytesBase64; e.RequestBase64; e.ResultBase64; e.ProofEnvelopeBase64; e.ReceiptBase64; e.ControllerBundleBase64]))
                @ (c.RejectedExchanges |> List.collect (fun e ->
                    [e.Challenge.BytesBase64; e.RequestBase64] @ e.FramesBase64
                    @ Option.toList e.ReceiverBundleBase64 @ Option.toList e.ControllerBundleBase64))
                @ (c.AbandonedChallenges |> List.map _.BytesBase64)
                @ (c.GenesisCheckpointBase64 |> Option.toList) @ (c.OutputCheckpointBase64 |> Option.toList))
            |> Option.defaultValue []
        custody @ [state.Original.IntentBase64] @ pinned @ (state.Challenge |> Option.map (fun p -> p.BytesBase64) |> Option.toList)
        @ state.EventsBase64 @ state.ResponsesBase64
        @ (state.CheckpointBase64 |> Option.toList) @ (state.AckDraftBase64 |> Option.toList)
    /// Validate bounded immutable source data. It is never a durable/authentication/effect predicate.
    let validate (state: PortableSupplierAdmissionState) =
        try
            if isNull (box state) || isNull (box state.Original) then Error "supplier-admission-state-refused"
            else
                // A quarantined malformed empty frame is retained exactly; it supplies zero raw bytes.
                let decoded = transcriptBytes state |> List.map (fun value -> if value = "" then Ok [||] else raw 65536 value)
                let total = decoded |> List.sumBy (function Ok bytes -> int64 bytes.Length | Error _ -> int64 MaximumRecordBytes)
                let validLedger =
                    state.ChallengeLedger |> List.forall (fun (kind, id, hash, status) ->
                        (match state.JournalVersion with SupplierJournalV2 -> kind = SupplierWireV1 | SupplierJournalV3 -> kind <> SupplierSelectedNativeProofV2 | SupplierJournalV4 -> true)
                        && id <> Guid.Empty && digest hash && (status = "consumed" || status = "abandoned"))
                let uniqueIds = state.ChallengeLedger |> List.map (fun (_, id, _, _) -> id) |> Set.ofList
                let validChallenge =
                    state.Challenge |> Option.forall (fun pending ->
                        let valid =
                            match pending with
                            | NativeProofV1 challenge -> state.JournalVersion <> SupplierJournalV2 && validNative state.Original challenge
                            | NativeSelectedProofV2 challenge ->
                                state.JournalVersion = SupplierJournalV4 && validSelectedNative state.Original challenge
                                && Some challenge.Context.ExpectedCurrentHead = state.Head
                                && challenge.Context.ExpectedCurrentSequence = state.Sequence
                            | WireV1 challenge ->
                                challenge.ChallengeId <> Guid.Empty && digest challenge.Digest
                                && challenge.ReceiverEpoch = state.Original.ReceiverEpoch && challenge.ExpectedHead = state.Head
                                && challenge.IssuedAt < challenge.NotAfter && challenge.NotAfter <= state.Original.Command.Deadline
                                && challenge.IssuedMonotonic >= 0L && challenge.DeadlineMonotonic > challenge.IssuedMonotonic
                                && (match raw 45000 challenge.BytesBase64 with Ok bytes -> domain "challenge" bytes = challenge.Digest | _ -> false)
                        valid && not (uniqueIds.Contains pending.ChallengeId))
                let validCustody =
                    match state.JournalVersion, state.CheckpointCustody with
                    | (SupplierJournalV2 | SupplierJournalV3), None -> true
                    | SupplierJournalV4, Some c ->
                        validateCustodyReferences state c
                        c.OriginalReceiverEpoch <> Guid.Empty && c.StoreFence = state.Revision
                        && validRaw 65536 c.OriginalIntentEnvelopeBase64
                        && c.WireExchanges.Length + c.NativeAcquisitions.Length + c.RejectedExchanges.Length + (if c.CreationExchange.IsSome then 1 else 0) <= 128
                        && c.AbandonedChallenges.Length <= 128
                        && (c.GenesisCheckpointBase64 |> Option.forall (fun value ->
                            match raw 16384 value with Ok bytes -> PortableSupplierCheckpoint.parseGenesis bytes |> Result.isOk | _ -> false))
                        && (c.OutputCheckpointBase64 |> Option.forall (fun value ->
                            match raw 16384 value with Ok bytes -> PortableSupplierCheckpoint.parseOutput bytes |> Result.isOk | _ -> false))
                        && (c.OutputCheckpointBase64.IsNone || c.GenesisCheckpointBase64.IsSome)
                    | _ -> false
                if state.Revision = 0UL || not validCustody
                   || (state.JournalVersion = SupplierJournalV4 && state.Revision = UInt64.MaxValue && state.Challenge.IsSome && state.FirstFailure.IsNone)
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
            { JournalVersion = SupplierJournalV2; Revision = 1UL; CheckpointCustody = None; Original = original; Phase = SupplierIntentStored; Challenge = None
              ChallengeLedger = []; Pinned = None; Head = None; Sequence = 0UL; EventsBase64 = []
              ResponsesBase64 = []; CheckpointBase64 = None; AckDraftBase64 = None
              FirstFailure = None; Conflicts = Set.empty }
        match raw 45000 original.IntentBase64 with
        | Ok bytes when domain "intent" bytes = original.IntentDigest -> validate state
        | _ -> Error "supplier-admission-original-intent-refused"

    let private next (state: PortableSupplierAdmissionState) =
        if state.Revision = UInt64.MaxValue then Error "supplier-admission-revision-overflow"
        else
            match state.JournalVersion, state.CheckpointCustody with
            | SupplierJournalV4, Some custody when custody.StoreFence <> UInt64.MaxValue ->
                Ok { state with Revision = state.Revision + 1UL; CheckpointCustody = Some { custody with StoreFence = custody.StoreFence + 1UL } }
            | SupplierJournalV4, _ -> Error "supplier-admission-fence-overflow"
            | _, _ -> Ok { state with Revision = state.Revision + 1UL }

    let private issueChallengeCore (challenge: PortableSupplierAdmissionChallenge) (state: PortableSupplierAdmissionState) =
        let expectedPurpose = if state.Phase = SupplierIntentStored then "creation" elif state.Head.IsNone then "genesis-readback" else "state-readback"
        if (state.CheckpointCustody |> Option.exists (fun c -> c.OutputCheckpointBase64.IsSome))
           || (state.JournalVersion = SupplierJournalV4 && state.Revision > UInt64.MaxValue - 2UL)
           || state.FirstFailure.IsSome || not state.Conflicts.IsEmpty || state.Challenge.IsSome
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
            | None | Some(NativeProofV1 _) | Some(NativeSelectedProofV2 _) -> refusal
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
        |> Result.bind (fun proposed -> if proposed.JournalVersion = SupplierJournalV4 then Ok proposed else validate proposed)

    /// No-effect reducer. Even a successful source transition is not ACK or execution authority.
    let private acceptCore (verified: PortableSupplierVerifiedTranscript) (state: PortableSupplierAdmissionState) =
        let value = verified.Observation
        if verified.ExpectedRevision <> state.Revision then Error "supplier-admission-stale-revision"
        elif not (sameOriginal state.Original value) then Error "supplier-admission-originals-changed"
        else
            match state.Challenge with
            | None -> Error "supplier-admission-no-pending-challenge"
            | Some(NativeProofV1 _) | Some(NativeSelectedProofV2 _) -> Error "supplier-admission-no-pending-wire-challenge"
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
                        let pinned =
                            if state.JournalVersion = SupplierJournalV4 then state.Pinned |> Option.orElse (Some value)
                            else Some value
                        let proposed = next { state with Phase = phase; Challenge = None; ChallengeLedger = consumed
                                                         Pinned = pinned; Head = head; Sequence = sequence; EventsBase64 = events
                                                         ResponsesBase64 = (if value.Kind = SupplierCreation then state.ResponsesBase64 else state.ResponsesBase64 @ [value.ResponseBase64]) }
                        if state.JournalVersion = SupplierJournalV4 then proposed else proposed |> Result.bind validate
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

    let private expectedV4 original revision fence (state: PortableSupplierAdmissionState) =
        state.JournalVersion = SupplierJournalV4 && state.Original = original && state.Revision = revision
        && (state.CheckpointCustody |> Option.exists (fun c -> c.StoreFence = fence))

    let private retainWireV4 (verified: PortableSupplierVerifiedTranscript) (exchange: PortableSupplierRetainedWireExchange) original revision fence state =
        if not (expectedV4 original revision fence state) then Error "supplier-v4-original-revision-fence-refused"
        elif state.FirstFailure.IsSome || not state.Conflicts.IsEmpty then Ok(SupplierCheckpointHeld state)
        elif state.Challenge <> Some(WireV1 exchange.Challenge) then Error "supplier-v4-exchange-challenge-refused"
        elif verified.EnvelopeSha256 <> (raw 65536 exchange.EvidenceBase64 |> Result.defaultWith invalidOp |> digestBytes) then Error "supplier-v4-exchange-evidence-refused"
        else
            acceptCore verified state |> Result.bind (fun proposed ->
                if proposed.FirstFailure.IsSome || not proposed.Conflicts.IsEmpty then
                    let custody = proposed.CheckpointCustody.Value
                    let rejected =
                        { AcceptedRevision = proposed.Revision; AcceptedStoreFence = custody.StoreFence
                          Challenge = WireV1 exchange.Challenge; NativeAnchor = None; RequestBase64 = exchange.RequestBase64
                          FramesBase64 = [exchange.ResultBase64; exchange.EvidenceBase64]
                          ReceiverBundleBase64 = Some exchange.ReceiverBundleBase64; ControllerBundleBase64 = Some exchange.ControllerBundleBase64
                          Reason = proposed.FirstFailure.Value }
                    let complete = { proposed with CheckpointCustody = Some { custody with RejectedExchanges = [rejected] } }
                    validate complete |> Result.map SupplierCheckpointHeld
                else
                    let custody = proposed.CheckpointCustody.Value
                    let retained = { exchange with AcceptedRevision = proposed.Revision; AcceptedStoreFence = custody.StoreFence }
                    let updated =
                        if verified.Observation.Kind = SupplierCreation then
                            if custody.CreationExchange.IsSome then PortableSupplierC1.fail ()
                            { custody with CreationExchange = Some retained }
                        else { custody with WireExchanges = custody.WireExchanges @ [retained] }
                    let candidate = { proposed with CheckpointCustody = Some updated }
                    if verified.Observation.Kind = SupplierGenesis then
                        if updated.GenesisCheckpointBase64.IsSome then Error "supplier-v4-genesis-already-retained"
                        else
                            let common = checkpointCommon candidate retained proposed.Revision custody.StoreFence None "controlled-test"
                            let exact = raw 26761 proposed.EventsBase64.Head |> Result.defaultWith invalidOp
                            let payload,_ = PortableSupplierC1.payload 26761 18000 exact
                            PortableSupplierCheckpoint.createGenesis common (domain "signed-envelope" exact) (domain "genesis" payload)
                            |> Result.bind (fun checkpoint ->
                                let complete = { candidate with CheckpointCustody = Some { updated with GenesisCheckpointBase64 = Some(Convert.ToBase64String(PortableSupplierCheckpoint.serializeGenesis checkpoint)) } }
                                validate complete |> Result.map SupplierCheckpointData)
                    else validate candidate |> Result.map SupplierCheckpointData)

    /// Quarantine the first encountered failed/uncertain exchange in the existing authority record.
    /// The pending challenge remains sticky; these bytes can only produce a held source state.
    let quarantineExchangeV4 original revision fence reason (request: string) (frames: string list)
                             (receiverBundle: string option) (controllerBundle: string option)
                             (state: PortableSupplierAdmissionState) =
        try
            if not (expectedV4 original revision fence state) then Error "supplier-v4-original-revision-fence-refused"
            elif state.FirstFailure.IsSome || not state.Conflicts.IsEmpty then Ok(SupplierCheckpointHeld state)
            elif state.Challenge.IsNone || frames.Length > 2
                 || not (List.contains reason ["supplier-authentication-refused"; "supplier-context-refused"; "supplier-framing-refused"; "supplier-exchange-unknown"]) then
                Error "supplier-v4-quarantine-refused"
            else
                validate state |> Result.bind (fun current ->
                    let requestBytes = raw 65536 request |> Result.defaultWith invalidOp
                    let exact = frames |> List.map (fun value -> if value = "" then [||] else raw 65536 value |> Result.defaultWith invalidOp)
                    receiverBundle |> Option.iter (fun value -> raw 32768 value |> Result.defaultWith invalidOp |> ignore)
                    controllerBundle |> Option.iter (fun value -> raw 8192 value |> Result.defaultWith invalidOp |> ignore)
                    let challenge = current.Challenge.Value
                    let cause = Array.concat (requestBytes::exact) |> digestBytes
                    next { current with FirstFailure = Some reason; Conflicts = current.Conflicts.Add cause }
                    |> Result.bind (fun proposed ->
                        let custody = proposed.CheckpointCustody.Value
                        let rejected =
                            { AcceptedRevision = proposed.Revision; AcceptedStoreFence = custody.StoreFence; Challenge = challenge
                              NativeAnchor = custody.PendingNativeAnchor; RequestBase64 = request; FramesBase64 = frames
                              ReceiverBundleBase64 = receiverBundle; ControllerBundleBase64 = controllerBundle; Reason = reason }
                        validate { proposed with CheckpointCustody = Some { custody with RejectedExchanges = [rejected] } }
                        |> Result.map SupplierCheckpointHeld))
        with _ -> Error "supplier-v4-quarantine-refused"

    // Before accepting supplied pair bytes, keep CAS/original/pending conflicts distinct.
    // After that boundary every failed join is retained through the existing sticky hold.
    let private acceptRetainedWireV4 verified (exchange: PortableSupplierRetainedWireExchange) original revision fence state =
        if not (expectedV4 original revision fence state) then Error "supplier-v4-original-revision-fence-refused"
        elif state.Challenge <> Some(WireV1 exchange.Challenge) then Error "supplier-v4-exchange-challenge-refused"
        else
            let quarantine () =
                quarantineExchangeV4 original revision fence "supplier-context-refused" exchange.RequestBase64
                    [exchange.ResultBase64;exchange.EvidenceBase64] (Some exchange.ReceiverBundleBase64) (Some exchange.ControllerBundleBase64) state
            try
                match retainWireV4 verified exchange original revision fence state with
                | Ok accepted -> Ok accepted
                | Error _ -> quarantine ()
            with _ -> quarantine ()

    /// Explicit structural caller: signatures are supplied by the separate transcript-verifier port.
    /// The retained pair and all cold joins are checked here; no authenticated/durable result escapes.
    let pinCreationV4 (verified: PortableSupplierVerifiedTranscript) exchange original revision fence state =
        try
            if verified.Observation.Kind <> SupplierCreation then Error "supplier-v4-creation-required"
            else acceptRetainedWireV4 verified exchange original revision fence state
        with _ -> Error "supplier-v4-creation-refused"

    /// Genesis and every later wire exchange share the existing reducer and owned byte custody.
    let acceptWireV4 (verified: PortableSupplierVerifiedTranscript) exchange original revision fence state =
        try
            if verified.Observation.Kind = SupplierCreation then Error "supplier-v4-wire-required"
            else acceptRetainedWireV4 verified exchange original revision fence state
        with _ -> Error "supplier-v4-wire-refused"

    /// Full structural native/output join. A real current-signature/policy factory must precede authenticated use.
    /// No native, durable, ACK or effect capability is produced by this source caller.
    let acceptNativeV4 original revision fence now monotonic (exchange: PortableSupplierRetainedNativeAcquisition)
                       (state: PortableSupplierAdmissionState) =
        let refuse () =
            quarantineExchangeV4 original revision fence "supplier-context-refused" exchange.RequestBase64
                [exchange.ResultBase64;exchange.ProofEnvelopeBase64] None (Some exchange.ControllerBundleBase64) state
        try
            if not (expectedV4 original revision fence state) then Error "supplier-v4-original-revision-fence-refused"
            elif state.FirstFailure.IsSome || not state.Conflicts.IsEmpty then Ok(SupplierCheckpointHeld state)
            elif state.Challenge <> Some exchange.Challenge || state.CheckpointCustody.Value.PendingNativeAnchor <> Some exchange.IssuanceAnchor
                 || state.CheckpointCustody.Value.OutputCheckpointBase64.IsSome then Error "supplier-v4-native-pending-refused"
            else
                validate state |> Result.bind (fun current ->
                    sourceNativePair current exchange |> ignore
                    let issued,notAfter,issuedMonotonic,deadlineMonotonic =
                        match exchange.Challenge with
                        | NativeProofV1 c -> c.IssuedAt,c.NotAfter,c.IssuedMonotonic,c.DeadlineMonotonic
                        | NativeSelectedProofV2 c -> c.IssuedAt,c.NotAfter,c.IssuedMonotonic,c.DeadlineMonotonic
                        | WireV1 _ -> PortableSupplierC1.fail ()
                    let _,result = raw 4096 exchange.ResultBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.payload 4096 896
                    let collected = result.GetProperty("collected_at") |> PortableSupplierC1.utc
                    if now < issued || now >= notAfter || monotonic < issuedMonotonic || monotonic >= deadlineMonotonic
                       || collected > now || now - collected > TimeSpan.FromSeconds 30. then PortableSupplierC1.fail ()
                    next { current with Challenge = None
                                        ChallengeLedger = (exchange.Challenge.Kind,exchange.Challenge.ChallengeId,exchange.Challenge.Digest,"consumed") :: current.ChallengeLedger }
                    |> Result.bind (fun proposed ->
                        let custody = proposed.CheckpointCustody.Value
                        let retained = { exchange with AcceptedRevision = proposed.Revision; AcceptedStoreFence = custody.StoreFence }
                        let candidate = { proposed with CheckpointCustody = Some { custody with PendingNativeAnchor = None; NativeAcquisitions = [retained] } }
                        let checkpoint = outputCheckpoint candidate retained "controlled-test"
                        let complete =
                            { candidate with CheckpointCustody = Some { candidate.CheckpointCustody.Value with
                                                                         OutputCheckpointBase64 = Some(PortableSupplierCheckpoint.serializeOutput checkpoint |> Convert.ToBase64String) } }
                        validate complete |> Result.map SupplierCheckpointData))
        with _ -> refuse ()

    /// A restart cannot revive an outstanding challenge in a new monotonic clock domain.
    let private abandonChallengeCore newEpoch (state: PortableSupplierAdmissionState) =
        if newEpoch = Guid.Empty || newEpoch = state.Original.ReceiverEpoch
           || (state.JournalVersion = SupplierJournalV4 && state.Challenge.IsNone) then Error "supplier-admission-epoch-refused"
        else
            let ledger =
                match state.Challenge with
                | None -> state.ChallengeLedger
                | Some challenge -> (challenge.Kind, challenge.ChallengeId, challenge.Digest, "abandoned") :: state.ChallengeLedger
            let custody = state.CheckpointCustody |> Option.map (fun c ->
                { c with PendingNativeAnchor = None; AbandonedChallenges = c.AbandonedChallenges @ (state.Challenge |> Option.toList) })
            next { state with Original = { state.Original with ReceiverEpoch = newEpoch }; Challenge = None; ChallengeLedger = ledger; CheckpointCustody = custody }
            |> Result.bind validate

    let beginIntent original =
        try beginIntentCore original with _ -> Error "supplier-admission-original-refused"
    /// Explicit fresh-original selection. Stage still refuses an occupied journal key.
    let beginIntentV3 original = beginIntent original |> Result.map (fun state -> { state with JournalVersion = SupplierJournalV3 })
    /// Fresh /4 source custody only. The same-store StageV4 refuses every occupied key.
    let beginIntentV4 original (signedOriginalIntent: byte array) (configuration: PortableSupplierOriginalNativeConfiguration) =
        try
            let snapshot = Array.copy signedOriginalIntent
            let bytes, intent = PortableSupplierC1.payload 65536 6000 snapshot
            sourceOriginal original (Convert.ToBase64String snapshot) |> ignore
            PortableSupplierOriginalNativeConfiguration.validate original original.ReceiverEpoch configuration |> Result.defaultWith invalidOp |> ignore
            let originalBytes = raw 6000 original.IntentBase64 |> Result.defaultWith invalidOp
            if bytes <> originalBytes || intent.GetProperty("schema").GetString() <> "fsgg.v3.receiver-intent/1"
               || intent.GetProperty("requested_profile").GetString() <> "signed-hold-v1"
               || intent.GetProperty("command_digest").GetString() <> domain "command" (originalWireCommand original.Command) then
                Error "supplier-v4-original-refused"
            else
                beginIntentCore original |> Result.bind (fun state ->
                    validate { state with JournalVersion = SupplierJournalV4
                                          CheckpointCustody = Some { OriginalReceiverEpoch = original.ReceiverEpoch; StoreFence = 1UL
                                                                     OriginalIntentEnvelopeBase64 = Convert.ToBase64String snapshot
                                                                     NativeConfiguration = configuration; PendingNativeAnchor = None
                                                                     CreationExchange = None; WireExchanges = []; NativeAcquisitions = []; RejectedExchanges = []; AbandonedChallenges = []
                                                                     GenesisCheckpointBase64 = None; OutputCheckpointBase64 = None } })
        with _ -> Error "supplier-v4-original-refused"
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
    /// Structural issuance from the exact retained selected prefix; no receipt/hash input exists.
    /// Authentication and current clock policy remain separately required by the source caller.
    let issueSelectedNativeProofChallengeV4 original revision fence challengeId issuedAt notAfter
                                            issuedMonotonic deadlineMonotonic (state: PortableSupplierAdmissionState) =
        try
            if not (expectedV4 original revision fence state) || state.Challenge.IsSome
               || state.Revision > UInt64.MaxValue - 2UL
               || state.FirstFailure.IsSome || not state.Conflicts.IsEmpty
               || state.Phase <> SupplierGenesisAccepted || state.CheckpointCustody.Value.OutputCheckpointBase64.IsSome
               || state.ChallengeLedger.Length >= 128
               || (state.ChallengeLedger |> List.exists (fun (_, id, _, _) -> id = challengeId)) then
                Error "supplier-selected-native-proof-challenge-refused"
            else
                validate state |> Result.bind (fun current ->
                    let context = selectedProofContext current
                    let provisional: PortableSupplierSelectedNativeProofChallenge =
                        { Context = context; ChallengeId = challengeId; Digest = String.replicate 64 "0"; BytesBase64 = ""
                          ReceiverEpoch = current.Original.ReceiverEpoch; IssuedAt = issuedAt; NotAfter = notAfter
                          IssuedMonotonic = issuedMonotonic; DeadlineMonotonic = deadlineMonotonic }
                    let exact = selectedNativeBytes provisional
                    let challenge = { provisional with Digest = digestBytes exact; BytesBase64 = Convert.ToBase64String exact }
                    if not (validSelectedNative current.Original challenge) then Error "supplier-selected-native-proof-challenge-refused"
                    else
                        let anchor = { ChallengeKind = SupplierSelectedNativeProofV2; ChallengeId = challenge.ChallengeId
                                       ChallengeDigest = challenge.Digest; Head = context.ExpectedCurrentHead; Sequence = context.ExpectedCurrentSequence }
                        let custody = { current.CheckpointCustody.Value with PendingNativeAnchor = Some anchor }
                        next { current with Challenge = Some(NativeSelectedProofV2 challenge); CheckpointCustody = Some custody } |> Result.bind validate)
        with _ -> Error "supplier-selected-native-proof-challenge-refused"

    /// /4 zero issuance derives the known receipt reference from the exact retained census event.
    let issueZeroNativeProofChallengeV4 original revision fence challengeId issuedAt notAfter
                                        issuedMonotonic deadlineMonotonic (state: PortableSupplierAdmissionState) =
        try
            if not (expectedV4 original revision fence state) || state.Challenge.IsSome
               || state.Revision > UInt64.MaxValue - 2UL
               || state.FirstFailure.IsSome || not state.Conflicts.IsEmpty
               || state.Phase <> SupplierGenesisAccepted || state.CheckpointCustody.Value.OutputCheckpointBase64.IsSome
               || state.ChallengeLedger.Length >= 128
               || (state.ChallengeLedger |> List.exists (fun (_, id, _, _) -> id = challengeId)) then
                Error "supplier-zero-native-proof-challenge-refused"
            else
                validate state |> Result.bind (fun current ->
                    let context = zeroProofContext current
                    let provisional: PortableSupplierNativeProofChallenge =
                        { Context = context; ChallengeId = challengeId; Digest = String.replicate 64 "0"; BytesBase64 = ""
                          ReceiverEpoch = current.Original.ReceiverEpoch; IssuedAt = issuedAt; NotAfter = notAfter
                          IssuedMonotonic = issuedMonotonic; DeadlineMonotonic = deadlineMonotonic }
                    let exact = nativeBytes provisional
                    let challenge = { provisional with Digest = digestBytes exact; BytesBase64 = Convert.ToBase64String exact }
                    if not (validNative current.Original challenge) then Error "supplier-zero-native-proof-challenge-refused"
                    else
                        let anchor = { ChallengeKind = SupplierNativeProofV1; ChallengeId = challenge.ChallengeId
                                       ChallengeDigest = challenge.Digest; Head = current.Head.Value; Sequence = current.Sequence }
                        let custody = { current.CheckpointCustody.Value with PendingNativeAnchor = Some anchor }
                        next { current with Challenge = Some(NativeProofV1 challenge); CheckpointCustody = Some custody } |> Result.bind validate)
        with _ -> Error "supplier-zero-native-proof-challenge-refused"

    let verify authenticator now monotonic state envelope =
        try verifyCore authenticator now monotonic state envelope with _ -> Error "supplier-admission-verification-refused"
    let accept verified state =
        if state.JournalVersion = SupplierJournalV4 then Error "supplier-v4-paired-checkpoint-required"
        else try acceptCore verified state with _ -> Error "supplier-admission-result-refused"
    let abandonChallenge epoch state =
        try abandonChallengeCore epoch state with _ -> Error "supplier-admission-restart-refused"

    /// Durability is unqualified in this source slice. Never prepare a usable ACK or hand off work.
    let prepareAck (_state: PortableSupplierAdmissionState) : Result<byte array, string> = Error "supplier-admission-durability-unavailable"
    let reserveHost (_state: PortableSupplierAdmissionState) : Result<unit, string> = Error "supplier-admission-production-handoff-unavailable"

    let private controlledRoleName = function
        | OriginalIntentSignature -> "original-intent" | EffectGrantSignature -> "create-effect-grant"
        | ReceiverCommandSignature -> "receiver-command" | GenesisAckSignature -> "receiver-genesis-ack"
        | OutputAckSignature -> "receiver-export-readback" | HostCommandResultSignature -> "host-command-result"
        | NativeProofSignature -> "native-proof" | SupplierCreationSignature -> "creation"
        | SupplierGenesisSignature -> "genesis" | SupplierEventSignature -> "supplier-event"
        | SupplierResponseSignature -> "supplier-response"

    /// Capture independently fixed TEST selection. No production context or verification result is made here.
    let createControlledSourceSelection (original: PortableSupplierAdmissionOriginal) originalEnvelopeSha256 operationId
                                        (binding: PortableHostBinding) (configuration: PortableSupplierOriginalNativeConfiguration)
                                        (policies: PortableSupplierControlledRolePolicy list)
                                        (current: PortableSupplierControlledCurrentObservation) =
        try
            if not (digest originalEnvelopeSha256) || operationId = Guid.Empty
               || binding.Command <> original.Command || binding.QualifiedImage <> original.QualifiedImage
               || policies.IsEmpty || policies.Length > 11
               || not (utcMicroseconds current.VerifiedAt && utcMicroseconds current.SelectionObservedAt && utcMicroseconds current.SelectionValidUntil)
               || (policies |> List.map _.Role |> Set.ofList |> Set.count) <> policies.Length then PortableSupplierC1.fail ()
            PortableSupplierOriginalNativeConfiguration.validate original original.ReceiverEpoch configuration |> Result.defaultWith invalidOp |> ignore
            let captured = policies |> List.map (fun policy ->
                if isNull policy.PublicKeyBlob || policy.PublicKeyBlob.Length <> 51
                   || isNull policy.PolicyBytes || policy.PolicyBytes.Length = 0 || policy.PolicyBytes.Length > 4096
                   || not (present policy.Principal) || policy.Principal.Length > 256
                   || not (present policy.PolicyRevision) || policy.PolicyRevision.Length > 128
                   || policy.ValidFrom >= policy.ValidUntil
                   || not (utcMicroseconds policy.ValidFrom && utcMicroseconds policy.ValidUntil && utcMicroseconds policy.RevocationCheckedAt) then PortableSupplierC1.fail ()
                let key = Array.copy policy.PublicKeyBlob
                let bytes = Array.copy policy.PolicyBytes
                if digestBytes bytes <> policy.ExpectedPolicySha256 then PortableSupplierC1.fail ()
                let snapshot = PortableSupplierC1.document 4096 bytes
                PortableSupplierC1.fields ["role";"principal";"public_key";"namespace";"revision";"valid_from";"valid_until"] snapshot
                let publicKey = (snapshot.GetProperty("public_key") |> PortableSupplierC1.text 512).Split(' ',StringSplitOptions.RemoveEmptyEntries)
                if publicKey.Length < 2 || publicKey[0] <> "ssh-ed25519" || Convert.FromBase64String(publicKey[1]) <> key
                   || (snapshot.GetProperty("role") |> PortableSupplierC1.text 32) <> controlledRoleName policy.Role
                   || (snapshot.GetProperty("principal") |> PortableSupplierC1.text 256) <> policy.Principal
                   || (snapshot.GetProperty("namespace") |> PortableSupplierC1.text 128) <> PortableSupplierSshsig.signingNamespace policy.Role
                   || (snapshot.GetProperty("revision") |> PortableSupplierC1.text 128) <> policy.PolicyRevision
                   || (snapshot.GetProperty("valid_from") |> PortableSupplierC1.utc) <> policy.ValidFrom
                   || (snapshot.GetProperty("valid_until") |> PortableSupplierC1.utc) <> policy.ValidUntil then PortableSupplierC1.fail ()
                { Role = policy.Role; PublicKeyBase64 = Convert.ToBase64String key; Principal = policy.Principal
                  PolicyBase64 = Convert.ToBase64String bytes; PolicySha256 = policy.ExpectedPolicySha256
                  PolicyRevision = policy.PolicyRevision; ValidFrom = policy.ValidFrom; ValidUntil = policy.ValidUntil
                  RevocationCheckedAt = policy.RevocationCheckedAt; Revocation = policy.Revocation })
            Ok { Original = original; OriginalEnvelopeSha256 = originalEnvelopeSha256; ExpectedOperationId = operationId
                 HostBinding = binding; NativeConfiguration = configuration; RolePolicies = captured; Current = current }
        with _ -> Error "supplier-controlled-selection-refused"

    let private controlledSelectionCurrent (selection: PortableSupplierControlledSourceSelection) (state: PortableSupplierAdmissionState) =
        let current = selection.Current
        let custody = state.CheckpointCustody |> Option.defaultWith PortableSupplierC1.fail
        if { selection.Original with ReceiverEpoch = state.Original.ReceiverEpoch } <> state.Original
           || custody.OriginalReceiverEpoch <> selection.Original.ReceiverEpoch
           || custody.NativeConfiguration <> selection.NativeConfiguration
           || (raw 65536 custody.OriginalIntentEnvelopeBase64 |> Result.defaultWith invalidOp |> digestBytes) <> selection.OriginalEnvelopeSha256
           || current.MaximumObservationAge < TimeSpan.Zero || current.SelectionObservedAt > current.VerifiedAt
           || current.VerifiedAt >= current.SelectionValidUntil
           || current.VerifiedAt - current.SelectionObservedAt > current.MaximumObservationAge
           || current.IssuedReceiverClockDomain = Guid.Empty || current.IssuedReceiverClockDomain <> current.CurrentReceiverClockDomain
           || current.ReceiverMonotonic < 0L
           || current.HostClock <> PortableSupplierOriginalNativeConfiguration.clock selection.NativeConfiguration
           || current.VerifiedAt < current.HostClock.ValidFrom || current.VerifiedAt >= current.HostClock.ValidUntil then PortableSupplierC1.fail ()
        validate state |> Result.defaultWith invalidOp |> ignore

    let private controlledRole (selection: PortableSupplierControlledSourceSelection) role =
        let policy = selection.RolePolicies |> List.filter (fun value -> value.Role = role)
        let policy = match policy with [one] -> one | _ -> PortableSupplierC1.fail ()
        let now = selection.Current.VerifiedAt
        if policy.Revocation <> ControlledNotRevoked || now < policy.ValidFrom || now >= policy.ValidUntil
           || policy.RevocationCheckedAt > now || now - policy.RevocationCheckedAt > selection.Current.MaximumObservationAge then PortableSupplierC1.fail ()
        policy

    let private controlledVerifierIdentity () =
        let assemblyDigest (assembly: System.Reflection.Assembly) =
            let location = assembly.Location
            if String.IsNullOrEmpty location then PortableSupplierC1.fail ()
            use stream = System.IO.File.OpenRead location
            if stream.Length = 0L || stream.Length > 67108864L then PortableSupplierC1.fail ()
            SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()
        let verifier = assemblyDigest typeof<PortableSupplierSignatureRole>.Assembly
        let implementation = assemblyDigest typeof<Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters>.Assembly
        let profile = PortableSupplierC1.objectBytes
                          ["profile",PortableSupplierC1.quoted "controlled-sshsig-ed25519-sha512-source-v1"
                           "implementation",PortableSupplierC1.quoted "BouncyCastle.Cryptography/2.7.0"
                           "implementation_sha256",PortableSupplierC1.quoted implementation
                           "verifier_sha256",PortableSupplierC1.quoted verifier]
        verifier,digestBytes profile

    let private controlledSignature selection role bytes =
        let policy = controlledRole selection role
        let verified = PortableSupplierSshsig.verify role (Convert.FromBase64String policy.PublicKeyBase64) bytes |> Result.defaultWith invalidOp
        policy,verified

    let private controlledBundle selection controller (required: (string * PortableSupplierSignatureRole * byte array * string) list) =
        let binary,profile = controlledVerifierIdentity ()
        let q = PortableSupplierC1.quoted
        let date (value: DateTimeOffset) = q (value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",Globalization.CultureInfo.InvariantCulture))
        let observations = required |> List.map (fun (name,role,bytes,kind) ->
            let policy,verified = controlledSignature selection role bytes
            let payload,_ = PortableSupplierC1.payload 65536 45000 bytes
            let value = if kind = "raw-sha256" then digestBytes payload else domain kind payload
            let fields =
                ["role",q name; "envelope_sha256",q (digestBytes bytes); "payload_digest",q value
                 "policy_digest",q policy.PolicySha256; "policy_revision",q policy.PolicyRevision; "issuer_principal",q policy.Principal
                 "public_key_fingerprint",q (PortableSupplierSshsig.publicKeyFingerprint verified)
                 "namespace",q (PortableSupplierSshsig.signingNamespace role); "key_algorithm",q "ssh-ed25519"; "message_hash_algorithm",q "sha512"
                 "verifier_binary_digest",q binary; "verifier_profile_digest",q profile; "verified_at",date selection.Current.VerifiedAt
                 "policy_valid_from",date policy.ValidFrom; "policy_valid_until",date policy.ValidUntil
                 "revocation_checked_at",date policy.RevocationCheckedAt; "revocation_status",q "not-revoked"
                 (if controller then "payload_digest_kind" else "payload_domain"),q kind]
            name,digestBytes bytes,PortableSupplierC1.objectBytes fields |> PortableSupplierC1.document 8192 |> PortableSupplierC1.canonical)
        let rows = observations |> List.sortBy (fun (role,hash,_) -> role,hash) |> List.map (fun (_,_,row) -> row)
        let raw = PortableSupplierC1.objectBytes
                      ["schema",q (if controller then "fsgg.v3.controller-verification-bundle/2" else "fsgg.v3.receiver-verification-bundle/1")
                       "provenance",q "controlled-test"; "observations","[" + String.concat "," rows + "]"]
        if controller then PortableSupplierVerificationBundle.parseController raw |> Result.defaultWith invalidOp |> ignore
        else PortableSupplierVerificationBundle.parseReceiver raw |> Result.defaultWith invalidOp |> ignore
        Convert.ToBase64String raw

    let private controlledCreationGrant selection (state: PortableSupplierAdmissionState) request (core: System.Text.Json.JsonElement) =
        let _,command = PortableSupplierC1.payload 65536 45000 request
        let grant = command.GetProperty("body").GetProperty("create_grant") |> PortableSupplierC1.b64url 10761
        let policy,_ = controlledSignature selection EffectGrantSignature grant
        let _,value = PortableSupplierC1.payload 10761 6000 grant
        PortableSupplierC1.fields ["schema";"profile";"effect";"grant_id";"generation";"intent_digest";"correlation_id";"issuer_principal";"policy_digest"
                                   "receiver_source_revision";"supplier_source_revision";"original_clock_ref";"not_before";"not_after";"body"] value
        let body = value.GetProperty("body")
        PortableSupplierC1.fields ["creation_challenge_digest";"host_lease_authority_ref";"host_lease_not_after";"pool_id";"qualified_inner_image"] body
        let challenge = match state.Challenge with Some(WireV1 challenge) -> challenge | _ -> PortableSupplierC1.fail ()
        let _,clock,_ = PortableSupplierOriginalNativeConfiguration.expectedHashes selection.NativeConfiguration
        let expected (key: string) = core.GetProperty(key).GetString()
        if value.GetProperty("schema").GetString() <> "fsgg.v3.effect-grant/1" || value.GetProperty("profile").GetString() <> "signed-hold-v1"
           || value.GetProperty("effect").GetString() <> "create-stopped" || (value.GetProperty("generation") |> PortableSupplierC1.u64) = 0UL
           || value.GetProperty("intent_digest").GetString() <> state.Original.IntentDigest
           || value.GetProperty("correlation_id").GetString() <> expected "correlation_id"
           || value.GetProperty("issuer_principal").GetString() <> policy.Principal || value.GetProperty("policy_digest").GetString() <> policy.PolicySha256
           || value.GetProperty("receiver_source_revision").GetString() <> state.Original.Command.SourceRevision
           || value.GetProperty("supplier_source_revision").GetString() <> state.Original.SupplierSourceRevision
           || value.GetProperty("original_clock_ref").GetString() <> clock
           || (value.GetProperty("not_before") |> PortableSupplierC1.utc) > challenge.IssuedAt
           || (value.GetProperty("not_after") |> PortableSupplierC1.utc) < challenge.NotAfter
           || body.GetProperty("creation_challenge_digest").GetString() <> challenge.Digest
           || body.GetProperty("host_lease_authority_ref").GetString() <> expected "host_lease_authority_ref"
           || body.GetProperty("host_lease_not_after").GetString() <> expected "host_lease_expires_at"
           || body.GetProperty("pool_id").GetString() <> expected "pool_id"
           || body.GetProperty("qualified_inner_image").GetString() <> state.Original.QualifiedImage then PortableSupplierC1.fail ()
        value.GetProperty("grant_id") |> PortableSupplierC1.uuid |> ignore

    let private controlledObservation selection creation reservationBytes (state: PortableSupplierAdmissionState)
                                      (exchange: PortableSupplierRetainedWireExchange) =
        let evidence = raw 65536 exchange.EvidenceBase64 |> Result.defaultWith invalidOp
        let payload,response = PortableSupplierC1.payload 65536 45000 evidence
        let coreBytes,receiptBytes =
            if creation then payload,evidence
            else
                let pinned = state.Pinned |> Option.defaultWith PortableSupplierC1.fail
                raw 6000 pinned.CreationBase64 |> Result.defaultWith invalidOp,raw 18761 pinned.CreationReceiptBase64 |> Result.defaultWith invalidOp
        let core = PortableSupplierC1.document 6000 coreBytes
        let reservation = PortableSupplierC1.document 6000 reservationBytes
        if (core.GetProperty("operation_id") |> PortableSupplierC1.uuid) <> selection.ExpectedOperationId then PortableSupplierC1.fail ()
        let count key =
            let value = reservation.GetProperty(key: string) |> PortableSupplierC1.u64
            if value > uint64 Int64.MaxValue then PortableSupplierC1.fail ()
            TimeSpan.FromTicks(int64 value)
        let date key = reservation.GetProperty(key: string) |> PortableSupplierC1.utc
        let originalReservation =
            { Binding = selection.HostBinding; AdmittedAt = date "admitted_at"; HostLeaseExpiresAt = date "host_lease_expires_at"
              MaximumRuntime = count "maximum_runtime_ticks"; ExportReserve = count "export_reserve_ticks"; CleanupReserve = count "cleanup_reserve_ticks"
              Selection = sourceSelection (reservation.GetProperty("selection")) }
        let _,result = raw 4096 exchange.ResultBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.payload 4096 896
        let events = if creation then [] else response.GetProperty("events").EnumerateArray() |> Seq.map (PortableSupplierC1.b64url 26761) |> Seq.toList
        let head key = if creation then None else response.GetProperty(key: string) |> PortableSupplierC1.optional PortableSupplierC1.digest
        let sequence = if creation then 0UL else events |> List.last |> PortableSupplierC1.payload 26761 18000 |> snd |> fun row -> row.GetProperty("sequence") |> PortableSupplierC1.u64
        let kind = if creation then SupplierCreation else SupplierGenesis
        let policy = controlledRole selection SupplierCreationSignature
        { Kind = kind; IntentDigest = state.Original.IntentDigest; TrustPolicyDigest = state.Original.TrustPolicyDigest
          ChallengeDigest = exchange.Challenge.Digest; Purpose = exchange.Challenge.Purpose; OriginalReservation = originalReservation
          Identity = { Issuer = policy.Principal; ContainerId = core.GetProperty("cid").GetString()
                       AllocationDevice = core.GetProperty("allocation_device") |> PortableSupplierC1.u64
                       AllocationInode = core.GetProperty("allocation_inode") |> PortableSupplierC1.u64 }
          OuterImageId = core.GetProperty("image_id").GetString(); SupplierSourceRevision = state.Original.SupplierSourceRevision
          CreationBase64 = Convert.ToBase64String coreBytes; CreationReceiptBase64 = Convert.ToBase64String receiptBytes
          ReservationBase64 = Convert.ToBase64String reservationBytes; ResponseBase64 = exchange.EvidenceBase64
          EventsBase64 = events |> List.map Convert.ToBase64String; FromHead = head "from_head"; ToHead = head "to_head"
          Sequence = sequence; CollectedAt = result.GetProperty("collected_at") |> PortableSupplierC1.utc }

    let private recheckControlledBundle selection controller encoded required collected notAfter =
        let root = bundleCoverage controller encoded (required |> List.map (fun (name,_,bytes,kind) -> name,bytes,kind))
        if root.GetProperty("provenance").GetString() <> "controlled-test" then PortableSupplierC1.fail ()
        let binary,profile = controlledVerifierIdentity ()
        for name,role,bytes,_ in required do
            let policy,verified = controlledSignature selection role bytes
            let row = root.GetProperty("observations").EnumerateArray() |> Seq.find (fun row -> row.GetProperty("role").GetString() = name && row.GetProperty("envelope_sha256").GetString() = digestBytes bytes)
            let at = row.GetProperty("verified_at") |> PortableSupplierC1.utc
            let checkedAt = row.GetProperty("revocation_checked_at") |> PortableSupplierC1.utc
            if row.GetProperty("policy_digest").GetString() <> policy.PolicySha256
               || row.GetProperty("policy_revision").GetString() <> policy.PolicyRevision
               || row.GetProperty("issuer_principal").GetString() <> policy.Principal
               || row.GetProperty("public_key_fingerprint").GetString() <> PortableSupplierSshsig.publicKeyFingerprint verified
               || (row.GetProperty("policy_valid_from") |> PortableSupplierC1.utc) <> policy.ValidFrom
               || (row.GetProperty("policy_valid_until") |> PortableSupplierC1.utc) <> policy.ValidUntil
               || row.GetProperty("verifier_binary_digest").GetString() <> binary || row.GetProperty("verifier_profile_digest").GetString() <> profile
               || at < policy.ValidFrom || at >= policy.ValidUntil || at > selection.Current.VerifiedAt
               || at < collected || at >= notAfter
               || checkedAt > at || at - checkedAt > selection.Current.MaximumObservationAge then PortableSupplierC1.fail ()

    /// Reverify immutable historical source evidence against fresh controlled policy/clock observations.
    /// Historical bundle/checkpoint bytes and their verification times are never rewritten.
    let private reverifyControlledHistory selection (state: PortableSupplierAdmissionState) =
        try
            controlledSelectionCurrent selection state
            let custody = state.CheckpointCustody.Value
            if state.FirstFailure.IsSome || not state.Conflicts.IsEmpty
               || not custody.NativeAcquisitions.IsEmpty || custody.OutputCheckpointBase64.IsSome
               || custody.WireExchanges.Length > 1 then PortableSupplierC1.fail ()
            let creation = custody.CreationExchange |> Option.defaultWith PortableSupplierC1.fail
            let original = raw 65536 custody.OriginalIntentEnvelopeBase64 |> Result.defaultWith invalidOp
            let rawRequest (exchange: PortableSupplierRetainedWireExchange) = raw 65536 exchange.RequestBase64 |> Result.defaultWith invalidOp
            let rawResult (exchange: PortableSupplierRetainedWireExchange) = raw 4096 exchange.ResultBase64 |> Result.defaultWith invalidOp
            let receipt = raw 18761 creation.EvidenceBase64 |> Result.defaultWith invalidOp
            let _,core = PortableSupplierC1.payload 18761 6000 receipt
            if (core.GetProperty("operation_id") |> PortableSupplierC1.uuid) <> selection.ExpectedOperationId then PortableSupplierC1.fail ()
            controlledCreationGrant selection { state with Challenge = Some(WireV1 creation.Challenge) } (rawRequest creation) core
            let reservation = state.Pinned |> Option.defaultWith PortableSupplierC1.fail |> fun pinned -> raw 6000 pinned.ReservationBase64 |> Result.defaultWith invalidOp
            let expectedPinned = controlledObservation selection true reservation state creation
            if state.Pinned <> Some expectedPinned then PortableSupplierC1.fail ()
            for exchange in creation::custody.WireExchanges do
                let _,result = rawResult exchange |> PortableSupplierC1.payload 4096 896
                let collected = result.GetProperty("collected_at") |> PortableSupplierC1.utc
                recheckControlledBundle selection true exchange.ControllerBundleBase64
                    ["original-intent",OriginalIntentSignature,original,"raw-sha256"
                     "receiver-command",ReceiverCommandSignature,rawRequest exchange,"raw-sha256"
                     "host-command-result",HostCommandResultSignature,rawResult exchange,"raw-sha256"] collected exchange.Challenge.NotAfter
                let required =
                    if exchange = creation then ["creation",SupplierCreationSignature,receipt,"creation"]
                    else
                        let response = raw 65536 exchange.EvidenceBase64 |> Result.defaultWith invalidOp
                        let _,row = PortableSupplierC1.payload 65536 45000 response
                        let events = row.GetProperty("events").EnumerateArray() |> Seq.map (PortableSupplierC1.b64url 26761) |> Seq.toList
                        match events with
                        | [genesis] -> ["creation",SupplierCreationSignature,receipt,"creation"; "genesis",SupplierGenesisSignature,genesis,"genesis"; "supplier-response",SupplierResponseSignature,response,"response"]
                        | _ -> PortableSupplierC1.fail ()
                recheckControlledBundle selection false exchange.ReceiverBundleBase64 required collected exchange.Challenge.NotAfter
            Ok ()
        with _ -> Error "supplier-controlled-cold-reverification-refused"

    /// Real signatures and byte-derived observations under copied TEST selection; never calls the public transcript port.
    let verifyAndAcceptControlledV4 selection original revision fence (state: PortableSupplierAdmissionState) exchange =
        if not (expectedV4 original revision fence state) then Error "supplier-v4-original-revision-fence-refused"
        elif state.FirstFailure.IsSome || not state.Conflicts.IsEmpty then Ok(HeldControlledSource state)
        else
            let creation,requestInput,frameInputs,reservationInput =
                match exchange with
                | ControlledCreationExchange(request,frames,reservation) -> true,request,frames,Some reservation
                | ControlledGenesisExchange(request,frames) -> false,request,frames,None
            try
                if isNull requestInput || requestInput.Length = 0 || requestInput.Length > 65536
                   || (reservationInput |> Option.exists (fun bytes -> isNull bytes || bytes.Length = 0 || bytes.Length > 6000))
                   || frameInputs.Length > 2 || (frameInputs |> List.exists (fun bytes -> isNull bytes || bytes.Length > 65536)) then PortableSupplierC1.fail ()
                let request = Array.copy requestInput
                let frames = frameInputs |> List.map Array.copy
                let reservation = reservationInput |> Option.map Array.copy
                let quarantine reason =
                    quarantineExchangeV4 original revision fence reason (Convert.ToBase64String request)
                        (frames |> List.map Convert.ToBase64String) None None state
                    |> Result.map (function SupplierCheckpointHeld held -> HeldControlledSource held | _ -> PortableSupplierC1.fail ())
                try
                    let challenge = match state.Challenge with Some(WireV1 challenge) -> challenge | _ -> PortableSupplierC1.fail ()
                    if creation <> (challenge.Purpose = "creation") then PortableSupplierC1.fail ()
                    controlledSelectionCurrent selection state
                    if not creation then reverifyControlledHistory selection state |> Result.defaultWith invalidOp |> ignore
                    let result,evidence = match frames with [result;evidence] when result.Length > 0 && evidence.Length > 0 && result.Length <= 4096 -> result,evidence | _ -> PortableSupplierC1.fail ()
                    let originalEnvelope = raw 65536 state.CheckpointCustody.Value.OriginalIntentEnvelopeBase64 |> Result.defaultWith invalidOp
                    let controller = controlledBundle selection true
                                         ["original-intent",OriginalIntentSignature,originalEnvelope,"raw-sha256"
                                          "receiver-command",ReceiverCommandSignature,request,"raw-sha256"
                                          "host-command-result",HostCommandResultSignature,result,"raw-sha256"]
                    let receipt = if creation then evidence else raw 18761 state.Pinned.Value.CreationReceiptBase64 |> Result.defaultWith invalidOp
                    let required =
                        if creation then ["creation",SupplierCreationSignature,receipt,"creation"]
                        else
                            let _,response = PortableSupplierC1.payload 65536 45000 evidence
                            let events = response.GetProperty("events").EnumerateArray() |> Seq.map (PortableSupplierC1.b64url 26761) |> Seq.toList
                            match events with
                            | [genesis] -> ["creation",SupplierCreationSignature,receipt,"creation"; "genesis",SupplierGenesisSignature,genesis,"genesis"; "supplier-response",SupplierResponseSignature,evidence,"response"]
                            | _ -> PortableSupplierC1.fail ()
                    let receiver = controlledBundle selection false required
                    let pair = { AcceptedRevision = 0UL; AcceptedStoreFence = 0UL; Challenge = challenge
                                 RequestBase64 = Convert.ToBase64String request; ResultBase64 = Convert.ToBase64String result; EvidenceBase64 = Convert.ToBase64String evidence
                                 ReceiverBundleBase64 = receiver; ControllerBundleBase64 = controller }
                    let reservation = if creation then reservation |> Option.defaultWith PortableSupplierC1.fail else raw 6000 state.Pinned.Value.ReservationBase64 |> Result.defaultWith invalidOp
                    let observation = controlledObservation selection creation reservation state pair
                    if creation then
                        let core = raw 6000 observation.CreationBase64 |> Result.defaultWith invalidOp |> PortableSupplierC1.document 6000
                        controlledCreationGrant selection state request core
                    let now = selection.Current.VerifiedAt
                    if now < challenge.IssuedAt || now >= challenge.NotAfter || selection.Current.ReceiverMonotonic < challenge.IssuedMonotonic
                       || selection.Current.ReceiverMonotonic >= challenge.DeadlineMonotonic || observation.CollectedAt > now
                       || now - observation.CollectedAt > TimeSpan.FromSeconds 30. then PortableSupplierC1.fail ()
                    let verified = SupplierVerifiedTranscript(state.Revision,digestBytes evidence,observation)
                    let accept = if creation then pinCreationV4 else acceptWireV4
                    match accept verified pair original revision fence state with
                    | Ok(SupplierCheckpointData proposed) ->
                        validate proposed |> Result.defaultWith invalidOp |> ignore
                        Ok(AuthenticatedControlledSource(ControlledSourceTransition(original,revision,fence,proposed)))
                    | Ok(SupplierCheckpointHeld held) -> Ok(HeldControlledSource held)
                    | Error _ -> quarantine "supplier-context-refused"
                with _ -> quarantine "supplier-authentication-refused"
            with _ -> Error "supplier-controlled-source-input-refused"


    /// A cold parse never supplies authentication; current selection and every retained signature are checked again.
    let reverifyControlledSourceV4 selection (state: PortableSupplierAdmissionState) =
        if state.Challenge.IsSome then Error "supplier-controlled-cold-pending-refused"
        else reverifyControlledHistory selection state |> Result.map (fun () -> ControlledSourceReadback state)
