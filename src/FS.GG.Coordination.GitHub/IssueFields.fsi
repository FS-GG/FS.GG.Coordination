namespace FS.GG.Coordination.GitHub

open System

type SemanticName = private SemanticName of string

[<RequireQualifiedAccess>]
module SemanticName =
    val tryCreate: string -> Result<SemanticName, string>
    val value: SemanticName -> string

type LiveId = private LiveId of string

[<RequireQualifiedAccess>]
module LiveId =
    val tryCreate: string -> Result<LiveId, string>
    val value: LiveId -> string

type IdentityKind = Repository | Issue | IssueType | Field | Option

type LiveIdentity =
    { Kind: IdentityKind
      Id: LiveId
      Name: SemanticName }

type PageEvidence =
    { PageCount: int
      NodeCount: int
      TerminalPage: bool }

type CompleteObservation<'value> =
    { Revision: string
      Evidence: PageEvidence
      Values: 'value list }

type Observation<'value> =
    | Complete of CompleteObservation<'value>
    | Incomplete of reason: string * cursor: string option
    | Unsupported of reason: string
    | Unauthorized of reason: string
    | Indeterminate of reason: string

type ObservationRefusal =
    | ObservationIncomplete of reason: string * cursor: string option
    | ObservationUnsupported of reason: string
    | ObservationUnauthorized of reason: string
    | ObservationIndeterminate of reason: string
    | InvalidCompletenessEvidence
    | MissingObservationRevision

type ResolutionFailure =
    | ObservationRefused of ObservationRefusal
    | IdentityMissing
    | IdentityDuplicated
    | DuplicateLiveId of LiveId

type FieldDataType = Text | Number | Date | SingleSelect

type FieldDeclaration =
    { Name: SemanticName
      DataType: FieldDataType
      Options: SemanticName list }

type LiveOption = { Id: LiveId; Name: SemanticName }

type LiveField =
    { Id: LiveId
      Name: SemanticName
      DataType: FieldDataType
      Options: LiveOption list }

type SchemaFailure =
    | SchemaObservationRefused of ObservationRefusal
    | InvalidFieldDeclaration
    | InvalidLiveField
    | FieldMissing
    | FieldDuplicated
    | DuplicateFieldId of LiveId
    | FieldTypeDrift of expected: FieldDataType * observed: FieldDataType
    | DuplicateOptionName of SemanticName
    | DuplicateOptionId of LiveId
    | MissingOption of SemanticName
    | UnexpectedOption of SemanticName
    | CurrentValueMissing
    | CurrentValueDuplicated

type FieldValue =
    | TextValue of string
    | NumberValue of decimal
    | DateValue of DateOnly
    | SingleSelectValue of SemanticName

type CurrentFieldValue =
    { IssueId: LiveId
      FieldId: LiveId
      Value: FieldValue }

type ObservedFieldValue =
    { Revision: string
      Evidence: PageEvidence
      Value: CurrentFieldValue }

type CurrentMutationState =
    | IssueAbsent
    | IssuePresent
    | FieldAbsent
    | FieldPresent of FieldValue

type MutationIntent =
    | CreateIssue of repositoryId: LiveId * title: string
    | UpdateField of issueId: LiveId * fieldId: LiveId * value: FieldValue
    | ClearField of issueId: LiveId * fieldId: LiveId

type MutationOperation =
    | CreateIssueOperation of repositoryId: LiveId * title: string
    | UpdateFieldOperation of issueId: LiveId * fieldId: LiveId * value: FieldValue
    | ClearFieldOperation of issueId: LiveId * fieldId: LiveId

type MutationPlan =
    { ExpectedRevision: string
      IdempotencyIdentity: string
      Operation: MutationOperation }

type NoOpReceipt =
    { ObservedRevision: string
      IdempotencyIdentity: string }

type PlanDecision = Planned of MutationPlan | NoOp of NoOpReceipt

type PlanRefusal =
    | PlanObservationRefused of ObservationRefusal
    | InvalidExpectedRevision
    | StaleExpectedRevision of observed: string
    | InvalidCausationIdentity
    | InvalidMutationIntent
    | AmbiguousCurrentState
    | IncompatibleCurrentState

[<RequireQualifiedAccess>]
module IssueFields =
    val resolveIdentity: expected: SemanticName -> kind: IdentityKind -> Observation<LiveIdentity> -> Result<LiveIdentity, ResolutionFailure>
    val validateField: FieldDeclaration -> Observation<LiveField> -> Result<LiveField, SchemaFailure>
    val readCurrentValue: issueId: LiveId -> fieldId: LiveId -> Observation<CurrentFieldValue> -> Result<ObservedFieldValue, SchemaFailure>
    val plan: expectedRevision: string -> causationIdentity: string -> MutationIntent -> Observation<CurrentMutationState> -> Result<PlanDecision, PlanRefusal>
