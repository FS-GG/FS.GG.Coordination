module FS.GG.Coordination.RepositorySettingsTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private desiredPath = Path.Combine(root, "eng/repository-settings/desired.json")
let private fixturePath = Path.Combine(root, "eng/repository-settings/fixture.json")

let private verify receiptPath =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/repository-settings/verify.fsx"; "--"; desiredPath; receiptPath ] do
        startInfo.ArgumentList.Add(argument)
    use child = Process.Start(startInfo)
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output + error

let private withMutation (oldValue: string) (newValue: string) (assertion: string -> unit) =
    let original = File.ReadAllText(fixturePath)
    Assert.Contains(oldValue, original)
    let changed = original.Replace(oldValue, newValue)
    Assert.NotEqual<string>(original, changed)
    let path = Path.Combine(Path.GetTempPath(), $"repository-settings-{Guid.NewGuid():N}.json")
    try
        File.WriteAllText(path, changed)
        assertion path
    finally
        File.Delete(path)

[<Fact>]
let ``repository provisioning contract is closed and least privilege`` () =
    use desired = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/repository-settings/desired.json")))
    let value = desired.RootElement
    Assert.Equal("fsgg.coordination.repository-settings-desired/1", value.GetProperty("schema").GetString())
    Assert.Equal("selected", value.GetProperty("actions").GetProperty("allowedActions").GetString())
    Assert.True(value.GetProperty("actions").GetProperty("githubOwnedAllowed").GetBoolean())
    Assert.False(value.GetProperty("actions").GetProperty("verifiedAllowed").GetBoolean())
    Assert.Empty(value.GetProperty("actions").GetProperty("patternsAllowed").EnumerateArray())
    let checks = value.GetProperty("checks").EnumerateArray() |> Seq.toList
    Assert.Equal(6, checks.Length)
    Assert.All(checks, fun check -> Assert.Equal(15368, check.GetProperty("integrationId").GetInt32()))
    Assert.All(value.GetProperty("rulesets").EnumerateArray(), fun ruleset -> Assert.Equal(0, ruleset.GetProperty("bypassActorCount").GetInt32()))

[<Fact>]
let ``canonical provisioning fixture passes the strict validator`` () =
    let exitCode, output = verify fixturePath
    Assert.Equal(0, exitCode)
    Assert.Contains("repository-settings: PASS", output)

[<Theory>]
[<InlineData("\"id\":1346720714", "\"id\":1346720715", "RS-STATE-MISMATCH")>]
[<InlineData("\"allowedActions\":\"selected\"", "\"allowedActions\":\"all\"", "RS-STATE-MISMATCH")>]
[<InlineData("\"permission\":\"maintain\"", "\"permission\":\"admin\"", "RS-STATE-MISMATCH")>]
[<InlineData("\"integrationId\":15368", "\"integrationId\":15369", "RS-STATE-MISMATCH")>]
[<InlineData("\"dependencyGraph\":\"enabled\"", "\"dependencyGraph\":\"disabled\"", "RS-STATE-MISMATCH")>]
[<InlineData("\"bypassActorCount\":0", "\"bypassActorCount\":1", "RS-RULESET-MISMATCH")>]
[<InlineData("\"requireCodeOwnerReview\":true", "\"requireCodeOwnerReview\":false", "RS-RULESET-MISMATCH")>]
[<InlineData("\"doNotEnforceOnCreate\":true", "\"doNotEnforceOnCreate\":false", "RS-RULESET-MISMATCH")>]
[<InlineData("\"updateAllowsFetchAndMerge\":false", "\"updateAllowsFetchAndMerge\":true", "RS-RULESET-MISMATCH")>]
[<InlineData(",\"update\"]", "]", "RS-RULESET-MISMATCH")>]
[<InlineData("\"httpStatus\":403", "\"httpStatus\":200", "RS-STATE-MISMATCH")>]
[<InlineData("\"id\":1,\"include\"", "\"id\":0,\"include\"", "RS-RULESET-ID")>]
[<InlineData("\"name\":\"teams\"", "\"name\":\"teams-missing\"", "RS-OPERATIONS")>]
[<InlineData("dependency-graph/sbom", "issues", "RS-OPERATION-CONTRACT")>]
[<InlineData("\"httpStatus\":204", "\"httpStatus\":200", "RS-OPERATION-CONTRACT")>]
[<InlineData("vulnerability-alerts", "actions/artifacts", "RS-OPERATION-CONTRACT")>]
[<InlineData("Coordination/teams", "Coordination/collaborators", "RS-OPERATION-CONTRACT")>]
[<InlineData("\"method\":\"GET\",\"name\":\"dependency-graph\"", "\"method\":\"POST\",\"name\":\"dependency-graph\"", "RS-OPERATION-CONTRACT")>]
[<InlineData("\"httpStatus\":200,\"method\":\"GET\",\"name\":\"main-ruleset\"", "\"httpStatus\":204,\"method\":\"GET\",\"name\":\"main-ruleset\"", "RS-RULESET-RESPONSE")>]
[<InlineData("rulesets/1\"", "rulesets/9\"", "RS-RULESET-RESPONSE")>]
[<InlineData("\"digest\":\"e", "\"digest\":\"8", "RS-RECEIPT-DIGEST")>]
let ``validator rejects material receipt mutation`` oldValue newValue expectedRule =
    withMutation oldValue newValue (fun path ->
        let exitCode, output = verify path
        Assert.NotEqual(0, exitCode)
        Assert.Contains(expectedRule, output))

[<Fact>]
let ``validator rejects noncanonical receipt bytes`` () =
    withMutation "{\"actions\"" "{ \"actions\"" (fun path ->
        let exitCode, output = verify path
        Assert.NotEqual(0, exitCode)
        Assert.Contains("RS-RECEIPT-CANONICAL", output))

[<Fact>]
let ``ruleset requests bind review checks signatures and no bypass`` () =
    let branch = File.ReadAllText(Path.Combine(root, "eng/repository-settings/main-ruleset.json"))
    let tags = File.ReadAllText(Path.Combine(root, "eng/repository-settings/release-tag-ruleset.json"))
    for token in [ "required_status_checks"; "require_code_owner_review"; "require_last_push_approval"; "required_review_thread_resolution"; "strict_required_status_checks_policy" ] do
        Assert.Contains(token, branch)
    for check in [ "deterministic-build"; "compiler-and-tests"; "dependency-and-security"; "package-install-smoke"; "bootstrap-recovery"; "evidence-manifest" ] do
        Assert.Contains(check, branch)
    Assert.Contains("\"bypass_actors\":[]", branch)
    Assert.Contains("\"bypass_actors\":[]", tags)
    Assert.Contains("required_signatures", tags)
    Assert.Contains("\"type\":\"update\"", tags)
    Assert.Contains("\"update_allows_fetch_and_merge\":false", tags)

[<Fact>]
let ``codeowners protects every provisioning authority surface`` () =
    let owners = File.ReadAllText(Path.Combine(root, ".github/CODEOWNERS"))
    for path in [ "*"; "/.github/"; "/eng/"; "/evidence/"; "/src/FS.GG.Coordination.Protocol/"; "/src/FS.GG.Coordination.Qualification.Contracts/" ] do
        Assert.Contains($"%s{path} @FS-GG/coordination-maintainers", owners)
