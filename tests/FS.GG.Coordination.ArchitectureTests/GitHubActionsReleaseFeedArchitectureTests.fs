module FS.GG.Coordination.GitHubActionsReleaseFeedArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let signaturePrecedesPureImplementation () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(project.IndexOf("ActionsReleaseFeedAdapter.fsi", StringComparison.Ordinal) < project.IndexOf("ActionsReleaseFeedAdapter.fs\"", StringComparison.Ordinal))
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/ActionsReleaseFeedAdapter.fs"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Process.Start"; "Environment.GetEnvironmentVariable"; "workflow_dispatch" ] do Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/ActionsReleaseFeedAdapter.fsi"))
    for required in [ "ArtifactSurface"; "LifecycleOutcome"; "ArtifactSurfaceObservation"; "EvidenceStage"; "ServedContent"; "val validate"; "val observeServedContent"; "val validateStages" ] do Assert.Contains(required, signature, StringComparison.Ordinal)

[<Fact>]
let fixtureIsCanonicalSyntheticAndComplete () =
    let bytes = File.ReadAllBytes(Path.Combine(root, "tests/fixtures/github-actions-release-feed/contract.json"))
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    Assert.Equal<string list>([ "controls"; "schema"; "synthetic" ], document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
    Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean())
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubActionsReleaseFeedQualification.requiredControls |> List.map GitHubActionsReleaseFeedQualification.controlId, controls)

[<Fact>]
let registeredValidatorIsOfflineIndependentAndPasses () =
    let validator = File.ReadAllText(Path.Combine(root, "eng/validate-github-actions-release-feed.fsx"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    for required in [ "let generated"; "let independent"; "AuthenticatedRetrieval"; "PublicServedBytes"; "UploadAccepted"; "StaleSurface" ] do Assert.Contains(required, validator, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-actions-release-feed.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-actions-release-feed.fsx"; "--"; "." ] do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-actions-release-feed-contract OK controls=18 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)
