namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.GitHub

/// The protected workflow supplies the installation token. This source does not mint one.
type MigrationSandboxProviderOptions =
    { ApiBase: Uri
      GraphQLUri: Uri
      Token: string
      UserAgent: string }

/// A raw provider page, recorded without credentials or private payload bytes.
type MigrationSandboxProviderProof =
    { RequestIdentity: string
      RawSha256: string
      NextIdentity: string option }

/// Identity and repository-selection facts requested with the exact installation token.
/// TokenSha256 binds the request credential to a later protected mint proof; controlled
/// transports alone do not establish live provider correspondence or write permissions.
type MigrationSandboxScopeIdentityEvidence =
    { TokenSha256: string
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
      TokenRepositorySelectionComplete: bool
      Proofs: MigrationSandboxProviderProof list }

type MigrationSandboxFixtureEvidence =
    { TokenSha256: string
      Observation: MigrationSandboxFixtureObservation
      RepositoryIdentity: MigrationSandboxProviderProof
      IssuePages: MigrationSandboxProviderProof list
      ProjectPages: MigrationSandboxProviderProof list
      ExactIssue: MigrationSandboxProviderProof }

[<RequireQualifiedAccess>]
type MigrationSandboxProviderFailure =
    | InvalidOptions
    | InvalidNonce
    | TransportRefused
    | HttpRefused of int
    | GraphQLErrors
    | MalformedResponse
    | ForeignIdentity
    | PaginationRefused
    | PopulationDrift
    | MissingRevision
    | GrantUnavailable

/// A single terminal census pass, not a quiescent two-pass Q5/Q6 snapshot.
/// This adapter has no write port or command. ObserveScope always refuses permission
/// attestation with an installation token alone: the workflow declaration and
/// X-Accepted-GitHub-Permissions are not evidence of the token's effective grants.
type MigrationSandboxProviderObservation =
    new: options:MigrationSandboxProviderOptions * transport:IMigrationGitHubReadTransport ->
        MigrationSandboxProviderObservation
    member ReadScopeIdentity: unit -> Result<MigrationSandboxScopeIdentityEvidence, MigrationSandboxProviderFailure>
    member ObserveScope: unit -> Result<MigrationSandboxScopeObservation, MigrationSandboxProviderFailure>
    member ReadFixture: nonce:string -> Result<MigrationSandboxFixtureEvidence, MigrationSandboxProviderFailure>
    member ObserveFixture: nonce:string -> Result<MigrationSandboxFixtureObservation, MigrationSandboxProviderFailure>
