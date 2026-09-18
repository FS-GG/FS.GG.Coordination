module FS.GG.Coordination.CallableCliArchitectureTests

open System
open System.IO
open Xunit

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))

let private read relative = File.ReadAllText(Path.Combine(root, relative))

[<Fact>]
let ``callable CLI is the only explicitly packable stable tool boundary`` () =
    let project = read "src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj"
    for expected in
        [
            "<IsPackable>true</IsPackable>"
            "<PackAsTool>true</PackAsTool>"
            "<ToolCommandName>fsgg-coordination</ToolCommandName>"
            "<PackageId>FS.GG.Coordination.Cli</PackageId>"
            "<Version>0.1.0</Version>"
        ] do Assert.Contains(expected, project, StringComparison.Ordinal)

    let otherProjects =
        Directory.GetFiles(Path.Combine(root, "src"), "*.fsproj", SearchOption.AllDirectories)
        |> Array.filter (fun path -> not (path.EndsWith("FS.GG.Coordination.Cli.fsproj", StringComparison.Ordinal)))
    Assert.All(otherProjects, fun path -> Assert.DoesNotContain("<IsPackable>true</IsPackable>", File.ReadAllText path, StringComparison.Ordinal))

[<Fact>]
let ``release preparation route has no publication tag or credential authority`` () =
    let workflow = read ".github/workflows/callable-cli-release-prepare.yml"
    Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal)
    Assert.Contains("contents: read", workflow, StringComparison.Ordinal)
    for forbidden in [ "packages: write"; "contents: write"; "nuget push"; "gh release"; "git tag"; "secrets." ] do
        Assert.DoesNotContain(forbidden, workflow, StringComparison.OrdinalIgnoreCase)

    let contract = read "eng/callable-cli-release-preparation.json"
    Assert.Contains("\"authorized\":false", contract, StringComparison.Ordinal)
    Assert.Contains("github-packages-then-byte-identical-nuget-org", contract.Replace("[\"github-packages\",\"nuget.org-byte-identical\"]", "github-packages-then-byte-identical-nuget-org"), StringComparison.Ordinal)
    Assert.Contains("separate-protected-.3b-operation", contract, StringComparison.Ordinal)

[<Fact>]
let ``native ordinary provider uses typed REST transport and protected journal adapter`` () =
    let source = read "src/FS.GG.Coordination.GitHub/OrdinaryGitHubRuntime.fs"
    Assert.Contains("IOrdinaryGitHubTransport", source, StringComparison.Ordinal)
    Assert.Contains("ShardedJournalAdapter.address Operation", source, StringComparison.Ordinal)
    Assert.Contains("NeverReplay", source, StringComparison.Ordinal)
    Assert.Contains("ordinary-delivery-journal/1", source, StringComparison.Ordinal)
    for forbidden in [ "Process.Start"; "gh "; "git merge"; "GitHubRouteClient" ] do
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)

