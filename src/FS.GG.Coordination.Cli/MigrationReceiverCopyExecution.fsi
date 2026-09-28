namespace FS.GG.Coordination.Cli

type MigrationReceiverCopyOperation =
    | CreateReceiverCopies
    | ReadReceiverCopies
    | RemoveReceiverCopies

type MigrationReceiverCopyExecutionAuthority =
    { TargetRepository: string
      ContentsWrite: bool
      WorkflowsWrite: bool
      ActionsSuppressed: bool
      ProtectedCustody: bool }

type MigrationReceiverCopyExecutionReceipt =
    { Schema: string
      Operation: MigrationReceiverCopyOperation
      AttemptId: string
      ManifestFingerprint: string
      Applied: bool
      DispatchCount: int
      Refs: Map<string, string>
      TargetObjects: MigrationReceiverCopyTargetObject list
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyExecution =
    /// Executes only CreateReceiverCopies, ReadReceiverCopies, or
    /// RemoveReceiverCopies. Create reserves durable intent before its single
    /// atomic push. A restored reservation performs readback only and never
    /// resends. Remove is chained to an applied create receipt and uses exact
    /// commit leases for one atomic delete.
    val execute:
        authority:MigrationReceiverCopyExecutionAuthority ->
        verifiedTransfer:MigrationReceiverCopyVerifiedTransfer ->
        operation:MigrationReceiverCopyOperation ->
        attemptRoot:string ->
        transport:IMigrationReceiverCopyGitTransport ->
            Result<MigrationReceiverCopyExecutionReceipt, string>

    val internal executeWithCutForTests:
        authority:MigrationReceiverCopyExecutionAuthority ->
        verifiedTransfer:MigrationReceiverCopyVerifiedTransfer ->
        operation:MigrationReceiverCopyOperation ->
        attemptRoot:string ->
        transport:IMigrationReceiverCopyGitTransport ->
        cutAfterReservation:bool ->
            Result<MigrationReceiverCopyExecutionReceipt, string>
