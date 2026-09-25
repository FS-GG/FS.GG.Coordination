namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Text.Json
open System.Text.RegularExpressions

/// Supplied by a future independently authenticated, prospectively subscribed transport.
/// This record alone is not proof that an App Server connection exists.
type CodexAppServerSubscriptionBinding =
    {
        Scope: DirectSessionTurnScope
        TurnId: string
        TransportIdentity: string
        ConnectionId: string
        SubscriptionDigest: string
        ProtocolVersion: string
    }

/// The ordinal is a future transport-journal sidecar; App Server notifications have no native cursor.
type CodexAppServerObservedFrame =
    {
        TransportIdentity: string
        ConnectionId: string
        Ordinal: int64
        Payload: byte array
    }

/// The future implementation must authenticate the connection and bind before turn start.
type ICodexAppServerSubscriptionAuthenticator =
    abstract member ReadBoundSubscription: unit -> Result<CodexAppServerSubscriptionBinding, string>

type CodexAppServerContinuityStatus =
    | AwaitingStart
    | InTurn of usageUpdates: int
    | TerminalObserved of status: string * usageUpdates: int
    | ContinuityGap of string

type CodexAppServerContinuityState =
    private
        {
            Binding: CodexAppServerSubscriptionBinding
            NextOrdinal: int64
            Status: CodexAppServerContinuityStatus
            LastCumulative: CodexAppServerTokenCounts option
            UsageHashes: Set<string>
        }

type private CodexAppServerNativeEvent =
    | NativeTurnStarted
    | NativeUsageUpdated of CodexAppServerUsageUpdate
    | NativeTurnTerminal of string

[<RequireQualifiedAccess>]
module CodexAppServerContinuity =
    let private digestPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private supportedItemTypes =
        set [ "userMessage"; "hookPrompt"; "agentMessage"; "functionCallOutput";
              "plan"; "reasoning"; "commandExecution"; "fileChange";
              "mcpToolCall"; "dynamicToolCall"; "collabAgentToolCall";
              "subAgentActivity"; "webSearch"; "imageView"; "sleep";
              "imageGeneration"; "enteredReviewMode"; "exitedReviewMode";
              "contextCompaction" ]

    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let private fields code required allowed (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then Error code
        else
            let names = node.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            let actual = names |> Set.ofList
            if names.Length <> actual.Count
               || not (Set.isSubset required actual)
               || not (Set.isSubset actual allowed) then Error code
            else Ok ()

    let private readText code (node: JsonElement) (name: string) =
        let value = node.GetProperty name
        if value.ValueKind <> JsonValueKind.String then Error code
        else
            let text = value.GetString()
            if boundedText text then Ok text else Error code

    let private optionalNullableString (node: JsonElement) (name: string) =
        match node.TryGetProperty name with
        | false, _ -> true
        | true, value ->
            value.ValueKind = JsonValueKind.String || value.ValueKind = JsonValueKind.Null

    let private optionalNullableInt32 (node: JsonElement) (name: string) =
        match node.TryGetProperty name with
        | false, _ -> true
        | true, value when value.ValueKind = JsonValueKind.Null -> true
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, _ -> true
            | _ -> false
        | _ -> false

    let private optionalNullableInt64 (node: JsonElement) (name: string) =
        match node.TryGetProperty name with
        | false, _ -> true
        | true, value when value.ValueKind = JsonValueKind.Null -> true
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt64() with
            | true, _ -> true
            | _ -> false
        | _ -> false

    let private optionalCommandSource (node: JsonElement) =
        match node.TryGetProperty "source" with
        | false, _ -> true
        | true, value when value.ValueKind = JsonValueKind.String ->
            Set.contains (value.GetString())
                (set [ "agent"; "userShell"; "unifiedExecStartup"; "unifiedExecInteraction" ])
        | _ -> false

    let private validCommandAction (action: JsonElement) =
        if action.ValueKind <> JsonValueKind.Object then false
        else
            let names = action.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match action.TryGetProperty "command", action.TryGetProperty "type" with
                | (true, command), (true, actionType) when
                    command.ValueKind = JsonValueKind.String
                    && actionType.ValueKind = JsonValueKind.String ->
                    match actionType.GetString() with
                    | "read" ->
                        match action.TryGetProperty "name", action.TryGetProperty "path" with
                        | (true, name), (true, path) ->
                            name.ValueKind = JsonValueKind.String
                            && path.ValueKind = JsonValueKind.String
                        | _ -> false
                    | "listFiles" -> optionalNullableString action "path"
                    | "search" ->
                        optionalNullableString action "path"
                        && optionalNullableString action "query"
                    | "unknown" -> true
                    | _ -> false
                | _ -> false

    let private validFileUpdateChange (change: JsonElement) =
        if change.ValueKind <> JsonValueKind.Object then false
        else
            let names = change.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match change.TryGetProperty "diff", change.TryGetProperty "path",
                      change.TryGetProperty "kind" with
                | (true, diff), (true, path), (true, kind) when
                    diff.ValueKind = JsonValueKind.String
                    && path.ValueKind = JsonValueKind.String
                    && kind.ValueKind = JsonValueKind.Object ->
                    let kindNames = kind.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    if kindNames.Length <> (kindNames |> Set.ofList |> Set.count) then false
                    else
                        match kind.TryGetProperty "type" with
                        | true, kindType when kindType.ValueKind = JsonValueKind.String ->
                            match kindType.GetString() with
                            | "add" | "delete" -> true
                            | "update" -> optionalNullableString kind "move_path"
                            | _ -> false
                        | _ -> false
                | _ -> false

    let private validOptionalMcpResult (item: JsonElement) =
        match item.TryGetProperty "result" with
        | false, _ -> true
        | true, result when result.ValueKind = JsonValueKind.Null -> true
        | true, result when result.ValueKind = JsonValueKind.Object ->
            let names = result.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match result.TryGetProperty "content" with
                | true, content -> content.ValueKind = JsonValueKind.Array
                | _ -> false
        | _ -> false

    let private validOptionalMcpError (item: JsonElement) =
        match item.TryGetProperty "error" with
        | false, _ -> true
        | true, error when error.ValueKind = JsonValueKind.Null -> true
        | true, error when error.ValueKind = JsonValueKind.Object ->
            let names = error.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match error.TryGetProperty "message" with
                | true, message -> message.ValueKind = JsonValueKind.String
                | _ -> false
        | _ -> false

    let private validOptionalMcpAppContext (item: JsonElement) =
        match item.TryGetProperty "appContext" with
        | false, _ -> true
        | true, context when context.ValueKind = JsonValueKind.Null -> true
        | true, context when context.ValueKind = JsonValueKind.Object ->
            let names = context.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match context.TryGetProperty "connectorId" with
                | true, connector when connector.ValueKind = JsonValueKind.String ->
                    optionalNullableString context "actionName"
                    && optionalNullableString context "appName"
                    && optionalNullableString context "linkId"
                    && optionalNullableString context "resourceUri"
                | _ -> false
        | _ -> false

    let private validOptionalMcpAppUi (item: JsonElement) =
        match item.TryGetProperty "mcpAppUi" with
        | false, _ -> true
        | true, ui when ui.ValueKind = JsonValueKind.Null -> true
        | true, ui when ui.ValueKind = JsonValueKind.Object ->
            let names = ui.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match ui.TryGetProperty "resourceUri", ui.TryGetProperty "preferredModelDisplayMode" with
                | (true, uri), (true, mode) ->
                    uri.ValueKind = JsonValueKind.String
                    && mode.ValueKind = JsonValueKind.String
                    && Set.contains (mode.GetString()) (set [ "inline"; "fullscreen" ])
                | _ -> false
        | _ -> false

    let private optionalNullableBoolean (node: JsonElement) (name: string) =
        match node.TryGetProperty name with
        | false, _ -> true
        | true, value ->
            value.ValueKind = JsonValueKind.Null
            || value.ValueKind = JsonValueKind.True
            || value.ValueKind = JsonValueKind.False

    let private validDynamicToolContentItem (content: JsonElement) =
        if content.ValueKind <> JsonValueKind.Object then false
        else
            let names = content.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match content.TryGetProperty "type" with
                | true, contentType when contentType.ValueKind = JsonValueKind.String ->
                    let payloadName =
                        match contentType.GetString() with
                        | "inputText" -> Some "text"
                        | "inputImage" -> Some "imageUrl"
                        | "inputAudio" -> Some "audioUrl"
                        | _ -> None
                    match payloadName with
                    | Some name ->
                        match content.TryGetProperty name with
                        | true, payload -> payload.ValueKind = JsonValueKind.String
                        | _ -> false
                    | None -> false
                | _ -> false

    let private validOptionalDynamicContentItems (item: JsonElement) =
        match item.TryGetProperty "contentItems" with
        | false, _ -> true
        | true, content when content.ValueKind = JsonValueKind.Null -> true
        | true, content when content.ValueKind = JsonValueKind.Array ->
            content.EnumerateArray() |> Seq.forall validDynamicToolContentItem
        | _ -> false

    let private validCollabAgentStates (states: JsonElement) =
        if states.ValueKind <> JsonValueKind.Object then false
        else
            let names = states.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                states.EnumerateObject()
                |> Seq.forall (fun entry ->
                    let state = entry.Value
                    if state.ValueKind <> JsonValueKind.Object then false
                    else
                        let stateNames = state.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                        if stateNames.Length <> (stateNames |> Set.ofList |> Set.count) then false
                        else
                            match state.TryGetProperty "status" with
                            | true, agentStatus when agentStatus.ValueKind = JsonValueKind.String ->
                                Set.contains (agentStatus.GetString())
                                    (set [ "pendingInit"; "running"; "interrupted"; "completed";
                                           "errored"; "shutdown"; "notFound" ])
                                && optionalNullableString state "message"
                            | _ -> false)

    let private optionalCollabReasoningEffort (item: JsonElement) =
        match item.TryGetProperty "reasoningEffort" with
        | false, _ -> true
        | true, effort when effort.ValueKind = JsonValueKind.Null -> true
        | true, effort when effort.ValueKind = JsonValueKind.String ->
            effort.GetString().Length > 0
        | _ -> false

    let private validOptionalWebSearchAction (item: JsonElement) =
        match item.TryGetProperty "action" with
        | false, _ -> true
        | true, action when action.ValueKind = JsonValueKind.Null -> true
        | true, action when action.ValueKind = JsonValueKind.Object ->
            let names = action.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match action.TryGetProperty "type" with
                | true, actionType when actionType.ValueKind = JsonValueKind.String ->
                    match actionType.GetString() with
                    | "search" ->
                        let queriesValid =
                            match action.TryGetProperty "queries" with
                            | false, _ -> true
                            | true, queries when queries.ValueKind = JsonValueKind.Null -> true
                            | true, queries when queries.ValueKind = JsonValueKind.Array ->
                                queries.EnumerateArray()
                                |> Seq.forall (fun query -> query.ValueKind = JsonValueKind.String)
                            | _ -> false
                        optionalNullableString action "query" && queriesValid
                    | "openPage" -> optionalNullableString action "url"
                    | "findInPage" ->
                        optionalNullableString action "url"
                        && optionalNullableString action "pattern"
                    | "other" -> true
                    | _ -> false
                | _ -> false
        | _ -> false

    let private validOptionalWebSearchResults (item: JsonElement) =
        match item.TryGetProperty "results" with
        | false, _ -> true
        | true, results ->
            results.ValueKind = JsonValueKind.Null
            || results.ValueKind = JsonValueKind.Array

    let private validTurnItem (item: JsonElement) =
        if item.ValueKind <> JsonValueKind.Object then false
        else
            let names = item.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if names.Length <> (names |> Set.ofList |> Set.count) then false
            else
                match item.TryGetProperty "id", item.TryGetProperty "type" with
                | (true, id), (true, itemType) when
                    id.ValueKind = JsonValueKind.String
                    && itemType.ValueKind = JsonValueKind.String ->
                    let typeName = itemType.GetString()
                    let variantValid =
                        match typeName with
                        | "agentMessage" ->
                            match item.TryGetProperty "text" with
                            | true, text -> text.ValueKind = JsonValueKind.String
                            | _ -> false
                        | "commandExecution" ->
                            match item.TryGetProperty "command", item.TryGetProperty "commandActions",
                                  item.TryGetProperty "cwd", item.TryGetProperty "status" with
                            | (true, command), (true, actions), (true, cwd), (true, status) ->
                                command.ValueKind = JsonValueKind.String
                                && actions.ValueKind = JsonValueKind.Array
                                && (actions.EnumerateArray() |> Seq.forall validCommandAction)
                                && cwd.ValueKind = JsonValueKind.String
                                && status.ValueKind = JsonValueKind.String
                                && Set.contains (status.GetString())
                                    (set [ "inProgress"; "completed"; "failed"; "declined" ])
                                && optionalNullableString item "aggregatedOutput"
                                && optionalNullableString item "pluginId"
                                && optionalNullableString item "processId"
                                && optionalNullableString item "scriptPath"
                                && optionalNullableInt64 item "durationMs"
                                && optionalNullableInt32 item "exitCode"
                                && optionalCommandSource item
                            | _ -> false
                        | "fileChange" ->
                            match item.TryGetProperty "changes", item.TryGetProperty "status" with
                            | (true, changes), (true, status) ->
                                changes.ValueKind = JsonValueKind.Array
                                && (changes.EnumerateArray() |> Seq.forall validFileUpdateChange)
                                && status.ValueKind = JsonValueKind.String
                                && Set.contains (status.GetString())
                                    (set [ "inProgress"; "completed"; "failed"; "declined" ])
                            | _ -> false
                        | "mcpToolCall" ->
                            match item.TryGetProperty "arguments", item.TryGetProperty "server",
                                  item.TryGetProperty "tool", item.TryGetProperty "status" with
                            | (true, _), (true, server), (true, tool), (true, status) ->
                                server.ValueKind = JsonValueKind.String
                                && tool.ValueKind = JsonValueKind.String
                                && status.ValueKind = JsonValueKind.String
                                && Set.contains (status.GetString())
                                    (set [ "inProgress"; "completed"; "failed" ])
                                && validOptionalMcpResult item
                                && validOptionalMcpError item
                                && validOptionalMcpAppContext item
                                && validOptionalMcpAppUi item
                                && optionalNullableBoolean item "readOnlyHint"
                                && optionalNullableInt64 item "durationMs"
                                && optionalNullableString item "mcpAppResourceUri"
                                && optionalNullableString item "pluginId"
                            | _ -> false
                        | "dynamicToolCall" ->
                            match item.TryGetProperty "arguments", item.TryGetProperty "tool",
                                  item.TryGetProperty "status" with
                            | (true, _), (true, tool), (true, status) ->
                                tool.ValueKind = JsonValueKind.String
                                && status.ValueKind = JsonValueKind.String
                                && Set.contains (status.GetString())
                                    (set [ "inProgress"; "completed"; "failed" ])
                                && optionalNullableString item "namespace"
                                && optionalNullableBoolean item "success"
                                && optionalNullableInt64 item "durationMs"
                                && validOptionalDynamicContentItems item
                            | _ -> false
                        | "collabAgentToolCall" ->
                            match item.TryGetProperty "agentsStates", item.TryGetProperty "receiverThreadIds",
                                  item.TryGetProperty "senderThreadId", item.TryGetProperty "status",
                                  item.TryGetProperty "tool" with
                            | (true, states), (true, receivers), (true, sender), (true, status), (true, tool) ->
                                validCollabAgentStates states
                                && receivers.ValueKind = JsonValueKind.Array
                                && (receivers.EnumerateArray()
                                    |> Seq.forall (fun receiver -> receiver.ValueKind = JsonValueKind.String))
                                && sender.ValueKind = JsonValueKind.String
                                && status.ValueKind = JsonValueKind.String
                                && Set.contains (status.GetString())
                                    (set [ "inProgress"; "completed"; "failed"; "interrupted" ])
                                && tool.ValueKind = JsonValueKind.String
                                && Set.contains (tool.GetString())
                                    (set [ "spawnAgent"; "sendInput"; "resumeAgent"; "wait";
                                           "closeAgent"; "sendMessage"; "followupTask";
                                           "interruptAgent"; "listAgents" ])
                                && optionalNullableString item "model"
                                && optionalNullableString item "prompt"
                                && optionalCollabReasoningEffort item
                            | _ -> false
                        | "webSearch" ->
                            match item.TryGetProperty "query" with
                            | true, query when query.ValueKind = JsonValueKind.String ->
                                validOptionalWebSearchAction item
                                && validOptionalWebSearchResults item
                            | _ -> false
                        | "subAgentActivity" ->
                            match item.TryGetProperty "agentPath", item.TryGetProperty "agentThreadId",
                                  item.TryGetProperty "kind" with
                            | (true, path), (true, threadId), (true, activityKind) ->
                                path.ValueKind = JsonValueKind.String
                                && threadId.ValueKind = JsonValueKind.String
                                && activityKind.ValueKind = JsonValueKind.String
                                && Set.contains (activityKind.GetString())
                                    (set [ "started"; "interacted"; "interrupted"; "completed" ])
                            | _ -> false
                        | _ -> true
                    boundedText (id.GetString())
                    && Set.contains typeName supportedItemTypes
                    && variantValid
                | _ -> false

    let private parseTurn expectedThread expectedTurn methodName (parameters: JsonElement) =
        match fields "app-server-turn-params-invalid"
                (set [ "threadId"; "turn" ]) (set [ "threadId"; "turn" ]) parameters with
        | Error error -> Error error
        | Ok () ->
            match readText "app-server-turn-identity-invalid" parameters "threadId" with
            | Error error -> Error error
            | Ok threadId when threadId <> expectedThread -> Error "app-server-turn-identity-mismatch"
            | Ok _ ->
                let turn = parameters.GetProperty "turn"
                let allowed =
                    set [ "id"; "status"; "items"; "error"; "startedAt";
                          "completedAt"; "durationMs"; "itemsView" ]
                match fields "app-server-turn-shape-invalid"
                        (set [ "id"; "status"; "items" ]) allowed turn with
                | Error error -> Error error
                | Ok () when turn.GetProperty("items").ValueKind <> JsonValueKind.Array ->
                    Error "app-server-turn-shape-invalid"
                | Ok () when
                    turn.GetProperty("items").EnumerateArray()
                    |> Seq.exists (validTurnItem >> not) ->
                    Error "app-server-turn-item-invalid"
                | Ok () ->
                    match
                        readText "app-server-turn-identity-invalid" turn "id",
                        readText "app-server-turn-status-invalid" turn "status"
                    with
                    | Ok turnId, _ when turnId <> expectedTurn ->
                        Error "app-server-turn-identity-mismatch"
                    | Ok _, Ok "inProgress" when methodName = "turn/started" ->
                        Ok NativeTurnStarted
                    | Ok _, Ok status when
                        methodName = "turn/completed"
                        && Set.contains status (set [ "completed"; "failed"; "interrupted" ]) ->
                        Ok(NativeTurnTerminal status)
                    | Ok _, Ok _ -> Error "app-server-turn-status-invalid"
                    | Error error, _ | _, Error error -> Error error

    let private parseFrame expectedThread expectedTurn (bytes: byte array) =
        if isNull bytes || bytes.Length = 0 || bytes.Length > 65536 then
            Error "app-server-continuity-frame-size-invalid"
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement
                match fields "app-server-continuity-frame-invalid"
                        (set [ "method"; "params" ]) (set [ "method"; "params" ]) root with
                | Error error -> Error error
                | Ok () ->
                    match readText "app-server-continuity-method-invalid" root "method" with
                    | Error error -> Error error
                    | Ok "thread/tokenUsage/updated" ->
                        CodexAppServerUsageProjection.parse expectedThread expectedTurn bytes
                        |> Result.map NativeUsageUpdated
                    | Ok ("turn/started" | "turn/completed" as methodName) ->
                        parseTurn expectedThread expectedTurn methodName (root.GetProperty "params")
                    | Ok _ -> Error "app-server-continuity-method-unsupported"
            with :? JsonException ->
                Error "app-server-continuity-json-invalid"

    let private cumulativeAtLeast newer older =
        newer.Input >= older.Input
        && newer.CachedInput >= older.CachedInput
        && newer.CacheWriteInput >= older.CacheWriteInput
        && newer.Output >= older.Output
        && newer.ReasoningOutput >= older.ReasoningOutput
        && newer.Total >= older.Total

    let private gap code state = { state with Status = ContinuityGap code }

    /// Bind the supplied expected scope and transport to a future authenticated subscription.
    /// There is no installed authenticator or connection to the current private session.
    let beginWindow expectedScope expectedTurn expectedTransport
        (authenticator: ICodexAppServerSubscriptionAuthenticator)
        : Result<CodexAppServerContinuityState, string> =
        if isNull (box expectedScope)
           || not (DirectSessionTelemetryFacts.validScope expectedScope)
           || not (boundedText expectedTurn)
           || not (boundedText expectedTransport)
           || isNull (box authenticator) then
            Error "app-server-subscription-input-invalid"
        else
            let result =
                try authenticator.ReadBoundSubscription()
                with _ -> Error "adapter-threw"
            match result with
            | Error _ -> Error "app-server-subscription-unavailable"
            | Ok binding when isNull (box binding) || isNull (box binding.Scope) ->
                Error "app-server-subscription-binding-invalid"
            | Ok binding when
                not (boundedText binding.TurnId)
                || not (boundedText binding.TransportIdentity)
                || not (boundedText binding.ConnectionId)
                || binding.ProtocolVersion <> "codex-app-server-v2/0.156.1"
                || isNull binding.SubscriptionDigest
                || not (digestPattern.IsMatch binding.SubscriptionDigest) ->
                Error "app-server-subscription-binding-invalid"
            | Ok binding when
                binding.Scope <> expectedScope
                || binding.TurnId <> expectedTurn
                || binding.TransportIdentity <> expectedTransport ->
                Error "app-server-subscription-binding-mismatch"
            | Ok binding ->
                Ok
                    { Binding = binding
                      NextOrdinal = 1L
                      Status = AwaitingStart
                      LastCumulative = None
                      UsageHashes = Set.empty }

    let status state = state.Status

    /// Exposes the validated binding to a future journal gate; it is not authentication proof.
    let subscriptionBinding state = state.Binding

    /// Apply one supplied frame. Any gap is latched; later frames cannot restore coverage.
    /// The ordinal is not a native App Server cursor and cannot prove upstream completeness.
    let apply (state: CodexAppServerContinuityState) (frame: CodexAppServerObservedFrame) =
        match state.Status with
        | ContinuityGap _ -> state
        | TerminalObserved _ -> gap "app-server-continuity-after-terminal" state
        | _ when isNull (box frame) -> gap "app-server-continuity-frame-missing" state
        | _ when frame.TransportIdentity <> state.Binding.TransportIdentity
                 || frame.ConnectionId <> state.Binding.ConnectionId ->
            gap "app-server-continuity-source-mismatch" state
        | _ when frame.Ordinal <> state.NextOrdinal ->
            gap "app-server-continuity-sequence-gap" state
        | _ ->
            match parseFrame state.Binding.Scope.ThreadId state.Binding.TurnId frame.Payload with
            | Error code -> gap code state
            | Ok event ->
                match state.Status, event with
                | AwaitingStart, NativeTurnStarted ->
                    { state with Status = InTurn 0; NextOrdinal = state.NextOrdinal + 1L }
                | AwaitingStart, _ -> gap "app-server-continuity-start-missing" state
                | InTurn _, NativeTurnStarted -> gap "app-server-continuity-start-duplicate" state
                | InTurn count, NativeUsageUpdated usage when
                    Set.contains usage.WireSha256 state.UsageHashes ->
                    gap "app-server-continuity-usage-duplicate" state
                | InTurn count, NativeUsageUpdated usage when
                    state.LastCumulative
                    |> Option.exists (fun prior -> not (cumulativeAtLeast usage.Cumulative prior)) ->
                    gap "app-server-continuity-usage-regressed" state
                | InTurn count, NativeUsageUpdated usage ->
                    { state with
                        Status = InTurn(count + 1)
                        NextOrdinal = state.NextOrdinal + 1L
                        LastCumulative = Some usage.Cumulative
                        UsageHashes = Set.add usage.WireSha256 state.UsageHashes }
                | InTurn count, NativeTurnTerminal terminal ->
                    { state with
                        Status = TerminalObserved(terminal, count)
                        NextOrdinal = state.NextOrdinal + 1L }
                | _ -> gap "app-server-continuity-transition-invalid" state

    /// A lost subscribed transport before terminal is a permanent coverage gap.
    let disconnected state =
        match state.Status with
        | AwaitingStart | InTurn _ -> gap "app-server-continuity-disconnected" state
        | _ -> state
