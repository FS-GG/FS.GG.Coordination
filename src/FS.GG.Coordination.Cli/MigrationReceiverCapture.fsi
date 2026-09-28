namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

/// Read-only source proof for the explicitly declared receivers in a disposable copy.
/// This is not a receiver-identities or workflow-pins inspect authority.
type MigrationReceiverTwoPass =
    { CohortSha256: string
      First: MigrationReceiverSnapshot list
      Second: MigrationReceiverSnapshot list }

/// Provider-backed identity of one validly signed receiver head. The signature and payload
/// digests bind the raw Git commit verification evidence; they do not invent a local trust root.
type MigrationReceiverSignedHeadIdentity =
    { ReceiverName: string
      RepositoryFullName: string
      RefName: string
      CommitSha: string
      EvidenceRequestUri: string
      VerificationReason: string
      SignatureSha256: string
      SignedPayloadSha256: string
      VerifiedAtUtc: System.DateTimeOffset }

/// One immutable external tool selected by a retained workflow blob.
type MigrationReceiverWorkflowToolIdentity =
    { ReceiverName: string
      WorkflowPath: string
      LineNumber: int
      EvidenceRequestUri: string
      Literal: string
      TargetRepository: string
      TargetPath: string option
      Revision: string
      Kind: ImmutableExecutionReferenceKind option
      WorkflowBlobSha1: string
      WorkflowBytesSha256: string
      RequiresMigration: bool }

/// Two stable reads of pin bytes. InventoryBound distinguishes caller declarations from a
/// provider-tree-derived workflow/package census.
type MigrationReceiverPinEvidenceOrigin =
    private
    | CallerDeclared
    | ProviderTreeSignedTools
    static member internal CreateCallerDeclared: unit -> MigrationReceiverPinEvidenceOrigin
    static member internal CreateProviderTreeSignedTools: unit -> MigrationReceiverPinEvidenceOrigin

type MigrationReceiverPinTwoPass =
    { CohortSha256: string
      EvidenceOrigin: MigrationReceiverPinEvidenceOrigin
      SignedHeads: MigrationReceiverSignedHeadIdentity list
      WorkflowTools: MigrationReceiverWorkflowToolIdentity list
      First: MigrationReceiverPinSnapshot list
      Second: MigrationReceiverPinSnapshot list }
    member InventoryBound: bool
    member SignedToolIdentitiesBound: bool

[<RequireQualifiedAccess>]
module MigrationReceiverCapture =
    val captureTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverTwoPass, string>

    val capturePinBytesTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        pinsByReceiver:Map<string, MigrationReceiverPinDeclaration list> ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverPinTwoPass, string>

    /// Derives every supported workflow/package path from two stable recursive provider trees,
    /// then captures and revalidates every corresponding blob in two further stable passes.
    val captureWorkflowPinsTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationReceiverPinTwoPass, string>

    /// Reparses retained raw commit and workflow bytes and requires their signed-head and
    /// immutable-tool identity roster to equal the stored provider-derived identities.
    val validateSignedToolIdentityEvidence:
        captured:MigrationReceiverPinTwoPass -> Result<unit, string>
