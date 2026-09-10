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
        {CommandId=commandId;ExpectedRevision=state.Revision;ExpectedGeneration=state.Generation
         PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=command}
let admit () = decide now initial (command "20000000-0000-0000-0000-000000000001") (digest "1") (Admit(snapshot ["board-a"],budget)) |> fun d -> apply d initial

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
        let envelope={CommandId=cid;ExpectedRevision=initial.Revision;ExpectedGeneration=initial.Generation;PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=Admit(snapshot [],budget)}
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
        let envelope={CommandId=cid;ExpectedRevision=initial.Revision;ExpectedGeneration=initial.Generation;PrincipalId="test-principal";SessionId=None;IssuedAt=now;ExpiresAt=now.AddMinutes 1.;Command=body}
        let accepted=FS.GG.Coordination.Core.Orchestration.decide now initial envelope
        let state=apply accepted initial
        let stored=(Map.find cid state.CommandReceipts).BodySha256
        Assert.Equal(stored,stored.ToLowerInvariant())
        Assert.Equal(Duplicate,(FS.GG.Coordination.Core.Orchestration.decide now state envelope).Receipt.Disposition)

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
        let candidate={CandidateId=Id.candidate(guid "80000000-0000-0000-0000-000000000001");BaselineSha=String.replicate 40 "a";HeadSha=String.replicate 40 "b";TreeSha=String.replicate 40 "c";ManifestSha256=digest "c";ContentSha256=contentDigest;MediaType="application/vnd.git.bundle";SizeBytes=10L;RetainUntil=now.AddDays 1.;Location=ContentAddressedObject $"sha256/{contentDigest}"}
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
