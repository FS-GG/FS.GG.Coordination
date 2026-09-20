namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Text.Json

/// Minimal native usage fact. No Codex prompt, output body, or authentication material crosses this boundary.
type CodexTurnUsage =
    {
        ThreadId: string
        TurnId: string option
        TurnSequence: int64
        Provider: string option
        ObservedModel: string option
        ObservedEffort: string option
        Backend: string option
        Input: int64
        CachedInput: int64
        Output: int64
        Reasoning: int64 option
        Total: int64
    }

[<RequireQualifiedAccess>]
module CodexTurnProjection =
    let private text (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String ->
            value.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None

    let private count (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt64() with
            | true, number when number >= 0L -> Some number
            | _ -> None
        | _ -> None

    /// Return None for unrelated frames, an exact completed-turn fact for valid usage,
    /// or a gap code for a completed turn whose native evidence cannot be counted.
    let project currentThread turnSequence (raw: string) =
        try
            use document = JsonDocument.Parse raw
            let root = document.RootElement

            if text root "type" <> Some "turn.completed" then
                None
            else
                match root.TryGetProperty "usage" with
                | false, _ -> Some(Error "missing-turn-usage")
                | true, usage when usage.ValueKind <> JsonValueKind.Object ->
                    Some(Error "malformed-turn-usage")
                | true, usage ->
                    let reasoning =
                        match usage.TryGetProperty "reasoning_output_tokens" with
                        | false, _ -> Some None
                        | true, _ -> count usage "reasoning_output_tokens" |> Option.map Some

                    match
                        text root "thread_id" |> Option.orElse currentThread,
                        count usage "input_tokens",
                        count usage "cached_input_tokens",
                        count usage "output_tokens",
                        reasoning
                    with
                    | Some thread, Some input, Some cached, Some output, Some reasoning when
                        cached <= input && (reasoning |> Option.forall (fun value -> value <= output))
                        ->
                        try
                            let total = Checked.(+) input output

                            Some(
                                Ok
                                    {
                                        ThreadId = thread
                                        TurnId = text root "turn_id"
                                        TurnSequence = turnSequence
                                        Provider = text root "provider"
                                        ObservedModel = text root "model"
                                        ObservedEffort = text root "effort"
                                        Backend = text root "backend"
                                        Input = input
                                        CachedInput = cached
                                        Output = output
                                        Reasoning = reasoning
                                        Total = total
                                    }
                            )
                        with :? OverflowException ->
                            Some(Error "usage-overflow")
                    | None, _, _, _, _ -> Some(Error "missing-turn-thread")
                    | _ -> Some(Error "invalid-turn-counters")
        with :? JsonException ->
            Some(Error "malformed-json-frame")
