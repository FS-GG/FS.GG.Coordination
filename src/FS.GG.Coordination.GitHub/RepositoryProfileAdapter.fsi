namespace FS.GG.Coordination.GitHub

open System

type RepositoryRole = Authority | Framework | NonParticipant
type AdministrationBoundary = OrganizationAdministered | ExternalObserveOnly
type RepositoryRosterRow =
    { Id: string
      FullName: string
      Role: RepositoryRole
      Capabilities: string list
      KitDelivery: string option
      AbsenceCover: string option
      Reason: string option }

type RepositoryRosterSnapshot =
    { SchemaVersion: int
      SourceRevision: string
      SourceArtifactSha256: string
      CanonicalRosterSha256: string
      ReviewedAt: DateTimeOffset
      Complete: bool
      Rows: RepositoryRosterRow list }

type NativeCustomProperty = { Name: string; Value: string }
type RepositoryProfile =
    { Id: string
      FullName: string
      Role: RepositoryRole
      Administration: AdministrationBoundary
      Capabilities: string list
      KitDelivery: string option
      AbsenceCover: string option
      Reason: string option
      NativeProperties: NativeCustomProperty list
      PropertyMutationPermitted: bool }

type RepositoryProfileReport =
    { SourceRevision: string
      SourceArtifactSha256: string
      CanonicalRosterSha256: string
      Profiles: RepositoryProfile list
      Seal: string }

type RepositoryProfileFinding =
    | UnsupportedRosterSchema of int
    | IncompleteRoster
    | StaleRoster
    | InvalidSourceBinding of string
    | InvalidRepositoryIdentity of string
    | DuplicateRepositoryId of string
    | DuplicateRepositoryName of string
    | UnsupportedRepositoryRole of string
    | UnsupportedRepositoryCapability of string * string
    | InvalidRepositoryMetadata of string
    | MissingAuthority
    | MultipleAuthorities
    | LossyRepositoryProfile of string
    | NativePropertyOverflow of string * string
    | AlteredRepositoryProfileSeal

module RepositoryProfileAdapter =
    val allowedCapabilities: string list
    val canonicalRosterDigest: RepositoryRosterRow list -> string
    val compile: asOf: DateTimeOffset -> maxAge: TimeSpan -> RepositoryRosterSnapshot -> Result<RepositoryProfileReport, RepositoryProfileFinding list>
    val verify: expectedSeal: string -> asOf: DateTimeOffset -> maxAge: TimeSpan -> RepositoryRosterSnapshot -> Result<RepositoryProfileReport, RepositoryProfileFinding list>
