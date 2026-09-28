namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverAuthorityCompositionRequest =
    { Options: MigrationInspectProviderOptions
      RosterOptions: MigrationReceiverRosterReadOptions
      RosterCapture: MigrationReceiverRosterCapture
      AcceptedEvidence: MigrationReceiverCopyAcceptedEvidence
      RunIdentity: MigrationSandboxSeedRequest
      CopyPlan: MigrationReceiverCopyPlanResult
      BlobBatches: MigrationReceiverCopyBlobBatch list
      BlobArtifacts: MigrationReceiverCopyBlobBatchArtifact list
      BlobCoverage: MigrationReceiverCopyBlobCoverage
      PinCapture: MigrationReceiverPinTwoPass }

type MigrationReceiverAuthorityComposition =
    { ReceiverIdentities: GitHubMigrationInspectAuthority * GitHubMigrationInspectAuthority
      WorkflowPins: GitHubMigrationInspectAuthority * GitHubMigrationInspectAuthority
      CopyPlanFingerprint: string
      BlobCoverageFingerprint: string
      ScopedSettingsSha256: string
      RepositoryRosterSha256: string }

type internal MigrationReceiverAuthorityBinding =
    { ReceiverName: string
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RefName: string
      CommitSha: string
      TreeSha: string }

type internal MigrationReceiverAuthorityVerifiedFacts =
    { Cohort: GitHubMigrationCopyCohort
      CopyPlanFingerprint: string
      BlobCoverageFingerprint: string
      BlobObjectCount: int
      Mappings: MigrationReceiverCopyMapping list
      Bindings: MigrationReceiverAuthorityBinding list
      SignedHeadReceiverNames: string list
      RosterRepositories: MigrationReceiverRosterRepository list
      ScopedSettingsSha256: string
      RepositoryRosterSha256: string
      RosterPages: GitHubMigrationInspectPage list
      ReceiverProof: GitHubMigrationInspectAuthority
      WorkflowProof: GitHubMigrationInspectAuthority }

[<RequireQualifiedAccess>]
module MigrationReceiverAuthorityComposer =
    /// Revalidates the accepted seven-receiver plan, all 8,999 retained/captured objects,
    /// the selected-installation roster/settings and provider-derived signed pin capture.
    /// It performs no provider request or mutation. Any incomplete or contradictory input refuses.
    val compose:
        request:MigrationReceiverAuthorityCompositionRequest ->
            Result<MigrationReceiverAuthorityComposition, string>

    /// Pure correspondence boundary used by focused controls after each source verifier has passed.
    val internal composeVerifiedForTests:
        facts:MigrationReceiverAuthorityVerifiedFacts ->
            Result<MigrationReceiverAuthorityComposition, string>
