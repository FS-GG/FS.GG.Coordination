namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Text
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type CodexAppServerContinuityTests() =
    let scope: DirectSessionTurnScope =
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
    let transport = "authenticated-local-app-server"
    let binding =
        {
            Scope = scope
            TurnId = "native-turn"
            TransportIdentity = transport
            ConnectionId = "connection-1"
            SubscriptionDigest = String.replicate 64 "d"
            ProtocolVersion = "codex-app-server-v2/0.156.1"
        }
    let fixture name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", name)
        |> File.ReadAllBytes
    let started = fixture "turn-started.json"
    let usage = fixture "usage-updated.json"
    let completed = fixture "turn-completed.json"

    let authenticator result =
        { new ICodexAppServerSubscriptionAuthenticator with
            member _.ReadBoundSubscription() = result }
    let beginBound () =
        match CodexAppServerContinuity.beginWindow scope "native-turn" transport (authenticator (Ok binding)) with
        | Ok state -> state
        | Error code -> failwithf "unexpected subscription refusal %s" code
    let frame ordinal bytes =
        { TransportIdentity = transport
          ConnectionId = binding.ConnectionId
          Ordinal = ordinal
          Payload = bytes }
    let status = CodexAppServerContinuity.status
    let replace (bytes: byte array) oldText newText =
        let original = Encoding.UTF8.GetString bytes
        Encoding.UTF8.GetBytes(original.Replace(oldText, newText, StringComparison.Ordinal))
    let completedWithItems items =
        replace completed "\"items\":[]" ("\"items\":" + items)
    let completedWithCommandActions actions =
        completedWithItems
            ("[{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"commandActions\":"
             + actions + ",\"cwd\":\"/tmp\",\"status\":\"completed\"}]")
    let completedWithCommandMetadata fields =
        completedWithItems
            ("[{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"commandActions\":[],\"cwd\":\"/tmp\",\"status\":\"completed\""
             + fields + "}]")

    [<Fact>]
    member _.``exact subscribed start usage terminal order retains only continuity metadata``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        Assert.Equal(InTurn 0, status first)
        let second = CodexAppServerContinuity.apply first (frame 2L usage)
        Assert.Equal(InTurn 1, status second)
        let third = CodexAppServerContinuity.apply second (frame 3L completed)
        Assert.Equal(TerminalObserved("completed", 1), status third)

    [<Fact>]
    member _.``subscription refuses wrong workspace item turn and transport``() =
        for wrong in
            [ { binding with Scope = { scope with WorkspaceId = "other-workspace" } }
              { binding with Scope = { scope with ItemId = "other-item" } }
              { binding with TurnId = "other-turn" }
              { binding with TransportIdentity = "untrusted-proxy" } ] do
            Assert.Equal(
                Error "app-server-subscription-binding-mismatch",
                CodexAppServerContinuity.beginWindow scope "native-turn" transport (authenticator (Ok wrong))
            )
        Assert.Equal(
            Error "app-server-subscription-unavailable",
            CodexAppServerContinuity.beginWindow scope "native-turn" transport
                (authenticator (Error "no-authenticated-subscription"))
        )
        for invalid in
            [ { binding with SubscriptionDigest = "not-a-digest" }
              { binding with ProtocolVersion = "codex-app-server-v1" }
              { binding with ConnectionId = "" } ] do
            Assert.Equal(
                Error "app-server-subscription-binding-invalid",
                CodexAppServerContinuity.beginWindow scope "native-turn" transport
                    (authenticator (Ok invalid))
            )

    [<Fact>]
    member _.``equal malformed expected and bound scopes refuse before source read``() =
        let malformedScopes =
            [ { scope with Repository = "FS-GG"; IssueRef = "FS-GG#123" }
              { scope with IssueRef = "FS-GG/other#123" }
              { scope with BindingDigest = "not-a-digest" }
              { scope with WorkspaceId = " " } ]
        for malformed in malformedScopes do
            let mutable reads = 0
            let source =
                { new ICodexAppServerSubscriptionAuthenticator with
                    member _.ReadBoundSubscription() =
                        reads <- reads + 1
                        Ok { binding with Scope = malformed } }
            Assert.Equal(
                Error "app-server-subscription-input-invalid",
                CodexAppServerContinuity.beginWindow malformed "native-turn" transport source
            )
            Assert.Equal(0, reads)

    [<Fact>]
    member _.``missing start and duplicate start are permanent gaps``() =
        let missing = CodexAppServerContinuity.apply (beginBound ()) (frame 1L usage)
        Assert.Equal(ContinuityGap "app-server-continuity-start-missing", status missing)
        let later = CodexAppServerContinuity.apply missing (frame 1L started)
        Assert.Equal(status missing, status later)
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let duplicate = CodexAppServerContinuity.apply first (frame 2L started)
        Assert.Equal(ContinuityGap "app-server-continuity-start-duplicate", status duplicate)

    [<Fact>]
    member _.``sequence loss and source substitution latch gaps``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let skipped = CodexAppServerContinuity.apply first (frame 3L usage)
        Assert.Equal(ContinuityGap "app-server-continuity-sequence-gap", status skipped)
        let replayed = CodexAppServerContinuity.apply first (frame 1L usage)
        Assert.Equal(ContinuityGap "app-server-continuity-sequence-gap", status replayed)
        let foreign = { frame 2L usage with ConnectionId = "borrowed-connection" }
        let substituted = CodexAppServerContinuity.apply first foreign
        Assert.Equal(ContinuityGap "app-server-continuity-source-mismatch", status substituted)

    [<Fact>]
    member _.``native thread turn and malformed terminal cannot be substituted``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let badThread = replace usage "native-thread" "other-thread"
        Assert.Equal(
            ContinuityGap "app-server-usage-identity-mismatch",
            status (CodexAppServerContinuity.apply first (frame 2L badThread))
        )
        let badTurn = replace completed "native-turn" "other-turn"
        Assert.Equal(
            ContinuityGap "app-server-turn-identity-mismatch",
            status (CodexAppServerContinuity.apply first (frame 2L badTurn))
        )
        let badStatus = replace completed "\"status\":\"completed\"" "\"status\":\"inProgress\""
        Assert.Equal(
            ContinuityGap "app-server-turn-status-invalid",
            status (CodexAppServerContinuity.apply first (frame 2L badStatus))
        )
        let duplicateId = replace completed "\"id\":\"native-turn\"" "\"id\":\"native-turn\",\"id\":\"native-turn\""
        Assert.Equal(
            ContinuityGap "app-server-turn-shape-invalid",
            status (CodexAppServerContinuity.apply first (frame 2L duplicateId))
        )
        let malformed = Encoding.UTF8.GetBytes("{\"method\":\"turn/completed\",")
        Assert.Equal(
            ContinuityGap "app-server-continuity-json-invalid",
            status (CodexAppServerContinuity.apply first (frame 2L malformed))
        )

    [<Fact>]
    member _.``nonobject turn item cannot mark a native terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for items in [ "[null]"; "[42]"; "[\"text\"]" ] do
            let terminal = completedWithItems items
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``missing or foreign turn item discriminator cannot mark a terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for items in
            [ "[{}]"
              "[{\"id\":\"item-1\",\"type\":\"foreignItem\"}]"
              "[{\"id\":\"\",\"type\":\"reasoning\"}]" ] do
            let terminal = completedWithItems items
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``duplicate turn item identity key cannot mark a terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let terminal =
            completedWithItems "[{\"id\":\"item-1\",\"id\":\"item-1\",\"type\":\"reasoning\"}]"
        Assert.Equal(
            ContinuityGap "app-server-turn-item-invalid",
            status (CodexAppServerContinuity.apply first (frame 2L terminal))
        )

    [<Fact>]
    member _.``supported common turn item identity retains terminal status``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let terminal = completedWithItems "[{\"id\":\"item-1\",\"type\":\"reasoning\"}]"
        Assert.Equal(
            TerminalObserved("completed", 0),
            status (CodexAppServerContinuity.apply first (frame 2L terminal))
        )

    [<Fact>]
    member _.``agent message item without string text cannot mark a native terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for item in
            [ "{\"id\":\"item-1\",\"type\":\"agentMessage\"}"
              "{\"id\":\"item-1\",\"type\":\"agentMessage\",\"text\":null}"
              "{\"id\":\"item-1\",\"type\":\"agentMessage\",\"text\":42}" ] do
            let terminal = completedWithItems ("[" + item + "]")
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``agent message item with schema string text retains terminal status``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let terminal =
            completedWithItems "[{\"id\":\"item-1\",\"type\":\"agentMessage\",\"text\":\"\"}]"
        Assert.Equal(
            TerminalObserved("completed", 0),
            status (CodexAppServerContinuity.apply first (frame 2L terminal))
        )

    [<Fact>]
    member _.``command item missing or malformed action collection cannot mark terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for item in
            [ "{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"cwd\":\"/tmp\",\"status\":\"completed\"}"
              "{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"commandActions\":null,\"cwd\":\"/tmp\",\"status\":\"completed\"}" ] do
            let terminal = completedWithItems ("[" + item + "]")
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``command item wrong primitive fields cannot mark terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for item in
            [ "{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":7,\"commandActions\":[],\"cwd\":\"/tmp\",\"status\":\"completed\"}"
              "{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"commandActions\":[],\"cwd\":null,\"status\":\"completed\"}" ] do
            let terminal = completedWithItems ("[" + item + "]")
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``command item foreign status cannot mark terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let terminal =
            completedWithItems "[{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"commandActions\":[],\"cwd\":\"/tmp\",\"status\":\"foreign\"}]"
        Assert.Equal(
            ContinuityGap "app-server-turn-item-invalid",
            status (CodexAppServerContinuity.apply first (frame 2L terminal))
        )

    [<Fact>]
    member _.``command item with required payload retains terminal status``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let terminal =
            completedWithItems "[{\"id\":\"item-1\",\"type\":\"commandExecution\",\"command\":\"echo ok\",\"commandActions\":[],\"cwd\":\"/tmp\",\"status\":\"completed\"}]"
        Assert.Equal(
            TerminalObserved("completed", 0),
            status (CodexAppServerContinuity.apply first (frame 2L terminal))
        )

    [<Fact>]
    member _.``nonobject or foreign nested command action cannot mark terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for actions in
            [ "[null]"
              "[{}]"
              "[{\"command\":\"echo ok\",\"type\":\"foreign\"}]" ] do
            let terminal = completedWithCommandActions actions
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``read command action requires string name and path``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for actions in
            [ "[{\"command\":\"cat x\",\"type\":\"read\",\"name\":\"x\"}]"
              "[{\"command\":\"cat x\",\"type\":\"read\",\"name\":42,\"path\":\"/tmp/x\"}]" ] do
            let terminal = completedWithCommandActions actions
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``search command action optional path and query have bounded shape``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for actions in
            [ "[{\"command\":\"rg x\",\"type\":\"search\",\"path\":42}]"
              "[{\"command\":\"rg x\",\"type\":\"search\",\"query\":42}]" ] do
            let terminal = completedWithCommandActions actions
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L terminal))
            )

    [<Fact>]
    member _.``supported nested command actions retain terminal status``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let actions =
            "[{\"command\":\"cat x\",\"type\":\"read\",\"name\":\"x\",\"path\":\"/tmp/x\"},"
            + "{\"command\":\"ls\",\"type\":\"listFiles\",\"path\":null},"
            + "{\"command\":\"rg x\",\"type\":\"search\",\"query\":null},"
            + "{\"command\":\"echo ok\",\"type\":\"unknown\"}]"
        let terminal = completedWithCommandActions actions
        Assert.Equal(
            TerminalObserved("completed", 0),
            status (CodexAppServerContinuity.apply first (frame 2L terminal))
        )

    [<Fact>]
    member _.``command item optional numeric metadata must be signed schema integers``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for fields in
            [ ",\"exitCode\":\"0\""
              ",\"exitCode\":1.5"
              ",\"exitCode\":2147483648"
              ",\"durationMs\":\"1\""
              ",\"durationMs\":9223372036854775808" ] do
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L (completedWithCommandMetadata fields)))
            )

    [<Fact>]
    member _.``command item optional nullable strings refuse foreign value shapes``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for fields in
            [ ",\"aggregatedOutput\":{}"
              ",\"pluginId\":false"
              ",\"processId\":[]"
              ",\"scriptPath\":17" ] do
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L (completedWithCommandMetadata fields)))
            )

    [<Fact>]
    member _.``command item source must be a known schema enum``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for fields in [ ",\"source\":\"foreign\""; ",\"source\":null"; ",\"source\":1" ] do
            Assert.Equal(
                ContinuityGap "app-server-turn-item-invalid",
                status (CodexAppServerContinuity.apply first (frame 2L (completedWithCommandMetadata fields)))
            )

    [<Fact>]
    member _.``command item schema optional nulls integer bounds and source retain terminal``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        for fields in
            [ ",\"aggregatedOutput\":null,\"pluginId\":null,\"processId\":null,\"scriptPath\":null,\"durationMs\":null,\"exitCode\":null"
              ",\"exitCode\":2147483647,\"durationMs\":9223372036854775807,\"source\":\"unifiedExecInteraction\""
              ",\"exitCode\":-2147483648,\"durationMs\":-9223372036854775808,\"source\":\"agent\"" ] do
            Assert.Equal(
                TerminalObserved("completed", 0),
                status (CodexAppServerContinuity.apply first (frame 2L (completedWithCommandMetadata fields)))
            )

    [<Fact>]
    member _.``cumulative usage regression and duplicate wire bytes refuse``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let second = CodexAppServerContinuity.apply first (frame 2L usage)
        let duplicate = CodexAppServerContinuity.apply second (frame 3L usage)
        Assert.Equal(ContinuityGap "app-server-continuity-usage-duplicate", status duplicate)
        let original = Encoding.UTF8.GetString usage
        let lessInput = original.Replace("\"inputTokens\":100", "\"inputTokens\":90", StringComparison.Ordinal)
        let regressed =
            lessInput.Replace("\"totalTokens\":140", "\"totalTokens\":130", StringComparison.Ordinal)
            |> Encoding.UTF8.GetBytes
        Assert.Equal(
            ContinuityGap "app-server-continuity-usage-regressed",
            status (CodexAppServerContinuity.apply second (frame 3L regressed))
        )

    [<Fact>]
    member _.``terminal without usage stays explicit and disconnect before terminal is a gap``() =
        let first = CodexAppServerContinuity.apply (beginBound ()) (frame 1L started)
        let noUsage = CodexAppServerContinuity.apply first (frame 2L completed)
        Assert.Equal(TerminalObserved("completed", 0), status noUsage)
        let afterTerminal = CodexAppServerContinuity.apply noUsage (frame 3L usage)
        Assert.Equal(ContinuityGap "app-server-continuity-after-terminal", status afterTerminal)
        let lost = CodexAppServerContinuity.disconnected first
        Assert.Equal(ContinuityGap "app-server-continuity-disconnected", status lost)
