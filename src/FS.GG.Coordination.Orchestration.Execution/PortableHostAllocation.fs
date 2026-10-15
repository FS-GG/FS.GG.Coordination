namespace FS.GG.Coordination.Orchestration.Execution

open System

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
