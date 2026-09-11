namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Security.Cryptography
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
type RunningProductionMainHost =
    { ExecutionActor:IActorRef; EffectLoop:Task; Workflow:MainRouteWorkflow
      Callbacks:MainProductionCallbacks; Relay:HostExecutorRelay }

[<RequireQualifiedAccess>]
module MainHostComposition =
    let private pump (workItems:IJournalStore) (workItemId:WorkItemId) (driver:MainEffectDriver) (cancellationToken:CancellationToken) = task {
        while not cancellationToken.IsCancellationRequested do
            let! recovered=HostedWriterJournal.recover workItems workItemId cancellationToken
            match recovered with
            | Error _ -> do! Task.Delay(TimeSpan.FromSeconds 1.,cancellationToken)
            | Ok current ->
                for intent in current.UnsettledEffects do
                    let! _=driver.Drive(intent.OperationId,cancellationToken)
                    ()
                do! Task.Delay(TimeSpan.FromMilliseconds 250.,cancellationToken) }
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
        let loop=pump workItems workItemId composition.EffectDriver cancellationToken
        {ExecutionActor=actor;EffectLoop=loop}

    /// Actual production graph used by Program after startup pause. Construction
    /// never calls Prepare: only the authenticated admission route may introduce
    /// a fresh route readback and resume the selected attempt.
    let startProduction (system:ActorSystem) (clock:TimeProvider) (workItems:IJournalStore) (candidates:ICandidateStore) (executions:PostgreSqlExecutionStore)
                        workItemId principal (resolver:IExecutorBindingResolver) (github:GitHubRouteClient)
                        (relay:HostExecutorRelay) (preparation:MainRoutePreparation) (cancellationToken:CancellationToken) =
        let remote=RemoteExecutorProvider(executions :> IExecutorCommandStore,resolver,relay :> IAuthenticatedExecutorTransport)
        let coordinator=ExecutionSessionCoordinator(remote :> IExecutionProvider,executions :> IExecutionSessionJournal,clock)
        let actor=system.ActorOf(ExecutionSessionActor.Props coordinator,"main-execution-session")
        let callbacks=MainProductionCallbacks(clock,actor,remote,candidates,github,preparation)
        let workflow=MainRouteWorkflow(clock,workItems,candidates,executions :> IExecutorCommandStore,executions :> IExecutionSessionJournal,workItemId,principal)
        let advance (route:HostedRoutePlan) intent _ token=workflow.Advance(preparation,intent,callbacks.TryCandidateReceipt route.CandidateId,token)
        let reconcile route intent token=callbacks.Reconcile(route,intent,token)
        let hosted=HostedWriterProviderAdapter.Create callbacks.Calls
        let driver=MainEffectDriver(clock,workItems,workItemId,principal,hosted,reconcile,advance)
        let loop=task {
            while not cancellationToken.IsCancellationRequested do
                let! recovered=HostedWriterJournal.recover workItems workItemId cancellationToken
                match recovered with
                | Error _->do! Task.Delay(TimeSpan.FromSeconds 1.,cancellationToken)
                | Ok current->
                    for intent in current.UnsettledEffects do
                        let! _=driver.Drive(intent.OperationId,cancellationToken)
                        ()
                    let! _=workflow.RecoverContinuation(preparation,cancellationToken)
                    do! Task.Delay(TimeSpan.FromMilliseconds 250.,cancellationToken) }
        {ExecutionActor=actor;EffectLoop=loop;Workflow=workflow;Callbacks=callbacks;Relay=relay}

/// The production admission boundary shared by Program and executable tests.
/// It binds one immutable preparation to one running actor graph. Exact retries
/// recover the original workflow; conflicting bytes cannot replace authority.
[<Sealed>]
type MainProductionAdmission
    (system:ActorSystem,clock:TimeProvider,workItems:IJournalStore,candidates:ICandidateStore,
     executions:PostgreSqlExecutionStore,workItemId:WorkItemId,principal:string,
     github:GitHubRouteClient,relay:HostExecutorRelay,cancellationToken:CancellationToken) =
    let gate=obj()
    let mutable running:RunningProductionMainHost option=None
    let mutable boundDigest:string option=None
    member _.Running = lock gate (fun ()->running)
    interface IMainRouteAdmissionHandler with
        member _.Admit(bytes,token)=task {
            match MainRouteAdmission.decode workItemId principal bytes with
            | Error reason->return Error reason
            | Ok preparation->
                let admissionDigest=SHA256.HashData bytes|>Convert.ToHexString|>fun value->value.ToLowerInvariant()
                let selected =
                    lock gate (fun ()->
                        match running,boundDigest with
                        | Some value,Some existing when existing=admissionDigest->Ok value
                        | Some _,_->Error "main-route-admission-binding-conflict"
                        | None,_->
                            let resolver=
                                PostgreSqlExecutorBindingResolver(
                                    executions :> IExecutorCommandStore,
                                    executions :> IExecutionSessionJournal,
                                    preparation.LaunchIntent.Key.AssignmentId,
                                    preparation.LaunchIntent.Key.AttemptId) :> IExecutorBindingResolver
                            let value=
                                MainHostComposition.startProduction system clock workItems candidates executions
                                    workItemId principal resolver github relay preparation cancellationToken
                            running<-Some value
                            boundDigest<-Some admissionDigest
                            Ok value)
                match selected with
                | Error reason->return Error reason
                | Ok value->return! value.Workflow.Prepare(preparation,token) }
