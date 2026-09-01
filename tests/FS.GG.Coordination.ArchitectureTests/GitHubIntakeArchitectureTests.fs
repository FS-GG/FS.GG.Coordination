module FS.GG.Coordination.GitHubIntakeArchitectureTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``intake boundary is signature first and contains no production transport`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(project.IndexOf("IntakeAdapter.fsi", StringComparison.Ordinal) < project.IndexOf("IntakeAdapter.fs\"", StringComparison.Ordinal))
    let implementation = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/IntakeAdapter.fs"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "Environment.GetEnvironmentVariable"; "api.github.com" ] do Assert.DoesNotContain(forbidden, implementation, StringComparison.Ordinal)
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/IntakeAdapter.fsi"))
    for required in [ "ProtocolInitializationIntent"; "IntakePlan"; "applyControlled"; "Compensate" ] do Assert.Contains(required, signature, StringComparison.Ordinal)

[<Fact>]
let ``intake fixture inventory is canonical and independently owned`` () =
    let fixture = File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/gs2-05-3/corpus.json"))
    for control in GitHubIntakeQualification.requiredControls |> List.map GitHubIntakeQualification.controlId do Assert.Contains("\"" + control + "\"", fixture, StringComparison.Ordinal)
    let qualification = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubIntakeQualification.fs"))
    Assert.DoesNotContain("FS.GG.Coordination.GitHub", qualification, StringComparison.Ordinal)

[<Fact>]
let ``registered intake gate runs cold and offline`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-intake.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let errors = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, errors + output)
    Assert.Contains("github-intake-contract OK controls=12 q=Q3 network=offline provenance=synthetic", output, StringComparison.Ordinal)
