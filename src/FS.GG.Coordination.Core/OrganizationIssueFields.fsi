namespace FS.GG.Coordination.Core

[<RequireQualifiedAccess>]
type SchedulingIntent = Backlog | Ready | Paused | Cancelled

[<RequireQualifiedAccess>]
type LifecycleStatus = Backlog | Ready | Blocked | Done

[<RequireQualifiedAccess>]
type HoldReason = NotYetActionable | Dependency | Decision | External | Operator

[<RequireQualifiedAccess>]
type WorkPriority = Critical | High | Normal | Low

[<RequireQualifiedAccess>]
type WorkEffort = S | M | L | XL

[<RequireQualifiedAccess>]
type WorkSeverity = Critical | High | Medium | Low | Unset

[<RequireQualifiedAccess>]
type WorkPhase = Planning | Execution | Verification | Operations

[<RequireQualifiedAccess>]
type Workstream = Composition | Coordination | Docs | Governance | Lifecycle | Versioning

type OrganizationIssueFieldObservation =
    { StableRowId: string
      Revision: string
      RepositoryScope: string
      NativeIssueType: string
      SchedulingIntent: string option
      LifecycleStatus: string option
      HoldReason: string option
      Priority: string option
      Effort: string option
      StartDate: string option
      TargetDate: string option
      Severity: string option
      Phase: string option
      Workstream: string option
      ContractReference: string option
      ContractAuthorityDigest: string option
      TouchSet: string list
      TouchSetAuthorityDigest: string option
      HierarchyPresent: bool
      HierarchyPreservable: bool
      Dependencies: string list
      DependenciesPreservable: bool
      RepositoryScopePreservable: bool
      LifecycleExempt: bool
      Complete: bool
      Current: bool
      Readable: bool }

[<RequireQualifiedAccess>]
type OrganizationIssueFieldDiagnostic =
    | UnreadableObservation | IncompleteObservation | StaleObservation
    | MissingStableRowId | MissingRevision | MissingRepositoryScope | MissingNativeIssueType
    | MissingSchedulingIntent | UnknownSchedulingIntent of string
    | MissingLifecycleStatus | UnknownLifecycleStatus of string | IntentStatusAuthorityConflict
    | MissingHoldReason | UnexpectedHoldReason | UnknownHoldReason of string
    | MissingPriority | UnknownPriority of string | MissingEffort | UnknownEffort of string
    | InvalidStartDate | InvalidTargetDate | ReversedDateRange
    | MissingSeverity | UnknownSeverity of string | MissingPhase | UnknownPhase of string
    | MissingWorkstream | UnknownWorkstream of string
    | InvalidContractReference | UnboundContractProjection
    | NoncanonicalTouchSet | UnboundTouchSetProjection
    | LossyHierarchy | LossyDependencies | LossyRepositoryScope | DuplicateStableRowId

type NormalizedOrganizationIssueFields =
    { SchedulingIntent: SchedulingIntent
      LifecycleStatus: LifecycleStatus
      HoldReason: HoldReason option
      Priority: WorkPriority
      Effort: WorkEffort
      StartDate: string option
      TargetDate: string option
      Severity: WorkSeverity
      Phase: WorkPhase
      Workstream: Workstream
      ContractReference: string option
      TouchSet: string list
      TouchSetDigest: string option }

type OrganizationIssueFieldDisposition =
    { StableRowId: string
      PrestateFingerprint: string
      Fields: NormalizedOrganizationIssueFields
      RepositoryScope: string
      NativeIssueType: string
      HierarchyPreserved: bool
      DependenciesPreserved: bool
      RepositoryScopePreserved: bool
      LifecycleExempt: bool
      NoOp: bool }

type OrganizationIssueFieldRefusal =
    { StableRowId: string option
      Diagnostics: OrganizationIssueFieldDiagnostic list }

[<RequireQualifiedAccess>]
module OrganizationIssueFields =
    val diagnosticCode: OrganizationIssueFieldDiagnostic -> string
    val schedulingIntentName: SchedulingIntent -> string
    val lifecycleStatusName: LifecycleStatus -> string
    val touchSetDigest: string list -> string
    val prestateFingerprint: OrganizationIssueFieldObservation -> string
    val validate: OrganizationIssueFieldObservation -> Result<NormalizedOrganizationIssueFields, OrganizationIssueFieldDiagnostic list>
    val plan: OrganizationIssueFieldObservation list -> Result<OrganizationIssueFieldDisposition list, OrganizationIssueFieldRefusal list>
    val canonicalPlanBytes: OrganizationIssueFieldDisposition list -> byte array
    val canonicalPlanSha256: OrganizationIssueFieldDisposition list -> string
