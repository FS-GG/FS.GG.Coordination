module FS.GG.Coordination.GitHubEventEnvelopeArchitectureTests

open System
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
let ``event envelope surface remains pure and repository local`` () =
    let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventEnvelopeQualification.fs"
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventEnvelopeQualification.fsi"
    let project = read "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "enqueue"; "dequeue"; "webhook"; "getenvironmentvariable" ] do
        Assert.DoesNotContain(forbidden, (source + signature).ToLowerInvariant())
    Assert.DoesNotContain("FS.GG.Coordination.GitHub", project, StringComparison.Ordinal)
    Assert.DoesNotContain("FS.GG.Coordination.Core", project, StringComparison.Ordinal)
    Assert.Contains("GitHubEventEnvelopeQualification.fsi", project, StringComparison.Ordinal)

[<Fact>]
let ``generated and independent retained cases are complete and distinct`` () =
    let cases relative =
        use document = JsonDocument.Parse(read relative)
        document.RootElement.GetProperty("cases").EnumerateArray()
        |> Seq.map _.GetString() |> Seq.toList
    let generated = cases "evidence/github-substrate-v2/gs2-07-1/generated-controls.json"
    let independent = cases "evidence/github-substrate-v2/gs2-07-1/independent-controls.json"
    Assert.Equal(GitHubEventEnvelopeQualification.requiredControls.Length, generated.Length)
    Assert.Equal(GitHubEventEnvelopeQualification.requiredControls.Length, independent.Length)
    Assert.NotEqual<string list>(generated, independent)

[<Fact>]
let ``Q3 keeps separate generated and independent execution paths`` () =
    let validator = read "eng/validate-github-event-envelope.fsx"
    Assert.Contains("let executeGenerated control", validator, StringComparison.Ordinal)
    Assert.Contains("let executeIndependent control", validator, StringComparison.Ordinal)
    Assert.DoesNotContain("let execute control independent", validator, StringComparison.Ordinal)
