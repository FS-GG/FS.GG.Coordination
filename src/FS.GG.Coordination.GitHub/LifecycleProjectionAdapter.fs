namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type LifecycleIntent = IntentBacklog | IntentReady | IntentPaused | IntentCancelled
type LifecycleFactOutcome = FactObserved | FactProvenAbsent | FactIncomplete | FactUnauthorized | FactUnreadable | FactStale | FactContradictory
type LifecycleAuthority = HoldAuthority | DependencyAuthority | ClaimJournalAuthority | PullRequestAuthority | ReviewJournalAuthority | DeliveryJournalAuthority
type LifecycleFact = { Subject: string; Revision: string; Authority: LifecycleAuthority; Outcome: LifecycleFactOutcome; Current: bool }
type LifecycleIssueState = IssueOpen | IssueClosed
type DerivedLifecycleStage = StageBacklog | StageReady | StagePaused | StageCancelled | StageBlocked | StageClaimed | StageInReview | StageAccepted | StageDelivered
type LifecycleProjectionObservation =
    { Complete: bool; Subject: string; Revision: string; Intent: LifecycleIntent; Hold: LifecycleFact
      Dependency: LifecycleFact; Claim: LifecycleFact; PullRequest: LifecycleFact; Review: LifecycleFact
      Delivery: LifecycleFact; DeliveryProtected: bool; IssueState: LifecycleIssueState; Status: StatusSnapshot }
type LifecycleProjectionCost = { AuthorityReads: int; MaximumEffects: int }
type LifecycleProjectionPlan =
    { Subject: string; SourceRevision: string; Stage: DerivedLifecycleStage; StatusName: string
      StatusDecision: StatusPlanDecision; Seal: string; Cost: LifecycleProjectionCost }
type LifecycleProjectionRefusal =
    | InvalidLifecycleSubject | LifecycleObservationIncomplete | InvalidLifecycleRevision | LifecycleFactSubjectMismatch
    | InvalidLifecycleFactRevision | WrongLifecycleAuthority of string * LifecycleAuthority
    | LifecycleFactNotKnowledge of string * LifecycleFactOutcome
    | HistoricalLifecycleFact of string | ContradictoryLifecycleFacts of string | UnprotectedLifecycleDelivery
    | ClosedIssueWithoutTerminalAuthority | LifecycleStatusOptionMissing of string
    | LifecycleStatusPlanRefused of StatusPlanRefusal | AlteredLifecyclePlan
    | LifecycleStatusPreStateRefused of StatusPreStateRefusal | LifecycleStatusPostStateRefused of StatusPostStateRefusal

[<RequireQualifiedAccess>]
module LifecycleProjectionAdapter =
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)
    let private canonicalSubject (value: string) = if isNull value then "" else value.Trim().ToLowerInvariant()
    let private sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hashParts values = values |> List.map frame |> String.concat "" |> sha256
    let private outcomeName = function
        | FactObserved -> "observed" | FactProvenAbsent -> "proven-absent" | FactIncomplete -> "incomplete"
        | FactUnauthorized -> "unauthorized" | FactUnreadable -> "unreadable" | FactStale -> "stale"
        | FactContradictory -> "contradictory"
    let private stageName = function
        | StageBacklog -> "backlog" | StageReady -> "ready" | StagePaused -> "paused"
        | StageCancelled -> "cancelled" | StageBlocked -> "blocked" | StageClaimed -> "claimed"
        | StageInReview -> "in-review" | StageAccepted -> "accepted" | StageDelivered -> "delivered"
    let statusName = function
        | StageBacklog -> "Backlog" | StageReady -> "Ready" | StagePaused | StageBlocked -> "Blocked"
        | StageClaimed -> "In progress" | StageInReview | StageAccepted -> "In review"
        | StageCancelled | StageDelivered -> "Done"

    let private namedFacts (observation: LifecycleProjectionObservation) =
        [ "hold", HoldAuthority, observation.Hold
          "dependency", DependencyAuthority, observation.Dependency
          "claim", ClaimJournalAuthority, observation.Claim
          "pull-request", PullRequestAuthority, observation.PullRequest
          "review", ReviewJournalAuthority, observation.Review
          "delivery", DeliveryJournalAuthority, observation.Delivery ]

    let private knowledge name fact =
        match fact.Outcome with
        | FactObserved when fact.Current -> Ok true
        | FactProvenAbsent when fact.Current -> Ok false
        | (FactObserved | FactProvenAbsent) -> Error(HistoricalLifecycleFact name)
        | outcome -> Error(LifecycleFactNotKnowledge(name, outcome))

    let derive (observation: LifecycleProjectionObservation) =
        if obj.ReferenceEquals(observation, null) then Error [ LifecycleObservationIncomplete ]
        else
            let expectedSubject = canonicalSubject observation.Subject
            let facts = namedFacts observation
            let structural =
                [ if not observation.Complete then LifecycleObservationIncomplete
                  if not (validText observation.Subject) then InvalidLifecycleSubject
                  if not (validText observation.Revision) then InvalidLifecycleRevision
                  if facts |> List.exists (fun (_, _, fact) -> canonicalSubject fact.Subject <> expectedSubject) then LifecycleFactSubjectMismatch
                  if facts |> List.exists (fun (_, _, fact) -> not (validText fact.Revision) || fact.Revision <> observation.Revision) then InvalidLifecycleFactRevision
                  for name, expectedAuthority, fact in facts do
                      if fact.Authority <> expectedAuthority then WrongLifecycleAuthority(name, fact.Authority) ]
            if not structural.IsEmpty then Error structural
            else
                let results = facts |> List.map (fun (name, _, fact) -> name, knowledge name fact)
                let refusals = results |> List.choose (fun (_, result) -> match result with Error e -> Some e | Ok _ -> None)
                if not refusals.IsEmpty then Error refusals
                else
                    let present name = results |> List.find (fun (factName, _) -> factName = name) |> snd |> Result.defaultValue false
                    let hold = present "hold"
                    let dependency = present "dependency"
                    let claim = present "claim"
                    let pullRequest = present "pull-request"
                    let review = present "review"
                    let delivery = present "delivery"
                    let contradictions =
                        [ if review && not pullRequest && not delivery then ContradictoryLifecycleFacts "accepted review requires a current pull request until delivery"
                          if delivery && not review then ContradictoryLifecycleFacts "delivery requires accepted review authority"
                          if delivery && not observation.DeliveryProtected then UnprotectedLifecycleDelivery
                          if observation.IssueState = IssueClosed && observation.Intent <> IntentCancelled && not delivery then ClosedIssueWithoutTerminalAuthority ]
                    if not contradictions.IsEmpty then Error contradictions
                    else
                        let stage =
                            if observation.Intent = IntentCancelled then StageCancelled
                            elif delivery then StageDelivered
                            elif review then StageAccepted
                            elif hold || dependency then StageBlocked
                            elif pullRequest then StageInReview
                            elif claim then StageClaimed
                            else
                                match observation.Intent with
                                | IntentReady -> StageReady | IntentPaused -> StagePaused
                                | IntentBacklog -> StageBacklog | IntentCancelled -> StageCancelled
                        Ok stage

    let private optionParts (option: StatusOptionProjection) = [ LiveId.value option.Id; SemanticName.value option.Name ]
    let private snapshotParts (snapshot: StatusSnapshot) =
        [ snapshot.Revision; LiveId.value snapshot.ProjectId; LiveId.value snapshot.ItemId
          LiveId.value snapshot.FieldId; SemanticName.value snapshot.FieldName
          snapshot.SelectedOptionId |> Option.map LiveId.value |> Option.defaultValue "" ]
        @ (snapshot.Options |> List.collect optionParts)
    let private operationParts = function
        | SetStatusOperation option -> [ "set-status"; LiveId.value option ]
        | ClearStatusOperation -> [ "clear-status" ]
    let private intentParts = function
        | SetStatus option -> [ "set-status"; LiveId.value option ]
        | ClearStatus -> [ "clear-status" ]
    let private decisionParts = function
        | StatusPlanned value ->
            [ "planned"; value.CausationIdentity; value.IdempotencyIdentity ]
            @ snapshotParts value.Before
            @ operationParts value.Operation
        | StatusNoOp value ->
            [ "no-op"; value.ObservedRevision; value.IdempotencyIdentity ] @ intentParts value.Intent
    let private expectedCost = function
        | StatusPlanned _ -> { AuthorityReads = 8; MaximumEffects = 1 }
        | StatusNoOp _ -> { AuthorityReads = 8; MaximumEffects = 0 }
    let private seal observation stage statusNameValue decision cost =
        let facts =
            namedFacts observation
            |> List.collect (fun (name, authority, fact) -> [ name; string authority; fact.Revision; outcomeName fact.Outcome; string fact.Current ])
        hashParts ([ canonicalSubject observation.Subject; observation.Revision; stageName stage; statusNameValue
                     string cost.AuthorityReads; string cost.MaximumEffects ] @ decisionParts decision @ facts)

    let plan causationIdentity observation =
        match derive observation with
        | Error refusals -> Error refusals
        | Ok stage ->
            let desiredName = statusName stage
            match observation.Status.Options |> List.tryFind (fun option -> SemanticName.value option.Name = desiredName) with
            | None -> Error [ LifecycleStatusOptionMissing desiredName ]
            | Some option ->
                match ProjectAdapter.planStatus observation.Status.Revision causationIdentity (SetStatus option.Id) observation.Status with
                | Error refusal -> Error [ LifecycleStatusPlanRefused refusal ]
                | Ok decision ->
                    let cost = expectedCost decision
                    Ok
                        { Subject = canonicalSubject observation.Subject; SourceRevision = observation.Revision; Stage = stage
                          StatusName = desiredName; StatusDecision = decision; Seal = seal observation stage desiredName decision cost
                          Cost = cost }

    let private validatePlan plan observation =
        match derive observation with
        | Error refusals -> Error refusals
        | Ok stage ->
            let cost = expectedCost plan.StatusDecision
            if canonicalSubject observation.Subject <> plan.Subject
               || observation.Revision <> plan.SourceRevision
               || stage <> plan.Stage
               || statusName stage <> plan.StatusName
               || cost <> plan.Cost
               || seal observation stage plan.StatusName plan.StatusDecision cost <> plan.Seal
            then Error [ AlteredLifecyclePlan ]
            else Ok stage

    let authorize plan observation statusObservation =
        match validatePlan plan observation with
        | Error refusals -> Error refusals
        | Ok _ ->
            match plan.StatusDecision with
            | StatusPlanned statusPlan ->
                match ProjectAdapter.checkStatusPreState statusPlan statusObservation with
                | Ok snapshot -> Ok snapshot
                | Error refusal -> Error [ LifecycleStatusPreStateRefused refusal ]
            | StatusNoOp receipt ->
                match ProjectAdapter.readStatus observation.Status.ProjectId observation.Status.ItemId statusObservation with
                | Ok snapshot when snapshot = observation.Status && snapshot.Revision = receipt.ObservedRevision -> Ok snapshot
                | Ok _ -> Error [ AlteredLifecyclePlan ]
                | Error refusal -> Error [ LifecycleStatusPreStateRefused(StatusPreStateReadRefused refusal) ]

    let verify expectedResultRevision plan observation statusObservation =
        match validatePlan plan observation with
        | Error refusals -> Error refusals
        | Ok _ ->
            match plan.StatusDecision with
            | StatusNoOp _ -> Error [ AlteredLifecyclePlan ]
            | StatusPlanned statusPlan ->
                match ProjectAdapter.verifyStatusPostState expectedResultRevision statusPlan statusObservation with
                | Ok snapshot -> Ok snapshot
                | Error refusal -> Error [ LifecycleStatusPostStateRefused refusal ]
