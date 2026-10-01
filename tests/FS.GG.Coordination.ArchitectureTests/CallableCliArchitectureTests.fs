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
            "<Version>0.2.0</Version>"
            "<PackageVersion>0.2.0</PackageVersion>"
            "<PackageReleaseNotes>Adds the portable workspace v1 contract and packaged executor assembly while preserving all published source profiles.</PackageReleaseNotes>"
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
    for expected in
        [
            "DOTNET_PROCESSOR_COUNT: 4"
            "run-packaged-portable-workspace-qualification.py"
            "load --input \"$IMAGE_STATE/candidate.oci.tar\""
            "passed\"], evidence[\"failed\"], evidence[\"unknown\"]) == (6, 0, 0)"
            "portable-workspace-release.fsx prepare"
            "ARTIFACT_ROOT: /tmp/pw-artifact-${{ github.run_id }}-${{ github.run_attempt }}"
            "EVIDENCE_ROOT: /tmp/pw-evidence-${{ github.run_id }}-${{ github.run_attempt }}"
            "cp \"$CANDIDATE_OUTPUT\"/* \"$ARTIFACT_ROOT/\""
            "path: ${{ env.ARTIFACT_ROOT }}/"
            "compression-level: 0"
        ] do Assert.Contains(expected, workflow, StringComparison.Ordinal)
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
            "[ \"0.1.1\"; \"0.1.2\"; \"0.1.3\"; \"0.1.4\"; \"0.1.5\"; \"0.1.6\"; \"0.1.7\"; \"0.2.0\" ]"
            "projectPackageVersion () = version"
            "let tag = $\"v{version}\""
            "root.GetProperty(\"tag\").GetString() = tag"
            "candidate package must contain exactly one Execution assembly"
            "installed schema export changed: {name}"
            "installed CLI did not refuse a malformed portable contract"
        ] do Assert.Contains(expected, script, StringComparison.Ordinal)

[<Fact>]
let ``portable release preparation binds explicit package image and qualification bytes without effect authority`` () =
    let script = read "eng/portable-workspace-release.fsx"
    for expected in
        [
            "only reviewed portable release version 0.2.0 may be prepared"
            "--package-manifest"
            "--image-archive"
            "--image-manifest"
            "--image-qualification"
            "--executor-evidence"
            "portable-workspace-v1-{version}.zip"
            "portable-workspace-linux-amd64-{version}.oci.tar"
            "portable-workspace-release-manifest.json"
            "package must contain exactly one Execution assembly"
            "OCI archive must contain exactly one image manifest"
            "executor qualification did not pass strictly"
            "manifest.Add(\"publicationAuthorized\", false)"
            "manifest.Add(\"tagAuthorized\", false)"
            "manifest.Add(\"activationAuthorized\", false)"
        ] do Assert.Contains(expected, script, StringComparison.Ordinal)

    Assert.DoesNotContain("podman pull", script, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("docker pull", script, StringComparison.OrdinalIgnoreCase)
    Assert.True(File.Exists(Path.Combine(root, "tests/portable-workspace/release/test_release_helper.py")))
    Assert.True(File.Exists(Path.Combine(root, "tests/portable-workspace/release/test_packaged_qualification.py")))
    Assert.True(File.Exists(Path.Combine(root, "tests/portable-workspace/release/test_artifact_layout.py")))
    Assert.True(File.Exists(Path.Combine(root, "tests/portable-workspace/release/test_release_readback.py")))
    let packaged = read "eng/run-packaged-portable-workspace-qualification.py"
    for expected in [ "package digest changed"; "qualification script package reference changed"; "DOTNET_PROCESSOR_COUNT\": \"4\""; "publicationAuthorized\": False" ] do
        Assert.Contains(expected, packaged, StringComparison.Ordinal)

[<Fact>]
let ``protected publication route preserves exact bytes ordering and recovery boundaries`` () =
    let workflow = read ".github/workflows/callable-cli-release-publish.yml"
    for expected in
        [
            "operation:"
            "publish-v2-lang-01-2-cli-020"
            "PACKAGE_VERSION: 0.2.0"
            "PORTABLE_BUNDLE: portable-workspace-v1-0.2.0.zip"
            "PORTABLE_IMAGE: portable-workspace-linux-amd64-0.2.0.oci.tar"
            "EXPECTED_BUNDLE_SHA256:"
            "EXPECTED_IMAGE_SHA256:"
            "packages: write"
            "id-token: write"
            "attestations: write"
            "NuGet/login@8d196754b4036150537f80ac539e15c2f1028841"
            "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c"
            "EXPECTED_OPERATION: publish-v2-lang-01-2-cli-020"
            "EXPECTED_PREPARATION_RUN_ID: 36794564231"
            "EXPECTED_PREPARATION_ARTIFACT_ID: 11133062598"
            "test \"$value\" != 0000000000000000000000000000000000000000"
            "[[ \"$value\" =~ ^[0-9a-f]{40}$ ]]"
            "[[ \"$EXPECTED_PREPARATION_ARTIFACT_ID\" =~ ^[1-9][0-9]*$ ]]"
            ".workflow_run.id == $run"
            ".workflow_run.head_sha == $source"
            "cmp \"$CANDIDATE_OUTPUT/$PACKAGE_FILE\" \"$READBACK_OUTPUT/reproduced/$PACKAGE_FILE\""
            "portable-workspace-release.fsx\" verify"
            "load --input \"$CANDIDATE_OUTPUT/$PORTABLE_IMAGE\""
            "run-packaged-portable-workspace-qualification.py"
            ".passed == 6 and .failed == 0 and .unknown == 0"
            "Anonymous public install and schema readback"
            "cmp \"contracts/portable-workspace/v1/$name.schema.json\""
            "eng/repository-settings/desired.json"
            "Publish to GitHub Packages first"
            "Observe nuget.org and validate recoverable ordering before either effect"
            "Create immutable tag and GitHub release only after both feeds settle"
            "Observe release assets and refuse collisions before any package effect"
            "Bind an existing complete release receipt before either package effect"
            "write-callable-cli-release-readback.py"
            "readback_present=true"
            "GITHUB_PRESENT: ${{ steps.org.outputs.present }}"
            "GitHub release observation is unknown"
            "Accept: application/octet-stream"
            "if: always()"
        ] do Assert.Contains(expected, workflow, StringComparison.Ordinal)

    Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.Ordinal)
    Assert.DoesNotContain("actions/permissions/selected-actions", workflow, StringComparison.Ordinal)
    Assert.DoesNotContain("NUGET_API_KEY }}", workflow.Replace("steps.nuget-login.outputs.NUGET_API_KEY }}", ""), StringComparison.Ordinal)
    let githubPush = workflow.IndexOf("nuget.pkg.github.com/FS-GG/index.json", StringComparison.Ordinal)
    let publicPush = workflow.IndexOf("api.nuget.org/v3/index.json", StringComparison.Ordinal)
    let releaseCollision = workflow.IndexOf("Observe release assets and refuse collisions before any package effect", StringComparison.Ordinal)
    let receiptCollision = workflow.IndexOf("Bind an existing complete release receipt before either package effect", StringComparison.Ordinal)
    Assert.True(releaseCollision >= 0 && releaseCollision < githubPush)
    Assert.True(receiptCollision > releaseCollision && receiptCollision < githubPush)
    Assert.True(githubPush >= 0 && publicPush > githubPush)

    let operation = read "eng/callable-cli-release-operation-017.json"
    Assert.Contains("\"operation\": \"publish-c3-coordination-cli-017\"", operation, StringComparison.Ordinal)
    Assert.Contains("\"preparationArtifactId\": 10989613858", operation, StringComparison.Ordinal)
    Assert.Contains("\"protectedMerge\": \"fdfdcfc91e65f814b42d8c9e061fec0e6221a139\"", operation, StringComparison.Ordinal)
    Assert.Contains("\"version\": \"0.1.7\"", operation, StringComparison.Ordinal)
    Assert.Contains("\"authorized\": false", operation, StringComparison.Ordinal)
    Assert.Contains("\"tagAfterBothFeeds\": true", operation, StringComparison.Ordinal)

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
