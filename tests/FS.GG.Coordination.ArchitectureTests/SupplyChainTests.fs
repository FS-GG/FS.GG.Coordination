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

[<Fact>]
let ``candidate supply chain proves positive and independent negative controls`` () =
    let exitCode, output, error = runSelfTest ()
    Assert.Equal(0, exitCode)
    Assert.Equal("", error)
    Assert.StartsWith("SUPPLY_CHAIN_SELFTEST_OK positive=1 negative=10", output)
    for caseName in [ "package-tamper"; "sbom-tamper"; "channel-substitution"; "stable-version"; "repack-count"; "workflow-channel-substitution"; "workflow-bypass"; "workflow-unreadable"; "workflow-unprotected"; "workflow-dynamic-source" ] do
        Assert.Contains(caseName, output)

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
    Assert.Contains("retention-days: 90", workflow)
    Assert.DoesNotContain("nuget.org", workflow.ToLowerInvariant())
    Assert.DoesNotContain("gh release", workflow)
    Assert.DoesNotContain("git tag", workflow)
    Assert.DoesNotContain("environment:", workflow)

[<Fact>]
let ``candidate implementation has one pack call and two clean consumers`` () =
    let implementation = File.ReadAllText(Path.Combine(root, "eng/supply-chain-candidate.fsx"))
    let packToken = "\"pack\"; packageProject"
    Assert.Equal(1, implementation.Split(packToken, StringSplitOptions.None).Length - 1)
    Assert.Contains("SPDX-2.3", implementation)
    Assert.Contains("https://in-toto.io/Statement/v1", implementation)
    Assert.Contains("packInvocations", implementation)
    Assert.Contains("canonicalizePackage", implementation)
    Assert.Contains("-p:PathMap=", implementation)
    Assert.Contains("SequenceEqual", implementation)
    Assert.Contains("supply-chain-consumer-a", implementation)
    Assert.Contains("supply-chain-consumer-b", implementation)
    for fixture in [ "supply-chain-consumer-a"; "supply-chain-consumer-b" ] do
        let directory = Path.Combine(root, "tests/fixtures", fixture)
        Assert.True(Directory.Exists directory)
        Assert.Single(Directory.GetFiles(directory, "*.fsproj")) |> ignore
        Assert.True(File.Exists(Path.Combine(directory, "Program.fs")))
