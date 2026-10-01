module FS.GG.Coordination.Orchestration.Observer.Tests.LearningOperationalOwnerRecordsTests

open System
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Orchestration.Observer

let private now = DateTimeOffset.Parse("2026-10-01T10:00:00Z")
let private sha c = String.replicate 64 c
let private proposalId = ProposalId.create(Guid.Parse "41000000-0000-0000-0000-000000000004")
let private roles =
    [ LearningOperationalOwnerRole.Root; LearningOperationalOwnerRole.Child; LearningOperationalOwnerRole.Retry
      LearningOperationalOwnerRole.Review; LearningOperationalOwnerRole.Rescue; LearningOperationalOwnerRole.Repair ]

let private state () =
    let workItems =
        roles
        |> List.mapi (fun index _ ->
            { Identity = WorkItemIdentity.create "R_owner" 1L $"I_owner_{index}" (int64 index + 10L)
              MembershipItemId = if index = 0 then "original" else $"member-{index}"
              Archived = index = 4 })
    let observation0 =
        { ProjectId = Id.project(Guid.Parse "11000000-0000-0000-0000-000000000001")
          SourceRevision = "protected-source"
          WorkflowRevision = Id.revision 7L
          Generation = Id.generation 3L
          ObservationSha256 = sha "0"
          Provenance =
            { Provider = "github-graphql"; QuerySha256 = sha "1"; EvidenceSha256 = sha "2"; CapturedAt = now.AddMinutes -10. }
          WorkItems = workItems
          NonWorkItemCount = 0 }
    let observation = { observation0 with ObservationSha256 = Observer.observationSha256 observation0 }
    let proposal =
        { ProposalId = proposalId; AttemptId = Id.attempt(Guid.Parse "31000000-0000-0000-0000-000000000003")
          ObservationSha256 = observation.ObservationSha256; WorkflowRevision = observation.WorkflowRevision
          Generation = observation.Generation; Scope = "learn-window"; NarrativeSha256 = sha "3"; Actions = []
          PlanSha256 = sha "4"; ProposedAt = now.AddMinutes -8. }
    let approval =
        { ProposalId = proposalId; PlanSha256 = proposal.PlanSha256; PlanningBudgetSha256 = sha "5"
          PrincipalId = "scheduler-owner"; Scope = proposal.Scope; ExpectedWorkflowRevision = observation.WorkflowRevision
          ExpectedGeneration = observation.Generation; CommandId = Id.command(Guid.Parse "51000000-0000-0000-0000-000000000005")
          CommandBodySha256 = sha "6"; Kind = ExplicitApproval; ApprovalSha256 = sha "7"; ApprovedAt = now.AddMinutes -7. }
    { Observer.initial with
        SessionId = Some(Id.session(Guid.Parse "21000000-0000-0000-0000-000000000002"))
        ProjectId = Some observation.ProjectId
        Sequence = 12L
        Observation = Some observation
        Proposals = Map.ofList [ proposalId, proposal ]
        CurrentProposal = Some proposalId
        Approvals = Map.ofList [ proposalId, approval ] }

let private input () =
    { WindowId = "window-2026-10"; OriginalItemId = "original"; ProposalId = proposalId
      Repository = "FS-GG/FS.GG.Coordination"; WorkClassId = "learn-01-current-focused"
      CalendarAdmissionBlock = "2026-10-a"; SeedReferenceSha256 = sha "8"
      SharedAllocationRosterReference = "shared-allocation-roster-1"
      Members =
        roles
        |> List.mapi (fun index role ->
            { ItemId = (if index = 0 then "original" else $"member-{index}")
              Role = role })
      ExpectedWorkflowRevision = Id.revision 7L; ExpectedGeneration = Id.generation 3L
      OptedInAt = now.AddHours -2.; EnrollmentOpensAt = now.AddHours -1.; EnrollmentClosesAt = now.AddHours 1.
      AdmittedAt = now.AddMinutes -1. }

let private decide state command =
    Observer.decide now state
        { CommandId = Id.command(Guid.NewGuid()); ExpectedSequence = state.Sequence; PrincipalId = "scheduler-owner"
          IssuedAt = now.AddMinutes -1.; ExpiresAt = now.AddMinutes 1.; Command = command }

[<Fact>]
let ``approved canonical six-role roster derives durable authority without future counters`` () =
    let source = state ()
    let decision = decide source (AdmitLearningOperationalOwnerWindow(input ()))
    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    let window =
        match decision.Events with
        | [ LearningOperationalOwnerWindowAdmitted value ] -> value
        | other -> failwithf "unexpected events %A" other
    Assert.Equal(6, window.Members.Length)
    Assert.Contains(window.Members, fun value -> value.Role = LearningOperationalOwnerRole.Rescue && value.ItemId = "member-4")
    Assert.Equal(source.Proposals[proposalId].PlanSha256, window.AcceptedPlanSha256)
    Assert.False(String.Equals((input()).SeedReferenceSha256, window.CoverageRosterSha256, StringComparison.Ordinal))
    Assert.All([ window.CanonicalWorkItemSha256; window.CoverageRosterSha256; window.SharedAllocationRosterSha256; window.AuthoritySha256; window.RecordSha256 ], fun value -> Assert.Equal(64, value.Length))

[<Fact>]
let ``stale approval generation expired window duplicate and incomplete roster refuse`` () =
    let source = state ()
    let stale = { input() with ExpectedGeneration = Id.generation 4L }
    let expired = { input() with EnrollmentClosesAt = now }
    let duplicate = { input() with Members = (input()).Members |> List.map (fun value -> if value.Role = LearningOperationalOwnerRole.Repair then { value with ItemId = "member-4" } else value) }
    let incomplete = { input() with Members = (input()).Members |> List.filter (fun value -> value.Role <> LearningOperationalOwnerRole.Review) }
    for value in [ stale; expired; duplicate; incomplete ] do
        Assert.Equal(ObserverRejected, (decide source (AdmitLearningOperationalOwnerWindow value)).Receipt.Disposition)

    let changedApproval =
        { source with
            Approvals =
                source.Approvals
                |> Map.add proposalId { source.Approvals[proposalId] with PlanSha256 = sha "9" }
        }

    Assert.Equal(
        ObserverRejected,
        (decide changedApproval (AdmitLearningOperationalOwnerWindow(input ()))).Receipt.Disposition
    )

[<Fact>]
let ``admission is immutable and explicit owner revocation is monotonic`` () =
    let source = state ()
    let admitted = decide source (AdmitLearningOperationalOwnerWindow(input ()))
    let admittedState = admitted.Events |> List.fold Observer.evolve source
    Assert.Equal(ObserverRejected, (decide admittedState (AdmitLearningOperationalOwnerWindow(input ()))).Receipt.Disposition)
    let revoke = { WindowId = "window-2026-10"; OriginalItemId = "original"; Reason = "operator-stop"; RevokedAt = now }
    let revoked = decide admittedState (RevokeLearningOperationalOwnerWindow revoke)
    Assert.Equal(ObserverAccepted, revoked.Receipt.Disposition)
    let revokedState = revoked.Events |> List.fold Observer.evolve admittedState
    Assert.Equal(ObserverRejected, (decide revokedState (RevokeLearningOperationalOwnerWindow revoke)).Receipt.Disposition)
