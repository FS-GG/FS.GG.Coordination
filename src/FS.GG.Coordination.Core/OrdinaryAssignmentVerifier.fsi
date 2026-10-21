namespace FS.GG.Coordination.Core

/// Checks supplied authority independently of the policy's alternative construction.
module OrdinaryAssignmentVerifier =
    val verify: OrdinaryAssignment.Authority -> OrdinaryAssignment.Snapshot -> OrdinaryAssignment.Decision -> Result<unit, OrdinaryAssignment.Refusal>
