module FS.GG.Coordination.GitHubWorkflowSelectionTests

open Xunit
open FS.GG.Coordination.Core
open FS.GG.Coordination.Qualification.Contracts

[<Fact>]
let ``selection controls are complete ordered and independently required`` () =
    let controls = GitHubWorkflowSelectionQualification.requiredSelectionControls
    let ids = controls |> List.map GitHubWorkflowSelectionQualification.selectionControlId
    Assert.Equal(23, controls.Length)
    Assert.Equal(23, ids |> Set.ofList |> Set.count)
    Assert.Equal("workflow-prerequisite", ids.Head)
    Assert.Contains("exact-workflow-seal", ids)
    Assert.Equal("no-workflow-mutation-surface", ids |> List.last)
    let passing: GitHubWorkflowControlResult<GitHubWorkflowSelectionControl> list =
        controls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubWorkflowSelectionQualification.validateSelection passing passing)
    let independentRed = passing |> List.mapi (fun index value -> if index = 7 then { value with ControlPassed = false } else value)
    Assert.True(GitHubWorkflowSelectionQualification.validateSelection passing independentRed |> Result.isError)

[<Fact>]
let ``supply-chain controls are distinct complete and independently required`` () =
    let controls = GitHubWorkflowSelectionQualification.requiredSupplyChainControls
    let ids = controls |> List.map GitHubWorkflowSelectionQualification.supplyChainControlId
    Assert.Equal(12, controls.Length)
    Assert.Equal(12, ids |> Set.ofList |> Set.count)
    Assert.Equal("fleet-baselines", ids.Head)
    Assert.Equal("removal-ledger", ids |> List.last)
    let passing: GitHubWorkflowControlResult<GitHubWorkflowSupplyChainControl> list =
        controls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubWorkflowSelectionQualification.validateSupplyChain passing passing)
    let generatedRed = passing |> List.mapi (fun index value -> if index = 10 then { value with BaselineGreen = false } else value)
    Assert.True(GitHubWorkflowSelectionQualification.validateSupplyChain generatedRed passing |> Result.isError)

[<Fact>]
let ``selection and supply-chain control identifiers do not overlap`` () =
    let selection = GitHubWorkflowSelectionQualification.requiredSelectionControls |> List.map GitHubWorkflowSelectionQualification.selectionControlId |> Set.ofList
    let supply = GitHubWorkflowSelectionQualification.requiredSupplyChainControls |> List.map GitHubWorkflowSelectionQualification.supplyChainControlId |> Set.ofList
    Assert.True(Set.intersect selection supply |> Set.isEmpty)

let private baseRevision = String.replicate 40 "a"
let private settings = String.replicate 64 "b"
let private inventory () : WorkflowSelectionInventory =
    let unsigned =
        { SchemaVersion = 1
          InventoryVersion = "coordination-workflows/1"
          GraphVersion = "fsgg.workflow-impact/1"
          BaseRevision = baseRevision
          SettingsSha256 = settings
          Complete = true
          PathRules =
            [ { Id = "source"; Pattern = "src/"; Match = Prefix; Roots = [ WorkflowObligation.Build ] }
              { Id = "tests"; Pattern = "tests/"; Match = Prefix; Roots = [ WorkflowObligation.Test ] }
              { Id = "workflow"; Pattern = ".github/"; Match = Prefix; Roots = [ WorkflowObligation.Policy ] }
              { Id = "release"; Pattern = "eng/release"; Match = Prefix; Roots = [ WorkflowObligation.Packaging ] }
              { Id = "docs"; Pattern = "docs/"; Match = Prefix; Roots = [ WorkflowObligation.Coordination ] } ]
          NonFileRules =
            [ { Id = "settings"; Pattern = "repository-settings"; Match = Exact; Roots = [ WorkflowObligation.Policy ] }
              { Id = "dependency"; Pattern = "dependency-revision"; Match = Exact; Roots = [ WorkflowObligation.Build; WorkflowObligation.Packaging ] } ]
          Dependencies =
            [ { Source = WorkflowObligation.Build; Target = WorkflowObligation.Test }
              { Source = WorkflowObligation.Test; Target = WorkflowObligation.Policy }
              { Source = WorkflowObligation.Policy; Target = WorkflowObligation.Coordination }
              { Source = WorkflowObligation.Packaging; Target = WorkflowObligation.Release } ]
          Unconditional = [ WorkflowObligation.Policy ]
          Aggregates = [ "required"; "supply-chain" ]
          Expensive = [ WorkflowObligation.Test; WorkflowObligation.Packaging; WorkflowObligation.Release ]
          Seal = "" }
    { unsigned with Seal = WorkflowSelection.computeInventorySeal unsigned }

let private request paths nonFiles : WorkflowSelectionRequest =
    { InventoryVersion = "coordination-workflows/1"
      GraphVersion = "fsgg.workflow-impact/1"
      ExpectedInventorySeal = (inventory ()).Seal
      BaseRevision = baseRevision
      SettingsSha256 = settings
      Complete = true
      ChangedPaths = paths
      NonFileInputs = nonFiles
      MergeGroup = None }

let private decision result : WorkflowSelectionDecision =
    match result with Ok value -> value | Error findings -> failwith $"unexpected refusal: {findings}"

[<Fact>]
let ``runtime selector derives arbitrary mixed closure and stable outcomes`` () =
    let selected = WorkflowSelection.select (inventory ()) (request [ "src/NewFeature.fs"; "eng/release-candidate.json" ] []) |> decision
    Assert.Equal<WorkflowObligation list>([ WorkflowObligation.Build; WorkflowObligation.Packaging ], selected.Roots)
    Assert.Equal<WorkflowObligation list>(
        [ WorkflowObligation.Build; WorkflowObligation.Test; WorkflowObligation.Policy
          WorkflowObligation.Coordination; WorkflowObligation.Packaging; WorkflowObligation.Release ], selected.Closure)
    Assert.Equal(6, selected.Children.Length)
    selected.Aggregates |> List.iter (fun value -> Assert.Equal("resolved", value.Status))

[<Fact>]
let ``runtime selector handles non-file input and does not provision not-applicable jobs`` () =
    let selected = WorkflowSelection.select (inventory ()) (request [] [ "repository-settings" ]) |> decision
    Assert.Equal<WorkflowObligation list>([ WorkflowObligation.Policy ], selected.Roots)
    let packaging = selected.Children |> List.find (fun value -> value.Obligation = WorkflowObligation.Packaging)
    match packaging.Disposition with
    | WorkflowChildDisposition.NotApplicable reason -> Assert.Contains("outside", reason)
    | _ -> failwith "packaging must be not applicable"
    Assert.False(packaging.ProvisionExpensiveJob)

[<Fact>]
let ``runtime selector recomputes merge group against queued head current base and settings`` () =
    let merge : MergeGroupSelectionInput =
        { QueuedHead = String.replicate 40 "c"; CurrentQueuedHead = String.replicate 40 "c"; CurrentBaseRevision = baseRevision
          CurrentSettingsSha256 = settings; Recomputed = true }
    let selected = WorkflowSelection.select (inventory ()) { request [ "src/Queue.fs" ] [] with MergeGroup = Some merge } |> decision
    Assert.Equal(Some merge.QueuedHead, selected.MergeGroupQueuedHead)
    let stale = { merge with CurrentSettingsSha256 = String.replicate 64 "d" }
    match WorkflowSelection.select (inventory ()) { request [ "src/Queue.fs" ] [] with MergeGroup = Some stale } with
    | Error findings -> Assert.Contains(InvalidMergeGroup "current settings differ from request settings", findings)
    | Ok _ -> failwith "stale merge-group settings must fail closed"
    let staleHead = { merge with QueuedHead = String.replicate 40 "d" }
    match WorkflowSelection.select (inventory ()) { request [ "src/Queue.fs" ] [] with MergeGroup = Some staleHead } with
    | Error findings -> Assert.Contains(InvalidMergeGroup "queued head differs from current queued head", findings)
    | Ok _ -> failwith "stale merge-group queued head must fail closed"

[<Fact>]
let ``runtime selector fails closed on unknown ambiguous stale incomplete and forged inventory`` () =
    let current = inventory ()
    Assert.True(WorkflowSelection.select current (request [ "assets/unknown.bin" ] []) |> Result.isError)
    Assert.True(WorkflowSelection.select current { request [ "src/X.fs" ] [] with BaseRevision = String.replicate 40 "e" } |> Result.isError)
    Assert.True(WorkflowSelection.select current { request [ "src/X.fs" ] [] with Complete = false } |> Result.isError)
    Assert.True(WorkflowSelection.select { current with Aggregates = [ "forged" ] } (request [ "src/X.fs" ] []) |> Result.isError)
    let inventedUnsigned = { current with InventoryVersion = "invented-v999"; Seal = "" }
    let invented = { inventedUnsigned with Seal = WorkflowSelection.computeInventorySeal inventedUnsigned }
    Assert.True(WorkflowSelection.select invented { request [ "src/X.fs" ] [] with InventoryVersion = "invented-v999"; ExpectedInventorySeal = invented.Seal } |> Result.isError)
    let ambiguousUnsigned =
        { current with
            PathRules = { Id = "all-fsharp"; Pattern = ".fs"; Match = Suffix; Roots = [ WorkflowObligation.Release ] } :: current.PathRules
            Seal = "" }
    let ambiguous = { ambiguousUnsigned with Seal = WorkflowSelection.computeInventorySeal ambiguousUnsigned }
    match WorkflowSelection.select ambiguous { request [ "src/X.fs" ] [] with ExpectedInventorySeal = ambiguous.Seal } with
    | Error findings -> Assert.Contains(findings, function AmbiguousChangedPath("src/X.fs", _) -> true | _ -> false)
    | Ok _ -> failwith "ambiguous rules must fail closed"
    let sameRootUnsigned =
        { current with
            PathRules = { Id = "all-fsharp"; Pattern = ".fs"; Match = Suffix; Roots = [ WorkflowObligation.Build ] } :: current.PathRules
            Seal = "" }
    let sameRoot = { sameRootUnsigned with Seal = WorkflowSelection.computeInventorySeal sameRootUnsigned }
    match WorkflowSelection.select sameRoot { request [ "src/X.fs" ] [] with ExpectedInventorySeal = sameRoot.Seal } with
    | Error findings -> Assert.Contains(findings, function AmbiguousChangedPath("src/X.fs", _) -> true | _ -> false)
    | Ok _ -> failwith "overlapping same-root rules remain ambiguous and must fail closed"
