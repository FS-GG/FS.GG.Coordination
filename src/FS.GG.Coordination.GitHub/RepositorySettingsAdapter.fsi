namespace FS.GG.Coordination.GitHub

type RepositoryIdentity =
    { NodeId: string
      DatabaseId: int64
      Owner: string
      Name: string
      DefaultBranch: string
      SourceRepositoryNodeId: string option }

type SettingsSurface =
    | Repository
    | CustomProperties
    | BranchRulesets
    | TagRulesets
    | MergePolicy
    | ActionsPolicy
    | Environments
    | ReleasesAndTags
    | CodeSecurity
    | DependencyControls
    | ImmutableReleases

type SettingValue = Boolean of bool | Integer of int64 | Text of string | TextList of string list

type RepositorySetting =
    { Surface: SettingsSurface
      Subject: string
      Name: string
      Value: SettingValue }

type SurfaceObservation =
    | Supported of Revision: string * Complete: bool * Settings: RepositorySetting list
    | Unsupported of reason: string
    | Unauthorized of reason: string
    | Unavailable of reason: string
    | Incomplete of reason: string
    | Unreadable of reason: string

type RepositorySettingsObservation =
    { Identity: RepositoryIdentity
      CapturedRevision: string
      Surfaces: Map<SettingsSurface, SurfaceObservation>
      Digest: string }

type DesiredRepositorySettings =
    { Identity: RepositoryIdentity
      Settings: RepositorySetting list
      Digest: string }

type SettingsFailure =
    | InvalidIdentity
    | IdentityDrift
    | MissingSurface of SettingsSurface
    | PartialSurface of SettingsSurface * string
    | UnsupportedDesiredSurface of SettingsSurface
    | ContradictorySetting of SettingsSurface * string * string
    | SecretValueForbidden of string
    | InvalidObservationDigest
    | InvalidDesiredDigest
    | StaleObservation of expected: string * actual: string

type SettingsOperation =
    { OperationId: string
      Surface: SettingsSurface
      Subject: string
      Name: string
      Before: SettingValue option
      After: SettingValue option
      RequiredPermission: string
      ObservationDigest: string
      DesiredDigest: string }

type RepositorySettingsPlan =
    { Identity: RepositoryIdentity
      ObservationRevision: string
      ObservationDigest: string
      DesiredDigest: string
      Operations: SettingsOperation list }

type SettingsTransportOutcome =
    | SettingsAccepted
    | SettingsDefiniteRefusal of string
    | SettingsResponseUnknown
    | SettingsPartiallyApplied of operationIds: string list

type SettingsReconcileOutcome =
    | SettingsVerified
    | SettingsRereadAndReplan
    | SettingsRollback of SettingsOperation list
    | SettingsForwardRepair of SettingsOperation list
    | SettingsRefused of string
    | SettingsIndeterminate of SettingsFailure

[<RequireQualifiedAccess>]
module RepositorySettingsAdapter =
    val surfaces: SettingsSurface list
    val surfaceId: SettingsSurface -> string
    val sha256: byte array -> string
    val identityDigest: RepositoryIdentity -> string
    val observationDigest: RepositoryIdentity -> capturedRevision: string -> Map<SettingsSurface, SurfaceObservation> -> string
    val desiredDigest: RepositoryIdentity -> RepositorySetting list -> string
    val validate: RepositorySettingsObservation -> Result<RepositorySettingsObservation, SettingsFailure>
    val plan: expectedRevision: string -> RepositorySettingsObservation -> DesiredRepositorySettings -> Result<RepositorySettingsPlan, SettingsFailure>
    val reconcile: RepositorySettingsPlan -> SettingsTransportOutcome -> RepositorySettingsObservation -> SettingsReconcileOutcome
