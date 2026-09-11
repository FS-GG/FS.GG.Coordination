module FS.GG.Coordination.OrchestrationTests

open System
open Xunit
open FS.GG.Coordination.Core.Orchestration

let now = DateTimeOffset(2026,9,10,12,0,0,TimeSpan.Zero)
let digest c = String.replicate 64 c
let guid (value:string) = Guid.Parse value
let command v = Id.command(guid v)
let work = WorkItemIdentity.create "R_node_immutable" 42L "I_node_immutable" 17L
let project = Id.project(guid "10000000-0000-0000-0000-000000000001")
let snapshot boards =
        {ProjectId=project;WorkItemId=work
         WorkflowRevision=Id.revision 8L;CanonicalSha256=digest "a";BoardMembershipIds=boards;CapturedAt=now}
let budget={TokenLimit=100L;RuntimeSecondsLimit=200L;CostMicrosLimit=300L;Deadline=now.AddHours 1.}
let apply decision state = List.fold evolve state decision.Events
let decide now state commandId _ command =
    FS.GG.Coordination.Core.Orchestration.decide now state
        {CommandId=commandId;ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=state.Revision;ExpectedGeneration=state.Generation
         PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=command}
let admit () = decide now initial (command "20000000-0000-0000-0000-000000000001") (digest "1") (Admit(snapshot ["board-a"],budget)) |> fun d -> apply d initial
let hostedRoute (state:State) attemptId candidateId : HostedRoutePlan =
    { RouteId=guid "90000000-0000-0000-0000-000000000001"; WorkItemId=work
      JobClass="routine-documentation-delivery"; AttemptId=attemptId; CandidateId=candidateId
      RepositoryNodeId="R_repo"; BranchRef="refs/heads/fsgg/pilot/recovery-evidence"; ClaimResourceId="pilot-claim"
      ClaimOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000001")
      ProcessOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000002")
      CandidateOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000003")
      BranchOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000004")
      PullRequestOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000005")
      MergeOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000006")
      ReadbackOperationId=Id.operation(guid "71000000-0000-0000-0000-000000000007")
      Generation=state.Generation; WorkflowRevision=Id.revision 8L; SelectedAt=now.AddMinutes(-1.) }
let effect operationId kind resource (state:State) =
    { OperationId=Id.operation(guid operationId); Kind=kind; Generation=state.Generation
      WorkflowRevision=Id.revision 8L; ResourceId=resource; PayloadSha256=digest "e" }
let routeEffect operationId kind resource (route:HostedRoutePlan) =
    { OperationId=operationId; Kind=kind; Generation=route.Generation
      WorkflowRevision=route.WorkflowRevision; ResourceId=resource; PayloadSha256=digest "e" }
let recordDispatchReadback commandStem (route:HostedRoutePlan) (intent:EffectIntent) providerResource candidateHead resultSha state =
    let recorded = decide now state (command ($"%s{commandStem}1")) "" (RecordEffectIntent intent) |> fun d -> apply d state
    let dispatched = decide now recorded (command ($"%s{commandStem}2")) "" (MarkEffectDispatching intent.OperationId) |> fun d -> apply d recorded
    let readback =
        { OperationId=intent.OperationId;RouteId=route.RouteId;AttemptId=route.AttemptId;CandidateId=route.CandidateId
          RepositoryNodeId=route.RepositoryNodeId;ProviderResourceId=providerResource
          CandidateHeadSha=candidateHead;ResultSha=resultSha;ProviderRevision=$"provider-%A{intent.Kind}"
          Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ObservedAt=now;Exists=true }
    decide now dispatched (command ($"%s{commandStem}3")) "" (RecordHostedEffectReadback(intent.OperationId,readback)) |> fun d -> apply d dispatched
let hostedReady () =
    let admitted = admit()
    let reservationId = Id.reservation(guid "31000000-0000-0000-0000-000000000001")
    let reserved = decide now admitted (command "26000000-0000-0000-0000-000000000001") "" (Reserve(reservationId,now.AddMinutes 5.,Set.singleton "pilot-claim")) |> fun d -> apply d admitted
    let attemptId = Id.attempt(guid "51000000-0000-0000-0000-000000000001")
    let candidateId = Id.candidate(guid "83000000-0000-0000-0000-000000000001")
    let route = hostedRoute reserved attemptId candidateId
    let selected = decide now reserved (command "26000000-0000-0000-0000-000000000002") "" (SelectHostedRoute route) |> fun d -> apply d reserved
    let claimIntent = routeEffect route.ClaimOperationId AcquireExternalClaim route.ClaimResourceId route
    let claimedEffect = recordDispatchReadback "26100000-0000-0000-0000-00000000000" route claimIntent route.ClaimResourceId None None selected
    let claim = { ClaimId=route.ClaimResourceId; Generation=route.Generation; WorkflowRevision=route.WorkflowRevision; ObservedAt=now }
    let claimed = decide now claimedEffect (command "26000000-0000-0000-0000-000000000003") "" (ObserveClaim claim) |> fun d -> apply d claimedEffect
    let runner = { RunnerId=Id.runner(guid "41000000-0000-0000-0000-000000000001"); PrincipalId="runner"; FingerprintSha256=digest "4"; Generation=claimed.Generation; ExpiresAt=now.AddMinutes 5. }
    let active = decide now claimed (command "26000000-0000-0000-0000-000000000004") "" (StartAttempt(attemptId,Id.session(guid "61000000-0000-0000-0000-000000000001"),runner)) |> fun d -> apply d claimed
    active,attemptId,candidateId,route

module Cases =
    [<Fact>]
    let ``canonical WorkItem ignores aliases urls and multi-board projection membership`` () =
        Assert.Equal(WorkItemIdentity.persistenceId (snapshot ["board-a"]).WorkItemId,WorkItemIdentity.persistenceId (snapshot ["board-z";"board-a"]).WorkItemId)
        let legacy=WorkItemIdentity.create "MDQ6VXNlcjU4MzIzMQ==" 42L "MDU6SXNzdWUxNw==" 17L
        let modern=WorkItemIdentity.create "R_new_global_id" 42L "I_new_global_id" 17L
        Assert.Equal(WorkItemIdentity.persistenceId legacy,WorkItemIdentity.persistenceId modern)

    [<Fact>]
    let ``duplicate command is idempotent and changed content conflicts`` () =
        let cid=command "20000000-0000-0000-0000-000000000002"
        let envelope={CommandId=cid;ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=initial.Revision;ExpectedGeneration=initial.Generation;PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=Admit(snapshot [],budget)}
        let first=FS.GG.Coordination.Core.Orchestration.decide now initial envelope
        let state=apply first initial
        let duplicate=FS.GG.Coordination.Core.Orchestration.decide now state envelope
        let conflict=FS.GG.Coordination.Core.Orchestration.decide now state {envelope with Command=Pause "changed-body"}
        Assert.Equal(Duplicate,duplicate.Receipt.Disposition); Assert.Empty duplicate.Events
        Assert.Equal(Conflict,conflict.Receipt.Disposition); Assert.Empty conflict.Events

    [<Fact>]
    let ``command envelope computes stable lowercase digest inside boundary`` () =
        let cid=command "20000000-0000-0000-0000-000000000016"
        let body=Admit(snapshot [],budget)
        let envelope={CommandId=cid;ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=initial.Revision;ExpectedGeneration=initial.Generation;PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=body}
        let accepted=FS.GG.Coordination.Core.Orchestration.decide now initial envelope
        let state=apply accepted initial
        let stored=(Map.find cid state.CommandReceipts).BodySha256
        Assert.Equal(stored,stored.ToLowerInvariant())
        Assert.Equal(Duplicate,(FS.GG.Coordination.Core.Orchestration.decide now state envelope).Receipt.Disposition)

    [<Fact>]
    let ``stale generation revision and expired envelopes refuse before state change`` () =
        let state=admit()
        let baseEnvelope={CommandId=command "20000000-0000-0000-0000-000000000017";ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=state.Revision;ExpectedGeneration=state.Generation;PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=Pause "maintenance"}
        let staleRevision={baseEnvelope with ExpectedRevision=Id.revision 0L}
        let staleGeneration={baseEnvelope with CommandId=command "20000000-0000-0000-0000-000000000018";ExpectedGeneration=Id.generation 0L}
        let expired={baseEnvelope with CommandId=command "20000000-0000-0000-0000-000000000019";ExpiresAt=now.AddTicks(-1L)}
        Assert.Equal("stale-workflow-revision",(FS.GG.Coordination.Core.Orchestration.decide now state staleRevision).Receipt.Detail)
        Assert.Equal("stale-generation",(FS.GG.Coordination.Core.Orchestration.decide now state staleGeneration).Receipt.Detail)
        Assert.Equal("invalid-or-expired-command-envelope",(FS.GG.Coordination.Core.Orchestration.decide now state expired).Receipt.Detail)
        let unsupported={baseEnvelope with CommandId=command "20000000-0000-0000-0000-000000000020";ProtocolVersion=Id.protocolVersion 2 0}
        Assert.Equal("unsupported-command-version",(FS.GG.Coordination.Core.Orchestration.decide now state unsupported).Receipt.Detail)

    [<Fact>]
    let ``reservation or claim alone cannot dispatch and current pair can`` () =
        let admitted=admit()
        let rid=Id.reservation(guid "30000000-0000-0000-0000-000000000001")
        let reserved=decide now admitted (command "20000000-0000-0000-0000-000000000003") (digest "3") (Reserve(rid,now.AddMinutes 5.,Set.singleton "claim")) |> fun d -> apply d admitted
        let runner={RunnerId=Id.runner(guid "40000000-0000-0000-0000-000000000001");PrincipalId="runner";FingerprintSha256=digest "4";Generation=reserved.Generation;ExpiresAt=now.AddMinutes 5.}
        let start=StartAttempt(Id.attempt(guid "50000000-0000-0000-0000-000000000001"),Id.session(guid "60000000-0000-0000-0000-000000000001"),runner)
        Assert.Equal(Rejected,(decide now reserved (command "20000000-0000-0000-0000-000000000004") (digest "4") start).Receipt.Disposition)
        let claim={ClaimId="claim";Generation=reserved.Generation;WorkflowRevision=Id.revision 8L;ObservedAt=now}
        let staleClaim={claim with WorkflowRevision=Id.revision 7L}
        let stale=decide now reserved (command "20000000-0000-0000-0000-000000000014") (digest "e") (ObserveClaim staleClaim) |> fun d -> apply d reserved
        Assert.Equal(Rejected,(decide now stale (command "20000000-0000-0000-0000-000000000015") (digest "f") start).Receipt.Disposition)
        let claimed=decide now reserved (command "20000000-0000-0000-0000-000000000005") (digest "5") (ObserveClaim claim) |> fun d -> apply d reserved
        Assert.Equal(Accepted,(decide now claimed (command "20000000-0000-0000-0000-000000000006") (digest "6") start).Receipt.Disposition)

    [<Fact>]
    let ``attempt history permits replacement only after an observed terminal outcome`` () =
        let admitted=admit()
        let rid=Id.reservation(guid "30000000-0000-0000-0000-000000000003")
        let reserved=decide now admitted (command "25000000-0000-0000-0000-000000000001") (digest "1") (Reserve(rid,now.AddMinutes 5.,Set.singleton "claim")) |> fun d -> apply d admitted
        let claim={ClaimId="claim";Generation=reserved.Generation;WorkflowRevision=Id.revision 8L;ObservedAt=now}
        let claimed=decide now reserved (command "25000000-0000-0000-0000-000000000002") (digest "2") (ObserveClaim claim) |> fun d -> apply d reserved
        let runner={RunnerId=Id.runner(guid "40000000-0000-0000-0000-000000000002");PrincipalId="runner";FingerprintSha256=digest "4";Generation=claimed.Generation;ExpiresAt=now.AddMinutes 5.}
        let firstId=Id.attempt(guid "50000000-0000-0000-0000-000000000002")
        let first=StartAttempt(firstId,Id.session(guid "60000000-0000-0000-0000-000000000002"),runner)
        let active=decide now claimed (command "25000000-0000-0000-0000-000000000003") (digest "3") first |> fun d -> apply d claimed
        let replacement=StartAttempt(Id.attempt(guid "50000000-0000-0000-0000-000000000003"),Id.session(guid "60000000-0000-0000-0000-000000000003"),runner)
        Assert.Equal(Rejected,(decide now active (command "25000000-0000-0000-0000-000000000004") (digest "4") replacement).Receipt.Disposition)
        let unknown=decide now active (command "25000000-0000-0000-0000-000000000005") (digest "5") (ObserveAttempt(firstId,OutcomeUnknown "heartbeat-lost")) |> fun d -> apply d active
        Assert.Equal(Rejected,(decide now unknown (command "25000000-0000-0000-0000-000000000006") (digest "6") replacement).Receipt.Disposition)
        let terminal=decide now unknown (command "25000000-0000-0000-0000-000000000007") (digest "7") (ObserveAttempt(firstId,ReconciledAbsent "runner-and-provider-observed")) |> fun d -> apply d unknown
        Assert.Equal(Accepted,(decide now terminal (command "25000000-0000-0000-0000-000000000008") (digest "8") replacement).Receipt.Disposition)
        let reusedId=StartAttempt(firstId,Id.session(guid "60000000-0000-0000-0000-000000000004"),runner)
        Assert.Equal(Conflict,(decide now terminal (command "25000000-0000-0000-0000-000000000009") (digest "9") reusedId).Receipt.Disposition)

    [<Fact>]
    let ``partial multi-touch claim release retains recovery capacity after compensation failure`` () =
        let admitted=admit()
        let rid=Id.reservation(guid "30000000-0000-0000-0000-000000000002")
        let reserved=decide now admitted (command "21000000-0000-0000-0000-000000000001") (digest "1") (Reserve(rid,now.AddMinutes 5.,Set.ofList ["claim-a";"claim-b"])) |> fun d -> apply d admitted
        let claim={ClaimId="claim-a";Generation=reserved.Generation;WorkflowRevision=Id.revision 8L;ObservedAt=now}
        let partial=decide now reserved (command "21000000-0000-0000-0000-000000000002") (digest "2") (ObserveClaim claim) |> fun d -> apply d reserved
        let released=decide now partial (command "21000000-0000-0000-0000-000000000003") (digest "3") (ReleaseReservation "claim-b-lost") |> fun d -> apply d partial
        let failed=decide now released (command "21000000-0000-0000-0000-000000000004") (digest "4") (RecordCompensationFailure("claim-a","provider-unavailable")) |> fun d -> apply d released
        Assert.True(failed.Reservation.IsNone)
        Assert.Contains("claim-a",failed.RecoveryObligations)
        Assert.Equal("provider-unavailable",failed.CompensationFailures["claim-a"])

    [<Fact>]
    let ``unknown external effect requires observation before retry`` () =
        let state=admit()
        let oid=Id.operation(guid "70000000-0000-0000-0000-000000000001")
        let intent={OperationId=oid;Kind=InspectExternalOperation;Generation=state.Generation;WorkflowRevision=Id.revision 8L;ResourceId="external-operation";PayloadSha256=digest "7"}
        let recorded=decide now state (command "20000000-0000-0000-0000-000000000007") (digest "7") (RecordEffectIntent intent) |> fun d -> apply d state
        let dispatch=decide now recorded (command "20000000-0000-0000-0000-000000000008") (digest "8") (MarkEffectDispatching oid)
        Assert.Single dispatch.Effects |> ignore
        let unknown=decide now (apply dispatch recorded) (command "20000000-0000-0000-0000-000000000009") (digest "9") (ObserveEffect(oid,Unknown "lost")) |> fun d -> apply d (apply dispatch recorded)
        let retry=decide now unknown (command "20000000-0000-0000-0000-000000000010") (digest "a") (MarkEffectDispatching oid)
        Assert.Equal("observe-before-retry",retry.Receipt.Detail)
        let absent=decide now unknown (command "22000000-0000-0000-0000-000000000001") (digest "b") (ObserveEffect(oid,ProvenAbsent)) |> fun d -> apply d unknown
        let authorized=decide now absent (command "22000000-0000-0000-0000-000000000002") (digest "c") (AuthorizeEffectRetry oid) |> fun d -> apply d absent
        Assert.Equal(Accepted,(decide now authorized (command "22000000-0000-0000-0000-000000000003") (digest "d") (MarkEffectDispatching oid)).Receipt.Disposition)

    [<Fact>]
    let ``hosted writer effects require ordered durable stages`` () =
        let active,attemptId,candidateId,route = hostedReady()
        let storeIntent = routeEffect route.CandidateOperationId StoreCandidate (Id.candidateValue candidateId |> string) route
        let premature = decide now active (command "27000000-0000-0000-0000-000000000001") "" (RecordEffectIntent storeIntent)
        Assert.Equal("hosted-effect-predecessor-required",premature.Receipt.Detail)
        let processIntent = routeEffect route.ProcessOperationId DispatchRunner (Id.attemptValue attemptId |> string) route
        let processed = recordDispatchReadback "27100000-0000-0000-0000-00000000000" route processIntent (Id.attemptValue attemptId |> string) None None active
        let recorded = decide now processed (command "27200000-0000-0000-0000-000000000001") "" (RecordEffectIntent storeIntent) |> fun d -> apply d processed
        Assert.Equal(Accepted,(decide now recorded (command "27200000-0000-0000-0000-000000000002") "" (MarkEffectDispatching storeIntent.OperationId)).Receipt.Disposition)

    [<Fact>]
    let ``startup persists pause and invalidates hosted route until exact readback`` () =
        let active,_,_,route = hostedReady()
        let candidateIntent = routeEffect route.CandidateOperationId StoreCandidate (Id.candidateValue route.CandidateId |> string) route
        let primed = { active with Operations=Map.add candidateIntent.OperationId (IntentRecorded candidateIntent) active.Operations }
        let startup = decide now primed (command "27800000-0000-0000-0000-000000000001") "" (RecordStartupPause "process-startup")
        Assert.Equal(Accepted,startup.Receipt.Disposition)
        let paused = apply startup primed
        Assert.Equal(Paused "process-startup",paused.Control)
        Assert.False(paused.ReadbackCurrent)
        Assert.Equal("resume-refused",(decide now paused (command "27800000-0000-0000-0000-000000000002") "" Resume).Receipt.Detail)
        Assert.Equal("effect-not-authorized",(decide now paused (command "27800000-0000-0000-0000-000000000006") "" (MarkEffectDispatching candidateIntent.OperationId)).Receipt.Detail)
        let readback =
            { RouteId=route.RouteId;WorkItemId=route.WorkItemId;RepositoryNodeId=route.RepositoryNodeId
              ProviderRevision="github-route-revision-1";EvidenceSha256=digest "f"
              Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ObservedAt=now }
        let reconnected = decide now paused (command "27800000-0000-0000-0000-000000000004") "" (RecordHostedRouteReadback readback)
        Assert.Equal(Accepted,reconnected.Receipt.Disposition)
        let current = apply reconnected paused
        Assert.True(current.ReadbackCurrent)
        Assert.Equal(Accepted,(decide now current (command "27800000-0000-0000-0000-000000000005") "" Resume).Receipt.Disposition)

    [<Fact>]
    let ``unknown hosted effect blocks replacement and later effects until proven absent`` () =
        let active,attemptId,candidateId,route = hostedReady()
        let processIntent = routeEffect route.ProcessOperationId DispatchRunner (Id.attemptValue attemptId |> string) route
        let processed = recordDispatchReadback "27300000-0000-0000-0000-00000000000" route processIntent (Id.attemptValue attemptId |> string) None None active
        let storeIntent = routeEffect route.CandidateOperationId StoreCandidate (Id.candidateValue candidateId |> string) route
        let recorded = decide now processed (command "27400000-0000-0000-0000-000000000001") "" (RecordEffectIntent storeIntent) |> fun d -> apply d processed
        let dispatch = decide now recorded (command "27400000-0000-0000-0000-000000000002") "" (MarkEffectDispatching storeIntent.OperationId) |> fun d -> apply d recorded
        let unknown = decide now dispatch (command "27400000-0000-0000-0000-000000000003") "" (ObserveEffect(storeIntent.OperationId,Unknown "response-lost")) |> fun d -> apply d dispatch
        let replacement = effect "72000000-0000-0000-0000-000000000003" StoreCandidate (Id.candidateValue candidateId |> string) unknown
        Assert.NotEqual(Accepted,(decide now unknown (command "27400000-0000-0000-0000-000000000004") "" (RecordEffectIntent replacement)).Receipt.Disposition)
        let absence =
            { OperationId=storeIntent.OperationId;RouteId=route.RouteId;AttemptId=attemptId;CandidateId=candidateId
              RepositoryNodeId=route.RepositoryNodeId;ProviderResourceId=storeIntent.ResourceId;CandidateHeadSha=None;ResultSha=None
              ProviderRevision="provider-store-absent";Generation=route.Generation;WorkflowRevision=route.WorkflowRevision;ObservedAt=now;Exists=false }
        let absent = decide now unknown (command "27400000-0000-0000-0000-000000000005") "" (RecordHostedEffectReadback(storeIntent.OperationId,absence)) |> fun d -> apply d unknown
        let retried = decide now absent (command "27400000-0000-0000-0000-000000000006") "" (AuthorizeEffectRetry storeIntent.OperationId) |> fun d -> apply d absent
        Assert.Equal(Accepted,(decide now retried (command "27400000-0000-0000-0000-000000000007") "" (MarkEffectDispatching storeIntent.OperationId)).Receipt.Disposition)

    [<Fact>]
    let ``adapter claim cannot complete hosted delivery without native readback`` () =
        let active,attemptId,candidateId,route = hostedReady()
        let processed = recordDispatchReadback "27500000-0000-0000-0000-00000000000" route (routeEffect route.ProcessOperationId DispatchRunner (Id.attemptValue attemptId |> string) route) (Id.attemptValue attemptId |> string) None None active
        let contentDigest = digest "d"
        let candidate = { CandidateId=candidateId;BaselineSha=String.replicate 40 "a";HeadSha=String.replicate 40 "b";TreeSha=String.replicate 40 "c";ManifestSha256=digest "c";ContentSha256=contentDigest;MediaType="application/vnd.fsgg.runner-candidate+zip";SizeBytes=10L;RetainUntil=now.AddDays 1.;Location=ContentAddressedObject $"sha256/{contentDigest}" }
        let proof = { CandidateId=candidateId;ContentSha256=candidate.ContentSha256;ManifestSha256=candidate.ManifestSha256;SizeBytes=candidate.SizeBytes;Location=candidate.Location;StoreId="postgresql-object-store";StoreSchemaVersion=1;StorageReceiptSha256=digest "e";VerifiedAt=now }
        let stored = recordDispatchReadback "27600000-0000-0000-0000-00000000000" route (routeEffect route.CandidateOperationId StoreCandidate (Id.candidateValue candidateId |> string) route) (Id.candidateValue candidateId |> string) (Some candidate.HeadSha) (Some candidate.ContentSha256) processed
        let accepted = decide now stored (command "27700000-0000-0000-0000-000000000001") "" (RecordCandidate(candidate,proof)) |> fun d -> apply d stored
        let published = recordDispatchReadback "27800000-0000-0000-0000-00000000000" route (routeEffect route.BranchOperationId PublishCandidateBranch route.BranchRef route) route.BranchRef (Some candidate.HeadSha) (Some candidate.HeadSha) accepted
        let opened = recordDispatchReadback "27900000-0000-0000-0000-00000000000" route (routeEffect route.PullRequestOperationId CreatePullRequest route.BranchRef route) "PR_node" (Some candidate.HeadSha) None published
        let mergeSha=String.replicate 40 "f"
        let merged = recordDispatchReadback "28000000-0000-0000-0000-00000000000" route (routeEffect route.MergeOperationId MergePullRequest route.BranchRef route) "PR_node" (Some candidate.HeadSha) (Some mergeSha) opened
        let readIntent = routeEffect route.ReadbackOperationId ReadNativeDelivery route.BranchRef route
        let recorded = decide now merged (command "28100000-0000-0000-0000-000000000001") "" (RecordEffectIntent readIntent) |> fun d -> apply d merged
        let dispatched = decide now recorded (command "28100000-0000-0000-0000-000000000002") "" (MarkEffectDispatching readIntent.OperationId) |> fun d -> apply d recorded
        Assert.Equal("hosted-effect-readback-required",(decide now dispatched (command "28100000-0000-0000-0000-000000000003") "" (ObserveEffect(readIntent.OperationId,Applied "adapter-says-merged"))).Receipt.Detail)
        Assert.Equal("native-delivery-readback-required",(decide now dispatched (command "28100000-0000-0000-0000-000000000004") "" (ObserveAttempt(attemptId,Completed))).Receipt.Detail)
        let readback = { OperationId=readIntent.OperationId;RouteId=route.RouteId;AttemptId=attemptId;CandidateId=candidateId;RepositoryNodeId=route.RepositoryNodeId;PullRequestNodeId="PR_node";CandidateHeadSha=candidate.HeadSha;ObservedPullRequestHeadSha=candidate.HeadSha;MergeCommitSha=mergeSha;ProviderRevision="github-pr-revision-1";Generation=dispatched.Generation;WorkflowRevision=Id.revision 8L;ObservedAt=now;Merged=true }
        let wrongAttempt = {readback with AttemptId=Id.attempt(guid "51000000-0000-0000-0000-000000000099")}
        let wrongCandidate = {readback with CandidateId=Id.candidate(guid "83000000-0000-0000-0000-000000000099")}
        let wrongRepository = {readback with RepositoryNodeId="R_other"}
        let wrongHead = {readback with ObservedPullRequestHeadSha=String.replicate 40 "e"}
        let wrongMerge = {readback with MergeCommitSha=String.replicate 40 "e"}
        let wrongGeneration = {readback with Generation=Id.generation 2L}
        let stale = {readback with ObservedAt=route.SelectedAt.AddTicks(-1L)}
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000007") "" (RecordNativeDeliveryReadback(readIntent.OperationId,wrongAttempt))).Receipt.Disposition)
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000010") "" (RecordNativeDeliveryReadback(readIntent.OperationId,wrongCandidate))).Receipt.Disposition)
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000008") "" (RecordNativeDeliveryReadback(readIntent.OperationId,wrongRepository))).Receipt.Disposition)
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000011") "" (RecordNativeDeliveryReadback(readIntent.OperationId,wrongHead))).Receipt.Disposition)
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000012") "" (RecordNativeDeliveryReadback(readIntent.OperationId,wrongMerge))).Receipt.Disposition)
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000013") "" (RecordNativeDeliveryReadback(readIntent.OperationId,wrongGeneration))).Receipt.Disposition)
        Assert.Equal(Rejected,(decide now dispatched (command "28100000-0000-0000-0000-000000000009") "" (RecordNativeDeliveryReadback(readIntent.OperationId,stale))).Receipt.Disposition)
        let forgedCompletion =
            { dispatched with
                NativeDeliveryReadbacks=Map.add readIntent.OperationId wrongHead dispatched.NativeDeliveryReadbacks
                Operations=Map.add readIntent.OperationId (Settled(readIntent,Applied wrongHead.ProviderRevision)) dispatched.Operations }
        Assert.Equal("native-delivery-readback-required",(decide now forgedCompletion (command "28100000-0000-0000-0000-000000000014") "" (ObserveAttempt(attemptId,Completed))).Receipt.Detail)
        let acceptedReadback = decide now dispatched (command "28100000-0000-0000-0000-000000000005") "" (RecordNativeDeliveryReadback(readIntent.OperationId,readback)) |> fun d -> apply d dispatched
        Assert.Equal(Accepted,(decide now acceptedReadback (command "28100000-0000-0000-0000-000000000006") "" (ObserveAttempt(attemptId,Completed))).Receipt.Disposition)

    [<Fact>]
    let ``hosted predecessor receipts cannot be borrowed from another route identity or generation`` () =
        let active,attemptId,_,route = hostedReady()
        let processIntent = routeEffect route.ProcessOperationId DispatchRunner (Id.attemptValue attemptId |> string) route
        let claimReceipt = active.HostedEffectReadbacks[route.ClaimOperationId]
        let otherAttempt = {claimReceipt with AttemptId=Id.attempt(guid "51000000-0000-0000-0000-000000000098")}
        let oldGeneration = {claimReceipt with Generation=Id.generation 0L}
        let assertBlocked receipt =
            let state = {active with HostedEffectReadbacks=Map.add route.ClaimOperationId receipt active.HostedEffectReadbacks}
            Assert.Equal("hosted-effect-predecessor-required",(decide now state (command "28200000-0000-0000-0000-000000000001") "" (RecordEffectIntent processIntent)).Receipt.Detail)
        assertBlocked otherAttempt
        assertBlocked oldGeneration

    [<Fact>]
    let ``settled prior generation permits later resource while unresolved prior work blocks`` () =
        let admitted = admit()
        let first = effect "74000000-0000-0000-0000-000000000001" InspectExternalOperation "provider-resource" admitted
        let recorded = decide now admitted (command "28300000-0000-0000-0000-000000000001") "" (RecordEffectIntent first) |> fun d -> apply d admitted
        let nextGeneration = Id.generation 2L
        let unresolved = evolve recorded (GenerationAdvanced nextGeneration)
        let later = {effect "74000000-0000-0000-0000-000000000002" InspectExternalOperation "provider-resource" unresolved with Generation=nextGeneration}
        Assert.Equal(Conflict,(decide now unresolved (command "28300000-0000-0000-0000-000000000002") "" (RecordEffectIntent later)).Receipt.Disposition)
        let settled = evolve recorded (EffectSettled(first.OperationId,Applied "provider-revision-1")) |> fun state -> evolve state (GenerationAdvanced nextGeneration)
        Assert.Equal(Accepted,(decide now settled (command "28300000-0000-0000-0000-000000000003") "" (RecordEffectIntent later)).Receipt.Disposition)

    [<Fact>]
    let ``dispatch effect cannot bypass work authorization guards`` () =
        let state=admit()
        let oid=Id.operation(guid "70000000-0000-0000-0000-000000000002")
        let intent={OperationId=oid;Kind=DispatchRunner;Generation=state.Generation;WorkflowRevision=Id.revision 8L;ResourceId="attempt";PayloadSha256=digest "e"}
        let recorded=decide now state (command "23000000-0000-0000-0000-000000000001") (digest "e") (RecordEffectIntent intent) |> fun d -> apply d state
        Assert.Equal("effect-not-authorized",(decide now recorded (command "23000000-0000-0000-0000-000000000002") (digest "f") (MarkEffectDispatching oid)).Receipt.Detail)

    [<Fact>]
    let ``changed command body cannot reuse a command identity`` () =
        let cid=command "24000000-0000-0000-0000-000000000001"
        let original=Pause "first"
        let state=admit()
        let accepted=decide now state cid (digest "a") original
        let persisted=apply accepted state
        let changed=decide now persisted cid (digest "b") (Pause "changed")
        Assert.Equal(Conflict,changed.Receipt.Disposition)

    [<Fact>]
    let ``work item node framing rejects delimiter injection`` () =
        Assert.Throws<ArgumentException>(fun () -> WorkItemIdentity.create "repo\nissue-node:forged" 42L "issue" 17L |> ignore) |> ignore

    [<Fact>]
    let ``budget arithmetic refuses signed overflow`` () =
        let state={admit() with Used={Tokens=Int64.MaxValue;RuntimeSeconds=0L;CostMicros=0L}}
        let outcome=decide now state (command "24000000-0000-0000-0000-000000000002") (digest "f") (ChargeBudget{Tokens=1L;RuntimeSeconds=0L;CostMicros=0L})
        Assert.Equal("budget-exceeded-or-expired",outcome.Receipt.Detail)

    [<Fact>]
    let ``subscription admission reaches durable dispatch without fabricated token or cost ceilings`` () =
        let subscription =
            { Schema="fsgg.coordination.subscription-execution-budget/1";AttemptLimit=1
              MaximumRuntime=TimeSpan.FromMinutes 30.;ExecutionDeadline=now.AddMinutes 30.
              Usage=TokensUnknown "provider-has-not-reported-usage"
              Cost={InvocationState="not-applicable";InvocationProvenance="subscription-session";BroaderAttributionState="unknown";BroaderAttributionProvenance="subscription-cost-not-attributable"} }
        let decideV2 at state id body =
            FS.GG.Coordination.Core.Orchestration.decide at state
                {CommandId=command id;ProtocolVersion=Id.protocolVersion 2 0;ExpectedRevision=state.Revision;ExpectedGeneration=state.Generation
                 PrincipalId="test-principal";SessionId=None;IssuedAt=at;ExpiresAt=at.AddMinutes 1.;Command=body}
        let admittedDecision=decideV2 now initial "25000000-0000-0000-0000-000000000001" (AdmitSubscription(snapshot ["board-a"],subscription))
        Assert.Equal(Accepted,admittedDecision.Receipt.Disposition)
        let admitted=apply admittedDecision initial
        Assert.True(admitted.Budget.IsNone)
        Assert.True(admitted.SubscriptionBudget.IsSome)
        let reservationId=Id.reservation(guid "35000000-0000-0000-0000-000000000001")
        let reserved=decide now admitted (command "25000000-0000-0000-0000-000000000002") "" (Reserve(reservationId,now.AddMinutes 5.,Set.singleton "claim")) |> fun result->apply result admitted
        let claim={ClaimId="claim";Generation=reserved.Generation;WorkflowRevision=Id.revision 8L;ObservedAt=now}
        let claimed=decide now reserved (command "25000000-0000-0000-0000-000000000003") "" (ObserveClaim claim) |> fun result->apply result reserved
        let runner={RunnerId=Id.runner(guid "45000000-0000-0000-0000-000000000001");PrincipalId="runner";FingerprintSha256=digest "4";Generation=claimed.Generation;ExpiresAt=now.AddMinutes 5.}
        let started=decide now claimed (command "25000000-0000-0000-0000-000000000004") "" (StartAttempt(Id.attempt(guid "55000000-0000-0000-0000-000000000001"),Id.session(guid "65000000-0000-0000-0000-000000000001"),runner))
        Assert.Equal(Accepted,started.Receipt.Disposition)
        let active=apply started claimed
        let accounting={ObservedAt=now.AddMinutes 40.;RuntimeSeconds=2400L;RuntimeWithinBound=false;Usage=TokensUnknown "provider-not-reported";Cost=subscription.Cost}
        let late=decideV2 (now.AddMinutes 40.) active "25000000-0000-0000-0000-000000000005" (RecordSubscriptionAccounting accounting)
        Assert.Equal(Accepted,late.Receipt.Disposition)
        let replayed=apply late active
        Assert.Equal(Some accounting,replayed.SubscriptionAccounting)
        Assert.Single replayed.Attempts |> ignore
        let downgraded={CommandId=command "25000000-0000-0000-0000-000000000006";ProtocolVersion=Id.protocolVersion 1 0;ExpectedRevision=initial.Revision;ExpectedGeneration=initial.Generation;PrincipalId="test";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=AdmitSubscription(snapshot [],subscription)}
        Assert.Equal("unsupported-command-version",(FS.GG.Coordination.Core.Orchestration.decide now initial downgraded).Receipt.Detail)

    [<Fact>]
    let ``revocation advances durable generation`` () =
        let state=admit()
        let decision=decide now state (command "20000000-0000-0000-0000-000000000011") (digest "b") (Revoke "operator")
        let replayed=List.fold evolve state decision.Events
        match replayed.Control with | Revoked "operator" -> () | x -> failwithf "%A" x
        Assert.True(Id.generationValue replayed.Generation > Id.generationValue state.Generation)

    [<Theory>]
    [<InlineData("../secret")>]
    [<InlineData("a/../../secret")>]
    [<InlineData("/absolute")>]
    [<InlineData("C:\\absolute")>]
    let ``candidate archive traversal is refused`` entry = Assert.False(CandidateArtifact.archiveEntryIsSafe entry)

    [<Fact>]
    let ``candidate acknowledgement needs recoverability`` () =
        let state=admit()
        let contentDigest=digest "d"
        let candidate={CandidateId=Id.candidate(guid "80000000-0000-0000-0000-000000000001");BaselineSha=String.replicate 40 "a";HeadSha=String.replicate 40 "b";TreeSha=String.replicate 40 "c";ManifestSha256=digest "c";ContentSha256=contentDigest;MediaType="application/vnd.fsgg.runner-candidate+zip";SizeBytes=10L;RetainUntil=now.AddDays 1.;Location=ContentAddressedObject $"sha256/{contentDigest}"}
        let proof={CandidateId=candidate.CandidateId;ContentSha256=candidate.ContentSha256;ManifestSha256=candidate.ManifestSha256;SizeBytes=candidate.SizeBytes;Location=candidate.Location;StoreId="postgresql-object-store";StoreSchemaVersion=1;StorageReceiptSha256=digest "e";VerifiedAt=now}
        Assert.Equal(Rejected,(decide now state (command "20000000-0000-0000-0000-000000000012") (digest "c") (RecordCandidate(candidate,{proof with SizeBytes=11L}))).Receipt.Disposition)
        Assert.Equal(Accepted,(decide now state (command "20000000-0000-0000-0000-000000000013") (digest "d") (RecordCandidate(candidate,proof))).Receipt.Disposition)

    [<Fact>]
    let ``project scheduler preserves recovery capacity`` () =
        let initial=ProjectOrchestrator.initial project 2 1
        let running=ProjectOrchestrator.evolve initial ProjectResumed
        let firstId=Id.reservation(guid "30000000-0000-0000-0000-000000000010")
        let normal=ProjectOrchestrator.decide running (Allocate(firstId,work,false)) |> Result.defaultWith failwith
        let occupied=List.fold ProjectOrchestrator.evolve running normal
        Assert.True(SchedulerReservation.recoveryCapacityPreserved occupied)
        let secondId=Id.reservation(guid "30000000-0000-0000-0000-000000000011")
        Assert.Equal(Error "capacity-reserved-for-recovery",ProjectOrchestrator.decide occupied (Allocate(secondId,work,false)))
        Assert.True(ProjectOrchestrator.decide occupied (Allocate(secondId,work,true)) |> Result.isOk)

    [<Fact>]
    let ``session rejects duplicates and gaps without advancing cursor`` () =
        let session={SessionId=Id.session(guid "60000000-0000-0000-0000-000000000010");RunnerId=Id.runner(guid "40000000-0000-0000-0000-000000000010");Generation=Id.generation 1L;LastClientSequence=4L;LastServerSequence=2L;Closed=false}
        Assert.Equal(Error "duplicate-client-message",Session.accept session (ClientMessage 4L))
        Assert.Equal(Error "client-sequence-gap",Session.accept session (ClientMessage 6L))
        Assert.Equal(5L,(Session.accept session (ClientMessage 5L) |> Result.defaultWith failwith).LastClientSequence)

    [<Fact>]
    let ``runner session cursors survive replay and stop while paused`` () =
        let active,attemptId,_,_ = hostedReady()
        let attempt=active.Attempts[attemptId]
        let opened=active.Sessions[attempt.SessionId]
        Assert.Equal(0L,opened.LastClientSequence)
        let advancedDecision=decide now active (command "29000000-0000-0000-0000-000000000001") "" (AcceptRunnerMessage(attempt.SessionId,1L,true,digest "9"))
        Assert.Equal(Accepted,advancedDecision.Receipt.Disposition)
        let advanced=apply advancedDecision active
        Assert.Equal(1L,advanced.Sessions[attempt.SessionId].LastClientSequence)
        Assert.Equal(1L,advanced.Sessions[attempt.SessionId].LastServerSequence)
        let replayed=List.fold evolve active advancedDecision.Events
        Assert.True(replayed.Sessions.ContainsKey attempt.SessionId)
        Assert.Equal("duplicate-runner-message",(decide now advanced (command "29000000-0000-0000-0000-000000000002") "" (AcceptRunnerMessage(attempt.SessionId,1L,false,digest "8"))).Receipt.Detail)
        Assert.Equal("runner-client-sequence-gap-or-inactive-session",(decide now advanced (command "29000000-0000-0000-0000-000000000003") "" (AcceptRunnerMessage(attempt.SessionId,3L,false,digest "7"))).Receipt.Detail)
        let paused=decide now advanced (command "29000000-0000-0000-0000-000000000004") "" (Pause "operator") |> fun result -> apply result advanced
        Assert.Equal("runner-client-sequence-gap-or-inactive-session",(decide now paused (command "29000000-0000-0000-0000-000000000005") "" (AcceptRunnerMessage(attempt.SessionId,2L,false,digest "6"))).Receipt.Detail)
