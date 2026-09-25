namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type CodexAppServerUsageProjectionTests() =
    let fixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", "usage-updated.json")
        |> File.ReadAllBytes

    let parse bytes = CodexAppServerUsageProjection.parse "native-thread" "native-turn" bytes
    let edit oldText newText =
        let original = Encoding.UTF8.GetString fixture
        Encoding.UTF8.GetBytes(original.Replace(oldText, newText, StringComparison.Ordinal))

    let refused (code: string) result =
        match result with
        | Error actual -> Assert.Equal(code, actual)
        | Ok _ -> failwithf "wanted refusal %s" code

    [<Fact>]
    member _.``pinned app-server usage notification preserves exact IDs and raw snapshots``() =
        match parse fixture with
        | Error code -> failwithf "unexpected refusal %s" code
        | Ok update ->
            Assert.Equal("native-thread", update.ThreadId)
            Assert.Equal("native-turn", update.TurnId)
            Assert.Equal(17L, update.Last.Input)
            Assert.Equal(4L, update.Last.CachedInput)
            Assert.Equal(9L, update.Last.Output)
            Assert.Equal(3L, update.Last.ReasoningOutput)
            Assert.Equal(26L, update.Last.Total)
            Assert.Equal(100L, update.Cumulative.Input)
            Assert.Equal(140L, update.Cumulative.Total)
            Assert.Equal(0L, update.Last.CacheWriteInput)
            let digest = SHA256.HashData fixture |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
            Assert.Equal(digest, update.WireSha256)

    [<Fact>]
    member _.``caller expected thread and turn must match independently``() =
        refused "app-server-usage-identity-mismatch"
            (CodexAppServerUsageProjection.parse "borrowed-thread" "native-turn" fixture)
        refused "app-server-usage-identity-mismatch"
            (CodexAppServerUsageProjection.parse "native-thread" "borrowed-turn" fixture)
        refused "app-server-expected-identity-invalid"
            (CodexAppServerUsageProjection.parse " " "native-turn" fixture)
        refused "app-server-usage-identity-mismatch"
            (parse (edit "native-turn" "other-turn"))

    [<Fact>]
    member _.``duplicate and missing mapping keys refuse``() =
        refused "app-server-usage-params-invalid"
            (parse (edit "\"threadId\":\"native-thread\""
                        "\"threadId\":\"native-thread\",\"threadId\":\"native-thread\""))
        refused "app-server-usage-counters-invalid"
            (parse (edit "\"inputTokens\":17," ""))
        refused "app-server-usage-counters-invalid"
            (parse (edit "\"inputTokens\":17"
                        "\"inputTokens\":17,\"inputTokens\":17"))
        refused "app-server-usage-frame-invalid"
            (parse (edit "\"method\":\"thread/tokenUsage/updated\""
                        "\"method\":\"thread/tokenUsage/updated\",\"items\":[]"))

    [<Fact>]
    member _.``invalid and regressed counter snapshots refuse``() =
        refused "app-server-usage-counters-invalid"
            (parse (edit "\"cachedInputTokens\":4" "\"cachedInputTokens\":18"))
        refused "app-server-usage-counters-invalid"
            (parse (edit "\"reasoningOutputTokens\":3" "\"reasoningOutputTokens\":10"))
        refused "app-server-usage-counters-invalid"
            (parse (edit "\"totalTokens\":26" "\"totalTokens\":25"))
        refused "app-server-usage-counters-invalid"
            (parse (edit "\"inputTokens\":17" "\"inputTokens\":-1"))
        let original = Encoding.UTF8.GetString fixture
        let lessInput = original.Replace("\"inputTokens\":100", "\"inputTokens\":10", StringComparison.Ordinal)
        let lessCached =
            lessInput.Replace("\"cachedInputTokens\":20", "\"cachedInputTokens\":2", StringComparison.Ordinal)
        let regressed =
            lessCached.Replace("\"totalTokens\":140", "\"totalTokens\":50", StringComparison.Ordinal)
            |> Encoding.UTF8.GetBytes
        refused "app-server-usage-total-regressed"
            (parse regressed)
        refused "app-server-usage-shape-invalid"
            (parse (edit "\"modelContextWindow\":128000" "\"modelContextWindow\":\"unknown\""))

    [<Fact>]
    member _.``other methods and transcript-shaped frames never become usage``() =
        refused "app-server-usage-method-unsupported"
            (parse (edit "thread/tokenUsage/updated" "turn/completed"))
        refused "app-server-usage-params-invalid"
            (parse (edit "\"turnId\":\"native-turn\""
                        "\"turnId\":\"native-turn\",\"items\":[{\"text\":\"private\"}]"))
        refused "app-server-usage-json-invalid"
            (parse (Encoding.UTF8.GetBytes "{"))
        refused "app-server-usage-frame-size-invalid"
            (parse (Array.zeroCreate<byte> 32769))
