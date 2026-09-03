module FS.GG.Coordination.GitHubFleetDryPlanArchitectureTests

open System
open System.IO
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))

[<Fact>]
let ``Q5 inventory is complete ordered and independently doubled`` () =
    let controls = GitHubFleetDryPlanQualification.requiredControls
    let ids = controls |> List.map GitHubFleetDryPlanQualification.controlId
    Assert.Equal(33, ids.Length)
    Assert.Equal(33, ids |> Set.ofList |> Set.count)
    let generated: GitHubFleetControlResult list = controls |> List.map (fun value -> { Control = value; ControlPassed = true; BaselineGreen = true })
    let independent: GitHubFleetControlResult list = controls |> List.map (fun value -> { Control = value; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubFleetDryPlanQualification.validateControls generated independent)

[<Fact>]
let ``fleet dry plan contract exposes no production mutation boundary`` () =
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubFleetDryPlanQualification.fs"))
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubFleetDryPlanQualification.fsi"))
    for forbidden in [ "HttpClient"; "Octokit"; "Transport."; "Write."; "apply:"; "executeOperation"; "updateRepository" ] do
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)
        Assert.DoesNotContain(forbidden, signature, StringComparison.Ordinal)
    Assert.Contains("let action = \"would-update\"", source, StringComparison.Ordinal)
    Assert.Contains("val compile:", signature, StringComparison.Ordinal)
    Assert.Contains("val reinspect:", signature, StringComparison.Ordinal)

[<Fact>]
let ``GS2-06.8 retained provider surface is complete`` () =
    let directory = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-8")
    let expected =
        [ "README.md"; "live-observations.json"; "desired-state.json"; "reinspection.json"
          "review.json"; "corpus.json"; "independent-expectations.json" ]
    Assert.True(Directory.Exists directory)
    let actual = Directory.GetFiles(directory) |> Array.map Path.GetFileName |> Array.sort |> Array.toList
    Assert.Equal<string list>(List.sort expected, actual)

[<Fact>]
let ``registered Q5 command remains exact and final`` () =
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    let validator = Path.Combine(root, "eng/validate-github-fleet-dry-plans.fsx")
    Assert.Contains("github-fleet-dry-plans-contract", catalog, StringComparison.Ordinal)
    Assert.Contains("\"id\": \"github-fleet-dry-plans-contract\"", catalog, StringComparison.Ordinal)
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-fleet-dry-plans.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)
    Assert.True(File.Exists validator)
    let index = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-units.json"))
    let previous = index.IndexOf("github-workflow-selection-supply-chain-contract", StringComparison.Ordinal)
    let fleet = index.IndexOf("github-fleet-dry-plans-contract", StringComparison.Ordinal)
    Assert.True(previous >= 0 && fleet > previous)
