module FS.GG.Coordination.GitHubMergeGroupArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
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
let ``merge-group surface is pure and repository local`` () =
    let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubMergeGroupQualification.fs"
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubMergeGroupQualification.fsi"
    let project = read "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "queueclient"; "enqueue"; "dequeue"; "getenvironmentvariable" ] do
        Assert.DoesNotContain(forbidden, (source + signature).ToLowerInvariant())
    Assert.DoesNotContain("FS.GG.Coordination.GitHub", project, StringComparison.Ordinal)
    Assert.DoesNotContain("FS.GG.Coordination.Core", project, StringComparison.Ordinal)
    Assert.Contains("GitHubMergeGroupQualification.fsi", project, StringComparison.Ordinal)
    for required in [ "ObservedBaseRepository"; "CurrentBaseRepository"; "ObservedBaseRef"; "CurrentBaseRef"; "ObservedRequiredChecks"; "CheckResults" ] do
        Assert.Contains(required, source + signature, StringComparison.Ordinal)
    Assert.Contains("strings (plan.CheckResults |> List.map checkFrame)", source, StringComparison.Ordinal)

[<Fact>]
let ``retained merge-group control inventories are exact and independent`` () =
    let inventory relative =
        use document = JsonDocument.Parse(read relative)
        let node = document.RootElement
        let strings (name: string) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
        strings "controls", strings "cases", node.GetProperty("caseContract").GetString()
    let generatedIds, generatedCases, generatedContract = inventory "evidence/github-substrate-v2/gs2-07-5/generated-controls.json"
    let independentIds, independentCases, independentContract = inventory "evidence/github-substrate-v2/gs2-07-5/independent-controls.json"
    let expected = GitHubMergeGroupQualification.requiredControls |> List.map GitHubMergeGroupQualification.controlId
    Assert.Equal<string list>(expected, generatedIds)
    Assert.Equal<string list>(expected, independentIds)
    Assert.Equal(expected.Length, generatedCases.Length)
    Assert.Equal(expected.Length, independentCases.Length)
    let sameCases = generatedCases = independentCases
    Assert.False(sameCases)
    Assert.NotEqual(generatedContract, independentContract)

[<Fact>]
let ``Q3 retains separate merge-group execution paths`` () =
    let validator = read "eng/validate-github-merge-group-support.fsx"
    Assert.Contains("let executeGenerated control", validator, StringComparison.Ordinal)
    Assert.Contains("let executeIndependent control", validator, StringComparison.Ordinal)

[<Fact>]
let ``Q3 merge-group validator executes every control`` () =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-merge-group-support.fsx"; "--"; root ] do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("GITHUB_MERGE_GROUP_OK disposition=merge-group-qualified controls=31", output, StringComparison.Ordinal)
