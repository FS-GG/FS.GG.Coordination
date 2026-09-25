namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type MigrationWorkflowPinKind =
    | Workflow
    | PackageToolPin

/// One caller-declared pin in the isolated copy. This declaration's exhaustiveness
/// is not independently established by the source reader.
type MigrationWorkflowPinDeclaration =
    { Kind: MigrationWorkflowPinKind
      Receiver: string
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RefName: string
      ExpectedHead: string
      Path: string
      ExpectedMode: string
      ExpectedBlobSha: string
      ExpectedBytesSha256: string }

type MigrationWorkflowPinBlob =
    { Declaration: MigrationWorkflowPinDeclaration
      RequestUri: string
      RawBody: string
      RawBodySha256: string
      Bytes: byte array
      BytesSha256: string
      GitBlobSha: string }

/// Read-only pin proof for the declared paths. The caller must separately prove
/// that its workflow and package/tool pin declarations are exhaustive.
type MigrationWorkflowPinTwoPass =
    { CohortSha256: string
      First: MigrationWorkflowPinBlob list
      Second: MigrationWorkflowPinBlob list }

[<RequireQualifiedAccess>]
module MigrationWorkflowPinCapture =
    val captureTwoPass:
        cohort:GitHubMigrationCopyCohort ->
        declarations:MigrationWorkflowPinDeclaration list ->
        receivers:MigrationReceiverTwoPass ->
        template:MigrationGitHubReadOptions ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationWorkflowPinTwoPass, string>
