module FS.GG.Coordination.GitHubNarrowReconciliationArchitectureTests

open System
open System.IO
open System.Text.Json
open System.Diagnostics
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))
let private read relative = File.ReadAllText(Path.Combine(root, relative))

[<Fact>]
let ``narrow reconciliation surface remains pure and repository local`` () =
    let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubNarrowReconciliationQualification.fs"
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubNarrowReconciliationQualification.fsi"
    let project = read "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "queueclient"; "webhook"; "getenvironmentvariable" ] do
        Assert.DoesNotContain(forbidden, (source + signature).ToLowerInvariant())
    Assert.DoesNotContain("FS.GG.Coordination.GitHub", project, StringComparison.Ordinal)
    Assert.DoesNotContain("FS.GG.Coordination.Core", project, StringComparison.Ordinal)
    Assert.Contains("GitHubNarrowReconciliationQualification.fsi", project, StringComparison.Ordinal)

[<Fact>]
let ``retained event writer and control inventories are exact`` () =
    use contract = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-2/contract.json")
    let strings (property: string) (node: JsonElement) = node.GetProperty(property).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubNarrowReconciliationQualification.supportedEventKinds, strings "eventKinds" contract.RootElement)
    Assert.Equal<string list>(GitHubNarrowReconciliationQualification.writerBoundary, strings "writerBoundary" contract.RootElement)
    let controls relative =
        use document = JsonDocument.Parse(read relative)
        strings "controls" document.RootElement, strings "cases" document.RootElement, document.RootElement.GetProperty("caseContract").GetString()
    let generatedIds, generatedCases, generatedContract = controls "evidence/github-substrate-v2/gs2-07-2/generated-controls.json"
    let independentIds, independentCases, independentContract = controls "evidence/github-substrate-v2/gs2-07-2/independent-controls.json"
    let expected = GitHubNarrowReconciliationQualification.requiredControls |> List.map GitHubNarrowReconciliationQualification.controlId
    Assert.Equal<string list>(expected, generatedIds)
    Assert.Equal<string list>(expected, independentIds)
    Assert.Equal(expected.Length, generatedCases.Length)
    Assert.Equal(expected.Length, independentCases.Length)
    let casesDiffer = generatedCases <> independentCases
    Assert.True(casesDiffer)
    Assert.NotEqual(generatedContract, independentContract)

[<Fact>]
let ``Q3 keeps generated and independent execution paths separate`` () =
    let validator = read "eng/validate-github-narrow-reconciliation.fsx"
    Assert.Contains("let executeGenerated control", validator, StringComparison.Ordinal)
    Assert.Contains("let executeIndependent control", validator, StringComparison.Ordinal)
    Assert.DoesNotContain("let execute control independent", validator, StringComparison.Ordinal)

[<Fact>]
let ``Q3 narrow reconciliation validator executes all controls`` () =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-narrow-reconciliation.fsx"; "--"; root ] do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("GITHUB_NARROW_RECONCILIATION_OK events=8 entries=8 controls=23", output, StringComparison.Ordinal)
