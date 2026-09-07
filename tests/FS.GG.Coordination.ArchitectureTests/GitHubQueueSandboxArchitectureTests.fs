module FS.GG.Coordination.GitHubQueueSandboxArchitectureTests

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
let ``queue sandbox contract is pure and identity bounded`` () =
    let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fs"
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fsi"
    let project = read "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"
    Assert.True(project.IndexOf("GitHubQueueSandbox.fsi", StringComparison.Ordinal) < project.IndexOf("GitHubQueueSandbox.fs\"", StringComparison.Ordinal))
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "getenvironmentvariable"; "process.start"; "github_token" ] do
        Assert.DoesNotContain(forbidden, (source + signature).ToLowerInvariant())
    Assert.Contains("FS-GG/FS.GG.GitHub.Substrate.Sandbox", source, StringComparison.Ordinal)
    Assert.Contains("1353050537L", source, StringComparison.Ordinal)
    for required in [ "CandidateSha"; "MergeGroupHeadSha"; "CurrentBaseObservationRevision"; "CurrentRequiredChecks"; "CurrentClaimGeneration"; "CurrentReviewDigest"; "CurrentDependencyDigest"; "CurrentReleaseObligationsMet"; "CurrentSettingsDigest" ] do
        Assert.Contains(required, source + signature, StringComparison.Ordinal)

[<Fact>]
let ``retained pilot and recovery inventories are exact and independently authored`` () =
    let inventory relative =
        use document = JsonDocument.Parse(read relative)
        let node = document.RootElement
        let strings (name: string) : string list = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
        strings "pilotControls", strings "recoveryControls", strings "cases", node.GetProperty("caseContract").GetString()
    let generatedPilot, generatedRecovery, generatedCases, generatedContract = inventory "evidence/github-substrate-v2/gs2-07-6/generated-controls.json"
    let independentPilot, independentRecovery, independentCases, independentContract = inventory "evidence/github-substrate-v2/gs2-07-6/independent-controls.json"
    Assert.Equal<string list>(GitHubQueueSandbox.pilotControlIds, generatedPilot)
    Assert.Equal<string list>(GitHubQueueSandbox.pilotControlIds, independentPilot)
    Assert.Equal<string list>(GitHubQueueSandbox.recoveryControlIds, generatedRecovery)
    Assert.Equal<string list>(GitHubQueueSandbox.recoveryControlIds, independentRecovery)
    Assert.Equal(generatedPilot.Length + generatedRecovery.Length - 10, generatedCases.Length)
    Assert.Equal(independentPilot.Length + independentRecovery.Length - 10, independentCases.Length)
    Assert.NotEqual<string>(String.concat "\n" generatedCases, String.concat "\n" independentCases)
    Assert.NotEqual(generatedContract, independentContract)

let private runGate script expected =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; script; "--"; root ] do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal("", error.Trim())
    Assert.Equal(0, child.ExitCode)
    Assert.Contains(expected, output, StringComparison.Ordinal)

[<Fact>]
let ``Q4 and Q6 execute generated and independent controls offline`` () =
    for script in [ "eng/validate-github-queue-sandbox-pilot.fsx"; "eng/validate-github-queue-sandbox-recovery.fsx" ] do
        let text = read script
        Assert.Contains("let generated", text, StringComparison.Ordinal)
        Assert.Contains("let independent", text, StringComparison.Ordinal)
        for forbidden in [ "new HttpClient"; "api.github.com"; "Environment.GetEnvironmentVariable("; "Process.Start(" ] do Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal)
    runGate "eng/validate-github-queue-sandbox-pilot.fsx" "GITHUB_QUEUE_SANDBOX_PILOT_OK disposition=queue-pilot-qualified controls=24"
    runGate "eng/validate-github-queue-sandbox-recovery.fsx" "GITHUB_QUEUE_SANDBOX_RECOVERY_OK disposition=queue-sandbox-recovered controls=20"
