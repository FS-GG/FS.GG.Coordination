namespace FS.GG.Coordination.GitHub

type IntakeSurface =
    | IssueIdentity | NativeIssueType | OrganizationFields | ProjectMembership | Hierarchy | Dependencies | RepositoryScope
    | InitialJournal | SchedulingIntent | Contract | TouchSet | Projections

type IntakeOutcome =
    | Observed of string | Missing | Redacted | Unauthorized of string | Archived | External | Draft
    | Unknown of string | Duplicate of string | Cycle of string | Partial of string | Stale of observed: string * expected: string
    | Unsupported of string | Unreadable of string | Indeterminate of string

type IntakeFact = { Surface: IntakeSurface; Outcome: IntakeOutcome }
type IntakePage = { Number: int; Cursor: string option; NextCursor: string option; Facts: IntakeFact list; TerminalPage: bool }
type IntakeObservation = { Identity: string; Revision: string; Pages: IntakePage list }
type IntakeSnapshot = { Identity: string; Revision: string; Facts: IntakeFact list; Digest: string }
type IntakeDiagnostic = { Code: string; Surface: IntakeSurface option; Message: string }

type ProtocolInitializationIntent =
    | InitializeJournal of string
    | InitializeSchedulingIntent of string
    | InitializeContract of string
    | InitializeTouchSet of string list
    | InitializeProjections of string list

type IntakeRequest = { Identity: string; Repository: string; Causation: string; Initializations: ProtocolInitializationIntent list }
type CanonicalIntakeIntent = { Identity: string; Repository: string; Causation: string; Initializations: ProtocolInitializationIntent list; Digest: string }
type IntakeEffect =
    { Ordinal: int
      OperationIdentity: string
      Dependencies: string list
      ExpectedRevision: string
      Precondition: IntakeFact
      Postcondition: IntakeFact
      Compensation: IntakeFact }
type IntakePlan = { Identity: string; Repository: string; Causation: string; Before: IntakeSnapshot; Effects: IntakeEffect list; IntendedPostState: IntakeFact list; Digest: string }
type IntakeNoOp = { Identity: string; Revision: string; Digest: string }
type IntakePlanDecision = IntakePlanned of IntakePlan | IntakeNoOp of IntakeNoOp

type DurableEffect = { PlanDigest: string; Ordinal: int; OperationIdentity: string; ResultRevision: string; PostStateDigest: string }
type IntakeApplyFailure =
    | InvalidSealedPlan | PreStateRefused of IntakeDiagnostic list | FullFenceChanged | ScriptLengthMismatch
    | EffectRejected of ordinal: int * reason: string * accepted: DurableEffect list | EffectPostStateRefused of ordinal: int * IntakeDiagnostic list * accepted: DurableEffect list
    | EffectPreconditionChanged of ordinal: int * accepted: DurableEffect list | EffectIdentityMismatch of ordinal: int * accepted: DurableEffect list
    | EffectPostStateMismatch of ordinal: int * accepted: DurableEffect list | DurableResultMismatch of ordinal: int | FinalPostStateMismatch of accepted: DurableEffect list

type ScriptedEffectResult = { Ordinal: int; OperationIdentity: string; Accepted: bool; Reason: string option; After: IntakeObservation }
type IntakeApplyReceipt = { PlanDigest: string; FinalRevision: string; AcceptedEffects: DurableEffect list; CompensatedOrdinals: int list }
type IntakeApplyMode = Execute | Resume of DurableEffect list | RollForward of DurableEffect list | Compensate of DurableEffect list

[<RequireQualifiedAccess>]
type DiscoveryDetail = Known of string | ExplicitlyUnknown of reason: string | Deferred of reason: string

[<RequireQualifiedAccess>]
type CaptureIdentityMode = CreateOrReuse

[<RequireQualifiedAccess>]
type CaptureAuthorityRead =
    | IssueIdentity
    | NativeTypeAndFields
    | ProjectMembership
    | Relations
    | RepositoryScope
    | ProtocolState

type StagedCaptureRequest =
    { Identity: string
      IdentityMode: CaptureIdentityMode
      Repository: string
      Causation: string
      RootCause: DiscoveryDetail
      Verification: DiscoveryDetail
      TouchSet: string list option }

type StagedCaptureObservation =
    { Intake: IntakeObservation
      AuthorityReads: CaptureAuthorityRead list
      UnrelatedProjectItems: int
      UnrelatedBacklogItems: int }

type IntakeOperationBudget = { AuthorityReads: int; Mutations: int }
type StagedCapturePlan =
    { ContractSchema: string
      Intent: CanonicalIntakeIntent
      Decision: IntakePlanDecision
      Budget: IntakeOperationBudget }

[<RequireQualifiedAccess>]
type ReadyPromotionSurface =
    | RootCause
    | TouchSet
    | VerificationContract
    | Dependencies
    | RouteDecision
    | NativeIssueType
    | OrganizationFields
    | RepositoryScope
    | WorkClassification

type ReadyPromotionFact = { Surface: ReadyPromotionSurface; Value: string }

[<RequireQualifiedAccess>]
module IntakeAdapter =
    val stagedContractSchema: string
    val requiredCaptureReads: CaptureAuthorityRead list
    val requiredReadyPromotionSurfaces: ReadyPromotionSurface list
    val validate: IntakeRequest -> Result<CanonicalIntakeIntent, IntakeDiagnostic list>
    val inspect: IntakeObservation -> Result<IntakeSnapshot, IntakeDiagnostic list>
    val plan: CanonicalIntakeIntent -> IntakeObservation -> Result<IntakePlanDecision, IntakeDiagnostic list>
    val applyControlled: IntakePlan -> reobserved: IntakeObservation -> mode: IntakeApplyMode -> scripted: ScriptedEffectResult list -> Result<IntakeApplyReceipt, IntakeApplyFailure>
    val validateCapture: StagedCaptureRequest -> Result<CanonicalIntakeIntent, IntakeDiagnostic list>
    val planCapture: StagedCaptureRequest -> StagedCaptureObservation -> Result<StagedCapturePlan, IntakeDiagnostic list>
    val prepareReadyPromotion: identity: string -> repository: string -> causation: string -> ReadyPromotionFact list -> Result<CanonicalIntakeIntent, IntakeDiagnostic list>
