module FS.GG.Coordination.Orchestration.Host.Tests.RunnerWireRuntimeTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

module private WireFixture =
    let now=DateTimeOffset.Parse("2026-09-11T05:00:00Z")
    let work=WorkItemIdentity.create "R_runner" 1269292704L "I_runner" 5420693870L
    let persistenceId=WorkItemIdentity.persistenceId work
    let generation=Id.generation 1L
    let revision=Id.revision 7L
    let routeId=Guid.Parse("10000000-0000-0000-0000-000000000001")
    let attemptId=Id.attempt(Guid.Parse("20000000-0000-0000-0000-000000000001"))
    let sessionId=Id.session(Guid.Parse("30000000-0000-0000-0000-000000000001"))
    let runnerId=Id.runner(Guid.Parse("40000000-0000-0000-0000-000000000001"))
    let candidateId=Id.candidate(Guid.Parse("50000000-0000-0000-0000-000000000001"))
    let fingerprint=String.replicate 64 "a"
    let route=
        { RouteId=routeId;WorkItemId=work;JobClass="routine-documentation-delivery";AttemptId=attemptId;CandidateId=candidateId
          RepositoryNodeId="R_runner";BranchRef="refs/heads/fsgg/pilot/3419";ClaimResourceId="claim-3419"
          ClaimOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000001"));ProcessOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000002"))
          CandidateOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000003"));BranchOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000004"))
          PullRequestOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000005"));MergeOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000006"))
          ReadbackOperationId=Id.operation(Guid.Parse("60000000-0000-0000-0000-000000000007"));Generation=generation;WorkflowRevision=revision;SelectedAt=now.AddMinutes(-1.) }
    let enrollment={RunnerId=runnerId;PrincipalId="main-runner";FingerprintSha256=fingerprint;Generation=generation;ExpiresAt=now.AddMinutes 30.}
    let attempt={AttemptId=attemptId;SessionId=sessionId;Runner=enrollment;Generation=generation;StartedAt=now.AddMinutes(-1.);Status=Active}
    let session={SessionId=sessionId;RunnerId=runnerId;Generation=generation;LastClientSequence=0L;LastServerSequence=0L;Closed=false}
    let processIntent={OperationId=route.ProcessOperationId;Kind=DispatchRunner;Generation=generation;WorkflowRevision=revision;ResourceId=string(Id.attemptValue attemptId);PayloadSha256=String.replicate 64 "b"}
    let claimReadback=
        { OperationId=route.ClaimOperationId;RouteId=routeId;AttemptId=attemptId;CandidateId=candidateId;RepositoryNodeId="R_runner"
          ProviderResourceId="claim-3419";CandidateHeadSha=None;ResultSha=None;ProviderRevision="claim-revision";Generation=generation
          WorkflowRevision=revision;ObservedAt=now.AddSeconds(-30.);Exists=true }
    let events=
        [ WorkAdmitted({ProjectId=Id.project(Guid.Parse("70000000-0000-0000-0000-000000000001"));WorkItemId=work;WorkflowRevision=revision;CanonicalSha256=String.replicate 64 "c";BoardMembershipIds=["ready-item"];CapturedAt=now.AddMinutes(-2.)},{TokenLimit=1000L;RuntimeSecondsLimit=1000L;CostMicrosLimit=1000L;Deadline=now.AddHours 1.})
          GenerationAdvanced generation
          ReservationCreated{ReservationId=Id.reservation(Guid.Parse("80000000-0000-0000-0000-000000000001"));Generation=generation;ExpiresAt=now.AddMinutes 20.;RequiredClaimIds=Set.singleton "claim-3419"}
          ClaimObserved{ClaimId="claim-3419";Generation=generation;WorkflowRevision=revision;ObservedAt=now.AddMinutes(-1.)}
          HostedRouteSelected route;HostedEffectReadbackAccepted claimReadback;AttemptStarted attempt;RunnerSessionOpened session
          EffectIntentRecorded processIntent;EffectDispatchStarted processIntent.OperationId ]

    type Journal(initial:Event list)=
        let rows=ResizeArray<SerializedEvent>()
        let inbox=Dictionary<Guid,string*int64>()
        do initial |> List.iteri(fun index eventValue ->
            let bytes=EventEnvelope.encode eventValue
            rows.Add{PersistenceId=persistenceId;Sequence=int64 index+1L;EventId=Guid.NewGuid();SchemaVersion=1;SerializerVersion=EventEnvelope.serializerVersion;Payload=bytes;PayloadSha256=Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant();EffectChange=NoEffect;RecordedAt=now})
        interface IJournalStore with
            member _.CheckReadiness _=Task.FromResult(Ok())
            member _.Recover(id,_)=Task.FromResult(if id=persistenceId then Ok{Events=List.ofSeq rows;Snapshot=None;UnsettledEffects=[];RequiresExternalReconciliation=false} else Error[StoreUnavailable "wrong-id"])
            member _.Append(request,_)=
                let commandId=Id.commandValue request.Inbox.CommandId
                match inbox.TryGetValue commandId with
                | true,(digest,sequence) when digest=request.Inbox.BodySha256 -> Task.FromResult(Duplicate sequence)
                | true,_ -> Task.FromResult Conflict
                | false,_ when request.ExpectedSequence<>int64 rows.Count -> Task.FromResult(WrongExpectedSequence(int64 rows.Count))
                | false,_ ->
                    request.Events |> List.iter rows.Add
                    let terminal=int64 rows.Count
                    inbox.Add(commandId,(request.Inbox.BodySha256,terminal))
                    Task.FromResult(Appended terminal)
            member _.SaveSnapshot(_,_)=Task.FromResult(Ok())
            member _.SaveProjectionCheckpoint(_,_)=Task.FromResult(Ok())

    let candidates=
        { new ICandidateStore with
            member _.Put(_,_) = Task.FromResult(Error CapacityRefused)
            member _.Read(_,_) = Task.FromResult(Error "unused")
            member _.Quarantine(_,_,_) = Task.FromResult(Error "unused")
            member _.CleanupUnreferenced(_,_,_) = Task.FromResult 0 }
    type Clock()=inherit TimeProvider() override _.GetUtcNow()=now

[<Fact>]
let ``assignment and acknowledgement persist cursors and settle only dispatch effect`` () = task {
    let journal=WireFixture.Journal(WireFixture.events) :> IJournalStore
    let store={WorkItems=journal;Candidates=WireFixture.candidates}
    let initialState=replay WireFixture.events
    let poll:RunnerPollRequest=
        { Schema=RunnerWire.pollSchema;CommandId=Guid.Parse("90000000-0000-0000-0000-000000000001");WorkItemPersistenceId=WireFixture.persistenceId
          SessionId=Id.sessionValue WireFixture.sessionId;RunnerId=Id.runnerValue WireFixture.runnerId;PrincipalId="main-runner";FingerprintSha256=WireFixture.fingerprint
          Generation=1L;ExpectedRevision=Id.revisionValue initialState.Revision;ClientSequence=1L;IssuedAt=WireFixture.now.AddSeconds(-1.);ExpiresAt=WireFixture.now.AddMinutes 5. }
    let! assignment=RunnerWireRuntime.poll (WireFixture.Clock()) store WireFixture.work poll CancellationToken.None
    let assignment=assignment |> Result.defaultWith failwith
    Assert.Equal(1L,assignment.ServerSequence)
    let! afterPoll=HostedWriterJournal.recover journal WireFixture.work CancellationToken.None
    let afterPoll=afterPoll |> Result.defaultWith(fun failures -> failwithf "%A" failures)
    Assert.Equal(1L,afterPoll.State.Sessions[WireFixture.sessionId].LastClientSequence)
    Assert.Equal(1L,afterPoll.State.Sessions[WireFixture.sessionId].LastServerSequence)
    let ack:RunnerAckRequest=
        { Schema=RunnerWire.ackSchema;CommandId=Guid.Parse("90000000-0000-0000-0000-000000000002");WorkItemPersistenceId=WireFixture.persistenceId
          SessionId=poll.SessionId;RunnerId=poll.RunnerId;PrincipalId=poll.PrincipalId;FingerprintSha256=poll.FingerprintSha256
          Generation=1L;ExpectedRevision=Id.revisionValue afterPoll.State.Revision;ClientSequence=2L;AssignmentSha256=assignment.AssignmentSha256
          IssuedAt=WireFixture.now.AddSeconds(-1.);ExpiresAt=WireFixture.now.AddMinutes 5. }
    let! acknowledged=RunnerWireRuntime.acknowledge (WireFixture.Clock()) store WireFixture.work ack CancellationToken.None
    let! replayed=RunnerWireRuntime.acknowledge (WireFixture.Clock()) store WireFixture.work ack CancellationToken.None
    match acknowledged with Ok() -> () | Error reason -> failwith $"acknowledgement failed: {reason}"
    match replayed with Ok() -> () | Error reason -> failwith $"acknowledgement replay failed: {reason}"
    let! final=HostedWriterJournal.recover journal WireFixture.work CancellationToken.None
    let final=final |> Result.defaultWith(fun failures -> failwithf "%A" failures)
    Assert.Equal(2L,final.State.Sessions[WireFixture.sessionId].LastClientSequence)
    Assert.True(final.State.Operations[WireFixture.route.ProcessOperationId] |> function OperationState.Settled(intent,Applied revision) -> intent.Kind=DispatchRunner && revision.StartsWith("runner-ack:") | _ -> false)
    Assert.Empty(final.State.Candidates) }

[<Fact>]
let ``restart resumes an accepted acknowledgement without advancing its client cursor twice`` () = task {
    let pollEnvelope=
        { CommandId=Id.command(Guid.Parse("91000000-0000-0000-0000-000000000001"));ProtocolVersion=Id.protocolVersion 1 0
          ExpectedRevision=(replay WireFixture.events).Revision;ExpectedGeneration=WireFixture.generation;PrincipalId="main-runner"
          SessionId=Some WireFixture.sessionId;IssuedAt=WireFixture.now.AddSeconds(-2.);ExpiresAt=WireFixture.now.AddMinutes 5.
          Command=AcceptRunnerMessage(WireFixture.sessionId,1L,true) }
    let pollDecision=decide WireFixture.now (replay WireFixture.events) pollEnvelope
    let afterPoll=replay (WireFixture.events @ pollDecision.Events)
    let unsigned:RunnerAssignment=
        { Schema=RunnerWire.assignmentSchema;WorkItemPersistenceId=WireFixture.persistenceId;RouteId=WireFixture.routeId
          AttemptId=Id.attemptValue WireFixture.attemptId;CandidateId=Id.candidateValue WireFixture.candidateId
          SessionId=Id.sessionValue WireFixture.sessionId;RunnerId=Id.runnerValue WireFixture.runnerId;PrincipalId="main-runner"
          FingerprintSha256=WireFixture.fingerprint;Generation=1L;WorkflowRevision=7L;ClientSequence=1L;ServerSequence=1L
          PayloadSha256=WireFixture.processIntent.PayloadSha256;AssignmentSha256="";ExpiresAt=WireFixture.enrollment.ExpiresAt
          Deadline=WireFixture.now.AddHours 1. }
    let assignmentSha=RunnerWire.assignmentDigest unsigned
    let commandId=Guid.Parse("91000000-0000-0000-0000-000000000002")
    let issuedAt=WireFixture.now.AddSeconds(-1.)
    let expiresAt=WireFixture.now.AddMinutes 5.
    let acceptedEnvelope=
        { CommandId=Id.command commandId;ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=afterPoll.Revision
          ExpectedGeneration=WireFixture.generation;PrincipalId="main-runner";SessionId=Some WireFixture.sessionId
          IssuedAt=issuedAt;ExpiresAt=expiresAt;Command=AcceptRunnerMessage(WireFixture.sessionId,2L,false) }
    let accepted=decide WireFixture.now afterPoll acceptedEnvelope
    let interrupted=WireFixture.events @ pollDecision.Events @ accepted.Events
    let journal=WireFixture.Journal(interrupted) :> IJournalStore
    let store={WorkItems=journal;Candidates=WireFixture.candidates}
    let request:RunnerAckRequest=
        { Schema=RunnerWire.ackSchema;CommandId=commandId;WorkItemPersistenceId=WireFixture.persistenceId
          SessionId=Id.sessionValue WireFixture.sessionId;RunnerId=Id.runnerValue WireFixture.runnerId;PrincipalId="main-runner"
          FingerprintSha256=WireFixture.fingerprint;Generation=1L;ExpectedRevision=Id.revisionValue afterPoll.Revision
          ClientSequence=2L;AssignmentSha256=assignmentSha;IssuedAt=issuedAt;ExpiresAt=expiresAt }
    let! resumed=RunnerWireRuntime.acknowledge (WireFixture.Clock()) store WireFixture.work request CancellationToken.None
    match resumed with Ok() -> () | Error reason -> failwith $"restart recovery failed: {reason}"
    let! recovered=HostedWriterJournal.recover journal WireFixture.work CancellationToken.None
    let recovered=recovered |> Result.defaultWith(fun failures -> failwithf "%A" failures)
    Assert.Equal(2L,recovered.State.Sessions[WireFixture.sessionId].LastClientSequence)
    Assert.True(recovered.State.Operations[WireFixture.route.ProcessOperationId] |> function OperationState.Settled _ -> true | _ -> false) }
