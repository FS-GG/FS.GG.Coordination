module FS.GG.Coordination.BootstrapCiTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Xunit

let private repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private verifier = Path.Combine(repositoryRoot, "eng/bootstrap-ci.fsx")
let private exactHead = String.replicate 40 "a"

let private runBootstrap root arguments =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add("fsi")
    startInfo.ArgumentList.Add(verifier)
    startInfo.ArgumentList.Add("--")
    arguments |> List.iter startInfo.ArgumentList.Add
    startInfo.ArgumentList.Add("--root")
    startInfo.ArgumentList.Add(root)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use childProcess = Process.Start startInfo
    let output = childProcess.StandardOutput.ReadToEnd()
    let error = childProcess.StandardError.ReadToEnd()
    childProcess.WaitForExit()
    childProcess.ExitCode, output.Trim(), error.Trim()

let private withScratch prefix action =
    let scratch = Directory.CreateTempSubdirectory(prefix)
    try action scratch.FullName
    finally scratch.Delete(true)

let private copyContract root =
    let eng = Directory.CreateDirectory(Path.Combine(root, "eng"))
    File.Copy(Path.Combine(repositoryRoot, "eng/bootstrap-ci-contract.json"), Path.Combine(eng.FullName, "bootstrap-ci-contract.json"))

let private withWorkflowMutation mutate verify =
    withScratch "fsgg-bootstrap-workflow-" (fun root ->
        copyContract root
        let workflows = Directory.CreateDirectory(Path.Combine(root, ".github/workflows"))
        let target = Path.Combine(workflows.FullName, "bootstrap-qualification.yml")
        File.Copy(Path.Combine(repositoryRoot, ".github/workflows/bootstrap-qualification.yml"), target)
        mutate target
        verify root)

let private vulnerabilityJson projectCount vulnerable =
    let vulnerability =
        if vulnerable then ",\"frameworks\":[{\"topLevelPackages\":[{\"vulnerabilities\":[{}]}]}]"
        else ""
    let projects =
        [ 1 .. projectCount ]
        |> List.map (fun index -> $"{{\"path\":\"/src/project-%d{index}.fsproj\"%s{vulnerability}}}")
        |> String.concat ","
    $"{{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[%s{projects}]}}"

let private validateVulnerability (contents: string) =
    withScratch "fsgg-bootstrap-vulnerability-" (fun root ->
        copyContract root
        let report = Path.Combine(root, "report.json")
        File.WriteAllText(report, contents)
        runBootstrap root [ "vulnerability"; "--report"; report ])

let private createArtifacts root =
    let paths =
        [ "deterministic-build/protocol.dll"
          "compiler-and-tests/architecture.trx"
          "dependency-and-security/vulnerability-report.json"
          "package-install-smoke/FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg"
          "evidence-manifest/contract.json" ]
    for relative in paths do
        let target = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        if relative = "evidence-manifest/contract.json" then
            File.Copy(Path.Combine(repositoryRoot, "eng/bootstrap-ci-contract.json"), target)
        else
            File.WriteAllText(target, $"artifact:%s{relative}")

let private withEvidence action =
    withScratch "fsgg-bootstrap-evidence-" (fun root ->
        copyContract root
        let artifacts = Path.Combine(root, "artifacts")
        Directory.CreateDirectory artifacts |> ignore
        createArtifacts artifacts
        let manifest = Path.Combine(root, "evidence.json")
        let collectCode, _, collectError =
            runBootstrap root [ "collect"; "--head"; exactHead; "--artifacts"; artifacts; "--output"; manifest ]
        Assert.Equal(0, collectCode)
        Assert.Equal("", collectError)
        action root artifacts manifest)

[<Fact>]
let ``bootstrap workflow satisfies the exact five-gate contract`` () =
    let exitCode, output, error = runBootstrap repositoryRoot [ "workflow" ]
    Assert.Equal(0, exitCode)
    Assert.Equal("BOOTSTRAP_CI_OK mode=workflow", output)
    Assert.Equal("", error)

[<Fact>]
let ``bootstrap workflow rejects a missing gate`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  deterministic-build:", "  deterministic-build-removed:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-job-set", error))

[<Fact>]
let ``bootstrap workflow rejects mutable action references`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v7")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-action-pin", error))

[<Fact>]
let ``bootstrap workflow rejects checkout not bound to the evidence candidate`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("          ref: ${{ github.event.pull_request.head.sha || github.sha }}\n", "")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-checkout-candidate", error))

[<Fact>]
let ``bootstrap workflow rejects authority expansion`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("contents: read", "contents: write")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-authority-ceiling", error))

[<Fact>]
let ``bootstrap workflow rejects runner context in job environment`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("/tmp/fsgg-${{ github.run_id }}-nuget-deterministic-build", "${{ runner.temp }}/nuget-deterministic-build")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-authority-ceiling", error))

[<Fact>]
let ``complete vulnerability report is accepted`` () =
    let exitCode, output, error = validateVulnerability (vulnerabilityJson 8 false)
    Assert.Equal(0, exitCode)
    Assert.Equal("BOOTSTRAP_CI_OK mode=vulnerability", output)
    Assert.Equal("", error)

[<Theory>]
[<InlineData(7, false, "vulnerability-report-completeness")>]
[<InlineData(8, true, "vulnerable-package")>]
let ``partial and vulnerable reports are rejected`` projectCount vulnerable rule =
    let exitCode, _, error = validateVulnerability (vulnerabilityJson projectCount vulnerable)
    Assert.NotEqual(0, exitCode)
    Assert.Contains($"rule=%s{rule}", error)

[<Fact>]
let ``malformed vulnerability report is rejected`` () =
    let exitCode, _, error = validateVulnerability "not-json"
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=vulnerability-report-unreadable", error)

[<Fact>]
let ``exact-head evidence and artifact digests are accepted`` () =
    withEvidence (fun root artifacts manifest ->
        let exitCode, output, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.Equal(0, exitCode)
        Assert.Equal("BOOTSTRAP_CI_OK mode=evidence", output)
        Assert.Equal("", error))

[<Fact>]
let ``evidence rejects a stale candidate`` () =
    withEvidence (fun root artifacts manifest ->
        let differentHead = String.replicate 40 "b"
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; differentHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-candidate", error))

[<Fact>]
let ``evidence rejects a missing gate`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        document["gates"].AsArray().RemoveAt(0)
        File.WriteAllText(manifest, document.ToJsonString())
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-gate-set", error))

[<Fact>]
let ``evidence rejects an artifact changed after collection`` () =
    withEvidence (fun root artifacts manifest ->
        File.AppendAllText(Path.Combine(artifacts, "deterministic-build/protocol.dll"), "tampered")
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-artifact-digest", error))
