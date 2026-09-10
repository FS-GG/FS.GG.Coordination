module FS.GG.Coordination.Orchestration.Observer.Tests.ObserverTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Orchestration.Observer

let now = DateTimeOffset.Parse("2026-09-10T10:00:00Z")
let sha character = String.replicate 64 character
let projectId = Id.project(Guid.Parse "10000000-0000-0000-0000-000000000001")
let sessionId = Id.session(Guid.Parse "20000000-0000-0000-0000-000000000002")
let attemptId = Id.attempt(Guid.Parse "30000000-0000-0000-0000-000000000003")
let proposalId = ProposalId.create(Guid.Parse "40000000-0000-0000-0000-000000000004")
let workItem = WorkItemIdentity.create "R_kgDOExample" 123L "I_kwDOExample" 42L
let budget = { TokenLimit = 100L; RuntimeSecondsLimit = 60L; CostMicrosLimit = 1_000L; Deadline = now.AddHours 1. }
let usage tokens seconds cost = { Tokens = tokens; RuntimeSeconds = seconds; CostMicros = cost }
let provenance = { Provider = "github-graphql"; QuerySha256 = sha "a"; EvidenceSha256 = sha "b"; CapturedAt = now.AddMinutes -2. }
let readback = { Provider = "github-graphql"; SourceRevision = "provider-rev"; EvidenceSha256 = sha "8"; ObservedAt = now }
let observation revision captured =
    let draft =
        { ProjectId = projectId; SourceRevision = revision; WorkflowRevision = Id.revision 7L; Generation = Id.generation 3L
          ObservationSha256 = sha "0"; Provenance = { provenance with CapturedAt = captured }; WorkItems = [ { Identity = workItem; MembershipItemId = "PVTI_item"; Archived = false } ]; NonWorkItemCount = 0 }
    { draft with ObservationSha256 = Observer.observationSha256 draft }

let envelope (state: ObserverState) command =
    { CommandId = Id.command(Guid.NewGuid()); ExpectedSequence = state.Sequence; PrincipalId = "planner-owner"; IssuedAt = now.AddSeconds -1.; ExpiresAt = now.AddMinutes 5.; Command = command }

let decide state command = Observer.decide now state (envelope state command)
let apply state command =
    let decision = decide state command
    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    decision.Events |> List.fold Observer.evolve state

let opened () = apply Observer.initial (OpenSession(sessionId, projectId, budget))
let observed () = apply (opened()) (RecordProjectObservation(observation "project-rev-1" (now.AddMinutes -1.)))

let proposalInput observationSha =
    { ProposalId = proposalId; AttemptId = attemptId; ObservationSha256 = observationSha; WorkflowRevision = Id.revision 7L; Generation = Id.generation 3L
      Scope = "repo:123/issues"; NarrativeSha256 = sha "d"; Actions = [ { Kind = InspectWorkItem; WorkItem = workItem; ParametersSha256 = sha "e" } ]; ProposedAt = now }

let approvedState () =
    let state0 = observed()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now.AddSeconds -5.))
    let state2 = apply state1 (CompletePlanningAttempt(attemptId, usage 5L 5L 5L, proposalInput state1.Observation.Value.ObservationSha256))
    let proposal = state2.Proposals[proposalId]
    let commandId = Id.command(Guid.Parse "50000000-0000-0000-0000-000000000005")
    let approval =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = Observer.budgetSha256 budget; PrincipalId = "planner-owner"; Scope = proposal.Scope
          ExpectedWorkflowRevision = proposal.WorkflowRevision; ExpectedGeneration = proposal.Generation; CommandId = commandId; CommandBodySha256 = sha "f"; Kind = ExplicitApproval; ApprovedAt = now }
    apply state2 (ApproveProposal approval), commandId

[<Fact>]
let ``complete typed lifecycle keeps every presentation stage distinct`` () =
    let state0 = observed()
    let state1 = apply state0 (RecordConversation { EntryId = Guid.NewGuid(); Role = Operator; BodySha256 = sha "1"; RecordedAt = now.AddMinutes -1. })
    let state2 = apply state1 (StartPlanningAttempt(attemptId, usage 40L 20L 400L, now.AddSeconds -5.))
    let input = proposalInput state2.Observation.Value.ObservationSha256
    let state3 = apply state2 (CompletePlanningAttempt(attemptId, usage 25L 10L 250L, input))
    let proposal = state3.Proposals[proposalId]
    let commandId = Id.command(Guid.Parse "50000000-0000-0000-0000-000000000005")
    let approvalInput =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = Observer.budgetSha256 budget; PrincipalId = "planner-owner"; Scope = proposal.Scope
          ExpectedWorkflowRevision = Id.revision 7L; ExpectedGeneration = Id.generation 3L; CommandId = commandId; CommandBodySha256 = sha "f"; Kind = ExplicitApproval; ApprovedAt = now }
    let state4 = apply state3 (ApproveProposal approvalInput)
    let approval = state4.Approvals[proposalId]
    let receipt = { CommandId = commandId; BodySha256 = sha "f"; Disposition = ReceiptDisposition.Accepted; Revision = Id.revision 8L; ProtocolVersion = Id.protocolVersion 1 0; Detail = "accepted" }
    let state5 = apply state4 (RecordCommandAcceptance { ProposalId = proposalId; ApprovalSha256 = approval.ApprovalSha256; Receipt = receipt; Provenance = readback; AcceptedAt = now })
    let state6 = apply state5 (RecordEffectCompletion { CommandId = commandId; OperationId = Id.operation(Guid.NewGuid()); Result = EffectApplied "provider-rev"; Provenance = readback; CompletedAt = now })
    let view = ObserverProjection.render state6
    Assert.Equal<ProjectionStage list>([ Conversation; Proposed; Approved; DurableAcceptance; EffectComplete ], view.Rows |> List.map _.Stage)
    Assert.Equal(Some(usage 75L 50L 750L), view.Remaining)
    Assert.Equal(view, ObserverProjection.fromRecovery { Events = []; State = state6 })

[<Fact>]
let ``stale planning output cannot replace newer observation`` () =
    let state0 = observed()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 40L 20L 400L, now.AddSeconds -5.))
    let stale = proposalInput state1.Observation.Value.ObservationSha256
    let state2 = apply state1 (RecordProjectObservation(observation "project-rev-2" now))
    let refused = decide state2 (CompletePlanningAttempt(attemptId, usage 1L 1L 1L, stale))
    Assert.Equal(ObserverRejected, refused.Receipt.Disposition)
    Assert.Equal("stale-or-invalid-proposal", refused.Receipt.Detail)
    Assert.Empty(state2.Proposals)

[<Fact>]
let ``agent death retains reservation until explicit conservative consumption`` () =
    let state0 = observed()
    let reservation = usage 40L 20L 400L
    let state1 = apply state0 (StartPlanningAttempt(attemptId, reservation, now))
    let state2 = apply state1 (MarkPlanningAttemptUnknown(attemptId, "worker-disconnected"))
    Assert.Equal(reservation, state2.Reserved)
    Assert.Equal(usage 0L 0L 0L, state2.Used)
    let denied = decide state2 (StartPlanningAttempt(Id.attempt(Guid.NewGuid()), usage 1L 1L 1L, now))
    Assert.Equal("planning-attempt-not-authorized", denied.Receipt.Detail)
    let state3 = apply state2 (ConsumeUnknownPlanningReservation(attemptId, "usage-unobservable"))
    Assert.Equal(reservation, state3.Used)
    Assert.Equal(usage 0L 0L 0L, state3.Reserved)

[<Fact>]
let ``budget deadline capacity and overflow refuse before planning`` () =
    let state0 = observed()
    let over = decide state0 (StartPlanningAttempt(attemptId, usage 101L 1L 1L, now))
    Assert.Equal("planning-attempt-not-authorized", over.Receipt.Detail)
    let expiredBudget = { budget with Deadline = now }
    let opening = decide Observer.initial (OpenSession(sessionId, projectId, expiredBudget))
    Assert.Equal("invalid-planning-budget", opening.Receipt.Detail)
    let huge = { budget with TokenLimit = Int64.MaxValue }
    let hugeState = apply Observer.initial (OpenSession(sessionId, projectId, huge)) |> fun state -> apply state (RecordProjectObservation(observation "r" now))
    let started = apply hugeState (StartPlanningAttempt(attemptId, usage Int64.MaxValue 1L 1L, now))
    let overflow = decide started (StartPlanningAttempt(Id.attempt(Guid.NewGuid()), usage 1L 0L 0L, now))
    Assert.Equal("planning-attempt-not-authorized", overflow.Receipt.Detail)
    let hugeRuntimeBudget = { budget with RuntimeSecondsLimit = Int64.MaxValue }
    let runtimeState = apply Observer.initial (OpenSession(sessionId, projectId, hugeRuntimeBudget)) |> fun state -> apply state (RecordProjectObservation(observation "runtime" now))
    let runtimeOverflow = decide runtimeState (StartPlanningAttempt(attemptId, usage 0L Int64.MaxValue 0L, now))
    Assert.Equal("planning-attempt-not-authorized", runtimeOverflow.Receipt.Detail)

[<Fact>]
let ``approval binds exact proposal budget principal scope revision generation and command body`` () =
    let state0 = observed()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now))
    let state2 = apply state1 (CompletePlanningAttempt(attemptId, usage 5L 5L 5L, proposalInput state1.Observation.Value.ObservationSha256))
    let proposal = state2.Proposals[proposalId]
    let baseline =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = Observer.budgetSha256 budget; PrincipalId = "planner-owner"; Scope = proposal.Scope
          ExpectedWorkflowRevision = Id.revision 7L; ExpectedGeneration = Id.generation 3L; CommandId = Id.command(Guid.NewGuid()); CommandBodySha256 = sha "f"; Kind = ExplicitApproval; ApprovedAt = now }
    for altered in [ { baseline with PlanSha256 = sha "0" }; { baseline with PlanningBudgetSha256 = sha "0" }; { baseline with Scope = "other" }; { baseline with ExpectedGeneration = Id.generation 4L } ] do
        Assert.Equal("stale-or-unbound-approval", (decide state2 (ApproveProposal altered)).Receipt.Detail)

[<Fact>]
let ``command digest binds complete authority envelope`` () =
    let state = opened()
    let original = envelope state (RecordConversation { EntryId = Guid.NewGuid(); Role = Operator; BodySha256 = sha "1"; RecordedAt = now })
    let digest = Observer.commandSha256 original
    Assert.NotEqual<string>(digest, Observer.commandSha256 { original with PrincipalId = "other" })
    Assert.NotEqual<string>(digest, Observer.commandSha256 { original with ExpectedSequence = original.ExpectedSequence + 1L })
    Assert.NotEqual<string>(digest, Observer.commandSha256 { original with ExpiresAt = original.ExpiresAt.AddSeconds 1. })
    let nullPrincipal = { original with PrincipalId = null }
    Assert.Equal("invalid-or-expired-observer-command", (Observer.decide now state nullPrincipal).Receipt.Detail)

[<Fact>]
let ``observation digest is derived and authority regression refuses`` () =
    let state = opened()
    let valid = observation "project-rev-1" (now.AddMinutes -1.)
    let altered = { valid with ObservationSha256 = sha "0" }
    Assert.Equal("invalid-project-observation", (decide state (RecordProjectObservation altered)).Receipt.Detail)
    let accepted = apply state (RecordProjectObservation valid)
    let regressedDraft = { valid with SourceRevision = "newer-provider-read"; Generation = Id.generation 2L; Provenance = { valid.Provenance with CapturedAt = now } }
    let regressed = { regressedDraft with ObservationSha256 = Observer.observationSha256 regressedDraft }
    Assert.Equal("stale-project-observation", (decide accepted (RecordProjectObservation regressed)).Receipt.Detail)

[<Fact>]
let ``proposal actions must name observed work and approval principal and live budget`` () =
    let state0 = observed()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now))
    let foreign = WorkItemIdentity.create "R_other" 999L "I_other" 1L
    let baselineProposal = proposalInput state1.Observation.Value.ObservationSha256
    let invalidInput = { baselineProposal with Actions = [ { Kind = RecommendRoutineWork; WorkItem = foreign; ParametersSha256 = sha "e" } ] }
    let invalidProposalDecision = decide state1 (CompletePlanningAttempt(attemptId, usage 1L 1L 1L, invalidInput))
    Assert.Equal("stale-or-invalid-proposal", invalidProposalDecision.Receipt.Detail)
    let validInput = proposalInput state1.Observation.Value.ObservationSha256
    let state2 = apply state1 (CompletePlanningAttempt(attemptId, usage 1L 1L 1L, validInput))
    let proposal = state2.Proposals[proposalId]
    let input =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = Observer.budgetSha256 budget; PrincipalId = "different-principal"; Scope = proposal.Scope
          ExpectedWorkflowRevision = proposal.WorkflowRevision; ExpectedGeneration = proposal.Generation; CommandId = Id.command(Guid.NewGuid()); CommandBodySha256 = sha "f"; Kind = ExplicitApproval; ApprovedAt = now }
    Assert.Equal("stale-or-unbound-approval", (decide state2 (ApproveProposal input)).Receipt.Detail)
    let later = budget.Deadline.AddSeconds 1.
    let approvalEnvelope = envelope state2 (ApproveProposal { input with PrincipalId = "planner-owner"; ApprovedAt = later })
    let laterEnvelope = { approvalEnvelope with IssuedAt = later; ExpiresAt = later.AddMinutes 1. }
    Assert.Equal("stale-or-unbound-approval", (Observer.decide later state2 laterEnvelope).Receipt.Detail)

[<Fact>]
let ``event codec roundtrip replays private identities and durable budget`` () =
    let state = observed()
    let events = [ SessionOpened(sessionId, projectId, budget); ProjectObservationRecorded(state.Observation.Value) ]
    let decoded =
        events
        |> List.map (ObserverEventCodec.encode >> ObserverEventCodec.tryDecode)
        |> List.map (function Ok value -> value | Error error -> failwith error)
    Assert.Equal<ObserverEvent list>(events, decoded)
    Assert.Equal(Observer.replay events, state)

[<Fact>]
let ``incomplete project observation cannot erase or invent stable identity`` () =
    let refusal = ProjectObservationBridge.observe projectId (Id.revision 1L) (Id.generation 1L) provenance [] (ProjectIncomplete("page lost", Some "cursor"))
    match refusal with Error(ProjectReadRefused(ProjectObservationRefused(ObservationIncomplete("page lost", Some "cursor")))) -> () | other -> failwithf "unexpected %A" other

[<Fact>]
let ``complete bridge requires immutable repository database and issue readback`` () =
    let live value = LiveId.tryCreate value |> Result.defaultWith failwith
    let repo = { Owner = "FS-GG"; Name = "FS.GG.Coordination" }
    let content = live "I_kwDOExample"
    let item = { ProjectId = live "PVT_project"; ItemId = live "PVTI_item"; Content = RepositoryIssue(repo, 42, content); Archived = false }
    let project = ProjectComplete("revision", [ { Number = 1; Items = [ item ]; TerminalPage = true } ])
    let missing = ProjectObservationBridge.observe projectId (Id.revision 1L) (Id.generation 1L) provenance [] project
    Assert.Equal(Error(MissingImmutableIssueIdentity content), missing)
    let fact = { Repository = repo; RepositoryNodeId = "R_kgDOExample"; RepositoryDatabaseId = 123L; ContentNodeId = content; IssueNodeId = "I_kwDOExample"; IssueNumber = 42L }
    let accepted = ProjectObservationBridge.observe projectId (Id.revision 1L) (Id.generation 1L) provenance [ fact ] project |> Result.defaultWith (sprintf "%A" >> failwith)
    Assert.Equal(WorkItemIdentity.persistenceId workItem, WorkItemIdentity.persistenceId accepted.WorkItems.Head.Identity)

type private ReadCapability() =
    interface IProjectReadCapability with member _.ObserveProject(_, _) = Task.FromResult(Error InvalidObservationProvenance)
type private PlanningCapability() =
    interface IBoundedPlanningCapability with member _.CreateProposal(_, _) = Task.FromResult(Error "not-run")
type private ReadbackCapability() =
    interface ICommandReadbackCapability with
        member _.ReadCommandAcceptance(_, _) = Task.FromResult(Error "not-run")
        member _.ReadEffectCompletions(_, _) = Task.FromResult(Error "not-run")
type private FixedReadbackCapability(acceptance: DurableCommandAcceptance, effects: DurableEffectCompletion list) =
    interface ICommandReadbackCapability with
        member _.ReadCommandAcceptance(_, _) = Task.FromResult(Ok acceptance)
        member _.ReadEffectCompletions(_, _) = Task.FromResult(Ok effects)
type private Journal() =
    interface IObserverJournalStore with
        member _.AppendObserver(_, _) = Task.FromResult(ObserverInvalidAppend "not-run")
        member _.RecoverObserver(_, _) = Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

[<Fact>]
let ``composition exposes only read planning and observer journal capabilities`` () =
    let composition = ObserverComposition.create (ReadCapability()) (PlanningCapability()) (ReadbackCapability()) (Journal())
    Assert.Equal<string list>([ "project-read"; "bounded-planning"; "command-readback"; "observer-journal" ], ObserverComposition.capabilities composition)
    let publicConstructors = typeof<ObserverComposition>.Assembly.GetExportedTypes() |> Array.collect _.GetConstructors()
    let parameters = publicConstructors |> Array.collect _.GetParameters() |> Array.map (fun value -> value.ParameterType.FullName)
    Assert.DoesNotContain(parameters, fun value -> not (isNull value) && (value.Contains("EffectIntent") || value.Contains("Runner") || value.Contains("Mutation")))

type private RecordingJournal(outcomes: ObserverAppendOutcome list) =
    let requests = ResizeArray<ObserverAppendRequest>()
    let mutable remaining = outcomes
    member _.Requests = List.ofSeq requests
    interface IObserverJournalStore with
        member _.AppendObserver(request, _) =
            requests.Add request
            match remaining with
            | head :: tail -> remaining <- tail; Task.FromResult head
            | [] -> Task.FromResult(ObserverAppendUnavailable "unexpected-append")
        member _.RecoverObserver(_, _) = Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

type private RecordingPlanner(beforeLaunch: unit -> unit, result: Result<PlanningResult, string>) =
    let mutable calls = 0
    member _.Calls = calls
    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) =
            beforeLaunch()
            calls <- calls + 1
            Task.FromResult result

type private ManualTimeProvider(initial: DateTimeOffset) =
    inherit TimeProvider()
    let mutable current = initial
    override _.GetUtcNow() = current
    member _.Advance(duration: TimeSpan) = current <- current.Add duration

type private DelayedReservationJournal(clock: ManualTimeProvider) =
    let requests = ResizeArray<ObserverAppendRequest>()
    interface IObserverJournalStore with
        member _.AppendObserver(request, _) =
            requests.Add request
            if requests.Count = 1 then clock.Advance(TimeSpan.FromSeconds 2.)
            Task.FromResult(ObserverAppended(request.Events |> List.last |> _.Sequence))
        member _.RecoverObserver(_, _) = Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

type private NonCooperativePlanner() =
    let pending = TaskCompletionSource<Result<PlanningResult, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) = pending.Task

type private SynchronouslyBlockingPlanner(entered: ManualResetEventSlim, release: ManualResetEventSlim) =
    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) =
            entered.Set()
            release.Wait()
            Task.FromResult(Error "released-after-observer-timeout")

let executionRequest state =
    { ObserverId = ObserverJournal.observerId sessionId; State = state; AttemptId = attemptId; Reservation = usage 10L 10L 10L
      StartCommandId = Id.command(Guid.NewGuid()); CompletionCommandId = Id.command(Guid.NewGuid()); UnknownCommandId = Id.command(Guid.NewGuid())
      PrincipalId = "planner-owner"; IssuedAt = now.AddSeconds -1.; ExpiresAt = now.AddMinutes 1. }

[<Fact>]
let ``runtime persists reservation before invoking bounded planner and persists result before return`` () = task {
    let state = observed()
    let journal = RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 3L) ])
    let input = proposalInput state.Observation.Value.ObservationSha256
    let planner = RecordingPlanner((fun () -> Assert.Single(journal.Requests) |> ignore), Ok { Proposal = input; ActualUse = usage 5L 5L 5L })
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let! outcome = ObserverRuntime.executePlanning (ManualTimeProvider now) composition (executionRequest state) CancellationToken.None
    match outcome with PlanningResultPersisted(finalState, proposal) -> Assert.Equal(3L, finalState.Sequence - state.Sequence); Assert.Equal(proposalId, proposal.ProposalId) | other -> failwithf "unexpected %A" other
    Assert.Equal(2, journal.Requests.Length)
    Assert.Equal(1, planner.Calls)
}

[<Fact>]
let ``duplicate reservation never relaunches planning agent`` () = task {
    let state = observed()
    let journal = RecordingJournal([ ObserverDuplicate(state.Sequence + 1L) ])
    let planner = RecordingPlanner((fun () -> ()), Error "must-not-run")
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let! outcome = ObserverRuntime.executePlanning (ManualTimeProvider now) composition (executionRequest state) CancellationToken.None
    match outcome with PlanningAlreadyRecorded _ -> () | other -> failwithf "unexpected %A" other
    Assert.Equal(0, planner.Calls)
}

[<Fact>]
let ``planner loss persists unknown state without releasing reservation`` () = task {
    let state = observed()
    let journal = RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])
    let planner = RecordingPlanner((fun () -> Assert.Single(journal.Requests) |> ignore), Error "agent-died")
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let! outcome = ObserverRuntime.executePlanning (ManualTimeProvider now) composition (executionRequest state) CancellationToken.None
    match outcome with
    | PlanningOutcomePersistedUnknown(finalState, "agent-died") ->
        Assert.Equal(usage 10L 10L 10L, finalState.Reserved)
        Assert.Equal(PlanningOutcomeUnknown "agent-died", finalState.Attempts[attemptId].Status)
    | other -> failwithf "unexpected %A" other
}

[<Fact>]
let ``runtime reads fresh time after a delayed successful planner`` () = task {
    let state = observed()
    let clock = ManualTimeProvider now
    let journal = RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 3L) ])
    let input = proposalInput state.Observation.Value.ObservationSha256
    let planner = RecordingPlanner((fun () -> clock.Advance(TimeSpan.FromSeconds 1.)), Ok { Proposal = input; ActualUse = usage 1L 1L 1L })
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let! outcome = ObserverRuntime.executePlanning clock composition (executionRequest state) CancellationToken.None
    match outcome with PlanningResultPersisted _ -> () | other -> failwithf "unexpected %A" other
    let completionEnvelope = journal.Requests[1].Command
    Assert.Equal(now.AddSeconds 1., completionEnvelope.IssuedAt)
}

[<Fact>]
let ``result after command expiry becomes durable unknown and retains reservation`` () = task {
    let state = observed()
    let clock = ManualTimeProvider now
    let journal = RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])
    let input = proposalInput state.Observation.Value.ObservationSha256
    let planner = RecordingPlanner((fun () -> clock.Advance(TimeSpan.FromSeconds 2.)), Ok { Proposal = input; ActualUse = usage 1L 1L 1L })
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let request = { executionRequest state with ExpiresAt = now.AddSeconds 1. }
    let! outcome = ObserverRuntime.executePlanning clock composition request CancellationToken.None
    match outcome with
    | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") -> Assert.Equal(request.Reservation, finalState.Reserved)
    | other -> failwithf "unexpected %A" other
}

[<Fact>]
let ``reservation append consuming deadline prevents planner launch`` () = task {
    let state = observed()
    let clock = ManualTimeProvider now
    let journal = DelayedReservationJournal(clock)
    let input = proposalInput state.Observation.Value.ObservationSha256
    let planner = RecordingPlanner((fun () -> ()), Ok { Proposal = input; ActualUse = usage 1L 1L 1L })
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let request = { executionRequest state with ExpiresAt = now.AddSeconds 1. }
    let! outcome = ObserverRuntime.executePlanning clock composition request CancellationToken.None
    match outcome with
    | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") -> Assert.Equal(request.Reservation, finalState.Reserved)
    | other -> failwithf "unexpected %A" other
    Assert.Equal(0, planner.Calls)
}

[<Fact>]
let ``noncooperative planner is bounded by command expiry and cannot release reservation`` () = task {
    let state = observed()
    let current = TimeProvider.System.GetUtcNow()
    let journal = RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])
    let planner = NonCooperativePlanner()
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let request = { executionRequest state with IssuedAt = current.AddMilliseconds -10.; ExpiresAt = current.AddMilliseconds 75. }
    let! outcome = ObserverRuntime.executePlanning TimeProvider.System composition request CancellationToken.None
    match outcome with
    | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") -> Assert.Equal(request.Reservation, finalState.Reserved)
    | other -> failwithf "unexpected %A" other
}

[<Fact>]
let ``synchronously blocking planner entry is bounded off the observer caller`` () = task {
    let state = observed()
    let current = TimeProvider.System.GetUtcNow()
    let journal = RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let planner = SynchronouslyBlockingPlanner(entered, release)
    let composition = ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
    let request = { executionRequest state with IssuedAt = current.AddMilliseconds -10.; ExpiresAt = current.AddMilliseconds 100. }
    try
        let! outcome = ObserverRuntime.executePlanning TimeProvider.System composition request CancellationToken.None
        Assert.True(entered.IsSet)
        match outcome with
        | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") -> Assert.Equal(request.Reservation, finalState.Reserved)
        | other -> failwithf "unexpected %A" other
    finally
        release.Set()
}

[<Fact>]
let ``authoritative projection records acceptance and effect only through bound readback`` () = task {
    let approved, commandId = approvedState()
    let approval = approved.Approvals[proposalId]
    let acceptance =
        { ProposalId = proposalId
          ApprovalSha256 = approval.ApprovalSha256
          Receipt = { CommandId = commandId; BodySha256 = sha "f"; Disposition = ReceiptDisposition.Accepted; Revision = Id.revision 8L; ProtocolVersion = Id.protocolVersion 1 0; Detail = "accepted" }
          Provenance = readback
          AcceptedAt = now }
    let operationId = Id.operation(Guid.Parse "60000000-0000-0000-0000-000000000006")
    let completion = { CommandId = commandId; OperationId = operationId; Result = EffectApplied "provider-rev"; Provenance = readback; CompletedAt = now }
    let journal = RecordingJournal([ ObserverAppended(approved.Sequence + 1L); ObserverAppended(approved.Sequence + 2L) ])
    let capability = FixedReadbackCapability(acceptance, [ completion ])
    let composition = ObserverComposition.create (ReadCapability()) (PlanningCapability()) capability journal
    let acceptanceRequest =
        { ObserverId = ObserverJournal.observerId sessionId; State = approved; ProposalId = proposalId; PersistenceCommandId = Id.command(Guid.NewGuid())
          PrincipalId = "observer-reader"; IssuedAt = now.AddSeconds -1.; ExpiresAt = now.AddMinutes 1. }
    let! acceptanceOutcome = ObserverRuntime.refreshCommandAcceptance (ManualTimeProvider now) composition acceptanceRequest CancellationToken.None
    let accepted = match acceptanceOutcome with CommandAcceptanceRefreshed state -> state | other -> failwithf "unexpected %A" other
    let effectRequest =
        { ObserverId = ObserverJournal.observerId sessionId; State = accepted; AcceptedCommandId = commandId; OperationId = operationId
          PersistenceCommandId = Id.command(Guid.NewGuid()); PrincipalId = "observer-reader"; IssuedAt = now.AddSeconds -1.; ExpiresAt = now.AddMinutes 1. }
    let! effectOutcome = ObserverRuntime.refreshEffectCompletion (ManualTimeProvider now) composition effectRequest CancellationToken.None
    let completed = match effectOutcome with EffectCompletionRefreshed state -> state | other -> failwithf "unexpected %A" other
    let authoritative = ObserverProjection.fromRecovery { Events = []; State = completed }
    Assert.Equal<ProjectionStage list>([ Proposed; Approved; DurableAcceptance; EffectComplete ], authoritative.Rows |> List.map _.Stage)
    Assert.All(journal.Requests, fun request -> Assert.Contains(request.Events, fun stored -> match stored.Event with CommandAcceptanceRecorded _ | EffectCompletionRecorded _ -> true | _ -> false))
}
