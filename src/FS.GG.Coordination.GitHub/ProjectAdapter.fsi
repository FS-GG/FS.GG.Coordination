namespace FS.GG.Coordination.GitHub

type RepositoryCoordinates = { Owner: string; Name: string }

type ProjectContent =
    | RepositoryIssue of repository: RepositoryCoordinates * number: int * contentId: LiveId
    | PullRequest of repository: RepositoryCoordinates * number: int * contentId: LiveId
    | DraftIssue of contentId: LiveId
    | RedactedContent of contentId: LiveId
    | UnknownContent of kind: string * contentId: LiveId

type ProjectItem =
    { ProjectId: LiveId
      ItemId: LiveId
      Content: ProjectContent
      Archived: bool }

type ProjectItemPage = { Number: int; Items: ProjectItem list; TerminalPage: bool }

type ProjectObservation =
    | ProjectComplete of revision: string * pages: ProjectItemPage list
    | ProjectIncomplete of reason: string * cursor: string option
    | ProjectUnsupported of reason: string
    | ProjectUnauthorized of reason: string
    | ProjectUnreadable of reason: string
    | ProjectIndeterminate of reason: string

type ProjectSnapshot =
    { Revision: string
      PageCount: int
      NodeCount: int
      Items: ProjectItem list }

type ProjectReadFailure =
    | ProjectObservationRefused of ObservationRefusal
    | ProjectObservationUnreadable of string
    | InvalidProjectPageChain
    | InvalidProjectItem of ProjectItem
    | DuplicateProjectItemId of LiveId
    | DuplicateProjectContent of LiveId

type ProjectMembership =
    | ActiveMembership of ProjectItem
    | ArchivedMembership of ProjectItem
    | ExternalRepositoryMembership of ProjectItem
    | DraftMembership of ProjectItem
    | RedactedMembership of ProjectItem
    | UnknownMembership of ProjectItem
    | MissingMembership

type MembershipResolutionFailure = InvalidExpectedRepository | InvalidTargetContentIdentity

type ProjectionNature = ProjectionOnly

type StatusOptionProjection = { Id: LiveId; Name: SemanticName }

type StatusFieldProjection =
    { ProjectId: LiveId
      ItemId: LiveId
      FieldId: LiveId
      FieldName: SemanticName
      Options: StatusOptionProjection list
      SelectedOptionId: LiveId option }

type StatusObservation =
    | StatusComplete of revision: string * evidence: PageEvidence * fields: StatusFieldProjection list
    | StatusIncomplete of reason: string * cursor: string option
    | StatusUnsupported of reason: string
    | StatusUnauthorized of reason: string
    | StatusUnreadable of reason: string
    | StatusIndeterminate of reason: string

type StatusSnapshot =
    { Revision: string
      Nature: ProjectionNature
      ProjectId: LiveId
      ItemId: LiveId
      FieldId: LiveId
      FieldName: SemanticName
      Options: StatusOptionProjection list
      SelectedOptionId: LiveId option }

type StatusReadFailure =
    | StatusObservationRefused of ObservationRefusal
    | StatusObservationUnreadable of string
    | InvalidStatusCompletenessEvidence
    | StatusFieldMissing
    | StatusFieldDuplicated
    | InvalidStatusField
    | DuplicateStatusOptionId of LiveId
    | DuplicateStatusOptionName of SemanticName
    | UnknownSelectedStatusOption of LiveId

type MembershipIntent = EnsureMember of projectId: LiveId * contentId: LiveId | EnsureNotMember of itemId: LiveId * contentId: LiveId
type MembershipOperation = AddMembershipOperation of projectId: LiveId * contentId: LiveId | RemoveMembershipOperation of projectId: LiveId * itemId: LiveId * contentId: LiveId

type MembershipPlan =
    { Before: ProjectSnapshot
      Repository: RepositoryCoordinates
      CausationIdentity: string
      IdempotencyIdentity: string
      Operation: MembershipOperation }

type MembershipNoOpReceipt = { ObservedRevision: string; IdempotencyIdentity: string; Intent: MembershipIntent }
type MembershipPlanDecision = MembershipPlanned of MembershipPlan | MembershipNoOp of MembershipNoOpReceipt

type MembershipPlanRefusal =
    | InvalidMembershipExpectedRevision
    | MembershipStaleExpectedRevision of observed: string
    | InvalidMembershipCausationIdentity
    | InvalidMembershipIntent
    | MembershipMutationIneligible of ProjectMembership

type MembershipPreStateRefusal =
    | MembershipPreStateReadRefused of ProjectReadFailure
    | MembershipReReadRequired of plannedRevision: string * observedRevision: string
    | ConcurrentMembershipChange

type MembershipPostStateRefusal =
    | MembershipPostStateReadRefused of ProjectReadFailure
    | InvalidMembershipResultRevision
    | MembershipResultRevisionDidNotAdvance of string
    | MembershipResultRevisionMismatch of expected: string * observed: string
    | InvalidResultingProjectItem
    | MembershipPostStateMismatch

type StatusIntent = SetStatus of optionId: LiveId | ClearStatus
type StatusOperation = SetStatusOperation of optionId: LiveId | ClearStatusOperation

type StatusPlan =
    { Before: StatusSnapshot
      CausationIdentity: string
      IdempotencyIdentity: string
      Operation: StatusOperation }

type StatusNoOpReceipt = { ObservedRevision: string; IdempotencyIdentity: string; Intent: StatusIntent }
type StatusPlanDecision = StatusPlanned of StatusPlan | StatusNoOp of StatusNoOpReceipt

type StatusPlanRefusal =
    | InvalidStatusExpectedRevision
    | StatusStaleExpectedRevision of observed: string
    | InvalidStatusCausationIdentity
    | InvalidStatusIntent
    | RequestedStatusOptionMissing of LiveId

type StatusPreStateRefusal =
    | StatusPreStateReadRefused of StatusReadFailure
    | StatusReReadRequired of plannedRevision: string * observedRevision: string
    | ConcurrentStatusChange

type StatusPostStateRefusal =
    | StatusPostStateReadRefused of StatusReadFailure
    | InvalidStatusResultRevision
    | StatusResultRevisionDidNotAdvance of string
    | StatusResultRevisionMismatch of expected: string * observed: string
    | StatusPostStateMismatch

[<RequireQualifiedAccess>]
module ProjectAdapter =
    val readProject: ProjectObservation -> Result<ProjectSnapshot, ProjectReadFailure>
    val resolveMembership: expectedRepository: RepositoryCoordinates -> targetContentId: LiveId -> ProjectSnapshot -> Result<ProjectMembership, MembershipResolutionFailure>
    val readStatus: projectId: LiveId -> itemId: LiveId -> StatusObservation -> Result<StatusSnapshot, StatusReadFailure>
    val planMembership: expectedRevision: string -> causationIdentity: string -> repository: RepositoryCoordinates -> MembershipIntent -> ProjectSnapshot -> Result<MembershipPlanDecision, MembershipPlanRefusal>
    val checkMembershipPreState: MembershipPlan -> ProjectObservation -> Result<ProjectSnapshot, MembershipPreStateRefusal>
    val verifyMembershipPostState: expectedResultRevision: string -> resultingItem: ProjectItem option -> MembershipPlan -> ProjectObservation -> Result<ProjectSnapshot, MembershipPostStateRefusal>
    val planStatus: expectedRevision: string -> causationIdentity: string -> StatusIntent -> StatusSnapshot -> Result<StatusPlanDecision, StatusPlanRefusal>
    val checkStatusPreState: StatusPlan -> StatusObservation -> Result<StatusSnapshot, StatusPreStateRefusal>
    val verifyStatusPostState: expectedResultRevision: string -> StatusPlan -> StatusObservation -> Result<StatusSnapshot, StatusPostStateRefusal>
