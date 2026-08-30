module FS.GG.Coordination.SupplyChainTests

open System
open System.Diagnostics
open System.IO
open Xunit

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private runSelfTest () =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/supply-chain-candidate.fsx"; "--"; "selftest"; "--repo"; "." ] do
        info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output.Trim(), error.Trim()

let private runReproducibilityTest () =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/supply-chain-candidate.fsx"; "--"; "reprotest"; "--repo"; "." ] do
        info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output.Trim(), error.Trim()

[<Fact>]
let ``candidate supply chain proves positive and independent negative controls`` () =
    let exitCode, output, error = runSelfTest ()
    Assert.Equal(0, exitCode)
    Assert.Equal("", error)
    Assert.StartsWith("SUPPLY_CHAIN_SELFTEST_OK positive=3 negative=26", output)
    for caseName in [ "package-tamper"; "symbol-tamper"; "assembly-digest-tamper"; "sbom-tamper"; "source-projection-tamper"; "channel-substitution"; "stable-version"; "repack-count"; "workflow-channel-substitution"; "workflow-bypass"; "workflow-unreadable"; "workflow-unprotected"; "workflow-dynamic-source"; "workflow-detached-source"; "served-route-owner"; "served-route-package"; "served-route-version"; "served-route-file"; "served-route-query"; "served-route-fragment"; "served-route-extra-segment"; "served-route-trailing-slash"; "served-route-double-slash"; "served-route-percent-encoding"; "served-route-channel-binding"; "served-route-source-binding" ] do
        Assert.Contains(caseName, output)

[<Fact>]
let ``candidate package portable symbols assembly and pdb are reproducible across unequal roots`` () =
    let exitCode, output, error = runReproducibilityTest ()
    Assert.Equal(0, exitCode)
    Assert.Equal("", error)
    Assert.Contains("SUPPLY_CHAIN_REPRODUCIBLE package=", output)
    for field in [ "symbols="; "assembly="; "pdb=" ] do Assert.Contains(field, output)

[<Fact>]
let ``candidate workflow is manual exact-sha and pre-production only`` () =
    let workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/candidate-supply-chain.yml"))
    Assert.Contains("workflow_dispatch:", workflow)
    Assert.Contains("expected_sha:", workflow)
    Assert.Contains("permissions:\n  contents: read\n  packages: write", workflow)
    Assert.Contains("dotnet fsi eng/supply-chain-candidate.fsx -- prepare", workflow)
    Assert.Contains("git merge-base --is-ancestor", workflow)
    Assert.Contains("--protected-ref refs/remotes/origin/main", workflow)
    Assert.Contains("https://nuget.pkg.github.com/FS-GG/index.json", workflow)
    Assert.Contains("https://nuget.pkg.github.com/fs-gg/download/", workflow)
    Assert.Contains("dotnet fsi eng/supply-chain-candidate.fsx -- verify-served", workflow)
    Assert.Contains("--no-symbols", workflow)
    Assert.Contains("FS.GG.Coordination.Protocol.*.snupkg", workflow)
    Assert.Contains("retention-days: 90", workflow)
    Assert.DoesNotContain("nuget.org", workflow.ToLowerInvariant())
    Assert.DoesNotContain("gh release", workflow)
    Assert.DoesNotContain("git tag", workflow)
    Assert.DoesNotContain("environment:", workflow)

[<Fact>]
let ``candidate implementation has one pack call and two clean consumers`` () =
    let implementation = File.ReadAllText(Path.Combine(root, "eng/supply-chain-candidate.fsx"))
    let qualification = File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/compiler-and-tests.sh"))
    let runnerTemp = File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/runner-temp.sh"))
    let packToken = "\"pack\"; packageProject"
    Assert.Equal(1, implementation.Split(packToken, StringSplitOptions.None).Length - 1)
    Assert.Contains("SPDX-2.3", implementation)
    Assert.Contains("https://in-toto.io/Statement/v1", implementation)
    Assert.Contains("packInvocations", implementation)
    Assert.Contains("canonicalizePackage", implementation)
    Assert.Contains("-p:PathMap=", implementation)
    Assert.Contains("BaseIntermediateOutputPath", implementation)
    Assert.Contains("UseSharedCompilation=false", implementation)
    Assert.Contains("-p:DebugType=portable", implementation)
    Assert.Contains("-p:DebugSymbols=true", implementation)
    Assert.Contains("-p:IncludeSymbols=true", implementation)
    Assert.Contains("-p:SymbolPackageFormat=snupkg", implementation)
    Assert.Contains("portablePdbSha256", implementation)
    Assert.Contains("installedAssemblySha256", implementation)
    Assert.Contains("fsgg-gs2-03-7-", implementation)
    Assert.Contains("pinnedDotnetSdkVersion", implementation)
    Assert.Contains("pinnedDotnetRuntimeVersion", implementation)
    Assert.Contains("pinnedFSharpCompilerSha256", implementation)
    Assert.Contains("requireServedRoute", implementation)
    Assert.Equal(3, implementation.Split("--disable-build-servers", StringSplitOptions.None).Length - 1)
    Assert.Contains("projectTrackedSource", implementation)
    Assert.Contains("git status --porcelain --untracked-files=all", qualification)
    Assert.Contains("identity-bound qualification requires a clean committed candidate", qualification)
    Assert.Contains("fsgg_resolve_runner_temp", qualification)
    Assert.Contains("${RUNNER_TEMP:-}", runnerTemp)
    Assert.Contains("mktemp -d", runnerTemp)
    Assert.Contains("trap 'rm -rf", runnerTemp)
    for gate in [ "bootstrap-recovery"; "compiler-and-tests"; "dependency-and-security"; "deterministic-build"; "evidence-manifest"; "package-install-smoke"; "workflow-static" ] do
        let source = File.ReadAllText(Path.Combine(root, $"eng/bootstrap-gates/{gate}.sh"))
        Assert.Contains("${BASH_SOURCE[0]}", source)
        Assert.Contains("runner-temp.sh", source)
        Assert.Contains("fsgg_resolve_runner_temp", source)
    Assert.Contains("\"archive\"; \"--format=zip\"", implementation)
    Assert.Contains("tracked-source", implementation)
    Assert.Contains("SequenceEqual", implementation)
    Assert.Contains("supply-chain-consumer-a", implementation)
    Assert.Contains("supply-chain-consumer-b", implementation)
    for fixture in [ "supply-chain-consumer-a"; "supply-chain-consumer-b" ] do
        let directory = Path.Combine(root, "tests/fixtures", fixture)
        Assert.True(Directory.Exists directory)
        Assert.Single(Directory.GetFiles(directory, "*.fsproj")) |> ignore
        Assert.True(File.Exists(Path.Combine(directory, "Program.fs")))
