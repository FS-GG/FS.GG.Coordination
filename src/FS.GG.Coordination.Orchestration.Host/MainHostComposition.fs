namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.PostgreSql

type MainHostComposition =
    { ExecutionProvider:IExecutionProvider
      ExecutionActor:Props
      HostedProvider:HostedWriterProviderAdapter
      EffectDriver:MainEffectDriver }

type RunningMainHost = { ExecutionActor:IActorRef; EffectLoop:Task }

[<RequireQualifiedAccess>]
module MainHostComposition =
    /// Wires the production authority graph. Main owns both journals and the actor;
    /// the authenticated transport owns only framed executor I/O. GitHub callbacks
    /// remain a separately supplied delivery identity.
    let create clock workItems (executions:PostgreSqlExecutionStore) workItemId principal resolver transport calls reconcile =
        let provider=RemoteExecutorProvider(executions :> IExecutorCommandStore,resolver,transport) :> IExecutionProvider
        let coordinator=ExecutionSessionCoordinator(provider,executions :> IExecutionSessionJournal,clock)
        let hosted=HostedWriterProviderAdapter.Create calls
        { ExecutionProvider=provider
          ExecutionActor=ExecutionSessionActor.Props coordinator
          HostedProvider=hosted
          EffectDriver=MainEffectDriver(clock,workItems,workItemId,principal,hosted,reconcile) }

    /// Starts the actual supervised execution actor and bounded effect pump. Startup
    /// remains paused by Core state; the pump can only reconcile already-exposed
    /// effects until a separately authorized Resume command is durable.
    let start (system:ActorSystem) (workItems:IJournalStore) (workItemId:WorkItemId) (composition:MainHostComposition) (cancellationToken:CancellationToken) =
        let actor=system.ActorOf(composition.ExecutionActor,"main-execution-session")
        let loop=task {
            while not cancellationToken.IsCancellationRequested do
                let! recovered=HostedWriterJournal.recover workItems workItemId cancellationToken
                match recovered with
                | Error _ -> do! Task.Delay(TimeSpan.FromSeconds 1.,cancellationToken)
                | Ok current ->
                    for intent in current.UnsettledEffects do
                        let! _=composition.EffectDriver.Drive(intent.OperationId,cancellationToken)
                        ()
                    do! Task.Delay(TimeSpan.FromMilliseconds 250.,cancellationToken) }
        {ExecutionActor=actor;EffectLoop=loop}
