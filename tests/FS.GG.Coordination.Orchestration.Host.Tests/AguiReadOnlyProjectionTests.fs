namespace FS.GG.Coordination.Orchestration.Host.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open AGUI.Abstractions
open AGUI.Formatting
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence
open FS.GG.Coordination.Orchestration.Host
open FS.GG.Coordination.Orchestration.PostgreSql
open Xunit

type AguiMemoryStore(recovery: Result<RecoveryResult, ReadinessFailure list>) =
    interface IJournalStore with
        member _.CheckReadiness _ = Task.FromResult(Ok())
        member _.Recover(_, _) = Task.FromResult recovery

        member _.Append(_, _) =
            Task.FromResult(AppendOutcome.InvalidAppend "read-only-fixture")

        member _.SaveSnapshot(_, _) = Task.FromResult(Error ReadOnlyStore)
        member _.SaveProjectionCheckpoint(_, _) = Task.FromResult(Error ReadOnlyStore)

type AguiReadOnlyProjectionTests() =
    let observedAt = DateTimeOffset(2026, 9, 30, 7, 0, 0, TimeSpan.Zero)

    let workItem = WorkItemIdentity.create "R_repo" 101L "I_issue" 42L

    let persistenceId = WorkItemIdentity.persistenceId workItem

    let admitted =
        WorkAdmitted(
            {
                ProjectId = Id.project (Guid.Parse "10000000-0000-0000-0000-000000000001")
                WorkItemId = workItem
                WorkflowRevision = Id.revision 0L
                CanonicalSha256 = String.replicate 64 "a"
                BoardMembershipIds = [ "PVTI_item" ]
                CapturedAt = observedAt
            },
            {
                TokenLimit = 100L
                RuntimeSecondsLimit = 60L
                CostMicrosLimit = 0L
                Deadline = observedAt.AddHours 1.0
            }
        )

    let stored sequence eventValue =
        let payload = EventEnvelope.encode eventValue

        {
            PersistenceId = persistenceId
            Sequence = sequence
            EventId = Guid.Parse("00000000-0000-0000-0000-" + sequence.ToString("000000000000"))
            SchemaVersion = 1
            SerializerVersion = EventEnvelope.serializerVersion
            Payload = payload
            PayloadSha256 = SHA256.HashData(payload) |> Convert.ToHexString |> _.ToLowerInvariant()
            EffectChange = NoEffect
            RecordedAt = observedAt.AddSeconds(float sequence)
        }

    let completeEvents =
        [
            stored 1L admitted
            stored 2L (GenerationAdvanced(Id.generation 3L))
            stored 3L (PausedEvent "read-only")
        ]

    let recovery events =
        Ok
            {
                Events = events
                Snapshot = None
                UnsettledEffects = []
                RequiresExternalReconciliation = false
            }

    let request generation afterSequence =
        {
            WorkItemId = workItem
            ExpectedGeneration = Id.generation generation
            AfterSequence = afterSequence
        }

    let project events generation afterSequence =
        AguiReadOnlyProjection.project
            (AguiMemoryStore(recovery events) :> IJournalStore)
            (request generation afterSequence)
            CancellationToken.None
        |> _.Result

    let success =
        function
        | Ok value -> value
        | Error failure -> failwithf "unexpected projection failure: %A" failure

    [<Fact>]
    member _.``official AG UI protocol and SDK identities are pinned``() =
        Assert.Equal(AguiReadOnlyProjection.testedProtocolVersion, AGUIProtocol.Version)
        Assert.Equal(Version(1, 0, 0, 0), typeof<BaseEvent>.Assembly.GetName().Version)
        Assert.Equal(Version(1, 0, 0, 0), typeof<SseEventStreamFormatter>.Assembly.GetName().Version)
        Assert.Equal("1.0.0", AguiReadOnlyProjection.testedSdkVersion)
        Assert.Equal("f08ccf853497914ef70a5f0197af45bb74915bc2", AguiReadOnlyProjection.testedSdkCommit)

    [<Fact>]
    member _.``durable work item replay emits AG UI 1 SSE to a Python client``() =
        let projection = project completeEvents 3L None |> success
        Assert.Equal("text/event-stream", projection.MediaType)
        Assert.False(projection.Partial)

        let start = ProcessStartInfo("python3")
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Agui", "python-sse-client.py"))
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false

        use client = Process.Start start
        client.StandardInput.BaseStream.Write(projection.SseBytes)
        client.StandardInput.Close()
        let output = client.StandardOutput.ReadToEnd()
        let error = client.StandardError.ReadToEnd()
        client.WaitForExit()
        Assert.True(client.ExitCode = 0, error)
        Assert.Equal("{\"eventCount\":3,\"sequence\":\"3\"}", output.Trim())

    [<Fact>]
    member _.``exact duplicate replay is idempotent and reconnect returns verified snapshot``() =
        let events =
            [ completeEvents[0]; completeEvents[1]; completeEvents[1]; completeEvents[2] ]

        let projection = project events 3L (Some 2L) |> success
        let text = Encoding.UTF8.GetString projection.SseBytes
        Assert.Equal(1, projection.DuplicateCount)
        Assert.Equal(3L, projection.LastSequence)
        Assert.Contains("\"duplicateCount\":1", text)
        Assert.Contains("\"reconnectAfterSequence\":\"2\"", text)
        Assert.Contains("\"partial\":false", text)

    [<Fact>]
    member _.``journal gap emits truthful partial state without delivery claim``() =
        let projection = project [ completeEvents[0]; completeEvents[2] ] 3L None |> success
        let text = Encoding.UTF8.GetString projection.SseBytes
        Assert.True(projection.Partial)
        Assert.Equal(Some(2L, 2L), projection.Gap)
        Assert.Contains("\"partialReason\":\"journal-sequence-gap\"", text)
        Assert.Contains("\"firstMissingSequence\":\"2\"", text)
        Assert.Contains("\"deliveryVerified\":false", text)
        Assert.DoesNotContain("\"workflowRevision\"", text)

    [<Fact>]
    member _.``stale generation and invalid reconnect cursor refuse before SSE``() =
        match project completeEvents 2L None with
        | Error(StaleGeneration(2L, 3L)) -> ()
        | other -> failwithf "expected stale generation refusal, got %A" other

        match project completeEvents 3L (Some 4L) with
        | Error(InvalidReconnectCursor(4L, 3L)) -> ()
        | other -> failwithf "expected cursor refusal, got %A" other

    [<Fact>]
    member _.``observer loss and conflicting duplicate expose no projection``() =
        let unavailable =
            AguiReadOnlyProjection.project
                (AguiMemoryStore(Error [ StoreUnavailable "fixture-offline" ]) :> IJournalStore)
                (request 3L None)
                CancellationToken.None
            |> _.Result

        match unavailable with
        | Error(ObserverUnavailable [ StoreUnavailable "fixture-offline" ]) -> ()
        | other -> failwithf "expected observer loss, got %A" other

        let conflict =
            { completeEvents[1] with
                Payload = EventEnvelope.encode (GenerationAdvanced(Id.generation 4L))
            }

        match project [ completeEvents[0]; completeEvents[1]; conflict ] 3L None with
        | Error(ConflictingDuplicate 2L) -> ()
        | other -> failwithf "expected duplicate conflict, got %A" other
