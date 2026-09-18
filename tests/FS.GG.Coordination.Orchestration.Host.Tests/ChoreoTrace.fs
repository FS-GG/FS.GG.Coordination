namespace FS.GG.Coordination.Orchestration.Host.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FsQuint

type ChoreoSnapshot =
    {
        Stage: int
        OperationId: string
        DurableStatus: string
        Paused: bool
        JournalRecovered: bool
        AuthorityFresh: bool
        ResumeAuthenticated: bool
        UnknownObserved: bool
        RetryObserved: bool
        DuplicateRejected: bool
        StaleRejected: bool
        IdentityRejected: bool
        SequenceRejected: bool
        RestartObserved: bool
    }

type ChoreoMilestone =
    {
        StateIndex: int
        Action: string
        QuintAction: string
        Participant: string
        Message: string
        Snapshot: ChoreoSnapshot
    }

type ChoreoScenario =
    {
        Id: string
        QuintTest: string
        File: string
        StateCount: int
        TraceSha256: string
        Invariant: string
        Milestones: ChoreoMilestone list
        Replay: QuintReplayTrace
    }

[<RequireQualifiedAccess>]
module ChoreoTrace =
    let private manifestSchema = "fsgg.quint.choreo-trace-manifest/1"
    let private observableSchema = "fsgg.coordination.hosted-writer-observation/1"
    let private rawVariable = "O2HostedWriterChoreoModel::choreo::s"

    let private canonicalSource =
        "src/FS.GG.Coordination.Protocol/Protocol.md#O2HostedWriterChoreoTests"

    let private quintSha =
        "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"

    let private choreoCommit = "000cf4eed315187dc6f216a148781cff7dde6521"

    let private sha256Bytes (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private sha256Text (value: string) =
        value |> Encoding.UTF8.GetBytes |> sha256Bytes

    let private sha256File path = File.ReadAllBytes path |> sha256Bytes

    let private exactProperties context expected (value: JsonObject) =
        let actual = value |> Seq.map _.Key |> Set.ofSeq
        let expectedSet = Set.ofList expected

        if actual <> expectedSet then
            failwith $"{context} properties differ: expected %A{expectedSet}, actual %A{actual}"

    let private text (value: JsonObject) (name: string) = value[name].GetValue<string>()
    let private integer (value: JsonObject) (name: string) = value[name].GetValue<int>()

    let private fixtureRoot () =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Choreo")

    let private protocolPath () =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol.md")

    let private mapValue name (value: JsonObject) =
        value["#map"].AsArray()
        |> Seq.map _.AsArray()
        |> Seq.tryFind (fun pair -> pair[0].GetValue<string>() = name)
        |> Option.map (fun pair -> pair[1].AsObject())
        |> Option.defaultWith (fun () -> failwith $"raw Choreo map entry {name} is missing")

    let private tag (value: JsonObject) = text value "tag"

    let private bigint (value: JsonObject) (name: string) =
        let encoded = value[name].AsObject()
        encoded["#bigint"].GetValue<string>()

    let private validateOperation context (optionValue: JsonObject) =
        match tag optionValue with
        | "None" -> ()
        | "Some" ->
            let operation = optionValue["value"].AsObject()
            let operationId = text operation "operation"

            if
                text operation "route" <> "route-1"
                || text operation "attempt" <> "attempt-1"
                || text operation "candidate" <> "candidate-1"
                || text operation "repository" <> "repository-1"
                || text operation "session" <> "session-1"
                || bigint operation "generation" <> "1"
                || not (Set.ofList [ "1"; "2" ] |> Set.contains (bigint operation "revision"))
                || not (
                    Set.ofList
                        [
                            "op-claim"
                            "op-process"
                            "op-candidate"
                            "op-branch"
                            "op-pr"
                            "op-merge"
                            "op-readback"
                        ]
                    |> Set.contains operationId
                )
            then
                failwith $"{context} operation envelope differs"
        | value -> failwith $"{context} option tag {value} is invalid"

    let private localState processName (state: JsonObject) =
        let globalState = state[rawVariable].AsObject()
        let system = globalState["system"].AsObject()
        let processState = mapValue processName system
        let local = processState["local"].AsObject()
        local["value"].AsObject()

    let private snapshot (state: JsonObject) =
        let host = localState "Host" state
        let journal = localState "Journal" state
        let current = host["current"].AsObject()
        validateOperation "Host current" current
        validateOperation "Journal current" (journal["current"].AsObject())

        let operationId =
            if tag current = "Some" then
                text (current["value"].AsObject()) "operation"
            else
                ""

        let completed =
            let completedSet = host["completed"].AsObject()
            completedSet["#set"].AsArray() |> Seq.length

        {
            Stage = completed
            OperationId = operationId
            DurableStatus = journal["status"].AsObject() |> tag
            Paused = host["paused"].GetValue<bool>()
            JournalRecovered = host["journalRecovered"].GetValue<bool>()
            AuthorityFresh = host["authorityFresh"].GetValue<bool>()
            ResumeAuthenticated = host["resumeAuthenticated"].GetValue<bool>()
            UnknownObserved = host["unknownObserved"].GetValue<bool>()
            RetryObserved = host["retryObserved"].GetValue<bool>()
            DuplicateRejected = host["duplicateRejected"].GetValue<bool>()
            StaleRejected = host["staleRejected"].GetValue<bool>()
            IdentityRejected = host["identityRejected"].GetValue<bool>()
            SequenceRejected = host["sequenceRejected"].GetValue<bool>()
            RestartObserved = host["restartObserved"].GetValue<bool>()
        }

    let private validateRawRoot id (root: JsonObject) =
        exactProperties "ITF root" [ "#meta"; "vars"; "states" ] root
        let metadata = root["#meta"].AsObject()
        exactProperties "ITF metadata" [ "format"; "format-description"; "source"; "status" ] metadata

        if
            text metadata "format" <> "ITF"
            || text metadata "status" <> "ok"
            || text metadata "source" <> canonicalSource
        then
            failwith $"{id} normalized ITF metadata differs"

        let vars = root["vars"].AsArray()

        if vars.Count <> 1 || vars[0].GetValue<string>() <> rawVariable then
            failwith $"{id} raw Quint variable differs"

        let states = root["states"].AsArray()

        states
        |> Seq.iteri (fun index state ->
            let stateObject = state.AsObject()
            exactProperties "ITF state" [ "#meta"; rawVariable ] stateObject
            let stateMetadata = stateObject["#meta"].AsObject()

            if stateMetadata["index"].GetValue<int>() <> index then
                failwith $"{id} state index {index} differs"

            snapshot stateObject |> ignore)

        states

    let private readRaw (bytes: byte array) =
        // FsQuint owns generic ITF limits and value validation. The guards below
        // additionally enforce this model's exact provenance and operation policy.
        match Itf.read Itf.defaultLimits bytes with
        | Error diagnostics -> failwith $"invalid raw ITF: %A{diagnostics}"
        | Ok _ -> JsonNode.Parse(bytes).AsObject()

    let validateRawText (textValue: string) =
        UTF8Encoding(false, true).GetBytes(textValue)
        |> readRaw
        |> validateRawRoot "untrusted"
        |> ignore

    let private integerValue value = QuintReplayValue.Integer(string value)
    let private booleanValue value = QuintReplayValue.Boolean value
    let private textValue value = QuintReplayValue.Text value

    let private projectedState (state: ChoreoSnapshot) =
        let progressed threshold = state.Stage >= threshold

        let operationStatus =
            if state.OperationId = "" then
                "none"
            else
                match state.DurableStatus with
                | "Intent" -> "intent"
                | "Dispatching" -> "dispatching"
                | "Unknown" -> "unknown"
                | "ProvenAbsent" -> "proven-absent"
                | value -> value.ToLowerInvariant()

        let restarted = state.Paused || state.RestartObserved
        let freshReadback = restarted && state.AuthorityFresh

        let draft: QuintReplayState =
            {
                Identity = String.replicate 64 "0"
                Bindings =
                    [
                        "state",
                        QuintReplayValue.Record
                            [
                                "activeAssignments",
                                integerValue (
                                    if state.Stage = 7 || (state.Paused && state.OperationId = "") then
                                        0
                                    else
                                        1
                                )
                                "adapterClaimedComplete", booleanValue false
                                "attemptId", textValue "attempt-1"
                                "branchPublished", booleanValue (progressed 4)
                                "budgetLimit", integerValue 1
                                "budgetUsed", integerValue (if state.Paused && state.Stage = 0 then 0 else 1)
                                "candidateDurable", booleanValue (progressed 3)
                                "candidateId", textValue "candidate-1"
                                "capacity", integerValue 1
                                "claimCurrent", booleanValue (progressed 1)
                                "currentGeneration", integerValue 1
                                "deliveryOpen", booleanValue true
                                "evidenceAttemptId", textValue (if state.Stage > 0 then "attempt-1" else "")
                                "evidenceCandidateId", textValue (if state.Stage > 0 then "candidate-1" else "")
                                "evidenceGeneration", integerValue (if state.Stage > 0 then 1 else 0)
                                "evidenceRepositoryId", textValue (if state.Stage > 0 then "repository-1" else "")
                                "evidenceRouteId", textValue (if state.Stage > 0 then "route-1" else "")
                                "executionOpen", booleanValue true
                                "freshReadback", booleanValue freshReadback
                                "jobClass", textValue "routine-documentation-delivery"
                                "mergeObserved", booleanValue (progressed 6)
                                "nativeReadbackObserved", booleanValue (progressed 7)
                                "operationId", textValue state.OperationId
                                "operationStatus", textValue operationStatus
                                "paused", booleanValue state.Paused
                                "permitGeneration", integerValue 1
                                "pullRequestObserved", booleanValue (progressed 5)
                                "readbackCurrent", booleanValue state.AuthorityFresh
                                "repositoryId", textValue "repository-1"
                                "restarted", booleanValue restarted
                                "routeId", textValue "route-1"
                                "sameOperationRetried", booleanValue state.RetryObserved
                                "stage", integerValue state.Stage
                                "subjectId", textValue "MDU6SXNzdWUx"
                                "unknownObserved", booleanValue state.UnknownObserved
                            ]
                    ]
            }

        { draft with
            Identity =
                QuintReplay.stateFingerprint draft
                |> Result.defaultWith (fun error -> failwith $"projected state fingerprint failed: %A{error}")
        }

    let private sourceBinding quintAction =
        let lines = File.ReadAllLines(protocolPath ())

        let moduleStart =
            lines
            |> Array.findIndex (fun line -> line.Trim() = "module O2HostedWriterChoreoModel {")

        let moduleEnd =
            lines
            |> Array.findIndex (fun line -> line.Trim() = "module O2HostedWriterChoreoTests {")

        let matches =
            lines
            |> Array.indexed
            |> Array.filter (fun (index, line) ->
                index > moduleStart
                && index < moduleEnd
                && (line.TrimStart().StartsWith($"action {quintAction}", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith($"pure def {quintAction}(", StringComparison.Ordinal)))

        match matches with
        | [| index, _ |] ->
            {
                Path = "src/FS.GG.Coordination.Protocol/Protocol.md"
                Line = index + 1
                Column = 3
            }
        | _ -> failwith $"expected one Choreo source definition for {quintAction}, got {matches.Length}"

    let private buildReplay sourceSha scenarioId stateCount (milestones: ChoreoMilestone list) =
        let initial = milestones.Head.Snapshot |> projectedState

        let environment: QuintReplayEnvironment =
            {
                Seed = "deterministic-test-action"
                Bounds = [ "rawStates", int64 stateCount ]
                ToolFingerprint = quintSha
                ProfileFingerprint = sha256Text observableSchema
                ContractFingerprint = sourceSha
                AdapterFingerprint = sha256Text "akka-hosted-writer/1"
                ImplementationFingerprint = sha256Text "FS.GG.Coordination.Orchestration.Host"
            }

        let steps =
            milestones.Tail
            |> List.mapi (fun index milestone ->
                {
                    Index = index + 1
                    Action = milestone.Action
                    Source = sourceBinding milestone.QuintAction
                    Expected = projectedState milestone.Snapshot
                })

        let draft: QuintReplayTrace =
            {
                SchemaVersion = 1
                TraceIdentity = String.replicate 64 "0"
                Environment = environment
                Initial = initial
                Steps = steps
            }

        { draft with
            TraceIdentity =
                QuintReplay.traceFingerprint draft
                |> Result.defaultWith (fun error -> failwith $"trace fingerprint failed for {scenarioId}: %A{error}")
        }

    let private parseMilestone (states: JsonArray) (node: JsonNode) =
        let value = node.AsObject()
        exactProperties "milestone" [ "stateIndex"; "action"; "quintAction"; "participant"; "message" ] value
        let stateIndex = integer value "stateIndex"

        if stateIndex < 0 || stateIndex >= states.Count then
            failwith $"milestone state index {stateIndex} is outside the raw trace"

        {
            StateIndex = stateIndex
            Action = text value "action"
            QuintAction = text value "quintAction"
            Participant = text value "participant"
            Message = text value "message"
            Snapshot = states[stateIndex].AsObject() |> snapshot
        }

    let private parseScenario sourceSha (value: JsonObject) =
        exactProperties
            "scenario"
            [
                "id"
                "quintTest"
                "file"
                "stateCount"
                "traceSha256"
                "invariant"
                "milestones"
            ]
            value

        let id = text value "id"
        let file = text value "file"
        let tracePath = Path.Combine(fixtureRoot (), file)

        if sha256File tracePath <> text value "traceSha256" then
            failwith $"{id} trace digest differs"

        let root = File.ReadAllBytes tracePath |> readRaw
        let states = validateRawRoot id root
        let stateCount = integer value "stateCount"

        if states.Count <> stateCount then
            failwith $"{id} expected {stateCount} states, got {states.Count}"

        let milestones =
            value["milestones"].AsArray() |> Seq.map (parseMilestone states) |> List.ofSeq

        if
            milestones.IsEmpty
            || milestones.Head.StateIndex <> 0
            || milestones.Head.Action <> "init"
        then
            failwith $"{id} must begin at raw state zero with init"

        let indices = milestones |> List.map _.StateIndex

        if indices <> (indices |> List.distinct |> List.sort) then
            failwith $"{id} milestone indices are not strictly increasing"

        {
            Id = id
            QuintTest = text value "quintTest"
            File = file
            StateCount = stateCount
            TraceSha256 = text value "traceSha256"
            Invariant = text value "invariant"
            Milestones = milestones
            Replay = buildReplay sourceSha id stateCount milestones
        }

    let loadAll () =
        let manifestPath = Path.Combine(fixtureRoot (), "manifest.json")
        let root = JsonNode.Parse(File.ReadAllBytes manifestPath).AsObject()

        exactProperties
            "manifest"
            [
                "schema"
                "model"
                "observableSchema"
                "source"
                "quint"
                "choreo"
                "command"
                "normalization"
                "rawVariable"
                "scenarios"
            ]
            root

        if text root "schema" <> manifestSchema then
            failwith "Choreo trace manifest schema differs"

        if text root "model" <> "O2HostedWriterChoreoModel" then
            failwith "Choreo trace model differs"

        if text root "observableSchema" <> observableSchema then
            failwith "Choreo observable schema differs"

        if text root "rawVariable" <> rawVariable then
            failwith "Choreo raw variable differs"

        let source = root["source"].AsObject()
        exactProperties "manifest source" [ "path"; "commit"; "sha256" ] source
        let sourceSha = text source "sha256"

        if text source "path" <> "src/FS.GG.Coordination.Protocol/Protocol.md" then
            failwith "source path differs"

        if text source "commit" <> "e1ff2a32649a180121c756f762d174e9c74f6620" then
            failwith "source commit differs"

        if sha256File (protocolPath ()) <> sourceSha then
            failwith "protocol source digest differs"

        let quint = root["quint"].AsObject()
        exactProperties "manifest Quint" [ "version"; "binarySha256"; "backend"; "maxSamples" ] quint

        if text quint "version" <> "0.32.0" || text quint "binarySha256" <> quintSha then
            failwith "Quint toolchain identity differs"

        if text quint "backend" <> "rust" || integer quint "maxSamples" <> 1 then
            failwith "Quint execution profile differs"

        let choreo = root["choreo"].AsObject()
        exactProperties "manifest Choreo" [ "repository"; "commit" ] choreo

        if
            text choreo "repository" <> "https://github.com/quint-co/choreo"
            || text choreo "commit" <> choreoCommit
        then
            failwith "Choreo source identity differs"

        let scenarios =
            root["scenarios"].AsArray()
            |> Seq.map (fun item -> parseScenario sourceSha (item.AsObject()))
            |> List.ofSeq

        if
            scenarios.Length <> 8
            || (scenarios |> List.map _.Id |> List.distinct |> List.length) <> 8
        then
            failwith "expected eight distinct deterministic Choreo scenarios"

        scenarios

    let load id =
        loadAll ()
        |> List.tryFind (fun scenario -> scenario.Id = id)
        |> Option.defaultWith (fun () -> failwith $"Choreo scenario {id} is missing")
