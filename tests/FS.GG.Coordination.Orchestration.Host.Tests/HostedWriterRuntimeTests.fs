module FS.GG.Coordination.Orchestration.Host.Tests.HostedWriterRuntimeTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Net
open System.Net.Http
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

module private Fixture =
    let now = DateTimeOffset.Parse "2026-09-10T19:00:00Z"
    let guid (value: string) = Guid.Parse value
    let workItem = WorkItemIdentity.create "R_writer" 7L "I_writer" 11L
    let attempt = Id.attempt(guid "10000000-0000-0000-0000-000000000001")
    let candidate = Id.candidate(guid "20000000-0000-0000-0000-000000000001")
    let operations = [1..7] |> List.map(fun value -> Id.operation(guid($"30000000-0000-0000-0000-{value:D12}")))
    let route =
        { RouteId = guid "40000000-0000-0000-0000-000000000001"
          WorkItemId = workItem; JobClass = "routine-documentation-delivery"
          AttemptId = attempt; CandidateId = candidate; RepositoryNodeId = "R_repo"
          BranchRef = "refs/heads/fsgg/pilot/docs"; ClaimResourceId = "claim-1"
          ClaimOperationId = operations[0]; ProcessOperationId = operations[1]
          CandidateOperationId = operations[2]; BranchOperationId = operations[3]
          PullRequestOperationId = operations[4]; MergeOperationId = operations[5]
          ReadbackOperationId = operations[6]; Generation = Id.generation 1L
          WorkflowRevision = Id.revision 8L; SelectedAt = now.AddMinutes -1. }
    let intent kind operationId =
        let resource =
            match kind with
            | AcquireExternalClaim -> route.ClaimResourceId
            | DispatchRunner -> Id.attemptValue route.AttemptId |> string
            | StoreCandidate -> Id.candidateValue route.CandidateId |> string
            | PublishCandidateBranch | CreatePullRequest | MergePullRequest | ReadNativeDelivery -> route.BranchRef
            | _ -> "outside"
        { OperationId = operationId; Kind = kind; Generation = route.Generation
          WorkflowRevision = route.WorkflowRevision; ResourceId = resource
          PayloadSha256 = String.replicate 64 "a" }
    let hosted (intentValue: EffectIntent) =
        { OperationId = intentValue.OperationId; RouteId = route.RouteId; AttemptId = route.AttemptId
          CandidateId = route.CandidateId; RepositoryNodeId = route.RepositoryNodeId
          ProviderResourceId = "provider-resource"; CandidateHeadSha = None; ResultSha = None
          ProviderRevision = "provider-revision"; Generation = route.Generation
          WorkflowRevision = route.WorkflowRevision; ObservedAt = now; Exists = true }
    let native (intentValue: EffectIntent) =
        { OperationId = intentValue.OperationId; RouteId = route.RouteId; AttemptId = route.AttemptId
          CandidateId = route.CandidateId; RepositoryNodeId = route.RepositoryNodeId
          PullRequestNodeId = "PR_node"; CandidateHeadSha = String.replicate 40 "a"
          ObservedPullRequestHeadSha = String.replicate 40 "a"; MergeCommitSha = String.replicate 40 "b"
          ProviderRevision = "provider-revision"; Generation = route.Generation
          WorkflowRevision = route.WorkflowRevision; ObservedAt = now; Merged = true }

type private FixedClock(now:DateTimeOffset) =
    inherit TimeProvider()
    override _.GetUtcNow()=now

[<Fact>]
let ``sealed adapter exposes exactly the seven route-bound operations`` () = task {
    let invoked = ResizeArray<EffectKind>()
    let hosted kind (_: HostedRoutePlan) (intentValue: EffectIntent) (_: CancellationToken) = invoked.Add kind; Task.FromResult(Ok(Fixture.hosted intentValue))
    let native (_: HostedRoutePlan) (intentValue: EffectIntent) (_: CancellationToken) = invoked.Add ReadNativeDelivery; Task.FromResult(Ok(Fixture.native intentValue))
    let adapter =
        HostedWriterProviderAdapter.Create
            { AcquireExternalClaim = hosted AcquireExternalClaim
              DispatchRunner = hosted DispatchRunner
              StoreCandidate = hosted StoreCandidate
              PublishCandidateBranch = hosted PublishCandidateBranch
              CreatePullRequest = hosted CreatePullRequest
              MergePullRequest = hosted MergePullRequest
              ReadNativeDelivery = native }
    let kinds =
        [ AcquireExternalClaim, Fixture.route.ClaimOperationId
          DispatchRunner, Fixture.route.ProcessOperationId
          StoreCandidate, Fixture.route.CandidateOperationId
          PublishCandidateBranch, Fixture.route.BranchOperationId
          CreatePullRequest, Fixture.route.PullRequestOperationId
          MergePullRequest, Fixture.route.MergeOperationId
          ReadNativeDelivery, Fixture.route.ReadbackOperationId ]
    for kind, operationId in kinds do
        let! result = adapter.Dispatch(Fixture.route, Fixture.intent kind operationId, CancellationToken.None)
        Assert.True(Result.isOk result)
    Assert.Equal<EffectKind list>(kinds |> List.map fst, List.ofSeq invoked)
    let outside = Fixture.intent ReleaseExternalClaim Fixture.route.ClaimOperationId
    let! refused = adapter.Dispatch(Fixture.route, outside, CancellationToken.None)
    Assert.Equal(Error "effect-is-not-bound-to-selected-hosted-route", refused)
    let wrong = Fixture.intent MergePullRequest Fixture.route.ClaimOperationId
    let! mismatched = adapter.Dispatch(Fixture.route, wrong, CancellationToken.None)
    Assert.Equal(Error "effect-is-not-bound-to-selected-hosted-route", mismatched)
    let staleReadbackCalls =
        { AcquireExternalClaim = fun route intentValue _ -> Task.FromResult(Ok { Fixture.hosted intentValue with RouteId = Guid.NewGuid() })
          DispatchRunner = hosted DispatchRunner; StoreCandidate = hosted StoreCandidate
          PublishCandidateBranch = hosted PublishCandidateBranch; CreatePullRequest = hosted CreatePullRequest
          MergePullRequest = hosted MergePullRequest; ReadNativeDelivery = native }
    let staleAdapter = HostedWriterProviderAdapter.Create staleReadbackCalls
    let! stale = staleAdapter.Dispatch(Fixture.route, Fixture.intent AcquireExternalClaim Fixture.route.ClaimOperationId, CancellationToken.None)
    Assert.Equal(Error "provider-readback-is-not-bound-to-selected-hosted-route", stale) }

[<Fact>]
let ``work item append binds deterministic event ids and effect metadata`` () =
    let operationId = Fixture.route.ClaimOperationId
    let intent = Fixture.intent AcquireExternalClaim operationId
    let commandId = Id.command(Fixture.guid "50000000-0000-0000-0000-000000000001")
    let envelope =
        { CommandId = commandId; ProtocolVersion = Id.protocolVersion 1 0
          ExpectedRevision = initial.Revision; ExpectedGeneration = initial.Generation
          PrincipalId = "pilot"; SessionId = None; IssuedAt = Fixture.now
          ExpiresAt = Fixture.now.AddMinutes 1.; Command = RecordEffectIntent intent }
    let receipt =
        { CommandId = commandId; BodySha256 = canonicalEnvelopeSha256 envelope
          Disposition = Accepted; Revision = Id.revision 2L
          ProtocolVersion = Id.protocolVersion 1 0; Detail = "fixture" }
    let decision =
        { Events = [ EffectIntentRecorded intent; EffectSettled(operationId, Applied "revision"); CommandRecorded receipt ]
          Effects = [ intent ]; Receipt = receipt }
    let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem
    let first = HostedWriterJournal.appendRequest persistenceId Fixture.now 0L envelope decision
    let replay = HostedWriterJournal.appendRequest persistenceId Fixture.now 0L envelope decision
    Assert.Equal(first, replay)
    Assert.Equal<EffectChange list>([ IntentAdded intent; Settled operationId; NoEffect ], first.Events |> List.map _.EffectChange)
    Assert.All(first.Events, fun stored -> Assert.NotEqual(Guid.Empty, stored.EventId))
    Assert.Equal(canonicalEnvelopeSha256 envelope, first.Inbox.BodySha256)

type private FixedStore(recovery: RecoveryResult) =
    interface IJournalStore with
        member _.CheckReadiness _ = Task.FromResult(Ok())
        member _.Recover(_, _) = Task.FromResult(Ok recovery)
        member _.Append(_, _) = Task.FromResult(InvalidAppend "unused")
        member _.SaveSnapshot(_, _) = Task.FromResult(Ok())
        member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Ok())

type private MemoryStore(initialEvents:Event list) =
    let persistenceId=WorkItemIdentity.persistenceId Fixture.workItem
    let mutable events =
        initialEvents |> List.mapi(fun index eventValue ->
            let payload=EventEnvelope.encode eventValue
            { PersistenceId=persistenceId;Sequence=int64(index+1);EventId=Guid.NewGuid();SchemaVersion=1
              SerializerVersion=EventEnvelope.serializerVersion;Payload=payload
              PayloadSha256=System.Security.Cryptography.SHA256.HashData payload|>Convert.ToHexString|>_.ToLowerInvariant()
              EffectChange=(match eventValue with EffectIntentRecorded intent->IntentAdded intent|EffectSettled(id,_)->EffectChange.Settled id|_->NoEffect)
              RecordedAt=Fixture.now })
    let commands=Dictionary<CommandId,string*int64>()
    interface IJournalStore with
        member _.CheckReadiness _=Task.FromResult(Ok())
        member _.Recover(_, _)=
            let state=events|>List.map(fun stored->EventEnvelope.tryDecode stored.Payload|>Result.defaultWith failwith)|>replay
            let unsettled=state.Operations|>Map.toList|>List.choose(fun (_,value)->match value with IntentRecorded i|Dispatching i|NeedsObservation(i,_)->Some i|_->None)
            Task.FromResult(Ok{Events=events;Snapshot=None;UnsettledEffects=unsettled;RequiresExternalReconciliation=not unsettled.IsEmpty})
        member _.Append(request,_)=
            match commands.TryGetValue request.Inbox.CommandId with
            | true,(digest,sequence) when digest=request.Inbox.BodySha256 -> Task.FromResult(Duplicate sequence)
            | true,_ -> Task.FromResult Conflict
            | _ when request.ExpectedSequence<>int64 events.Length -> Task.FromResult(WrongExpectedSequence(int64 events.Length))
            | _ ->
                events<-events@request.Events
                let tail=int64 events.Length
                commands[request.Inbox.CommandId]<-(request.Inbox.BodySha256,tail)
                Task.FromResult(Appended tail)
        member _.SaveSnapshot(_, _)=Task.FromResult(Ok())
        member _.SaveProjectionCheckpoint(_, _)=Task.FromResult(Ok())

let private activeClaimEvents () =
    let snapshot={ProjectId=Id.project(Guid.NewGuid());WorkItemId=Fixture.workItem;WorkflowRevision=Fixture.route.WorkflowRevision;CanonicalSha256=String.replicate 64 "b";BoardMembershipIds=[];CapturedAt=Fixture.now.AddMinutes(-2.)}
    let budget={Schema="fsgg.coordination.subscription-execution-budget/1";AttemptLimit=1;MaximumRuntime=TimeSpan.FromMinutes 30.;ExecutionDeadline=Fixture.now.AddMinutes 25.;Usage=TokensUnknown "not-reported";Cost={InvocationState="not-applicable";InvocationProvenance="subscription";BroaderAttributionState="unknown";BroaderAttributionProvenance="unattributed"}}
    let reservation={ReservationId=Id.reservation(Guid.NewGuid());Generation=Fixture.route.Generation;ExpiresAt=Fixture.now.AddMinutes 20.;RequiredClaimIds=Set.empty}
    let intent=Fixture.intent AcquireExternalClaim Fixture.route.ClaimOperationId
    [SubscriptionWorkAdmitted(snapshot,budget);GenerationAdvanced Fixture.route.Generation;ReservationCreated reservation;HostedRouteSelected Fixture.route;ResumedEvent;EffectIntentRecorded intent],intent

[<Fact>]
let ``duplicate durable command returns original accepted receipt after response loss`` () = task {
    let events,_=activeClaimEvents()
    let store=MemoryStore events :> IJournalStore
    let commandId=Id.command(Guid.Parse "70000000-0000-0000-0000-000000000001")
    let envelope={CommandId=commandId;ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=Id.revision(int64 events.Length);ExpectedGeneration=Fixture.route.Generation;PrincipalId="pilot";SessionId=None;IssuedAt=Fixture.now;ExpiresAt=Fixture.now.AddMinutes 1.;Command=MarkEffectDispatching Fixture.route.ClaimOperationId}
    let! first=HostedWriterJournal.decideAndAppend (FixedClock Fixture.now) store Fixture.workItem envelope CancellationToken.None
    let! replayed=HostedWriterJournal.decideAndAppend (FixedClock Fixture.now) store Fixture.workItem envelope CancellationToken.None
    let firstDecision,_=Result.defaultWith failwith first
    let replayDecision,_=Result.defaultWith failwith replayed
    Assert.Equal(ReceiptDisposition.Accepted,firstDecision.Receipt.Disposition)
    Assert.Equal(firstDecision.Receipt,replayDecision.Receipt) }

[<Fact>]
let ``paused restart reconciles dispatch without repeating provider mutation`` () = task {
    let events,intent=activeClaimEvents()
    let store=MemoryStore(events@[EffectDispatchStarted intent.OperationId;StartupPausedEvent "restart"]) :> IJournalStore
    let mutable writes=0
    let mutable reconciles=0
    let hosted={OperationId=intent.OperationId;RouteId=Fixture.route.RouteId;AttemptId=Fixture.route.AttemptId;CandidateId=Fixture.route.CandidateId;RepositoryNodeId=Fixture.route.RepositoryNodeId;ProviderResourceId=Fixture.route.ClaimResourceId;CandidateHeadSha=None;ResultSha=None;ProviderRevision="claim-revision";Generation=Fixture.route.Generation;WorkflowRevision=Fixture.route.WorkflowRevision;ObservedAt=Fixture.now;Exists=true}
    let call _ _ _=writes<-writes+1;Task.FromResult(Ok hosted)
    let adapter=HostedWriterProviderAdapter.Create{AcquireExternalClaim=call;DispatchRunner=call;StoreCandidate=call;PublishCandidateBranch=call;CreatePullRequest=call;MergePullRequest=call;ReadNativeDelivery=fun _ _ _->Task.FromResult(Error "unused")}
    let reconcile _ _ _=reconciles<-reconciles+1;Task.FromResult(Ok(HostedEffect hosted))
    let driver=MainEffectDriver(FixedClock Fixture.now,store,Fixture.workItem,"pilot",adapter,reconcile)
    let! result=driver.Drive(intent.OperationId,CancellationToken.None)
    match result with EffectCompleted _->()|other->failwithf "%A" other
    Assert.Equal(0,writes)
    Assert.Equal(1,reconciles) }

[<Fact>]
let ``Main verifies complete executor bundle before durable candidate readback`` () = task {
    let bytes=System.Text.Encoding.UTF8.GetBytes "immutable git bundle"
    let digest=RunnerWire.sha256 bytes
    let candidateId=Id.candidateValue Fixture.candidate
    let unsigned={Schema=ExecutorWire.artifactManifestSchema;CommandId=Guid.NewGuid();CandidateId=candidateId;BaselineObjectId=String.replicate 40 "a";HeadObjectId=String.replicate 40 "b";TreeObjectId=String.replicate 40 "c";BundleSha256=digest;BundleSizeBytes=int64 bytes.Length;ManifestSha256="";ChunkBytes=bytes.Length}
    let manifest={unsigned with ManifestSha256=ExecutorWire.artifactManifestDigest unsigned}
    let chunk={Schema=ExecutorWire.artifactContentSchema;CommandId=manifest.CommandId;CandidateId=candidateId;BundleSha256=digest;Offset=0L;Final=true;ContentBase64=Convert.ToBase64String bytes}
    let mutable stored=None
    let store=
        { new ICandidateStore with
            member _.Put(value,_)=
                stored<-Some value
                Task.FromResult(Ok{CandidateId=value.Candidate.CandidateId;ContentSha256=value.Candidate.ContentSha256;ManifestSha256=value.Candidate.ManifestSha256;SizeBytes=value.Candidate.SizeBytes;Location=value.Candidate.Location;StoreId="main";StoreSchemaVersion=1;StorageReceiptSha256=String.replicate 64 "d";VerifiedAt=Fixture.now})
            member _.Read(_, _)=Task.FromResult(Ok stored.Value)
            member _.Quarantine(_,_,_)=Task.FromResult(Ok())
            member _.CleanupUnreferenced(_,_,_)=Task.FromResult 0 }
    let readback={Frames=[ExecutorWire.encodeArtifactManifest manifest;ExecutorWire.encodeArtifactContent chunk]}
    let! accepted=RemoteCandidatePipeline.store store candidateId manifest.BaselineObjectId (Fixture.now.AddDays 1.) readback CancellationToken.None
    Assert.True(Result.isOk accepted)
    let! truncated=RemoteCandidatePipeline.store store candidateId manifest.BaselineObjectId (Fixture.now.AddDays 1.) {Frames=[ExecutorWire.encodeArtifactManifest manifest]} CancellationToken.None
    Assert.Equal(Error "executor-artifact-content-refused",truncated) }

type private RelayHandler(expectedToken:string,responseBytes:byte array) =
    inherit HttpMessageHandler()
    override _.SendAsync(request,_) =
        Assert.Equal("Bearer",request.Headers.Authorization.Scheme)
        Assert.Equal(expectedToken,request.Headers.Authorization.Parameter)
        let response=new HttpResponseMessage(HttpStatusCode.OK)
        response.Content<-new ByteArrayContent(responseBytes)
        Task.FromResult response

[<Fact>]
let ``loopback executor transport authenticates and rejects truncated frames`` () = task {
    let token=String.replicate 32 "t"
    let framed (bytes:byte array)=Array.concat[[|0uy;0uy;0uy;byte bytes.Length|];bytes]
    use goodClient=new HttpClient(new RelayHandler(token,framed [|1uy;2uy|]))
    let transport=HttpExecutorTransport(goodClient,Uri "http://127.0.0.1:19441/v1/executor/exchange",token,1024) :> IAuthenticatedExecutorTransport
    let! accepted=transport.Exchange([[|3uy|]],CancellationToken.None)
    Assert.Equal(Ok{Frames=[[|1uy;2uy|]]},accepted)
    use badClient=new HttpClient(new RelayHandler(token,[|0uy;0uy;0uy;4uy;1uy|]))
    let bad=HttpExecutorTransport(badClient,Uri "http://127.0.0.1:19441/v1/executor/exchange",token,1024) :> IAuthenticatedExecutorTransport
    let! refused=bad.Exchange([[|3uy|]],CancellationToken.None)
    Assert.Equal(Error "executor-http-response-framing-refused",refused) }

[<Fact>]
let ``work item recovery refuses an untyped snapshot instead of inventing state`` () = task {
    let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem
    let snapshot =
        { PersistenceId = persistenceId; Sequence = 4L; SchemaVersion = 1
          Payload = [| 1uy |]; PayloadSha256 = String.replicate 64 "a" }
    let store = FixedStore
                    { Events = []; Snapshot = Some snapshot; UnsettledEffects = []
                      RequiresExternalReconciliation = false }
    let! recovered = HostedWriterJournal.recover store Fixture.workItem CancellationToken.None
    Assert.Equal(Error [ CorruptRecord(persistenceId, 4L) ], recovered) }

[<Fact>]
let ``work item recovery replays typed events and retains unsettled provider work`` () = task {
    let persistenceId = WorkItemIdentity.persistenceId Fixture.workItem
    let intent = Fixture.intent AcquireExternalClaim Fixture.route.ClaimOperationId
    let snapshot =
        { ProjectId=Id.project(Fixture.guid "60000000-0000-0000-0000-000000000001")
          WorkItemId=Fixture.workItem;WorkflowRevision=Fixture.route.WorkflowRevision
          CanonicalSha256=String.replicate 64 "b";BoardMembershipIds=[];CapturedAt=Fixture.now }
    let budget =
        { TokenLimit=100L;RuntimeSecondsLimit=100L;CostMicrosLimit=100L
          Deadline=Fixture.now.AddMinutes 30. }
    let commandId = Id.command(Fixture.guid "60000000-0000-0000-0000-000000000002")
    let envelope =
        { CommandId=commandId;ProtocolVersion=Id.protocolVersion 1 0
          ExpectedRevision=initial.Revision;ExpectedGeneration=initial.Generation
          PrincipalId="pilot";SessionId=None;IssuedAt=Fixture.now;ExpiresAt=Fixture.now.AddMinutes 1.
          Command=Admit(snapshot,budget) }
    let receipt =
        { CommandId=commandId;BodySha256=canonicalEnvelopeSha256 envelope;Disposition=Accepted
          Revision=Id.revision 3L;ProtocolVersion=Id.protocolVersion 1 0;Detail="fixture" }
    let decision =
        { Events=[WorkAdmitted(snapshot,budget);EffectIntentRecorded intent;CommandRecorded receipt]
          Effects=[];Receipt=receipt }
    let stored = HostedWriterJournal.appendRequest persistenceId Fixture.now 0L envelope decision
    let store = FixedStore
                    { Events=stored.Events;Snapshot=None;UnsettledEffects=[intent]
                      RequiresExternalReconciliation=true }
    let! recovered = HostedWriterJournal.recover store Fixture.workItem CancellationToken.None
    match recovered with
    | Error failures -> failwithf "typed work item recovery failed: %A" failures
    | Ok result ->
        Assert.Equal(Some Fixture.workItem,result.State.WorkItemId)
        Assert.Equal(IntentRecorded intent,result.State.Operations[intent.OperationId])
        Assert.Equal<EffectIntent list>([intent],result.UnsettledEffects)
        Assert.True(result.RequiresExternalReconciliation) }
