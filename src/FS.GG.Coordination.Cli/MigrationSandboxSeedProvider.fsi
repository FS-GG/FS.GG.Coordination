namespace FS.GG.Coordination.Cli

open System

/// One exact provider request template. This value contains no credential and cannot dispatch itself.
type MigrationSandboxSeedProviderRequest =
    { Method: string
      Uri: string
      ApiVersion: string
      Body: byte array option
      EffectId: string
      IdempotencyKey: string }

type MigrationSandboxSeedProviderEvidence =
    { PreviousSnapshot: MigrationSandboxSeedJournalSnapshot option
      CurrentSnapshot: MigrationSandboxSeedJournalSnapshot
      MintProofBytes: ReadOnlyMemory<byte>
      ProtectedHostReceiptBytes: ReadOnlyMemory<byte>
      SeedPlanBytes: ReadOnlyMemory<byte>
      CorpusBytes: ReadOnlyMemory<byte> }

/// Exact request/response bytes retained from one provider page.
type MigrationSandboxSeedProviderPage =
    { Request: MigrationSandboxSeedProviderRequest
      StatusCode: int
      ResponseBody: ReadOnlyMemory<byte> }

type MigrationSandboxSeedProviderPass =
    { IssuePages: MigrationSandboxSeedProviderPage list
      ProjectPages: MigrationSandboxSeedProviderPage list }

/// MutationResponse is optional because transport loss is expected. FirstPass and SecondPass
/// must be independently captured, terminal and byte-for-byte equal before ownership is classified.
type MigrationSandboxSeedProviderCapture =
    { MutationResponse: MigrationSandboxSeedProviderPage option
      FirstPass: MigrationSandboxSeedProviderPass
      SecondPass: MigrationSandboxSeedProviderPass }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedProviderAuthority =
    | SourceOnlyUnavailable

type MigrationSandboxSeedProviderPlan =
    { MutationTemplate: MigrationSandboxSeedProviderRequest
      DispatchRequest: MigrationSandboxSeedProviderRequest option
      FirstIssueRead: MigrationSandboxSeedProviderRequest
      FirstProjectRead: MigrationSandboxSeedProviderRequest
      UnknownResponseRequiresRecoveryPending: bool
      DispatchAuthority: MigrationSandboxSeedProviderAuthority }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedProviderDisposition =
    | Applied of MigrationSandboxOwnedResource
    | ProvenAbsent
    | RecoveryPending of reason: string
    | Conflict of reason: string
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedProviderFailure =
    | InvalidSealedExecution
    | ProtectedHostEvidenceInvalid
    | WrongEffect
    | EffectNotDispatchable
    | InvalidOwnership
    | CaptureIncomplete
    | CaptureDrift
    | ForeignRead
    | MalformedResponse

[<RequireQualifiedAccess>]
module MigrationSandboxSeedProvider =
    /// Restore an exact durable S1 snapshot and build exact GraphQL mutation and first-page
    /// readback templates. A restored InFlight or RecoveryPending effect is reread-only;
    /// DispatchRequest and authority remain unavailable until a separate protected host join
    /// proves a fresh, non-restored dispatch boundary.
    val plan:
        evidence: MigrationSandboxSeedProviderEvidence ->
            Result<MigrationSandboxSeedProviderPlan, MigrationSandboxSeedProviderFailure>

    /// Parse two complete provider readback passes and classify only exact nonce-owned resources.
    /// No caller completeness flag is accepted. Unknown or foreign ownership never becomes Applied.
    val reconcile:
        evidence: MigrationSandboxSeedProviderEvidence ->
        capture: MigrationSandboxSeedProviderCapture ->
            Result<MigrationSandboxSeedProviderDisposition, MigrationSandboxSeedProviderFailure>
