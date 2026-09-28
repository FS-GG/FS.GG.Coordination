module FS.GG.Coordination.Orchestration.Observer.Tests.LearningAssignmentAdapterTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Observer

let private now = DateTimeOffset.Parse("2026-09-29T20:00:00Z")
let private revision = "39cb47312586a8e5a0b949c89cfce4bcb4a1955c"
let private obligation = "preserve-original-treatment"
let private repository = "FS-GG/FS.GG.Coordination"
let private repoRoot =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "docs/roadmaps/learn-01-context-shadow.md")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "repository root with the real LEARN roadmap source was not found"
        else
            find directory.Parent

    find (DirectoryInfo(Directory.GetCurrentDirectory()))

let private sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private actualSource path =
    let bytes = File.ReadAllBytes(Path.Combine(repoRoot, path))

    {
        Repository = repository
        Path = path
        Revision = revision
        Sha256 = sha256 bytes
    }, bytes

let private workItem = WorkItemIdentity.create "R_learning" 101L "I_learning" 13L
let private itemId = WorkItemIdentity.persistenceId workItem
let private sessionId = Id.session(Guid.Parse "73f44a9b-4ab9-4198-aecb-622a157e0131")
let private projectId = Id.project(Guid.Parse "c909b750-f353-450a-9d01-2a272311932b")
let private workflowRevision = Id.revision 17L
let private generation = Id.generation 4L

let private budget =
    {
        TokenLimit = 100L
        RuntimeSecondsLimit = 60L
        CostMicrosLimit = 1_000L
        Deadline = now.AddHours 1.
    }

let private envelope (state: ObserverState) command =
    {
        CommandId = Id.command(Guid.NewGuid())
        ExpectedSequence = state.Sequence
        PrincipalId = "learning-owner"
        IssuedAt = now.AddMinutes -1.
        ExpiresAt = now.AddMinutes 5.
        Command = command
    }

let private apply (state: ObserverState) command =
    let decision = Observer.decide now state (envelope state command)
    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    decision.Events |> List.fold Observer.evolve state

let private observedState () =
    let opened = apply Observer.initial (OpenSession(sessionId, projectId, budget))

    let draft =
        {
            ProjectId = projectId
            SourceRevision = revision
            WorkflowRevision = workflowRevision
            Generation = generation
            ObservationSha256 = String.replicate 64 "0"
            Provenance =
                {
                    Provider = "github-graphql"
                    QuerySha256 = String.replicate 64 "1"
                    EvidenceSha256 = String.replicate 64 "2"
                    CapturedAt = now.AddMinutes -2.
                }
            WorkItems =
                [
                    {
                        Identity = workItem
                        MembershipItemId = "PVTI_learning"
                        Archived = false
                    }
                ]
            NonWorkItemCount = 0
        }

    let observation =
        { draft with
            ObservationSha256 = Observer.observationSha256 draft
        }

    apply opened (RecordProjectObservation observation)

type private Fixture =
    {
        ProposalRequest: LearningProposalRequest
        Proposal: LearningProposal
        SourceState: ObserverState
        RetrievedSources: RetrievedLearningSource list
        Preparation: LearningAssignmentPreparationRequest
    }

type private FixedClock() =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type private UnusedCapabilities() =
    interface IProjectReadCapability with
        member _.ObserveProject(_, _) = Task.FromResult(Error InvalidObservationProvenance)

    interface IBoundedPlanningCapability with
        member _.CreateProposal(_, _) = Task.FromResult(Error "not-run")

    interface ICommandReadbackCapability with
        member _.ReadCommandAcceptance(_, _) = Task.FromResult(Error "not-run")
        member _.ReadEffectCompletions(_, _) = Task.FromResult(Error "not-run")

type private SourceBoundJournal(sourceState: ObserverState) =
    let mutable events: ObserverStoredEvent list = []

    interface IObserverJournalStore with
        member _.RecoverObserver(_, _) =
            Task.FromResult(
                Ok
                    {
                        Events = events
                        State = events |> List.map _.Event |> Observer.replay
                    }
            )

        member _.AppendObserver(request, _) =
            let input = ObserverCommand.tryLearningTreatmentInput request.Command.Command
            let state = events |> List.map _.Event |> Observer.replay

            let decisionState =
                { state with
                    Observation = sourceState.Observation
                }

            let sourceMatches =
                input
                |> Option.exists (fun value ->
                    value.SourceObserverId = ObserverJournal.observerId sourceState.SessionId.Value
                    && value.SourceSequence = sourceState.Sequence
                    && (sourceState.Observation
                        |> Option.exists (fun observation ->
                            value.SourceObservationSha256 = observation.ObservationSha256
                            && value.ExpectedWorkflowRevision = observation.WorkflowRevision
                            && value.ExpectedGeneration = observation.Generation)))

            let decision = Observer.decide request.ReceivedAt decisionState request.Command

            if
                not sourceMatches
                || decision.Receipt.Disposition <> ObserverAccepted
                || decision.Events <> (request.Events |> List.map _.Event)
            then
                Task.FromResult(ObserverInvalidAppend "events-do-not-match-command-decision")
            else
                events <- events @ request.Events
                Task.FromResult(ObserverAppended(int64 events.Length))

let private fixture () =
    let planSource, planBytes = actualSource "docs/roadmaps/learn-01-context-shadow.md"
    let instructionSource, instructionBytes = actualSource "AGENTS.md"

    let plan =
        {
            PlanId = "learn-01.3-executor-observation"
            PlanSha256 = planSource.Sha256
            Source = planSource
            WorkClassId = LearningContext.WorkClassId
            ContractId = LearningContext.ContractId
            ContractRevision = revision
            AuthoritativeObligations = [ obligation ]
            State = Reusable
        }

    let instruction =
        {
            ReferenceId = "repository-guidance"
            Source = instructionSource
            Class = Mandatory
            Obligations = [ obligation ]
            EstimatedBytes = int64 instructionBytes.Length
            SelectedForFocused = true
            Trust = GoverningInstruction
            ClaimsInstructionAuthority = false
            RetrievalMethod = "exact-repository-bytes"
            InclusionReason = "governing repository instruction"
        }

    let contextRequest =
        {
            ItemId = itemId
            OriginalItemId = itemId
            Relation = Original
            Treatment =
                {
                    OriginalItemId = itemId
                    Arm = Focused
                    ShadowBindingSha256 = String.replicate 64 "3"
                }
            InheritedTreatment = None
            Plan = Some plan
            ExpectedPlanId = plan.PlanId
            ExpectedPlanSha256 = plan.PlanSha256
            ExpectedPlanSource = plan.Source
            ExpectedContractRevision = plan.ContractRevision
            RequiredMandatoryReferenceIds = [ instruction.ReferenceId ]
            RequiredAuthoritativeObligations = [ obligation ]
            References = [ instruction ]
            Capacity =
                {
                    MaximumReferences = 4
                    MaximumEstimatedBytes = int64 (planBytes.Length + instructionBytes.Length + 1024)
                    MaximumConcurrentPreviews = 2
                    ActivePreviews = 0
                    ReservedPreviews = 0
                }
            SyntheticShadowOnly = true
        }

    let proposalRequest =
        {
            ProposalId = "learn-01.3-j1-keep"
            ItemId = itemId
            OriginalItemId = itemId
            Action = Keep
            ContextRequest = Some contextRequest
            InvestigationQuestions = []
            Slices = []
            IntegrationContract = None
            DirectSmallRequested = false
            DirectSmallEvidence = None
            SyntheticShadowOnly = true
        }

    let proposal =
        LearningProposal.propose proposalRequest
        |> Result.defaultWith (sprintf "proposal refused: %A" >> failwith)

    let sourceState = observedState ()
    let retrieved = [ { Source = planSource; Content = planBytes }; { Source = instructionSource; Content = instructionBytes } ]

    let preparation =
        {
            ProposalRequest = proposalRequest
            AcceptedProposal = proposal
            SourceState = sourceState
            ExpectedContractId = LearningContext.ContractId
            ExpectedContractRevision = revision
            ExpectedAuthoritativeObligations = [ obligation ]
            RetrievedSources = retrieved
            AssignedAt = now.AddSeconds -1.
        }

    {
        ProposalRequest = proposalRequest
        Proposal = proposal
        SourceState = sourceState
        RetrievedSources = retrieved
        Preparation = preparation
    }

[<Fact>]
let ``real keep proposal and source bytes become a replayable assignment without planner use`` () =
    let value = fixture ()

    let prepared =
        LearningAssignmentAdapter.prepare value.Preparation
        |> Result.defaultWith (sprintf "preparation refused: %A" >> failwith)

    Assert.Equal(LearningPlanningDisposition.ReuseValidPlan, prepared.Disposition)
    Assert.True(prepared.Assignment.Planner.IsNone)
    Assert.False(prepared.Assignment.DirectSmallEligible)
    Assert.Equal(value.Proposal.CanonicalSha256, prepared.Assignment.ProposalSha256)
    Assert.Equal(value.Proposal.ContextManifest.Value.CanonicalSha256, prepared.Assignment.ContextManifestSha256)
    Assert.Empty(value.SourceState.Attempts)
    Assert.Equal(0L, value.SourceState.Reserved.Tokens)
    Assert.Equal(0L, value.SourceState.Used.Tokens)

    let canonicalState =
        { Observer.initial with
            Observation = value.SourceState.Observation
        }

    let raw = Observer.decide now canonicalState (envelope canonicalState (AssignLearningTreatment prepared.Assignment))
    Assert.Equal(ObserverRejected, raw.Receipt.Disposition)
    Assert.Equal("invalid-learning-treatment-assignment", raw.Receipt.Detail)

    let decision =
        Observer.decide
            now
            canonicalState
            (envelope canonicalState (AssignPreparedLearningTreatment prepared.PreparedTreatment))

    Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
    let encoded = decision.Events |> List.map ObserverEventCodec.encode
    let decoded = encoded |> List.map (ObserverEventCodec.tryDecode >> Result.defaultWith failwith)
    Assert.Equal<byte array list>(encoded, decoded |> List.map ObserverEventCodec.encode)

    let recovered = Observer.replay decoded
    let treatment = recovered.LearningTreatments[itemId]
    Assert.Equal(prepared.Assignment.ProposalSha256, treatment.ProposalSha256)
    Assert.Equal(prepared.Assignment.ContextManifestSha256, treatment.ContextManifestSha256)
    Assert.True(treatment.Planner.IsNone)

    let capabilities = UnusedCapabilities()
    let journal = SourceBoundJournal(value.SourceState)

    let composition =
        ObserverComposition.create
            (capabilities :> IProjectReadCapability)
            (capabilities :> IBoundedPlanningCapability)
            (capabilities :> ICommandReadbackCapability)
            (journal :> IObserverJournalStore)

    let persistenceRequest =
        {
            SourceObserverId = prepared.Assignment.SourceObserverId
            SourceState = value.SourceState
            PreparedTreatment = prepared.PreparedTreatment
            CommandId = Id.command(Guid.NewGuid())
            PrincipalId = "learning-owner"
            IssuedAt = now.AddMinutes -1.
            ExpiresAt = now.AddMinutes 5.
        }

    let persisted =
        ObserverRuntime.assignPreparedLearningTreatment
            (FixedClock())
            composition
            persistenceRequest
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    match persisted with
    | LearningTreatmentPersisted(durable, binding) ->
        Assert.Equal(itemId, durable.OriginalItemId)
        Assert.Equal(durable.AssignmentSha256, binding.AssignmentSha256)
    | other -> failwithf "prepared treatment was not durably appended: %A" other

    let replayed =
        ObserverRuntime.assignPreparedLearningTreatment
            (FixedClock())
            composition
            { persistenceRequest with CommandId = Id.command(Guid.NewGuid()) }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    match replayed with
    | LearningTreatmentReplayed(durable, _) -> Assert.Equal(itemId, durable.OriginalItemId)
    | other -> failwithf "prepared treatment did not replay from durable state: %A" other

[<Fact>]
let ``preparation verifies actual content obligations source head and canonical work item`` () =
    let value = fixture ()
    let first = value.RetrievedSources.Head
    let changed = Array.copy first.Content
    changed[0] <- changed[0] ^^^ 0xffuy

    match LearningAssignmentAdapter.prepare { value.Preparation with RetrievedSources = { first with Content = changed } :: value.RetrievedSources.Tail } with
    | Error(RetrievedSourceContentMismatch(_, path, sourceRevision)) ->
        Assert.Equal(first.Source.Path, path)
        Assert.Equal(revision, sourceRevision)
    | other -> failwithf "expected changed-content refusal, got %A" other

    match LearningAssignmentAdapter.prepare { value.Preparation with RetrievedSources = value.RetrievedSources.Tail } with
    | Error RetrievedSourceSetMismatch -> ()
    | other -> failwithf "expected missing-source refusal, got %A" other

    match LearningAssignmentAdapter.prepare { value.Preparation with ExpectedAuthoritativeObligations = [ "different" ] } with
    | Error AuthoritativeObligationMismatch -> ()
    | other -> failwithf "expected obligation refusal, got %A" other

    let stale =
        { value.SourceState.Observation.Value with
            SourceRevision = "different-head"
        }

    match LearningAssignmentAdapter.prepare { value.Preparation with SourceState = { value.SourceState with Observation = Some stale } } with
    | Error SourceObserverStateInvalid -> ()
    | other -> failwithf "expected stale-source refusal, got %A" other

    let movedDraft =
        { value.SourceState.Observation.Value with
            SourceRevision = "newer-head"
            ObservationSha256 = String.replicate 64 "0"
        }

    let moved = { movedDraft with ObservationSha256 = Observer.observationSha256 movedDraft }

    match LearningAssignmentAdapter.prepare { value.Preparation with SourceState = { value.SourceState with Observation = Some moved } } with
    | Error(SourceRevisionMismatch(_, _, sourceRevision)) -> Assert.Equal(revision, sourceRevision)
    | other -> failwithf "expected exact-source-revision refusal, got %A" other

    let wrongObservation =
        let draft =
            { value.SourceState.Observation.Value with
                WorkItems = []
                ObservationSha256 = String.replicate 64 "0"
            }

        { draft with ObservationSha256 = Observer.observationSha256 draft }

    match LearningAssignmentAdapter.prepare { value.Preparation with SourceState = { value.SourceState with Observation = Some wrongObservation } } with
    | Error(CanonicalWorkItemMissing missing) -> Assert.Equal(itemId, missing)
    | other -> failwithf "expected canonical-item refusal, got %A" other

[<Fact>]
let ``planning disposition distinguishes fixed planner and eligible direct-small`` () =
    let baseRequest =
        {
            ProposalId = "learn-01.3-create"
            ItemId = itemId
            OriginalItemId = itemId
            Action = Create
            ContextRequest = None
            InvestigationQuestions = []
            Slices = []
            IntegrationContract = None
            DirectSmallRequested = false
            DirectSmallEvidence = None
            SyntheticShadowOnly = true
        }

    let planned = LearningProposal.propose baseRequest |> Result.defaultWith (sprintf "%A" >> failwith)
    Assert.Equal(Ok LearningPlanningDisposition.Planned, LearningAssignmentAdapter.tryPlanningDisposition planned)

    let evidence =
        {
            SingleRepository = true
            StableContract = true
            KnownImplementationLocation = true
            ExistingTestBoundary = true
            NoProtectedOperation = true
            NoCrossRepositoryContractChange = true
            EstimatedTouchedFiles = 2
            EvidenceReferences = [ "adapter"; "tests" ]
        }

    let direct =
        LearningProposal.propose { baseRequest with DirectSmallRequested = true; DirectSmallEvidence = Some evidence }
        |> Result.defaultWith (sprintf "%A" >> failwith)

    Assert.Equal(Ok LearningPlanningDisposition.DirectSmall, LearningAssignmentAdapter.tryPlanningDisposition direct)

[<Fact>]
let ``prepared command proof cannot be reconstructed from caller supplied JSON`` () =
    Assert.Empty(typeof<PreparedLearningTreatment>.GetConstructors())

    let crafted =
        """{"contractVersion":"learn-01-assignment-preparation/1","disposition":"reuseValidPlan","input":{}}"""

    Assert.ThrowsAny<NotSupportedException>(fun () ->
        JsonSerializer.Deserialize<PreparedLearningTreatment>(crafted) |> ignore)
    |> ignore
