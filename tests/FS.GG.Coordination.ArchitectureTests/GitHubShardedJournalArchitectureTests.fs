module FS.GG.Coordination.GitHubShardedJournalArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``journal adapter signatures precede implementations and stay transport free`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(project.IndexOf("ShardedJournalAdapter.fsi", StringComparison.Ordinal) < project.IndexOf("ShardedJournalAdapter.fs\"", StringComparison.Ordinal))
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Process.Start" ] do Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fsi"))
    for required in [ "CanonicalBlob"; "JournalCheckpoint"; "EffectiveBranchRule"; "planConflict" ] do Assert.Contains(required, signature, StringComparison.Ordinal)

[<Fact>]
let ``journal fixture is canonical synthetic offline and complete`` () =
    let bytes = File.ReadAllBytes(Path.Combine(root, "tests/fixtures/github-sharded-journal/contract.json"))
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    Assert.Equal<string list>([ "controls"; "generated"; "schema"; "synthetic" ], document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
    Assert.True(document.RootElement.GetProperty("synthetic").GetBoolean())
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubShardedJournalQualification.requiredControls |> List.map GitHubShardedJournalQualification.controlId, controls)

[<Fact>]
let ``registered journal validator is offline independent and passes`` () =
    let path = Path.Combine(root, "eng/validate-github-sharded-journal.fsx")
    let validator = File.ReadAllText path
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("generatedResults", validator, StringComparison.Ordinal)
    Assert.Contains("independentResults", validator, StringComparison.Ordinal)
    Assert.Contains("validateProtection", validator, StringComparison.Ordinal)
    let independentStart = validator.IndexOf("let independentResults", StringComparison.Ordinal)
    let independentEnd = validator.IndexOf("let generated =", independentStart, StringComparison.Ordinal)
    let independentBody = validator.Substring(independentStart, independentEnd - independentStart)
    Assert.DoesNotContain("requiredControls", independentBody, StringComparison.Ordinal)
    Assert.DoesNotContain("result control true", independentBody, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-sharded-journal.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-sharded-journal.fsx"; "--"; "." ] do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-sharded-journal-contract OK controls=18 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)
