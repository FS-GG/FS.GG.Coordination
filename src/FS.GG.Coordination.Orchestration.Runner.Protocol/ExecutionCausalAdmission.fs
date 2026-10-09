namespace FS.GG.Coordination.Orchestration.Runner.Protocol

open System
open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type ExecutionDependencyDeclaration =
    { OriginalItemId: string
      InvocationId: string
      SourceReference: string }

[<CLIMutable>]
type ExecutionCausalDeclaration =
    { Purpose: string
      DependencyCoverage: string
      Dependencies: ExecutionDependencyDeclaration array
      RetryOfInvocationId: string }

/// Immutable dispatch metadata. Neither these bytes nor a telemetry receipt grant execution authority.
[<CLIMutable>]
type ExecutionCausalAdmission =
    { Schema: string
      OriginalItemId: string
      MemberItemId: string
      AssignmentId: Guid
      AttemptId: Guid
      Generation: int64
      InvocationId: string
      RootInvocationId: string
      ParentInvocationId: string
      ParentAttemptId: Nullable<Guid>
      ParentGeneration: Nullable<int64>
      RootAttemptId: Guid
      RootGeneration: int64
      Relation: string
      AdmittedAt: DateTimeOffset
      RootAdmittedAt: DateTimeOffset
      ClockProvenance: string
      Declaration: ExecutionCausalDeclaration }

[<RequireQualifiedAccess>]
module ExecutionCausalAdmission =
    let schema = "fsgg.orchestration.execution-causal-admission/1"
    let maximumBytes = 4096
    let private options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                                                MaxDepth = 8)
    let private text maximum (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= maximum
        && (value |> Seq.forall (Char.IsControl >> not))
    let invocationId (original: string) (attempt: Guid) (generation: int64) =
        "invocation-" + RunnerWire.sha256(System.Text.Encoding.UTF8.GetBytes(original + "\u001f" + attempt.ToString("N") + "\u001f" + string generation + "\u001finvocation"))
    let unclassified =
        { Purpose = "unclassified"; DependencyCoverage = "unknown"; Dependencies = [||]; RetryOfInvocationId = null }
    let private validDeclaration (value: ExecutionCausalDeclaration) parent =
        not (isNull (box value))
        && (set [ "planning"; "implementation"; "review"; "validation"; "delivery"; "repair"; "operations"; "other"; "unclassified" ]).Contains value.Purpose
        && (set [ "complete"; "partial"; "unknown" ]).Contains value.DependencyCoverage
        && not (isNull value.Dependencies) && value.Dependencies.Length <= 16
        && (value.Dependencies |> Array.forall (fun edge ->
            not (isNull (box edge)) && text 512 edge.OriginalItemId && text 128 edge.InvocationId && text 512 edge.SourceReference))
        && (value.Dependencies |> Array.map (fun edge -> edge.OriginalItemId, edge.InvocationId) |> Array.distinct |> Array.length) = value.Dependencies.Length
        && (isNull value.RetryOfInvocationId || (not (isNull parent) && value.RetryOfInvocationId = parent))
    let validate (value: ExecutionCausalAdmission) =
        if isNull (box value) then Error "causal-admission-missing"
        elif value.Schema <> schema || not (text 512 value.OriginalItemId) || not (text 512 value.MemberItemId)
             || value.AssignmentId = Guid.Empty || value.AttemptId = Guid.Empty || value.Generation < 0L
             || value.RootAttemptId = Guid.Empty || value.RootGeneration < 0L || value.RootGeneration > value.Generation
             || value.AdmittedAt = DateTimeOffset.MinValue || value.RootAdmittedAt > value.AdmittedAt
             || value.RootAdmittedAt = DateTimeOffset.MinValue || value.ClockProvenance <> "host-wall"
             || value.InvocationId <> invocationId value.OriginalItemId value.AttemptId value.Generation
             || value.RootInvocationId <> invocationId value.OriginalItemId value.RootAttemptId value.RootGeneration
             || (if value.Relation = "root" then
                    not (isNull value.ParentInvocationId) || value.ParentAttemptId.HasValue || value.ParentGeneration.HasValue || value.InvocationId <> value.RootInvocationId
                 else
                    not ((set [ "child"; "follow-up" ]).Contains value.Relation)
                    || not (text 128 value.ParentInvocationId) || value.Generation <= value.RootGeneration
                    || not value.ParentAttemptId.HasValue || not value.ParentGeneration.HasValue
                    || value.ParentAttemptId.Value = Guid.Empty || value.ParentGeneration.Value < value.RootGeneration
                    || value.ParentGeneration.Value >= value.Generation
                    || value.ParentInvocationId <> invocationId value.OriginalItemId value.ParentAttemptId.Value value.ParentGeneration.Value)
             || not (validDeclaration value.Declaration value.ParentInvocationId)
             || (value.Declaration.Dependencies |> Array.exists (fun edge -> edge.InvocationId = value.InvocationId)) then
            Error "causal-admission-invalid"
        else Ok value
    let encode (value: ExecutionCausalAdmission) = JsonSerializer.SerializeToUtf8Bytes(value, options)
    let private noDuplicateProperties (element: JsonElement) =
        let rec inspect (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                let properties = value.EnumerateObject() |> Seq.toArray
                (properties |> Array.map _.Name |> Array.distinct |> Array.length) = properties.Length
                && (properties |> Array.forall (fun property -> inspect property.Value))
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.forall inspect
            | _ -> true
        inspect element
    let parse (bytes: byte array) =
        if isNull bytes || bytes.Length = 0 || bytes.Length > maximumBytes then Error "causal-admission-size-refused"
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory bytes)
                if not (noDuplicateProperties document.RootElement) then Error "causal-admission-duplicate-property"
                else JsonSerializer.Deserialize<ExecutionCausalAdmission>(bytes, options) |> validate
            with :? JsonException -> Error "causal-admission-json-refused"
    let fromBase64 (value: string) =
        try
            if isNull value || value.Length > 5464 then Error "causal-admission-encoding-refused"
            else Convert.FromBase64String value |> parse
        with :? FormatException -> Error "causal-admission-encoding-refused"
    let create (original: string) (memberId: string) (assignment: Guid) (attempt: Guid) (generation: int64) (admittedAt: DateTimeOffset) (declaration: ExecutionCausalDeclaration) (parent: ExecutionCausalAdmission option) =
        let value =
            { Schema = schema; OriginalItemId = original; MemberItemId = memberId
              AssignmentId = assignment; AttemptId = attempt; Generation = generation
              InvocationId = invocationId original attempt generation
              RootInvocationId = parent |> Option.map _.RootInvocationId |> Option.defaultValue (invocationId original attempt generation)
              ParentInvocationId = parent |> Option.map _.InvocationId |> Option.toObj
              ParentAttemptId = parent |> Option.map _.AttemptId |> Option.toNullable
              ParentGeneration = parent |> Option.map _.Generation |> Option.toNullable
              RootAttemptId = parent |> Option.map _.RootAttemptId |> Option.defaultValue attempt
              RootGeneration = parent |> Option.map _.RootGeneration |> Option.defaultValue generation
              Relation = if parent.IsSome then "child" else "root"
              AdmittedAt = admittedAt; RootAdmittedAt = parent |> Option.map _.RootAdmittedAt |> Option.defaultValue admittedAt
              ClockProvenance = "host-wall"; Declaration = declaration }
        validate value |> Result.bind (fun accepted -> parse (encode accepted))

/// Existing ingest/1 fact kinds only; declarations stay in durable admission until receiver support exists.
[<RequireQualifiedAccess>]
module ExecutionAdmissionFacts =
    open System.Text.Json.Nodes
    let private hash value = RunnerWire.sha256(System.Text.Encoding.UTF8.GetBytes(value: string))
    let dispatchId original attempt generation =
        "dispatch-" + hash (original + "\u001f" + (attempt: Guid).ToString("N") + "\u001f" + string generation + "\u001fdispatch")
    let activationId original = "activation-" + hash (original + "\u001factivation")
    let prepare (admission: ExecutionCausalAdmission) requestedModel requestedEffort =
        ExecutionCausalAdmission.validate admission |> Result.bind (fun a ->
            let context = a.InvocationId
            let dispatch = dispatchId a.OriginalItemId a.AttemptId a.Generation
            let activation = activationId a.OriginalItemId
            let event kind identity =
                let value = JsonObject()
                value["kind"] <- JsonValue.Create(kind: string)
                value["identity"] <- JsonValue.Create(identity: string)
                value["itemId"] <- JsonValue.Create a.OriginalItemId
                value["revision"] <- JsonValue.Create 0
                value
            let optional (value: JsonObject) (key: string) (text: string option) = value[key] <- text |> Option.map JsonValue.Create |> Option.defaultValue null
            let expected = event "expected-dispatch" ("expected-dispatch-" + dispatch)
            expected["dispatchId"] <- JsonValue.Create dispatch
            expected["activationId"] <- JsonValue.Create activation
            expected["relation"] <- JsonValue.Create a.Relation
            optional expected "parentDispatchId" (if a.ParentAttemptId.HasValue then Some(dispatchId a.OriginalItemId a.ParentAttemptId.Value a.ParentGeneration.Value) else None)
            expected["runtime"] <- JsonValue.Create "codex-exec"
            expected["expectedAt"] <- JsonValue.Create(a.AdmittedAt.ToString("O"))
            expected["clockProvenance"] <- JsonValue.Create "host-wall"
            let lineage = event "invocation-lineage" ("invocation-lineage-" + context)
            lineage["dispatchId"] <- JsonValue.Create dispatch
            lineage["invocationId"] <- JsonValue.Create context
            lineage["relation"] <- JsonValue.Create a.Relation
            optional lineage "parentInvocationId" (Option.ofObj a.ParentInvocationId)
            lineage["rootInvocationId"] <- JsonValue.Create a.RootInvocationId
            lineage["runtime"] <- JsonValue.Create "codex-exec"
            let admissionFact = event "runtime-admission" ("runtime-admission-" + context)
            admissionFact["invocationId"] <- JsonValue.Create context
            admissionFact["featureId"] <- JsonValue.Create "coordination-orchestration"
            admissionFact["attemptId"] <- JsonValue.Create(a.AttemptId.ToString("N") + "-g" + string a.Generation)
            optional admissionFact "parentAttemptId" (if a.ParentAttemptId.HasValue then Some(a.ParentAttemptId.Value.ToString("N") + "-g" + string a.ParentGeneration.Value) else None)
            admissionFact["producerStream"] <- JsonValue.Create "coordination"
            optional admissionFact "requestedModel" requestedModel
            optional admissionFact "requestedEffort" requestedEffort
            admissionFact["backend"] <- null
            let time = event "event-time" ("event-time-" + context + "-admission")
            time["invocationId"] <- JsonValue.Create context
            time["event"] <- JsonValue.Create "admission"
            time["occurredAt"] <- JsonValue.Create(a.AdmittedAt.ToString("O"))
            time["occurredClockProvenance"] <- JsonValue.Create "host-wall"
            time["observedAt"] <- JsonValue.Create(a.AdmittedAt.ToString("O"))
            time["observedClockProvenance"] <- JsonValue.Create "host-wall"
            let activationFact = event "operational-activation" ("operational-activation-" + activation)
            activationFact["activationId"] <- JsonValue.Create activation
            activationFact["scope"] <- JsonValue.Create "explicit-future-dispatches"
            activationFact["runtime"] <- JsonValue.Create "codex-exec"
            activationFact["activatedAt"] <- JsonValue.Create(a.RootAdmittedAt.ToString("O"))
            activationFact["clockProvenance"] <- JsonValue.Create "host-wall"
            activationFact["lateAfterSeconds"] <- JsonValue.Create 3600
            let events = if a.Relation = "root" then [ activationFact; expected; lineage; admissionFact; time ] else [ expected; lineage; admissionFact; time ]
            let digest = events |> List.map (fun value -> value["identity"].GetValue<string>()) |> String.concat "\u001f" |> hash
            let batch = JsonObject()
            batch["schema"] <- JsonValue.Create "fsgg.telemetry.ingest/1"
            batch["ingestId"] <- JsonValue.Create("batch-" + digest)
            batch["sourceIdentity"] <- JsonValue.Create "coordination"
            batch["generation"] <- JsonValue.Create context
            batch["cursor"] <- JsonValue.Create digest
            batch["eventCount"] <- JsonValue.Create events.Length
            let payload = JsonArray()
            events |> List.iter payload.Add
            batch["events"] <- payload
            Ok("batch-" + digest, System.Text.Encoding.UTF8.GetBytes(batch.ToJsonString(JsonSerializerOptions(WriteIndented = false)))))
