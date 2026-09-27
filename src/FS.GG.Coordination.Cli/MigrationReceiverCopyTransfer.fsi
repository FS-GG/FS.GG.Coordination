namespace FS.GG.Coordination.Cli

open System

type MigrationReceiverCopyDerivedRef =
    { ReceiverCopyId: string
      SourceRepository: string
      SourceCommit: string
      SourceTree: string
      DerivedRef: string
      DerivedTree: string
      DerivedCommit: string
      BlobCount: int }

type MigrationReceiverCopyTransferManifest =
    { Schema: string
      TargetRepository: string
      RunIdentity: MigrationSandboxSeedRequest
      PlanFingerprint: string
      BlobCoverageFingerprint: string
      BlobSha256BySha1: Map<string, string>
      ObjectStorePath: string
      DerivedRefs: MigrationReceiverCopyDerivedRef list
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyTransfer =
    /// Reopens the complete, independently verified 8,999-object coverage and
    /// materializes seven exact trees and deterministic parentless commits in a
    /// fresh local bare repository. The commit messages bind the original source
    /// repository, commit and tree; they do not claim source ancestry replication.
    val prepare:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        batches:MigrationReceiverCopyBlobBatch list ->
        artifacts:MigrationReceiverCopyBlobBatchArtifact list ->
        verifiedCoverage:MigrationReceiverCopyBlobCoverage ->
        objectStoreRoot:string ->
            Result<MigrationReceiverCopyTransferManifest, string>

    val verify:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        batches:MigrationReceiverCopyBlobBatch list ->
        artifacts:MigrationReceiverCopyBlobBatchArtifact list ->
        verifiedCoverage:MigrationReceiverCopyBlobCoverage ->
        observed:MigrationReceiverCopyTransferManifest ->
            Result<MigrationReceiverCopyTransferManifest, string>

    val internal prepareSyntheticForTests:
        runIdentity:MigrationSandboxSeedRequest ->
        planFingerprint:string ->
        coverageFingerprint:string ->
        objectStoreRoot:string ->
        receivers:(string * string * string * string * string * (string * string * string * byte array) list) list ->
            Result<MigrationReceiverCopyTransferManifest, string>
