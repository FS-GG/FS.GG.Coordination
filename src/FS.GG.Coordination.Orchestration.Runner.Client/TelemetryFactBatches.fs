namespace FS.GG.Coordination.Orchestration.Runner.Client

#nowarn "3391"

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Orchestration.Execution.Codex
open FS.GG.Coordination.Orchestration.Runner.Protocol

type TelemetryInvocation =
    {
        ItemId: string
        AttemptId: string
        ActivationId: string
        DispatchId: string
        InvocationId: string
    }

[<RequireQualifiedAccess>]
module TelemetryFactBatches =
    let private hash (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let rootInvocation (command: ExecutorCommandV2) =
        let key =
            command.WorkItemPersistenceId
            + "\u001f"
            + command.AttemptId.ToString("N")
            + "\u001f"
            + string command.Generation

        {
            ItemId = command.WorkItemPersistenceId
            AttemptId = command.AttemptId.ToString("N") + "-g" + string command.Generation
            ActivationId = "activation-" + hash (command.WorkItemPersistenceId + "\u001factivation")
            DispatchId = "dispatch-" + hash (key + "\u001fdispatch")
            InvocationId = "invocation-" + hash (key + "\u001finvocation")
        }

    let private optional (event: JsonObject) (key: string) (value: string option) =
        event[key] <-
            match value with
            | Some text -> JsonValue.Create text
            | None -> null

    let private event (kind: string) (identity: string) (context: TelemetryInvocation) =
        let value = JsonObject()
        value["kind"] <- kind
        value["identity"] <- identity
        value["itemId"] <- context.ItemId
        value["revision"] <- 0
        value

    let private batch (context: TelemetryInvocation) (events: JsonObject list) =
        let identities = events |> List.map (fun event -> event["identity"].GetValue<string>())
        let digest = hash (String.concat "\u001f" identities)
        let root = JsonObject()
        root["schema"] <- "fsgg.telemetry.ingest/1"
        root["ingestId"] <- "batch-" + digest
        root["sourceIdentity"] <- "coordination"
        root["generation"] <- context.InvocationId
        root["cursor"] <- digest
        root["eventCount"] <- events.Length
        let payload = JsonArray()
        events |> List.iter payload.Add
        root["events"] <- payload
        "batch-" + digest, Encoding.UTF8.GetBytes(root.ToJsonString(JsonSerializerOptions(WriteIndented = false)))

    let prospectiveRoot (command: ExecutorCommandV2) observedAt =
        let context = rootInvocation command
        let timestamp = (observedAt: DateTimeOffset).ToString("O")

        let activation = event "operational-activation" ("operational-activation-" + context.ActivationId) context
        activation["activationId"] <- context.ActivationId
        activation["scope"] <- "explicit-future-dispatches"
        activation["runtime"] <- "codex-exec"
        activation["activatedAt"] <- timestamp
        activation["clockProvenance"] <- "host-wall"
        activation["lateAfterSeconds"] <- 3600

        let expected = event "expected-dispatch" ("expected-dispatch-" + context.DispatchId) context
        expected["dispatchId"] <- context.DispatchId
        expected["activationId"] <- context.ActivationId
        expected["relation"] <- "root"
        expected["parentDispatchId"] <- null
        expected["runtime"] <- "codex-exec"
        expected["expectedAt"] <- timestamp
        expected["clockProvenance"] <- "host-wall"

        let lineage = event "invocation-lineage" ("invocation-lineage-" + context.InvocationId) context
        lineage["dispatchId"] <- context.DispatchId
        lineage["invocationId"] <- context.InvocationId
        lineage["relation"] <- "root"
        lineage["parentInvocationId"] <- null
        lineage["rootInvocationId"] <- context.InvocationId
        lineage["runtime"] <- "codex-exec"

        let admission = event "runtime-admission" ("runtime-admission-" + context.InvocationId) context
        admission["invocationId"] <- context.InvocationId
        admission["featureId"] <- "coordination-orchestration"
        admission["attemptId"] <- context.AttemptId
        admission["parentAttemptId"] <- null
        admission["producerStream"] <- "coordination"
        optional admission "requestedModel" (Option.ofObj command.RequestedModel)
        optional admission "requestedEffort" (Option.ofObj command.RequestedEffort)
        admission["backend"] <- null

        let admissionTime = event "event-time" ("event-time-" + context.InvocationId + "-admission") context
        admissionTime["invocationId"] <- context.InvocationId
        admissionTime["event"] <- "admission"
        admissionTime["occurredAt"] <- timestamp
        admissionTime["occurredClockProvenance"] <- "host-wall"
        admissionTime["observedAt"] <- timestamp
        admissionTime["observedClockProvenance"] <- "host-wall"

        batch context [ activation; expected; lineage; admission; admissionTime ]

    let completedTurn (context: TelemetryInvocation) requestedModel requestedEffort (turn: CodexTurnUsage) =
        let nativeKey = turn.TurnId |> Option.defaultValue (string turn.TurnSequence)
        let identity = "runtime-usage-" + hash (context.InvocationId + "\u001f" + turn.ThreadId + "\u001f" + nativeKey)
        let usage = event "runtime-turn-usage" identity context
        usage["invocationId"] <- context.InvocationId
        usage["threadId"] <- turn.ThreadId
        optional usage "turnId" turn.TurnId
        usage["turnSequence"] <- turn.TurnSequence
        usage["provider"] <- null
        optional usage "requestedModel" requestedModel
        usage["observedModel"] <- null
        optional usage "requestedEffort" requestedEffort
        usage["observedEffort"] <- null
        usage["backend"] <- null
        usage["scope"] <- "completed-turn"
        usage["provenance"] <- "codex-exec-jsonl"
        usage["input"] <- turn.Input
        usage["cachedInput"] <- turn.CachedInput
        usage["output"] <- turn.Output
        usage["reasoning"] <- turn.Reasoning |> Option.map JsonValue.Create |> Option.defaultValue null
        usage["total"] <- turn.Total
        batch context [ usage ]

    let gap (context: TelemetryInvocation) gapId code =
        let identity = "runtime-gap-" + hash (context.InvocationId + "\u001f" + gapId + "\u001f" + code)
        let value = event "runtime-gap" identity context
        value["invocationId"] <- context.InvocationId
        value["code"] <- code
        batch context [ value ]

    let processStart (context: TelemetryInvocation) (processId: int) (at: DateTimeOffset) =
        let started = event "runtime-start" ("runtime-process-" + context.InvocationId) context
        started["invocationId"] <- context.InvocationId
        started["threadId"] <- null
        started["turnId"] <- null
        started["turnSequence"] <- null
        started["processId"] <- processId
        started["phase"] <- "process"

        let timestamp = at.ToString("O")
        let timing = event "event-time" ("event-time-" + context.InvocationId + "-start") context
        timing["invocationId"] <- context.InvocationId
        timing["event"] <- "start"
        timing["occurredAt"] <- timestamp
        timing["occurredClockProvenance"] <- "host-wall"
        timing["observedAt"] <- timestamp
        timing["observedClockProvenance"] <- "host-wall"
        batch context [ started; timing ]

    let processTerminal (context: TelemetryInvocation) exitCode threadId (at: DateTimeOffset) =
        let terminal = event "runtime-terminal" ("runtime-terminal-" + context.InvocationId) context
        terminal["invocationId"] <- context.InvocationId
        optional terminal "threadId" threadId
        terminal["outcome"] <-
            if exitCode = 0 then "completed"
            elif List.contains exitCode [ 130; 137; 143 ] then "cancelled"
            else "failed"
        terminal["exitCode"] <- exitCode

        let timestamp = at.ToString("O")
        let timing = event "event-time" ("event-time-" + context.InvocationId + "-terminal") context
        timing["invocationId"] <- context.InvocationId
        timing["event"] <- "terminal"
        timing["occurredAt"] <- timestamp
        timing["occurredClockProvenance"] <- "host-wall"
        timing["observedAt"] <- timestamp
        timing["observedClockProvenance"] <- "host-wall"
        batch context [ terminal; timing ]
