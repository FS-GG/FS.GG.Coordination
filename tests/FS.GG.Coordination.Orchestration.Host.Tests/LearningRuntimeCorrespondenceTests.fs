module FS.GG.Coordination.Orchestration.Host.Tests.LearningRuntimeCorrespondenceTests

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.Observer
open FS.GG.Coordination.Orchestration.Host.Tests.HostTests

let private sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private trace id =
    LearningTrace.loadAll () |> List.find (fun value -> value.Id = id)

let private preparedRoot () =
    let repoRoot =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "docs/roadmaps/learn-01-context-shadow.md")) then
                directory.FullName
            elif isNull directory.Parent then
                failwith "repository root missing"
            else
                find directory.Parent

        find (DirectoryInfo(Directory.GetCurrentDirectory()))

    let source path =
        let bytes = File.ReadAllBytes(Path.Combine(repoRoot, path))

        {
            Repository = "FS-GG/FS.GG.Coordination"
            Path = path
            Revision = "39cb47312586a8e5a0b949c89cfce4bcb4a1955c"
            Sha256 = sha256 bytes
        },
        bytes

    let planSource, planBytes = source "docs/roadmaps/learn-01-context-shadow.md"
    let instructionSource, instructionBytes = source "AGENTS.md"
    let itemId = WorkItemIdentity.persistenceId Fixture.permit.SubjectId
    let obligation = "preserve-original-treatment"

    let plan =
        {
            PlanId = "learn-01.3-executor-observation"
            PlanSha256 = planSource.Sha256
            Source = planSource
            WorkClassId = LearningContext.WorkClassId
            ContractId = LearningContext.ContractId
            ContractRevision = planSource.Revision
            AuthoritativeObligations = [ obligation ]
            State = Reusable
        }

    let instruction =
        {
            ReferenceId = "repository-guidance"
            Source = instructionSource
            Class = Mandatory
            Obligations = [ obligation ]
            EstimatedBytes = instructionBytes.LongLength
            SelectedForFocused = true
            Trust = GoverningInstruction
            ClaimsInstructionAuthority = false
            RetrievalMethod = "exact-repository-bytes"
            InclusionReason = "governing instruction"
        }

    let context =
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
                    MaximumEstimatedBytes = planBytes.LongLength + instructionBytes.LongLength + 1024L
                    MaximumConcurrentPreviews = 1
                    ActivePreviews = 0
                    ReservedPreviews = 0
                }
            SyntheticShadowOnly = true
        }

    let proposalRequest =
        {
            ProposalId = "learn-j6-root"
            ItemId = itemId
            OriginalItemId = itemId
            Action = Keep
            ContextRequest = Some context
            InvestigationQuestions = []
            Slices = []
            IntegrationContract = None
            DirectSmallRequested = false
            DirectSmallEvidence = None
            SyntheticShadowOnly = true
        }

    let proposal =
        LearningProposal.propose proposalRequest
        |> Result.defaultWith (sprintf "%A" >> failwith)

    let session = Id.session (Guid.Parse "73f44a9b-4ab9-4198-aecb-622a157e0131")

    let budget: PlanningBudget =
        {
            TokenLimit = 100L
            RuntimeSecondsLimit = 60L
            CostMicrosLimit = 1000L
            Deadline = Fixture.now.AddHours 1.
        }

    let command (state: ObserverState) value =
        {
            CommandId = Id.command (Guid.NewGuid())
            ExpectedSequence = state.Sequence
            PrincipalId = "pilot-route"
            IssuedAt = Fixture.now.AddMinutes -1.
            ExpiresAt = Fixture.now.AddMinutes 1.
            Command = value
        }

    let apply state value =
        let decision = Observer.decide Fixture.now state (command state value)
        Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
        decision.Events |> List.fold Observer.evolve state

    let projectId = Id.project (Guid.Parse "c909b750-f353-450a-9d01-2a272311932b")
    let opened = apply Observer.initial (OpenSession(session, projectId, budget))

    let observation0 =
        {
            ProjectId = projectId
            SourceRevision = planSource.Revision
            WorkflowRevision = Id.revision 7L
            Generation = Id.generation 1L
            ObservationSha256 = String.replicate 64 "0"
            Provenance =
                {
                    Provider = "github-graphql"
                    QuerySha256 = String.replicate 64 "1"
                    EvidenceSha256 = String.replicate 64 "2"
                    CapturedAt = Fixture.now.AddMinutes -2.
                }
            WorkItems =
                [
                    {
                        Identity = Fixture.permit.SubjectId
                        MembershipItemId = "PVTI_learning"
                        Archived = false
                    }
                ]
            NonWorkItemCount = 0
        }

    let observation =
        { observation0 with
            ObservationSha256 = Observer.observationSha256 observation0
        }

    let observed = apply opened (RecordProjectObservation observation)

    let assignmentRequest =
        {
            ProposalRequest = proposalRequest
            AcceptedProposal = proposal
            SourceState = observed
            ExpectedContractId = LearningContext.ContractId
            ExpectedContractRevision = plan.ContractRevision
            ExpectedAuthoritativeObligations = [ obligation ]
            InheritedDurableTreatment = None
            RetrievedSources =
                [
                    {
                        Source = planSource
                        Content = planBytes
                    }
                    {
                        Source = instructionSource
                        Content = instructionBytes
                    }
                ]
            AssignedAt = Fixture.now.AddSeconds -1.
        }

    let prepared =
        LearningAssignmentAdapter.prepare assignmentRequest
        |> Result.defaultWith (sprintf "%A" >> failwith)

    let input = prepared.Assignment

    let treatment =
        {
            SourceObserverId = input.SourceObserverId
            SourceSequence = input.SourceSequence
            SourceObservationSha256 = input.SourceObservationSha256
            OriginalItemId = input.OriginalItemId
            Arm = input.Arm
            ProposalSha256 = input.ProposalSha256
            ContextManifestSha256 = input.ContextManifestSha256
            Planner = input.Planner
            Worker = input.Worker
            DirectSmallEligible = input.DirectSmallEligible
            WorkflowRevision = input.ExpectedWorkflowRevision
            Generation = input.ExpectedGeneration
            OwnerPrincipalId = "pilot-route"
            AssignmentSha256 = Observer.learningTreatmentSha256 "pilot-route" input
            AssignedAt = input.AssignedAt
        }

    let binding =
        {
            ItemId = itemId
            OriginalItemId = itemId
            Relation = Original
            AssignmentSha256 = treatment.AssignmentSha256
            OwnerPrincipalId = treatment.OwnerPrincipalId
            BoundAt = Fixture.now
        }

    let durableState =
        { observed with
            LearningTreatments = Map.ofList [ itemId, treatment ]
            LearningTreatmentBindings = Map.ofList [ itemId, binding ]
        }

    prepared.PreparedTreatment, treatment, binding, durableState, planSource.Sha256, assignmentRequest

let private preparedInherited relation (treatment: DurableLearningTreatment) =
    let original = Fixture.permit.SubjectId

    let child =
        match relation with
        | Descendant _ -> WorkItemIdentity.create "R_learning_child" 1L "I_learning_child" 1L
        | Retry _ -> WorkItemIdentity.create "R_learning_retry" 1L "I_learning_retry" 1L
        | Original -> invalidArg (nameof relation) "inherited scenario requires child or retry"

    let originalId = WorkItemIdentity.persistenceId original
    let childId = WorkItemIdentity.persistenceId child
    let revision = "39cb47312586a8e5a0b949c89cfce4bcb4a1955c"
    let planBytes = Encoding.UTF8.GetBytes "canonical reusable learning plan"
    let instructionBytes = Encoding.UTF8.GetBytes "canonical repository instruction"

    let planSource =
        {
            Repository = "FS-GG/FS.GG.Coordination"
            Path = "docs/roadmaps/learn-01-context-shadow.md"
            Revision = revision
            Sha256 = sha256 planBytes
        }

    let instructionSource =
        {
            Repository = "FS-GG/FS.GG.Coordination"
            Path = "AGENTS.md"
            Revision = revision
            Sha256 = sha256 instructionBytes
        }

    let obligation = "preserve-original-treatment"

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
            EstimatedBytes = instructionBytes.LongLength
            SelectedForFocused = true
            Trust = GoverningInstruction
            ClaimsInstructionAuthority = false
            RetrievalMethod = "exact-repository-bytes"
            InclusionReason = "governing instruction"
        }

    let synthetic =
        {
            OriginalItemId = originalId
            Arm = treatment.Arm
            ShadowBindingSha256 = String.replicate 64 "3"
        }

    let context =
        {
            ItemId = childId
            OriginalItemId = originalId
            Relation = relation
            Treatment = synthetic
            InheritedTreatment = Some synthetic
            Plan = Some plan
            ExpectedPlanId = plan.PlanId
            ExpectedPlanSha256 = plan.PlanSha256
            ExpectedPlanSource = plan.Source
            ExpectedContractRevision = revision
            RequiredMandatoryReferenceIds = [ instruction.ReferenceId ]
            RequiredAuthoritativeObligations = [ obligation ]
            References = [ instruction ]
            Capacity =
                {
                    MaximumReferences = 4
                    MaximumEstimatedBytes = 4096L
                    MaximumConcurrentPreviews = 1
                    ActivePreviews = 0
                    ReservedPreviews = 0
                }
            SyntheticShadowOnly = true
        }

    let proposalRequest =
        {
            ProposalId = "learn-j6-inherited"
            ItemId = childId
            OriginalItemId = originalId
            Action = Keep
            ContextRequest = Some context
            InvestigationQuestions = []
            Slices = []
            IntegrationContract = None
            DirectSmallRequested = false
            DirectSmallEvidence = None
            SyntheticShadowOnly = true
        }

    let proposal =
        LearningProposal.propose proposalRequest
        |> Result.defaultWith (sprintf "%A" >> failwith)

    let session = Id.session (Guid.Parse "83f44a9b-4ab9-4198-aecb-622a157e0131")

    let budget: PlanningBudget =
        {
            TokenLimit = 100L
            RuntimeSecondsLimit = 60L
            CostMicrosLimit = 1000L
            Deadline = Fixture.now.AddHours 1.
        }

    let command (state: ObserverState) value =
        {
            CommandId = Id.command (Guid.NewGuid())
            ExpectedSequence = state.Sequence
            PrincipalId = "pilot-route"
            IssuedAt = Fixture.now.AddMinutes -1.
            ExpiresAt = Fixture.now.AddMinutes 1.
            Command = value
        }

    let apply state value =
        let decision = Observer.decide Fixture.now state (command state value)
        Assert.Equal(ObserverAccepted, decision.Receipt.Disposition)
        decision.Events |> List.fold Observer.evolve state

    let projectId = Id.project (Guid.Parse "d909b750-f353-450a-9d01-2a272311932b")
    let opened = apply Observer.initial (OpenSession(session, projectId, budget))

    let observation0 =
        {
            ProjectId = projectId
            SourceRevision = revision
            WorkflowRevision = Id.revision 8L
            Generation = Id.generation 1L
            ObservationSha256 = String.replicate 64 "0"
            Provenance =
                {
                    Provider = "github-graphql"
                    QuerySha256 = String.replicate 64 "1"
                    EvidenceSha256 = String.replicate 64 "2"
                    CapturedAt = Fixture.now
                }
            WorkItems =
                [
                    {
                        Identity = original
                        MembershipItemId = "PVTI_original"
                        Archived = false
                    }
                    {
                        Identity = child
                        MembershipItemId = "PVTI_child"
                        Archived = false
                    }
                ]
            NonWorkItemCount = 0
        }

    let observation =
        { observation0 with
            ObservationSha256 = Observer.observationSha256 observation0
        }

    let observed = apply opened (RecordProjectObservation observation)

    LearningAssignmentAdapter.prepare
        {
            ProposalRequest = proposalRequest
            AcceptedProposal = proposal
            SourceState = observed
            ExpectedContractId = LearningContext.ContractId
            ExpectedContractRevision = revision
            ExpectedAuthoritativeObligations = [ obligation ]
            InheritedDurableTreatment = Some treatment
            RetrievedSources =
                [
                    {
                        Source = planSource
                        Content = planBytes
                    }
                    {
                        Source = instructionSource
                        Content = instructionBytes
                    }
                ]
            AssignedAt = Fixture.now
        }
    |> Result.map (fun prepared -> prepared, observed, child)

let private selectionFor (prepared: PreparedLearningTreatment) =
    let request =
        { Fixture.preparationRequest () with
            RequestedModel = prepared.Input.Worker.Model
            RequestedEffort = prepared.Input.Worker.Effort
            InputMediaType = "text/plain; charset=utf-8"
        }

    let provider =
        {
            Provider = "codex"
            AdapterVersion = "qualification"
        }

    let executable =
        {
            Path = "/controlled/codex"
            Version = Some "0.158.0"
            Sha256 = Some(String.replicate 64 "e")
        }

    let query =
        {
            Provider = provider
            Executable = executable
            Requested =
                {
                    Model = Some request.RequestedModel
                    Effort = Some request.RequestedEffort
                }
            MaximumAge = TimeSpan.FromMinutes 5.
        }

    let evidence =
        {
            Schema = LearningSelectionEvidence.schema
            Provider = provider
            Executable = executable
            Requested = query.Requested
            Status = LearningCapabilityStatus.Supported
            Provenance = "controlled-qualification-record"
            ObservedAt = Fixture.now.AddSeconds -1.
            ExpiresAt = Fixture.now.AddMinutes 1.
        }

    request, query, evidence

type private ExactBindingStore() =
    let values = Dictionary<Guid * Guid, LearningExecutionBinding>()
    member _.Count = values.Count

    interface ILearningExecutionBindingStore with
        member _.BindLearningExecution(value, _) =
            match LearningExecutionBinding.validate value with
            | Error reason -> Task.FromResult(Error reason)
            | Ok value ->
                let key = value.AssignmentId, value.AttemptId

                match values.TryGetValue key with
                | false, _ ->
                    values.Add(key, value)
                    Task.FromResult(Ok value)
                | true, prior when prior = value -> Task.FromResult(Ok prior)
                | true, _ -> Task.FromResult(Error "learning-execution-binding-conflict")

        member _.ReadLearningExecution(assignmentId, attemptId, _) =
            match values.TryGetValue((assignmentId, attemptId)) with
            | true, value -> Task.FromResult(Ok value)
            | _ -> Task.FromResult(Error "learning-execution-binding-missing")

type private ExactOperationalWindowStore(initial: LearningOperationalWindowBinding option) =
    let mutable value = initial
    member _.Value = value

    interface ILearningOperationalWindowStore with
        member _.BindLearningOperationalWindow(binding, _) =
            match LearningOperationalWindow.validate binding, value with
            | Error reason, _ -> Task.FromResult(Error reason)
            | Ok binding, None ->
                value <- Some binding
                Task.FromResult(Ok binding)
            | Ok binding, Some prior when prior = binding -> Task.FromResult(Ok prior)
            | Ok _, Some _ -> Task.FromResult(Error "learning-operational-window-conflict")

        member _.ReadLearningOperationalWindow(windowId, originalItemId, _) =
            match value with
            | Some binding when binding.WindowId = windowId && binding.OriginalItemId = originalItemId ->
                Task.FromResult(Ok binding)
            | _ -> Task.FromResult(Error "learning-operational-window-missing")

type private ExactOperationalReadinessSource(initial: LearningOperationalReadinessSnapshot option) =
    let mutable value = initial

    member _.Value
        with get () = value
        and set next = value <- next

    interface ILearningOperationalReadinessSource with
        member _.ReadLearningOperationalReadiness(key, _) =
            match value with
            | Some snapshot when
                snapshot.Request.WindowId = key.WindowId
                && snapshot.Request.OriginalItemId = key.OriginalItemId
                ->
                Task.FromResult(Ok snapshot)
            | _ -> Task.FromResult(Error "controlled-readiness-unavailable")

type private SequencedOperationalReadinessSource
    (first: LearningOperationalReadinessSnapshot, second: LearningOperationalReadinessSnapshot) =
    let mutable reads = 0
    member _.Reads = reads

    interface ILearningOperationalReadinessSource with
        member _.ReadLearningOperationalReadiness(key, _) =
            reads <- reads + 1
            let snapshot = if reads = 1 then first else second

            if
                snapshot.Request.WindowId = key.WindowId
                && snapshot.Request.OriginalItemId = key.OriginalItemId
            then
                Task.FromResult(Ok snapshot)
            else
                Task.FromResult(Error "controlled-readiness-unavailable")

type private ExactClock(now: DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow() = now

type private ExactLearningProvider(evidence: LearningSelectionEvidence) =
    let mutable observations = 0
    member _.Observations = observations

    interface ILearningExecutionProvider with
        member _.ObserveLearningSelection(requested, _) =
            observations <- observations + 1
            Task.FromResult { evidence with Requested = requested }

        member _.LaunchLearning(_, _) =
            Task.FromException<LaunchResult>(InvalidOperationException "Host admission must not launch the provider")

type private ComposedTransitioningReadinessSource
    (
        now: DateTimeOffset,
        key: LearningOperationalWindowKey,
        authority: LearningOperationalAuthorityRecord,
        cohort: LearningOperationalCohortRecord,
        prospective: LearningOperationalCensusRecord,
        assigned: LearningOperationalCensusRecord
    ) =
    let mutable reads = 0
    member _.Reads = reads

    interface ILearningOperationalReadinessSource with
        member _.ReadLearningOperationalReadiness(actualKey, _) =
            reads <- reads + 1

            if actualKey <> key then
                Task.FromResult(Error "controlled-readiness-key-refused")
            else
                let census = if reads = 1 then prospective else assigned

                LearningOperationalWindow.composeAuthoritativeReadiness
                    now
                    (TimeSpan.FromMinutes 10.)
                    key
                    authority
                    cohort
                    census
                |> Task.FromResult

let private operationalWindow
    (assignedAt: DateTimeOffset)
    (originalItemId: string)
    (arm: LearningContextArm)
    (admission: MainAdmissionPreparationRequest)
    acceptedPlanSha256
    canonicalWorkItemSha256
    =
    let armName =
        match arm with
        | Current -> "current"
        | Focused -> "focused"

    let request seed : LearningOperationalWindowRequest =
        {
            Enabled = true
            WindowId = "learn-01.4-controlled-window"
            SeedReferenceSha256 = seed
            Repository = LearningOperationalWindow.policyRepository
            CalendarAdmissionBlock = "2026-10-01/2026-10-29"
            OriginalItemId = originalItemId
            AuthorityId = "pilot-route"
            AuthorityRevision = string admission.WorkflowRevision
            AuthoritySha256 = admission.RouteEvidenceSha256
            OptedInAt = assignedAt.AddMinutes -2.
            EnrollmentOpensAt = assignedAt.AddMinutes -1.
            EnrollmentClosesAt = assignedAt.AddDays 28.
        }

    let selected =
        [ 0..255 ]
        |> List.map (fun value -> request (value.ToString("x2") |> String.replicate 32))
        |> List.find (fun candidate -> LearningOperationalWindow.deriveArm candidate = armName)

    let key = { WindowId = selected.WindowId; OriginalItemId = originalItemId }

    let source producer record observedAt =
        {
            ProducerId = producer
            Revision = string admission.WorkflowRevision
            RecordId = record
            ObservedAt = observedAt
        }

    let roles = [ "root"; "child"; "retry"; "review"; "rescue"; "repair" ]

    let authority =
        {
            Key = key
            Source = source "observer-journal" "authority" (assignedAt.AddMinutes -1.)
            Enabled = true
            Repository = selected.Repository
            CalendarAdmissionBlock = selected.CalendarAdmissionBlock
            SeedReferenceSha256 = selected.SeedReferenceSha256
            AuthorityId = selected.AuthorityId
            AuthorityRevision = selected.AuthorityRevision
            OptedInAt = selected.OptedInAt
            EnrollmentOpensAt = selected.EnrollmentOpensAt
            EnrollmentClosesAt = selected.EnrollmentClosesAt
            RevokedAt = None
        }

    let cohort =
        {
            Key = key
            Source = source "observer-journal" "cohort" (assignedAt.AddMinutes -1.)
            AppliedAt = assignedAt.AddMinutes -2.
            AcceptedPlanSha256 = acceptedPlanSha256
            CanonicalWorkItemSha256 = canonicalWorkItemSha256
            Members =
                roles
                |> List.mapi (fun index role ->
                    {
                        ItemId = if index = 0 then originalItemId else $"{originalItemId}-{role}"
                        OriginalItemId = originalItemId
                        Role = role
                    })
        }

    let prospectiveMembers =
        cohort.Members
        |> List.map (fun memberValue ->
            {
                ItemId = memberValue.ItemId
                OriginalItemId = originalItemId
                Role = memberValue.Role
                State = "prospective"
                Source = source "execution-census" ($"prospective-{memberValue.Role}") (assignedAt.AddMinutes -1.)
                NativeUsageSha256 = None
                SharedCostSha256 = None
                Execution = None
            })

    let prospective =
        {
            Key = key
            Source = source "execution-census" "prospective" (assignedAt.AddMinutes -1.)
            InstalledCustody = Some(source "installed-owner" "custody" (assignedAt.AddMinutes -1.))
            ProviderCapability = Some(source "installed-owner" "capability" (assignedAt.AddMinutes -1.))
            NativeDeliveryRevision = "no-native-delivery-before-assignment"
            Members = prospectiveMembers
        }

    let assigned =
        { prospective with
            Source = source "execution-census" "assigned" assignedAt
            Members =
                prospectiveMembers
                |> List.map (fun memberValue ->
                    if memberValue.Role = "root" then
                        { memberValue with
                            State = "assigned"
                            Source = source "execution-census" "assigned-root" assignedAt
                            Execution =
                                Some
                                    {
                                        AssignmentId = admission.ProcessOperationId
                                        AttemptId = admission.AttemptId
                                        Generation = 1L
                                        Phase = LearningOperationalExecutionPhase.AssignedUnlaunched
                                        FirstDispatchSha256 = None
                                        Source = source "execution-route-store" "route-binding" assignedAt
                                    }
                        }
                    else
                        memberValue)
        }

    let snapshot =
        LearningOperationalWindow.composeAuthoritativeReadiness
            assignedAt
            (TimeSpan.FromMinutes 10.)
            key
            authority
            cohort
            prospective
        |> Result.defaultWith failwith

    let prepared =
        LearningOperationalWindow.prepare assignedAt snapshot.Request snapshot.Evidence
        |> Result.defaultWith failwith

    snapshot, prepared, key, authority, cohort, prospective, assigned

[<Fact>]
let ``operational admission requires a durable pre-assignment window before launch`` () =
    task {
        let prepared, treatment, subject, observerState, planSha256, assignmentRequest =
            preparedRoot ()

        let request, _, evidence = selectionFor prepared

        let readinessSnapshot, window, windowKey, windowAuthority, windowCohort, prospectiveCensus, assignedCensus =
            operationalWindow
                treatment.AssignedAt
                treatment.OriginalItemId
                treatment.Arm
                request
                planSha256
                (String.replicate 64 "9")

        let producerWindows = ExactOperationalWindowStore(Some window.Binding)
        let producerReadiness =
            ComposedTransitioningReadinessSource(
                treatment.AssignedAt,
                windowKey,
                windowAuthority,
                windowCohort,
                prospectiveCensus,
                assignedCensus
            )

        let assignedSnapshot =
            LearningOperationalWindow.composeAuthoritativeReadiness
                treatment.AssignedAt
                (TimeSpan.FromMinutes 10.)
                windowKey
                windowAuthority
                windowCohort
                assignedCensus
            |> Result.defaultWith failwith

        let runWithReadiness enabled stored (readiness: ILearningOperationalReadinessSource) (clock: TimeProvider) =
            task {
                let journal = Fixture.MemoryJournal()
                let executor = Fixture.MemoryExecutor(fun () -> journal.State)
                let bindings = Fixture.MemoryLearningBindings(fun () -> executor.AttemptCount)
                let windows = ExactOperationalWindowStore(stored)

                let provider =
                    ExactLearningProvider(
                        { evidence with
                            ExpiresAt = clock.GetUtcNow().AddMinutes 1.
                        }
                    )

                let options =
                    { LearningOperationalAdmissionOptions.disabled with
                        Enabled = enabled
                    }

                let! result =
                    LearningMainAdmission.prepareOperationalWithProvider
                        options
                        provider
                        clock
                        (Fixture.MemoryObserverLearning(observerState))
                        journal
                        executor
                        executor
                        bindings
                        readiness
                        windows
                        Fixture.permit.SubjectId
                        "pilot-route"
                        request
                        prepared
                        treatment
                        subject
                        window
                        CancellationToken.None

                return result, executor.AttemptCount, executor.Writes, bindings, provider.Observations
            }

        let run enabled stored readinessValue (clock: TimeProvider) =
            runWithReadiness
                enabled
                stored
                (ExactOperationalReadinessSource(readinessValue) :> ILearningOperationalReadinessSource)
                clock

        let! preparedAssignment =
            LearningMainAdmission.prepareOperationalAssignment
                (Fixture.FixedClock())
                (producerReadiness :> ILearningOperationalReadinessSource)
                (ExactOperationalWindowStore(None))
                {
                    WindowId = readinessSnapshot.Request.WindowId
                    OriginalItemId = readinessSnapshot.Request.OriginalItemId
                }
                assignmentRequest
                CancellationToken.None

        let preparedAgain, preparedWindow =
            preparedAssignment |> Result.defaultWith failwith

        Assert.Equal(prepared.Input, preparedAgain.PreparedTreatment.Input)
        Assert.Equal(window.Binding, preparedWindow.Binding)

        let! accepted, attempts, _, bindings, observations =
            runWithReadiness
                true
                producerWindows.Value
                (producerReadiness :> ILearningOperationalReadinessSource)
                (Fixture.FixedClock())

        let admitted = accepted |> Result.defaultWith failwith
        Assert.True(bindings.BeforeIntent)
        Assert.Equal(1, attempts)
        Assert.Equal(1, observations)
        Assert.Equal(LearningExecutionBinding.operationalSchema, admitted.Binding.Schema)
        Assert.False(admitted.Binding.QualificationOnly)
        Assert.Equal(Some window.Binding, admitted.Binding.OperationalWindow)
        Assert.Equal(3, producerReadiness.Reads)

        let nativeBeginWithoutCounters =
            { assignedCensus with
                Members =
                    assignedCensus.Members
                    |> List.map (fun memberValue ->
                        if memberValue.Role = "root" then
                            { memberValue with
                                Execution =
                                    memberValue.Execution
                                    |> Option.map (fun execution ->
                                        { execution with
                                            Phase = LearningOperationalExecutionPhase.Started
                                            FirstDispatchSha256 = Some(String.replicate 64 "7")
                                        })
                            }
                        else
                            memberValue)
            }

        Assert.Equal(
            Error "learning-operational-readiness-accounting-unknown",
            LearningOperationalWindow.composeAuthoritativeReadiness
                treatment.AssignedAt
                (TimeSpan.FromMinutes 10.)
                windowKey
                windowAuthority
                windowCohort
                nativeBeginWithoutCounters
        )

        Assert.Equal(
            (match treatment.Arm with
             | Current -> "current"
             | Focused -> "focused"),
            admitted.Binding.Arm
        )

        Assert.Equal(
            Ok admitted.Binding,
            LearningExecutionBinding.decode (LearningExecutionBinding.canonicalBytes admitted.Binding)
        )

        for relation in [ Descendant prepared.Input.ItemId; Retry prepared.Input.ItemId ] do
            let inherited, sourceState, identity =
                preparedInherited relation treatment
                |> Result.defaultWith (sprintf "%A" >> failwith)

            let childSubject =
                {
                    ItemId = inherited.Assignment.ItemId
                    OriginalItemId = inherited.Assignment.OriginalItemId
                    Relation = inherited.Assignment.Relation
                    AssignmentSha256 = treatment.AssignmentSha256
                    OwnerPrincipalId = treatment.OwnerPrincipalId
                    BoundAt = Fixture.now
                }

            let durableState =
                { sourceState with
                    LearningTreatments = Map.ofList [ treatment.OriginalItemId, treatment ]
                    LearningTreatmentBindings = Map.ofList [ childSubject.ItemId, childSubject ]
                }

            let childRequest0, _, childEvidence = selectionFor inherited.PreparedTreatment

            let childRequest =
                { childRequest0 with
                    WorkflowRevision = Id.revisionValue inherited.PreparedTreatment.CurrentWorkflowRevision
                }

            let childJournal = Fixture.MemoryJournal()
            let childExecutor = Fixture.MemoryExecutor(fun () -> childJournal.State)

            let childBindings =
                Fixture.MemoryLearningBindings(fun () -> childExecutor.AttemptCount)

            let childProvider = ExactLearningProvider(childEvidence)

            let! childResult =
                LearningMainAdmission.prepareOperationalWithProvider
                    { LearningOperationalAdmissionOptions.disabled with
                        Enabled = true
                    }
                    childProvider
                    (Fixture.FixedClock())
                    (Fixture.MemoryObserverLearning(durableState))
                    childJournal
                    childExecutor
                    childExecutor
                    childBindings
                    (producerReadiness :> ILearningOperationalReadinessSource)
                    producerWindows
                    identity
                    "pilot-route"
                    childRequest
                    inherited.PreparedTreatment
                    treatment
                    childSubject
                    window
                    CancellationToken.None

            let childBinding = (childResult |> Result.defaultWith failwith).Binding
            Assert.Equal(Some window.Binding, childBinding.OperationalWindow)
            Assert.Equal(treatment.AssignmentSha256, childBinding.TreatmentAssignmentSha256)
            Assert.Equal(treatment.ContextManifestSha256, childBinding.TreatmentContextManifestSha256)
            Assert.Equal(inherited.PreparedTreatment.CurrentContextManifestSha256, childBinding.ContextManifestSha256)
            Assert.NotEqual<string>(childBinding.TreatmentContextManifestSha256, childBinding.ContextManifestSha256)
            Assert.Equal(admitted.Binding.MaximumAttempts, childBinding.MaximumAttempts)
            Assert.Equal(admitted.Binding.MaximumRuntimeSeconds, childBinding.MaximumRuntimeSeconds)
            Assert.Equal(1, childExecutor.AttemptCount)

        let! absent, absentAttempts, absentWrites, _, _ =
            run true None (Some assignedSnapshot) (Fixture.FixedClock())

        Assert.Equal(Error "learning-main-admission-operational-window-unavailable", absent)
        Assert.Equal(0, absentAttempts)
        Assert.Equal(0, absentWrites)

        let! unavailable, unavailableAttempts, unavailableWrites, _, _ =
            run true (Some window.Binding) None (Fixture.FixedClock())

        Assert.Equal(Error "learning-main-admission-operational-readiness-unavailable", unavailable)
        Assert.Equal(0, unavailableAttempts)
        Assert.Equal(0, unavailableWrites)

        let changed0 =
            { window.Binding with
                BindingSha256 = ""
                SeedReferenceSha256 = String.replicate 64 "f"
            }

        let changed =
            { changed0 with
                BindingSha256 = LearningOperationalWindow.digest changed0
            }

        let! changedResult, changedAttempts, changedWrites, _, _ =
            run true (Some changed) (Some assignedSnapshot) (Fixture.FixedClock())

        Assert.Equal(Error "learning-main-admission-operational-window-not-durable", changedResult)
        Assert.Equal(0, changedAttempts)
        Assert.Equal(0, changedWrites)

        let! disabled, disabledAttempts, disabledWrites, _, disabledObservations =
            run false (Some window.Binding) (Some assignedSnapshot) (Fixture.FixedClock())

        Assert.Equal(Error "learning-main-admission-operational-disabled", disabled)
        Assert.Equal(0, disabledAttempts)
        Assert.Equal(0, disabledWrites)
        Assert.Equal(0, disabledObservations)

        let forgedReadiness =
            { readinessSnapshot with
                Evidence =
                    { readinessSnapshot.Evidence with
                        AcceptedPlanSha256 = String.replicate 64 "f"
                    }
            }

        let! forgedAssignment =
            LearningMainAdmission.prepareOperationalAssignment
                (Fixture.FixedClock())
                (ExactOperationalReadinessSource(Some forgedReadiness))
                (ExactOperationalWindowStore(None))
                {
                    WindowId = forgedReadiness.Request.WindowId
                    OriginalItemId = forgedReadiness.Request.OriginalItemId
                }
                assignmentRequest
                CancellationToken.None

        Assert.Equal(Error "learning-operational-assignment-readiness-refused", forgedAssignment)

        let! expired, expiredAttempts, expiredWrites, _, _ =
            run
                true
                (Some window.Binding)
                (Some assignedSnapshot)
                (ExactClock(readinessSnapshot.Evidence.ExpiresAt) :> TimeProvider)

        Assert.Equal(Error "learning-operational-window-readiness-stale", expired)
        Assert.Equal(0, expiredAttempts)
        Assert.Equal(0, expiredWrites)

        let changedReadiness =
            { assignedSnapshot with
                Evidence =
                    { assignedSnapshot.Evidence with
                        DispatchCensusSha256 = String.replicate 64 "e"
                    }
            }

        let changingSource =
            SequencedOperationalReadinessSource(assignedSnapshot, changedReadiness)

        let! changedAtFence, changedAtFenceAttempts, _, changedAtFenceBindings, _ =
            runWithReadiness true (Some window.Binding) changingSource (Fixture.FixedClock())

        Assert.Equal(Error "learning-main-admission-operational-readiness-changed", changedAtFence)
        Assert.Equal(2, changingSource.Reads)
        Assert.Equal(0, changedAtFenceAttempts)
        Assert.False(changedAtFenceBindings.BeforeIntent)

        let selectedExecution = assignedSnapshot.SelectedExecution |> Option.defaultWith (fun () -> failwith "execution")

        let alteredExecutions =
            [
                None
                Some { selectedExecution with AssignmentId = Guid.NewGuid() }
                Some { selectedExecution with AttemptId = Guid.NewGuid() }
                Some { selectedExecution with Generation = selectedExecution.Generation + 1L }
                Some
                    { selectedExecution with
                        Phase = LearningOperationalExecutionPhase.Started
                        FirstDispatchSha256 = Some(String.replicate 64 "7")
                    }
            ]

        for altered in alteredExecutions do
            let alteredSnapshot = { assignedSnapshot with SelectedExecution = altered }
            let source = SequencedOperationalReadinessSource(assignedSnapshot, alteredSnapshot)
            let! result, attemptCount, _, alteredBindings, _ =
                runWithReadiness true (Some window.Binding) source (Fixture.FixedClock())

            Assert.Equal(Error "learning-main-admission-operational-readiness-changed", result)
            Assert.Equal(0, attemptCount)
            Assert.False(alteredBindings.BeforeIntent)
    }

[<Fact>]
let ``valid root admission corresponds to root and identical-context child oracle traces`` () =
    task {
        let rootOracle = trace "testRootExecutionIsValid"
        let childOracle = trace "testIdenticalContextChildIsValid"
        Assert.Equal(None, LearningTrace.firstDivergence rootOracle)
        Assert.Equal(None, LearningTrace.firstDivergence childOracle)
        let childTerminal = childOracle.States |> List.last
        Assert.Equal(childTerminal.RootManifest, childTerminal.ChildManifest)
        Assert.NotEqual(childTerminal.RootBindingIdentity, childTerminal.ChildBindingIdentity)

        let prepared, treatment, subject, observerState, _, _ = preparedRoot ()
        let request, query, evidence = selectionFor prepared
        let journal = Fixture.MemoryJournal()
        let executor = Fixture.MemoryExecutor(fun () -> journal.State)
        let bindings = Fixture.MemoryLearningBindings(fun () -> executor.AttemptCount)
        let observer = Fixture.MemoryObserverLearning(observerState)

        let! result =
            LearningMainAdmission.prepare
                (Fixture.FixedClock())
                observer
                journal
                executor
                executor
                bindings
                Fixture.permit.SubjectId
                "pilot-route"
                request
                prepared
                treatment
                subject
                query
                evidence
                CancellationToken.None

        let accepted = result |> Result.defaultWith failwith
        Assert.True(bindings.BeforeIntent)
        Assert.Equal(1, executor.AttemptCount)
        Assert.Equal(prepared.CurrentContextManifestSha256, accepted.Binding.ContextManifestSha256)

        let childPrepared, childObserved, childIdentity =
            preparedInherited (Descendant prepared.Input.ItemId) treatment
            |> Result.defaultWith (sprintf "%A" >> failwith)

        let retryPrepared, retryObserved, retryIdentity =
            preparedInherited (Retry prepared.Input.ItemId) treatment
            |> Result.defaultWith (sprintf "%A" >> failwith)

        for inherited in [ childPrepared; retryPrepared ] do
            Assert.Equal(prepared.Input.OriginalItemId, inherited.Assignment.OriginalItemId)
            Assert.Equal(treatment.ProposalSha256, inherited.Assignment.ProposalSha256)
            Assert.Equal(treatment.ContextManifestSha256, inherited.Assignment.ContextManifestSha256)
            Assert.Equal(treatment.AssignedAt, inherited.Assignment.AssignedAt)

            Assert.NotEqual<string>(
                prepared.CurrentContextManifestSha256,
                inherited.PreparedTreatment.CurrentContextManifestSha256
            )

        let child0 =
            { accepted.Binding with
                BindingSha256 = ""
                SubjectBindingSha256 = accepted.Binding.BindingSha256
                ItemId = "I_learning_child"
                Relation = "descendant"
                ParentItemId = Some accepted.Binding.ItemId
                AssignmentId = Guid.Parse "81000000-0000-4000-8000-000000000001"
                AttemptId = Guid.Parse "81000000-0000-4000-8000-000000000002"
                ProposalSha256 = childPrepared.PreparedTreatment.CurrentProposalSha256
                ContextManifestSha256 = childPrepared.PreparedTreatment.CurrentContextManifestSha256
                RenderedInputSha256 = childPrepared.PreparedTreatment.RenderedInputSha256
                ManifestId = $"context:{childPrepared.PreparedTreatment.CurrentContextManifestSha256}"
            }

        let child =
            { child0 with
                BindingSha256 = LearningExecutionBinding.digest child0
            }

        Assert.Equal(Ok child, LearningExecutionBinding.validate child)
        Assert.Equal(accepted.Binding.TreatmentContextManifestSha256, child.TreatmentContextManifestSha256)
        Assert.NotEqual<string>(accepted.Binding.ContextManifestSha256, child.ContextManifestSha256)
        Assert.NotEqual<string>(accepted.Binding.BindingSha256, child.BindingSha256)

        for inherited, sourceState, identity in
            [
                childPrepared, childObserved, childIdentity
                retryPrepared, retryObserved, retryIdentity
            ] do
            let childSubject =
                {
                    ItemId = inherited.Assignment.ItemId
                    OriginalItemId = inherited.Assignment.OriginalItemId
                    Relation = inherited.Assignment.Relation
                    AssignmentSha256 = treatment.AssignmentSha256
                    OwnerPrincipalId = treatment.OwnerPrincipalId
                    BoundAt = Fixture.now
                }

            let durableState =
                { sourceState with
                    LearningTreatments = Map.ofList [ treatment.OriginalItemId, treatment ]
                    LearningTreatmentBindings = Map.ofList [ childSubject.ItemId, childSubject ]
                }

            let childRequest0, childQuery, childEvidence =
                selectionFor inherited.PreparedTreatment

            let childRequest =
                { childRequest0 with
                    WorkflowRevision = Id.revisionValue inherited.PreparedTreatment.CurrentWorkflowRevision
                }

            let childJournal = Fixture.MemoryJournal()
            let childExecutor = Fixture.MemoryExecutor(fun () -> childJournal.State)

            let childBindings =
                Fixture.MemoryLearningBindings(fun () -> childExecutor.AttemptCount)

            let! childResult =
                LearningMainAdmission.prepare
                    (Fixture.FixedClock())
                    (Fixture.MemoryObserverLearning(durableState))
                    childJournal
                    childExecutor
                    childExecutor
                    childBindings
                    identity
                    "pilot-route"
                    childRequest
                    inherited.PreparedTreatment
                    treatment
                    childSubject
                    childQuery
                    childEvidence
                    CancellationToken.None

            let admitted = childResult |> Result.defaultWith failwith
            Assert.True(childBindings.BeforeIntent)
            Assert.Equal(1, childExecutor.AttemptCount)
            Assert.Equal(treatment.AssignmentSha256, admitted.Binding.TreatmentAssignmentSha256)
            Assert.Equal(treatment.ProposalSha256, admitted.Binding.TreatmentProposalSha256)
            Assert.Equal(treatment.ContextManifestSha256, admitted.Binding.TreatmentContextManifestSha256)

            Assert.Equal(
                inherited.PreparedTreatment.CurrentContextManifestSha256,
                admitted.Binding.ContextManifestSha256
            )

            Assert.NotEqual<string>(
                admitted.Binding.TreatmentContextManifestSha256,
                admitted.Binding.ContextManifestSha256
            )

            let wrongJournal = Fixture.MemoryJournal()
            let wrongExecutor = Fixture.MemoryExecutor(fun () -> wrongJournal.State)

            let wrongBindings =
                Fixture.MemoryLearningBindings(fun () -> wrongExecutor.AttemptCount)

            let! wrongItem =
                LearningMainAdmission.prepare
                    (Fixture.FixedClock())
                    (Fixture.MemoryObserverLearning(durableState))
                    wrongJournal
                    wrongExecutor
                    wrongExecutor
                    wrongBindings
                    Fixture.permit.SubjectId
                    "pilot-route"
                    childRequest
                    inherited.PreparedTreatment
                    treatment
                    childSubject
                    childQuery
                    childEvidence
                    CancellationToken.None

            Assert.Equal(Error "learning-main-admission-work-item-refused", wrongItem)
            Assert.Equal(0, wrongExecutor.AttemptCount)
            Assert.Equal(0, wrongExecutor.Writes)

            let staleJournal = Fixture.MemoryJournal()
            let staleExecutor = Fixture.MemoryExecutor(fun () -> staleJournal.State)

            let staleBindings =
                Fixture.MemoryLearningBindings(fun () -> staleExecutor.AttemptCount)

            let staleRequest =
                { childRequest with
                    WorkflowRevision = childRequest.WorkflowRevision - 1L
                }

            let! stale =
                LearningMainAdmission.prepare
                    (Fixture.FixedClock())
                    (Fixture.MemoryObserverLearning(durableState))
                    staleJournal
                    staleExecutor
                    staleExecutor
                    staleBindings
                    identity
                    "pilot-route"
                    staleRequest
                    inherited.PreparedTreatment
                    treatment
                    childSubject
                    childQuery
                    childEvidence
                    CancellationToken.None

            Assert.Equal(Error "learning-main-admission-authority-stale", stale)
            Assert.Equal(0, staleExecutor.AttemptCount)
    }

[<Fact>]
let ``binding replay is idempotent while changed treatment and self reference are refused`` () =
    task {
        Assert.Equal(Some 3, LearningTrace.firstDivergence (trace "testChangedExecutionDuplicateMutationFails"))
        Assert.Equal(Some 4, LearningTrace.firstDivergence (trace "testExecutionTreatmentMutationFails"))
        Assert.Equal(Some 2, LearningTrace.firstDivergence (trace "testExecutionSelfBindingMutationFails"))

        let prepared, treatment, subject, observerState, _, _ = preparedRoot ()
        let request, query, evidence = selectionFor prepared
        let journal = Fixture.MemoryJournal()
        let executor = Fixture.MemoryExecutor(fun () -> journal.State)
        let bindings = Fixture.MemoryLearningBindings(fun () -> executor.AttemptCount)

        let! accepted =
            LearningMainAdmission.prepare
                (Fixture.FixedClock())
                (Fixture.MemoryObserverLearning(observerState))
                journal
                executor
                executor
                bindings
                Fixture.permit.SubjectId
                "pilot-route"
                request
                prepared
                treatment
                subject
                query
                evidence
                CancellationToken.None

        let root = (accepted |> Result.defaultWith failwith).Binding
        let store = ExactBindingStore()

        let! first =
            (store :> ILearningExecutionBindingStore).BindLearningExecution(root, CancellationToken.None)

        let! replay =
            (store :> ILearningExecutionBindingStore).BindLearningExecution(root, CancellationToken.None)

        Assert.Equal(first, replay)
        Assert.Equal(1, store.Count)

        let changed0 =
            { root with
                BindingSha256 = ""
                TreatmentAssignmentSha256 = String.replicate 64 "f"
            }

        let changed =
            { changed0 with
                BindingSha256 = LearningExecutionBinding.digest changed0
            }

        let! conflict =
            (store :> ILearningExecutionBindingStore).BindLearningExecution(changed, CancellationToken.None)

        Assert.Equal(Error "learning-execution-binding-conflict", conflict)
        Assert.Equal(1, store.Count)

        let selfReference =
            { root with
                SubjectBindingSha256 = root.BindingSha256
            }

        Assert.Equal(Error "learning-execution-binding-digest-refused", LearningExecutionBinding.validate selfReference)
    }

[<Fact>]
let ``durability authority and capability refusals create no launch intent`` () =
    task {
        let unsupportedOracle = trace "testUnsupportedCapabilityNoLaunch"
        Assert.Equal(None, LearningTrace.firstDivergence unsupportedOracle)
        Assert.Equal(0, (unsupportedOracle.States |> List.last).LaunchCount)

        let prepared, treatment, subject, observerState, _, _ = preparedRoot ()
        let request, query, evidence = selectionFor prepared

        let run observer durable evidence =
            task {
                let journal = Fixture.MemoryJournal()
                let executor = Fixture.MemoryExecutor(fun () -> journal.State)
                let bindings = Fixture.MemoryLearningBindings(fun () -> executor.AttemptCount)

                let! result =
                    LearningMainAdmission.prepare
                        (Fixture.FixedClock())
                        observer
                        journal
                        executor
                        executor
                        bindings
                        Fixture.permit.SubjectId
                        "pilot-route"
                        request
                        prepared
                        durable
                        subject
                        query
                        evidence
                        CancellationToken.None

                return result, executor.AttemptCount, executor.Writes
            }

        let! absent, absentAttempts, absentWrites =
            run (Fixture.MemoryObserverLearning(Observer.initial)) treatment evidence

        Assert.Equal(Error "learning-main-admission-treatment-not-durable", absent)
        Assert.Equal(0, absentAttempts)
        Assert.Equal(0, absentWrites)

        let staleTreatment =
            { treatment with
                Generation = Id.generation 2L
            }

        let staleState =
            { observerState with
                LearningTreatments = Map.ofList [ treatment.OriginalItemId, staleTreatment ]
            }

        let! stale, staleAttempts, staleWrites =
            run (Fixture.MemoryObserverLearning(staleState)) staleTreatment evidence

        Assert.Equal(Error "learning-main-admission-treatment-not-durable", stale)
        Assert.Equal(0, staleAttempts)
        Assert.Equal(0, staleWrites)

        for changed in
            [
                { treatment with
                    OriginalItemId = "I_wrong_original"
                }
                { treatment with Arm = Current }
            ] do
            let changedState =
                { observerState with
                    LearningTreatments = Map.ofList [ prepared.Input.OriginalItemId, changed ]
                }

            let! refused, attempts, writes =
                run (Fixture.MemoryObserverLearning(changedState)) changed evidence

            Assert.Equal(Error "learning-main-admission-treatment-not-durable", refused)
            Assert.Equal(0, attempts)
            Assert.Equal(0, writes)

        match preparedInherited (Descendant "I_missing_parent") treatment with
        | Error(CanonicalWorkItemMissing "I_missing_parent") -> ()
        | value -> failwith $"missing parent was not refused: {value}"

        let unsupportedEvidence =
            { evidence with
                Status = LearningCapabilityStatus.Unsupported "model-effort-unavailable"
            }

        let! unsupported, unsupportedAttempts, unsupportedWrites =
            run (Fixture.MemoryObserverLearning(observerState)) treatment unsupportedEvidence

        Assert.Equal(Error "learning-selection-unsupported:model-effort-unavailable", unsupported)
        Assert.Equal(0, unsupportedAttempts)
        Assert.Equal(0, unsupportedWrites)
    }
