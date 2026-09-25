namespace FS.GG.Coordination.GitHub

open System

/// One provider GET, retained with the exact payload and its SHA-256 digest.
type MigrationWorkflowPolicyEvidence =
    { RequestUri: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationWorkflowDefaultPermission =
    | WorkflowRead
    | WorkflowWrite

type MigrationWorkflowAccessLevel =
    | AccessNone
    | AccessUser
    | AccessOrganization
    | AccessEnterprise

type MigrationPrivateForkWorkflowPolicy =
    { RunWorkflowsFromForkPullRequests: bool
      SendWriteTokensToWorkflows: bool
      SendSecretsAndVariables: bool
      RequireApprovalForForkPrWorkflows: bool }

/// A complete read pass over three private-repository Actions policy endpoints.
type MigrationWorkflowPolicyPass =
    { DefaultPermission: MigrationWorkflowDefaultPermission
      CanApprovePullRequestReviews: bool
      ForkPullRequests: MigrationPrivateForkWorkflowPolicy
      AccessLevel: MigrationWorkflowAccessLevel
      WorkflowEvidence: MigrationWorkflowPolicyEvidence
      ForkEvidence: MigrationWorkflowPolicyEvidence
      AccessEvidence: MigrationWorkflowPolicyEvidence }

/// Partial ActionsPolicy source only. Both policy passes and all three identity GETs must agree.
type MigrationRepositoryWorkflowPolicy =
    { RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RepositoryUpdatedAt: DateTimeOffset
      Visibility: string
      InitialIdentity: MigrationWorkflowPolicyEvidence
      MiddleIdentity: MigrationWorkflowPolicyEvidence
      TerminalIdentity: MigrationWorkflowPolicyEvidence
      FirstPass: MigrationWorkflowPolicyPass
      SecondPass: MigrationWorkflowPolicyPass }

[<RequireQualifiedAccess>]
module MigrationRepositoryWorkflowPolicyRead =
    val read:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryWorkflowPolicy, MigrationReadFailure>
