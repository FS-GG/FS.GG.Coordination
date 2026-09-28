namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution

type ICodexLearningCapabilityDiscovery =
    abstract member Discover: LearningSelectionQuery * CancellationToken -> Task<LearningSelectionEvidence>

[<RequireQualifiedAccess>]
module CodexLearningProviderCapability =
    let unknown observedAt (query: LearningSelectionQuery) code =
        {
            Schema = LearningSelectionEvidence.schema
            Provider = query.Provider
            Executable = query.Executable
            Requested = query.Requested
            Status = LearningCapabilityStatus.Unknown code
            Provenance = "codex-cli-pinned-discovery"
            ObservedAt = observedAt
            ExpiresAt = observedAt.Add(query.MaximumAge)
        }

    let unsupported observedAt (query: LearningSelectionQuery) code =
        { unknown observedAt query code with
            Status = LearningCapabilityStatus.Unsupported code
        }

    let supported observedAt (query: LearningSelectionQuery) provenance =
        {
            Schema = LearningSelectionEvidence.schema
            Provider = query.Provider
            Executable = query.Executable
            Requested = query.Requested
            Status = LearningCapabilityStatus.Supported
            Provenance = provenance
            ObservedAt = observedAt
            ExpiresAt = observedAt.Add(query.MaximumAge)
        }

    /// Interpret one complete, caller-authenticated app-server model/list response.
    /// Generating its schema or constructing CLI flags does not call this boundary.
    let fromModelList observedAt (query: LearningSelectionQuery) (bytes: byte array) =
        let fail code = unknown observedAt query code

        try
            use document = JsonDocument.Parse(ReadOnlyMemory bytes)
            let root = document.RootElement

            let complete =
                match root.TryGetProperty "nextCursor" with
                | false, _ -> true
                | true, value -> value.ValueKind = JsonValueKind.Null

            match query.Requested.Model, query.Requested.Effort, root.TryGetProperty "data" with
            | _, _, _ when root.ValueKind <> JsonValueKind.Object -> fail "codex-model-list-invalid"
            | _, _, _ when not complete -> fail "codex-model-list-incomplete"
            | Some model, Some effort, (true, data) when data.ValueKind = JsonValueKind.Array ->
                let matches =
                    data.EnumerateArray()
                    |> Seq.choose (fun item ->
                        match item.TryGetProperty "model", item.TryGetProperty "supportedReasoningEfforts" with
                        | (true, modelValue), (true, efforts) when
                            modelValue.ValueKind = JsonValueKind.String
                            && efforts.ValueKind = JsonValueKind.Array
                            && modelValue.GetString() = model
                            ->
                            efforts.EnumerateArray()
                            |> Seq.choose (fun option ->
                                match option.TryGetProperty "reasoningEffort" with
                                | true, value when value.ValueKind = JsonValueKind.String ->
                                    value.GetString() |> Option.ofObj
                                | _ -> None)
                            |> Set.ofSeq
                            |> Some
                        | _ -> None)
                    |> Seq.toList

                match matches with
                | [] -> unsupported observedAt query "requested-model-unsupported"
                | [ efforts ] when efforts.Contains effort -> supported observedAt query "codex-app-server:model/list"
                | [ _ ] -> unsupported observedAt query "requested-effort-unsupported"
                | _ -> fail "codex-model-list-ambiguous"
            | None, _, _
            | _, None, _ -> fail "requested-selection-incomplete"
            | _ -> fail "codex-model-list-invalid"
        with
        | :? JsonException
        | :? InvalidOperationException -> fail "codex-model-list-invalid"
