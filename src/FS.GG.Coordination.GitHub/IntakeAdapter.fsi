namespace FS.GG.Coordination.GitHub

type IntakeSurface = Issue | IssueFields | ProjectMembership | Hierarchy | Dependencies | ProtocolState

type IntakeOutcome =
    | Observed of string
    | Missing
    | Redacted
    | Unauthorized of string
    | Archived
    | External
    | Draft
    | Unsupported of string
    | Unreadable of string
    | Indeterminate of string

type IntakeFact = { Surface: IntakeSurface; Outcome: IntakeOutcome }
type IntakePage = { Number: int; Facts: IntakeFact list; TerminalPage: bool }
type IntakeObservation = { Identity: string; Revision: string; Pages: IntakePage list }
type IntakeSnapshot = { Identity: string; Revision: string; Facts: IntakeFact list; Digest: string }

type IntakeDiagnostic = { Code: string; Surface: IntakeSurface option; Message: string }

type ProtocolInitializationIntent =
    | InitializeProtocolIssue of string
    | InitializeProjectMembership of string
    | InitializeHierarchy of string
    | InitializeDependencies of string
    | InitializeRequiredIssueFields of string

type IntakeEffect = { Ordinal: int; Surface: IntakeSurface; Before: string; After: string; Compensation: string }
type IntakePlan = { Identity: string; Causation: string; Before: IntakeSnapshot; Effects: IntakeEffect list; IntendedPostState: IntakeFact list; Digest: string }
type IntakeNoOp = { Identity: string; Revision: string; Digest: string }
type IntakePlanDecision = IntakePlanned of IntakePlan | IntakeNoOp of IntakeNoOp

type IntakeApplyFailure =
    | InvalidSealedPlan
    | PreStateRefused of IntakeDiagnostic list
    | FullFenceChanged
    | ScriptLengthMismatch
    | EffectRejected of ordinal: int * reason: string
    | EffectPostStateRefused of ordinal: int * IntakeDiagnostic list
    | EffectPostStateMismatch of ordinal: int
    | DurableResultMismatch of ordinal: int
    | FinalPostStateMismatch

type DurableEffect = { PlanDigest: string; Ordinal: int; ResultRevision: string; PostStateDigest: string }
type ScriptedEffectResult = { Ordinal: int; Accepted: bool; Reason: string option; After: IntakeObservation }
type IntakeApplyReceipt = { PlanDigest: string; FinalRevision: string; AcceptedEffects: DurableEffect list; CompensatedOrdinals: int list }
type IntakeApplyMode = Execute | Resume of DurableEffect list | RollForward of DurableEffect list | Compensate of DurableEffect list

[<RequireQualifiedAccess>]
module IntakeAdapter =
    val observe: IntakeObservation -> Result<IntakeSnapshot, IntakeDiagnostic list>
    val plan: causation: string -> intents: ProtocolInitializationIntent list -> IntakeObservation -> Result<IntakePlanDecision, IntakeDiagnostic list>
    val applyControlled: IntakePlan -> reobserved: IntakeObservation -> mode: IntakeApplyMode -> scripted: ScriptedEffectResult list -> Result<IntakeApplyReceipt, IntakeApplyFailure>

