module FS.GG.Coordination.GitHubProjectAdapterArchitectureTests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``Project adapter public and qualification signatures precede implementations`` () =
    let github = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(github.IndexOf("ProjectAdapter.fsi", StringComparison.Ordinal) < github.IndexOf("ProjectAdapter.fs\"", StringComparison.Ordinal))
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/ProjectAdapter.fsi"))
    Assert.Contains("ProjectionOnly", signature, StringComparison.Ordinal)
    for forbidden in [ "ClaimAuthority"; "ReviewAuthority"; "CompletionAuthority"; "authorizeTransition" ] do
        Assert.DoesNotContain(forbidden, signature, StringComparison.OrdinalIgnoreCase)
    let qualification = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"))
    Assert.True(qualification.IndexOf("GitHubProjectAdapterQualification.fsi", StringComparison.Ordinal) < qualification.IndexOf("GitHubProjectAdapterQualification.fs\"", StringComparison.Ordinal))
    Assert.DoesNotContain("FS.GG.Coordination.GitHub.fsproj", qualification, StringComparison.Ordinal)

[<Fact>]
let ``Project adapter fixture is canonical synthetic offline and closed`` () =
    let bytes = File.ReadAllBytes(Path.Combine(root, "tests/fixtures/github-project-adapter/contract.json"))
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    Assert.Equal<string list>([ "controls"; "generated"; "schema"; "synthetic" ], document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
    Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean())
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubProjectAdapterQualification.requiredControls |> List.map GitHubProjectAdapterQualification.controlId, controls)
    let text = Text.Encoding.UTF8.GetString bytes
    for forbidden in [ "api.github.com"; "github.com/graphql"; "token" ] do Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``registered Project adapter validator is offline uses two producers and passes`` () =
    let path = Path.Combine(root, "eng/validate-github-project-adapter.fsx")
    let validator = File.ReadAllText path
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("generatedResults", validator, StringComparison.Ordinal)
    Assert.Contains("independentResults", validator, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-project-adapter.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-project-adapter.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-project-adapter-contract OK controls=10 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)
