namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Security.Cryptography
open System.Text.Json

/// The app-server's last and cumulative token snapshots are not completed-turn usage facts.
type CodexAppServerTokenCounts =
    {
        Input: int64
        CachedInput: int64
        CacheWriteInput: int64
        Output: int64
        ReasoningOutput: int64
        Total: int64
    }

/// Read-only projection of one app-server v2 `thread/tokenUsage/updated` notification.
/// Transport authentication, subscription and event continuity are external obligations.
type CodexAppServerUsageUpdate =
    {
        ThreadId: string
        TurnId: string
        Last: CodexAppServerTokenCounts
        Cumulative: CodexAppServerTokenCounts
        WireSha256: string
    }

[<RequireQualifiedAccess>]
module CodexAppServerUsageProjection =
    let private boundedText (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && (value |> Seq.forall (Char.IsControl >> not))

    let private objectFields code required allowed (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then
            Error code
        else
            let names = node.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            let actual = names |> Set.ofList
            if names.Length <> actual.Count
               || not (Set.isSubset required actual)
               || not (Set.isSubset actual allowed) then
                Error code
            else
                Ok ()

    let private readText (code: string) (node: JsonElement) (name: string) =
        let value = node.GetProperty name
        if value.ValueKind <> JsonValueKind.String then Error code
        else
            let text = value.GetString()
            if boundedText text then Ok text else Error code

    let private readCount (code: string) (node: JsonElement) (name: string) =
        let value = node.GetProperty name
        if value.ValueKind <> JsonValueKind.Number then Error code
        else
            match value.TryGetInt64() with
            | true, count when count >= 0L -> Ok count
            | _ -> Error code

    let internal validCounts (value: CodexAppServerTokenCounts) =
        not (isNull (box value))
        && value.Input >= 0L
        && value.CachedInput >= 0L
        && value.CacheWriteInput >= 0L
        && value.Output >= 0L
        && value.ReasoningOutput >= 0L
        && value.Total >= 0L
        && value.CachedInput <= value.Input
        && value.ReasoningOutput <= value.Output
        && value.Input <= Int64.MaxValue - value.Output
        && value.Total = value.Input + value.Output

    let private counts (node: JsonElement) =
        let code = "app-server-usage-counters-invalid"
        let required =
            set [ "inputTokens"; "cachedInputTokens"; "outputTokens";
                  "reasoningOutputTokens"; "totalTokens" ]
        let allowed = Set.add "cacheWriteInputTokens" required
        match objectFields code required allowed node with
        | Error error -> Error error
        | Ok () ->
            let cacheWrite =
                if node.TryGetProperty("cacheWriteInputTokens") |> fst then
                    readCount code node "cacheWriteInputTokens"
                else Ok 0L
            match
                readCount code node "inputTokens",
                readCount code node "cachedInputTokens",
                cacheWrite,
                readCount code node "outputTokens",
                readCount code node "reasoningOutputTokens",
                readCount code node "totalTokens"
            with
            | Ok input, Ok cached, Ok cacheWritten, Ok output, Ok reasoning, Ok total ->
                let value =
                    { Input = input
                      CachedInput = cached
                      CacheWriteInput = cacheWritten
                      Output = output
                      ReasoningOutput = reasoning
                      Total = total }
                if validCounts value then Ok value else Error code
            | _ -> Error code

    let internal dominates total last =
        total.Input >= last.Input
        && total.CachedInput >= last.CachedInput
        && total.CacheWriteInput >= last.CacheWriteInput
        && total.Output >= last.Output
        && total.ReasoningOutput >= last.ReasoningOutput
        && total.Total >= last.Total

    /// Parse only an independently supplied app-server usage notification for exact expected IDs.
    /// This never maps `last` or cumulative snapshots to `CodexTurnUsage` or publishes telemetry.
    let parse expectedThreadId expectedTurnId (bytes: byte array)
        : Result<CodexAppServerUsageUpdate, string> =
        if not (boundedText expectedThreadId) || not (boundedText expectedTurnId) then
            Error "app-server-expected-identity-invalid"
        elif isNull bytes || bytes.Length = 0 || bytes.Length > 32768 then
            Error "app-server-usage-frame-size-invalid"
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                let root = document.RootElement
                match objectFields "app-server-usage-frame-invalid"
                        (set [ "method"; "params" ]) (set [ "method"; "params" ]) root with
                | Error error -> Error error
                | Ok () ->
                    match readText "app-server-usage-method-invalid" root "method" with
                    | Error error -> Error error
                    | Ok methodName when methodName <> "thread/tokenUsage/updated" ->
                        Error "app-server-usage-method-unsupported"
                    | Ok _ ->
                        let parameters = root.GetProperty "params"
                        match objectFields "app-server-usage-params-invalid"
                                (set [ "threadId"; "turnId"; "tokenUsage" ])
                                (set [ "threadId"; "turnId"; "tokenUsage" ]) parameters with
                        | Error error -> Error error
                        | Ok () ->
                            match
                                readText "app-server-usage-identity-invalid" parameters "threadId",
                                readText "app-server-usage-identity-invalid" parameters "turnId"
                            with
                            | Ok threadId, Ok turnId when
                                threadId <> expectedThreadId || turnId <> expectedTurnId ->
                                Error "app-server-usage-identity-mismatch"
                            | Ok threadId, Ok turnId ->
                                let usage = parameters.GetProperty "tokenUsage"
                                match objectFields "app-server-usage-shape-invalid"
                                        (set [ "last"; "total" ])
                                        (set [ "last"; "total"; "modelContextWindow" ]) usage with
                                | Error error -> Error error
                                | Ok () when
                                    (match usage.TryGetProperty "modelContextWindow" with
                                     | false, _ -> false
                                     | true, value when value.ValueKind = JsonValueKind.Null -> false
                                     | true, value when value.ValueKind = JsonValueKind.Number ->
                                         match value.TryGetInt64() with
                                         | true, count when count >= 0L -> false
                                         | _ -> true
                                     | _ -> true) ->
                                    Error "app-server-usage-shape-invalid"
                                | Ok () ->
                                    match counts (usage.GetProperty "last"), counts (usage.GetProperty "total") with
                                    | Ok last, Ok cumulative when dominates cumulative last ->
                                        Ok
                                            { ThreadId = threadId
                                              TurnId = turnId
                                              Last = last
                                              Cumulative = cumulative
                                              WireSha256 =
                                                SHA256.HashData bytes
                                                |> Convert.ToHexString
                                                |> fun value -> value.ToLowerInvariant() }
                                    | Ok _, Ok _ -> Error "app-server-usage-total-regressed"
                                    | Error error, _ | _, Error error -> Error error
                            | Error error, _ | _, Error error -> Error error
            with :? JsonException ->
                Error "app-server-usage-json-invalid"
