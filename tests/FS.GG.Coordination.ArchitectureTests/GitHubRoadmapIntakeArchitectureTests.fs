module FS.GG.Coordination.GitHubRoadmapIntakeArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private sha path = File.ReadAllBytes(path) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``roadmap intake boundary is signature first bounded and transport free`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(project.IndexOf("RoadmapIntakeAdapter.fsi", StringComparison.Ordinal) < project.IndexOf("RoadmapIntakeAdapter.fs\"", StringComparison.Ordinal))
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RoadmapIntakeAdapter.fsi"))
    for required in [ "RoadmapDefinition"; "RoadmapIssueType"; "RoadmapTarget"; "SetParent"; "SetDependency"; "SetStart"; "SetTarget"; "SetField"; "EnsureProjectProjection"; "RoadmapCost"; "validatePlan"; "inspect"; "applyControlled" ] do
        Assert.Contains(required, signature, StringComparison.Ordinal)
    let implementation = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RoadmapIntakeAdapter.fs"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable"; "OrganizationWideReconcile"; "FullProjectTraversal" ] do
        Assert.DoesNotContain(forbidden, implementation, StringComparison.Ordinal)

[<Fact>]
let ``roadmap intake Q3 evidence is independently inventoried and predecessor bound`` () =
    let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-4/corpus.json")
    let independentPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-4/independent-expectations.json")
    use corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
    use independent = JsonDocument.Parse(File.ReadAllBytes independentPath)
    let expected = GitHubRoadmapIntakeQualification.requiredControls |> List.map GitHubRoadmapIntakeQualification.controlId
    let controls (document: JsonDocument) = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(expected, controls corpus)
    Assert.Equal<string list>(expected, controls independent)
    Assert.Equal(sha (Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.9.json")), corpus.RootElement.GetProperty("acceptedPredecessorReceiptSha256").GetString())
    let validator = File.ReadAllText(Path.Combine(root, "eng/validate-github-roadmap-intake.fsx"))
    Assert.Contains("let independentMutation", validator, StringComparison.Ordinal)
    Assert.DoesNotContain("MutationRed = true", validator, StringComparison.Ordinal)

[<Fact>]
let ``registered roadmap intake gate runs cold offline and without production writes`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-roadmap-intake.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let errors = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, errors + output)
    Assert.Contains("github-roadmap-intake-contract OK controls=18 q=Q3 network=offline provenance=generated+independent production-writes=0", output, StringComparison.Ordinal)
