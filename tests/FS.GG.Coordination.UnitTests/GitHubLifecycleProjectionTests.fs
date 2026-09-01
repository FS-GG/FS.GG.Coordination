module FS.GG.Coordination.GitHubLifecycleProjectionTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private live value = LiveId.tryCreate value |> Result.defaultWith failwith
let private name value = SemanticName.tryCreate value |> Result.defaultWith failwith
let private subject = "FS-GG/Repo#42"
let private fact authority outcome = { Subject = subject; Revision = "lifecycle-1"; Authority = authority; Outcome = outcome; Current = true }
let private hold outcome = fact HoldAuthority outcome
let private dependency outcome = fact DependencyAuthority outcome
let private claim outcome = fact ClaimJournalAuthority outcome
let private pullRequest outcome = fact PullRequestAuthority outcome
let private review outcome = fact ReviewJournalAuthority outcome
let private delivery outcome = fact DeliveryJournalAuthority outcome
let private options: StatusOptionProjection list =
    [ "Backlog"; "Ready"; "Blocked"; "In progress"; "In review"; "Done" ]
    |> List.mapi (fun index value -> { Id = live $"OPT_{index}"; Name = name value })
let private status =
    { Revision = "status-1"; Nature = ProjectionOnly; ProjectId = live "PROJECT_1"; ItemId = live "ITEM_1"
      FieldId = live "FIELD_1"; FieldName = name "Status"; Options = options; SelectedOptionId = Some options[0].Id }
let private observation =
    { Complete = true; Subject = subject; Revision = "lifecycle-1"; Intent = IntentBacklog; Hold = hold FactProvenAbsent
      Dependency = dependency FactProvenAbsent; Claim = claim FactProvenAbsent; PullRequest = pullRequest FactProvenAbsent
      Review = review FactProvenAbsent; Delivery = delivery FactProvenAbsent
      DeliveryProtected = false; IssueState = IssueOpen; Status = status }
let private statusObservation snapshot =
    let field =
        { ProjectId = snapshot.ProjectId; ItemId = snapshot.ItemId; FieldId = snapshot.FieldId
          FieldName = snapshot.FieldName; Options = snapshot.Options; SelectedOptionId = snapshot.SelectedOptionId }
    StatusComplete(snapshot.Revision, { PageCount = 1; NodeCount = 1; TerminalPage = true }, [ field ])

[<Fact>]
let ``formal lifecycle precedence is preserved and status remains a projection`` () =
    let cases =
        [ observation, StageBacklog, "Backlog"
          { observation with Intent = IntentReady }, StageReady, "Ready"
          { observation with Intent = IntentPaused; Hold = hold FactObserved }, StageBlocked, "Blocked"
          { observation with Intent = IntentReady; Claim = claim FactObserved }, StageClaimed, "In progress"
          { observation with Intent = IntentReady; PullRequest = pullRequest FactObserved }, StageInReview, "In review"
          { observation with Intent = IntentReady; PullRequest = pullRequest FactObserved; Review = review FactObserved }, StageAccepted, "In review"
          { observation with Intent = IntentReady; PullRequest = pullRequest FactObserved; Review = review FactObserved; Delivery = delivery FactObserved; DeliveryProtected = true; IssueState = IssueClosed }, StageDelivered, "Done"
          { observation with Intent = IntentCancelled; IssueState = IssueClosed }, StageCancelled, "Done" ]
    for value, expectedStage, expectedStatus in cases do
        let stage = LifecycleProjectionAdapter.derive value |> Result.defaultWith (failwithf "%A")
        Assert.Equal(expectedStage, stage)
        Assert.Equal(expectedStatus, LifecycleProjectionAdapter.statusName stage)
    let forgedStatus = { observation with Status = { status with SelectedOptionId = Some options[5].Id } }
    Assert.Equal(Ok StageBacklog, LifecycleProjectionAdapter.derive forgedStatus)

[<Fact>]
let ``unknown historical contradictory and unprotected facts fail closed`` () =
    Assert.Equal(Error [ LifecycleFactNotKnowledge("claim", FactUnreadable) ], LifecycleProjectionAdapter.derive { observation with Claim = claim FactUnreadable })
    Assert.Equal(Error [ HistoricalLifecycleFact "claim" ], LifecycleProjectionAdapter.derive { observation with Claim = { claim FactObserved with Current = false } })
    Assert.Equal(Error [ WrongLifecycleAuthority("claim", ReviewJournalAuthority) ], LifecycleProjectionAdapter.derive { observation with Claim = review FactObserved })
    Assert.Equal(Error [ InvalidLifecycleFactRevision ], LifecycleProjectionAdapter.derive { observation with Claim = { claim FactObserved with Revision = "older" } })
    Assert.Equal(Error [ ContradictoryLifecycleFacts "accepted review requires a current pull request until delivery" ], LifecycleProjectionAdapter.derive { observation with Review = review FactObserved })
    Assert.Equal(Error [ UnprotectedLifecycleDelivery ], LifecycleProjectionAdapter.derive { observation with Review = review FactObserved; Delivery = delivery FactObserved })
    Assert.Equal(Error [ ClosedIssueWithoutTerminalAuthority ], LifecycleProjectionAdapter.derive { observation with IssueState = IssueClosed })

[<Fact>]
let ``exact plan fences source and status revisions and verifies authoritative poststate`` () =
    let requested = { observation with Intent = IntentReady }
    let plan = LifecycleProjectionAdapter.plan "cause-1" requested |> Result.defaultWith (failwithf "%A")
    Assert.Equal(StageReady, plan.Stage)
    Assert.Equal(8, plan.Cost.AuthorityReads)
    Assert.Equal(1, plan.Cost.MaximumEffects)
    Assert.True(LifecycleProjectionAdapter.authorize plan requested (statusObservation status) |> Result.isOk)
    Assert.Equal(Error [ AlteredLifecyclePlan ], LifecycleProjectionAdapter.authorize plan { requested with Intent = IntentBacklog } (statusObservation status))
    let stale = { status with Revision = "status-2" }
    match LifecycleProjectionAdapter.authorize plan requested (statusObservation stale) with
    | Error [ LifecycleStatusPreStateRefused(StatusReReadRequired("status-1", "status-2")) ] -> ()
    | value -> failwithf "%A" value
    let applied = { status with Revision = "status-2"; SelectedOptionId = Some options[1].Id }
    Assert.True(LifecycleProjectionAdapter.verify "status-2" plan (statusObservation applied) |> Result.isOk)

[<Fact>]
let ``exact equality is a zero-effect replay and qualification inventory is closed`` () =
    let current = { observation with Status = { status with SelectedOptionId = Some options[0].Id } }
    let first = LifecycleProjectionAdapter.plan "cause-1" current |> Result.defaultWith (failwithf "%A")
    Assert.Equal(0, first.Cost.MaximumEffects)
    match first.StatusDecision with StatusNoOp _ -> () | _ -> failwith "expected no-op"
    let again = LifecycleProjectionAdapter.plan "cause-1" current |> Result.defaultWith (failwithf "%A")
    Assert.Equal(first.Seal, again.Seal)
    let passing: GitHubLifecycleProjectionControlResult list =
        GitHubLifecycleProjectionQualification.requiredControls
        |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok(), GitHubLifecycleProjectionQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index value -> if index = 11 then { value with MutationRed = false } else value)
    match GitHubLifecycleProjectionQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun value -> value.ControlId = "historical-fact")
    | Ok() -> failwith "historical fact mutation authorized"
