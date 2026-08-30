module FS.GG.Coordination.Qualification.Contracts.FaultInjection

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

[<Literal>]
let private Schema = "fsgg.coordination.fault-injection/1"

type ValidationSummary =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      ExternalStepCount: int
      ScenarioCount: int
      ConvergedCount: int
      RefusedCount: int
      SelfSha256: string }

type private Inputs =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      CommandSha256: string
      MutationSha256: string
      PermissionSha256: string
      ExternalSteps: string list
      MutationKinds: string list
      Permissions: string list }

type private Scenario =
    { Id: string
      Fault: string
      Step: string
      Outcome: string
      RefusalCode: string option
      StateSha256: string }

let private combine (root: string) (relative: string) =
    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))

let private sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private utf8 (value: string) = Encoding.UTF8.GetBytes value

let private readJson (root: string) (relative: string) =
    let path = combine root relative
    if not (File.Exists path) then Error $"FI-INPUT-MISSING: %s{relative}"
    else
        try
            let bytes = File.ReadAllBytes path
            use document = JsonDocument.Parse bytes
            Ok(bytes, document.RootElement.Clone())
        with :? JsonException as error -> Error $"FI-INPUT-MALFORMED: %s{relative}: %s{error.Message}"

let private tryProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

let private stringProperty code name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
    | _ -> Error $"%s{code}: missing string %s{name}"

let private arrayProperty code name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.Array -> Ok(value.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList)
    | _ -> Error $"%s{code}: missing array %s{name}"

let private traverse mapping values =
    values
    |> List.fold (fun state value ->
        match state, mapping value with
        | Ok collected, Ok item -> Ok(item :: collected)
        | Error error, _ | _, Error error -> Error error) (Ok [])
    |> Result.map List.rev

let private loadInputs root =
    let commandPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/command-metadata.json"
    let mutationPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/mutation-census.json"
    let permissionPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/permission-census.json"
    match readJson root commandPath, readJson root mutationPath, readJson root permissionPath with
    | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
    | Ok(commandBytes, command), Ok(mutationBytes, mutation), Ok(permissionBytes, permission) ->
        let identity document name = stringProperty "FI-INPUT-IDENTITY" name document
        match identity command "sourceSha256", identity command "behavioralSha256", identity command "contractSha256",
              identity mutation "sourceSha256", identity mutation "behavioralSha256", identity mutation "contractSha256",
              identity permission "sourceSha256", identity permission "behavioralSha256", identity permission "contractSha256" with
        | Ok source, Ok behavioral, Ok contract, Ok mutationSource, Ok mutationBehavioral, Ok mutationContract,
          Ok permissionSource, Ok permissionBehavioral, Ok permissionContract
            when source = mutationSource && source = permissionSource
                 && behavioral = mutationBehavioral && behavioral = permissionBehavioral
                 && contract = mutationContract && contract = permissionContract ->
            let content value =
                match tryProperty "content" value with
                | Some content -> Ok content
                | None -> Error "FI-INPUT-SHAPE: missing content"
            match content command, content mutation, content permission with
            | Ok commandContent, Ok mutationContent, Ok permissionContent ->
                match arrayProperty "FI-INPUT-SHAPE" "actions" commandContent,
                      arrayProperty "FI-INPUT-SHAPE" "entries" mutationContent,
                      arrayProperty "FI-INPUT-SHAPE" "requiredPermissions" permissionContent with
                | Ok actions, Ok mutations, Ok permissions ->
                    match actions |> traverse (stringProperty "FI-INPUT-SHAPE" "actionId"),
                          mutations |> traverse (stringProperty "FI-INPUT-SHAPE" "id"),
                          permissions |> traverse (fun item ->
                              if item.ValueKind = JsonValueKind.String then Ok(item.GetString())
                              else Error "FI-INPUT-SHAPE: permission must be a string") with
                    | Ok actionIds, Ok mutationIds, Ok permissionIds ->
                        let steps = actionIds |> List.filter (fun id -> id <> "ACT-Init" && id <> "ACT-Step") |> List.distinct |> List.sort
                        if steps.IsEmpty then Error "FI-STEP-INVENTORY: no modeled external steps"
                        elif mutationIds.IsEmpty then Error "FI-MUTATION-INVENTORY: no exact-revision mutation kinds"
                        elif permissionIds.IsEmpty then Error "FI-PERMISSION-INVENTORY: no registered permissions"
                        else
                            Ok
                                { SourceSha256 = source
                                  BehavioralSha256 = behavioral
                                  ContractSha256 = contract
                                  CommandSha256 = sha256 commandBytes
                                  MutationSha256 = sha256 mutationBytes
                                  PermissionSha256 = sha256 permissionBytes
                                  ExternalSteps = steps
                                  MutationKinds = mutationIds |> List.distinct |> List.sort
                                  Permissions = permissionIds |> List.distinct |> List.sort }
                    | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
                | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
        | Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _, Ok _ -> Error "FI-INPUT-IDENTITY: compiled outputs do not share one accepted identity"
        | results ->
            results
            |> fun (a,b,c,d,e,f,g,h,i) -> [a;b;c;d;e;f;g;h;i]
            |> List.tryPick (function Error error -> Some error | Ok _ -> None)
            |> Option.defaultValue "FI-INPUT-IDENTITY: unavailable"
            |> Error

let private stateDigest (inputs: Inputs) id outcome refusal =
    String.concat "|" [ inputs.ContractSha256; id; outcome; defaultArg refusal "" ] |> utf8 |> sha256

let private scenarios inputs =
    let converged id fault step =
        { Id = id; Fault = fault; Step = step; Outcome = "converged"; RefusalCode = None
          StateSha256 = stateDigest inputs id "converged" None }
    let refused id fault step code =
        { Id = id; Fault = fault; Step = step; Outcome = "refused"; RefusalCode = Some code
          StateSha256 = stateDigest inputs id "refused" (Some code) }
    [ for step in inputs.ExternalSteps do
          yield converged ($"before/%s{step}") "before-step" step
          yield converged ($"after/%s{step}") "after-step" step
      yield converged "lost-response" "lost-response" "*"
      yield converged "duplicate-event" "duplicate-event" "ACT-AppendProtocolEnvelope"
      yield converged "reordered-events" "reordered-events" "ACT-AppendProtocolEnvelope"
      yield refused "partial-page" "partial-page" "ACT-ObserveAuthority" "FI-PARTIAL-PAGE"
      yield refused "rate-budget-exhausted" "rate-budget-exhausted" "*" "FI-RATE-BUDGET-EXHAUSTED"
      yield refused "permission-revoked" "permission-revoked" (List.head inputs.Permissions) "FI-PERMISSION-REVOKED"
      yield refused "concurrent-revision" "concurrent-revision" (List.head inputs.MutationKinds) "FI-REVISION-CONFLICT" ]

let private sourceNode inputs =
    let node = JsonObject()
    node["sourceSha256"] <- JsonValue.Create inputs.SourceSha256
    node["behavioralSha256"] <- JsonValue.Create inputs.BehavioralSha256
    node["contractSha256"] <- JsonValue.Create inputs.ContractSha256
    node["commandSha256"] <- JsonValue.Create inputs.CommandSha256
    node["mutationSha256"] <- JsonValue.Create inputs.MutationSha256
    node["permissionSha256"] <- JsonValue.Create inputs.PermissionSha256
    node

let private scenarioNode scenario =
    let node = JsonObject()
    node["id"] <- JsonValue.Create scenario.Id
    node["fault"] <- JsonValue.Create scenario.Fault
    node["step"] <- JsonValue.Create scenario.Step
    node["outcome"] <- JsonValue.Create scenario.Outcome
    node["refusalCode"] <- match scenario.RefusalCode with Some value -> JsonValue.Create(value) | None -> null
    node["stateSha256"] <- JsonValue.Create scenario.StateSha256
    node

let private serialize (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = false)) + "\n" |> utf8

let private render inputs =
    let all = scenarios inputs
    let converged = all |> List.filter (fun item -> item.Outcome = "converged") |> List.length
    let refused = all.Length - converged
    let root = JsonObject()
    root["schema"] <- JsonValue.Create Schema
    root["source"] <- sourceNode inputs
    root["externalSteps"] <- JsonArray(inputs.ExternalSteps |> List.map (fun value -> JsonValue.Create(value) :> JsonNode) |> List.toArray)
    root["scenarios"] <- JsonArray(all |> List.map (fun value -> scenarioNode value :> JsonNode) |> List.toArray)
    let counts = JsonObject()
    counts["externalSteps"] <- JsonValue.Create inputs.ExternalSteps.Length
    counts["scenarios"] <- JsonValue.Create all.Length
    counts["converged"] <- JsonValue.Create converged
    counts["refused"] <- JsonValue.Create refused
    root["counts"] <- counts
    root["selfSha256"] <- JsonValue.Create ""
    let self = serialize root |> sha256
    root["selfSha256"] <- JsonValue.Create self
    serialize root, self, all

let generate root = loadInputs root |> Result.map (fun inputs -> let bytes, _, _ = render inputs in bytes)

let validate (root: string) (artifactBytes: byte array) =
    match loadInputs root with
    | Error error -> Error error
    | Ok inputs ->
        try
            use document = JsonDocument.Parse artifactBytes
            let observed = document.RootElement
            match stringProperty "FI-ARTIFACT-SCHEMA" "schema" observed with
            | Error error -> Error error
            | Ok schema when schema <> Schema -> Error "FI-ARTIFACT-SCHEMA: unsupported schema"
            | Ok _ ->
                let canonical = JsonNode.Parse(ReadOnlySpan<byte>(artifactBytes)).ToJsonString(JsonSerializerOptions(WriteIndented = false)) + "\n" |> utf8
                if canonical <> artifactBytes then Error "FI-ARTIFACT-CANONICAL: artifact is not canonical JSON"
                else
                    let expected, self, all = render inputs
                    if expected <> artifactBytes then
                        match tryProperty "source" observed, tryProperty "externalSteps" observed, tryProperty "scenarios" observed, tryProperty "selfSha256" observed with
                        | Some source, _, _, _ when source.GetRawText() <> (sourceNode inputs).ToJsonString() -> Error "FI-ARTIFACT-SOURCE: accepted protocol identity differs"
                        | _, Some steps, _, _ when steps.GetRawText() <> JsonSerializer.Serialize(inputs.ExternalSteps) -> Error "FI-STEP-INVENTORY: modeled external-step inventory differs"
                        | _, _, Some cases, _ when cases.GetArrayLength() <> all.Length -> Error "FI-SCENARIO-COUNT: fault matrix is incomplete"
                        | _, _, _, Some digest when digest.ValueKind = JsonValueKind.String && digest.GetString() <> self -> Error "FI-SELF-DIGEST: artifact self digest differs"
                        | _, _, Some cases, _ ->
                            let ids = cases.EnumerateArray() |> Seq.map (stringProperty "FI-SCENARIO-SHAPE" "id") |> Seq.toList
                            let expectedIds = all |> List.map (fun item -> Ok item.Id)
                            if ids <> expectedIds then Error "FI-SCENARIO-ORDER: scenario identity or order differs"
                            else Error "FI-SCENARIO-OUTCOME: convergence, refusal, or state identity differs"
                        | _ -> Error "FI-ARTIFACT-SHAPE: fault matrix differs"
                    else
                        let converged = all |> List.filter (fun item -> item.Outcome = "converged") |> List.length
                        Ok
                            { SourceSha256 = inputs.SourceSha256
                              BehavioralSha256 = inputs.BehavioralSha256
                              ContractSha256 = inputs.ContractSha256
                              ExternalStepCount = inputs.ExternalSteps.Length
                              ScenarioCount = all.Length
                              ConvergedCount = converged
                              RefusedCount = all.Length - converged
                              SelfSha256 = self }
        with :? JsonException as error -> Error $"FI-ARTIFACT-MALFORMED: %s{error.Message}"

let write (root: string) (outputPath: string) =
    match generate root with
    | Error error -> Error error
    | Ok bytes ->
        let fullPath = if Path.IsPathRooted outputPath then outputPath else combine root outputPath
        Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
        File.WriteAllBytes(fullPath, bytes)
        validate root bytes

let check (root: string) (artifactPath: string) =
    let fullPath = if Path.IsPathRooted artifactPath then artifactPath else combine root artifactPath
    if not (File.Exists fullPath) then Error $"FI-ARTIFACT-MISSING: %s{artifactPath}"
    else File.ReadAllBytes fullPath |> validate root
