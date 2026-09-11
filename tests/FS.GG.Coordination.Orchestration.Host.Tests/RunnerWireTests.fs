module FS.GG.Coordination.Orchestration.Host.Tests.RunnerWireTests

open System
open System.Text
open Xunit
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Orchestration.PostgreSql
open FS.GG.Coordination.Orchestration.Runner.Protocol

let private now=DateTimeOffset.Parse("2026-09-11T05:00:00Z")

[<Fact>]
let ``runner poll is a closed digest-bound message`` () =
    let request:RunnerPollRequest=
        { Schema=RunnerWire.pollSchema;CommandId=Guid.Parse("10000000-0000-0000-0000-000000000001")
          WorkItemPersistenceId="work-item-v1-"+String.replicate 64 "a";SessionId=Guid.Parse("20000000-0000-0000-0000-000000000001")
          RunnerId=Guid.Parse("30000000-0000-0000-0000-000000000001");PrincipalId="main-runner"
          FingerprintSha256=String.replicate 64 "b";Generation=3L;ExpectedRevision=14L;ClientSequence=1L
          IssuedAt=now;ExpiresAt=now.AddMinutes 1. }
    let bytes=RunnerWire.serialize request
    Assert.Equal(Ok request,RunnerWire.parsePoll bytes)
    let changed=Encoding.UTF8.GetString(bytes).TrimEnd('}').Insert(Encoding.UTF8.GetString(bytes).TrimEnd('}').Length,",\"unknown\":true}") |> Encoding.UTF8.GetBytes
    Assert.Equal(Error "runner-message-shape-refused",RunnerWire.parsePoll changed)
    let duplicate=Encoding.UTF8.GetString(bytes).Replace("\"schema\":",$"\"schema\":\"{RunnerWire.pollSchema}\",\"schema\":") |> Encoding.UTF8.GetBytes
    Assert.Equal(Error "runner-message-shape-refused",RunnerWire.parsePoll duplicate)

[<Fact>]
let ``assignment digest changes with authority and ignores only its own digest field`` () =
    let assignment:RunnerAssignment=
        { Schema=RunnerWire.assignmentSchema;WorkItemPersistenceId="work-item-v1-"+String.replicate 64 "a"
          RouteId=Guid.Parse("10000000-0000-0000-0000-000000000001");AttemptId=Guid.Parse("20000000-0000-0000-0000-000000000001")
          CandidateId=Guid.Parse("30000000-0000-0000-0000-000000000001");SessionId=Guid.Parse("40000000-0000-0000-0000-000000000001")
          RunnerId=Guid.Parse("50000000-0000-0000-0000-000000000001");PrincipalId="main-runner";FingerprintSha256=String.replicate 64 "b"
          Generation=3L;WorkflowRevision=14L;ClientSequence=1L;ServerSequence=1L;PayloadSha256=String.replicate 64 "c"
          AssignmentSha256="";ExpiresAt=now.AddMinutes 5.;Deadline=now.AddHours 1. }
    let digest=RunnerWire.assignmentDigest assignment
    Assert.True(RunnerWire.validSha256 digest)
    Assert.Equal(digest,RunnerWire.assignmentDigest {assignment with AssignmentSha256=String.replicate 64 "f"})
    Assert.False(String.Equals(digest,RunnerWire.assignmentDigest {assignment with Generation=4L},StringComparison.Ordinal))

[<Fact>]
let ``event serializer v2 reads retained v1 event bytes`` () =
    let eventValue=PausedEvent "retained-v1"
    let bytes=EventEnvelope.encode eventValue
    Assert.Equal("fsgg.orchestration.core-event-json/1",EventEnvelope.legacySerializerVersion)
    Assert.Equal("fsgg.orchestration.core-event-json/2",EventEnvelope.serializerVersion)
    Assert.Equal(Ok eventValue,EventEnvelope.tryDecode bytes)
