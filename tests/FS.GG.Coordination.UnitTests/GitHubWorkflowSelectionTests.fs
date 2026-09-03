module FS.GG.Coordination.GitHubWorkflowSelectionTests

open Xunit
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
