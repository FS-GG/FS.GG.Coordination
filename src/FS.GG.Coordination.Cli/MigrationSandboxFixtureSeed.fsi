namespace FS.GG.Coordination.Cli

/// The workflow run identity and frozen-corpus digest declared for one disposable seed.
type MigrationSandboxSeedRequest =
    { CandidateSha: string
      WorkflowRunId: int64
      WorkflowRunAttempt: int
      RunNonce: string
      CorpusSha256: string }

/// Typed scope observation. A live adapter must independently bind each field to raw provider
/// responses and a protected workflow App token before this can authorize a real effect.
type MigrationSandboxScopeObservation =
    { Complete: bool
      ActorLogin: string
      ActorDatabaseId: int64
      RepositoryId: int64
      RepositoryNodeId: string
      RepositoryFullName: string
      RepositoryPrivate: bool
      RepositoryDescription: string
      ProjectOrganization: string
      ProjectNumber: int
      ProjectNodeId: string
      ProjectTitle: string
      ProjectPrivate: bool
      ProjectClosed: bool
      GrantedPermissions: Set<string> }

type MigrationSandboxIssueObservation =
    { RepositoryId: int64
      Number: int
      DatabaseId: int64
      NodeId: string
      Title: string
      Body: string option
      State: string
      Labels: string list
      Revision: string }

/// A complete, exact-scope provider census is required at every call. The two unowned
/// digests exclude fixture issue #1 and its Project membership, respectively.
type MigrationSandboxFixtureObservation =
    { Complete: bool
      ProjectNodeId: string
      Issue: MigrationSandboxIssueObservation
      ProjectItemIdsForIssue: string list
      NonceIssueNodeIds: string list
      UnownedIssuesSha256: string
      UnownedProjectItemsSha256: string }

/// Write-ahead state retained by a durable protected-workflow adapter. Loaded values are
/// revalidated by cleanup; the seal is an integrity digest, not an authorization signature.
type MigrationSandboxSeedIntent =
    { Request: MigrationSandboxSeedRequest
      Prestate: MigrationSandboxFixtureObservation
      SeedTitle: string
      SeedBody: string option
      OwnedProjectItemId: string option
      Seal: string }

type MigrationSandboxSeedResult =
    { IntentSha256: string
      IssueNodeId: string
      ProjectItemId: string }

type MigrationSandboxCleanupResult =
    { IntentSha256: string
      ZeroResidue: bool }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedFailure =
    | InvalidRequest
    | ScopeRefused
    | MissingGrant
    | IncompleteObservation
    | UnsafePrestate
    | ExistingIntent
    | MissingIntent
    | IndeterminateProjectItem of itemNodeId:string
    | ChangedTarget
    | EffectFailed of string
    | CleanupResidue

/// No implementation of this port or callable live command is supplied by this source slice.
/// A later adapter must prove raw provider correspondence, durable intent, scoped grants and
/// global sandbox serialization before any protected workflow may use it with a token.
/// A lost Project Add response or lost durable item ID remains indeterminate: cleanup does not
/// infer ownership from issue membership and cannot claim Q6 zero residue until a separate
/// provider recovery probe proves provenance or refuses live qualification.
type IMigrationSandboxFixtureSeedPort =
    abstract ObserveScope: unit -> Result<MigrationSandboxScopeObservation, string>
    abstract ObserveFixture: nonce:string -> Result<MigrationSandboxFixtureObservation, string>
    abstract PersistIntent: MigrationSandboxSeedIntent -> Result<unit, string>
    abstract LoadIntent: unit -> Result<MigrationSandboxSeedIntent option, string>
    abstract PatchIssue:
        repositoryId:int64 * issueNodeId:string * expectedRevision:string *
        title:string * body:string option * state:string * labels:string list -> Result<unit, string>
    abstract AddProjectItem: projectNodeId:string * issueNodeId:string -> Result<string, string>
    abstract DeleteProjectItem: projectNodeId:string * itemNodeId:string -> Result<unit, string>

[<RequireQualifiedAccess>]
module MigrationSandboxFixtureSeed =
    val seed:
        request:MigrationSandboxSeedRequest -> port:IMigrationSandboxFixtureSeedPort ->
            Result<MigrationSandboxSeedResult, MigrationSandboxSeedFailure>
    val cleanup:
        request:MigrationSandboxSeedRequest -> port:IMigrationSandboxFixtureSeedPort ->
            Result<MigrationSandboxCleanupResult, MigrationSandboxSeedFailure>
