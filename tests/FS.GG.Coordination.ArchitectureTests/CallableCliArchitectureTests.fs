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
            "<Version>0.1.2</Version>"
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
let ``callable CLI release preparation binds reviewed version project and tag`` () =
    let script = read "eng/callable-cli-release.fsx"
    for expected in
        [
            "[ \"0.1.1\"; \"0.1.2\" ]"
            "projectPackageVersion () = version"
            "let tag = $\"v{version}\""
            "root.GetProperty(\"tag\").GetString() = tag"
        ] do Assert.Contains(expected, script, StringComparison.Ordinal)

[<Fact>]
let ``protected publication route preserves exact bytes ordering and recovery boundaries`` () =
    let workflow = read ".github/workflows/callable-cli-release-publish.yml"
    for expected in
        [
            "operation:"
            "publish-v2-call-01.3b"
            "ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf"
            "packages: write"
            "id-token: write"
            "attestations: write"
            "NuGet/login@8d196754b4036150537f80ac539e15c2f1028841"
            "eng/repository-settings/desired.json"
            "Publish to GitHub Packages first"
            "Observe nuget.org and validate recoverable ordering before either effect"
            "Create immutable tag and GitHub release only after both feeds settle"
            "if: always()"
        ] do Assert.Contains(expected, workflow, StringComparison.Ordinal)

    Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.Ordinal)
    Assert.DoesNotContain("actions/permissions/selected-actions", workflow, StringComparison.Ordinal)
    Assert.DoesNotContain("NUGET_API_KEY }}", workflow.Replace("steps.nuget-login.outputs.NUGET_API_KEY }}", ""), StringComparison.Ordinal)
    let githubPush = workflow.IndexOf("nuget.pkg.github.com/FS-GG/index.json", StringComparison.Ordinal)
    let publicPush = workflow.IndexOf("api.nuget.org/v3/index.json", StringComparison.Ordinal)
    Assert.True(githubPush >= 0 && publicPush > githubPush)

    let operation = read "eng/callable-cli-release-operation.json"
    Assert.Contains("\"operation\":\"publish-v2-call-01.3b\"", operation, StringComparison.Ordinal)
    Assert.Contains("\"receiverAdoption\":{\"authorized\":false", operation, StringComparison.Ordinal)
    Assert.Contains("\"tagAfterBothFeeds\":true", operation, StringComparison.Ordinal)

[<Fact>]
let ``native ordinary provider uses typed REST transport and protected journal adapter`` () =
    let source = read "src/FS.GG.Coordination.GitHub/OrdinaryGitHubRuntime.fs"
    Assert.Contains("IOrdinaryGitHubTransport", source, StringComparison.Ordinal)
    Assert.Contains("ShardedJournalAdapter.address Operation", source, StringComparison.Ordinal)
    Assert.Contains("NeverReplay", source, StringComparison.Ordinal)
    Assert.Contains("ordinary-delivery-journal/1", source, StringComparison.Ordinal)
    for forbidden in [ "Process.Start"; "gh "; "git merge"; "GitHubRouteClient" ] do
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)

[<Fact>]
let ``installed harness binds frozen artifact receiver and loopback-only recovery`` () =
    let contract = read "eng/callable-cli-installed-harness.json"
    let retained = read "evidence/github-substrate-v2/gs2-09-9/installed-harness.json"
    let coverage = read "evidence/github-substrate-v2/gs2-09-9/recovery-coverage.json"
    let harness = read "eng/test-callable-cli-installed-harness.py"
    let proposal = read "eng/callable-cli-isolated-operation-proposal.json"
    let validator = read "eng/validate-github-callable-ordinary-delivery.fsx"
    for expected in
        [
            "ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf"
            "e7f440a2a1f94d51dbcdd7146494c97e6386f9dcc8034a028e3e851d364390e3"
            "587f46e15e1404dbe0dc1e9e6b47cf2861d7b502"
            "separate-protected-.4b-operation"
            "observed-403-entitlement-unknown-not-absence"
        ] do Assert.Contains(expected, contract, StringComparison.Ordinal)
    for expected in [ "loopback-only"; "AdvancePending"; "providerMutations"; "127.0.0.1" ] do
        Assert.Contains(expected, harness, StringComparison.Ordinal)
    Assert.Contains("GitHubOrdinaryDeliveryTests|FullyQualifiedName~GitHubOrdinaryRuntimeTests", validator, StringComparison.Ordinal)
    Assert.Contains("test-callable-cli-installed-harness.py", validator, StringComparison.Ordinal)
    Assert.Contains("\"providerMutations\":0", retained, StringComparison.Ordinal)
    Assert.Contains("\"firstAccepted\":false", retained, StringComparison.Ordinal)
    Assert.Contains("\"replayNoOp\":true", retained, StringComparison.Ordinal)
    for expected in
        [
            "effect-outcomes-proven-absent-unknown-applied"
            "lost-journal-acknowledgements"
            "native-completion-reconciliation"
            "pending-or-nonzero-is-not-acceptance"
            "\"externalProviderMutation\":false"
        ] do Assert.Contains(expected, coverage, StringComparison.Ordinal)
    for expected in
        [
            "v2-call-01-4b-isolated-native-v1"
            "FS-GG/FS.GG.Coordination.CallableSandbox"
            "\"authorized\": false"
            "prepared-not-authorized"
            "refused-no-compatible-admitted-target"
        ] do Assert.Contains(expected, proposal, StringComparison.Ordinal)
