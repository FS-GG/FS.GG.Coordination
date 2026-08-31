module FS.GG.Coordination.GitHubIssueFieldArchitectureTests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``issue field and qualification signatures precede implementations`` () =
    let githubProject = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(githubProject.IndexOf("IssueFields.fsi", StringComparison.Ordinal) < githubProject.IndexOf("IssueFields.fs\"", StringComparison.Ordinal))
    let qualificationProject = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"))
    Assert.True(qualificationProject.IndexOf("GitHubIssueFieldQualification.fsi", StringComparison.Ordinal) < qualificationProject.IndexOf("GitHubIssueFieldQualification.fs\"", StringComparison.Ordinal))

[<Fact>]
let ``independent issue field inventory is closed and every mutation turns red`` () =
    let independentInventory: GitHubIssueFieldControl list =
        [ GitHubIssueFieldControl.Pagination
          GitHubIssueFieldControl.DuplicateIdentity
          GitHubIssueFieldControl.TypeDrift
          GitHubIssueFieldControl.OptionDrift
          GitHubIssueFieldControl.StaleRevision
          GitHubIssueFieldControl.IncompleteObservation
          GitHubIssueFieldControl.NoOpMutation ]
    Assert.Equal<GitHubIssueFieldControl list>(GitHubIssueFieldQualification.requiredControls, independentInventory)
    let generated: GitHubIssueFieldControlResult list = independentInventory |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    let independent: GitHubIssueFieldControlResult list = independentInventory |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubIssueFieldQualification.validate generated independent)
    let broken = independent |> List.mapi (fun index result -> if index = 0 then { result with MutationRed = false } else result)
    match GitHubIssueFieldQualification.validate generated broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GIFQ-INDEPENDENT-NOT-RED" && finding.ControlId = "pagination")
    | Ok () -> failwith "an independent mutation that stayed green was accepted"

[<Fact>]
let ``Q3 issue field fixture is canonical synthetic and closed`` () =
    let path = Path.Combine(root, "tests/fixtures/github-issue-field/contract.json")
    let bytes = File.ReadAllBytes path
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    let names = document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList
    Assert.Equal<string list>([ "controls"; "generated"; "schema"; "synthetic" ], names)
    Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean())
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubIssueFieldQualification.requiredControls |> List.map GitHubIssueFieldQualification.controlId, controls)
    let text = Text.Encoding.UTF8.GetString bytes
    Assert.DoesNotContain("api.github.com", text, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("github.com/graphql", text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``registered issue field Q3 validator is offline and runs two producers`` () =
    let validatorPath = Path.Combine(root, "eng/validate-github-issue-field.fsx")
    Assert.True(File.Exists validatorPath)
    let validator = File.ReadAllText validatorPath
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("generatedResults", validator, StringComparison.Ordinal)
    Assert.Contains("independentResults", validator, StringComparison.Ordinal)
    Assert.Contains("tests/fixtures/github-issue-field/contract.json", validator, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-issue-field.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)

[<Fact>]
let ``registered issue field Q3 command passes against repository local evidence`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-issue-field.fsx"; "--"; "." ] do
        startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-issue-field-contract OK controls=7 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)

[<Fact>]
let ``issue field qualification contracts do not invert production dependencies`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"))
    Assert.DoesNotContain("FS.GG.Coordination.GitHub.fsproj", project, StringComparison.Ordinal)
