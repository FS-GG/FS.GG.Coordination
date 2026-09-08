namespace FS.GG.Coordination.Qualification.Contracts

type RuntimeBuildInputs =
    { OutputType: string
      IsPackable: bool
      PublishProfile: string option
      RuntimeIdentifier: string option
      SelfContained: bool
      Listening: bool
      DeploymentConfigured: bool
      ProductionAuthority: bool
      EvaluatedProjects: string list }

type RuntimeClauseDisposition =
    { Clause: string
      Disposition: string
      Evidence: string }

type RuntimeRecoveryExercise =
    { ExerciseId: string
      Failure: string
      ExpectedSubjects: string list
      RecoveredSubjects: string list
      ExpectedPages: int list
      RecoveredPages: int list
      AuthorityBefore: string
      AuthorityAfter: string
      RecoveryPath: string
      ProviderConfirmed: bool
      Settlement: string
      DiagnosticInput: string option
      DiagnosticOutput: string option }

type RuntimeAcceptedChild =
    { UnitId: string
      ReceiptDigest: string }

type RuntimeOperationsFacts =
    { Unit: string
      PrerequisiteReceiptSha256: string
      RoadmapRevision: string
      RoadmapSha256: string
      CandidateHead: string
      BuildInputs: RuntimeBuildInputs
      Clauses: RuntimeClauseDisposition list
      Exercises: RuntimeRecoveryExercise list
      AcceptedChildren: RuntimeAcceptedChild list
      ModelIdentity: string
      ProductionV2: bool
      InstalledAuditExecution: bool
      PollingReduced: bool
      Gs208Claimed: bool }

type RuntimeOperationsReport =
    { SchemaVersion: int
      Unit: string
      PrerequisiteReceiptSha256: string
      RoadmapRevision: string
      RoadmapSha256: string
      CandidateHead: string
      RuntimeDisposition: string
      AuditAuthority: string
      BuildInputs: RuntimeBuildInputs
      Clauses: RuntimeClauseDisposition list
      Exercises: RuntimeRecoveryExercise list
      AcceptedChildren: RuntimeAcceptedChild list
      ModelIdentity: string
      Limits: string list
      ProductionV2: bool
      InstalledAuditExecution: bool
      PollingReduced: bool
      Gs208Claimed: bool
      Seal: string }

[<RequireQualifiedAccess>]
type RuntimeOperationsFinding =
    | MissingField of string
    | ChangedPrerequisite
    | ChangedRoadmap
    | SubstitutedHead
    | HostActivated of string
    | ClauseInventoryChanged
    | UnsupportedDisposition of string
    | MissingRecovery of string
    | OmittedSubject of string
    | OmittedPage of string
    | StaleReplayAuthority of string
    | InventedSettlement of string
    | SecretLeak of string
    | ChildReceiptChanged of string
    | ModelIdentityChanged
    | UnsupportedClaim of string
    | AlteredSeal
    | ReplayConflict
    | InvalidSerialization of string

type RuntimeOperationsControlResult =
    { ControlId: string
      ControlPassed: bool
      BaselineGreen: bool
      Evidence: string }

[<RequireQualifiedAccess>]
module GitHubRuntimeOperationsQualification =
    val prerequisiteReceiptSha256: string
    val roadmapRevision: string
    val roadmapSha256: string
    val modelIdentity: string
    val requiredClauses: RuntimeClauseDisposition list
    val requiredFailures: string list
    val acceptedChildren: RuntimeAcceptedChild list
    val requiredControls: string list
    val redactDiagnostic: syntheticSecret: string -> diagnostic: string -> string
    val compile: facts: RuntimeOperationsFacts -> Result<RuntimeOperationsReport, RuntimeOperationsFinding list>
    val serialize: report: RuntimeOperationsReport -> string
    val verify: expectedSeal: string -> report: RuntimeOperationsReport -> Result<RuntimeOperationsReport, RuntimeOperationsFinding list>
    val parse: value: string -> Result<RuntimeOperationsReport, RuntimeOperationsFinding list>
    val replay: prior: RuntimeOperationsReport -> facts: RuntimeOperationsFacts -> Result<RuntimeOperationsReport, RuntimeOperationsFinding list>
    val validateControls: generated: RuntimeOperationsControlResult list -> independent: RuntimeOperationsControlResult list -> Result<unit, string list>
