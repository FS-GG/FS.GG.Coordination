namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Observer
open FS.GG.Coordination.Orchestration.PostgreSql

type LearningMainAdmissionResult =
    {
        PreparationBytes: byte array
        Binding: LearningExecutionBinding
    }

type LearningOperationalAdmissionOptions =
    {
        Enabled: bool
        MaximumEvidenceAge: TimeSpan
    }

[<RequireQualifiedAccess>]
module LearningOperationalAdmissionOptions =
    let disabled =
        {
            Enabled = false
            MaximumEvidenceAge = TimeSpan.FromMinutes 5.
        }

[<RequireQualifiedAccess>]
module LearningMainAdmission =
    let private sha (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private arm =
        function
        | Current -> "current"
        | Focused -> "focused"

    let private relation =
        function
        | Original -> "original", None
        | Descendant parent -> "descendant", Some parent
        | Retry parent -> "retry", Some parent

    let authoritativeOperationalReadiness
        (clock: TimeProvider)
        (options: LearningOperationalAdmissionOptions)
        (authority: ILearningOperationalAuthoritySource)
        (cohort: ILearningOperationalCohortSource)
        (census: ILearningOperationalCensusSource)
        =
        if not options.Enabled then
            Error "learning-operational-readiness-composition-disabled"
        elif options.MaximumEvidenceAge <= TimeSpan.Zero then
            Error "learning-operational-readiness-composition-age-refused"
        else
            AuthoritativeLearningOperationalReadinessSource(
                clock,
                options.MaximumEvidenceAge,
                authority,
                cohort,
                census
            )
            :> ILearningOperationalReadinessSource
            |> Ok

    let private treatmentMatches (prepared: PreparedLearningTreatment) (treatment: DurableLearningTreatment) =
        let input = prepared.Input

        treatment.OriginalItemId = input.OriginalItemId
        && treatment.Arm = input.Arm
        && treatment.ProposalSha256 = input.ProposalSha256
        && treatment.ContextManifestSha256 = input.ContextManifestSha256
        && treatment.Planner = input.Planner
        && treatment.Worker = input.Worker
        && treatment.DirectSmallEligible = input.DirectSmallEligible
        && treatment.WorkflowRevision = input.ExpectedWorkflowRevision
        && treatment.Generation = input.ExpectedGeneration
        && treatment.AssignedAt = input.AssignedAt

    let private treatmentInput (treatment: DurableLearningTreatment) =
        {
            SourceObserverId = treatment.SourceObserverId
            SourceSequence = treatment.SourceSequence
            SourceObservationSha256 = treatment.SourceObservationSha256
            ItemId = treatment.OriginalItemId
            OriginalItemId = treatment.OriginalItemId
            Relation = Original
            Arm = treatment.Arm
            ProposalSha256 = treatment.ProposalSha256
            ContextManifestSha256 = treatment.ContextManifestSha256
            Planner = treatment.Planner
            Worker = treatment.Worker
            DirectSmallEligible = treatment.DirectSmallEligible
            ExpectedWorkflowRevision = treatment.WorkflowRevision
            ExpectedGeneration = treatment.Generation
            AssignedAt = treatment.AssignedAt
        }

    let private subjectDigest (treatment: DurableLearningTreatment) (binding: DurableLearningTreatmentBinding) =
        let relationName, parent = relation binding.Relation

        [
            binding.ItemId
            binding.OriginalItemId
            relationName
            Option.defaultValue "" parent
            binding.AssignmentSha256
            binding.OwnerPrincipalId
            binding.BoundAt.ToString("O")
            treatment.AssignmentSha256
        ]
        |> String.concat "\n"
        |> sha

    let private validateOperationalReadiness
        (clock: TimeProvider)
        (source: ILearningOperationalReadinessSource)
        (key: LearningOperationalWindowKey)
        (expected: PreparedLearningOperationalWindow)
        (expectedUnlaunched: ExecutionKey option)
        (token: CancellationToken)
        =
        task {
            let! current = source.ReadLearningOperationalReadiness(key, token)

            return
                match current with
                | Error _ -> Error "learning-main-admission-operational-readiness-unavailable"
                | Ok current when expectedUnlaunched.IsSome && current.Evidence.UnassignedSharedAllocation ->
                    Error "learning-main-admission-assigned-execution-unavailable"
                | Ok current when not current.Evidence.UnassignedSharedAllocation ->
                    let executionMatches =
                        match expectedUnlaunched, current.SelectedExecution with
                        | None, _ -> true
                        | Some expectedExecution, Some actual ->
                            actual.AssignmentId = expectedExecution.AssignmentId
                            && actual.AttemptId = expectedExecution.AttemptId
                            && actual.Generation = expectedExecution.Generation
                            && actual.Phase = LearningOperationalExecutionPhase.AssignedUnlaunched
                            && actual.FirstDispatchSha256.IsNone
                        | Some _, None -> false

                    if
                        current.Evidence.ObservedAt > clock.GetUtcNow()
                        || current.Evidence.ExpiresAt <= clock.GetUtcNow()
                    then
                        Error "learning-operational-window-readiness-stale"
                    elif
                        not executionMatches
                        ||
                        current.Request.WindowId <> expected.Binding.WindowId
                        || current.Request.SeedReferenceSha256 <> expected.Binding.SeedReferenceSha256
                        || current.Request.Repository <> expected.Binding.Repository
                        || current.Request.CalendarAdmissionBlock <> expected.Binding.CalendarAdmissionBlock
                        || current.Request.OriginalItemId <> expected.Binding.OriginalItemId
                        || current.Request.AuthorityId <> expected.Binding.AuthorityId
                        || current.Request.AuthorityRevision <> expected.Binding.AuthorityRevision
                        || current.Request.AuthoritySha256 <> expected.Binding.AuthoritySha256
                        || current.Request.OptedInAt <> expected.Binding.OptedInAt
                        || current.Request.EnrollmentOpensAt <> expected.Binding.EnrollmentOpensAt
                        || current.Request.EnrollmentClosesAt <> expected.Binding.EnrollmentClosesAt
                        || current.Evidence.Schema <> LearningOperationalWindow.readinessSchema
                        || current.Evidence.WindowId <> expected.Binding.WindowId
                        || current.Evidence.Repository <> expected.Binding.Repository
                        || current.Evidence.WorkClassId <> expected.Binding.WorkClassId
                        || current.Evidence.OriginalItemId <> expected.Binding.OriginalItemId
                        || current.Evidence.AcceptedPlanSha256 <> expected.Binding.AcceptedPlanSha256
                        || current.Evidence.CanonicalWorkItemSha256 <> expected.Binding.CanonicalWorkItemSha256
                        || current.Evidence.CoverageRosterSha256 <> expected.Binding.CoverageRosterSha256
                        || not current.Evidence.CompleteNativeUsage
                    then
                        Error "learning-main-admission-operational-readiness-changed"
                    else
                        LearningOperationalWindow.validateCurrent (clock.GetUtcNow()) expected.Binding
                        |> Result.map (fun _ -> current)
                | Ok current ->
                    match
                        LearningOperationalWindow.prepare expected.Binding.AssignedAt current.Request current.Evidence
                    with
                    | Error reason -> Error reason
                    | Ok preparedCurrent when preparedCurrent.Binding <> expected.Binding ->
                        Error "learning-main-admission-operational-readiness-changed"
                    | Ok preparedCurrent ->
                        LearningOperationalWindow.validateCurrent (clock.GetUtcNow()) preparedCurrent.Binding
                        |> Result.map (fun _ -> current)
        }

    let prepareOperationalAssignment
        (clock: TimeProvider)
        (readiness: ILearningOperationalReadinessSource)
        (operationalWindows: ILearningOperationalWindowStore)
        (windowKey: LearningOperationalWindowKey)
        (request: LearningAssignmentPreparationRequest)
        (token: CancellationToken)
        =
        task {
            if request.InheritedDurableTreatment.IsSome then
                return Error "learning-operational-assignment-original-required"
            else
                let! resolved = readiness.ReadLearningOperationalReadiness(windowKey, token)

                match resolved with
                | Error _ -> return Error "learning-operational-assignment-readiness-unavailable"
                | Ok snapshot ->
                    let planSource = request.AcceptedProposal.ContextManifest |> Option.map _.PlanSource

                    let observationRevision =
                        request.SourceState.Observation
                        |> Option.map (fun observation -> string (Id.revisionValue observation.WorkflowRevision))

                    if
                        snapshot.Request.WindowId <> windowKey.WindowId
                        || snapshot.Request.OriginalItemId <> windowKey.OriginalItemId
                        || snapshot.Evidence.WindowId <> windowKey.WindowId
                        || snapshot.Evidence.OriginalItemId <> windowKey.OriginalItemId
                        || planSource |> Option.map _.Sha256 <> Some snapshot.Evidence.AcceptedPlanSha256
                        || observationRevision <> Some snapshot.Request.AuthorityRevision
                    then
                        return Error "learning-operational-assignment-readiness-refused"
                    else
                        match
                            LearningOperationalWindow.prepare request.AssignedAt snapshot.Request snapshot.Evidence
                        with
                        | Error reason -> return Error reason
                        | Ok operationalWindow ->
                            let expected = operationalWindow.Binding

                            match LearningOperationalWindow.validateCurrent (clock.GetUtcNow()) expected with
                            | Error reason -> return Error reason
                            | Ok _ ->
                                let! persisted = operationalWindows.BindLearningOperationalWindow(expected, token)

                                match persisted with
                                | Error reason -> return Error reason
                                | Ok durable when durable <> expected ->
                                    return Error "learning-operational-assignment-window-conflict"
                                | Ok _ ->
                                    match LearningAssignmentAdapter.prepare request with
                                    | Error reason ->
                                        return
                                            Error(sprintf "learning-operational-assignment-prepare-refused:%A" reason)
                                    | Ok prepared ->
                                        let arm =
                                            match prepared.Assignment.Arm with
                                            | Current -> "current"
                                            | Focused -> "focused"

                                        if
                                            prepared.Assignment.ItemId <> expected.OriginalItemId
                                            || prepared.Assignment.OriginalItemId <> expected.OriginalItemId
                                            || arm <> expected.Arm
                                            || prepared.Assignment.AssignedAt <> expected.AssignedAt
                                        then
                                            return Error "learning-operational-assignment-window-refused"
                                        else
                                            return Ok(prepared, operationalWindow)
        }

    let private prepareVerified
        (clock: TimeProvider)
        (workItems: IJournalStore)
        (executions: IExecutorCommandStore)
        (executionJournal: IExecutionSessionJournal)
        (learningBindings: ILearningExecutionBindingStore)
        workItemId
        principal
        (request: MainAdmissionPreparationRequest)
        (prepared: PreparedLearningTreatment)
        (treatment: DurableLearningTreatment)
        (subject: DurableLearningTreatmentBinding)
        (operationalWindow: PreparedLearningOperationalWindow option)
        (operationalReadiness:
            (ILearningOperationalReadinessSource
                * LearningOperationalWindowKey
                * LearningOperationalReadinessSnapshot) option)
        (capabilityQuery: LearningSelectionQuery)
        (capabilityEvidence: LearningSelectionEvidence)
        (token: CancellationToken)
        =
        let requested: RequestedSelection =
            {
                Model = Some prepared.Input.Worker.Model
                Effort = Some prepared.Input.Worker.Effort
            }

        let requestedMatches =
            request.RequestedModel = prepared.Input.Worker.Model
            && request.RequestedEffort = prepared.Input.Worker.Effort

        let durableMatches =
            prepared.ContractVersion = LearningAssignmentAdapter.ContractVersion
            && treatmentMatches prepared treatment
            && subject.ItemId = prepared.Input.ItemId
            && subject.OriginalItemId = prepared.Input.OriginalItemId
            && subject.Relation = prepared.Input.Relation
            && subject.AssignmentSha256 = treatment.AssignmentSha256
            && subject.OwnerPrincipalId = treatment.OwnerPrincipalId
            && principal = treatment.OwnerPrincipalId

        let operationalMatches =
            match operationalWindow with
            | None -> true
            | Some preparedWindow ->
                let window = preparedWindow.Binding

                window.OriginalItemId = prepared.Input.OriginalItemId
                && window.Arm = arm treatment.Arm
                && window.AssignedAt = treatment.AssignedAt
                && window.AuthorityId = principal
                && window.AuthorityRevision = string (Id.revisionValue treatment.WorkflowRevision)

        if not durableMatches then
            Task.FromResult(Error "learning-main-admission-treatment-refused")
        elif not operationalMatches then
            Task.FromResult(Error "learning-main-admission-operational-window-refused")
        elif not requestedMatches || capabilityQuery.Requested <> requested then
            Task.FromResult(Error "learning-main-admission-selection-refused")
        else
            match LearningSelectionEvidence.authorize (clock.GetUtcNow()) capabilityQuery capabilityEvidence with
            | Error reason -> Task.FromResult(Error reason)
            | Ok() ->
                let mutable durableBinding = None

                let beforeLaunch (snapshot: PlanningSnapshot) generation (launch: LaunchIntent) cancellationToken =
                    task {
                        let! readinessResult =
                            match operationalWindow, operationalReadiness with
                            | None, None -> Task.FromResult(Ok())
                            | Some expected, Some(source, key, baseline) ->
                                task {
                                    let! validated =
                                        validateOperationalReadiness
                                            clock
                                            source
                                            key
                                            expected
                                            (if subject.Relation = Original then Some launch.Key else None)
                                            cancellationToken

                                    return
                                        match validated with
                                        | Ok current when current = baseline -> Ok()
                                        | Ok _ -> Error "learning-main-admission-operational-readiness-changed"
                                        | Error reason -> Error reason
                                }
                            | _ -> Task.FromResult(Error "learning-main-admission-operational-readiness-refused")

                        if Result.isError readinessResult then
                            return readinessResult
                        elif
                            generation <> prepared.CurrentGeneration
                            || snapshot.WorkflowRevision <> prepared.CurrentWorkflowRevision
                        then
                            return Error "learning-main-admission-authority-stale"
                        elif
                            launch.InputDigest <> prepared.RenderedInputSha256
                            || launch.Requested <> requested
                        then
                            return Error "learning-main-admission-input-refused"
                        else
                            let relationName, parent = relation subject.Relation
                            let operational = operationalWindow |> Option.map _.Binding

                            let (schema,
                                 policyRepository,
                                 policyRevision,
                                 policyPath,
                                 policySha256,
                                 policyStatus,
                                 workClassId,
                                 qualificationOnly) =
                                match operational with
                                | Some window ->
                                    LearningExecutionBinding.operationalSchema,
                                    window.PolicyRepository,
                                    window.PolicyRevision,
                                    window.PolicyPath,
                                    window.PolicySha256,
                                    window.PolicyStatus,
                                    window.WorkClassId,
                                    false
                                | None ->
                                    LearningExecutionBinding.schema,
                                    "FS-GG/.github",
                                    "2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4",
                                    "policy/learn-01-current-focused-v1.json",
                                    "91713679fd486459188f2144e75cc69b77720c7841b6e75cd5d4d35620ed4179",
                                    "source-contract-not-enrolled",
                                    LearningContext.WorkClassId,
                                    true

                            let value0 =
                                {
                                    Schema = schema
                                    BindingSha256 = ""
                                    TreatmentAssignmentSha256 = treatment.AssignmentSha256
                                    TreatmentOwnerPrincipalId = treatment.OwnerPrincipalId
                                    TreatmentWorkflowRevision = string (Id.revisionValue treatment.WorkflowRevision)
                                    TreatmentGeneration = Id.generationValue treatment.Generation
                                    TreatmentAssignedAt = treatment.AssignedAt
                                    TreatmentProposalSha256 = treatment.ProposalSha256
                                    TreatmentContextManifestSha256 = treatment.ContextManifestSha256
                                    TreatmentArm = arm treatment.Arm
                                    SubjectBindingSha256 = subjectDigest treatment subject
                                    ItemId = subject.ItemId
                                    OriginalItemId = subject.OriginalItemId
                                    Relation = relationName
                                    ParentItemId = parent
                                    AssignmentId = launch.Key.AssignmentId
                                    AttemptId = launch.Key.AttemptId
                                    Generation = launch.Key.Generation
                                    ProposalSha256 = prepared.CurrentProposalSha256
                                    ContextManifestSha256 = prepared.CurrentContextManifestSha256
                                    RenderedInputSha256 = launch.InputDigest
                                    Requested = launch.Requested
                                    Deadline = launch.Limits.Deadline
                                    MaximumRuntimeSeconds = int64 launch.Limits.MaximumRuntime.TotalSeconds
                                    MaximumAttempts = launch.Limits.MaximumAttempts
                                    SnapshotId = $"{request.ProjectId:D}:{request.WorkflowRevision}"
                                    SnapshotDigest = request.CanonicalSha256
                                    SnapshotCapturedAt = request.SelectedAt
                                    ManifestId = $"context:{prepared.CurrentContextManifestSha256}"
                                    ManifestVersion = prepared.ManifestVersion
                                    ExperimentContractId = LearningContext.ContractId
                                    PolicyRepository = policyRepository
                                    PolicyRevision = policyRevision
                                    PolicyPath = policyPath
                                    PolicySha256 = policySha256
                                    PolicyStatus = policyStatus
                                    WorkClassId = workClassId
                                    RubricVersion = "1"
                                    RecipeId = prepared.RecipeId
                                    RecipeDigest = prepared.RecipeDigest
                                    Arm = arm treatment.Arm
                                    QualificationOnly = qualificationOnly
                                    OperationalWindow = operational
                                }

                            let value =
                                { value0 with
                                    BindingSha256 = LearningExecutionBinding.digest value0
                                }

                            let! bound = learningBindings.BindLearningExecution(value, cancellationToken)

                            match bound with
                            | Error reason -> return Error reason
                            | Ok binding ->
                                durableBinding <- Some binding
                                return Ok()
                    }

                task {
                    let! result =
                        MainAdmissionPreparer.prepareWithPreIntent
                            clock
                            workItems
                            executions
                            executionJournal
                            workItemId
                            principal
                            request
                            prepared.RenderedInput
                            beforeLaunch
                            token

                    return
                        match result, durableBinding with
                        | Ok bytes, Some binding ->
                            Ok
                                {
                                    PreparationBytes = bytes
                                    Binding = binding
                                }
                        | Error reason, _ -> Error reason
                        | Ok _, None -> Error "learning-main-admission-binding-missing"
                }

    let prepare
        (clock: TimeProvider)
        (observerJournal: IObserverJournalStore)
        (workItems: IJournalStore)
        (executions: IExecutorCommandStore)
        (executionJournal: IExecutionSessionJournal)
        (learningBindings: ILearningExecutionBindingStore)
        workItemId
        principal
        (request: MainAdmissionPreparationRequest)
        (prepared: PreparedLearningTreatment)
        (treatment: DurableLearningTreatment)
        (subject: DurableLearningTreatmentBinding)
        (capabilityQuery: LearningSelectionQuery)
        (capabilityEvidence: LearningSelectionEvidence)
        (token: CancellationToken)
        =
        task {
            if WorkItemIdentity.persistenceId workItemId <> prepared.Input.ItemId then
                return Error "learning-main-admission-work-item-refused"
            else
                let observerId =
                    ObserverJournal.learningTreatmentObserverId prepared.Input.OriginalItemId

                let! recovered = observerJournal.RecoverObserver(observerId, token)

                match recovered with
                | Error _ -> return Error "learning-main-admission-treatment-unavailable"
                | Ok recovery ->
                    let storedTreatment =
                        recovery.State.LearningTreatments |> Map.tryFind prepared.Input.OriginalItemId

                    let storedSubject =
                        recovery.State.LearningTreatmentBindings |> Map.tryFind prepared.Input.ItemId

                    let expectedAssignment =
                        Observer.learningTreatmentSha256 principal (treatmentInput treatment)

                    if
                        storedTreatment <> Some treatment
                        || storedSubject <> Some subject
                        || treatment.AssignmentSha256 <> expectedAssignment
                    then
                        return Error "learning-main-admission-treatment-not-durable"
                    else
                        return!
                            prepareVerified
                                clock
                                workItems
                                executions
                                executionJournal
                                learningBindings
                                workItemId
                                principal
                                request
                                prepared
                                treatment
                                subject
                                None
                                None
                                capabilityQuery
                                capabilityEvidence
                                token
        }

    let prepareOperational
        (clock: TimeProvider)
        (observerJournal: IObserverJournalStore)
        (workItems: IJournalStore)
        (executions: IExecutorCommandStore)
        (executionJournal: IExecutionSessionJournal)
        (learningBindings: ILearningExecutionBindingStore)
        (operationalReadiness: ILearningOperationalReadinessSource)
        (operationalWindows: ILearningOperationalWindowStore)
        workItemId
        principal
        (request: MainAdmissionPreparationRequest)
        (prepared: PreparedLearningTreatment)
        (treatment: DurableLearningTreatment)
        (subject: DurableLearningTreatmentBinding)
        (operationalWindow: PreparedLearningOperationalWindow)
        (capabilityQuery: LearningSelectionQuery)
        (capabilityEvidence: LearningSelectionEvidence)
        (token: CancellationToken)
        =
        task {
            if WorkItemIdentity.persistenceId workItemId <> prepared.Input.ItemId then
                return Error "learning-main-admission-work-item-refused"
            else
                let expectedWindow = operationalWindow.Binding

                let! durableWindow =
                    operationalWindows.ReadLearningOperationalWindow(
                        expectedWindow.WindowId,
                        prepared.Input.OriginalItemId,
                        token
                    )

                match durableWindow with
                | Error _ -> return Error "learning-main-admission-operational-window-unavailable"
                | Ok durableWindow when durableWindow <> expectedWindow ->
                    return Error "learning-main-admission-operational-window-not-durable"
                | Ok _ ->
                    let key =
                        {
                            WindowId = expectedWindow.WindowId
                            OriginalItemId = expectedWindow.OriginalItemId
                        }

                    let! readinessCurrent =
                        validateOperationalReadiness
                            clock
                            operationalReadiness
                            key
                            operationalWindow
                            (if subject.Relation = Original then
                                 Some
                                     {
                                         AssignmentId = request.ProcessOperationId
                                         AttemptId = request.AttemptId
                                         Generation = Id.generationValue prepared.CurrentGeneration
                                     }
                             else
                                 None)
                            token

                    match readinessCurrent with
                    | Error reason -> return Error reason
                    | Ok readinessBaseline ->
                        let observerId =
                            ObserverJournal.learningTreatmentObserverId prepared.Input.OriginalItemId

                        let! recovered = observerJournal.RecoverObserver(observerId, token)

                        match recovered with
                        | Error _ -> return Error "learning-main-admission-treatment-unavailable"
                        | Ok recovery ->
                            let storedTreatment =
                                recovery.State.LearningTreatments |> Map.tryFind prepared.Input.OriginalItemId

                            let storedSubject =
                                recovery.State.LearningTreatmentBindings |> Map.tryFind prepared.Input.ItemId

                            let expectedAssignment =
                                Observer.learningTreatmentSha256 principal (treatmentInput treatment)

                            if
                                storedTreatment <> Some treatment
                                || storedSubject <> Some subject
                                || treatment.AssignmentSha256 <> expectedAssignment
                            then
                                return Error "learning-main-admission-treatment-not-durable"
                            else
                                return!
                                    prepareVerified
                                        clock
                                        workItems
                                        executions
                                        executionJournal
                                        learningBindings
                                        workItemId
                                        principal
                                        request
                                        prepared
                                        treatment
                                        subject
                                        (Some operationalWindow)
                                        (Some(operationalReadiness, key, readinessBaseline))
                                        capabilityQuery
                                        capabilityEvidence
                                        token
        }

    let prepareOperationalWithProvider
        (options: LearningOperationalAdmissionOptions)
        (provider: ILearningExecutionProvider)
        (clock: TimeProvider)
        (observerJournal: IObserverJournalStore)
        (workItems: IJournalStore)
        (executions: IExecutorCommandStore)
        (executionJournal: IExecutionSessionJournal)
        (learningBindings: ILearningExecutionBindingStore)
        (operationalReadiness: ILearningOperationalReadinessSource)
        (operationalWindows: ILearningOperationalWindowStore)
        workItemId
        principal
        (request: MainAdmissionPreparationRequest)
        (prepared: PreparedLearningTreatment)
        (treatment: DurableLearningTreatment)
        (subject: DurableLearningTreatmentBinding)
        (operationalWindow: PreparedLearningOperationalWindow)
        (token: CancellationToken)
        =
        task {
            if not options.Enabled then
                return Error "learning-main-admission-operational-disabled"
            elif options.MaximumEvidenceAge <= TimeSpan.Zero then
                return Error "learning-main-admission-evidence-age-refused"
            else
                let requested: RequestedSelection =
                    {
                        Model = Some prepared.Input.Worker.Model
                        Effort = Some prepared.Input.Worker.Effort
                    }

                let! evidence = provider.ObserveLearningSelection(requested, token)

                let query =
                    {
                        Provider = evidence.Provider
                        Executable = evidence.Executable
                        Requested = requested
                        MaximumAge = options.MaximumEvidenceAge
                    }

                return!
                    prepareOperational
                        clock
                        observerJournal
                        workItems
                        executions
                        executionJournal
                        learningBindings
                        operationalReadiness
                        operationalWindows
                        workItemId
                        principal
                        request
                        prepared
                        treatment
                        subject
                        operationalWindow
                        query
                        evidence
                        token
        }
