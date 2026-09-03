namespace FS.GG.Coordination.Core

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type WorkflowObligation = Build | Test | Policy | Coordination | Packaging | Release
type WorkflowRuleMatch = Exact | Prefix | Suffix
type WorkflowImpactRule = { Id: string; Pattern: string; Match: WorkflowRuleMatch; Roots: WorkflowObligation list }
type WorkflowDependency = { Source: WorkflowObligation; Target: WorkflowObligation }
type WorkflowSelectionInventory =
    { SchemaVersion: int; InventoryVersion: string; GraphVersion: string; BaseRevision: string
      SettingsSha256: string; Complete: bool; PathRules: WorkflowImpactRule list
      NonFileRules: WorkflowImpactRule list; Dependencies: WorkflowDependency list
      Unconditional: WorkflowObligation list; Aggregates: string list
      Expensive: WorkflowObligation list; Seal: string }
type MergeGroupSelectionInput =
    { QueuedHead: string; CurrentQueuedHead: string; CurrentBaseRevision: string; CurrentSettingsSha256: string; Recomputed: bool }
type WorkflowSelectionRequest =
    { InventoryVersion: string; GraphVersion: string; ExpectedInventorySeal: string; BaseRevision: string; SettingsSha256: string
      Complete: bool; ChangedPaths: string list; NonFileInputs: string list
      MergeGroup: MergeGroupSelectionInput option }
type WorkflowChildDisposition = Selected | NotApplicable of reason: string
type WorkflowChildDecision =
    { Obligation: WorkflowObligation; Disposition: WorkflowChildDisposition; ProvisionExpensiveJob: bool }
type WorkflowAggregateDecision =
    { Name: string; Status: string; SelectedCount: int; NotApplicableCount: int }
type WorkflowSelectionDecision =
    { InventoryVersion: string; GraphVersion: string; InventorySeal: string
      Roots: WorkflowObligation list; Closure: WorkflowObligation list
      Children: WorkflowChildDecision list; Aggregates: WorkflowAggregateDecision list
      MergeGroupQueuedHead: string option }
type WorkflowSelectionRefusal =
    | UnsupportedSchemaVersion of int
    | UnsupportedInventoryVersion of expected: string * observed: string
    | UnsupportedGraphVersion of expected: string * observed: string
    | IncompleteInventory | IncompleteRequest | InvalidInventory of string
    | InventorySealMismatch of expected: string * observed: string
    | StaleBaseRevision of expected: string * observed: string
    | StaleSettings of expected: string * observed: string
    | UnknownChangedPath of string | UnknownNonFileInput of string
    | AmbiguousChangedPath of path: string * ruleIds: string list
    | AmbiguousNonFileInput of input: string * ruleIds: string list
    | InvalidMergeGroup of string

module WorkflowSelection =
    let supportedInventoryVersion = "coordination-workflows/1"
    let supportedGraphVersion = "fsgg.workflow-impact/1"
    let private obligations = [ Build; Test; Policy; Coordination; Packaging; Release ]
    let obligationId = function
        | Build -> "build" | Test -> "test" | Policy -> "policy" | Coordination -> "coordination"
        | Packaging -> "packaging" | Release -> "release"
    let tryParseObligation (value: string) =
        match value with
        | "build" -> Some Build | "test" -> Some Test | "policy" -> Some Policy
        | "coordination" -> Some Coordination | "packaging" -> Some Packaging
        | "release" -> Some Release | _ -> None
    let ruleMatchId = function Exact -> "exact" | Prefix -> "prefix" | Suffix -> "suffix"
    let tryParseRuleMatch (value: string) =
        match value with "exact" -> Some Exact | "prefix" -> Some Prefix | "suffix" -> Some Suffix | _ -> None

    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)
    let private revision value = validText value && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private sha256Text value = validText value && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private sha256 (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private order value = obligations |> List.findIndex ((=) value)
    let private ordered values = values |> List.distinct |> List.sortBy order
    let private obligationList values = values |> ordered |> List.map obligationId |> String.concat ","
    let private ruleText rule =
        [ rule.Id; rule.Pattern; ruleMatchId rule.Match; obligationList rule.Roots ] |> List.map frame |> String.concat "|"
    let private edgeText edge = $"{obligationId edge.Source}>{obligationId edge.Target}"

    let computeInventorySeal inventory =
        [ string inventory.SchemaVersion; inventory.InventoryVersion; inventory.GraphVersion
          inventory.BaseRevision; inventory.SettingsSha256; string inventory.Complete
          inventory.PathRules |> List.map ruleText |> String.concat ";"
          inventory.NonFileRules |> List.map ruleText |> String.concat ";"
          inventory.Dependencies |> List.map edgeText |> String.concat ","
          obligationList inventory.Unconditional
          String.concat "," inventory.Aggregates
          obligationList inventory.Expensive ]
        |> List.map frame |> String.concat "|" |> sha256

    let private matches rule (value: string) =
        match rule.Match with
        | Exact -> String.Equals(value, rule.Pattern, StringComparison.Ordinal)
        | Prefix -> value.StartsWith(rule.Pattern, StringComparison.Ordinal)
        | Suffix -> value.EndsWith(rule.Pattern, StringComparison.Ordinal)

    let private classify unknown ambiguous rules values =
        let classifyOne value =
            let matched = rules |> List.filter (fun rule -> matches rule value)
            match matched with
            | [] -> Error [ unknown value ]
            | [ rule ] -> Ok rule.Roots
            | many -> Error [ ambiguous (value, many |> List.map _.Id |> List.sort) ]
        values
        |> List.map classifyOne
        |> List.fold (fun state item ->
            match state, item with
            | Ok roots, Ok next -> Ok(roots @ next)
            | Error findings, Error next -> Error(findings @ next)
            | Error findings, _ -> Error findings
            | _, Error findings -> Error findings) (Ok [])

    let private graphClosure edges roots =
        let rec loop seen =
            let next =
                edges
                |> List.choose (fun edge -> if Set.contains edge.Source seen then Some edge.Target else None)
                |> Set.ofList |> Set.difference <| seen
            if Set.isEmpty next then seen else loop (Set.union seen next)
        roots |> Set.ofList |> loop |> Set.toList |> ordered

    let private inventoryFindings inventory =
        [ if inventory.SchemaVersion <> 1 then UnsupportedSchemaVersion inventory.SchemaVersion
          if not inventory.Complete then IncompleteInventory
          if not (validText inventory.InventoryVersion) then InvalidInventory "inventoryVersion"
          if not (validText inventory.GraphVersion) then InvalidInventory "graphVersion"
          if inventory.InventoryVersion <> supportedInventoryVersion then UnsupportedInventoryVersion(supportedInventoryVersion, inventory.InventoryVersion)
          if inventory.GraphVersion <> supportedGraphVersion then UnsupportedGraphVersion(supportedGraphVersion, inventory.GraphVersion)
          if not (revision inventory.BaseRevision) then InvalidInventory "baseRevision"
          if not (sha256Text inventory.SettingsSha256) then InvalidInventory "settingsSha256"
          if inventory.PathRules.IsEmpty then InvalidInventory "pathRules"
          if inventory.NonFileRules.IsEmpty then InvalidInventory "nonFileRules"
          if inventory.Aggregates.IsEmpty || inventory.Aggregates |> List.exists (validText >> not) then InvalidInventory "aggregates"
          if inventory.Aggregates.Length <> (inventory.Aggregates |> Set.ofList |> Set.count) then InvalidInventory "duplicate aggregate"
          if inventory.PathRules @ inventory.NonFileRules |> List.exists (fun rule -> not (validText rule.Id) || not (validText rule.Pattern) || rule.Roots.IsEmpty) then InvalidInventory "impact rule"
          let ruleIds = inventory.PathRules @ inventory.NonFileRules |> List.map _.Id
          if ruleIds.Length <> (ruleIds |> Set.ofList |> Set.count) then InvalidInventory "duplicate rule id"
          if inventory.Dependencies |> List.exists (fun edge -> edge.Source = edge.Target) then InvalidInventory "self dependency"
          if inventory.Unconditional.IsEmpty then InvalidInventory "unconditional obligations" ]

    let select inventory request =
        let expectedSeal = computeInventorySeal inventory
        let preflight =
            [ yield! inventoryFindings inventory
              if not request.Complete then IncompleteRequest
              if request.InventoryVersion <> inventory.InventoryVersion then UnsupportedInventoryVersion(inventory.InventoryVersion, request.InventoryVersion)
              if request.GraphVersion <> inventory.GraphVersion then UnsupportedGraphVersion(inventory.GraphVersion, request.GraphVersion)
              if request.ExpectedInventorySeal <> inventory.Seal then InventorySealMismatch(request.ExpectedInventorySeal, inventory.Seal)
              if request.BaseRevision <> inventory.BaseRevision then StaleBaseRevision(inventory.BaseRevision, request.BaseRevision)
              if request.SettingsSha256 <> inventory.SettingsSha256 then StaleSettings(inventory.SettingsSha256, request.SettingsSha256)
              if inventory.Seal <> expectedSeal then InventorySealMismatch(expectedSeal, inventory.Seal)
              match request.MergeGroup with
              | None -> ()
              | Some merge ->
                  if not (revision merge.QueuedHead) then InvalidMergeGroup "queuedHead"
                  if not (revision merge.CurrentQueuedHead) then InvalidMergeGroup "currentQueuedHead"
                  if merge.QueuedHead <> merge.CurrentQueuedHead then InvalidMergeGroup "queued head differs from current queued head"
                  if not merge.Recomputed then InvalidMergeGroup "selection was not recomputed"
                  if merge.CurrentBaseRevision <> request.BaseRevision then InvalidMergeGroup "current base differs from request base"
                  if merge.CurrentSettingsSha256 <> request.SettingsSha256 then InvalidMergeGroup "current settings differ from request settings" ]
        if not preflight.IsEmpty then Error preflight
        else
            let paths = request.ChangedPaths |> List.distinct
            let nonFiles = request.NonFileInputs |> List.distinct
            if paths.IsEmpty && nonFiles.IsEmpty then Error [ IncompleteRequest ]
            else
                let pathRoots = classify UnknownChangedPath (fun (value, ids) -> AmbiguousChangedPath(value, ids)) inventory.PathRules paths
                let nonFileRoots = classify UnknownNonFileInput (fun (value, ids) -> AmbiguousNonFileInput(value, ids)) inventory.NonFileRules nonFiles
                match pathRoots, nonFileRoots with
                | Error left, Error right -> Error(left @ right)
                | Error findings, _ | _, Error findings -> Error findings
                | Ok left, Ok right ->
                    let roots = left @ right |> ordered
                    let closure = graphClosure inventory.Dependencies (roots @ inventory.Unconditional)
                    let closureSet = Set.ofList closure
                    let expensive = Set.ofList inventory.Expensive
                    let children =
                        obligations
                        |> List.map (fun obligation ->
                            if Set.contains obligation closureSet then
                                { Obligation = obligation; Disposition = Selected
                                  ProvisionExpensiveJob = Set.contains obligation expensive }
                            else
                                { Obligation = obligation
                                  Disposition = NotApplicable $"{obligationId obligation} is outside the derived closure"
                                  ProvisionExpensiveJob = false })
                    let selected = closure.Length
                    let aggregates =
                        inventory.Aggregates
                        |> List.map (fun name ->
                            { Name = name; Status = "resolved"; SelectedCount = selected
                              NotApplicableCount = obligations.Length - selected })
                    Ok
                        { InventoryVersion = inventory.InventoryVersion; GraphVersion = inventory.GraphVersion
                          InventorySeal = expectedSeal; Roots = roots; Closure = closure; Children = children
                          Aggregates = aggregates
                          MergeGroupQueuedHead = request.MergeGroup |> Option.map _.QueuedHead }
