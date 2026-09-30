namespace FS.GG.Coordination.Orchestration.Execution.AgentFramework

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI.Workflows

type AgentFrameworkCandidateWorkItem =
    {
        WorkItemId: string
        Projection: AgentFrameworkExecutionProjection
    }

type AgentFrameworkBranchResult =
    {
        WorkItemId: string
        Branch: string
        Accepted: bool
        Detail: string
    }

type AgentFrameworkWorkflowResult =
    {
        WorkItemId: string
        CandidateId: string
        InspectionAccepted: bool
        VerificationAccepted: bool
        DeliveryClaimed: bool
    }

type AgentFrameworkWorkflowStart =
    | Fresh
    | ResumeFromFrameworkCheckpoint of checkpointId: string

type internal CandidateSourceExecutor() =
    inherit Executor<AgentFrameworkCandidateWorkItem, AgentFrameworkCandidateWorkItem>("candidate-source")

    override _.HandleAsync(input, _, _) = ValueTask<AgentFrameworkCandidateWorkItem>(input)

type internal CandidateInspectionExecutor(?hold: AgentFrameworkCandidateWorkItem -> CancellationToken -> Task) =
    inherit Executor<AgentFrameworkCandidateWorkItem, AgentFrameworkBranchResult>("candidate-inspection")

    let hold = defaultArg hold (fun _ _ -> Task.CompletedTask)

    override _.HandleAsync(input, _, cancellationToken) =
        ValueTask<AgentFrameworkBranchResult>(
            task {
                do! hold input cancellationToken

                let accepted =
                    not (String.IsNullOrWhiteSpace input.Projection.CandidateId)
                    && not (isNull input.Projection.Usage)

                return
                    {
                        WorkItemId = input.WorkItemId
                        Branch = "candidate-inspection"
                        Accepted = accepted
                        Detail = if accepted then "candidate-and-usage-present" else "candidate-or-usage-missing"
                    }
            }
        )

type internal DeterministicVerificationExecutor() =
    inherit Executor<AgentFrameworkCandidateWorkItem, AgentFrameworkBranchResult>("deterministic-verification")

    override _.HandleAsync(input, _, _) =
        let accepted =
            input.Projection.Lifecycle = "succeeded"
            && not input.Projection.DeliveryClaimed
            && not (String.IsNullOrWhiteSpace input.Projection.HeadSha)
            && not (String.IsNullOrWhiteSpace input.Projection.TreeSha)

        ValueTask<AgentFrameworkBranchResult>(
            {
                WorkItemId = input.WorkItemId
                Branch = "deterministic-verification"
                Accepted = accepted
                Detail = if accepted then "terminal-candidate-verified" else "terminal-candidate-refused"
            }
        )

type internal CandidateJoinExecutor(candidate: AgentFrameworkCandidateWorkItem) =
    inherit Executor<AgentFrameworkBranchResult, AgentFrameworkWorkflowResult option>("candidate-join")

    let gate = obj()
    let results = Dictionary<string, AgentFrameworkBranchResult>(StringComparer.Ordinal)

    override _.HandleAsync(input, _, _) =
        lock gate (fun () ->
            if input.WorkItemId <> candidate.WorkItemId then
                raise (InvalidOperationException "agent-framework-work-item-identity-mismatch")

            if results.ContainsKey input.Branch then
                raise (InvalidOperationException "agent-framework-duplicate-branch-result")

            results.Add(input.Branch, input)

            if results.Count = 2 then
                let inspection = results["candidate-inspection"]
                let verification = results["deterministic-verification"]

                Some
                    {
                        WorkItemId = candidate.WorkItemId
                        CandidateId = candidate.Projection.CandidateId
                        InspectionAccepted = inspection.Accepted
                        VerificationAccepted = verification.Accepted
                        DeliveryClaimed = false
                    }
            else
                None)
        |> ValueTask<AgentFrameworkWorkflowResult option>

[<RequireQualifiedAccess>]
module AgentFrameworkWorkflow =
    let private build candidate hold =
        let source = CandidateSourceExecutor()
        let inspection = CandidateInspectionExecutor(hold)
        let verification = DeterministicVerificationExecutor()
        let join = CandidateJoinExecutor(candidate)

        let sourceBinding = ExecutorBinding.op_Implicit(source :> Executor)
        let inspectionBinding = ExecutorBinding.op_Implicit(inspection :> Executor)
        let verificationBinding = ExecutorBinding.op_Implicit(verification :> Executor)
        let joinBinding = ExecutorBinding.op_Implicit(join :> Executor)

        WorkflowBuilder(sourceBinding)
            .AddFanOutEdge(sourceBinding, [ inspectionBinding; verificationBinding ])
            .AddFanInBarrierEdge([ inspectionBinding; verificationBinding ], joinBinding)
            .WithOutputFrom(joinBinding)
            .Build()

    /// Starts only a fresh in-process trial. The join keeps process-local state,
    /// so an Agent Framework checkpoint cannot establish durable execution or
    /// provider-effect ownership and is refused before an executor runs.
    let start request candidate hold cancellationToken =
        task {
            match request with
            | ResumeFromFrameworkCheckpoint checkpointId when String.IsNullOrWhiteSpace checkpointId ->
                return Error "agent-framework-workflow-checkpoint-identity-invalid"
            | ResumeFromFrameworkCheckpoint _ ->
                return Error "agent-framework-workflow-checkpoint-replay-unsupported"
            | Fresh ->
                let workflow = build candidate hold

                let! stream =
                    InProcessExecution.RunStreamingAsync<AgentFrameworkCandidateWorkItem>(
                        workflow,
                        candidate,
                        null,
                        cancellationToken
                    )
                    |> _.AsTask()

                return Ok stream
        }
