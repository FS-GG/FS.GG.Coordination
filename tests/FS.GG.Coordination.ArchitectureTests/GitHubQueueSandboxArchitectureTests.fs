module FS.GG.Coordination.GitHubQueueSandboxArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
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

let private copyTree source destination =
    Directory.CreateDirectory(destination) |> ignore
    for file in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(source, file)
        let target = Path.Combine(destination, relative)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.Copy(file, target)

let private isolatedEvidence () =
    let directory = Directory.CreateTempSubdirectory("gs2-07-6-tamper-")
    let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2")
    copyTree (Path.Combine(evidenceRoot, "gs2-07-6")) (Path.Combine(directory.FullName, "evidence/github-substrate-v2/gs2-07-6"))
    let accepted = Path.Combine(directory.FullName, "evidence/github-substrate-v2/accepted")
    Directory.CreateDirectory(accepted) |> ignore
    File.Copy(Path.Combine(evidenceRoot, "accepted/GS2-07.5.json"), Path.Combine(accepted, "GS2-07.5.json"))
    let source = Path.Combine(directory.FullName, "src/FS.GG.Coordination.Qualification.Contracts")
    Directory.CreateDirectory(source) |> ignore
    File.Copy(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fs"), Path.Combine(source, "GitHubQueueSandbox.fs"))
    directory

let private runGateAt evidenceRoot script =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; script; "--"; evidenceRoot ] do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

let private mutateJson path mutation =
    let document = JsonNode.Parse(File.ReadAllText path)
    mutation document
    File.WriteAllText(path, document.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

[<Fact>]
let ``Q4 and Q6 execute generated and independent controls offline`` () =
    for script in [ "eng/validate-github-queue-sandbox-pilot.fsx"; "eng/validate-github-queue-sandbox-recovery.fsx" ] do
        let text = read script
        Assert.Contains("let generated", text, StringComparison.Ordinal)
        Assert.Contains("let independent", text, StringComparison.Ordinal)
    for script in [ "eng/validate-github-queue-sandbox-pilot.fsx"; "eng/validate-github-queue-sandbox-recovery.fsx"; "eng/validate-github-queue-routine-burst.fsx" ] do
        let text = read script
        for forbidden in [ "new HttpClient"; "api.github.com"; "Environment.GetEnvironmentVariable("; "Process.Start(" ] do Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal)
    runGate "eng/validate-github-queue-sandbox-pilot.fsx" "GITHUB_QUEUE_SANDBOX_PILOT_OK disposition=queue-pilot-qualified controls=24"
    runGate "eng/validate-github-queue-sandbox-recovery.fsx" "GITHUB_QUEUE_SANDBOX_RECOVERY_OK disposition=queue-sandbox-recovered controls=20"
    runGate "eng/validate-github-queue-routine-burst.fsx" "GITHUB_QUEUE_ROUTINE_BURST_OK subjects=2 hints=5"

[<Fact>]
let ``Q4 and Q6 reject contradictory retained hosted evidence`` () =
    let cases : (string * (JsonNode -> unit)) list =
        [ "candidate head", fun node -> (node["candidate"].AsObject())["sha"] <- JsonValue.Create(String.replicate 40 "a")
          "initial base", fun node -> (node["initialAdmission"].AsObject())["baseSha"] <- JsonValue.Create(String.replicate 40 "b")
          "dependency authority", fun node -> (((node["authority"].AsObject())["current"]).AsObject())["dependencyDigest"] <- JsonValue.Create(String.replicate 64 "c")
          "settings authority", fun node -> (((node["authority"].AsObject())["observed"]).AsObject())["settingsDigest"] <- JsonValue.Create(String.replicate 64 "d")
          "distinct authority observation", fun node -> (((node["authority"].AsObject())["current"]).AsObject())["observedAtUnixSeconds"] <- JsonValue.Create(1L) ]
    for name, mutation in cases do
        let isolated = isolatedEvidence()
        try
            let hosted = Path.Combine(isolated.FullName, "evidence/github-substrate-v2/gs2-07-6/hosted-run.json")
            mutateJson hosted mutation
            for script in [ "eng/validate-github-queue-sandbox-pilot.fsx"; "eng/validate-github-queue-sandbox-recovery.fsx" ] do
                let exitCode, output, error = runGateAt isolated.FullName script
                Assert.NotEqual(0, exitCode)
                Assert.DoesNotContain("_OK", output, StringComparison.Ordinal)
                Assert.False(String.IsNullOrWhiteSpace error, $"{name} should fail closed in {script}")
        finally Directory.Delete(isolated.FullName, true)

[<Fact>]
let ``Q4 and Q6 reject a retained proof bound to the wrong queue ref`` () =
    let isolated = isolatedEvidence()
    try
        let proofPath = Path.Combine(isolated.FullName, "evidence/github-substrate-v2/gs2-07-6/hosted-proof.json")
        mutateJson proofPath (fun node -> node.AsObject()["fullRef"] <- JsonValue.Create("refs/heads/gh-readonly-queue/wrong"))
        for script in [ "eng/validate-github-queue-sandbox-pilot.fsx"; "eng/validate-github-queue-sandbox-recovery.fsx" ] do
            let exitCode, output, _ = runGateAt isolated.FullName script
            Assert.NotEqual(0, exitCode)
            Assert.DoesNotContain("_OK", output, StringComparison.Ordinal)
    finally Directory.Delete(isolated.FullName, true)

[<Fact>]
let ``Q6 rejects tampered checkpoint journal handoff and expiry records`` () =
    let cases : (string * string * (JsonNode -> unit)) list =
        [ "checkpoint", "durable-checkpoint.json", fun node -> node.AsObject()["candidateSha"] <- JsonValue.Create(String.replicate 40 "e")
          "journal", "final-journal.json", fun node -> (((node["appliedEffects"].AsArray())[0]).AsObject())["resultDigest"] <- JsonValue.Create(String.replicate 64 "f")
          "handoff", "process-handoff.json", fun node -> node.AsObject()["resumePid"] <- (node["preparePid"].DeepClone())
          "expiry", "expired-admission-refusal.json", fun node -> node.AsObject()["decision"] <- JsonValue.Create("accepted-expired-admission") ]
    for name, relative, mutation in cases do
        let isolated = isolatedEvidence()
        try
            let path = Path.Combine(isolated.FullName, "evidence/github-substrate-v2/gs2-07-6", relative)
            mutateJson path mutation
            let exitCode, output, error = runGateAt isolated.FullName "eng/validate-github-queue-sandbox-recovery.fsx"
            Assert.NotEqual(0, exitCode)
            Assert.DoesNotContain("_OK", output, StringComparison.Ordinal)
            Assert.False(String.IsNullOrWhiteSpace error, $"{name} should fail closed")
        finally Directory.Delete(isolated.FullName, true)

[<Fact>]
let ``routine burst gate rejects stale hosted heads and cleanup mismatch`` () =
    let cases : (string * string * (JsonNode -> unit)) list =
        [ "current head", "routine-burst.json", fun node -> (((((node["decisions"].AsArray())[0]).AsObject())["currentRun"]).AsObject())["headSha"] <- JsonValue.Create(String.replicate 40 "9")
          "cleanup", "burst-cleanup.json", fun node -> node.AsObject()["settingsDigest"] <- JsonValue.Create(String.replicate 64 "8") ]
    for name, relative, mutation in cases do
        let isolated = isolatedEvidence()
        try
            let path = Path.Combine(isolated.FullName, "evidence/github-substrate-v2/gs2-07-6", relative)
            mutateJson path mutation
            let exitCode, output, error = runGateAt isolated.FullName "eng/validate-github-queue-routine-burst.fsx"
            Assert.NotEqual(0, exitCode)
            Assert.DoesNotContain("_OK", output, StringComparison.Ordinal)
            Assert.False(String.IsNullOrWhiteSpace error, $"{name} should fail closed")
        finally Directory.Delete(isolated.FullName, true)

[<Fact>]
let ``hosted harness fails closed and retains typed exact-head proof`` () =
    let harness = read "evidence/github-substrate-v2/gs2-07-6/execute-sandbox-pilot.sh"
    let workflow = read "evidence/github-substrate-v2/gs2-07-6/sandbox-queue-workflow.yml"
    for required in [ "ref_status"; "cleanup_armed=false"; "cleanup_failed"; "on_exit"; "prepare_phase"; "resume_phase"; "CHECKPOINT_SEALED"; "EXPIRED_ADMISSION_REFUSED"; "separateProcesses"; "record_effect"; "record_compensation"; "final-journal.json"; "DETERMINISTIC_RETRY"; "HOSTED_ARTIFACT"; "final_settings"; "final_branches"; "final_workflows" ] do
        Assert.Contains(required, harness, StringComparison.Ordinal)
    Assert.True(harness.IndexOf("[[ $(ref_status", StringComparison.Ordinal) < harness.IndexOf("cleanup_armed=true", StringComparison.Ordinal))
    for required in [ "hosted-proof.json"; "repositoryId"; "runId"; "mergeGroupHeadSha"; "fullRef"; "workflowSha"; "actions/upload-artifact@v4"; "retention-days: 90" ] do
        Assert.Contains(required, workflow, StringComparison.Ordinal)
