namespace FS.GG.Coordination.Core

/// A bounded policy candidate; does not allocate, release or dispatch.
module OrdinaryAssignmentPolicy =
    val select: OrdinaryAssignment.Snapshot -> Result<OrdinaryAssignment.Decision, OrdinaryAssignment.Refusal>
