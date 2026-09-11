module FS.GG.Coordination.Orchestration.Host.Tests.HostedWriterRuntimeTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host

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
