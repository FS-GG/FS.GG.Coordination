namespace FS.GG.FourD.Typed

open System
open System.IO
open System.Text.Json

module Program =
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let private readBounded path =
        let info = FileInfo path
        if not info.Exists || info.Length <= 0L || info.Length > 65536L then invalidArg "input" "typed-input-refused"
        File.ReadAllBytes path
    let private rejectDuplicateProperties (bytes: byte array) =
        let mutable reader = Utf8JsonReader(ReadOnlySpan bytes, JsonReaderOptions(MaxDepth = 32))
        let scopes = Collections.Generic.Stack<Collections.Generic.HashSet<string>>()
        while reader.Read() do
            match reader.TokenType with
            | JsonTokenType.StartObject -> scopes.Push(Collections.Generic.HashSet(StringComparer.Ordinal))
            | JsonTokenType.EndObject -> scopes.Pop() |> ignore
            | JsonTokenType.PropertyName ->
                if scopes.Count = 0 || not (scopes.Peek().Add(reader.GetString())) then
                    invalidArg "input" "typed-json-duplicate-refused"
            | _ -> ()
    let private requiredString (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> invalidArg name "typed-string-refused"
    let private requiredInt (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | _ -> invalidArg name "typed-integer-refused"
    let private requiredBool (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | true, value when value.ValueKind = JsonValueKind.False -> false
        | _ -> invalidArg name "typed-boolean-refused"
    let private parsePhase = function
        | "idle" -> Idle | "acquired" -> Acquired | "validated" -> Validated | "admitted" -> Admitted
        | "effect" -> Effect | "cleanup" -> Cleanup | "finished" -> Finished | "cleanup-failed" -> CleanupFailed
        | _ -> invalidArg "phase" "typed-phase-refused"
    let private phaseText = function
        | Idle -> "idle" | Acquired -> "acquired" | Validated -> "validated" | Admitted -> "admitted"
        | Effect -> "effect" | Cleanup -> "cleanup" | Finished -> "finished" | CleanupFailed -> "cleanup-failed"
    let private outcomeText = function NoneObserved -> "none" | Unknown -> "unknown" | Success -> "success" | Refused -> "refused"

    let private readState (root: JsonElement) =
        let expected = Set ["schema"; "phase"; "acquiredIdentity"; "validatedIdentity"; "admittedIdentity"
                            "currentIdentity"; "effectAcknowledged"; "outcome"; "owned"; "closed"; "cancelled"
                            "budgetRemaining"; "effectEligible"; "cleanupComplete"; "successful"]
        let actual = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if actual <> expected then invalidArg "state" "typed-state-shape-refused"
        let strings (name: string) =
            let values = root.GetProperty(name).EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
            if values |> List.exists (fun value -> isNull value || not (Policy.validToken value))
               || values.Length <> (Set.ofList values).Count then invalidArg name "typed-resource-refused"
            Set.ofList values
        let optional (name: string) =
            let value=root.GetProperty name
            if value.ValueKind=JsonValueKind.Null then None
            elif value.ValueKind=JsonValueKind.String && Policy.validToken(value.GetString()) then Some(value.GetString())
            else invalidArg name "typed-identity-refused"
        let state = { Phase = parsePhase (requiredString root "phase"); AcquiredIdentity = optional "acquiredIdentity";
          ValidatedIdentity = optional "validatedIdentity"; AdmittedIdentity = optional "admittedIdentity";
          CurrentIdentity = optional "currentIdentity"; EffectAcknowledged = requiredBool root "effectAcknowledged";
          Outcome = (match requiredString root "outcome" with "success" -> Success | "unknown" -> Unknown | "refused" -> Refused | "none" -> NoneObserved | _ -> invalidArg "outcome" "typed-outcome-refused");
          Owned = strings "owned"; Closed = strings "closed"; Cancelled = requiredBool root "cancelled";
          BudgetRemaining = requiredInt root "budgetRemaining" }
        if requiredString root "schema" <> "fsgg.fourd.typed-operation-state/1"
           || requiredBool root "effectEligible" <> Policy.effectEligible state
           || requiredBool root "cleanupComplete" <> Policy.cleanupComplete state
           || requiredBool root "successful" <> Policy.successful state
           || not (Policy.validateState state) then
            invalidArg "state" "typed-state-refused"
        state

    let private writeState state =
        {| schema = "fsgg.fourd.typed-operation-state/1"; phase = phaseText state.Phase
           acquiredIdentity = Option.toObj state.AcquiredIdentity; validatedIdentity = Option.toObj state.ValidatedIdentity
           admittedIdentity = Option.toObj state.AdmittedIdentity; currentIdentity = Option.toObj state.CurrentIdentity
           effectAcknowledged = state.EffectAcknowledged; outcome = outcomeText state.Outcome
           owned = state.Owned |> Set.toArray; closed = state.Closed |> Set.toArray
           cancelled = state.Cancelled; budgetRemaining = state.BudgetRemaining
           effectEligible = Policy.effectEligible state; cleanupComplete = Policy.cleanupComplete state
           successful = Policy.successful state |}

    let private observation (root: JsonElement) =
        let fields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        let cost = requiredInt root "cost"
        match requiredString root "kind" with
        | "acquire" when fields = Set ["kind";"identity";"resource";"cost"] -> Acquire(requiredString root "identity", requiredString root "resource", cost)
        | "validate" when fields = Set ["kind";"identity";"cost"] -> Validate(requiredString root "identity", cost)
        | "admit" when fields = Set ["kind";"identity";"cost"] -> Admit(requiredString root "identity", cost)
        | "invalidate" when fields = Set ["kind";"identity";"cost"] -> Invalidate(requiredString root "identity", cost)
        | "begin-effect" when fields = Set ["kind";"resource";"acknowledged";"cost"] -> BeginEffect(requiredString root "resource", requiredBool root "acknowledged", cost)
        | "observe-success" when fields = Set ["kind";"cost"] -> ObserveSuccess cost
        | "begin-cleanup" when fields = Set ["kind";"cancelled";"cost"] -> BeginCleanup(requiredBool root "cancelled", cost)
        | "cancel" when fields = Set ["kind";"cost"] -> Cancel cost
        | "close" when fields = Set ["kind";"resource";"cost"] -> Close(requiredString root "resource", cost)
        | "finish" when fields = Set ["kind";"cost"] -> Finish cost
        | _ -> invalidArg "kind" "observation-refused"

    let private execute input output =
        let bytes = readBounded input
        rejectDuplicateProperties bytes
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 16))
        let root = doc.RootElement
        if requiredString root "schema" <> "fsgg.fourd.typed-operation-request/1" then invalidArg "input" "typed-schema-refused"
        let hasState = root.TryGetProperty("state") |> fst
        let requestFields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if requestFields <> (if hasState then Set ["schema";"state";"observation"] else Set ["schema";"budget";"observation"])
        then invalidArg "input" "typed-request-shape-refused"
        let state = if hasState then readState (root.GetProperty "state") else Policy.initial (requiredInt root "budget")
        match Policy.reduce state (observation (root.GetProperty "observation")) with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted = false; refusal = reason |}, options) + "\n")
            2
        | Ok value ->
            File.WriteAllText(output, JsonSerializer.Serialize(writeState value, options) + "\n")
            0

    let private validateJoin input output =
        let bytes = readBounded input
        rejectDuplicateProperties bytes
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 32))
        let root = doc.RootElement
        let fields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if fields <> Set ["schema";"admission";"placementSha";"runId";"runAttempt";"observedSourceSha"
                          "observedSourceTree";"observedInventorySha256";"observedNow"] then
            invalidArg "input" "typed-join-request-shape-refused"
        if requiredString root "schema" <> "fsgg.fourd.typed-source-join-request/1" then invalidArg "input" "typed-schema-refused"
        let admission = root.GetProperty "admission"
        match Join.validate admission (requiredString root "placementSha") (requiredString root "runId")
                  (requiredString root "runAttempt") (requiredString root "observedSourceSha")
                  (requiredString root "observedSourceTree") (requiredString root "observedInventorySha256")
                  (requiredString root "observedNow") with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted = false; refusal = reason |}, options) + "\n")
            2
        | Ok identity ->
            File.WriteAllText(output, JsonSerializer.Serialize(
                {| schema = "fsgg.fourd.typed-source-join/1"; accepted = true; identity = identity |}, options) + "\n")
            0

    let private validateRoot input output =
        let bytes = readBounded input
        rejectDuplicateProperties bytes
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 16))
        let root = doc.RootElement
        let fields = root.EnumerateObject() |> Seq.map (_.Name) |> Set.ofSeq
        if fields <> Set ["schema";"context";"observed";"placementSha";"placementTree";"runId"]
           || requiredString root "schema" <> "fsgg.fourd.typed-root-join-request/1" then
            invalidArg "input" "typed-root-request-refused"
        match Join.validateRoot (root.GetProperty "context") (root.GetProperty "observed") (requiredString root "placementSha")
                                (requiredString root "placementTree") (requiredString root "runId") with
        | Error reason ->
            File.WriteAllText(output, JsonSerializer.Serialize({| accepted=false; refusal=reason |}, options)+"\n"); 2
        | Ok identity ->
            File.WriteAllText(output, JsonSerializer.Serialize(
                {| schema="fsgg.fourd.typed-root-join/1"; accepted=true; identity=identity |}, options)+"\n"); 0

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length <> 3 then 2
            elif argv[0] = "transition" then execute argv[1] argv[2]
            elif argv[0] = "validate-join" then validateJoin argv[1] argv[2]
            elif argv[0] = "validate-root" then validateRoot argv[1] argv[2]
            else 2
        with _ -> 2
