namespace FS.GG.Coordination.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Core

module WorkflowSelectionCommand =
    let private usage () =
        eprintfn "workflow-select --inventory FILE --request FILE --expected-inventory-version VERSION --expected-graph-version VERSION --expected-seal SHA256 --current-base SHA --current-settings SHA256 --current-queued-head SHA|none | workflow-select --seal-inventory FILE"
        2

    let private exact name expected (value: JsonObject) =
        let actual = value |> Seq.map _.Key |> Set.ofSeq
        let expectedSet = Set.ofList expected
        if actual <> expectedSet then
            let unknown = Set.difference actual expectedSet |> String.concat ","
            let missing = Set.difference expectedSet actual |> String.concat ","
            invalidArg name $"shape differs; unknown=[{unknown}] missing=[{missing}]"

    let private objectAt name (value: JsonNode) =
        match value with :? JsonObject as result -> result | _ -> invalidArg name "object required"
    let private arrayAt name (value: JsonNode) =
        match value with :? JsonArray as result -> result | _ -> invalidArg name "array required"
    let private text (name: string) (value: JsonObject) = value[name].GetValue<string>()
    let private boolean (name: string) (value: JsonObject) = value[name].GetValue<bool>()
    let private integer (name: string) (value: JsonObject) = value[name].GetValue<int>()
    let private texts (name: string) (value: JsonObject) = arrayAt name value[name] |> Seq.map _.GetValue<string>() |> Seq.toList
    let private parseObligation value =
        WorkflowSelection.tryParseObligation value
        |> Option.defaultWith (fun () -> invalidArg "obligation" $"unknown obligation '{value}'")
    let private obligations (name: string) (value: JsonObject) = texts name value |> List.map parseObligation
    let private parseMatch value =
        WorkflowSelection.tryParseRuleMatch value
        |> Option.defaultWith (fun () -> invalidArg "match" $"unknown rule match '{value}'")
    let private parseRule (value: JsonNode) =
        let item = objectAt "rule" value
        exact "rule" [ "id"; "pattern"; "match"; "roots" ] item
        { Id = text "id" item; Pattern = text "pattern" item
          Match = text "match" item |> parseMatch; Roots = obligations "roots" item }
    let private rules (name: string) (value: JsonObject) = arrayAt name value[name] |> Seq.map parseRule |> Seq.toList
    let private parseDependency (value: JsonNode) =
        let item = objectAt "dependency" value
        exact "dependency" [ "source"; "target" ] item
        { Source = text "source" item |> parseObligation; Target = text "target" item |> parseObligation }

    let private loadObject path =
        let node = JsonNode.Parse(File.ReadAllText path)
        if isNull node then invalidArg path "JSON document is empty"
        objectAt path node

    let private parseInventory path =
        let value = loadObject path
        exact "inventory"
            [ "schemaVersion"; "inventoryVersion"; "graphVersion"; "baseRevision"; "settingsSha256"
              "complete"; "pathRules"; "nonFileRules"; "dependencies"; "unconditional"
              "aggregates"; "expensive"; "seal" ] value
        { SchemaVersion = integer "schemaVersion" value
          InventoryVersion = text "inventoryVersion" value
          GraphVersion = text "graphVersion" value
          BaseRevision = text "baseRevision" value
          SettingsSha256 = text "settingsSha256" value
          Complete = boolean "complete" value
          PathRules = rules "pathRules" value
          NonFileRules = rules "nonFileRules" value
          Dependencies = arrayAt "dependencies" value["dependencies"] |> Seq.map parseDependency |> Seq.toList
          Unconditional = obligations "unconditional" value
          Aggregates = texts "aggregates" value
          Expensive = obligations "expensive" value
          Seal = text "seal" value }

    let private parseRequest path =
        let value = loadObject path
        exact "request"
            [ "inventoryVersion"; "graphVersion"; "expectedInventorySeal"; "baseRevision"; "settingsSha256"; "complete"
              "changedPaths"; "nonFileInputs"; "mergeGroup" ] value
        let mergeGroup =
            if isNull value["mergeGroup"] then None
            else
                let item = objectAt "mergeGroup" value["mergeGroup"]
                exact "mergeGroup" [ "queuedHead"; "currentQueuedHead"; "currentBaseRevision"; "currentSettingsSha256"; "recomputed" ] item
                Some
                    { QueuedHead = text "queuedHead" item
                      CurrentQueuedHead = text "currentQueuedHead" item
                      CurrentBaseRevision = text "currentBaseRevision" item
                      CurrentSettingsSha256 = text "currentSettingsSha256" item
                      Recomputed = boolean "recomputed" item }
        { InventoryVersion = text "inventoryVersion" value
          GraphVersion = text "graphVersion" value
          ExpectedInventorySeal = text "expectedInventorySeal" value
          BaseRevision = text "baseRevision" value
          SettingsSha256 = text "settingsSha256" value
          Complete = boolean "complete" value
          ChangedPaths = texts "changedPaths" value
          NonFileInputs = texts "nonFileInputs" value
          MergeGroup = mergeGroup }

    let private refusalCode = function
        | UnsupportedSchemaVersion _ -> "unsupported-schema-version"
        | UnsupportedInventoryVersion _ -> "unsupported-inventory-version"
        | UnsupportedGraphVersion _ -> "unsupported-graph-version"
        | IncompleteInventory -> "incomplete-inventory" | IncompleteRequest -> "incomplete-request"
        | InvalidInventory _ -> "invalid-inventory" | InventorySealMismatch _ -> "inventory-seal-mismatch"
        | StaleBaseRevision _ -> "stale-base-revision" | StaleSettings _ -> "stale-settings"
        | UnknownChangedPath _ -> "unknown-changed-path" | UnknownNonFileInput _ -> "unknown-non-file-input"
        | AmbiguousChangedPath _ -> "ambiguous-changed-path" | AmbiguousNonFileInput _ -> "ambiguous-non-file-input"
        | InvalidMergeGroup _ -> "invalid-merge-group"

    let private refusalMessage = function
        | UnsupportedSchemaVersion value -> string value
        | UnsupportedInventoryVersion(expected, observed)
        | UnsupportedGraphVersion(expected, observed)
        | InventorySealMismatch(expected, observed)
        | StaleBaseRevision(expected, observed)
        | StaleSettings(expected, observed) -> $"expected={expected};observed={observed}"
        | InvalidInventory field -> field
        | UnknownChangedPath value | UnknownNonFileInput value -> value
        | AmbiguousChangedPath(value, ids) | AmbiguousNonFileInput(value, ids) ->
            let joined = String.concat "," ids
            $"{value};rules={joined}"
        | InvalidMergeGroup reason -> reason
        | IncompleteInventory | IncompleteRequest -> "required input is incomplete"

    let private writeDecision decision =
        use writer = new Utf8JsonWriter(Console.OpenStandardOutput(), JsonWriterOptions(Indented = false))
        writer.WriteStartObject(); writer.WriteString("schema", "fsgg.coordination.workflow-selection-decision/1")
        writer.WriteString("inventoryVersion", decision.InventoryVersion); writer.WriteString("graphVersion", decision.GraphVersion)
        writer.WriteString("inventorySeal", decision.InventorySeal)
        writer.WriteStartArray("roots"); decision.Roots |> List.iter (WorkflowSelection.obligationId >> writer.WriteStringValue); writer.WriteEndArray()
        writer.WriteStartArray("closure"); decision.Closure |> List.iter (WorkflowSelection.obligationId >> writer.WriteStringValue); writer.WriteEndArray()
        writer.WriteStartArray("children")
        for child in decision.Children do
            writer.WriteStartObject(); writer.WriteString("obligation", WorkflowSelection.obligationId child.Obligation)
            match child.Disposition with
            | Selected -> writer.WriteString("disposition", "selected"); writer.WriteNull("reason")
            | NotApplicable reason -> writer.WriteString("disposition", "not-applicable"); writer.WriteString("reason", reason)
            writer.WriteBoolean("provisionExpensiveJob", child.ProvisionExpensiveJob); writer.WriteEndObject()
        writer.WriteEndArray(); writer.WriteStartArray("aggregates")
        for aggregate in decision.Aggregates do
            writer.WriteStartObject(); writer.WriteString("name", aggregate.Name); writer.WriteString("status", aggregate.Status)
            writer.WriteNumber("selectedCount", aggregate.SelectedCount); writer.WriteNumber("notApplicableCount", aggregate.NotApplicableCount); writer.WriteEndObject()
        writer.WriteEndArray()
        match decision.MergeGroupQueuedHead with Some value -> writer.WriteString("mergeGroupQueuedHead", value) | None -> writer.WriteNull("mergeGroupQueuedHead")
        writer.WriteEndObject(); writer.Flush(); printfn ""

    let private writeRefusals refusals =
        use writer = new Utf8JsonWriter(Console.OpenStandardError(), JsonWriterOptions(Indented = false))
        writer.WriteStartObject(); writer.WriteString("schema", "fsgg.coordination.workflow-selection-refusal/1")
        writer.WriteStartArray("findings")
        for refusal in refusals do
            writer.WriteStartObject(); writer.WriteString("code", refusalCode refusal); writer.WriteString("message", refusalMessage refusal); writer.WriteEndObject()
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); eprintfn ""

    let run arguments =
        let rec parse inventory request seal expectedInventory expectedGraph expectedSeal currentBase currentSettings currentQueuedHead = function
            | [] -> inventory, request, seal, expectedInventory, expectedGraph, expectedSeal, currentBase, currentSettings, currentQueuedHead
            | "--inventory" :: value :: rest -> parse (Some value) request seal expectedInventory expectedGraph expectedSeal currentBase currentSettings currentQueuedHead rest
            | "--request" :: value :: rest -> parse inventory (Some value) seal expectedInventory expectedGraph expectedSeal currentBase currentSettings currentQueuedHead rest
            | "--seal-inventory" :: value :: rest -> parse inventory request (Some value) expectedInventory expectedGraph expectedSeal currentBase currentSettings currentQueuedHead rest
            | "--expected-inventory-version" :: value :: rest -> parse inventory request seal (Some value) expectedGraph expectedSeal currentBase currentSettings currentQueuedHead rest
            | "--expected-graph-version" :: value :: rest -> parse inventory request seal expectedInventory (Some value) expectedSeal currentBase currentSettings currentQueuedHead rest
            | "--expected-seal" :: value :: rest -> parse inventory request seal expectedInventory expectedGraph (Some value) currentBase currentSettings currentQueuedHead rest
            | "--current-base" :: value :: rest -> parse inventory request seal expectedInventory expectedGraph expectedSeal (Some value) currentSettings currentQueuedHead rest
            | "--current-settings" :: value :: rest -> parse inventory request seal expectedInventory expectedGraph expectedSeal currentBase (Some value) currentQueuedHead rest
            | "--current-queued-head" :: value :: rest -> parse inventory request seal expectedInventory expectedGraph expectedSeal currentBase currentSettings (Some value) rest
            | _ -> None, None, None, None, None, None, None, None, None
        match parse None None None None None None None None None (Array.toList arguments) with
        | None, None, Some inventoryPath, None, None, None, None, None, None ->
            try printfn "%s" (parseInventory inventoryPath |> WorkflowSelection.computeInventorySeal); 0
            with ex -> eprintfn "workflow-select: malformed input: %s" ex.Message; 2
        | Some inventoryPath, Some requestPath, None, Some expectedInventory, Some expectedGraph, Some expectedSeal, Some currentBase, Some currentSettings, Some currentQueuedHead ->
            try
                let inventory = parseInventory inventoryPath
                let request = parseRequest requestPath
                let authorityRefusals =
                    [ if expectedInventory <> WorkflowSelection.supportedInventoryVersion || inventory.InventoryVersion <> expectedInventory || request.InventoryVersion <> expectedInventory then
                          UnsupportedInventoryVersion(expectedInventory, request.InventoryVersion)
                      if expectedGraph <> WorkflowSelection.supportedGraphVersion || inventory.GraphVersion <> expectedGraph || request.GraphVersion <> expectedGraph then
                          UnsupportedGraphVersion(expectedGraph, request.GraphVersion)
                      if inventory.Seal <> expectedSeal || request.ExpectedInventorySeal <> expectedSeal then InventorySealMismatch(expectedSeal, inventory.Seal)
                      if request.BaseRevision <> currentBase then StaleBaseRevision(currentBase, request.BaseRevision)
                      if request.SettingsSha256 <> currentSettings then StaleSettings(currentSettings, request.SettingsSha256)
                      match request.MergeGroup with
                      | None when currentQueuedHead <> "none" -> InvalidMergeGroup "current queued-head authority must be none outside merge group"
                      | Some merge when merge.QueuedHead <> currentQueuedHead -> InvalidMergeGroup "queued head authority differs"
                      | Some merge when merge.CurrentQueuedHead <> currentQueuedHead -> InvalidMergeGroup "current queued head authority differs"
                      | Some merge when merge.CurrentBaseRevision <> currentBase -> InvalidMergeGroup "current base authority differs"
                      | Some merge when merge.CurrentSettingsSha256 <> currentSettings -> InvalidMergeGroup "current settings authority differs"
                      | _ -> () ]
                match authorityRefusals, WorkflowSelection.select inventory request with
                | refusal :: rest, _ -> writeRefusals (refusal :: rest); 3
                | [], Ok decision -> writeDecision decision; 0
                | [], Error refusals -> writeRefusals refusals; 3
            with ex ->
                eprintfn "workflow-select: malformed input: %s" ex.Message
                2
        | _ -> usage ()
