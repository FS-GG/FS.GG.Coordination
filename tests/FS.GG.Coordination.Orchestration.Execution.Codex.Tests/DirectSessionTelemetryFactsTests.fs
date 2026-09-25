namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type DirectSessionTelemetryFactsTests() =
    let assignment: DirectSessionTurnScope =
        {
            WorkspaceId = "main-fsharp-dev"
            Repository = "FS-GG/.github"
            ItemId = "work-item-v1-" + String.replicate 64 "b"
            IssueRef = "FS-GG/.github#123"
            AttemptId = "attempt-1"
            InvocationId = "invocation-" + String.replicate 64 "a"
            ThreadId = "native-thread"
            SourceIdentity = "coordination"
            ProducerId = "fsharp-dev-main"
            BindingDigest = String.replicate 64 "c"
        }

    let usage: CodexTurnUsage =
        {
            ThreadId = "native-thread"
            TurnId = Some "native-turn-1"
            TurnSequence = 1L
            Provider = Some "openai"
            ObservedModel = Some "gpt-6-sol"
            ObservedEffort = Some "high"
            Backend = None
            Input = 17L
            CachedInput = 4L
            Output = 9L
            Reasoning = Some 3L
            Total = 26L
        }

    let observed: DirectSessionCompletedTurn =
        { SourceBinding = assignment
          Usage = usage
          CounterProvenance = "native-current-session-fixture" }

    let refuse (code: string) result =
        match result with
        | Error actual -> Assert.Equal(code, actual)
        | Ok _ -> failwithf "wanted refusal %s" code

    let accepted result =
        match result with
        | Ok value -> value
        | Error code -> failwithf "unexpected refusal %s" code

    [<Fact>]
    member _.``native turn maps to one scoped existing ingest envelope``() =
        let prepared =
            DirectSessionTelemetryFacts.prepareCompletedTurn assignment observed |> accepted

        Assert.Equal(assignment.WorkspaceId, prepared.WorkspaceId)
        Assert.Equal(assignment.Repository, prepared.Repository)
        Assert.Equal(assignment.ItemId, prepared.ItemId)
        Assert.Equal(assignment.IssueRef, prepared.IssueRef)
        Assert.Equal(assignment.AttemptId, prepared.AttemptId)
        Assert.Equal(assignment.InvocationId, prepared.InvocationId)
        Assert.Equal(assignment.ThreadId, prepared.ThreadId)
        Assert.Equal(usage.TurnId.Value, prepared.TurnId)
        Assert.Equal(usage.TurnSequence, prepared.TurnSequence)
        Assert.Equal(assignment.SourceIdentity, prepared.SourceIdentity)
        Assert.Equal(assignment.ProducerId, prepared.ProducerId)
        Assert.Equal(assignment.BindingDigest, prepared.BindingDigest)
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData prepared.Payload).ToLowerInvariant(),
            prepared.PayloadSha256
        )

        use document = JsonDocument.Parse prepared.Payload
        let root = document.RootElement
        Assert.Equal("fsgg.telemetry.ingest/1", root.GetProperty("schema").GetString())
        Assert.Equal(prepared.IngestId, root.GetProperty("ingestId").GetString())
        Assert.Equal(assignment.SourceIdentity, root.GetProperty("sourceIdentity").GetString())
        Assert.Equal(assignment.InvocationId, root.GetProperty("generation").GetString())
        Assert.Equal(1, root.GetProperty("eventCount").GetInt32())
        let event = root.GetProperty("events")[0]
        Assert.Equal("runtime-turn-usage", event.GetProperty("kind").GetString())
        Assert.Equal(prepared.EventIdentity, event.GetProperty("identity").GetString())
        Assert.Equal(assignment.ItemId, event.GetProperty("itemId").GetString())
        Assert.Equal(assignment.InvocationId, event.GetProperty("invocationId").GetString())
        Assert.Equal("native-thread", event.GetProperty("threadId").GetString())
        Assert.Equal("native-turn-1", event.GetProperty("turnId").GetString())
        Assert.Equal(1L, event.GetProperty("turnSequence").GetInt64())
        Assert.Equal("completed-turn", event.GetProperty("scope").GetString())
        Assert.Equal(observed.CounterProvenance, event.GetProperty("provenance").GetString())
        Assert.Equal(17L, event.GetProperty("input").GetInt64())
        Assert.Equal(4L, event.GetProperty("cachedInput").GetInt64())
        Assert.Equal(9L, event.GetProperty("output").GetInt64())
        Assert.Equal(3L, event.GetProperty("reasoning").GetInt64())
        Assert.Equal(26L, event.GetProperty("total").GetInt64())
        Assert.False(root.TryGetProperty("workspaceId") |> fst)

        let fixturePath =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "telemetry", "native-turn.json")
        use existing = JsonDocument.Parse(File.ReadAllText fixturePath)
        let names (value: JsonElement) =
            value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        Assert.True(names existing.RootElement = names root)
        Assert.True(names (existing.RootElement.GetProperty("events")[0]) = names event)

    [<Fact>]
    member _.``every exact assignment dimension is bound before mapping``() =
        let variants =
            [ { assignment with WorkspaceId = "other-workspace" }
              { assignment with Repository = "FS-GG/Other"; IssueRef = "FS-GG/Other#123" }
              { assignment with ItemId = "other-item" }
              { assignment with IssueRef = "FS-GG/.github#124" }
              { assignment with AttemptId = "attempt-2" }
              { assignment with InvocationId = "invocation-other" }
              { assignment with SourceIdentity = "other-stream" }
              { assignment with ProducerId = "other-producer" }
              { assignment with BindingDigest = String.replicate 64 "d" } ]

        for other in variants do
            refuse "direct-session-scope-mismatch"
                (DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                    { observed with SourceBinding = other })
        refuse "direct-session-assignment-invalid"
            (DirectSessionTelemetryFacts.prepareCompletedTurn
                { assignment with IssueRef = "FS-GG/Other#123" } observed)

    [<Fact>]
    member _.``native thread and turn identity are required``() =
        refuse "direct-session-thread-mismatch"
            (DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                { observed with Usage = { usage with ThreadId = "other-thread" } })
        refuse "direct-session-native-turn-id-missing"
            (DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                { observed with Usage = { usage with TurnId = None } })
        refuse "direct-session-native-turn-id-missing"
            (DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                { observed with Usage = { usage with TurnId = Some " " } })

    [<Fact>]
    member _.``invalid or unproven counters cannot enter an envelope``() =
        for broken in
            [ { usage with CachedInput = 18L }
              { usage with Reasoning = Some 10L }
              { usage with Total = 25L }
              { usage with Input = -1L }
              { usage with Provider = Some(String.replicate 129 "x") }
              { usage with TurnSequence = 0L } ] do
            refuse "direct-session-usage-invalid"
                (DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                    { observed with Usage = broken })
        refuse "direct-session-provenance-missing"
            (DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                { observed with CounterProvenance = "" })

    [<Fact>]
    member _.``changed counters retain identity but change payload digest``() =
        let first = DirectSessionTelemetryFacts.prepareCompletedTurn assignment observed |> accepted
        let changed =
            DirectSessionTelemetryFacts.prepareCompletedTurn assignment
                { observed with Usage = { usage with Input = 18L; Total = 27L } }
            |> accepted
        Assert.Equal(first.EventIdentity, changed.EventIdentity)
        Assert.Equal(first.IngestId, changed.IngestId)
        Assert.NotEqual<string>(first.PayloadSha256, changed.PayloadSha256)
