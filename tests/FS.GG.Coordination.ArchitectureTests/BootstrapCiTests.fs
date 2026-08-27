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
    let requiredProjects =
        [ "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj"
          "src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj"
          "src/FS.GG.Coordination.Core/FS.GG.Coordination.Core.fsproj"
          "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"
          "src/FS.GG.Coordination.Protocol/FS.GG.Coordination.Protocol.fsproj"
          "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"
          "tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj"
          "tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj" ]
    let projects =
        requiredProjects
        |> List.take projectCount
        |> List.map (fun path -> $"{{\"path\":\"%s{path}\"%s{vulnerability}}}")
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
          "bootstrap-recovery/result.json"
          "evidence-manifest/contract.json" ]
    for relative in paths do
        let target = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        if relative = "evidence-manifest/contract.json" then
            File.Copy(Path.Combine(repositoryRoot, "eng/bootstrap-ci-contract.json"), target)
        elif relative = "bootstrap-recovery/result.json" then
            let packageDigest = String.replicate 64 "b"
            File.WriteAllText(
                target,
                $"{{\"schema\":\"fsgg.coordination.bootstrap-recovery/1\",\"candidate\":\"%s{exactHead}\",\"packageSha256\":\"%s{packageDigest}\",\"publishedSources\":[\"https://api.nuget.org/v3/index.json\"],\"stages\":[\"clone\",\"restore\",\"build\",\"unit-tests\",\"architecture-tests\",\"pack\",\"install\",\"execute\"]}}\n")
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

let private runPackageSmoke scratch packageOverride =
    let startInfo = ProcessStartInfo("bash")
    startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "eng/package-install-smoke.sh"))
    startInfo.ArgumentList.Add(scratch)
    packageOverride |> Option.iter (fun path -> startInfo.Environment["FSGG_BOOTSTRAP_PACKAGE_OVERRIDE"] <- path)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use childProcess = Process.Start startInfo
    let output = childProcess.StandardOutput.ReadToEnd()
    let error = childProcess.StandardError.ReadToEnd()
    childProcess.WaitForExit()
    childProcess.ExitCode, output.Trim(), error.Trim()

[<Fact>]
let ``bootstrap workflow satisfies the exact six-job contract`` () =
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
let ``bootstrap workflow rejects duplicate job identities`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  compiler-and-tests:", "  deterministic-build:\n  compiler-and-tests:")))
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
let ``bootstrap workflow rejects pinned but unapproved actions`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Upload the evidence manifest", "      - name: Unapproved pinned action\n        uses: example/deploy@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n      - name: Upload the evidence manifest")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-byte-contract", error))

[<Fact>]
let ``bootstrap workflow rejects cross-run artifact downloads`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("          path: ${{ runner.temp }}/bootstrap-artifacts", "          path: ${{ runner.temp }}/bootstrap-artifacts\n          run-id: 1")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-byte-contract", error))

[<Fact>]
let ``bootstrap workflow rejects checkout not bound to the evidence candidate`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("          ref: ${{ github.event.pull_request.head.sha || github.sha }}", "          # ref: ${{ github.event.pull_request.head.sha || github.sha }}")))
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
            Assert.Contains("rule=workflow-permissions", error)
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
let ``bootstrap workflow rejects incomplete triggers`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  pull_request:", "  # pull_request:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-trigger", error))

[<Fact>]
let ``bootstrap workflow rejects trigger children without top-level on`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("on:\n", "off:\n")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-trigger", error))

[<Fact>]
let ``bootstrap workflow rejects a vacuous action inventory`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("uses:", "uses-disabled:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-action-pin", error))

[<Fact>]
let ``bootstrap workflow rejects a missing required command`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("        run: dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror", "        run: dotnet build FS.GG.Coordination.sln --configuration Release --no-restore\n        # run: dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-command-contract", error))

[<Fact>]
let ``bootstrap workflow rejects shell success suppression`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror", "dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror || true")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-command-contract", error))

[<Fact>]
let ``bootstrap workflow rejects any unexpected executable command`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("          dotnet restore FS.GG.Coordination.sln --locked-mode", "          set +o errexit\n          dotnet restore FS.GG.Coordination.sln --locked-mode")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-command-contract", error))

[<Fact>]
let ``bootstrap workflow rejects checkout ref outside with`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("        with:\n          ref: ${{ github.event.pull_request.head.sha || github.sha }}", "        env:\n          ref: ${{ github.event.pull_request.head.sha || github.sha }}")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-checkout-candidate", error))

[<Fact>]
let ``bootstrap workflow rejects conditional gates`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Build with warnings as errors", "      - name: Build with warnings as errors\n        if: false")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-gate-bypass", error))

[<Fact>]
let ``bootstrap workflow rejects package override seam`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Pack and consume through a clean project", "      - name: Pack and consume through a clean project\n        env:\n          FSGG_BOOTSTRAP_PACKAGE_OVERRIDE: fake.nupkg")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-authority-ceiling", error))

[<Fact>]
let ``bootstrap workflow rejects imported v1 completion machinery`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Upload the evidence manifest", "      - name: Forbidden completion route\n        run: scripts/fsgg-coord delivery\n      - name: Upload the evidence manifest")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-authority-ceiling", error))

[<Fact>]
let ``workflow comments cannot bypass the exact byte contract`` () =
    withWorkflowMutation
        (fun path -> File.AppendAllText(path, "\n# scripts/fsgg-coord delivery is intentionally absent\n"))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-byte-contract", error)
            Assert.DoesNotContain("rule=workflow-authority-ceiling", error))

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
let ``unsafe vulnerability source is rejected`` () =
    let report = (vulnerabilityJson 8 false).Replace("https://api.nuget.org", "http://api.nuget.org")
    let exitCode, _, error = validateVulnerability report
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=vulnerability-report-source", error)

[<Fact>]
let ``unexpected HTTPS vulnerability source is rejected`` () =
    let report = (vulnerabilityJson 8 false).Replace("https://api.nuget.org/v3/index.json", "https://packages.example.invalid/v3/index.json")
    let exitCode, _, error = validateVulnerability report
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=vulnerability-report-source", error)

[<Fact>]
let ``incomplete vulnerability parameters are rejected`` () =
    let report = (vulnerabilityJson 8 false).Replace("--vulnerable --include-transitive", "--vulnerable")
    let exitCode, _, error = validateVulnerability report
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=vulnerability-report-parameters", error)

[<Fact>]
let ``same-count wrong-project vulnerability report is rejected`` () =
    let report = (vulnerabilityJson 8 false).Replace("src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj", "src/Wrong/Wrong.fsproj")
    let exitCode, _, error = validateVulnerability report
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=vulnerability-report-completeness", error)

[<Fact>]
let ``package smoke rejects an absent staged package`` () =
    withScratch "fsgg-bootstrap-package-absent-" (fun root ->
        let missing = Path.Combine(root, "absent.nupkg")
        let run = Path.Combine(root, "run")
        let exitCode, _, _ = runPackageSmoke run (Some missing)
        Assert.NotEqual(0, exitCode))

[<Fact>]
let ``package smoke rejects tampered staged bytes`` () =
    withScratch "fsgg-bootstrap-package-tampered-" (fun root ->
        let tampered = Path.Combine(root, "FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg")
        File.WriteAllText(tampered, "not a NuGet package")
        let run = Path.Combine(root, "run")
        let exitCode, _, _ = runPackageSmoke run (Some tampered)
        Assert.NotEqual(0, exitCode))

[<Fact>]
let ``package override is unavailable inside GitHub Actions`` () =
    withScratch "fsgg-bootstrap-package-actions-" (fun root ->
        let package = Path.Combine(root, "candidate.nupkg")
        File.WriteAllText(package, "irrelevant")
        let startInfo = ProcessStartInfo("bash")
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "eng/package-install-smoke.sh"))
        startInfo.ArgumentList.Add(Path.Combine(root, "run"))
        startInfo.Environment["GITHUB_ACTIONS"] <- "true"
        startInfo.Environment["FSGG_BOOTSTRAP_PACKAGE_OVERRIDE"] <- package
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        use childProcess = Process.Start startInfo
        let error = childProcess.StandardError.ReadToEnd()
        childProcess.WaitForExit()
        Assert.NotEqual(0, childProcess.ExitCode)
        Assert.Contains("package override is forbidden", error))

[<Fact>]
let ``exact-head evidence and artifact digests are accepted`` () =
    withEvidence (fun root artifacts manifest ->
        let exitCode, output, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.Equal(0, exitCode)
        Assert.Equal("BOOTSTRAP_CI_OK mode=evidence", output)
        Assert.Equal("", error))

let private mutateRecoveryReceipt mutate =
    withEvidence (fun root artifacts manifest ->
        let path = Path.Combine(artifacts, "bootstrap-recovery/result.json")
        mutate path
        runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ])

[<Fact>]
let ``recovery evidence rejects malformed JSON`` () =
    let exitCode, _, error = mutateRecoveryReceipt (fun path -> File.WriteAllText(path, "not-json"))
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=recovery-receipt-unreadable", error)

[<Fact>]
let ``recovery evidence rejects a stale candidate`` () =
    let exitCode, _, error =
        mutateRecoveryReceipt (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace(exactHead, String.replicate 40 "c")))
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=recovery-receipt-candidate", error)

[<Fact>]
let ``recovery evidence rejects feed substitution`` () =
    let exitCode, _, error =
        mutateRecoveryReceipt (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("https://api.nuget.org/v3/index.json", "https://packages.example.invalid/v3/index.json")))
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=recovery-receipt-source", error)

[<Theory>]
[<InlineData("\"clone\",", "")>]
[<InlineData("\"clone\",\"restore\"", "\"restore\",\"clone\"")>]
[<InlineData("\"execute\"", "\"execute\",\"publish\"")>]
let ``recovery evidence rejects missing reordered and extra stages`` (original: string) (replacement: string) =
    let exitCode, _, error =
        mutateRecoveryReceipt (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace(original, replacement)))
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=recovery-receipt-stages", error)

[<Fact>]
let ``recovery evidence rejects a malformed package digest`` () =
    let exitCode, _, error =
        mutateRecoveryReceipt (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace(String.replicate 64 "b", "ABC")))
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=recovery-receipt-package-digest", error)

[<Fact>]
let ``recovery evidence rejects unexpected fields and noncanonical bytes`` () =
    let unexpectedCode, _, unexpectedError =
        mutateRecoveryReceipt (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("{\"schema\"", "{\"extra\":true,\"schema\"")))
    Assert.NotEqual(0, unexpectedCode)
    Assert.Contains("rule=recovery-receipt-properties", unexpectedError)
    let shapeCode, _, shapeError =
        mutateRecoveryReceipt (fun path -> File.AppendAllText(path, "\n"))
    Assert.NotEqual(0, shapeCode)
    Assert.Contains("rule=recovery-receipt-canonical", shapeError)

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

[<Fact>]
let ``evidence rejects duplicate and unknown gates`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        let gates = document["gates"].AsArray()
        gates.Add(gates[0].DeepClone())
        gates[1]["id"] <- JsonValue.Create("unknown-gate")
        File.WriteAllText(manifest, document.ToJsonString())
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-gate-set", error))

[<Fact>]
let ``evidence rejects malformed declared digests`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        let first = document["gates"].AsArray()[0]
        first["sha256"] <- JsonValue.Create("not-a-sha256")
        File.WriteAllText(manifest, document.ToJsonString())
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-artifact-digest", error))

[<Fact>]
let ``evidence rejects altered command contracts and artifact paths`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        let first = document["gates"].AsArray()[0]
        first["commands"].AsArray().Clear()
        first["artifact"] <- JsonValue.Create("../outside")
        File.WriteAllText(manifest, document.ToJsonString())
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-command-contract", error)
        Assert.Contains("rule=evidence-artifact-path", error))

[<Fact>]
let ``evidence rejects missing artifact files`` () =
    withEvidence (fun root artifacts manifest ->
        File.Delete(Path.Combine(artifacts, "compiler-and-tests/architecture.trx"))
        let exitCode, _, error =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-artifact-missing", error))

[<Fact>]
let ``evidence rejects malformed manifests and contract digests`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        document["contractSha256"] <- JsonValue.Create("wrong")
        File.WriteAllText(manifest, document.ToJsonString())
        let digestExit, _, digestError =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, digestExit)
        Assert.Contains("rule=evidence-contract-digest", digestError)
        File.WriteAllText(manifest, "not-json")
        let malformedExit, _, malformedError =
            runBootstrap root [ "evidence"; "--head"; exactHead; "--artifacts"; artifacts; "--file"; manifest ]
        Assert.NotEqual(0, malformedExit)
        Assert.Contains("rule=evidence-unreadable", malformedError))
