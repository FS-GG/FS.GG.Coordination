module FS.GG.Coordination.Orchestration.Host.Tests.RunnerWireTests

open System
open System.Text
open System.Text.Json
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
    use document=JsonDocument.Parse bytes
    let reordered=
        document.RootElement.EnumerateObject() |> Seq.rev
        |> Seq.map(fun property -> $"{JsonSerializer.Serialize property.Name}:{property.Value.GetRawText()}")
        |> String.concat "," |> fun properties -> Encoding.UTF8.GetBytes($"{{{properties}}}")
    let semantic=RunnerWire.parsePoll reordered |> Result.defaultWith failwith
    Assert.True(bytes.AsSpan().SequenceEqual(RunnerWire.serialize semantic))

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
    let closed={assignment with AssignmentSha256=digest}
    Assert.Equal(Ok closed,RunnerWire.parseAssignment(RunnerWire.serialize closed))
    let extra=Encoding.UTF8.GetString(RunnerWire.serialize closed).Replace("}",",\"extra\":true}") |> Encoding.UTF8.GetBytes
    Assert.Equal(Error "runner-message-shape-refused",RunnerWire.parseAssignment extra)

[<Fact>]
let ``successful runner receipts are closed and bounded`` () =
    let ack:RunnerAckReceipt={Schema=RunnerWire.ackReceiptSchema;Accepted=true;WorkflowRevision=14L}
    let candidate:RunnerCandidateReceipt={Schema=RunnerWire.candidateReceiptSchema;Accepted=true;WorkflowRevision=17L}
    Assert.Equal(Ok ack,RunnerWire.parseAckReceipt(RunnerWire.serialize ack))
    Assert.Equal(Ok candidate,RunnerWire.parseCandidateReceipt(RunnerWire.serialize candidate))
    Assert.True(RunnerWire.parseAckReceipt(Array.zeroCreate 8193) |> Result.isError)
    let malformed=Encoding.UTF8.GetBytes($"{{\"schema\":\"{RunnerWire.ackReceiptSchema}\",\"accepted\":true,\"workflowRevision\":14,\"extra\":false}}")
    Assert.Equal(Error "runner-message-shape-refused",RunnerWire.parseAckReceipt malformed)

[<Fact>]
let ``event serializer v2 reads retained v1 event bytes`` () =
    let eventValue=PausedEvent "retained-v1"
    let bytes=EventEnvelope.encode eventValue
    Assert.Equal("fsgg.orchestration.core-event-json/1",EventEnvelope.legacySerializerVersion)
    Assert.Equal("fsgg.orchestration.core-event-json/2",EventEnvelope.serializerVersion)
    Assert.Equal(Ok eventValue,EventEnvelope.tryDecode bytes)
    let receipt={CommandId=Id.command(Guid.Parse("90000000-0000-0000-0000-000000000001"));BodySha256=String.replicate 64 "a";Disposition=Accepted;Revision=Id.revision 1L;ProtocolVersion=Id.protocolVersion 1 0;Detail="retained"}
    let before=evolve initial eventValue
    Assert.Equal(before.Revision,(evolve before (CommandRecorded receipt)).Revision)
