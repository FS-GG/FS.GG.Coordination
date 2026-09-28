module FS.GG.Coordination.Orchestration.Observer.Tests.ObserverTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Orchestration.Observer

let now = DateTimeOffset.Parse("2026-09-10T10:00:00Z")
let sha character = String.replicate 64 character
let projectId = Id.project (Guid.Parse "10000000-0000-0000-0000-000000000001")
let sessionId = Id.session (Guid.Parse "20000000-0000-0000-0000-000000000002")
let attemptId = Id.attempt (Guid.Parse "30000000-0000-0000-0000-000000000003")

let proposalId =
    ProposalId.create (Guid.Parse "40000000-0000-0000-0000-000000000004")

let workItem = WorkItemIdentity.create "R_kgDOExample" 123L "I_kwDOExample" 42L

let budget =
    {
        TokenLimit = 100L
        RuntimeSecondsLimit = 60L
        CostMicrosLimit = 1_000L
        Deadline = now.AddHours 1.
    }

let usage tokens seconds cost =
    {
        Tokens = tokens
        RuntimeSeconds = seconds
        CostMicros = cost
    }

let provenance =
    {
        Provider = "github-graphql"
        QuerySha256 = sha "a"
        EvidenceSha256 = sha "b"
        CapturedAt = now.AddMinutes -2.
    }

let readback =
    {
        Provider = "github-graphql"
        SourceRevision = "provider-rev"
        EvidenceSha256 = sha "8"
        ObservedAt = now
    }

let observation revision captured =
    let draft =
        {
            ProjectId = projectId
            SourceRevision = revision
            WorkflowRevision = Id.revision 7L
            Generation = Id.generation 3L
            ObservationSha256 = sha "0"
            Provenance =
                { provenance with
                    CapturedAt = captured
                }
            WorkItems =
                [
                    {
                        Identity = workItem
                        MembershipItemId = "PVTI_item"
                        Archived = false
                    }
                ]
            NonWorkItemCount = 0
        }

    { draft with
        ObservationSha256 = Observer.observationSha256 draft
    }

let envelope (state: ObserverState) command =
    {
        CommandId = Id.command (Guid.NewGuid())
        ExpectedSequence = state.Sequence
        PrincipalId = "planner-owner"
        IssuedAt = now.AddSeconds -1.
        ExpiresAt = now.AddMinutes 5.
        Command = command
    }

let decide state command =
    Observer.decide now state (envelope state command)

let apply state command =
    let decision = decide state command
    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    decision.Events |> List.fold Observer.evolve state

let opened () =
    apply Observer.initial (OpenSession(sessionId, projectId, budget))

let observed () =
    apply (opened ()) (RecordProjectObservation(observation "project-rev-1" (now.AddMinutes -1.)))

let proposalInput observationSha =
    {
        ProposalId = proposalId
        AttemptId = attemptId
        ObservationSha256 = observationSha
        WorkflowRevision = Id.revision 7L
        Generation = Id.generation 3L
        Scope = "repo:123/issues"
        NarrativeSha256 = sha "d"
        Actions =
            [
                {
                    Kind = InspectWorkItem
                    WorkItem = workItem
                    ParametersSha256 = sha "e"
                }
            ]
        ProposedAt = now
    }

let approvedState () =
    let state0 = observed ()

    let state1 =
        apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now.AddSeconds -5.))

    let state2 =
        apply
            state1
            (CompletePlanningAttempt(
                attemptId,
                usage 5L 5L 5L,
                proposalInput state1.Observation.Value.ObservationSha256
            ))

    let proposal = state2.Proposals[proposalId]
    let commandId = Id.command (Guid.Parse "50000000-0000-0000-0000-000000000005")

    let approval =
        {
            ProposalId = proposalId
            PlanSha256 = proposal.PlanSha256
            PlanningBudgetSha256 = Observer.budgetSha256 budget
            PrincipalId = "planner-owner"
            Scope = proposal.Scope
            ExpectedWorkflowRevision = proposal.WorkflowRevision
            ExpectedGeneration = proposal.Generation
            CommandId = commandId
            CommandBodySha256 = sha "f"
            Kind = ExplicitApproval
            ApprovedAt = now
        }

    apply state2 (ApproveProposal approval), commandId

let learningAssignment relation itemId originalItemId =
    {
        SourceObserverId = ObserverJournal.observerId sessionId
        SourceSequence = 2L
        SourceObservationSha256 = (observation "project-rev-1" (now.AddMinutes -1.)).ObservationSha256
        ItemId = itemId
        OriginalItemId = originalItemId
        Relation = relation
        Arm = Focused
        ProposalSha256 = sha "4"
        ContextManifestSha256 = sha "5"
        Planner =
            Some
                {
                    Model = LearningProposal.PlannerModel
                    Effort = LearningProposal.PlannerEffort
                }
        Worker =
            {
                Model = LearningContext.WorkerModel
                Effort = LearningContext.WorkerEffort
            }
        DirectSmallEligible = false
        ExpectedWorkflowRevision = Id.revision 7L
        ExpectedGeneration = Id.generation 3L
        AssignedAt = now
    }

let treatmentState () =
    { Observer.initial with
        Observation = (observed ()).Observation
    }

[<Fact>]
let ``complete typed lifecycle keeps every presentation stage distinct`` () =
    let state0 = observed ()

    let state1 =
        apply
            state0
            (RecordConversation
                {
                    EntryId = Guid.NewGuid()
                    Role = Operator
                    BodySha256 = sha "1"
                    RecordedAt = now.AddMinutes -1.
                })

    let state2 =
        apply state1 (StartPlanningAttempt(attemptId, usage 40L 20L 400L, now.AddSeconds -5.))

    let input = proposalInput state2.Observation.Value.ObservationSha256

    let state3 =
        apply state2 (CompletePlanningAttempt(attemptId, usage 25L 10L 250L, input))

    let proposal = state3.Proposals[proposalId]
    let commandId = Id.command (Guid.Parse "50000000-0000-0000-0000-000000000005")

    let approvalInput =
        {
            ProposalId = proposalId
            PlanSha256 = proposal.PlanSha256
            PlanningBudgetSha256 = Observer.budgetSha256 budget
            PrincipalId = "planner-owner"
            Scope = proposal.Scope
            ExpectedWorkflowRevision = Id.revision 7L
            ExpectedGeneration = Id.generation 3L
            CommandId = commandId
            CommandBodySha256 = sha "f"
            Kind = ExplicitApproval
            ApprovedAt = now
        }

    let state4 = apply state3 (ApproveProposal approvalInput)
    let approval = state4.Approvals[proposalId]

    let receipt =
        {
            CommandId = commandId
            BodySha256 = sha "f"
            Disposition = ReceiptDisposition.Accepted
            Revision = Id.revision 8L
            ProtocolVersion = Id.protocolVersion 1 0
            Detail = "accepted"
        }

    let state5 =
        apply
            state4
            (RecordCommandAcceptance
                {
                    ProposalId = proposalId
                    ApprovalSha256 = approval.ApprovalSha256
                    Receipt = receipt
                    Provenance = readback
                    AcceptedAt = now
                })

    let state6 =
        apply
            state5
            (RecordEffectCompletion
                {
                    CommandId = commandId
                    OperationId = Id.operation (Guid.NewGuid())
                    Result = EffectApplied "provider-rev"
                    Provenance = readback
                    CompletedAt = now
                })

    let view = ObserverProjection.render state6

    Assert.Equal<ProjectionStage list>(
        [ Conversation; Proposed; Approved; DurableAcceptance; EffectComplete ],
        view.Rows |> List.map _.Stage
    )

    Assert.Equal(Some(usage 75L 50L 750L), view.Remaining)
    Assert.Equal(view, ObserverProjection.fromRecovery { Events = []; State = state6 })

[<Fact>]
let ``stale planning output cannot replace newer observation`` () =
    let state0 = observed ()

    let state1 =
        apply state0 (StartPlanningAttempt(attemptId, usage 40L 20L 400L, now.AddSeconds -5.))

    let stale = proposalInput state1.Observation.Value.ObservationSha256

    let state2 =
        apply state1 (RecordProjectObservation(observation "project-rev-2" now))

    let refused =
        decide state2 (CompletePlanningAttempt(attemptId, usage 1L 1L 1L, stale))

    Assert.Equal(ObserverRejected, refused.Receipt.Disposition)
    Assert.Equal("stale-or-invalid-proposal", refused.Receipt.Detail)
    Assert.Empty(state2.Proposals)

[<Fact>]
let ``agent death retains reservation until explicit conservative consumption`` () =
    let state0 = observed ()
    let reservation = usage 40L 20L 400L
    let state1 = apply state0 (StartPlanningAttempt(attemptId, reservation, now))

    let state2 =
        apply state1 (MarkPlanningAttemptUnknown(attemptId, "worker-disconnected"))

    Assert.Equal(reservation, state2.Reserved)
    Assert.Equal(usage 0L 0L 0L, state2.Used)

    let denied =
        decide state2 (StartPlanningAttempt(Id.attempt (Guid.NewGuid()), usage 1L 1L 1L, now))

    Assert.Equal("planning-attempt-not-authorized", denied.Receipt.Detail)

    let state3 =
        apply state2 (ConsumeUnknownPlanningReservation(attemptId, "usage-unobservable"))

    Assert.Equal(reservation, state3.Used)
    Assert.Equal(usage 0L 0L 0L, state3.Reserved)

[<Fact>]
let ``budget deadline capacity and overflow refuse before planning`` () =
    let state0 = observed ()
    let over = decide state0 (StartPlanningAttempt(attemptId, usage 101L 1L 1L, now))
    Assert.Equal("planning-attempt-not-authorized", over.Receipt.Detail)
    let expiredBudget = { budget with Deadline = now }

    let opening =
        decide Observer.initial (OpenSession(sessionId, projectId, expiredBudget))

    Assert.Equal("invalid-planning-budget", opening.Receipt.Detail)

    let huge =
        { budget with
            TokenLimit = Int64.MaxValue
        }

    let hugeState =
        apply Observer.initial (OpenSession(sessionId, projectId, huge))
        |> fun state -> apply state (RecordProjectObservation(observation "r" now))

    let started =
        apply hugeState (StartPlanningAttempt(attemptId, usage Int64.MaxValue 1L 1L, now))

    let overflow =
        decide started (StartPlanningAttempt(Id.attempt (Guid.NewGuid()), usage 1L 0L 0L, now))

    Assert.Equal("planning-attempt-not-authorized", overflow.Receipt.Detail)

    let hugeRuntimeBudget =
        { budget with
            RuntimeSecondsLimit = Int64.MaxValue
        }

    let runtimeState =
        apply Observer.initial (OpenSession(sessionId, projectId, hugeRuntimeBudget))
        |> fun state -> apply state (RecordProjectObservation(observation "runtime" now))

    let runtimeOverflow =
        decide runtimeState (StartPlanningAttempt(attemptId, usage 0L Int64.MaxValue 0L, now))

    Assert.Equal("planning-attempt-not-authorized", runtimeOverflow.Receipt.Detail)

[<Fact>]
let ``approval binds exact proposal budget principal scope revision generation and command body`` () =
    let state0 = observed ()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now))

    let state2 =
        apply
            state1
            (CompletePlanningAttempt(
                attemptId,
                usage 5L 5L 5L,
                proposalInput state1.Observation.Value.ObservationSha256
            ))

    let proposal = state2.Proposals[proposalId]

    let baseline =
        {
            ProposalId = proposalId
            PlanSha256 = proposal.PlanSha256
            PlanningBudgetSha256 = Observer.budgetSha256 budget
            PrincipalId = "planner-owner"
            Scope = proposal.Scope
            ExpectedWorkflowRevision = Id.revision 7L
            ExpectedGeneration = Id.generation 3L
            CommandId = Id.command (Guid.NewGuid())
            CommandBodySha256 = sha "f"
            Kind = ExplicitApproval
            ApprovedAt = now
        }

    for altered in
        [
            { baseline with PlanSha256 = sha "0" }
            { baseline with
                PlanningBudgetSha256 = sha "0"
            }
            { baseline with Scope = "other" }
            { baseline with
                ExpectedGeneration = Id.generation 4L
            }
        ] do
        Assert.Equal("stale-or-unbound-approval", (decide state2 (ApproveProposal altered)).Receipt.Detail)

[<Fact>]
let ``command digest binds complete authority envelope`` () =
    let state = opened ()

    let original =
        envelope
            state
            (RecordConversation
                {
                    EntryId = Guid.NewGuid()
                    Role = Operator
                    BodySha256 = sha "1"
                    RecordedAt = now
                })

    let digest = Observer.commandSha256 original
    Assert.NotEqual<string>(digest, Observer.commandSha256 { original with PrincipalId = "other" })

    Assert.NotEqual<string>(
        digest,
        Observer.commandSha256
            { original with
                ExpectedSequence = original.ExpectedSequence + 1L
            }
    )

    Assert.NotEqual<string>(
        digest,
        Observer.commandSha256
            { original with
                ExpiresAt = original.ExpiresAt.AddSeconds 1.
            }
    )

    let nullPrincipal = { original with PrincipalId = null }
    Assert.Equal("invalid-or-expired-observer-command", (Observer.decide now state nullPrincipal).Receipt.Detail)

[<Fact>]
let ``observation digest is derived and authority regression refuses`` () =
    let state = opened ()
    let valid = observation "project-rev-1" (now.AddMinutes -1.)

    let altered =
        { valid with
            ObservationSha256 = sha "0"
        }

    Assert.Equal("invalid-project-observation", (decide state (RecordProjectObservation altered)).Receipt.Detail)
    let accepted = apply state (RecordProjectObservation valid)

    let regressedDraft =
        { valid with
            SourceRevision = "newer-provider-read"
            Generation = Id.generation 2L
            Provenance =
                { valid.Provenance with
                    CapturedAt = now
                }
        }

    let regressed =
        { regressedDraft with
            ObservationSha256 = Observer.observationSha256 regressedDraft
        }

    Assert.Equal("stale-project-observation", (decide accepted (RecordProjectObservation regressed)).Receipt.Detail)

[<Fact>]
let ``proposal actions must name observed work and approval principal and live budget`` () =
    let state0 = observed ()
    let state1 = apply state0 (StartPlanningAttempt(attemptId, usage 10L 10L 10L, now))
    let foreign = WorkItemIdentity.create "R_other" 999L "I_other" 1L
    let baselineProposal = proposalInput state1.Observation.Value.ObservationSha256

    let invalidInput =
        { baselineProposal with
            Actions =
                [
                    {
                        Kind = RecommendRoutineWork
                        WorkItem = foreign
                        ParametersSha256 = sha "e"
                    }
                ]
        }

    let invalidProposalDecision =
        decide state1 (CompletePlanningAttempt(attemptId, usage 1L 1L 1L, invalidInput))

    Assert.Equal("stale-or-invalid-proposal", invalidProposalDecision.Receipt.Detail)
    let validInput = proposalInput state1.Observation.Value.ObservationSha256

    let state2 =
        apply state1 (CompletePlanningAttempt(attemptId, usage 1L 1L 1L, validInput))

    let proposal = state2.Proposals[proposalId]

    let input =
        {
            ProposalId = proposalId
            PlanSha256 = proposal.PlanSha256
            PlanningBudgetSha256 = Observer.budgetSha256 budget
            PrincipalId = "different-principal"
            Scope = proposal.Scope
            ExpectedWorkflowRevision = proposal.WorkflowRevision
            ExpectedGeneration = proposal.Generation
            CommandId = Id.command (Guid.NewGuid())
            CommandBodySha256 = sha "f"
            Kind = ExplicitApproval
            ApprovedAt = now
        }

    Assert.Equal("stale-or-unbound-approval", (decide state2 (ApproveProposal input)).Receipt.Detail)
    let later = budget.Deadline.AddSeconds 1.

    let approvalEnvelope =
        envelope
            state2
            (ApproveProposal
                { input with
                    PrincipalId = "planner-owner"
                    ApprovedAt = later
                })

    let laterEnvelope =
        { approvalEnvelope with
            IssuedAt = later
            ExpiresAt = later.AddMinutes 1.
        }

    Assert.Equal("stale-or-unbound-approval", (Observer.decide later state2 laterEnvelope).Receipt.Detail)

[<Fact>]
let ``original treatment is durable before any executor authority exists`` () =
    let state0 = treatmentState ()
    let input = learningAssignment Original "FS-GG/Coordination#42" "FS-GG/Coordination#42"
    let sessionDecision = decide (observed ()) (AssignLearningTreatment input)
    Assert.Equal(ObserverRejected, sessionDecision.Receipt.Disposition)
    Assert.Equal("learning-treatment-canonical-runtime-required", sessionDecision.Receipt.Detail)
    Assert.Empty(sessionDecision.Events)

    let decision = decide state0 (AssignLearningTreatment input)

    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    Assert.Equal("learning-treatment-assigned", decision.Receipt.Detail)
    let assigned = Assert.Single(decision.Events)
    let state1 = Observer.evolve state0 assigned
    let treatment = state1.LearningTreatments[input.OriginalItemId]
    let binding = state1.LearningTreatmentBindings[input.ItemId]

    Assert.Equal(Focused, treatment.Arm)
    Assert.Equal(LearningProposal.PlannerModel, treatment.Planner.Value.Model)
    Assert.Equal(LearningContext.WorkerModel, treatment.Worker.Model)
    Assert.Equal(Observer.learningTreatmentSha256 "planner-owner" input, treatment.AssignmentSha256)
    Assert.Equal(Original, binding.Relation)
    Assert.Empty(state1.Acceptances)
    Assert.Empty(state1.Effects)

[<Fact>]
let ``crash replay returns the same durable treatment without redrawing`` () =
    let state0 = treatmentState ()
    let input = learningAssignment Original "FS-GG/Coordination#42" "FS-GG/Coordination#42"
    let first = decide state0 (AssignLearningTreatment input)
    let assignedEvent = Assert.Single(first.Events)
    let recovered = Observer.evolve state0 assignedEvent
    let replayed = decide recovered (AssignLearningTreatment input)

    Assert.Equal(ObserverAccepted, replayed.Receipt.Disposition)
    Assert.Equal("learning-treatment-replayed", replayed.Receipt.Detail)
    Assert.Empty(replayed.Events)
    Assert.Equal(recovered.Sequence, replayed.Receipt.Sequence)
    Assert.Equal(
        recovered.LearningTreatments[input.OriginalItemId].AssignmentSha256,
        Observer.learningTreatmentSha256 "planner-owner" input
    )

    let bytes = ObserverEventCodec.encode assignedEvent
    Assert.Equal(Ok assignedEvent, ObserverEventCodec.tryDecode bytes)

[<Fact>]
let ``descendants and retries inherit one original owner and cannot redraw`` () =
    let originalId = "FS-GG/Coordination#42"
    let state0 = apply (treatmentState ()) (AssignLearningTreatment(learningAssignment Original originalId originalId))

    let missingParent =
        learningAssignment (Descendant "FS-GG/Coordination#missing") "FS-GG/Coordination#46" originalId

    let missingParentDecision = decide state0 (AssignLearningTreatment missingParent)
    Assert.Equal(ObserverRejected, missingParentDecision.Receipt.Disposition)
    Assert.Equal("learning-treatment-lineage-missing", missingParentDecision.Receipt.Detail)
    Assert.Empty(missingParentDecision.Events)

    let descendant = learningAssignment (Descendant originalId) "FS-GG/Coordination#43" originalId
    let inherited = decide state0 (AssignLearningTreatment descendant)
    let inheritedEvent = Assert.Single(inherited.Events)
    let inheritedBytes = ObserverEventCodec.encode inheritedEvent
    Assert.Equal(Ok inheritedEvent, ObserverEventCodec.tryDecode inheritedBytes)
    let state1 = Observer.evolve state0 inheritedEvent
    let retry = learningAssignment (Retry descendant.ItemId) "FS-GG/Coordination#44" originalId
    let state2 = apply state1 (AssignLearningTreatment retry)
    let treatment = state2.LearningTreatments[originalId]

    for item in [ originalId; descendant.ItemId; retry.ItemId ] do
        Assert.Equal(treatment.AssignmentSha256, state2.LearningTreatmentBindings[item].AssignmentSha256)
        Assert.Equal("planner-owner", state2.LearningTreatmentBindings[item].OwnerPrincipalId)

    let redraw =
        { learningAssignment (Descendant originalId) "FS-GG/Coordination#45" originalId with
            Arm = Current
        }

    let redrawDecision = decide state2 (AssignLearningTreatment redraw)
    Assert.Equal(ObserverRejected, redrawDecision.Receipt.Disposition)
    Assert.Equal("learning-treatment-conflict", redrawDecision.Receipt.Detail)
    Assert.Empty(redrawDecision.Events)

    let foreign =
        { envelope state2 (AssignLearningTreatment retry) with
            PrincipalId = "second-owner"
        }

    let foreignDecision = Observer.decide now state2 foreign
    Assert.Equal(ObserverRejected, foreignDecision.Receipt.Disposition)
    Assert.Equal("learning-treatment-owner-conflict", foreignDecision.Receipt.Detail)
    Assert.Empty(foreignDecision.Events)

[<Fact>]
let ``stale generation and invalid fixed profile refuse before persistence`` () =
    let state = treatmentState ()
    let originalId = "FS-GG/Coordination#42"

    let stale =
        { learningAssignment Original originalId originalId with
            ExpectedGeneration = Id.generation 2L
        }

    let staleDecision = decide state (AssignLearningTreatment stale)
    Assert.Equal(ObserverRejected, staleDecision.Receipt.Disposition)
    Assert.Equal("stale-learning-treatment-generation", staleDecision.Receipt.Detail)
    Assert.Empty(staleDecision.Events)

    let wrongProfile =
        { learningAssignment Original originalId originalId with
            Worker = { Model = "unqualified"; Effort = "medium" }
        }

    let profileDecision = decide state (AssignLearningTreatment wrongProfile)
    Assert.Equal(ObserverRejected, profileDecision.Receipt.Disposition)
    Assert.Equal("invalid-learning-treatment-assignment", profileDecision.Receipt.Detail)
    Assert.Empty(profileDecision.Events)
    Assert.Empty(state.LearningTreatments)

[<Fact>]
let ``event codec roundtrip replays private identities and durable budget`` () =
    let state = observed ()

    let events =
        [
            SessionOpened(sessionId, projectId, budget)
            ProjectObservationRecorded(state.Observation.Value)
        ]

    let decoded =
        events
        |> List.map (ObserverEventCodec.encode >> ObserverEventCodec.tryDecode)
        |> List.map (function
            | Ok value -> value
            | Error error -> failwith error)

    Assert.Equal<ObserverEvent list>(events, decoded)
    Assert.Equal(Observer.replay events, state)

[<Fact>]
let ``transient project failures cannot erase known membership or create an observation`` () =
    let known = observed ()
    let knownObservation = known.Observation
    let knownSequence = known.Sequence

    let failures =
        [
            ProjectIncomplete("page lost", Some "cursor"), "incomplete"
            ProjectUnreadable "timeout", "timeout"
            ProjectUnreadable "rate-limited", "rate-limited"
        ]

    for failure, expected in failures do
        let refusal =
            ProjectObservationBridge.observe projectId (Id.revision 8L) (Id.generation 3L) provenance [] failure

        match refusal, expected with
        | Error(ProjectReadRefused(ProjectObservationRefused(ObservationIncomplete("page lost", Some "cursor")))),
          "incomplete" -> ()
        | Error(ProjectReadRefused(ProjectObservationUnreadable reason)), expectedReason ->
            Assert.Equal(expectedReason, reason)
        | other -> failwithf "unexpected project failure projection: %A" other

        Assert.Equal(knownObservation, known.Observation)
        Assert.Equal(knownSequence, known.Sequence)

[<Fact>]
let ``complete bridge requires immutable repository database and issue readback`` () =
    let live value =
        LiveId.tryCreate value |> Result.defaultWith failwith

    let repo =
        {
            Owner = "FS-GG"
            Name = "FS.GG.Coordination"
        }

    let content = live "I_kwDOExample"

    let item =
        {
            ProjectId = live "PVT_project"
            ItemId = live "PVTI_item"
            Content = RepositoryIssue(repo, 42, content)
            Archived = false
        }

    let project =
        ProjectComplete(
            "revision",
            [
                {
                    Number = 1
                    Items = [ item ]
                    TerminalPage = true
                }
            ]
        )

    let missing =
        ProjectObservationBridge.observe projectId (Id.revision 1L) (Id.generation 1L) provenance [] project

    Assert.Equal(Error(MissingImmutableIssueIdentity content), missing)

    let fact =
        {
            Repository = repo
            RepositoryNodeId = "R_kgDOExample"
            RepositoryDatabaseId = 123L
            ContentNodeId = content
            IssueNodeId = "I_kwDOExample"
            IssueNumber = 42L
        }

    let accepted =
        ProjectObservationBridge.observe projectId (Id.revision 1L) (Id.generation 1L) provenance [ fact ] project
        |> Result.defaultWith (sprintf "%A" >> failwith)

    Assert.Equal(
        WorkItemIdentity.persistenceId workItem,
        WorkItemIdentity.persistenceId accepted.WorkItems.Head.Identity
    )

    let movedMembership =
        ProjectComplete(
            "revision-2",
            [
                {
                    Number = 1
                    Items =
                        [
                            { item with
                                ItemId = live "PVTI_other_board_projection"
                            }
                        ]
                    TerminalPage = true
                }
            ]
        )

    let reconciled =
        ProjectObservationBridge.observe
            projectId
            (Id.revision 2L)
            (Id.generation 1L)
            { provenance with
                EvidenceSha256 = sha "c"
            }
            [ fact ]
            movedMembership
        |> Result.defaultWith (sprintf "%A" >> failwith)

    let owners =
        [ accepted; reconciled ]
        |> List.collect _.WorkItems
        |> List.map (fun value -> WorkItemIdentity.persistenceId value.Identity)
        |> Set.ofList

    Assert.Equal(1, owners.Count)

type private ReadCapability() =
    interface IProjectReadCapability with
        member _.ObserveProject(_, _) =
            Task.FromResult(Error InvalidObservationProvenance)

type private PlanningCapability() =
    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) = Task.FromResult(Error "not-run")

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
        member _.AppendObserver(_, _) =
            Task.FromResult(ObserverInvalidAppend "not-run")

        member _.RecoverObserver(_, _) =
            Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

type private InMemoryObserverJournal() =
    let streams = Dictionary<string, ResizeArray<ObserverStoredEvent>>()
    let mutable appendCount = 0

    member _.AppendCount = appendCount
    member _.StreamLength observerId =
        lock streams (fun () ->
            match streams.TryGetValue observerId with
            | true, events -> events.Count
            | _ -> 0)

    interface IObserverJournalStore with
        member _.AppendObserver(request, _) =
            lock streams (fun () ->
                let stream =
                    match streams.TryGetValue request.ObserverId with
                    | true, value -> value
                    | _ ->
                        let value = ResizeArray<ObserverStoredEvent>()
                        streams.Add(request.ObserverId, value)
                        value

                let currentState =
                    stream |> Seq.map _.Event |> Seq.toList |> Observer.replay

                let expected =
                    if stream.Count = 0 then 1L else stream[stream.Count - 1].Sequence + 1L

                match request.Events with
                | [] -> Task.FromResult(ObserverInvalidAppend "empty-append")
                | first :: _ when first.Sequence <> expected ->
                    Task.FromResult(ObserverWrongExpectedSequence(expected - 1L))
                | events ->
                    let decisionState =
                        match request.Command.Command with
                        | AssignLearningTreatment input when
                            request.ObserverId = ObserverJournal.learningTreatmentObserverId input.OriginalItemId
                            ->
                            match streams.TryGetValue input.SourceObserverId with
                            | true, sourceEvents ->
                                let sourceState =
                                    sourceEvents |> Seq.map _.Event |> Seq.toList |> Observer.replay

                                if
                                    sourceState.Sequence = input.SourceSequence
                                    && (sourceState.SessionId
                                        |> Option.exists (fun sessionId ->
                                            ObserverJournal.observerId sessionId = input.SourceObserverId))
                                    && (sourceState.Observation
                                        |> Option.exists (fun observation ->
                                            observation.ObservationSha256 = input.SourceObservationSha256
                                            && observation.WorkflowRevision = input.ExpectedWorkflowRevision
                                            && observation.Generation = input.ExpectedGeneration))
                                then
                                    Some
                                        { currentState with
                                            Observation = sourceState.Observation
                                        }
                                else
                                    None
                            | _ -> None
                        | AssignLearningTreatment _ -> None
                        | _ -> Some currentState

                    match decisionState with
                    | None -> Task.FromResult(ObserverInvalidAppend "source-reference-mismatch")
                    | Some value ->
                        let decision = Observer.decide request.ReceivedAt value request.Command

                        if
                            decision.Receipt.Disposition <> ObserverAccepted
                            || decision.Events <> (events |> List.map _.Event)
                        then
                            Task.FromResult(ObserverInvalidAppend "events-do-not-match-command-decision")
                        else
                            events |> List.iter stream.Add
                            appendCount <- appendCount + 1
                            Task.FromResult(ObserverAppended(stream[stream.Count - 1].Sequence)))

        member _.RecoverObserver(observerId, _) =
            lock streams (fun () ->
                let events =
                    match streams.TryGetValue observerId with
                    | true, value -> List.ofSeq value
                    | _ -> []

                Task.FromResult(
                    Ok
                        {
                            Events = events
                            State = Observer.replay (events |> List.map _.Event)
                        }
                ))

[<Fact>]
let ``composition exposes only read planning and observer journal capabilities`` () =
    let composition =
        ObserverComposition.create (ReadCapability()) (PlanningCapability()) (ReadbackCapability()) (Journal())

    Assert.Equal<string list>(
        [ "project-read"; "bounded-planning"; "command-readback"; "observer-journal" ],
        ObserverComposition.capabilities composition
    )

    let publicConstructors =
        typeof<ObserverComposition>.Assembly.GetExportedTypes()
        |> Array.collect _.GetConstructors()

    let parameters =
        publicConstructors
        |> Array.collect _.GetParameters()
        |> Array.map (fun value -> value.ParameterType.FullName)

    Assert.DoesNotContain(
        parameters,
        fun value ->
            not (isNull value)
            && (value.Contains("EffectIntent")
                || value.Contains("Runner")
                || value.Contains("Mutation"))
    )

[<Fact>]
let ``canonical treatment stream survives a new observer session and refuses another owner`` () =
    task {
        let originalId = "FS-GG/Coordination#42"
        let secondSession = Id.session (Guid.Parse "20000000-0000-0000-0000-000000000099")
        let journal = InMemoryObserverJournal()
        let clock = { new TimeProvider() with override _.GetUtcNow() = now }

        let composition =
            ObserverComposition.create (ReadCapability()) (PlanningCapability()) (ReadbackCapability()) journal

        let persistSource session =
            task {
                let observerId = ObserverJournal.observerId session
                let mutable state = Observer.initial

                let append command =
                    task {
                        let commandEnvelope = envelope state command
                        let decision = Observer.decide now state commandEnvelope
                        let request = ObserverJournal.appendRequest observerId now commandEnvelope decision
                        let! outcome = (journal :> IObserverJournalStore).AppendObserver(request, CancellationToken.None)
                        Assert.Equal<ObserverAppendOutcome>(ObserverAppended decision.Receipt.Sequence, outcome)
                        state <- decision.Events |> List.fold Observer.evolve state
                    }

                do! append (OpenSession(session, projectId, budget))
                do! append (RecordProjectObservation(observation "project-rev-1" (now.AddMinutes -1.)))
                return state
            }

        let! firstSource = persistSource sessionId
        let! secondSource = persistSource secondSession
        let input = learningAssignment Original originalId originalId

        let request (source: ObserverState) principal treatment =
            let sourceObserverId = ObserverJournal.observerId source.SessionId.Value

            {
                SourceObserverId = sourceObserverId
                SourceState = source
                Input =
                    { treatment with
                        SourceObserverId = sourceObserverId
                        SourceSequence = source.Sequence
                        SourceObservationSha256 = source.Observation.Value.ObservationSha256
                    }
                CommandId = Id.command (Guid.NewGuid())
                PrincipalId = principal
                IssuedAt = now.AddSeconds -1.
                ExpiresAt = now.AddMinutes 1.
            }

        let! first =
            ObserverRuntime.assignLearningTreatment
                clock
                composition
                (request firstSource "planner-owner" input)
                CancellationToken.None

        let assigned =
            match first with
            | LearningTreatmentPersisted(treatment, binding) ->
                Assert.Equal(Original, binding.Relation)
                treatment
            | other -> failwithf "unexpected %A" other

        let! replayed =
            ObserverRuntime.assignLearningTreatment
                clock
                composition
                (request secondSource "planner-owner" input)
                CancellationToken.None

        match replayed with
        | LearningTreatmentReplayed(treatment, binding) ->
            Assert.Equal(assigned.AssignmentSha256, treatment.AssignmentSha256)
            Assert.Equal(assigned.AssignmentSha256, binding.AssignmentSha256)
        | other -> failwithf "unexpected %A" other

        let! foreign =
            ObserverRuntime.assignLearningTreatment
                clock
                composition
                (request secondSource "second-owner" input)
                CancellationToken.None

        match foreign with
        | LearningTreatmentCommandRefused receipt ->
            Assert.Equal("learning-treatment-owner-conflict", receipt.Detail)
        | other -> failwithf "unexpected %A" other

        let redraw = { input with Arm = Current }

        let! redrawn =
            ObserverRuntime.assignLearningTreatment
                clock
                composition
                (request secondSource "planner-owner" redraw)
                CancellationToken.None

        match redrawn with
        | LearningTreatmentCommandRefused receipt ->
            Assert.Equal("learning-treatment-conflict", receipt.Detail)
        | other -> failwithf "unexpected %A" other

        Assert.Equal(5, journal.AppendCount)
        Assert.Equal(1, journal.StreamLength(ObserverJournal.learningTreatmentObserverId originalId))
        Assert.NotEqual(ObserverJournal.observerId sessionId, ObserverJournal.observerId secondSession)
        Assert.Equal(
            ObserverJournal.learningTreatmentObserverId originalId,
            ObserverJournal.learningTreatmentObserverId input.OriginalItemId
        )
    }

type private RecordingJournal(outcomes: ObserverAppendOutcome list) =
    let requests = ResizeArray<ObserverAppendRequest>()
    let mutable remaining = outcomes
    member _.Requests = List.ofSeq requests

    interface IObserverJournalStore with
        member _.AppendObserver(request, _) =
            requests.Add request

            match remaining with
            | head :: tail ->
                remaining <- tail
                Task.FromResult head
            | [] -> Task.FromResult(ObserverAppendUnavailable "unexpected-append")

        member _.RecoverObserver(_, _) =
            Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

type private RecordingPlanner(beforeLaunch: unit -> unit, result: Result<PlanningResult, string>) =
    let mutable calls = 0
    member _.Calls = calls

    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) =
            beforeLaunch ()
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

            if requests.Count = 1 then
                clock.Advance(TimeSpan.FromSeconds 2.)

            Task.FromResult(ObserverAppended(request.Events |> List.last |> _.Sequence))

        member _.RecoverObserver(_, _) =
            Task.FromResult(Error [ ObserverStoreUnavailable "not-run" ])

type private NonCooperativePlanner() =
    let pending =
        TaskCompletionSource<Result<PlanningResult, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) = pending.Task

type private SynchronouslyBlockingPlanner(entered: ManualResetEventSlim, release: ManualResetEventSlim) =
    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) =
            entered.Set()
            release.Wait()
            Task.FromResult(Error "released-after-observer-timeout")

let executionRequest state =
    {
        ObserverId = ObserverJournal.observerId sessionId
        State = state
        AttemptId = attemptId
        Reservation = usage 10L 10L 10L
        StartCommandId = Id.command (Guid.NewGuid())
        CompletionCommandId = Id.command (Guid.NewGuid())
        UnknownCommandId = Id.command (Guid.NewGuid())
        PrincipalId = "planner-owner"
        IssuedAt = now.AddSeconds -1.
        ExpiresAt = now.AddMinutes 1.
    }

[<Fact>]
let ``runtime persists reservation before invoking bounded planner and persists result before return`` () =
    task {
        let state = observed ()

        let journal =
            RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 3L) ])

        let input = proposalInput state.Observation.Value.ObservationSha256

        let planner =
            RecordingPlanner(
                (fun () -> Assert.Single(journal.Requests) |> ignore),
                Ok
                    {
                        Proposal = input
                        ActualUse = usage 5L 5L 5L
                    }
            )

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal

        let! outcome =
            ObserverRuntime.executePlanning
                (ManualTimeProvider now)
                composition
                (executionRequest state)
                CancellationToken.None

        match outcome with
        | PlanningResultPersisted(finalState, proposal) ->
            Assert.Equal(3L, finalState.Sequence - state.Sequence)
            Assert.Equal(proposalId, proposal.ProposalId)
        | other -> failwithf "unexpected %A" other

        Assert.Equal(2, journal.Requests.Length)
        Assert.Equal(1, planner.Calls)
    }

[<Fact>]
let ``duplicate reservation never relaunches planning agent`` () =
    task {
        let state = observed ()
        let journal = RecordingJournal([ ObserverDuplicate(state.Sequence + 1L) ])
        let planner = RecordingPlanner((fun () -> ()), Error "must-not-run")

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal

        let! outcome =
            ObserverRuntime.executePlanning
                (ManualTimeProvider now)
                composition
                (executionRequest state)
                CancellationToken.None

        match outcome with
        | PlanningAlreadyRecorded _ -> ()
        | other -> failwithf "unexpected %A" other

        Assert.Equal(0, planner.Calls)
    }

[<Fact>]
let ``planner loss persists unknown state without releasing reservation`` () =
    task {
        let state = observed ()

        let journal =
            RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])

        let planner =
            RecordingPlanner((fun () -> Assert.Single(journal.Requests) |> ignore), Error "agent-died")

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal

        let! outcome =
            ObserverRuntime.executePlanning
                (ManualTimeProvider now)
                composition
                (executionRequest state)
                CancellationToken.None

        match outcome with
        | PlanningOutcomePersistedUnknown(finalState, "agent-died") ->
            Assert.Equal(usage 10L 10L 10L, finalState.Reserved)
            Assert.Equal(PlanningOutcomeUnknown "agent-died", finalState.Attempts[attemptId].Status)
        | other -> failwithf "unexpected %A" other
    }

[<Fact>]
let ``runtime reads fresh time after a delayed successful planner`` () =
    task {
        let state = observed ()
        let clock = ManualTimeProvider now

        let journal =
            RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 3L) ])

        let input = proposalInput state.Observation.Value.ObservationSha256

        let planner =
            RecordingPlanner(
                (fun () -> clock.Advance(TimeSpan.FromSeconds 1.)),
                Ok
                    {
                        Proposal = input
                        ActualUse = usage 1L 1L 1L
                    }
            )

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal

        let! outcome =
            ObserverRuntime.executePlanning clock composition (executionRequest state) CancellationToken.None

        match outcome with
        | PlanningResultPersisted _ -> ()
        | other -> failwithf "unexpected %A" other

        let completionEnvelope = journal.Requests[1].Command
        Assert.Equal(now.AddSeconds 1., completionEnvelope.IssuedAt)
    }

[<Fact>]
let ``result after command expiry becomes durable unknown and retains reservation`` () =
    task {
        let state = observed ()
        let clock = ManualTimeProvider now

        let journal =
            RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])

        let input = proposalInput state.Observation.Value.ObservationSha256

        let planner =
            RecordingPlanner(
                (fun () -> clock.Advance(TimeSpan.FromSeconds 2.)),
                Ok
                    {
                        Proposal = input
                        ActualUse = usage 1L 1L 1L
                    }
            )

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal

        let request =
            { executionRequest state with
                ExpiresAt = now.AddSeconds 1.
            }

        let! outcome =
            ObserverRuntime.executePlanning clock composition request CancellationToken.None

        match outcome with
        | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") ->
            Assert.Equal(request.Reservation, finalState.Reserved)
        | other -> failwithf "unexpected %A" other
    }

[<Fact>]
let ``reservation append consuming deadline prevents planner launch`` () =
    task {
        let state = observed ()
        let clock = ManualTimeProvider now
        let journal = DelayedReservationJournal(clock)
        let input = proposalInput state.Observation.Value.ObservationSha256

        let planner =
            RecordingPlanner(
                (fun () -> ()),
                Ok
                    {
                        Proposal = input
                        ActualUse = usage 1L 1L 1L
                    }
            )

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal

        let request =
            { executionRequest state with
                ExpiresAt = now.AddSeconds 1.
            }

        let! outcome =
            ObserverRuntime.executePlanning clock composition request CancellationToken.None

        match outcome with
        | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") ->
            Assert.Equal(request.Reservation, finalState.Reserved)
        | other -> failwithf "unexpected %A" other

        Assert.Equal(0, planner.Calls)
    }

[<Fact>]
let ``noncooperative planner is bounded by command expiry and cannot release reservation`` () =
    task {
        let current = TimeProvider.System.GetUtcNow()

        let state =
            { observed () with
                Budget =
                    Some
                        { budget with
                            Deadline = current.AddMinutes 1.
                        }
            }

        let journal =
            RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])

        let planner = NonCooperativePlanner()

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
        // Keep enough launch margin for a loaded CI thread pool while retaining a short,
        // externally measured bound on a planner that never cooperates.
        let request =
            { executionRequest state with
                IssuedAt = current.AddSeconds -1.
                ExpiresAt = current.AddSeconds 2.
            }

        let elapsed = Diagnostics.Stopwatch.StartNew()

        let! outcome =
            ObserverRuntime.executePlanning TimeProvider.System composition request CancellationToken.None

        elapsed.Stop()

        match outcome with
        | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") ->
            Assert.Equal(request.Reservation, finalState.Reserved)
        | other -> failwithf "unexpected %A" other

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds 10.,
            $"observer exceeded its wall-clock bound: {elapsed.Elapsed}"
        )
    }

[<Fact>]
let ``synchronously blocking planner entry is bounded off the observer caller`` () =
    task {
        let current = TimeProvider.System.GetUtcNow()

        let state =
            { observed () with
                Budget =
                    Some
                        { budget with
                            Deadline = current.AddMinutes 1.
                        }
            }

        let journal =
            RecordingJournal([ ObserverAppended(state.Sequence + 1L); ObserverAppended(state.Sequence + 2L) ])

        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        let planner = SynchronouslyBlockingPlanner(entered, release)

        let composition =
            ObserverComposition.create (ReadCapability()) planner (ReadbackCapability()) journal
        // The deadline must cover ordinary CI scheduling latency so the assertion tests
        // synchronous planner isolation rather than thread-pool admission speed.
        let request =
            { executionRequest state with
                IssuedAt = current.AddSeconds -1.
                ExpiresAt = current.AddSeconds 2.
            }

        let elapsed = Diagnostics.Stopwatch.StartNew()

        try
            let! outcome =
                ObserverRuntime.executePlanning TimeProvider.System composition request CancellationToken.None

            elapsed.Stop()
            Assert.True(entered.IsSet)

            match outcome with
            | PlanningOutcomePersistedUnknown(finalState, "planning-window-expired") ->
                Assert.Equal(request.Reservation, finalState.Reserved)
            | other -> failwithf "unexpected %A" other

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds 10.,
                $"observer exceeded its wall-clock bound: {elapsed.Elapsed}"
            )
        finally
            release.Set()
    }

[<Fact>]
let ``authoritative projection records acceptance and effect only through bound readback`` () =
    task {
        let approved, commandId = approvedState ()
        let approval = approved.Approvals[proposalId]

        let acceptance =
            {
                ProposalId = proposalId
                ApprovalSha256 = approval.ApprovalSha256
                Receipt =
                    {
                        CommandId = commandId
                        BodySha256 = sha "f"
                        Disposition = ReceiptDisposition.Accepted
                        Revision = Id.revision 8L
                        ProtocolVersion = Id.protocolVersion 1 0
                        Detail = "accepted"
                    }
                Provenance = readback
                AcceptedAt = now
            }

        let operationId = Id.operation (Guid.Parse "60000000-0000-0000-0000-000000000006")

        let completion =
            {
                CommandId = commandId
                OperationId = operationId
                Result = EffectApplied "provider-rev"
                Provenance = readback
                CompletedAt = now
            }

        let journal =
            RecordingJournal(
                [
                    ObserverAppended(approved.Sequence + 1L)
                    ObserverAppended(approved.Sequence + 2L)
                ]
            )

        let capability = FixedReadbackCapability(acceptance, [ completion ])

        let composition =
            ObserverComposition.create (ReadCapability()) (PlanningCapability()) capability journal

        let acceptanceRequest =
            {
                ObserverId = ObserverJournal.observerId sessionId
                State = approved
                ProposalId = proposalId
                PersistenceCommandId = Id.command (Guid.NewGuid())
                PrincipalId = "observer-reader"
                IssuedAt = now.AddSeconds -1.
                ExpiresAt = now.AddMinutes 1.
            }

        let! acceptanceOutcome =
            ObserverRuntime.refreshCommandAcceptance
                (ManualTimeProvider now)
                composition
                acceptanceRequest
                CancellationToken.None

        let accepted =
            match acceptanceOutcome with
            | CommandAcceptanceRefreshed state -> state
            | other -> failwithf "unexpected %A" other

        let effectRequest =
            {
                ObserverId = ObserverJournal.observerId sessionId
                State = accepted
                AcceptedCommandId = commandId
                OperationId = operationId
                PersistenceCommandId = Id.command (Guid.NewGuid())
                PrincipalId = "observer-reader"
                IssuedAt = now.AddSeconds -1.
                ExpiresAt = now.AddMinutes 1.
            }

        let! effectOutcome =
            ObserverRuntime.refreshEffectCompletion
                (ManualTimeProvider now)
                composition
                effectRequest
                CancellationToken.None

        let completed =
            match effectOutcome with
            | EffectCompletionRefreshed state -> state
            | other -> failwithf "unexpected %A" other

        let authoritative =
            ObserverProjection.fromRecovery { Events = []; State = completed }

        Assert.Equal<ProjectionStage list>(
            [ Proposed; Approved; DurableAcceptance; EffectComplete ],
            authoritative.Rows |> List.map _.Stage
        )

        Assert.All(
            journal.Requests,
            fun request ->
                Assert.Contains(
                    request.Events,
                    fun stored ->
                        match stored.Event with
                        | CommandAcceptanceRecorded _
                        | EffectCompletionRecorded _ -> true
                        | _ -> false
                )
        )
    }
