namespace FS.GG.FourD.Typed

open System
open System.IO
open System.Text.Json

module Program =
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let private requiredString (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> null
    let private requiredInt (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | _ -> 0
    let private requiredBool (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | true, value when value.ValueKind = JsonValueKind.False -> false
        | _ -> false
    let private parsePhase = function
        | "idle" -> Idle | "acquired" -> Acquired | "validated" -> Validated | "admitted" -> Admitted
        | "effect" -> Effect | "cleanup" -> Cleanup | "finished" -> Finished | _ -> CleanupFailed
    let private phaseText = function
        | Idle -> "idle" | Acquired -> "acquired" | Validated -> "validated" | Admitted -> "admitted"
        | Effect -> "effect" | Cleanup -> "cleanup" | Finished -> "finished" | CleanupFailed -> "cleanup-failed"
    let private outcomeText = function NoneObserved -> "none" | Unknown -> "unknown" | Success -> "success" | Refused -> "refused"

    let private readState (root: JsonElement) =
        let strings (name: string) = root.GetProperty(name).EnumerateArray() |> Seq.map (_.GetString()) |> Set.ofSeq
        let optional (name: string) = let value = requiredString root name in if isNull value then None else Some value
        { Phase = parsePhase (requiredString root "phase"); AcquiredIdentity = optional "acquiredIdentity"
          ValidatedIdentity = optional "validatedIdentity"; AdmittedIdentity = optional "admittedIdentity"
          CurrentIdentity = optional "currentIdentity"; EffectAcknowledged = requiredBool root "effectAcknowledged"
          Outcome = match requiredString root "outcome" with "success" -> Success | "unknown" -> Unknown | "refused" -> Refused | _ -> NoneObserved
          Owned = strings "owned"; Closed = strings "closed"; Cancelled = requiredBool root "cancelled"
          BudgetRemaining = requiredInt root "budgetRemaining" }

    let private writeState state =
        {| schema = "fsgg.fourd.typed-operation-state/1"; phase = phaseText state.Phase
           acquiredIdentity = Option.toObj state.AcquiredIdentity; validatedIdentity = Option.toObj state.ValidatedIdentity
           admittedIdentity = Option.toObj state.AdmittedIdentity; currentIdentity = Option.toObj state.CurrentIdentity
           effectAcknowledged = state.EffectAcknowledged; outcome = outcomeText state.Outcome
           owned = state.Owned |> Set.toArray; closed = state.Closed |> Set.toArray
           cancelled = state.Cancelled; budgetRemaining = state.BudgetRemaining
           effectEligible = Policy.effectEligible state; cleanupComplete = Policy.cleanupComplete state
           successful = Policy.successful state |}

    let private observation root =
        let cost = requiredInt root "cost"
        match requiredString root "kind" with
        | "acquire" -> Acquire(requiredString root "identity", requiredString root "resource", cost)
        | "validate" -> Validate(requiredString root "identity", cost)
        | "admit" -> Admit(requiredString root "identity", cost)
        | "invalidate" -> Invalidate(requiredString root "identity", cost)
        | "begin-effect" -> BeginEffect(requiredString root "resource", requiredBool root "acknowledged", cost)
        | "observe-success" -> ObserveSuccess cost
        | "cancel" -> Cancel cost | "close" -> Close(requiredString root "resource", cost)
        | "finish" -> Finish cost | _ -> invalidArg "kind" "observation-refused"

    let private execute input output =
        let bytes = File.ReadAllBytes input
        if bytes.Length = 0 || bytes.Length > 65536 then invalidArg "input" "typed-input-refused"
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 16))
        let root = doc.RootElement
        if requiredString root "schema" <> "fsgg.fourd.typed-operation-request/1" then invalidArg "input" "typed-schema-refused"
        let state = if root.TryGetProperty("state") |> fst then readState (root.GetProperty "state")
                    else Policy.initial (requiredInt root "budget")
        match Policy.reduce state (observation (root.GetProperty "observation")) with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted = false; refusal = reason |}, options) + "\n")
            2
        | Ok value ->
            File.WriteAllText(output, JsonSerializer.Serialize(writeState value, options) + "\n")
            0

    let private validateJoin input output =
        let bytes = File.ReadAllBytes input
        if bytes.Length = 0 || bytes.Length > 65536 then invalidArg "input" "typed-input-refused"
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 32))
        let root = doc.RootElement
        if requiredString root "schema" <> "fsgg.fourd.typed-source-join-request/1" then invalidArg "input" "typed-schema-refused"
        let admission = root.GetProperty "admission"
        match Join.validate admission (requiredString root "placementSha") (requiredString root "runId")
                  (requiredString root "runAttempt") (requiredString root "observedSourceSha")
                  (requiredString root "observedSourceTree") (requiredString root "observedInventorySha256") with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted = false; refusal = reason |}, options) + "\n")
            2
        | Ok identity ->
            File.WriteAllText(output, JsonSerializer.Serialize(
                {| schema = "fsgg.fourd.typed-source-join/1"; accepted = true; identity = identity |}, options) + "\n")
            0

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length <> 3 then 2
            elif argv[0] = "transition" then execute argv[1] argv[2]
            elif argv[0] = "validate-join" then validateJoin argv[1] argv[2]
            else 2
        with _ -> 2
