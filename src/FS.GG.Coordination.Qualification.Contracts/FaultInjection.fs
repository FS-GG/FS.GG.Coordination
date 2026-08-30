module FS.GG.Coordination.Qualification.Contracts.FaultInjection

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

[<Literal>]
let private Schema = "fsgg.coordination.fault-injection/1"

[<RequireQualifiedAccess>]
type SubjectDefect =
    | None
    | SkipRetry
    | DuplicateIsApplied
    | PreserveArrivalOrder
    | AcceptPartialPage
    | IgnoreRateBudget
    | IgnorePermission
    | IgnoreRevision

type TraceEvent = { Ordinal: int; Kind: string; Step: string; Revision: int }

type Execution =
    { Id: string; Fault: string; Step: string; Outcome: string; RefusalCode: string option
      InitialStateSha256: string; FinalStateSha256: string; Trace: TraceEvent list }

type ValidationSummary =
    { SourceSha256: string; BehavioralSha256: string; ContractSha256: string
      ExternalStepCount: int; ScenarioCount: int; ConvergedCount: int; RefusedCount: int; SelfSha256: string }

type private Inputs =
    { SourceSha256: string; BehavioralSha256: string; ContractSha256: string
      SettingsSha256: string; MutationSha256: string; PermissionSha256: string
      ExternalSteps: string list; MutationOutcomes: Set<string>; ObservationOutcomes: Set<string>
      Permission: string }

type private State =
    { Revision: int; Applied: Set<string>; Events: Map<int, string>; NextEvent: int; Pages: Set<int>; TerminalPage: bool }

let private combine (root: string) (relative: string) = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
let private utf8 (value: string) = Encoding.UTF8.GetBytes value
let private sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let private stateSha state =
    String.concat "|"
        [ string state.Revision; state.Applied |> Set.toList |> String.concat ","
          state.Events |> Map.toList |> List.map (fun (ordinal, step) -> $"%d{ordinal}:%s{step}") |> String.concat ","
          state.Pages |> Set.toList |> List.map string |> String.concat ","; string state.TerminalPage ]
    |> utf8 |> sha256

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

let private readJson root relative =
    let path = combine root relative
    if not (File.Exists path) then Error $"FI-INPUT-MISSING: %s{relative}"
    else
        try
            let bytes = File.ReadAllBytes path
            use document = JsonDocument.Parse bytes
            Ok(bytes, document.RootElement.Clone())
        with :? JsonException as error -> Error $"FI-INPUT-MALFORMED: %s{relative}: %s{error.Message}"

let private fieldString name record =
    tryProperty "fields" record
    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
    |> Option.bind (fun fields ->
        fields.EnumerateArray()
        |> Seq.tryFind (fun field -> stringProperty "FI-INPUT-SHAPE" "name" field = Ok name))
    |> Option.bind (tryProperty "value")
    |> Option.bind (tryProperty "value")
    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
    |> Option.map _.GetString()

let private catalogueIds contract prefix =
    match arrayProperty "FI-INPUT-SHAPE" "catalogue" contract with
    | Error error -> Error error
    | Ok rows ->
        rows
        |> List.choose (fun row -> stringProperty "FI-INPUT-SHAPE" "id" row |> Result.toOption)
        |> List.filter _.StartsWith(prefix, StringComparison.Ordinal)
        |> Set.ofList
        |> Ok

let private loadInputs root =
    let settingsPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/settings-plans.json"
    let mutationPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/mutation-census.json"
    let permissionPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/permission-census.json"
    let contractPath = "src/FS.GG.Coordination.Protocol/Generated/contract.json"
    match readJson root settingsPath, readJson root mutationPath, readJson root permissionPath, readJson root contractPath with
    | Error error, _, _, _ | _, Error error, _, _ | _, _, Error error, _ | _, _, _, Error error -> Error error
    | Ok(settingsBytes, settings), Ok(mutationBytes, mutation), Ok(permissionBytes, permission), Ok(_, contract) ->
        let identities value =
            match stringProperty "FI-INPUT-IDENTITY" "sourceSha256" value,
                  stringProperty "FI-INPUT-IDENTITY" "behavioralSha256" value,
                  stringProperty "FI-INPUT-IDENTITY" "contractSha256" value with
            | Ok a, Ok b, Ok c -> Ok(a,b,c)
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
        match identities settings, identities mutation, identities permission with
        | Ok(source, behavioral, contractSha), Ok(ms, mb, mc), Ok(ps, pb, pc)
            when (source,behavioral,contractSha) = (ms,mb,mc) && (source,behavioral,contractSha) = (ps,pb,pc) ->
            let specification =
                tryProperty "content" settings |> Option.bind (tryProperty "specification") |> Option.bind (tryProperty "value")
            let permissions = tryProperty "content" permission |> Option.bind (tryProperty "requiredPermissions")
            match specification |> Option.bind (fieldString "phaseContract"), permissions, catalogueIds contract "MOUT-", catalogueIds contract "OBS-" with
            | Some phaseContract, Some permissionRows, Ok mutationOutcomes, Ok observationOutcomes ->
                let steps =
                    Text.RegularExpressions.Regex.Matches(phaseContract, "DSPH-[A-Za-z]+")
                    |> Seq.cast<Text.RegularExpressions.Match>
                    |> Seq.map _.Value |> Seq.distinct |> Seq.toList
                let permissionIds = permissionRows.EnumerateArray() |> Seq.map _.GetString() |> Seq.sort |> Seq.toList
                let requiredOutcomes = set [ "MOUT-RateLimited"; "MOUT-RevisionConflict" ]
                let requiredObservations = set [ "OBS-Incomplete"; "OBS-Unauthorized" ]
                if steps <> [ "DSPH-Inspect"; "DSPH-Plan"; "DSPH-Apply"; "DSPH-Verify" ] then
                    Error "FI-STEP-INVENTORY: accepted desired-state phase contract differs"
                elif not (Set.isSubset requiredOutcomes mutationOutcomes) || not (Set.isSubset requiredObservations observationOutcomes) then
                    Error "FI-OUTCOME-INVENTORY: accepted typed outcomes are incomplete"
                elif permissionIds.IsEmpty then Error "FI-PERMISSION-INVENTORY: accepted permission census is empty"
                else
                    Ok { SourceSha256=source; BehavioralSha256=behavioral; ContractSha256=contractSha
                         SettingsSha256=sha256 settingsBytes; MutationSha256=sha256 mutationBytes
                         PermissionSha256=sha256 permissionBytes; ExternalSteps=steps
                         MutationOutcomes=mutationOutcomes; ObservationOutcomes=observationOutcomes
                         Permission=List.head permissionIds }
            | None, _, _, _ -> Error "FI-STEP-INVENTORY: desired-state phaseContract is missing"
            | _, None, _, _ -> Error "FI-PERMISSION-INVENTORY: permission census is missing"
            | _, _, Error error, _ | _, _, _, Error error -> Error error
        | Ok _, Ok _, Ok _ -> Error "FI-INPUT-IDENTITY: compiled outputs do not share one accepted identity"
        | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error

let private initial = { Revision=1; Applied=Set.empty; Events=Map.empty; NextEvent=1; Pages=Set.empty; TerminalPage=false }

let private applyStep step state trace =
    if Set.contains step state.Applied then
        state, trace @ [{ Ordinal=trace.Length+1; Kind="idempotent"; Step=step; Revision=state.Revision }]
    else
        let next =
            { state with Revision=state.Revision+1; Applied=Set.add step state.Applied
                         Events=Map.add state.NextEvent step state.Events; NextEvent=state.NextEvent+1 }
        next, trace @ [{ Ordinal=trace.Length+1; Kind="applied"; Step=step; Revision=next.Revision }]

let private run inputs defect id fault target =
    let mutable state = initial
    let mutable trace = []
    let mutable refusal : string option = None
    let add kind step = trace <- trace @ [{ Ordinal=trace.Length+1; Kind=kind; Step=step; Revision=state.Revision }]
    let executeStep step =
        let shouldRefuse code = refusal <- Some code; add "refused" step
        if fault="partial-page" && step="DSPH-Inspect" then
            state <- { state with Pages=Set.singleton 1; TerminalPage=false }; add "page" step
            if defect = SubjectDefect.AcceptPartialPage then state <- { state with TerminalPage=true }
            else shouldRefuse "OBS-Incomplete"
        elif fault="rate-budget-exhausted" && step="DSPH-Plan" && defect <> SubjectDefect.IgnoreRateBudget then shouldRefuse "MOUT-RateLimited"
        elif fault="permission-revoked" && step="DSPH-Apply" && defect <> SubjectDefect.IgnorePermission then shouldRefuse "OBS-Unauthorized"
        elif fault="concurrent-revision" && step="DSPH-Apply" && defect <> SubjectDefect.IgnoreRevision then
            state <- { state with Revision=state.Revision+1 }; add "external-revision" step; shouldRefuse "MOUT-RevisionConflict"
        elif (fault="before-step" && step=target) then
            add "fault-before" step
            if defect <> SubjectDefect.SkipRetry then add "retry" step; let next, events = applyStep step state trace in state <- next; trace <- events
        elif (fault="after-step" && step=target) || (fault="lost-response" && step=target) then
            let next, events = applyStep step state trace in state <- next; trace <- events
            add "response-lost" step
            if defect <> SubjectDefect.SkipRetry then add "retry" step; let replayed, replayEvents = applyStep step state trace in state <- replayed; trace <- replayEvents
        else
            let next, events = applyStep step state trace in state <- next; trace <- events
    for step in inputs.ExternalSteps do if refusal.IsNone then executeStep step
    if refusal.IsNone && fault="duplicate-event" then
        add "duplicate-delivered" target
        let firstIdentity, firstStep = state.Events |> Map.toList |> List.head
        let arrivals = (state.Events |> Map.toList) @ [ firstIdentity, firstStep ]
        if defect=SubjectDefect.DuplicateIsApplied then
            state <- { state with Revision=state.Revision+1; Events=Map.add state.NextEvent firstStep state.Events; NextEvent=state.NextEvent+1 }
        else
            state <- { state with Events=arrivals |> List.fold (fun reduced (identity,step) -> Map.add identity step reduced) Map.empty }
            add "duplicate-discarded" target
    if refusal.IsNone && fault="reordered-events" then
        let reversed = state.Events |> Map.toList |> List.rev
        add "events-reversed" target
        if defect=SubjectDefect.PreserveArrivalOrder then
            state <- { state with Events=reversed |> List.mapi (fun index (_,step) -> index+1,step) |> Map.ofList }
        else
            state <- { state with Events=reversed |> List.sortBy fst |> Map.ofList }
            add "events-reduced-by-ordinal" target
    let outcome = if refusal.IsSome then "refused" else "converged"
    { Id=id; Fault=fault; Step=target; Outcome=outcome; RefusalCode=refusal
      InitialStateSha256=stateSha initial; FinalStateSha256=stateSha state; Trace=trace }

let execute root defect =
    loadInputs root
    |> Result.map (fun inputs ->
        [ for step in inputs.ExternalSteps do
              yield run inputs defect $"before/%s{step}" "before-step" step
              yield run inputs defect $"after/%s{step}" "after-step" step
          yield run inputs defect "lost-response" "lost-response" "DSPH-Apply"
          yield run inputs defect "duplicate-event" "duplicate-event" "DSPH-Apply"
          yield run inputs defect "reordered-events" "reordered-events" "DSPH-Apply"
          yield run inputs defect "partial-page" "partial-page" "DSPH-Inspect"
          yield run inputs defect "rate-budget-exhausted" "rate-budget-exhausted" "DSPH-Plan"
          yield run inputs defect "permission-revoked" "permission-revoked" inputs.Permission
          yield run inputs defect "concurrent-revision" "concurrent-revision" "MUT-Set" ])

let private sourceNode inputs =
    let node=JsonObject()
    for name,value in ["sourceSha256",inputs.SourceSha256;"behavioralSha256",inputs.BehavioralSha256;"contractSha256",inputs.ContractSha256;"settingsSha256",inputs.SettingsSha256;"mutationSha256",inputs.MutationSha256;"permissionSha256",inputs.PermissionSha256] do node[name] <- value
    node

let private traceNode event =
    let node = JsonObject()
    node["ordinal"] <- event.Ordinal
    node["kind"] <- event.Kind
    node["step"] <- event.Step
    node["revision"] <- event.Revision
    node

let private executionNode item =
    let node = JsonObject()
    node["id"] <- item.Id
    node["fault"] <- item.Fault
    node["step"] <- item.Step
    node["outcome"] <- item.Outcome
    node["refusalCode"] <- match item.RefusalCode with Some value -> JsonValue.Create(value) | None -> null
    node["initialStateSha256"] <- item.InitialStateSha256
    node["finalStateSha256"] <- item.FinalStateSha256
    node["trace"] <- JsonArray(item.Trace |> List.map (fun event -> traceNode event :> JsonNode) |> List.toArray)
    node

let private serialize (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented=false))+"\n" |> utf8

let private render inputs executions =
    let root = JsonObject()
    root["schema"] <- Schema
    root["source"] <- sourceNode inputs
    root["externalSteps"] <- JsonArray(inputs.ExternalSteps |> List.map (fun step -> JsonValue.Create(step):>JsonNode) |> List.toArray)
    root["executions"] <- JsonArray(executions |> List.map (fun item -> executionNode item :> JsonNode) |> List.toArray)
    let converged = executions |> List.filter (fun item -> item.Outcome="converged") |> List.length
    let counts = JsonObject()
    counts["externalSteps"] <- inputs.ExternalSteps.Length
    counts["scenarios"] <- executions.Length
    counts["converged"] <- converged
    counts["refused"] <- executions.Length-converged
    root["counts"] <- counts
    root["selfSha256"] <- ""
    let self = serialize root |> sha256
    root["selfSha256"] <- self
    serialize root,self

let generate root =
    match loadInputs root, execute root SubjectDefect.None with
    | Ok inputs, Ok executions -> render inputs executions |> fst |> Ok
    | Error error, _ | _, Error error -> Error error

let validate root (artifactBytes: byte array) =
    match loadInputs root, execute root SubjectDefect.None with
    | Error error, _ | _, Error error -> Error error
    | Ok inputs, Ok executions ->
        try
            use document=JsonDocument.Parse artifactBytes
            let observed=document.RootElement
            match stringProperty "FI-ARTIFACT-SCHEMA" "schema" observed with
            | Error error -> Error error
            | Ok schema when schema<>Schema -> Error "FI-ARTIFACT-SCHEMA: unsupported schema"
            | Ok _ ->
                let canonical=JsonNode.Parse(ReadOnlySpan<byte>(artifactBytes)).ToJsonString()+"\n" |> utf8
                if canonical<>artifactBytes then Error "FI-ARTIFACT-CANONICAL: artifact is not canonical JSON"
                else
                    let expected,self=render inputs executions
                    if expected<>artifactBytes then
                        match tryProperty "source" observed, tryProperty "externalSteps" observed, tryProperty "executions" observed, tryProperty "selfSha256" observed with
                        | Some source,_,_,_ when source.GetRawText()<>(sourceNode inputs).ToJsonString() -> Error "FI-ARTIFACT-SOURCE: accepted protocol identity differs"
                        | _,Some steps,_,_ when steps.GetRawText()<>JsonSerializer.Serialize(inputs.ExternalSteps) -> Error "FI-STEP-INVENTORY: external-step authority differs"
                        | _,_,Some cases,_ when cases.GetArrayLength()<>executions.Length -> Error "FI-SCENARIO-COUNT: executed fault matrix is incomplete"
                        | _,_,_,Some digest when digest.GetString()<>self -> Error "FI-SELF-DIGEST: artifact self digest differs"
                        | _,_,Some cases,_ ->
                            let ids=cases.EnumerateArray() |> Seq.map (stringProperty "FI-EXECUTION-SHAPE" "id") |> Seq.toList
                            if ids<>(executions |> List.map (fun item -> Ok item.Id)) then Error "FI-SCENARIO-ORDER: execution identity or order differs"
                            else Error "FI-EXECUTION-TRACE: executed outcome or retained trace differs"
                        | _ -> Error "FI-ARTIFACT-SHAPE: executed fault matrix differs"
                    else
                        let converged=executions |> List.filter (fun item -> item.Outcome="converged") |> List.length
                        Ok { SourceSha256=inputs.SourceSha256; BehavioralSha256=inputs.BehavioralSha256; ContractSha256=inputs.ContractSha256
                             ExternalStepCount=inputs.ExternalSteps.Length; ScenarioCount=executions.Length; ConvergedCount=converged
                             RefusedCount=executions.Length-converged; SelfSha256=self }
        with :? JsonException as error -> Error $"FI-ARTIFACT-MALFORMED: %s{error.Message}"

let write (root: string) (outputPath: string) =
    match generate root with
    | Error error -> Error error
    | Ok bytes -> let full=if Path.IsPathRooted outputPath then outputPath else combine root outputPath in Directory.CreateDirectory(Path.GetDirectoryName full)|>ignore; File.WriteAllBytes(full,bytes); validate root bytes

let check (root: string) (artifactPath: string) =
    let full=if Path.IsPathRooted artifactPath then artifactPath else combine root artifactPath
    if File.Exists full then validate root (File.ReadAllBytes full) else Error $"FI-ARTIFACT-MISSING: %s{artifactPath}"
