module FS.GG.Coordination.AgentFrameworkWorkflowTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI.Workflows
open Xunit
open FS.GG.Coordination.Orchestration.Execution.AgentFramework

let private projection candidateId =
    {
        Lifecycle = "succeeded"
        CandidateId = candidateId
        HeadSha = String.replicate 40 "a"
        TreeSha = String.replicate 40 "b"
        Usage =
            [|
                {
                    Name = "input_tokens"
                    Kind = "known"
                    Value = Nullable 11L
                    UnitName = "tokens"
                    Provenance = "fixture"
                }
            |]
        DeliveryClaimed = false
    }

let private run candidate hold =
    task {
        let workflow = AgentFrameworkWorkflow.build candidate hold
        let! stream =
            InProcessExecution.RunStreamingAsync<AgentFrameworkCandidateWorkItem>(
                workflow,
                candidate,
                null,
                CancellationToken.None
            )
            |> _.AsTask()

        let enumerator = stream.WatchStreamAsync(CancellationToken.None).GetAsyncEnumerator()
        let mutable output: AgentFrameworkWorkflowResult option = None
        let mutable reading = true
        let observed = ResizeArray<string>()

        while reading do
            let! present = enumerator.MoveNextAsync().AsTask()

            if present then
                observed.Add($"{enumerator.Current.GetType().Name}:{enumerator.Current.Data}")
                match enumerator.Current with
                | :? WorkflowOutputEvent as eventValue ->
                    match eventValue.Data with
                    | :? (AgentFrameworkWorkflowResult option) as value -> output <- value
                    | _ -> ()
                | _ -> ()
            else
                reading <- false

        do! enumerator.DisposeAsync().AsTask()
        do! stream.DisposeAsync().AsTask()
        return
            output
            |> Option.defaultWith (fun () ->
                failwithf "agent-framework-workflow-output-missing:%s" (String.concat "|" observed))
    }

[<Fact>]
let ``real workflow runs candidate parallel inspection verification and join without delivery`` () =
    task {
        let candidate =
            {
                WorkItemId = "work-104"
                Projection = projection "30000000-0000-0000-0000-000000000104"
            }

        let! result = run candidate (fun _ _ -> Task.CompletedTask)

        Assert.Equal(candidate.WorkItemId, result.WorkItemId)
        Assert.Equal(candidate.Projection.CandidateId, result.CandidateId)
        Assert.True(result.InspectionAccepted)
        Assert.True(result.VerificationAccepted)
        Assert.False(result.DeliveryClaimed)
    }

[<Fact>]
let ``held branch does not block an independent execution work item`` () =
    task {
        let heldStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let hold (candidate: AgentFrameworkCandidateWorkItem) (cancellationToken: CancellationToken) =
            task {
                if candidate.WorkItemId = "held-work" then
                    heldStarted.TrySetResult() |> ignore
                    do! release.Task.WaitAsync(cancellationToken)
            }
            :> Task

        let held =
            run
                {
                    WorkItemId = "held-work"
                    Projection = projection "30000000-0000-0000-0000-000000000105"
                }
                hold

        do! heldStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.)

        let independent =
            run
                {
                    WorkItemId = "independent-work"
                    Projection = projection "30000000-0000-0000-0000-000000000106"
                }
                hold

        let! independentResult = independent.WaitAsync(TimeSpan.FromSeconds 5.)
        Assert.Equal("independent-work", independentResult.WorkItemId)
        Assert.False(held.IsCompleted)

        release.TrySetResult() |> ignore
        let! heldResult = held.WaitAsync(TimeSpan.FromSeconds 5.)
        Assert.Equal("held-work", heldResult.WorkItemId)
        Assert.False(heldResult.DeliveryClaimed)
    }
