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
    let mutable currentSourceState = sourceState
    member _.SetSourceState value = currentSourceState <- value

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
                    Observation = currentSourceState.Observation
                }

            let sourceMatches =
                input
                |> Option.exists (fun value ->
                    let sourceWorkflowRevision, sourceGeneration =
                        match request.Command.Command with
                        | AssignPreparedLearningTreatment prepared ->
                            prepared.CurrentWorkflowRevision, prepared.CurrentGeneration
                        | _ -> value.ExpectedWorkflowRevision, value.ExpectedGeneration

                    value.SourceObserverId = ObserverJournal.observerId currentSourceState.SessionId.Value
                    && value.SourceSequence = currentSourceState.Sequence
                    && (currentSourceState.Observation
                        |> Option.exists (fun observation ->
                            value.SourceObservationSha256 = observation.ObservationSha256
                            && sourceWorkflowRevision = observation.WorkflowRevision
                            && sourceGeneration = observation.Generation)))

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
            InheritedDurableTreatment = None
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
    Assert.Equal(sha256 prepared.PreparedTreatment.RenderedInput, prepared.PreparedTreatment.RenderedInputSha256)
    Assert.Contains("docs/roadmaps/learn-01-context-shadow.md", System.Text.Encoding.UTF8.GetString prepared.PreparedTreatment.RenderedInput)
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
let ``descendant and retry keep immutable treatment while binding their actual current context`` () =
    let root = fixture ()
    let rootPrepared =
        LearningAssignmentAdapter.prepare root.Preparation
        |> Result.defaultWith (sprintf "root preparation refused: %A" >> failwith)

    let capabilities = UnusedCapabilities()
    let journal = SourceBoundJournal(root.SourceState)
    let composition =
        ObserverComposition.create
            (capabilities :> IProjectReadCapability)
            (capabilities :> IBoundedPlanningCapability)
            (capabilities :> ICommandReadbackCapability)
            (journal :> IObserverJournalStore)

    let persist sourceState prepared =
        journal.SetSourceState sourceState
        ObserverRuntime.assignPreparedLearningTreatment
            (FixedClock())
            composition
            { SourceObserverId = prepared.Assignment.SourceObserverId
              SourceState = sourceState
              PreparedTreatment = prepared.PreparedTreatment
              CommandId = Id.command(Guid.NewGuid())
              PrincipalId = "learning-owner"
              IssuedAt = now.AddMinutes -1.
              ExpiresAt = now.AddMinutes 5. }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let rootTreatment, rootBinding =
        match persist root.SourceState rootPrepared with
        | LearningTreatmentPersisted(treatment, binding) -> treatment, binding
        | other -> failwithf "root treatment was not persisted: %A" other

    let baseContext = root.ProposalRequest.ContextRequest.Value

    let advanceSource (state: ObserverState) identity revision generation =
        let prior = state.Observation.Value
        let draft =
            { prior with
                WorkflowRevision = revision
                Generation = generation
                ObservationSha256 = String.replicate 64 "0"
                WorkItems =
                    prior.WorkItems
                    @ [ { Identity = identity; MembershipItemId = $"PVTI_{WorkItemIdentity.persistenceId identity}"; Archived = false } ] }
        let observation = { draft with ObservationSha256 = Observer.observationSha256 draft }
        apply state (RecordProjectObservation observation)

    let prepareInherited itemIdentity relation sourceState =
        let childId = WorkItemIdentity.persistenceId itemIdentity
        let context =
            { baseContext with
                ItemId = childId
                Relation = relation
                InheritedTreatment = Some baseContext.Treatment }
        let proposalRequest =
            { root.ProposalRequest with
                ProposalId = $"learn-01.3-{childId}"
                ItemId = childId
                ContextRequest = Some context }
        let proposal =
            LearningProposal.propose proposalRequest
            |> Result.defaultWith (sprintf "inherited proposal refused: %A" >> failwith)
        let request =
            { root.Preparation with
                ProposalRequest = proposalRequest
                AcceptedProposal = proposal
                SourceState = sourceState
                InheritedDurableTreatment = Some rootTreatment
                AssignedAt = now }
        request,
        (LearningAssignmentAdapter.prepare request
         |> Result.defaultWith (sprintf "inherited preparation refused: %A" >> failwith))

    let childIdentity = WorkItemIdentity.create "R_learning" 101L "I_learning_child" 14L
    let childId = WorkItemIdentity.persistenceId childIdentity
    let childSource = advanceSource root.SourceState childIdentity (Id.revision 18L) (Id.generation 5L)
    let childRequest, childPrepared = prepareInherited childIdentity (Descendant itemId) childSource

    Assert.Equal(rootTreatment.ProposalSha256, childPrepared.Assignment.ProposalSha256)
    Assert.Equal(rootTreatment.ContextManifestSha256, childPrepared.Assignment.ContextManifestSha256)
    Assert.Equal(rootTreatment.WorkflowRevision, childPrepared.Assignment.ExpectedWorkflowRevision)
    Assert.Equal(rootTreatment.Generation, childPrepared.Assignment.ExpectedGeneration)
    Assert.NotEqual<string>(rootTreatment.ContextManifestSha256, childPrepared.PreparedTreatment.CurrentContextManifestSha256)
    Assert.NotEqual<string>(rootTreatment.ProposalSha256, childPrepared.PreparedTreatment.CurrentProposalSha256)
    Assert.Equal(Id.revision 18L, childPrepared.PreparedTreatment.CurrentWorkflowRevision)
    Assert.Equal(Id.generation 5L, childPrepared.PreparedTreatment.CurrentGeneration)
    Assert.Equal(sha256 childPrepared.PreparedTreatment.RenderedInput, childPrepared.PreparedTreatment.RenderedInputSha256)

    let childBinding =
        match persist childSource childPrepared with
        | LearningTreatmentPersisted(treatment, binding) ->
            Assert.Equal(rootTreatment, treatment)
            binding
        | other -> failwithf "child treatment binding was not persisted: %A" other

    Assert.Equal(rootTreatment.AssignmentSha256, childBinding.AssignmentSha256)
    Assert.Equal(childId, childBinding.ItemId)
    Assert.Equal(Descendant itemId, childBinding.Relation)

    let retryIdentity = WorkItemIdentity.create "R_learning" 101L "I_learning_retry" 15L
    let retrySource = advanceSource childSource retryIdentity (Id.revision 19L) (Id.generation 6L)
    let _, retryPrepared = prepareInherited retryIdentity (Retry childId) retrySource

    match persist retrySource retryPrepared with
    | LearningTreatmentPersisted(treatment, binding) ->
        Assert.Equal(rootTreatment, treatment)
        Assert.Equal(rootTreatment.AssignmentSha256, binding.AssignmentSha256)
        Assert.Equal(Retry childId, binding.Relation)
        Assert.NotEqual<string>(childPrepared.PreparedTreatment.CurrentContextManifestSha256, retryPrepared.PreparedTreatment.CurrentContextManifestSha256)
    | other -> failwithf "retry treatment binding was not persisted: %A" other

    match LearningAssignmentAdapter.prepare { childRequest with InheritedDurableTreatment = None } with
    | Error InheritedTreatmentMismatch -> ()
    | other -> failwithf "expected missing-parent treatment refusal, got %A" other

    match LearningAssignmentAdapter.prepare { childRequest with InheritedDurableTreatment = Some { rootTreatment with OriginalItemId = childId } } with
    | Error InheritedTreatmentMismatch -> ()
    | other -> failwithf "expected wrong-original treatment refusal, got %A" other

    let wrongArm = if rootTreatment.Arm = Focused then Current else Focused
    match LearningAssignmentAdapter.prepare { childRequest with InheritedDurableTreatment = Some { rootTreatment with Arm = wrongArm } } with
    | Error InheritedTreatmentMismatch -> ()
    | other -> failwithf "expected wrong-arm treatment refusal, got %A" other

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
