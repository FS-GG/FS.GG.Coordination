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
let observation revision digest captured =
    { ProjectId = projectId; SourceRevision = revision; WorkflowRevision = Id.revision 7L; Generation = Id.generation 3L
      ObservationSha256 = digest; Provenance = { provenance with CapturedAt = captured }; WorkItems = [ { Identity = workItem; MembershipItemId = "PVTI_item"; Archived = false } ]; NonWorkItemCount = 0 }

let envelope (state: ObserverState) command =
    { CommandId = Id.command(Guid.NewGuid()); ExpectedSequence = state.Sequence; PrincipalId = "planner-owner"; IssuedAt = now.AddSeconds -1.; ExpiresAt = now.AddMinutes 5.; Command = command }

let decide state command = Observer.decide now state (envelope state command)
let apply state command =
    let decision = decide state command
    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    decision.Events |> List.fold Observer.evolve state

let opened () = apply Observer.initial (OpenSession(sessionId, projectId, budget))
let observed () = apply (opened()) (RecordProjectObservation(observation "project-rev-1" (sha "c") (now.AddMinutes -1.)))

let proposalInput observationSha =
    { ProposalId = proposalId; AttemptId = attemptId; ObservationSha256 = observationSha; WorkflowRevision = Id.revision 7L; Generation = Id.generation 3L
      Scope = "repo:123/issues"; NarrativeSha256 = sha "d"; Actions = [ { Kind = "inspect"; WorkItem = workItem; ParametersSha256 = sha "e" } ]; ProposedAt = now }

[<Fact>]
let ``complete typed lifecycle keeps every presentation stage distinct`` () =
    let state0 = observed()
    let state1 = apply state0 (RecordConversation { EntryId = Guid.NewGuid(); Role = Operator; BodySha256 = sha "1"; RecordedAt = now.AddMinutes -1. })
    let state2 = apply state1 (StartPlanningAttempt(attemptId, usage 40L 20L 400L, now.AddSeconds -30.))
    let input = proposalInput state2.Observation.Value.ObservationSha256
    let state3 = apply state2 (CompletePlanningAttempt(attemptId, usage 25L 10L 250L, input))
    let proposal = state3.Proposals[proposalId]
    let commandId = Id.command(Guid.Parse "50000000-0000-0000-0000-000000000005")
    let approvalInput =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = Observer.budgetSha256 budget; PrincipalId = "human:owner"; Scope = proposal.Scope
          ExpectedWorkflowRevision = Id.revision 7L; ExpectedGeneration = Id.generation 3L; CommandId = commandId; CommandBodySha256 = sha "f"; Kind = ExplicitApproval; ApprovedAt = now }
    let state4 = apply state3 (ApproveProposal approvalInput)
    let approval = state4.Approvals[proposalId]
    let receipt = { CommandId = commandId; BodySha256 = sha "f"; Disposition = ReceiptDisposition.Accepted; Revision = Id.revision 8L; ProtocolVersion = Id.protocolVersion 1 0; Detail = "accepted" }
    let state5 = apply state4 (RecordCommandAcceptance { ProposalId = proposalId; ApprovalSha256 = approval.ApprovalSha256; Receipt = receipt; AcceptedAt = now })
    let state6 = apply state5 (RecordEffectCompletion { CommandId = commandId; OperationId = Id.operation(Guid.NewGuid()); Result = EffectApplied "provider-rev"; CompletedAt = now })
    let view = ObserverProjection.render state6
    Assert.Equal<ProjectionStage list>([ Conversation; Proposed; Approved; DurableAcceptance; EffectComplete ], view.Rows |> List.map _.Stage)
    Assert.Equal(Some(usage 75L 50L 750L), view.Remaining)

[<Fact>]
let ``stale planning output cannot replace newer observation`` () =
    let state0 = observed()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 40L 20L 400L, now.AddSeconds -30.))
    let stale = proposalInput state1.Observation.Value.ObservationSha256
    let state2 = apply state1 (RecordProjectObservation(observation "project-rev-2" (sha "9") now))
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
    let hugeState = apply Observer.initial (OpenSession(sessionId, projectId, huge)) |> fun state -> apply state (RecordProjectObservation(observation "r" (sha "c") now))
    let started = apply hugeState (StartPlanningAttempt(attemptId, usage Int64.MaxValue 1L 1L, now))
    let overflow = decide started (StartPlanningAttempt(Id.attempt(Guid.NewGuid()), usage 1L 0L 0L, now))
    Assert.Equal("planning-attempt-not-authorized", overflow.Receipt.Detail)

[<Fact>]
let ``approval binds exact proposal budget principal scope revision generation and command body`` () =
    let state0 = observed()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now))
    let state2 = apply state1 (CompletePlanningAttempt(attemptId, usage 5L 5L 5L, proposalInput state1.Observation.Value.ObservationSha256))
    let proposal = state2.Proposals[proposalId]
    let baseline =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = Observer.budgetSha256 budget; PrincipalId = "human:owner"; Scope = proposal.Scope
          ExpectedWorkflowRevision = Id.revision 7L; ExpectedGeneration = Id.generation 3L; CommandId = Id.command(Guid.NewGuid()); CommandBodySha256 = sha "f"; Kind = ExplicitApproval; ApprovedAt = now }
    for altered in [ { baseline with PlanSha256 = sha "0" }; { baseline with PlanningBudgetSha256 = sha "0" }; { baseline with Scope = "other" }; { baseline with ExpectedGeneration = Id.generation 4L } ] do
        Assert.Equal("stale-or-unbound-approval", (decide state2 (ApproveProposal altered)).Receipt.Detail)

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
type private Journal() =
    interface IObserverJournalStore with
        member _.AppendObserver(_, _) = Task.FromResult(ObserverInvalidAppend "not-run")
        member _.RecoverObserver(_, _) = Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

[<Fact>]
let ``composition exposes only read planning and observer journal capabilities`` () =
    let composition = ObserverComposition.create (ReadCapability()) (PlanningCapability()) (Journal())
    Assert.Equal<string list>([ "project-read"; "bounded-planning"; "observer-journal" ], ObserverComposition.capabilities composition)
    let publicConstructors = typeof<ObserverComposition>.Assembly.GetExportedTypes() |> Array.collect _.GetConstructors()
    let parameters = publicConstructors |> Array.collect _.GetParameters() |> Array.map (fun value -> value.ParameterType.FullName)
    Assert.DoesNotContain(parameters, fun value -> not (isNull value) && (value.Contains("EffectIntent") || value.Contains("Runner") || value.Contains("Mutation")))
