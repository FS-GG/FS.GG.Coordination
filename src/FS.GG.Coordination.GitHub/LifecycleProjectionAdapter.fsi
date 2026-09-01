namespace FS.GG.Coordination.GitHub

type LifecycleIntent = IntentBacklog | IntentReady | IntentPaused | IntentCancelled
type LifecycleFactOutcome = FactObserved | FactProvenAbsent | FactIncomplete | FactUnauthorized | FactUnreadable | FactStale | FactContradictory
type LifecycleAuthority = HoldAuthority | DependencyAuthority | ClaimJournalAuthority | PullRequestAuthority | ReviewJournalAuthority | DeliveryJournalAuthority
type LifecycleFact =
    { Subject: string
      Revision: string
      Authority: LifecycleAuthority
      Outcome: LifecycleFactOutcome
      Current: bool }
type LifecycleIssueState = IssueOpen | IssueClosed
type DerivedLifecycleStage = StageBacklog | StageReady | StagePaused | StageCancelled | StageBlocked | StageClaimed | StageInReview | StageAccepted | StageDelivered
type LifecycleProjectionObservation =
    { Complete: bool
      Subject: string
      Revision: string
      Intent: LifecycleIntent
      Hold: LifecycleFact
      Dependency: LifecycleFact
      Claim: LifecycleFact
      PullRequest: LifecycleFact
      Review: LifecycleFact
      Delivery: LifecycleFact
      DeliveryProtected: bool
      IssueState: LifecycleIssueState
      Status: StatusSnapshot }
type LifecycleProjectionCost = { AuthorityReads: int; MaximumEffects: int }
type LifecycleProjectionPlan =
    { Subject: string
      SourceRevision: string
      Stage: DerivedLifecycleStage
      StatusName: string
      StatusDecision: StatusPlanDecision
      Seal: string
      Cost: LifecycleProjectionCost }
type LifecycleProjectionRefusal =
    | InvalidLifecycleSubject
    | LifecycleObservationIncomplete
    | InvalidLifecycleRevision
    | LifecycleFactSubjectMismatch
    | InvalidLifecycleFactRevision
    | WrongLifecycleAuthority of string * LifecycleAuthority
    | LifecycleFactNotKnowledge of string * LifecycleFactOutcome
    | HistoricalLifecycleFact of string
    | ContradictoryLifecycleFacts of string
    | UnprotectedLifecycleDelivery
    | ClosedIssueWithoutTerminalAuthority
    | LifecycleStatusOptionMissing of string
    | LifecycleStatusPlanRefused of StatusPlanRefusal
    | AlteredLifecyclePlan
    | LifecycleStatusPreStateRefused of StatusPreStateRefusal
    | LifecycleStatusPostStateRefused of StatusPostStateRefusal

[<RequireQualifiedAccess>]
module LifecycleProjectionAdapter =
    val derive: LifecycleProjectionObservation -> Result<DerivedLifecycleStage, LifecycleProjectionRefusal list>
    val statusName: DerivedLifecycleStage -> string
    val plan: causationIdentity: string -> LifecycleProjectionObservation -> Result<LifecycleProjectionPlan, LifecycleProjectionRefusal list>
    val authorize: LifecycleProjectionPlan -> LifecycleProjectionObservation -> StatusObservation -> Result<StatusSnapshot, LifecycleProjectionRefusal list>
    val verify: expectedResultRevision: string -> LifecycleProjectionPlan -> StatusObservation -> Result<StatusSnapshot, LifecycleProjectionRefusal list>
