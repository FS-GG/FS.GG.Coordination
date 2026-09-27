namespace FS.GG.Coordination.Cli

open System

type IMigrationReceiverCopyLocalGitObjectSource =
    inherit IDisposable
    abstract ReadBlob: sha1:string -> Result<ReadOnlyMemory<byte>, string>

type IMigrationReceiverCopyVerifiedObjectSource =
    inherit IDisposable
    abstract ReadBlob: sha1:string -> Result<ReadOnlyMemory<byte>, string>

type MigrationReceiverCopyBlobBatch =
    { ReceiverCopyBlobPlanFingerprint: string
      ReceiverCopyBlobBatchOrdinal: int
      ReceiverCopyBlobSha1s: string list
      ReceiverCopyBlobExpectedBytes: int64
      ReceiverCopyBlobBatchFingerprint: string }

type MigrationReceiverCopyBlobBatchArtifact =
    { ReceiverCopyBlobArtifactPlanFingerprint: string
      ReceiverCopyBlobArtifactBatchOrdinal: int
      ReceiverCopyBlobArtifactPath: string
      ReceiverCopyBlobArtifactSha256: string
      ReceiverCopyBlobSha256BySha1: Map<string, string>
      ReceiverCopyBlobArtifactFingerprint: string }

type MigrationReceiverCopyBlobCoverage =
    { ReceiverCopyBlobCoveragePlanFingerprint: string
      ReceiverCopyBlobCoverageBatchFingerprints: string list
      ReceiverCopyBlobCoverageSha256BySha1: Map<string, string>
      ReceiverCopyBlobCoverageFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyBlobCapture =
    /// Creates an exact-object reader from the seven plan-derived receiver repositories.
    /// The implementation verifies every pinned commit/tree, derives source occurrence per
    /// SHA-1 internally, and invokes shell-free `git --no-replace-objects cat-file --batch`
    /// with lazy fetch and replacement disabled and Git override variables cleared. Each
    /// locator's origin name must match the accepted census repository before object access;
    /// this is a local configuration binding, not proof against an actor controlling that
    /// repository's config and object database.
    val createLocalGitSource:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        repositoryLocations:Map<string, string> ->
            Result<IMigrationReceiverCopyLocalGitObjectSource, string>

    /// Plans the globally deduplicated missing set in deterministic batches of at most
    /// 64 objects and 8 MiB. The verified copy plan remains the only population source.
    val planBatches:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
            Result<MigrationReceiverCopyBlobBatch list, string>

    /// Reads exact blob identities from a local Git object source only and writes one
    /// private atomic artifact beneath artifactRoot. Existing artifacts are reverified.
    val captureBatch:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        source:IMigrationReceiverCopyLocalGitObjectSource ->
        artifactRoot:string ->
        batch:MigrationReceiverCopyBlobBatch ->
            Result<MigrationReceiverCopyBlobBatchArtifact, string>

    /// Reopens the retained artifact and independently verifies mode, Git blob SHA-1,
    /// declared size, byte SHA-256 and copy-plan/batch fingerprints.
    val verifyBatch:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        batch:MigrationReceiverCopyBlobBatch ->
        artifact:MigrationReceiverCopyBlobBatchArtifact ->
            Result<MigrationReceiverCopyBlobBatchArtifact, string>

    /// Proves coverage only after independently reopening every planned artifact and
    /// joining the 8,539 captured missing identities with all 460 independently
    /// reverified accepted retained identities into the exact disjoint 8,999 union.
    val verifyCoverage:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        batches:MigrationReceiverCopyBlobBatch list ->
        artifacts:MigrationReceiverCopyBlobBatchArtifact list ->
            Result<MigrationReceiverCopyBlobCoverage, string>

    /// Revalidates the exact 8,999-object coverage before construction. Each read
    /// reopens captured bytes through the private no-follow path and checks their
    /// size, Git SHA-1 and SHA-256 immediately before the caller receives a copy.
    /// Retained bytes are reparsed from the accepted archive and checked likewise.
    val createVerifiedCompleteObjectSource:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        verifiedCopyPlan:MigrationReceiverCopyPlanResult ->
        batches:MigrationReceiverCopyBlobBatch list ->
        artifacts:MigrationReceiverCopyBlobBatchArtifact list ->
        coverage:MigrationReceiverCopyBlobCoverage ->
            Result<IMigrationReceiverCopyVerifiedObjectSource, string>

    /// Test-only bounded projection used to exercise high-volume diagnostic draining.
    val internal drainDiagnosticsForTests: bytes:ReadOnlyMemory<byte> -> string

    /// Test-only subprocess exercise for bounded diagnostics and timeout termination.
    val internal runDiagnosticProcessForTests:
        executable:string -> arguments:string list -> timeoutMilliseconds:int -> Result<string * string, string>

    /// Test-only projection of native durability result handling.
    val internal durabilityResultForTests: operation:string -> result:int -> errorNumber:int -> Result<unit, string>

    /// Test-only generic retained/captured population join.
    val internal joinCoverageForTests:
        retained:Map<string, string> -> captured:Map<string, string> -> required:Set<string> -> acceptedExternalCount:int -> Result<Map<string, string>, string>
