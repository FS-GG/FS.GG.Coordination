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
    { PreparationBytes: byte array
      Binding: LearningExecutionBinding }

[<RequireQualifiedAccess>]
module LearningMainAdmission =
    let private sha (value: string) = SHA256.HashData(Encoding.UTF8.GetBytes value) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private arm = function Current -> "current" | Focused -> "focused"
    let private relation = function Original -> "original", None | Descendant parent -> "descendant", Some parent | Retry parent -> "retry", Some parent

    let private treatmentMatches (prepared: PreparedLearningTreatment) (treatment: DurableLearningTreatment) =
        let input = prepared.Input
        treatment.SourceObserverId = input.SourceObserverId
        && treatment.SourceSequence = input.SourceSequence
        && treatment.SourceObservationSha256 = input.SourceObservationSha256
        && treatment.OriginalItemId = input.OriginalItemId
        && treatment.Arm = input.Arm
        && treatment.ProposalSha256 = input.ProposalSha256
        && treatment.ContextManifestSha256 = input.ContextManifestSha256
        && treatment.Planner = input.Planner
        && treatment.Worker = input.Worker
        && treatment.DirectSmallEligible = input.DirectSmallEligible
        && treatment.WorkflowRevision = input.ExpectedWorkflowRevision
        && treatment.Generation = input.ExpectedGeneration
        && treatment.AssignedAt = input.AssignedAt

    let private subjectDigest (treatment: DurableLearningTreatment) (binding: DurableLearningTreatmentBinding) =
        let relationName, parent = relation binding.Relation
        [ binding.ItemId; binding.OriginalItemId; relationName; Option.defaultValue "" parent
          binding.AssignmentSha256; binding.OwnerPrincipalId; binding.BoundAt.ToString("O")
          treatment.AssignmentSha256 ]
        |> String.concat "\n"
        |> sha

    let prepare
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
        (capabilityQuery: LearningSelectionQuery)
        (capabilityEvidence: LearningSelectionEvidence)
        (token: CancellationToken)
        =
        let requested: RequestedSelection = { Model = Some prepared.Input.Worker.Model; Effort = Some prepared.Input.Worker.Effort }
        let requestedMatches = request.RequestedModel = prepared.Input.Worker.Model && request.RequestedEffort = prepared.Input.Worker.Effort
        let durableMatches =
            prepared.ContractVersion = LearningAssignmentAdapter.ContractVersion
            && treatmentMatches prepared treatment
            && subject.ItemId = prepared.Input.ItemId
            && subject.OriginalItemId = prepared.Input.OriginalItemId
            && subject.Relation = prepared.Input.Relation
            && subject.AssignmentSha256 = treatment.AssignmentSha256
            && subject.OwnerPrincipalId = treatment.OwnerPrincipalId
            && principal = treatment.OwnerPrincipalId

        if not durableMatches then Task.FromResult(Error "learning-main-admission-treatment-refused")
        elif not requestedMatches || capabilityQuery.Requested <> requested then Task.FromResult(Error "learning-main-admission-selection-refused")
        else
            match LearningSelectionEvidence.authorize (clock.GetUtcNow()) capabilityQuery capabilityEvidence with
            | Error reason -> Task.FromResult(Error reason)
            | Ok() ->
                let mutable durableBinding = None
                let beforeLaunch (snapshot: PlanningSnapshot) generation (launch: LaunchIntent) cancellationToken =
                    task {
                        if generation <> prepared.Input.ExpectedGeneration || snapshot.WorkflowRevision <> prepared.Input.ExpectedWorkflowRevision then
                            return Error "learning-main-admission-authority-stale"
                        elif launch.InputDigest <> prepared.RenderedInputSha256 || launch.Requested <> requested then
                            return Error "learning-main-admission-input-refused"
                        else
                            let relationName, parent = relation subject.Relation
                            let value0 =
                                { Schema = LearningExecutionBinding.schema
                                  BindingSha256 = ""
                                  TreatmentAssignmentSha256 = treatment.AssignmentSha256
                                  SubjectBindingSha256 = subjectDigest treatment subject
                                  ItemId = subject.ItemId
                                  OriginalItemId = subject.OriginalItemId
                                  Relation = relationName
                                  ParentItemId = parent
                                  AssignmentId = launch.Key.AssignmentId
                                  AttemptId = launch.Key.AttemptId
                                  Generation = launch.Key.Generation
                                  ProposalSha256 = treatment.ProposalSha256
                                  ContextManifestSha256 = treatment.ContextManifestSha256
                                  RenderedInputSha256 = launch.InputDigest
                                  Requested = launch.Requested
                                  Deadline = launch.Limits.Deadline
                                  MaximumRuntimeSeconds = int64 launch.Limits.MaximumRuntime.TotalSeconds
                                  MaximumAttempts = launch.Limits.MaximumAttempts
                                  SnapshotId = $"{request.ProjectId:D}:{request.WorkflowRevision}"
                                  SnapshotDigest = request.CanonicalSha256
                                  SnapshotCapturedAt = request.SelectedAt
                                  ManifestId = $"context:{treatment.ContextManifestSha256}"
                                  ManifestVersion = prepared.ManifestVersion
                                  ExperimentContractId = LearningContext.ContractId
                                  PolicyRepository = "FS-GG/.github"
                                  PolicyRevision = "2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4"
                                  PolicyPath = "policy/learn-01-current-focused-v1.json"
                                  PolicySha256 = "91713679fd486459188f2144e75cc69b77720c7841b6e75cd5d4d35620ed4179"
                                  PolicyStatus = "source-contract-not-enrolled"
                                  WorkClassId = LearningContext.WorkClassId
                                  RubricVersion = "1"
                                  RecipeId = prepared.RecipeId
                                  Arm = arm treatment.Arm
                                  QualificationOnly = true }
                            let value = { value0 with BindingSha256 = LearningExecutionBinding.digest value0 }
                            let! bound = learningBindings.BindLearningExecution(value, cancellationToken)
                            match bound with Error reason -> return Error reason | Ok binding -> durableBinding <- Some binding; return Ok()
                    }

                task {
                    let! result =
                        MainAdmissionPreparer.prepareWithPreIntent clock workItems executions executionJournal workItemId principal request prepared.RenderedInput beforeLaunch token
                    return
                        match result, durableBinding with
                        | Ok bytes, Some binding -> Ok { PreparationBytes = bytes; Binding = binding }
                        | Error reason, _ -> Error reason
                        | Ok _, None -> Error "learning-main-admission-binding-missing"
                }
