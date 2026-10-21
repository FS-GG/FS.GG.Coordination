namespace FS.GG.Coordination.Orchestration.Runner.Protocol

open System
open System.Text.Json
open System.Text.Json.Serialization

/// Closed transport projection; the full independently checked evidence travels as content.
[<CLIMutable>]
type OrdinaryAssignmentArtifact = {
    Schema: string; AssignmentSha256: string; EvidenceBase64: string
    WorkItemPersistenceId: string; Principal: string; AssignmentId: Guid; AttemptId: Guid
    Generation: int64; ReservationId: Guid; RecordedAt: DateTimeOffset; Deadline: DateTimeOffset
    MaximumRuntimeSeconds: int64; MaximumAttempts: int; InputSha256: string
    Workspace: string; RepositoryBinding: string; BaselineObjectId: string
    AllowedPaths: string array; Validations: string array; Model: string; Effort: string
    Context: string array; Tools: string array; Effects: string array
    Outputs: string array; Reviews: string array; AcceptanceOwner: string }

[<CLIMutable>]
type OrdinaryExecutorEnvelope = {
    Schema: string; Digest: string; PayloadBase64: string
    AssignmentSha256: string; ArtifactSha256: string }

[<RequireQualifiedAccess>]
module OrdinaryAssignmentWire =
    let artifactSchema = "fsgg.orchestration.ordinary-assignment/1"
    let routeSchema = "fsgg.orchestration.executor-route-binding/4"
    let commandSchema = "fsgg.orchestration.executor-command/6"
    let maximumArtifactBytes = 2 * 1024 * 1024
    let private options = JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase, MaxDepth=64,
                                               UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow)
    let encode<'T> (value: 'T) = JsonSerializer.SerializeToUtf8Bytes(value, options)
    let private exact (names: Set<string>) (root: JsonElement) =
        if root.ValueKind<>JsonValueKind.Object then false
        else
            let fields = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
            fields.Length=names.Count && Set.ofArray fields=names
    let private unique (root: JsonElement) =
        let rec loop (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                let fields=value.EnumerateObject() |> Seq.toArray
                fields.Length=(fields |> Array.map _.Name |> Array.distinct |> Array.length)
                && (fields |> Array.forall (fun p -> loop p.Value))
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.forall loop
            | _ -> true
        loop root
    let decode<'T> names maximum (bytes: byte array) =
        if isNull bytes || bytes.Length=0 || bytes.Length>maximum then Error "ordinary-wire-size-refused"
        else
            try
                use doc=JsonDocument.Parse(ReadOnlyMemory bytes, JsonDocumentOptions(MaxDepth=64))
                if not (exact names doc.RootElement && unique doc.RootElement) then Error "ordinary-wire-shape-refused"
                else Ok(JsonSerializer.Deserialize<'T>(bytes, options))
            with :? JsonException -> Error "ordinary-wire-json-refused"
    let private artifactFields =
        set ["schema";"assignmentSha256";"evidenceBase64";"workItemPersistenceId";"principal";"assignmentId";"attemptId";
             "generation";"reservationId";"recordedAt";"deadline";"maximumRuntimeSeconds";"maximumAttempts";"inputSha256";
             "workspace";"repositoryBinding";"baselineObjectId";"allowedPaths";"validations";"model";"effort";
             "context";"tools";"effects";"outputs";"reviews";"acceptanceOwner"]
    let parseArtifact bytes =
        decode<OrdinaryAssignmentArtifact> artifactFields maximumArtifactBytes bytes
        |> Result.bind (fun value ->
            let items (v: string array) = not(isNull v) && v.Length<=256 &&
                                          (v |> Array.forall (fun s -> not(String.IsNullOrWhiteSpace s) && s.Length<=4096))
                                          && (Set.ofArray v |> Set.count) = v.Length
            if value.Schema<>artifactSchema || not(RunnerWire.validSha256 value.AssignmentSha256)
               || not(RunnerWire.validSha256 value.InputSha256) || String.IsNullOrWhiteSpace value.EvidenceBase64
               || value.AssignmentId=Guid.Empty || value.AttemptId=Guid.Empty || value.ReservationId=Guid.Empty
               || value.Generation<0L || value.MaximumAttempts<>1 || value.MaximumRuntimeSeconds<=0L
               || value.Deadline<=value.RecordedAt || String.IsNullOrWhiteSpace value.Principal
               || String.IsNullOrWhiteSpace value.AcceptanceOwner
               || String.IsNullOrWhiteSpace value.Model || String.IsNullOrWhiteSpace value.Effort
               || String.IsNullOrWhiteSpace value.Workspace || String.IsNullOrWhiteSpace value.RepositoryBinding
               || String.IsNullOrWhiteSpace value.WorkItemPersistenceId
               || value.Model.Length>128 || value.Effort.Length>64 || value.Principal.Length>128
               || value.AcceptanceOwner.Length>256 || value.WorkItemPersistenceId.Length>512
               || not ([value.AllowedPaths;value.Validations;value.Context;value.Tools;value.Effects;value.Outputs;value.Reviews] |> List.forall items)
            then Error "ordinary-artifact-refused" else Ok value)
    let reference (bytes: byte array) =
      if isNull bytes || bytes.Length=0 || bytes.Length>32*1024 then Error "ordinary-control-size-refused"
      else
        try
            use doc=JsonDocument.Parse(ReadOnlyMemory bytes)
            let mutable schema=Unchecked.defaultof<JsonElement>
            if doc.RootElement.TryGetProperty("schema", &schema) &&
               (schema.GetString()=routeSchema || schema.GetString()=commandSchema) then
                decode<OrdinaryExecutorEnvelope> (set ["schema";"digest";"payloadBase64";"assignmentSha256";"artifactSha256"]) (32*1024) bytes
                |> Result.bind (fun v ->
                    if not(String.IsNullOrWhiteSpace v.PayloadBase64) && RunnerWire.validSha256 v.Digest && RunnerWire.validSha256 v.AssignmentSha256 && RunnerWire.validSha256 v.ArtifactSha256
                       && RunnerWire.sha256 (encode {v with Digest=""})=v.Digest then Ok(Some v)
                    else Error "ordinary-reference-refused")
            else Ok None
        with
        | :? JsonException
        | :? InvalidOperationException -> Error "ordinary-reference-json-refused"
    let payload (value: OrdinaryExecutorEnvelope) =
        try
            let bytes=Convert.FromBase64String value.PayloadBase64
            if bytes.Length=0 || bytes.Length>24*1024 then Error "ordinary-payload-size-refused" else Ok bytes
        with :? FormatException -> Error "ordinary-payload-format-refused"
    let wrap schema assignment artifact (payloadBytes: byte array) =
      if isNull payloadBytes || payloadBytes.Length=0 || payloadBytes.Length>24*1024
         || not(RunnerWire.validSha256 assignment && RunnerWire.validSha256 artifact)
         || (schema<>routeSchema && schema<>commandSchema) then Error "ordinary-wrap-input-refused"
      else
        let v={Schema=schema;Digest="";PayloadBase64=Convert.ToBase64String payloadBytes;
               AssignmentSha256=assignment;ArtifactSha256=artifact}
        let bytes=encode {v with Digest=RunnerWire.sha256(encode v)}
        if bytes.Length>32*1024 then Error "ordinary-reference-size-refused" else Ok bytes
