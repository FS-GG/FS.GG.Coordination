namespace FS.GG.Coordination.Orchestration.Execution.Codex

open System
open System.Collections.Generic
open System.Text.Json

/// Read-only report over a pinned generated schema, not runtime authentication.
type CodexAppServerSdkCapability =
    { CliVersion: string
      TurnCompletedUsage: string
      ThreadUsageShape: string
      RawResponseUsageScope: string
      RunningThreadResumeDescription: string
      CurrentDirectSessionAttachment: string
      NativeCompletedTurnUsageVerdict: string }

[<RequireQualifiedAccess>]
module CodexAppServerSdkCapability =
    let private pinnedVersion = "codex-cli 0.156.1"

    let private property (name: string) (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then None
        else
            match node.TryGetProperty name with
            | true, value -> Some value
            | _ -> None

    let private stringValue (node: JsonElement) =
        if node.ValueKind = JsonValueKind.String then Some(node.GetString()) else None

    let private names (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then None
        else node.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq |> Some

    let private stringSet (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Array then None
        else
            let values = node.EnumerateArray() |> Seq.map stringValue |> Seq.toList
            if values |> List.exists Option.isNone then None
            else
                let strings = values |> List.choose id
                let unique = strings |> Set.ofList
                if unique.Count <> strings.Length then None else Some unique

    let rec private uniqueKeys (node: JsonElement) =
        match node.ValueKind with
        | JsonValueKind.Object ->
            let seen = HashSet<string>(StringComparer.Ordinal)
            node.EnumerateObject()
            |> Seq.forall (fun field -> seen.Add field.Name && uniqueKeys field.Value)
        | JsonValueKind.Array -> node.EnumerateArray() |> Seq.forall uniqueKeys
        | _ -> true

    let private exactShape required properties (node: JsonElement) =
        match property "required" node |> Option.bind stringSet,
              property "properties" node |> Option.bind names,
              names node with
        | Some actualRequired, Some actualProperties, Some fields ->
            actualRequired = required
            && actualProperties = properties
            && Set.isSubset fields (set [ "$schema"; "description"; "properties";
                                          "required"; "title"; "type" ])
        | _ -> false

    let private refIs name (node: JsonElement) =
        names node = Some(set [ "$ref" ])
        && property "$ref" node |> Option.bind stringValue = Some("#/definitions/" + name)

    /// A changed shape requires review; this never grants direct-session attachment or usage authority.
    let inspect (cliVersion: string) (schemaBytes: byte array)
        : Result<CodexAppServerSdkCapability, string> =
        if cliVersion <> pinnedVersion then Error "app-server-sdk-version-unpinned"
        elif isNull schemaBytes || schemaBytes.Length = 0 || schemaBytes.Length > 2097152 then
            Error "app-server-sdk-schema-size-invalid"
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(schemaBytes))
                let root = document.RootElement
                if not (uniqueKeys root) then Error "app-server-sdk-schema-duplicate-key"
                else
                    match property "definitions" root with
                    | None -> Error "app-server-sdk-schema-invalid"
                    | Some defs ->
                        let definition name = property name defs
                        match definition "TurnCompletedNotification",
                              definition "Turn",
                              definition "ThreadTokenUsageUpdatedNotification",
                              definition "ThreadTokenUsage",
                              definition "RawResponseCompletedNotification",
                              definition "ThreadResumeParams" with
                        | Some completed, Some turn, Some updated, Some usage,
                          Some rawResponse, Some resume when
                            exactShape (set [ "threadId"; "turn" ])
                                (set [ "threadId"; "turn" ]) completed
                            && (property "properties" completed
                                |> Option.bind (property "turn")
                                |> Option.exists (refIs "Turn"))
                            && exactShape (set [ "id"; "items"; "status" ])
                                (set [ "completedAt"; "durationMs"; "error"; "id";
                                       "items"; "itemsView"; "startedAt"; "status" ]) turn
                            && exactShape (set [ "threadId"; "tokenUsage"; "turnId" ])
                                (set [ "threadId"; "tokenUsage"; "turnId" ]) updated
                            && (property "properties" updated
                                |> Option.bind (property "tokenUsage")
                                |> Option.exists (refIs "ThreadTokenUsage"))
                            && exactShape (set [ "last"; "total" ])
                                (set [ "last"; "modelContextWindow"; "total" ]) usage
                            && exactShape (set [ "responseId"; "threadId"; "turnId" ])
                                (set [ "responseId"; "threadId"; "turnId"; "usage"; "usageMetadata" ]) rawResponse
                            && (property "description" rawResponse
                                |> Option.bind stringValue
                                |> Option.exists (fun (value: string) ->
                                    value = "Internal-only notification containing the exact usage from one upstream Responses API completion."))
                            && (property "required" resume |> Option.bind stringSet = Some(set [ "threadId" ]))
                            && (property "properties" resume
                                |> Option.bind (property "threadId")
                                |> Option.isSome) ->
                            let runningRejoin =
                                property "description" resume
                                |> Option.bind stringValue
                                |> Option.exists (fun (value: string) ->
                                    value.Contains("If thread_id identifies a running thread", StringComparison.Ordinal))
                            Ok
                                { CliVersion = pinnedVersion
                                  TurnCompletedUsage = "absent-from-turn-completed"
                                  ThreadUsageShape = "last-and-cumulative-snapshots"
                                  RawResponseUsageScope = "internal-one-upstream-response"
                                  RunningThreadResumeDescription =
                                    if runningRejoin then "running-thread-rejoin-described"
                                    else "running-thread-rejoin-undetermined"
                                  CurrentDirectSessionAttachment = "not-authenticated-by-schema"
                                  NativeCompletedTurnUsageVerdict = "not-established" }
                        | _ -> Error "app-server-sdk-schema-drift"
            with :? JsonException ->
                Error "app-server-sdk-schema-json-invalid"
