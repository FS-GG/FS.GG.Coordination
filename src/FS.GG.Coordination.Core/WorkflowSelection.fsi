namespace FS.GG.Coordination.Core

type WorkflowObligation =
    | Build
    | Test
    | Policy
    | Coordination
    | Packaging
    | Release

type WorkflowRuleMatch = Exact | Prefix | Suffix

type WorkflowImpactRule =
    { Id: string
      Pattern: string
      Match: WorkflowRuleMatch
      Roots: WorkflowObligation list }

type WorkflowDependency =
    { Source: WorkflowObligation
      Target: WorkflowObligation }

type WorkflowSelectionInventory =
    { SchemaVersion: int
      InventoryVersion: string
      GraphVersion: string
      BaseRevision: string
      SettingsSha256: string
      Complete: bool
      PathRules: WorkflowImpactRule list
      NonFileRules: WorkflowImpactRule list
      Dependencies: WorkflowDependency list
      Unconditional: WorkflowObligation list
      Aggregates: string list
      Expensive: WorkflowObligation list
      Seal: string }

type MergeGroupSelectionInput =
    { QueuedHead: string
      CurrentBaseRevision: string
      CurrentSettingsSha256: string
      Recomputed: bool }

type WorkflowSelectionRequest =
    { InventoryVersion: string
      GraphVersion: string
      BaseRevision: string
      SettingsSha256: string
      Complete: bool
      ChangedPaths: string list
      NonFileInputs: string list
      MergeGroup: MergeGroupSelectionInput option }

type WorkflowChildDisposition = Selected | NotApplicable of reason: string

type WorkflowChildDecision =
    { Obligation: WorkflowObligation
      Disposition: WorkflowChildDisposition
      ProvisionExpensiveJob: bool }

type WorkflowAggregateDecision =
    { Name: string
      Status: string
      SelectedCount: int
      NotApplicableCount: int }

type WorkflowSelectionDecision =
    { InventoryVersion: string
      GraphVersion: string
      InventorySeal: string
      Roots: WorkflowObligation list
      Closure: WorkflowObligation list
      Children: WorkflowChildDecision list
      Aggregates: WorkflowAggregateDecision list
      MergeGroupQueuedHead: string option }

type WorkflowSelectionRefusal =
    | UnsupportedSchemaVersion of int
    | UnsupportedInventoryVersion of expected: string * observed: string
    | UnsupportedGraphVersion of expected: string * observed: string
    | IncompleteInventory
    | IncompleteRequest
    | InvalidInventory of string
    | InventorySealMismatch of expected: string * observed: string
    | StaleBaseRevision of expected: string * observed: string
    | StaleSettings of expected: string * observed: string
    | UnknownChangedPath of string
    | UnknownNonFileInput of string
    | AmbiguousChangedPath of path: string * ruleIds: string list
    | AmbiguousNonFileInput of input: string * ruleIds: string list
    | InvalidMergeGroup of string

module WorkflowSelection =
    val obligationId: WorkflowObligation -> string
    val tryParseObligation: string -> WorkflowObligation option
    val ruleMatchId: WorkflowRuleMatch -> string
    val tryParseRuleMatch: string -> WorkflowRuleMatch option
    val computeInventorySeal: WorkflowSelectionInventory -> string
    val select: WorkflowSelectionInventory -> WorkflowSelectionRequest -> Result<WorkflowSelectionDecision, WorkflowSelectionRefusal list>
