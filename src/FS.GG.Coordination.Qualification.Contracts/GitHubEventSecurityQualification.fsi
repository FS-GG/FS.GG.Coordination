namespace FS.GG.Coordination.Qualification.Contracts

type GitHubEventSecurityFacts =
    { RawPayload: byte array
      Signature: string
      Secret: byte array
      DeliveryId: string
      ExpectedInstallationId: int64
      ExpectedRepository: string
      ReceivedAtUnixSeconds: int64
      EventTimestampUnixSeconds: int64
      ReplayWindowSeconds: int64
      SeenDeliveryIds: string list
      SeenPayloadSha256: string list
      ApiSubject: string
      ApiRevision: int64
      RequiredPermissions: string list
      GrantedPermissions: string list
      AttemptsDerivedWrite: bool }

type GitHubEventSecurityPlan =
    { SchemaVersion: int
      DeliveryId: string
      InstallationId: int64
      Repository: string
      SignatureAlgorithm: string
      Signature: string
      PayloadSha256: string
      EventTimestampUnixSeconds: int64
      Subject: string
      SubjectRevision: int64
      RequiredPermissions: string list
      ReplayLowerBound: int64
      ReplayUpperBound: int64
      Disposition: string
      AttemptsDerivedWrite: bool
      SchedulingKey: string
      Seal: string }

[<RequireQualifiedAccess>]
type GitHubEventSecurityFinding =
    | MissingField of string
    | MalformedField of string
    | MalformedPayload of string
    | InvalidSignature
    | InstallationScopeMismatch of int64
    | RepositoryScopeMismatch of string
    | ReplayExpired of int64
    | ReplayFromFuture of int64
    | DuplicateDelivery of string
    | DuplicatePayload of string
    | PayloadApiDisagreement of string
    | NonCanonicalPermissions of string
    | MissingPermission of string
    | ExcessivePermission of string
    | DirectWriteAttempt of string
    | AlteredSeal
    | ReplayConflict of string
    | InvalidSerialization of string

type GitHubEventSecurityControl =
    | EventSecurityPrerequisite | EventSecurityRoadmap | SignaturePositive | SignatureNegative
    | EventInstallationScope | EventRepositoryScope | ReplayLowerBound | ReplayUpperBound
    | DuplicateDelivery | PayloadApiAgreement | PayloadApiDisagreement | LeastPrivilege
    | ExcessivePermission | MissingPermission | SchedulingOnly | ExclusiveWriter
    | DirectWrite | EventSecurityOrdering | EventSecuritySeal | EventSecurityReplay
    | EventSecurityQuintPreservation | EventSecurityNoNetwork | EventSecurityNoProductionQueue
    | EventSecurityNoMutation

type GitHubEventSecurityControlResult =
    { Control: GitHubEventSecurityControl
      ControlPassed: bool
      BaselineGreen: bool }

module GitHubEventSecurityQualification =
    val disposition: string
    val requiredControls: GitHubEventSecurityControl list
    val controlId: GitHubEventSecurityControl -> string
    val sign: secret: byte array -> rawPayload: byte array -> string
    val compile: GitHubEventSecurityFacts -> Result<GitHubEventSecurityPlan, GitHubEventSecurityFinding list>
    val serialize: GitHubEventSecurityPlan -> string
    val parse: string -> Result<GitHubEventSecurityPlan, GitHubEventSecurityFinding list>
    val verify: expectedSeal: string -> GitHubEventSecurityPlan -> Result<GitHubEventSecurityPlan, GitHubEventSecurityFinding list>
    val replay: prior: GitHubEventSecurityPlan -> facts: GitHubEventSecurityFacts -> Result<GitHubEventSecurityPlan, GitHubEventSecurityFinding list>
    val validateControls: generated: GitHubEventSecurityControlResult list -> independent: GitHubEventSecurityControlResult list -> Result<unit, string list>
