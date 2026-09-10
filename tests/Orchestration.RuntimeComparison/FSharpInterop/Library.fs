namespace Orchestration.RuntimeComparison.FSharpInterop

open System.Threading.Tasks
open Akka.Persistence
open Temporalio.Workflows

/// Compile-only evidence for the F# surface of the two candidate SDKs.
type AkkaPersistentWorkflow(persistenceId: string) =
    inherit ReceivePersistentActor()

    do
        base.Recover<string>(fun _ -> ())
        base.Command<string>(fun _ -> ())

    override _.PersistenceId = persistenceId

[<Workflow("FSharpInteropWorkflow")>]
type TemporalWorkflow() =
    [<WorkflowRun>]
    member _.RunAsync(value: string) = Task.FromResult(value)
