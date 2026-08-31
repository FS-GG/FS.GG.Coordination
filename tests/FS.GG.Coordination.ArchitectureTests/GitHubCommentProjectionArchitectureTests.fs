module FS.GG.Coordination.GitHubCommentProjectionArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``comment projection public and qualification signatures precede implementations without authority leakage`` () =
    let github = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(github.IndexOf("CommentProjectionAdapter.fsi", StringComparison.Ordinal) < github.IndexOf("CommentProjectionAdapter.fs\"", StringComparison.Ordinal))
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/CommentProjectionAdapter.fsi"))
    Assert.Contains("JournalAuthority", signature, StringComparison.Ordinal)
    Assert.Contains("ProjectionTrustFailure", signature, StringComparison.Ordinal)
    for forbidden in [ "ClaimAuthority"; "ReviewAuthority"; "CompletionAuthority"; "authorizeTransition"; "HttpClient" ] do
        Assert.DoesNotContain(forbidden, signature, StringComparison.OrdinalIgnoreCase)
    let qualification = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"))
    Assert.True(qualification.IndexOf("GitHubCommentProjectionQualification.fsi", StringComparison.Ordinal) < qualification.IndexOf("GitHubCommentProjectionQualification.fs\"", StringComparison.Ordinal))
    Assert.DoesNotContain("FS.GG.Coordination.GitHub.fsproj", qualification, StringComparison.Ordinal)

[<Fact>]
let ``comment projection fixture is canonical synthetic offline and closed`` () =
    let bytes = File.ReadAllBytes(Path.Combine(root, "tests/fixtures/github-comment-projection/contract.json"))
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    Assert.Equal<string list>([ "controls"; "generated"; "schema"; "synthetic" ], document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
    Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean())
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubCommentProjectionQualification.requiredControls |> List.map GitHubCommentProjectionQualification.controlId, controls)
    let text = Text.Encoding.UTF8.GetString bytes
    for forbidden in [ "api.github.com"; "github.com/graphql"; "token" ] do Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``registered comment projection validator is offline uses independent producers and passes`` () =
    let path = Path.Combine(root, "eng/validate-github-comment-projection.fsx")
    let validator = File.ReadAllText path
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("generatedResults", validator, StringComparison.Ordinal)
    Assert.Contains("independentResults", validator, StringComparison.Ordinal)
    let independentStart = validator.IndexOf("let independentResults", StringComparison.Ordinal)
    let independentEnd = validator.IndexOf("let generated =", independentStart, StringComparison.Ordinal)
    let independentBody = validator.Substring(independentStart, independentEnd - independentStart)
    Assert.DoesNotContain("generatedResults", independentBody, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-comment-projection.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-comment-projection.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-comment-projection-contract OK controls=12 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)
