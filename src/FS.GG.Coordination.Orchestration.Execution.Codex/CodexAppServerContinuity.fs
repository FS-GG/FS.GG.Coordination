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
