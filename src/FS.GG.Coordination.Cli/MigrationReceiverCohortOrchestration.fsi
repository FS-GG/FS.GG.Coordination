namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

type MigrationReceiverCohortOrchestrationRequest =
    {
        InstallationOptions: MigrationReceiverInstallationReadOptions
        ProviderOptions: MigrationInspectProviderOptions
        AcceptedEvidence: MigrationReceiverCopyAcceptedEvidence
        RunIdentity: MigrationSandboxSeedRequest
        CopyPlan: MigrationReceiverCopyPlanResult
        BlobBatches: MigrationReceiverCopyBlobBatch list
        BlobArtifacts: MigrationReceiverCopyBlobBatchArtifact list
        VerifiedTransfer: MigrationReceiverCopyVerifiedTransfer
        CopyReceipt: MigrationReceiverCopyExecutionReceipt
    }

type MigrationReceiverCohortOrchestrationResult =
    {
        Installation: MigrationReceiverInstallationCapture
        PinCapture: MigrationReceiverPinTwoPass
        BlobCoverage: MigrationReceiverCopyBlobCoverage
        AuthorityComposition: MigrationReceiverAuthorityComposition
    }

[<RequireQualifiedAccess>]
module MigrationReceiverCohortOrchestration =
    val internal validateMintRunBindingForTests:
        options: MigrationReceiverInstallationReadOptions ->
        run: MigrationSandboxSeedRequest -> Result<unit, string>

    /// Performs the read-only receiver authority join. It first captures the exact selected
    /// sandbox installation, binds its sole repository and all seven plan-derived refs to the
    /// declared cohort, captures workflow pins in two passes, reopens every blob artifact, and
    /// only then delegates canonical proof construction to the authority composer.
    val compose:
        request: MigrationReceiverCohortOrchestrationRequest ->
        mintTransport: HttpMigrationReceiverTokenMintTransport ->
        transport: IMigrationGitHubReadTransport ->
            Result<MigrationReceiverCohortOrchestrationResult, string>

    /// Pure correspondence boundary for focused controls. This never qualifies an authority.
    val internal validateBindingsForTests:
        installation: MigrationReceiverInstallationCapture ->
        options: MigrationInspectProviderOptions ->
        copyPlan: MigrationReceiverCopyPlanResult ->
            Result<unit, string>

    val internal validateTargetCopyForTests:
        options: MigrationInspectProviderOptions ->
        copyPlan: MigrationReceiverCopyPlanResult ->
        verifiedTransfer: MigrationReceiverCopyVerifiedTransfer ->
        receipt: MigrationReceiverCopyExecutionReceipt -> Result<unit, string>
