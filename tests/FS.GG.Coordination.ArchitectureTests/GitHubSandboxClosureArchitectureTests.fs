module FS.GG.Coordination.GitHubSandboxClosureArchitectureTests

open System
open System.Diagnostics
open System.IO
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let signaturePrecedesPureImplementation () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(project.IndexOf("GitHubSandboxClosure.fsi", StringComparison.Ordinal) < project.IndexOf("GitHubSandboxClosure.fs\"", StringComparison.Ordinal))
    let signature = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/GitHubSandboxClosure.fsi"))
    for required in [ "SandboxPlan"; "SandboxClosureReceipt"; "SandboxCleanupResult"; "validatePlan"; "validateReceipt"; "planDigest"; "resultDigest" ] do Assert.Contains(required, signature, StringComparison.Ordinal)
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/GitHubSandboxClosure.fs"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable"; "Process.Start" ] do Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)

[<Fact>]
let q4ValidatorIsLiteralOfflineAndMutationSensitive () =
    let validator = File.ReadAllText(Path.Combine(root, "eng/validate-github-sandbox-closure.fsx"))
    for required in [ "ProductionIdentity"; "ProductionTarget"; "ProductionCredential"; "StaleFence"; "ResponseUnknown"; "PartialCleanup"; "ReceiptSubstitution"; "WarmReuse"; "OmittedAdapter"; "let generated"; "let independent" ] do Assert.Contains(required, validator, StringComparison.Ordinal)
    for forbidden in [ "HttpClient"; "api.github.com"; "GITHUB_TOKEN"; "Environment.GetEnvironmentVariable" ] do Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-sandbox-closure.fsx"; "--"; "." ] do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-sandbox-closure-contract OK controls=10 q=Q4 network=offline provenance=synthetic baseline=green", output, StringComparison.Ordinal)

[<Fact>]
let comprehensiveRouteIsColdAndLiveAuthorityFailsClosed () =
    let harness = File.ReadAllText(Path.Combine(root, "eng/qualify-github-sandbox-closure.sh"))
    for script in [ "validate-github-transport.fsx"; "validate-github-issue-field.fsx"; "validate-github-native-relation.fsx"; "validate-github-project-adapter.fsx"; "validate-github-comment-projection.fsx"; "validate-github-sharded-journal.fsx"; "validate-github-repository-settings.fsx"; "validate-github-actions-release-feed.fsx"; "validate-github-sandbox-closure.fsx" ] do Assert.Contains(script, harness, StringComparison.Ordinal)
    for required in [ "child_pid=$!"; "wait \"$child_pid\""; "production-capable or unmarked authority refused before any write"; "execute-github-sandbox-live.sh" ] do Assert.Contains(required, harness, StringComparison.Ordinal)
    let live = File.ReadAllText(Path.Combine(root, "eng/execute-github-sandbox-live.sh"))
    for required in [ "fs-gg-cross-repo-dispatch[bot]"; "R_kgDOUKXpqQ"; "PVT_kwDOEYAWY84BiESo"; "refusing before any write"; "addProjectV2ItemById"; "deleteProjectV2Item"; "sub_issues"; "release-asset.txt"; "labels:[$issue1.labels[].name]"; "labels:$labels"; "[.labels[].name]|sort"; "cleanup.json"; "closure.json" ] do Assert.Contains(required, live, StringComparison.Ordinal)
