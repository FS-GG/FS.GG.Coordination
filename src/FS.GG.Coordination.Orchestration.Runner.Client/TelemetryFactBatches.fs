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

    let private batch (context: TelemetryInvocation) (event: JsonObject) =
        let identity = event["identity"].GetValue<string>()
        let digest = hash identity
        let root = JsonObject()
        root["schema"] <- "fsgg.telemetry.ingest/1"
        root["ingestId"] <- "batch-" + digest
        root["sourceIdentity"] <- "coordination"
        root["generation"] <- context.InvocationId
        root["cursor"] <- digest
        root["eventCount"] <- 1
        let events = JsonArray()
        events.Add event
        root["events"] <- events
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

        [ activation; expected; lineage; admission; admissionTime ] |> List.map (batch context)

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
        batch context usage
