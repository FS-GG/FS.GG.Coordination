module FS.GG.Coordination.GitHubRepositorySettingsArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``repository settings signature precedes implementation and remains pure`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(project.IndexOf("RepositorySettingsAdapter.fsi", StringComparison.Ordinal) < project.IndexOf("RepositorySettingsAdapter.fs\"", StringComparison.Ordinal))
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RepositorySettingsAdapter.fs"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Process.Start"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RepositorySettingsAdapter.fsi"))
    for required in [ "RepositoryIdentity"; "SurfaceObservation"; "SettingsOperation"; "SettingsReconcileOutcome"; "val plan"; "val reconcile" ] do Assert.Contains(required, signature, StringComparison.Ordinal)

[<Fact>]
let ``repository settings fixture is canonical synthetic offline and complete`` () =
    let bytes = File.ReadAllBytes(Path.Combine(root, "tests/fixtures/github-repository-settings/contract.json"))
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    Assert.Equal<string list>([ "controls"; "generated"; "schema"; "synthetic" ], document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
    Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean())
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubRepositorySettingsQualification.requiredControls |> List.map GitHubRepositorySettingsQualification.controlId, controls)

[<Fact>]
let ``registered repository settings validator is offline independent and passes`` () =
    let path = Path.Combine(root, "eng/validate-github-repository-settings.fsx")
    let validator = File.ReadAllText path
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("generatedResults", validator, StringComparison.Ordinal)
    Assert.Contains("independentResults", validator, StringComparison.Ordinal)
    Assert.Contains("SettingsPartiallyApplied", validator, StringComparison.Ordinal)
    let independentStart = validator.IndexOf("let independentResults", StringComparison.Ordinal)
    let independentEnd = validator.IndexOf("let generatedResultsValue", independentStart, StringComparison.Ordinal)
    let independentBody = validator.Substring(independentStart, independentEnd - independentStart)
    Assert.DoesNotContain("requiredControls", independentBody, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-repository-settings.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-repository-settings.fsx"; "--"; "." ] do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-repository-settings-contract OK controls=20 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)
