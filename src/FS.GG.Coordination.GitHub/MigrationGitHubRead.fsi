namespace FS.GG.Coordination.GitHub

open System
open System.Net.Http

type MigrationGitHubReadOptions =
    { ApiBase: Uri
      GraphQLUri: Uri
      Token: string
      UserAgent: string
      Owner: string
      Repository: string
      ExpectedRepositoryId: int64 }

type MigrationIssueRecord =
    { Number: int
      DatabaseId: int64
      NodeId: string
      State: string
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssuePopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      Issues: MigrationIssueRecord list
      PullRequestCount: int }

/// One explicitly partial repository-settings surface, bound to the raw provider response.
type MigrationRepositoryCoreSettings =
    { RepositoryId: int64
      NodeId: string
      FullName: string
      DefaultBranch: string
      Visibility: string
      Archived: bool
      Disabled: bool
      HasIssues: bool
      AllowSquashMerge: bool
      AllowMergeCommit: bool
      AllowRebaseMerge: bool
      DeleteBranchOnMerge: bool
      PayloadJson: string
      PayloadSha256: string }

type MigrationPullRequestRecord =
    { Number: int
      DatabaseId: int64
      NodeId: string
      State: string
      UpdatedAt: DateTimeOffset
      HeadSha: string
      BaseSha: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationRestPageEvidence =
    { RequestedUri: string
      PayloadSha256: string
      NextUri: string option }

type MigrationPullRequestPopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      PullRequests: MigrationPullRequestRecord list }

type MigrationIssueCommentRecord =
    { DatabaseId: int64
      NodeId: string
      SubjectNumber: int
      ActorLogin: string
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      Body: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueCommentPopulation =
    { RepositoryId: int64
      SubjectNumber: int
      SubjectNodeId: string
      PageCount: int
      Terminal: bool
      Pages: MigrationRestPageEvidence list
      Comments: MigrationIssueCommentRecord list }

type MigrationIssueTypeRecord =
    { NodeId: string
      Name: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueTypePopulation =
    { RepositoryId: int64
      PageCount: int
      Terminal: bool
      IssueTypes: MigrationIssueTypeRecord list }

[<RequireQualifiedAccess>]
type MigrationRelationKind = ParentChild | Blocks

type MigrationRelationEndpoint =
    { NodeId: string
      RepositoryId: int64 }

type MigrationRelationEdge =
    { Kind: MigrationRelationKind
      Source: MigrationRelationEndpoint
      Target: MigrationRelationEndpoint }

type MigrationRelationContinuationPage =
    { Connection: string
      RequestedCursor: string
      PayloadJson: string
      PayloadSha256: string }

type MigrationIssueRelationRecord =
    { IssueNodeId: string
      UpdatedAt: DateTimeOffset
      PayloadJson: string
      PayloadSha256: string
      ContinuationPages: MigrationRelationContinuationPage list }

type MigrationRelationPopulation =
    { RepositoryId: int64
      IssueCount: int
      CompleteForRepository: bool
      ExternalEdgeCount: int
      Edges: MigrationRelationEdge list
      Issues: MigrationIssueRelationRecord list }

type MigrationProjectReadOptions =
    { GraphQLUri: Uri
      Token: string
      UserAgent: string
      Organization: string
      ProjectNumber: int
      ExpectedProjectNodeId: string }

[<RequireQualifiedAccess>]
type MigrationProjectContent =
    | Issue of nodeId:string * repositoryId:int64 * number:int
    | PullRequest of nodeId:string * repositoryId:int64 * number:int
    | DraftIssue of nodeId:string

type MigrationProjectItemRecord =
    { ItemNodeId: string
      Archived: bool
      UpdatedAt: DateTimeOffset
      Content: MigrationProjectContent
      PayloadJson: string
      PayloadSha256: string }

type MigrationProjectItemPopulation =
    { ProjectNodeId: string
      PageCount: int
      Terminal: bool
      TotalCount: int
      Items: MigrationProjectItemRecord list }

[<RequireQualifiedAccess>]
type MigrationProjectFieldKind = BuiltIn | SingleSelect | MultiSelect | Iteration

type MigrationProjectFieldOption = { Id: string; Name: string }

type MigrationProjectFieldRecord =
    { FieldNodeId: string
      Name: string
      DataType: string
      Kind: MigrationProjectFieldKind
      Options: MigrationProjectFieldOption list
      PayloadJson: string
      PayloadSha256: string }

type MigrationProjectFieldPopulation =
    { ProjectNodeId: string
      PageCount: int
      Terminal: bool
      TotalCount: int
      Fields: MigrationProjectFieldRecord list }

type MigrationProjectFieldValueRecord =
    { FieldNodeId: string
      ValueKind: string
      ValueNodeId: string option
      PayloadJson: string
      PayloadSha256: string }

type MigrationProjectItemValueRecord =
    { ItemNodeId: string
      UpdatedAt: DateTimeOffset
      FieldValueCount: int
      FieldValues: MigrationProjectFieldValueRecord list }

type MigrationProjectValuePopulation =
    { ProjectNodeId: string
      PageCount: int
      Terminal: bool
      TotalCount: int
      Items: MigrationProjectItemValueRecord list }

type MigrationProjectSnapshot =
    { ProjectNodeId: string
      ItemCount: int
      FieldCount: int
      FieldValueCount: int
      NormalizedSha256: string
      Items: MigrationProjectItemPopulation
      Fields: MigrationProjectFieldPopulation
      Values: MigrationProjectValuePopulation }

[<RequireQualifiedAccess>]
type MigrationReadFailure =
    | InvalidOptions
    | TransportUnavailable
    | HttpRefused of status:int
    | GraphQLErrors
    | MalformedResponse of reason:string
    | IdentityDrift
    | PaginationRefused of reason:string
    | DuplicateIdentity of identity:string
    | PopulationDrift
    | SnapshotMismatch of reason:string

type IMigrationGitHubReadTransport =
    abstract Send: GitHubRequest -> TransportOutcome

type HttpMigrationGitHubReadTransport =
    new: client:HttpClient -> HttpMigrationGitHubReadTransport
    interface IMigrationGitHubReadTransport

[<RequireQualifiedAccess>]
module MigrationGitHubRead =
    val readRepositoryCoreSettings:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationRepositoryCoreSettings, MigrationReadFailure>

    val readIssues:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationIssuePopulation, MigrationReadFailure>

    val readPullRequests:
        options:MigrationGitHubReadOptions ->
        issues:MigrationIssuePopulation ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationPullRequestPopulation, MigrationReadFailure>

    val readIssueComments:
        options:MigrationGitHubReadOptions ->
        issues:MigrationIssuePopulation ->
        issueNumber:int ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationIssueCommentPopulation, MigrationReadFailure>

    val readIssueTypes:
        options:MigrationGitHubReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationIssueTypePopulation, MigrationReadFailure>

    val readNativeRelations:
        options:MigrationGitHubReadOptions ->
        issues:MigrationIssuePopulation ->
        transport:IMigrationGitHubReadTransport ->
            Result<MigrationRelationPopulation, MigrationReadFailure>

    val readProjectItems:
        options:MigrationProjectReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationProjectItemPopulation, MigrationReadFailure>

    val readProjectFields:
        options:MigrationProjectReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationProjectFieldPopulation, MigrationReadFailure>

    val readProjectValues:
        options:MigrationProjectReadOptions -> transport:IMigrationGitHubReadTransport ->
            Result<MigrationProjectValuePopulation, MigrationReadFailure>

    val reconcileProject:
        items:MigrationProjectItemPopulation ->
        fields:MigrationProjectFieldPopulation ->
        values:MigrationProjectValuePopulation ->
            Result<MigrationProjectSnapshot, MigrationReadFailure>
