namespace FS.GG.Coordination.GitHub

open System

type CommentIdentity = { DatabaseId: int64; NodeId: string }

type CommentObservation =
    { Identity: CommentIdentity
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      AuthorLogin: string
      Body: byte array }

type CommentPage =
    { Number: int
      Comments: CommentObservation list
      EndCursor: string option
      TerminalPage: bool }

type CommentReadObservation =
    | CommentsComplete of revision: string * pages: CommentPage list
    | CommentsIncomplete of reason: string * cursor: string option
    | CommentsUnsupported of reason: string
    | CommentsUnauthorized of reason: string
    | CommentsUnreadable of reason: string
    | CommentsIndeterminate of reason: string

type CommentSnapshot =
    { Revision: string
      PageCount: int
      NodeCount: int
      TerminalCursor: string option
      Comments: CommentObservation list }

type CommentReadFailure =
    | CommentObservationIncomplete of string * string option
    | CommentObservationUnsupported of string
    | CommentObservationUnauthorized of string
    | CommentObservationUnreadable of string
    | CommentObservationIndeterminate of string
    | InvalidCommentRevision
    | InvalidCommentPageChain
    | InvalidCommentObservation of int64
    | DuplicateCommentDatabaseId of int64
    | DuplicateCommentNodeId of string
    | ReorderedCommentObservation

type JournalAuthority =
    { Subject: string
      JournalKind: string
      JournalShard: string
      Generation: int64
      JournalCommit: string
      AuthorityDigest: string }

type ProjectionMarker =
    { Schema: string
      Subject: string
      JournalKind: string
      JournalShard: string
      Generation: int64
      JournalCommit: string
      AuthorityDigest: string
      ProjectionDigest: string }

type ParsedProjection =
    { Marker: ProjectionMarker
      HumanBody: byte array
      FullBodyDigest: string }

type MarkerFailure =
    | MarkerMissing
    | MarkerMalformed of string
    | MarkerUnsupported of string
    | ProjectionDigestMismatch of expected: string * observed: string

type ExpectedProjection =
    { Identity: CommentIdentity
      UpdatedAt: DateTimeOffset
      BodyDigest: string }

type TrustedProjection =
    { Comment: CommentObservation
      Projection: ParsedProjection
      Authority: JournalAuthority }

type ProjectionTrustFailure =
    | ProjectionMissing
    | ProjectionDeleted of CommentIdentity
    | ProjectionEdited of CommentIdentity
    | ProjectionMalformed of CommentIdentity * MarkerFailure
    | ProjectionTampered of CommentIdentity
    | ProjectionAuthorityDigestMismatch of expected: string * observed: string
    | ProjectionAmbiguous of CommentIdentity

type RenderingPolicy = { Version: string }

type ProjectionRequest =
    { Authority: JournalAuthority
      Policy: RenderingPolicy
      HumanBody: string
      CausationIdentity: string }

type ProjectionOperation =
    | CreateProjection of body: byte array
    | ReplaceProjection of identity: CommentIdentity * expectedUpdatedAt: DateTimeOffset * expectedBodyDigest: string * body: byte array

type ProjectionPlan =
    { Before: CommentSnapshot
      Authority: JournalAuthority
      Policy: RenderingPolicy
      CausationIdentity: string
      DesiredBody: byte array
      DesiredBodyDigest: string
      IdempotencyIdentity: string
      Operation: ProjectionOperation }

type ProjectionNoOpReceipt =
    { ObservedRevision: string
      Identity: CommentIdentity
      IdempotencyIdentity: string }

type ProjectionPlanDecision = ProjectionPlanned of ProjectionPlan | ProjectionNoOp of ProjectionNoOpReceipt

type ProjectionPlanFailure =
    | InvalidProjectionRequest of string
    | ProjectionSelectionFailed of ProjectionTrustFailure

type ProjectionPreStateFailure =
    | ProjectionPreStateReadFailed of CommentReadFailure
    | ProjectionReReadRequired of plannedRevision: string * observedRevision: string
    | ConcurrentProjectionChange
    | DurableAuthorityChanged

type ProjectionPostStateFailure =
    | ProjectionPostStateReadFailed of CommentReadFailure
    | ProjectionResultRevisionDidNotAdvance of string
    | ProjectionResultRevisionMismatch of expected: string * observed: string
    | ProjectionPostStateMismatch
    | UnrelatedCommentChanged

[<RequireQualifiedAccess>]
module CommentProjectionAdapter =
    [<Literal>]
    val MarkerPrefix: string = "<!-- fsgg:projection/v1 -->"
    [<Literal>]
    val MarkerSchema: string = "fsgg.coordination.projection-marker/1"
    val sha256: byte array -> string
    val readComments: CommentReadObservation -> Result<CommentSnapshot, CommentReadFailure>
    val parseProjection: CommentObservation -> Result<ParsedProjection, MarkerFailure>
    val evaluateTrust: expected: ExpectedProjection option -> authority: JournalAuthority -> CommentSnapshot -> Result<TrustedProjection, ProjectionTrustFailure>
    val renderProjection: ProjectionRequest -> Result<byte array, ProjectionPlanFailure>
    val planProjection: expected: ExpectedProjection option -> ProjectionRequest -> CommentSnapshot -> Result<ProjectionPlanDecision, ProjectionPlanFailure>
    val checkPreState: currentAuthority: JournalAuthority -> ProjectionPlan -> CommentReadObservation -> Result<CommentSnapshot, ProjectionPreStateFailure>
    val verifyPostState: expectedResultRevision: string -> resultingComment: CommentObservation -> ProjectionPlan -> CommentReadObservation -> Result<CommentSnapshot, ProjectionPostStateFailure>
