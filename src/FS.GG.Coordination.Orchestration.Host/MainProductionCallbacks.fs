namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Pattern
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Execution

/// Concrete seven-effect boundary. It accepts authority only as an already
/// selected route+intent pair from HostedWriterProviderAdapter; provider and
/// GitHub identities remain separate capabilities.
[<Sealed>]
type MainProductionCallbacks
    (clock:TimeProvider,actor:IActorRef,remote:RemoteExecutorProvider,candidates:ICandidateStore,
     github:GitHubRouteClient,preparation:MainRoutePreparation) =

    let stored=ConcurrentDictionary<CandidateId,CandidateStorageReceipt>()
    let hosted (route:HostedRoutePlan) (intent:EffectIntent) resource head result revision exists =
        {OperationId=intent.OperationId;RouteId=route.RouteId;AttemptId=route.AttemptId;CandidateId=route.CandidateId
         RepositoryNodeId=route.RepositoryNodeId;ProviderResourceId=resource;CandidateHeadSha=head;ResultSha=result
         ProviderRevision=revision;Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ObservedAt=clock.GetUtcNow();Exists=exists}
    let actorResult (command:ExecutionSessionCommand) (token:CancellationToken)=task {
        try
            let! result=actor.Ask<CoordinationResult>(box command,TimeSpan.FromSeconds 30.,token)
            return Ok result
        with :? OperationCanceledException->return Error "execution-actor-timeout"|error->return Error("execution-actor-refused:"+error.GetType().Name) }
    let observation = function
        | SessionAdvanced state|SessionDuplicate state -> state.Observation|_ -> None
    let actorFailure = function
        | SessionRefused reason|SessionNeedsReconciliation reason->reason
        | _->"execution-observation-missing"
    let processWasObserved (value:SessionObservation) = value.Lifecycle<>SessionLifecycle.OutcomeUnknown
    let dispatch (route:HostedRoutePlan) (intent:EffectIntent) (token:CancellationToken)=task {
        let! result=actorResult (Launch preparation.LaunchIntent) token
        return match result with
               | Ok value->match observation value with Some observed when processWasObserved observed->Ok(hosted route intent (Id.attemptValue route.AttemptId|>string) None None (ProviderSessionReference.value observed.Session) true)|Some _->Error "executor-process-creation-unconfirmed"|None->Error(actorFailure value)
               | Error reason->Error reason }
    let storeCandidate (route:HostedRoutePlan) (intent:EffectIntent) (token:CancellationToken)=task {
        let! observed=actorResult (Observe preparation.LaunchIntent.Key) token
        match observed with
        | Error reason->return Error reason
        | Ok value->
            match observation value|>Option.bind _.Candidate with
            | None->return Error(actorFailure value)
            | Some candidate when candidate.CandidateId<>Id.candidateValue route.CandidateId->return Error "executor-candidate-identity-refused"
            | Some candidate->
                let! frames=remote.ReadCandidate(preparation.LaunchIntent,token)
                match frames with
                | Error reason->return Error reason
                | Ok readback->
                    let! accepted=RemoteCandidatePipeline.store candidates candidate.CandidateId preparation.WorkspaceManifest.BaselineObjectId preparation.LaunchIntent.Limits.Deadline readback token
                    match accepted with
                    | Error reason->return Error reason
                    | Ok(value,receipt)->stored[route.CandidateId]<-receipt;return Ok(hosted route intent (string candidate.CandidateId) (Some value.HeadSha) (Some value.ContentSha256) receipt.StorageReceiptSha256 true) }
    let publish (route:HostedRoutePlan) (intent:EffectIntent) (token:CancellationToken)=task {
        let! candidate=candidates.Read(route.CandidateId,token)
        match candidate with
        | Error reason->return Error reason
        | Ok value->
            let! published=github.PublishBranch(route.BranchRef,None,value.Candidate.HeadSha,value.Bytes,Id.operationValue intent.OperationId,token)
            return published|>Result.map(fun(resource,revision)->hosted route intent resource (Some value.Candidate.HeadSha) (Some value.Candidate.HeadSha) revision true) }
    let createPull (route:HostedRoutePlan) (intent:EffectIntent) (token:CancellationToken)=task {
        let! candidate=candidates.Read(route.CandidateId,token)
        match candidate with
        | Error reason->return Error reason
        | Ok value->
            let! created=github.CreatePullRequest(route.BranchRef,value.Candidate.HeadSha,Id.operationValue intent.OperationId,token)
            return created|>Result.map(fun(resource,head)->hosted route intent resource (Some head) None head true) }
    let merge (route:HostedRoutePlan) (intent:EffectIntent) (token:CancellationToken)=task {
        let! candidate=candidates.Read(route.CandidateId,token)
        match candidate with
        | Error reason->return Error reason
        | Ok value->
            let! merged=github.Merge(route.BranchRef,value.Candidate.HeadSha,Id.operationValue intent.OperationId,token)
            return merged|>Result.map(fun(node,mergeSha,revision)->hosted route intent node (Some value.Candidate.HeadSha) (Some mergeSha) revision true) }
    let native (route:HostedRoutePlan) (intent:EffectIntent) (token:CancellationToken)=task {
        let! candidate=candidates.Read(route.CandidateId,token)
        match candidate with
        | Error reason->return Error reason
        | Ok value->
            let! delivered=github.ReadDelivery(route.BranchRef,value.Candidate.HeadSha,token)
            return delivered|>Result.map(fun(node,mergeSha,revision)->
                {OperationId=intent.OperationId;RouteId=route.RouteId;AttemptId=route.AttemptId;CandidateId=route.CandidateId;RepositoryNodeId=route.RepositoryNodeId
                 PullRequestNodeId=node;CandidateHeadSha=value.Candidate.HeadSha;ObservedPullRequestHeadSha=value.Candidate.HeadSha;MergeCommitSha=mergeSha;ProviderRevision=revision
                 Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ObservedAt=clock.GetUtcNow();Merged=true}) }
    member _.Reconcile(route:HostedRoutePlan,intent:EffectIntent,token:CancellationToken) : Task<Result<HostedWriterProviderReadback,string>>=task {
        match intent.Kind with
        | AcquireExternalClaim ->
            let! result=github.ReadClaim(route.ClaimResourceId,Id.operationValue intent.OperationId,token)
            return result|>Result.map(fun(resource,revision)->HostedEffect(hosted route intent resource None None revision true))
        | DispatchRunner ->
            let! result=actorResult (Reconnect preparation.LaunchIntent.Key) token
            match result with
            | Error reason->return Error reason
            | Ok value->
                return match observation value with
                       | Some observed when processWasObserved observed->Ok(HostedEffect(hosted route intent (Id.attemptValue route.AttemptId|>string) None None (ProviderSessionReference.value observed.Session) true))
                       | Some _->Error "executor-process-creation-unconfirmed"
                       | None->Error(actorFailure value)
        | StoreCandidate ->
            let! found=candidates.Read(route.CandidateId,token)
            match found with
            | Error reason->return Error reason
            | Ok value->
                let! replay=candidates.Put(value,token)
                match replay with
                | Ok receipt|Error(Existing receipt)->stored[route.CandidateId]<-receipt;return Ok(HostedEffect(hosted route intent (string(Id.candidateValue route.CandidateId)) (Some value.Candidate.HeadSha) (Some value.Candidate.ContentSha256) receipt.StorageReceiptSha256 true))
                | other->return Error($"candidate-storage-reconciliation-refused:{other}")
        | PublishCandidateBranch ->
            let! found=candidates.Read(route.CandidateId,token)
            match found with Error reason->return Error reason|Ok value->let! result=github.ReadBranch(route.BranchRef,value.Candidate.HeadSha,token) in return result|>Result.map(fun(resource,revision)->HostedEffect(hosted route intent resource (Some value.Candidate.HeadSha) (Some value.Candidate.HeadSha) revision true))
        | CreatePullRequest ->
            let! found=candidates.Read(route.CandidateId,token)
            match found with Error reason->return Error reason|Ok value->let! result=github.ReadPullRequest(route.BranchRef,value.Candidate.HeadSha,token) in return result|>Result.map(fun(node,head,revision)->HostedEffect(hosted route intent node (Some head) None revision true))
        | MergePullRequest ->
            let! candidate=candidates.Read(route.CandidateId,token)
            match candidate with
            | Error reason->return Error reason
            | Ok value->
                let! result=github.ReadDelivery(route.BranchRef,value.Candidate.HeadSha,token)
                return result|>Result.map(fun(node,mergeSha,revision)->HostedEffect(hosted route intent node (Some value.Candidate.HeadSha) (Some mergeSha) revision true))
        | ReadNativeDelivery ->
            let! result=native route intent token
            return result|>Result.map NativeDelivery
        | _->return Error "main-production-reconciliation-kind-refused" }
    member _.TryCandidateReceipt(candidateId:CandidateId)=match stored.TryGetValue candidateId with true,value->Some value|_->None
    member _.Calls =
        {AcquireExternalClaim=fun (route:HostedRoutePlan) (intent:EffectIntent) token->task {let! value=github.AcquireClaim(route.ClaimResourceId,Id.operationValue intent.OperationId,token) in return value|>Result.map(fun(resource,revision)->hosted route intent resource None None revision true)}
         DispatchRunner=dispatch;StoreCandidate=storeCandidate;PublishCandidateBranch=publish;CreatePullRequest=createPull;MergePullRequest=merge;ReadNativeDelivery=native}
