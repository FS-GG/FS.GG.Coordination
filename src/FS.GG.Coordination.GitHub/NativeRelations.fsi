namespace FS.GG.Coordination.GitHub

type RelationKind = ParentChild | Blocks

type RelationEdge =
    { Kind: RelationKind
      Source: LiveId
      Target: LiveId }

type RelationPage =
    { Number: int
      Edges: RelationEdge list
      TerminalPage: bool }

type NativeRelationObservation =
    | RelationsComplete of revision: string * scope: RelationKind * pages: RelationPage list
    | RelationsIncomplete of reason: string * cursor: string option
    | RelationsUnsupported of reason: string
    | RelationsUnauthorized of reason: string
    | RelationsIndeterminate of reason: string

type RelationSnapshot = { Revision: string; Scope: RelationKind; PageCount: int; NodeCount: int; Edges: RelationEdge list }

type RelationReadFailure =
    | RelationObservationRefused of ObservationRefusal
    | InvalidRelationPageChain
    | RelationKindMismatch of expected: RelationKind * observed: RelationKind
    | InvalidRelationEdge of RelationEdge
    | DuplicateRelationEdge of RelationEdge

type RelationIntent = AddEdge of RelationEdge | RemoveEdge of RelationEdge
type RelationOperation = AddEdgeOperation of RelationEdge | RemoveEdgeOperation of RelationEdge

type RelationMutationPlan = { Before: RelationSnapshot; CausationIdentity: string; IdempotencyIdentity: string; Operation: RelationOperation }

type RelationNoOpReceipt = { ObservedRevision: string; IdempotencyIdentity: string; Intent: RelationIntent }

type RelationPlanDecision = RelationPlanned of RelationMutationPlan | RelationNoOp of RelationNoOpReceipt

type RelationPlanRefusal =
    | InvalidRelationExpectedRevision
    | RelationStaleExpectedRevision of observed: string
    | InvalidRelationCausationIdentity
    | InvalidRelationIntent
    | RelationIntentOutsideScope of expected: RelationKind * observed: RelationKind

type RelationPreStateRefusal =
    | PreStateReadRefused of RelationReadFailure
    | ReReadRequired of plannedRevision: string * observedRevision: string
    | ConcurrentPreStateChange of planned: RelationEdge list * observed: RelationEdge list

type RelationPostStateRefusal =
    | PostStateReadRefused of RelationReadFailure
    | InvalidResultRevision
    | ResultRevisionDidNotAdvance of revision: string
    | ResultRevisionMismatch of expected: string * observed: string
    | PostStateMismatch of expected: RelationEdge list * observed: RelationEdge list

[<RequireQualifiedAccess>]
module NativeRelations =
    val read: NativeRelationObservation -> Result<RelationSnapshot, RelationReadFailure>
    val plan: expectedRevision: string -> causationIdentity: string -> RelationIntent -> RelationSnapshot -> Result<RelationPlanDecision, RelationPlanRefusal>
    val checkPreState: RelationMutationPlan -> NativeRelationObservation -> Result<RelationSnapshot, RelationPreStateRefusal>
    val verifyPostState: expectedResultRevision: string -> RelationMutationPlan -> NativeRelationObservation -> Result<RelationSnapshot, RelationPostStateRefusal>
