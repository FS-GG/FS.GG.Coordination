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

    let private closedServerRoutes (definitions: JsonElement) =
        let route (node: JsonElement) =
            let fields = names node
            let properties = property "properties" node
            let methodSchema = properties |> Option.bind (property "method")
            let paramsSchema = properties |> Option.bind (property "params")
            match fields, property "type" node |> Option.bind stringValue,
                  property "required" node |> Option.bind stringSet,
                  properties |> Option.bind names,
                  methodSchema, paramsSchema with
            | Some fields, Some "object", Some required, Some propertyNames,
              Some methodNode, Some paramsNode when
                Set.isSubset fields (set [ "description"; "properties"; "required"; "title"; "type" ])
                && required = set [ "method"; "params" ]
                && propertyNames = set [ "method"; "params" ]
                && (names methodNode
                    |> Option.exists (fun methodFields ->
                        Set.isSubset (set [ "enum"; "type" ]) methodFields
                        && Set.isSubset methodFields (set [ "enum"; "title"; "type" ])))
                && (property "type" methodNode |> Option.bind stringValue = Some "string")
                && names paramsNode = Some(set [ "$ref" ]) ->
                match property "enum" methodNode |> Option.bind stringSet,
                      property "$ref" paramsNode |> Option.bind stringValue with
                | Some methods, Some reference when
                    methods.Count = 1 && reference.StartsWith("#/definitions/", StringComparison.Ordinal) ->
                    let target = reference.Substring("#/definitions/".Length)
                    if target.Length > 0 && property target definitions |> Option.isSome then
                        Some(Set.minElement methods, target)
                    else None
                | _ -> None
            | _ -> None

        match property "ServerNotification" definitions with
        | Some notification when
            names notification
            |> Option.exists (fun fields ->
                Set.isSubset fields (set [ "$schema"; "description"; "oneOf"; "properties"; "title" ])) ->
            let emitted =
                property "properties" notification
                |> Option.bind (property "emittedAtMs")
            match property "properties" notification |> Option.bind names, emitted,
                  property "oneOf" notification with
            | Some properties, Some emittedAt, Some choices when
                properties = set [ "emittedAtMs" ]
                && (names emittedAt
                    |> Option.exists (fun fields ->
                        Set.isSubset (set [ "format"; "type" ]) fields
                        && Set.isSubset fields (set [ "description"; "format"; "type" ])))
                && (property "type" emittedAt |> Option.bind stringValue = Some "integer")
                && (property "format" emittedAt |> Option.bind stringValue = Some "int64")
                && choices.ValueKind = JsonValueKind.Array ->
                let items = choices.EnumerateArray() |> Seq.toList
                let routes = items |> List.choose route
                if items.IsEmpty || items.Length > 256 || routes.Length <> items.Length then false
                else
                    let methods = routes |> List.map fst |> Set.ofList
                    let routeMap = Map.ofList routes
                    let unreviewedTurnUsageRoute =
                        routes
                        |> List.exists (fun (_, target) ->
                            property target definitions
                            |> Option.bind (property "properties")
                            |> Option.bind names
                            |> Option.exists (fun fields ->
                                Set.contains "turn" fields && Set.contains "usage" fields))
                    methods.Count = routes.Length
                    && not unreviewedTurnUsageRoute
                    && Map.tryFind "turn/completed" routeMap = Some "TurnCompletedNotification"
                    && Map.tryFind "thread/tokenUsage/updated" routeMap
                       = Some "ThreadTokenUsageUpdatedNotification"
            | _ -> false
        | _ -> false

    let private closedClientResumeRoute (definitions: JsonElement) =
        let route (node: JsonElement) =
            let properties = property "properties" node
            let methodSchema = properties |> Option.bind (property "method")
            let paramsSchema = properties |> Option.bind (property "params")
            match names node, property "type" node |> Option.bind stringValue,
                  property "required" node |> Option.bind stringSet,
                  properties |> Option.bind names,
                  properties |> Option.bind (property "id"),
                  methodSchema, paramsSchema with
            | Some fields, Some "object", Some required, Some propertyNames,
              Some id, Some methodNode, Some paramsNode when
                Set.isSubset fields (set [ "description"; "properties"; "required"; "title"; "type" ])
                && (required = set [ "id"; "method" ]
                    || required = set [ "id"; "method"; "params" ])
                && propertyNames = set [ "id"; "method"; "params" ]
                && refIs "RequestId" id
                && (names methodNode
                    |> Option.exists (fun methodFields ->
                        Set.isSubset (set [ "enum"; "type" ]) methodFields
                        && Set.isSubset methodFields (set [ "enum"; "title"; "type" ])))
                && (property "type" methodNode |> Option.bind stringValue = Some "string") ->
                match property "enum" methodNode |> Option.bind stringSet with
                | Some methods when methods.Count = 1 ->
                    Some(Set.minElement methods, (paramsNode, required))
                | _ -> None
            | _ -> None

        match property "ClientRequest" definitions with
        | Some requests when
            names requests
            |> Option.exists (fun fields ->
                Set.isSubset fields (set [ "$schema"; "description"; "oneOf"; "title" ])) ->
            match property "oneOf" requests with
            | Some choices when choices.ValueKind = JsonValueKind.Array ->
                let items = choices.EnumerateArray() |> Seq.toList
                let routes = items |> List.choose route
                if items.IsEmpty || items.Length > 256 || routes.Length <> items.Length then false
                else
                    let methods = routes |> List.map fst |> Set.ofList
                    let routeMap = Map.ofList routes
                    methods.Count = routes.Length
                    && (routeMap |> Map.tryFind "thread/resume"
                        |> Option.exists (fun (parameters, required) ->
                            required = set [ "id"; "method"; "params" ]
                            && refIs "ThreadResumeParams" parameters))
            | _ -> false
        | _ -> false

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
                    | Some _ when
                        names root <> Some(set [ "$schema"; "definitions"; "title"; "type" ])
                        || (property "$schema" root |> Option.bind stringValue)
                           <> Some "http://json-schema.org/draft-07/schema#"
                        || (property "title" root |> Option.bind stringValue)
                           <> Some "CodexAppServerProtocolV2"
                        || (property "type" root |> Option.bind stringValue) <> Some "object" ->
                        Error "app-server-sdk-schema-drift"
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
                            closedServerRoutes defs
                            && closedClientResumeRoute defs
                            && exactShape (set [ "threadId"; "turn" ])
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
