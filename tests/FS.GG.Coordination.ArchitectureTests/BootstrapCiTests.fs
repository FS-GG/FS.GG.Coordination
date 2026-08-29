module FS.GG.Coordination.BootstrapCiTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private verifier = Path.Combine(repositoryRoot, "eng/bootstrap-ci.fsx")
let private exactHead = String.replicate 40 "a"

let private runBootstrap root arguments =
    BootstrapCi.execute (arguments @ [ "--root"; root ])

let private runBootstrapAdapter root arguments =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add("fsi")
    startInfo.ArgumentList.Add(verifier)
    startInfo.ArgumentList.Add("--")
    for argument in arguments @ [ "--root"; root ] do startInfo.ArgumentList.Add(argument)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use childProcess = Process.Start startInfo
    let output = childProcess.StandardOutput.ReadToEnd().Trim()
    let error = childProcess.StandardError.ReadToEnd().Trim()
    childProcess.WaitForExit()
    childProcess.ExitCode, output, error

let private runGateWithoutRepositorySubject gateId root =
    let startInfo = ProcessStartInfo("bash")
    startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, $"eng/bootstrap-gates/%s{gateId}.sh"))
    startInfo.WorkingDirectory <- root
    startInfo.Environment["RUNNER_TEMP"] <- Path.Combine(root, "runner-temp")
    startInfo.Environment["FSGG_CANDIDATE_SHA"] <- exactHead
    startInfo.Environment["FSGG_QUINT_RECEIPT"] <- Path.Combine(root, "runner-temp/canonical-quint/qualification.json")
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use childProcess = Process.Start startInfo
    let output = childProcess.StandardOutput.ReadToEnd()
    let error = childProcess.StandardError.ReadToEnd()
    childProcess.WaitForExit()
    childProcess.ExitCode, output, error

let private withScratch prefix action =
    let scratch = Directory.CreateTempSubdirectory(prefix)
    try action scratch.FullName
    finally scratch.Delete(true)

let private copyContract root =
    let eng = Directory.CreateDirectory(Path.Combine(root, "eng"))
    File.Copy(Path.Combine(repositoryRoot, "eng/bootstrap-qualification-plan.json"), Path.Combine(eng.FullName, "bootstrap-qualification-plan.json"))

let private withWorkflowMutation mutate verify =
    withScratch "fsgg-bootstrap-workflow-" (fun root ->
        copyContract root
        let workflows = Directory.CreateDirectory(Path.Combine(root, ".github/workflows"))
        let target = Path.Combine(workflows.FullName, "bootstrap-qualification.yml")
        File.Copy(Path.Combine(repositoryRoot, ".github/workflows/bootstrap-qualification.yml"), target)
        mutate target
        verify root)

let private withPlanMutation mutate verify =
    withScratch "fsgg-bootstrap-plan-" (fun root ->
        copyContract root
        let workflows = Directory.CreateDirectory(Path.Combine(root, ".github/workflows"))
        File.Copy(Path.Combine(repositoryRoot, ".github/workflows/bootstrap-qualification.yml"), Path.Combine(workflows.FullName, "bootstrap-qualification.yml"))
        let target = Path.Combine(root, "eng/bootstrap-qualification-plan.json")
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
          "canonical-quint/qualification.json"
          "dependency-and-security/vulnerability-report.json"
          "package-install-smoke/FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg"
          "bootstrap-recovery/result.json"
          "evidence-manifest/plan.json" ]
    for relative in paths do
        let target = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        if relative = "evidence-manifest/plan.json" then
            File.Copy(Path.Combine(repositoryRoot, "eng/bootstrap-qualification-plan.json"), target)
        elif relative = "bootstrap-recovery/result.json" then
            let packageDigest = String.replicate 64 "b"
            File.WriteAllText(
                target,
                $"{{\"schema\":\"fsgg.coordination.bootstrap-recovery/1\",\"candidate\":\"%s{exactHead}\",\"packageSha256\":\"%s{packageDigest}\",\"publishedSources\":[\"https://api.nuget.org/v3/index.json\"],\"stages\":[\"clone\",\"restore\",\"build\",\"unit-tests\",\"architecture-tests\",\"pack\",\"install\",\"execute\"]}}\n")
        elif relative = "canonical-quint/qualification.json" then
            let preparationDigest = String.replicate 64 "c"
            let sourceDigest = "b82983e10324c241cef1187cf58ce2ec5222ab4d7e253d53179d5343927c518a"
            let contractDigest = "60bf639dc6c6e4a31ac284c57d85cb10a5cd7c0cce5532552884b5a3ea1b8c76"
            let toolchainDigest = "79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486"
            let quintDigest = "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
            let apalacheDigest = "4753c0ebb2cbb266e2c6ac19ab5ca3827d726cc80fd1fc5d7c1eeb64736cd60b"
            let resultDigest =
                SHA256.HashData(Encoding.UTF8.GetBytes($"passed|passed|8|56|85|61|14|%s{preparationDigest}|none|none"))
                |> Convert.ToHexString
                |> _.ToLowerInvariant()
            File.WriteAllText(
                target,
                $"{{\"schema\":\"fsgg.coordination.canonical-quint-qualification/1\",\"q1Outcome\":\"passed\",\"q2Outcome\":\"passed\",\"positiveInvariantCount\":8,\"negativeControlCount\":56,\"preparationDurationMs\":100,\"q2DurationMs\":200,\"totalDurationMs\":300,\"processCounts\":{{\"external\":85,\"quintCli\":61,\"apalacheVerify\":14}},\"tools\":{{\"toolchainSha256\":\"%s{toolchainDigest}\",\"quintSha256\":\"%s{quintDigest}\",\"apalacheJarSha256\":\"%s{apalacheDigest}\"}},\"inputs\":{{\"sourceSha256\":\"%s{sourceDigest}\",\"contractSha256\":\"%s{contractDigest}\"}},\"preparationSha256\":\"%s{preparationDigest}\",\"failure\":null,\"resultSha256\":\"%s{resultDigest}\"}}")
        else
            File.WriteAllText(target, $"artifact:%s{relative}")

let private withEvidence action =
    withScratch "fsgg-bootstrap-evidence-" (fun root ->
        copyContract root
        let artifacts = Path.Combine(root, "artifacts")
        Directory.CreateDirectory artifacts |> ignore
        createArtifacts artifacts
        let manifest = Path.Combine(root, "evidence.json")
        let decision = Path.Combine(root, "decision.json")
        QualificationReuse.decide exactHead (String.replicate 64 "d") None None
        |> QualificationReuse.decisionBytes
        |> fun bytes -> File.WriteAllBytes(decision, bytes)
        let collectCode, _, collectError =
            runBootstrap root [ "collect"; "--head"; exactHead; "--artifacts"; artifacts; "--output"; manifest; "--decision"; decision ]
        Assert.Equal(0, collectCode)
        Assert.Equal("", collectError)
        action root artifacts manifest)

let private runEvidence root head artifacts manifest =
    runBootstrap root
        [ "evidence"; "--head"; head; "--artifacts"; artifacts; "--file"; manifest
          "--decision"; Path.Combine(root, "decision.json") ]

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

let private tracked (mode: string) (path: string) (contents: string) : QualificationReuse.TrackedFile =
    { Mode = mode; Path = path; Bytes = Encoding.UTF8.GetBytes contents }

[<Fact>]
let ``qualification subject is canonical across tracked-file enumeration order`` () =
    let files = [ tracked "100644" "b.txt" "b"; tracked "100755" "a.sh" "a" ]
    let create values =
        QualificationReuse.createSubject values (Encoding.UTF8.GetBytes "plan") (Encoding.UTF8.GetBytes "workflow") (Encoding.UTF8.GetBytes "environment") (Encoding.UTF8.GetBytes "review")
    let first = create files
    let second = create (List.rev files)
    Assert.Equal(first, second)
    Assert.True((QualificationReuse.subjectBytes first).AsSpan().SequenceEqual((QualificationReuse.subjectBytes second).AsSpan()))

[<Theory>]
[<InlineData("100644", "a.txt", "value-2")>]
[<InlineData("100755", "a.txt", "value")>]
[<InlineData("100644", "renamed.txt", "value")>]
let ``qualification subject changes for independently mutated tree bytes mode or path`` mode path contents =
    let create file =
        QualificationReuse.createSubject [ file ] (Encoding.UTF8.GetBytes "plan") (Encoding.UTF8.GetBytes "workflow") (Encoding.UTF8.GetBytes "environment") (Encoding.UTF8.GetBytes "review")
    let baseline = create (tracked "100644" "a.txt" "value")
    let mutated = create (tracked mode path contents)
    Assert.NotEqual<string>(baseline.TreeSha256, mutated.TreeSha256)
    Assert.NotEqual<string>(baseline.SubjectSha256, mutated.SubjectSha256)

[<Fact>]
let ``qualification subject rejects duplicate unsafe and unsupported tracked identities`` () =
    let create files =
        QualificationReuse.createSubject files [| 1uy |] [| 2uy |] [| 3uy |] [| 4uy |] |> ignore
    Assert.Throws<ArgumentException>(fun () -> create [ tracked "100644" "a" "1"; tracked "100644" "a" "2" ]) |> ignore
    Assert.Throws<ArgumentException>(fun () -> create [ tracked "100644" "../a" "1" ]) |> ignore
    Assert.Throws<ArgumentException>(fun () -> create [ tracked "160000" "submodule" "1" ]) |> ignore

[<Fact>]
let ``reuse decision distinguishes hit miss and incomplete authority`` () =
    let subject = String.replicate 64 "a"
    let prior: QualificationReuse.PriorRun =
        { Head = String.replicate 40 "b"; RunId = 42L; Attempt = 1
          EvidenceSha256 = String.replicate 64 "c"; ArtifactExpiresAt = "2026-09-01T00:00:00Z"; RunnerMinutes = Some 14M }
    Assert.Equal(QualificationReuse.Reuse, (QualificationReuse.decide exactHead subject (Some prior) (Some subject)).Kind)
    Assert.Equal(QualificationReuse.Execute, (QualificationReuse.decide exactHead subject None None).Kind)
    Assert.Equal(QualificationReuse.Execute, (QualificationReuse.decide exactHead subject (Some prior) (Some(String.replicate 64 "d"))).Kind)
    Assert.Equal(QualificationReuse.Refuse, (QualificationReuse.decide exactHead subject (Some prior) None).Kind)
    let unmeasured = { prior with RunnerMinutes = None }
    let measuredDecision = QualificationReuse.decide exactHead subject (Some prior) (Some subject)
    let unmeasuredDecision = QualificationReuse.decide exactHead subject (Some unmeasured) (Some subject)
    Assert.Equal(QualificationReuse.Reuse, unmeasuredDecision.Kind)
    Assert.NotEqual<string>(measuredDecision.SelfSha256, unmeasuredDecision.SelfSha256)
    Assert.Equal(Ok unmeasuredDecision, QualificationReuse.decisionBytes unmeasuredDecision |> QualificationReuse.parseDecision)
    let invalid = { prior with RunnerMinutes = Some -1M }
    Assert.Throws<ArgumentException>(fun () -> QualificationReuse.decide exactHead subject (Some invalid) (Some subject) |> ignore) |> ignore

[<Fact>]
let ``reuse receipt round trips canonical bytes and rejects tampering`` () =
    let decision = QualificationReuse.decide exactHead (String.replicate 64 "a") None None
    let bytes = QualificationReuse.decisionBytes decision
    Assert.Equal(Ok decision, QualificationReuse.parseDecision bytes)
    let tampered = Encoding.UTF8.GetString(bytes).Replace("no-compatible-prior", "different-reason") |> Encoding.UTF8.GetBytes
    Assert.True(QualificationReuse.parseDecision tampered |> Result.isError)
    let unknown = Encoding.UTF8.GetString(bytes).Replace("{\"schema\"", "{\"unknown\":true,\"schema\"") |> Encoding.UTF8.GetBytes
    Assert.True(QualificationReuse.parseDecision unknown |> Result.isError)

[<Fact>]
let ``bootstrap workflow satisfies the reuse decision plus exact seven-gate contract`` () =
    let exitCode, output, error = runBootstrap repositoryRoot [ "workflow" ]
    Assert.Equal(0, exitCode)
    Assert.Equal("BOOTSTRAP_CI_OK mode=workflow", output)
    Assert.Equal("", error)

[<Fact>]
let ``reuse telemetry measures completed runner jobs without becoming route authority`` () =
    let entryPoint = File.ReadAllText(Path.Combine(repositoryRoot, "eng/bootstrap-gates/reuse-decision.sh"))
    Assert.Contains("actions/runs/$run_id/jobs?filter=latest&per_page=100", entryPoint)
    Assert.Contains("runner_minutes=\"\"", entryPoint)
    Assert.Contains("select_args+=(--runner-minutes \"$runner_minutes\")", entryPoint)

[<Fact>]
let ``production FSI adapter matches the compiled green outcome`` () =
    Assert.Equal(runBootstrap repositoryRoot [ "workflow" ], runBootstrapAdapter repositoryRoot [ "workflow" ])

[<Fact>]
let ``production FSI adapter matches the compiled red diagnostic`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  deterministic-build:", "  deterministic-build-removed:")))
        (fun root -> Assert.Equal(runBootstrap root [ "workflow" ], runBootstrapAdapter root [ "workflow" ]))

[<Fact>]
let ``workflow generator reproduces the committed projection`` () =
    withScratch "fsgg-bootstrap-generator-" (fun root ->
        copyContract root
        let output = Path.Combine(root, "generated.yml")
        let exitCode, _, error = runBootstrap root [ "generate"; "--output"; output ]
        Assert.Equal(0, exitCode)
        Assert.Equal("", error)
        let expected = File.ReadAllBytes(Path.Combine(repositoryRoot, ".github/workflows/bootstrap-qualification.yml"))
        let actual = File.ReadAllBytes output
        Assert.True(expected.AsSpan().SequenceEqual(actual.AsSpan())))

[<Fact>]
let ``qualification plan rejects an unreviewed action revision`` () =
    withPlanMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("3d3c42e5aac5ba805825da76410c181273ba90b1", String.replicate 40 "a")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=qualification-plan-invalid", error))

[<Fact>]
let ``qualification plan rejects a legacy action runtime`` () =
    withPlanMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("\"checkout\": \"node24\"", "\"checkout\": \"node20\"")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=qualification-plan-invalid", error))

[<Fact>]
let ``qualification plan rejects reuse before the reviewed evidence epoch`` () =
    withPlanMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("2026-08-29T13:32:00Z", "2026-01-01T00:00:00Z")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=qualification-plan-invalid", error))

[<Fact>]
let ``qualification plan rejects an incomplete terminal dependency edge`` () =
    withPlanMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("[\"deterministic-build\", \"compiler-and-tests\", \"canonical-quint\", \"dependency-and-security\", \"package-install-smoke\", \"bootstrap-recovery\"]", "[\"compiler-and-tests\", \"canonical-quint\", \"dependency-and-security\", \"package-install-smoke\", \"bootstrap-recovery\"]")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=qualification-plan-invalid", error))

[<Fact>]
let ``representative gate addition changes only the plan and its stable entry point`` () =
    withPlanMutation
        (fun path ->
            let plan = JsonNode.Parse(File.ReadAllText(path)).AsObject()
            plan["requiredGateCount"] <- JsonValue.Create(8)
            let jobs = plan["jobs"].AsArray()
            let gate = JsonObject()
            gate["id"] <- JsonValue.Create("representative-gate")
            gate["artifact"] <- JsonValue.Create("representative-gate/result.json")
            gate["timeoutMinutes"] <- JsonValue.Create(10)
            gate["entryPoint"] <- JsonValue.Create("bash eng/bootstrap-gates/representative-gate.sh")
            gate["fetchDepth"] <- JsonValue.Create(1)
            gate["alwaysUpload"] <- JsonValue.Create(false)
            gate["downloadArtifacts"] <- JsonValue.Create(false)
            gate["environment"] <- JsonObject()
            gate["uploadName"] <- JsonValue.Create("representative-gate")
            gate["uploadPath"] <- JsonValue.Create("${{ runner.temp }}/representative-gate/result.json")
            gate["needs"] <- JsonArray()
            jobs.Insert(jobs.Count - 1, gate)
            let terminalJob = jobs[jobs.Count - 1]
            let terminalNeeds = terminalJob["needs"].AsArray()
            terminalNeeds.Add(JsonValue.Create("representative-gate"))
            File.WriteAllText(path, plan.ToJsonString()))
        (fun root ->
            let output = Path.Combine(root, "representative.yml")
            let exitCode, _, error = runBootstrap root [ "generate"; "--output"; output ]
            Assert.Equal(0, exitCode)
            Assert.Equal("", error)
            let workflow = File.ReadAllText(output)
            Assert.Contains("  representative-gate:", workflow)
            Assert.Contains("run: bash eng/bootstrap-gates/representative-gate.sh", workflow))

[<Theory>]
[<InlineData("deterministic-build", "FS.GG.Coordination.sln")>]
[<InlineData("compiler-and-tests", "FS.GG.Coordination.sln")>]
[<InlineData("canonical-quint", "FS.GG.Coordination.Qualification.Contracts.fsproj")>]
[<InlineData("dependency-and-security", "FS.GG.Coordination.sln")>]
[<InlineData("package-install-smoke", "FS.GG.Coordination.Protocol.fsproj")>]
[<InlineData("bootstrap-recovery", "eng/bootstrap-recovery.fsx")>]
[<InlineData("reuse-decision", "eng/bootstrap-qualification-plan.json")>]
[<InlineData("evidence-manifest", "bootstrap-decision/decision.json")>]
let ``stable gate entry point refuses a removed repository subject`` gateId expectedSubject =
    withScratch $"fsgg-bootstrap-gate-inversion-%s{gateId}-" (fun root ->
        let exitCode, output, error = runGateWithoutRepositorySubject gateId root
        Assert.NotEqual(0, exitCode)
        Assert.Contains(expectedSubject, output + error))

[<Fact>]
let ``performance evidence retains five source-linked timing samples and every acceptance threshold`` () =
    let evaluation =
        File.ReadAllText(Path.Combine(repositoryRoot, "work/78-shorten-qualification-critical-path/performance-evaluation.md"))
    let requiredEvidence =
        [ "actions/runs/33248808361"
          "actions/runs/33250382392/attempts/1"
          "actions/runs/33250382392/attempts/2"
          "actions/runs/33251281115"
          "actions/runs/33251621507"
          "| Baseline | 2s | 31s | 1033s | 22s | 1103s | 366s |"
          "| Receipt-bound cache-free | 49s | 24s | 775s | 24s | 840s | 478s |"
          "Compiler/tests improvement exceeds 30% in all four candidate attempts."
          "Aggregate runner-time improvement exceeds 10% in all four candidate attempts."
          "Cache miss/hit semantics are equal" ]
    for evidence in requiredEvidence do Assert.Contains(evidence, evaluation)
    let removedSource = evaluation.Replace("actions/runs/33251281115", "missing-cache-free-run")
    Assert.DoesNotContain("actions/runs/33251281115", removedSource)

[<Fact>]
let ``reuse performance evidence retains the exact execute hit pair and thresholds`` () =
    let evaluation =
        File.ReadAllText(Path.Combine(repositoryRoot, "work/80-digest-bound-exact-head-qualification-reuse/performance-evaluation.md"))
    let requiredEvidence =
        [ "actions/runs/33255549867"
          "actions/runs/33255929882"
          "30c9b48940f9a598af170183049bde9f0494693c"
          "saved 467 wall-seconds (89.5%) and 873 runner-seconds (94.7%, 14m33s)"
          "settled in 55 seconds, below the 180-second target"
          "added 78 wall-seconds over the comparable cohort median, below the 90-second ceiling"
          "represents an unavailable measurement as `null`"
          "route selection remains unchanged for measured versus unavailable telemetry" ]
    for evidence in requiredEvidence do Assert.Contains(evidence, evaluation)

[<Fact>]
let ``bootstrap control surface stays typed thin and bounded`` () =
    let lineCount relative = File.ReadAllLines(Path.Combine(repositoryRoot, relative)).Length
    let gateLines =
        Directory.GetFiles(Path.Combine(repositoryRoot, "eng/bootstrap-gates"), "*.sh")
        |> Array.sumBy (File.ReadAllLines >> Array.length)
    let core = File.ReadAllText(Path.Combine(repositoryRoot, "src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs"))
    let reuseCore = File.ReadAllText(Path.Combine(repositoryRoot, "src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs"))
    let workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github/workflows/bootstrap-qualification.yml"))
    Assert.InRange(lineCount ".github/workflows/bootstrap-qualification.yml", 1, 260)
    Assert.InRange(lineCount "eng/bootstrap-qualification-plan.json", 1, 190)
    Assert.InRange(lineCount "eng/bootstrap-ci.fsx", 1, 22)
    Assert.InRange(lineCount "src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs", 1, 900)
    Assert.InRange(lineCount "src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs", 1, 300)
    Assert.InRange(gateLines, 1, 140)
    Assert.DoesNotContain("requiredRunFragments", core)
    Assert.DoesNotContain("workflowSha256", core)
    Assert.DoesNotContain("Text.RegularExpressions", core)
    Assert.DoesNotContain("expectedIds", core)
    Assert.DoesNotContain("gate.Id =", core)
    Assert.DoesNotContain("job.Id =", core)
    Assert.DoesNotContain("GitHub", reuseCore)
    Assert.DoesNotContain("NUGET_PACKAGES: ${{ runner.", workflow)
    Assert.DoesNotContain("FSGG_QUINT_RECEIPT: ${{ runner.", workflow)
    Assert.DoesNotContain("actions/cache@", workflow)

[<Fact>]
let ``bootstrap workflow rejects a missing gate`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  deterministic-build:", "  deterministic-build-removed:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects duplicate job identities`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  compiler-and-tests:", "  deterministic-build:\n  compiler-and-tests:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects mutable action references`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v7")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects pinned but unapproved actions`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Upload qualification evidence", "      - name: Unapproved pinned action\n        uses: example/deploy@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n      - name: Upload qualification evidence")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects an unbound cross-run artifact download`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("          path: ${{ runner.temp }}/bootstrap-artifacts", "          path: ${{ runner.temp }}/bootstrap-artifacts\n          run-id: 1")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects checkout not bound to the evidence candidate`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("          ref: ${{ github.event.pull_request.head.sha || github.sha }}", "          # ref: ${{ github.event.pull_request.head.sha || github.sha }}")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects authority expansion`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("contents: read", "contents: write")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects unavailable runner context in job environment`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("NUGET_PACKAGES: /tmp/fsgg-${{ github.run_id }}-nuget-canonical-quint", "NUGET_PACKAGES: ${{ runner.temp }}/nuget-canonical-quint")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects incomplete triggers`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("  pull_request:", "  # pull_request:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects trigger children without top-level on`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("on:\n", "off:\n")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects a vacuous action inventory`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("uses:", "uses-disabled:")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects a missing required command`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("        run: bash eng/bootstrap-gates/compiler-and-tests.sh", "        # run: bash eng/bootstrap-gates/compiler-and-tests.sh")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects shell success suppression`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("run: bash eng/bootstrap-gates/compiler-and-tests.sh", "run: bash eng/bootstrap-gates/compiler-and-tests.sh || true")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects any unexpected executable command`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("        run: bash eng/bootstrap-gates/compiler-and-tests.sh", "        run: |\n          set +o errexit\n          bash eng/bootstrap-gates/compiler-and-tests.sh")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects checkout ref outside with`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("        with:\n          ref: ${{ github.event.pull_request.head.sha || github.sha }}", "        env:\n          ref: ${{ github.event.pull_request.head.sha || github.sha }}")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects conditional gates`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Run the stable qualification gate", "      - name: Run the stable qualification gate\n        if: false")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects package override seam`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("        run: bash eng/bootstrap-gates/package-install-smoke.sh", "        env:\n          FSGG_BOOTSTRAP_PACKAGE_OVERRIDE: fake.nupkg\n        run: bash eng/bootstrap-gates/package-install-smoke.sh")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``bootstrap workflow rejects imported v1 completion machinery`` () =
    withWorkflowMutation
        (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace("      - name: Upload qualification evidence", "      - name: Forbidden completion route\n        run: scripts/fsgg-coord delivery\n      - name: Upload qualification evidence")))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error))

[<Fact>]
let ``workflow comments cannot bypass the exact byte contract`` () =
    withWorkflowMutation
        (fun path -> File.AppendAllText(path, "\n# scripts/fsgg-coord delivery is intentionally absent\n"))
        (fun root ->
            let exitCode, _, error = runBootstrap root [ "workflow" ]
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=workflow-projection-stale", error)
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
            runEvidence root exactHead artifacts manifest
        Assert.Equal(0, exitCode)
        Assert.Equal("BOOTSTRAP_CI_OK mode=evidence", output)
        Assert.Equal("", error))

[<Fact>]
let ``reuse revalidates prior execution artifacts and emits current-head evidence`` () =
    withEvidence (fun root artifacts priorManifest ->
        let copy relative source =
            let target = Path.Combine(artifacts, relative)
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(source, target)
        copy "reuse-decision/decision.json" (Path.Combine(root, "decision.json"))
        copy "bootstrap-evidence-manifest/bootstrap-evidence.json" priorManifest
        let evidenceDigest = SHA256.HashData(File.ReadAllBytes priorManifest) |> Convert.ToHexString |> _.ToLowerInvariant()
        let prior: QualificationReuse.PriorRun =
            { Head = exactHead; RunId = 42L; Attempt = 1; EvidenceSha256 = evidenceDigest
              ArtifactExpiresAt = "2026-09-01T00:00:00Z"; RunnerMinutes = Some 14M }
        let currentHead = String.replicate 40 "e"
        let currentDecision = QualificationReuse.decide currentHead (String.replicate 64 "d") (Some prior) (Some(String.replicate 64 "d"))
        let decisionPath = Path.Combine(root, "reuse.json")
        File.WriteAllBytes(decisionPath, QualificationReuse.decisionBytes currentDecision)
        let currentManifest = Path.Combine(root, "current-evidence.json")
        let collectCode, _, collectError =
            runBootstrap root [ "collect"; "--head"; currentHead; "--artifacts"; artifacts; "--output"; currentManifest; "--decision"; decisionPath ]
        Assert.Equal(0, collectCode)
        Assert.Equal("", collectError)
        let evidenceCode, _, evidenceError =
            runBootstrap root [ "evidence"; "--head"; currentHead; "--artifacts"; artifacts; "--file"; currentManifest; "--decision"; decisionPath ]
        Assert.Equal(0, evidenceCode)
        Assert.Equal("", evidenceError)
        let current = JsonNode.Parse(File.ReadAllText currentManifest).AsObject()
        Assert.Equal("reuse", current["route"].GetValue<string>())
        Assert.Equal(currentHead, current["candidate"].GetValue<string>())
        let retainedHead = ((current["prior"])["head"]).GetValue<string>()
        Assert.Equal(exactHead, retainedHead))

[<Fact>]
let ``reuse refuses a selected prior manifest whose bytes changed`` () =
    withEvidence (fun root artifacts priorManifest ->
        let priorDecisionPath = Path.Combine(artifacts, "reuse-decision/decision.json")
        let retainedManifest = Path.Combine(artifacts, "bootstrap-evidence-manifest/bootstrap-evidence.json")
        Directory.CreateDirectory(Path.GetDirectoryName priorDecisionPath) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName retainedManifest) |> ignore
        File.Copy(Path.Combine(root, "decision.json"), priorDecisionPath)
        File.Copy(priorManifest, retainedManifest)
        let originalDigest = SHA256.HashData(File.ReadAllBytes retainedManifest) |> Convert.ToHexString |> _.ToLowerInvariant()
        let prior: QualificationReuse.PriorRun =
            { Head = exactHead; RunId = 42L; Attempt = 1; EvidenceSha256 = originalDigest
              ArtifactExpiresAt = "2026-09-01T00:00:00Z"; RunnerMinutes = Some 14M }
        let currentHead = String.replicate 40 "e"
        let decision = QualificationReuse.decide currentHead (String.replicate 64 "d") (Some prior) (Some(String.replicate 64 "d"))
        let decisionPath = Path.Combine(root, "reuse.json")
        File.WriteAllBytes(decisionPath, QualificationReuse.decisionBytes decision)
        File.AppendAllText(retainedManifest, "tampered")
        let exitCode, _, error =
            runBootstrap root [ "collect"; "--head"; currentHead; "--artifacts"; artifacts; "--output"; Path.Combine(root, "current.json"); "--decision"; decisionPath ]
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=reuse-prior-evidence-digest", error))

let private mutateRecoveryReceipt mutate =
    withEvidence (fun root artifacts manifest ->
        let path = Path.Combine(artifacts, "bootstrap-recovery/result.json")
        mutate path
        runEvidence root exactHead artifacts manifest)

let private mutateCanonicalQuintReceipt mutate =
    withEvidence (fun root artifacts manifest ->
        let path = Path.Combine(artifacts, "canonical-quint/qualification.json")
        mutate path
        runEvidence root exactHead artifacts manifest)

[<Theory>]
[<InlineData("\"q1Outcome\":\"passed\"", "\"q1Outcome\":\"failed\"", "quint-receipt-outcome")>]
[<InlineData("\"positiveInvariantCount\":8", "\"positiveInvariantCount\":7", "quint-receipt-inventory")>]
[<InlineData("\"negativeControlCount\":56", "\"negativeControlCount\":55", "quint-receipt-inventory")>]
[<InlineData("\"totalDurationMs\":300", "\"totalDurationMs\":301", "quint-receipt-timing")>]
[<InlineData("\"external\":85", "\"external\":84", "quint-receipt-process-count")>]
[<InlineData("\"quintCli\":61", "\"quintCli\":60", "quint-receipt-process-count")>]
[<InlineData("\"apalacheVerify\":14", "\"apalacheVerify\":13", "quint-receipt-process-count")>]
[<InlineData("\"quintCli\":61", "\"quintCli\":0", "quint-receipt-process-count")>]
[<InlineData("\"resultSha256\":\"", "\"resultSha256\":\"0", "quint-receipt-result-digest")>]
let ``canonical Quint receipt rejects incomplete or contradictory evidence`` (original: string) (replacement: string) (rule: string) =
    let exitCode, _, error =
        mutateCanonicalQuintReceipt (fun path -> File.WriteAllText(path, File.ReadAllText(path).Replace(original, replacement)))
    Assert.NotEqual(0, exitCode)
    Assert.Contains($"rule=%s{rule}", error)

[<Fact>]
let ``canonical Quint receipt rejects malformed JSON`` () =
    let exitCode, _, error = mutateCanonicalQuintReceipt (fun path -> File.WriteAllText(path, "not-json"))
    Assert.NotEqual(0, exitCode)
    Assert.Contains("rule=quint-receipt-unreadable", error)

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
            runEvidence root differentHead artifacts manifest
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-candidate", error))

[<Fact>]
let ``evidence rejects a missing gate`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        document["gates"].AsArray().RemoveAt(0)
        File.WriteAllText(manifest, document.ToJsonString())
        let exitCode, _, error =
            runEvidence root exactHead artifacts manifest
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-gate-set", error))

[<Fact>]
let ``evidence rejects an artifact changed after collection`` () =
    withEvidence (fun root artifacts manifest ->
        File.AppendAllText(Path.Combine(artifacts, "deterministic-build/protocol.dll"), "tampered")
        let exitCode, _, error =
            runEvidence root exactHead artifacts manifest
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
            runEvidence root exactHead artifacts manifest
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
            runEvidence root exactHead artifacts manifest
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
            runEvidence root exactHead artifacts manifest
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-command-contract", error)
        Assert.Contains("rule=evidence-artifact-path", error))

[<Fact>]
let ``evidence rejects missing artifact files`` () =
    withEvidence (fun root artifacts manifest ->
        File.Delete(Path.Combine(artifacts, "compiler-and-tests/architecture.trx"))
        let exitCode, _, error =
            runEvidence root exactHead artifacts manifest
        Assert.NotEqual(0, exitCode)
        Assert.Contains("rule=evidence-artifact-missing", error))

[<Fact>]
let ``evidence rejects malformed manifests and contract digests`` () =
    withEvidence (fun root artifacts manifest ->
        let document = JsonNode.Parse(File.ReadAllText manifest).AsObject()
        document["planSha256"] <- JsonValue.Create("wrong")
        File.WriteAllText(manifest, document.ToJsonString())
        let digestExit, _, digestError =
            runEvidence root exactHead artifacts manifest
        Assert.NotEqual(0, digestExit)
        Assert.Contains("rule=evidence-plan-digest", digestError)
        File.WriteAllText(manifest, "not-json")
        let malformedExit, _, malformedError =
            runEvidence root exactHead artifacts manifest
        Assert.NotEqual(0, malformedExit)
        Assert.Contains("rule=evidence-unreadable", malformedError))
