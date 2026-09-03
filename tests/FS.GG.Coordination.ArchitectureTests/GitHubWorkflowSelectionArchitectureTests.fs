module FS.GG.Coordination.GitHubWorkflowSelectionArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private shaFile path = File.ReadAllBytes(Path.Combine(root, path)) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private tracked paths =
    let startInfo = ProcessStartInfo("git")
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    for argument in [ "ls-files"; "--error-unmatch"; "--" ] @ paths do startInfo.ArgumentList.Add(argument)
    use child = Process.Start(startInfo)
    let output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output

let private runValidator script marker =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; script; "--"; "." ] do startInfo.ArgumentList.Add(argument)
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start(startInfo)
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, output + error)
    Assert.Contains(marker, output)

let private runCli arguments =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add(Path.Combine(root, "src/FS.GG.Coordination.Cli/bin/Release/net10.0/FS.GG.Coordination.Cli.dll"))
    for argument in arguments do startInfo.ArgumentList.Add(argument)
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start(startInfo)
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

[<Fact>]
let ``workflow selection public surface is pure and mutation free`` () =
    let surface = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubWorkflowSelectionQualification.fsi"
    for required in [ "type GitHubWorkflowObligation"; "type GitHubWorkflowSelectionSnapshot"; "type GitHubWorkflowSelectionReport"; "val compile"; "val verify"; "val validateSelection"; "val validateSupplyChain" ] do
        Assert.Contains(required, surface)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "val provision"; "PATCH"; "POST"; "DELETE" ] do
        Assert.DoesNotContain(forbidden, surface)

[<Fact>]
let ``production selector CLI handles arbitrary and merge-group inputs and fails stale authority closed`` () =
    let common request currentSettings =
        [ "workflow-select"; "--inventory"; "evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json"
          "--request"; request; "--current-base"; "57305e540267f3f4696ba5a6cdfc84361de577d3"
          "--current-settings"; currentSettings ]
    let goodCode, goodOutput, goodError =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-arbitrary.json" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518")
    Assert.Equal(0, goodCode)
    Assert.Empty(goodError)
    use good = JsonDocument.Parse(goodOutput)
    Assert.Equal("fsgg.coordination.workflow-selection-decision/1", good.RootElement.GetProperty("schema").GetString())
    Assert.Equal(6, good.RootElement.GetProperty("closure").GetArrayLength())
    let mergeCode, mergeOutput, _ =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-merge-group.json" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518")
    Assert.Equal(0, mergeCode)
    Assert.Contains(String.replicate 40 "c", mergeOutput)
    let staleCode, _, staleError =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-merge-group.json" (String.replicate 64 "d"))
    Assert.Equal(3, staleCode)
    Assert.Contains("stale-settings", staleError)

[<Fact>]
let ``repository owns callable reusable composite aggregate and sentinel contracts`` () =
    let reusable = read ".github/workflows/reusable-obligation-selection.yml"
    let composite = read ".github/actions/coordination-setup/action.yml"
    let sentinel = read ".github/workflows/workflow-selection-sentinel.yml"
    Assert.Contains("workflow_call:", reusable)
    Assert.Contains("workflow-select --inventory", reusable)
    Assert.Contains("if: ${{ always() }}", reusable)
    Assert.Contains("outcome: ${{ steps.aggregate.outputs.outcome }}", reusable)
    Assert.Contains("using: composite", composite)
    Assert.Contains("Verify the exact candidate", composite)
    Assert.Contains("schedule:", sentinel)
    Assert.Contains("workflow-selection-sentinel.sh", sentinel)
    Assert.DoesNotContain("gh api", reusable + sentinel)
    Assert.DoesNotContain("fleetSelectionEnabled=true", reusable + sentinel)

[<Fact>]
let ``original GS2-06-7 receipt remains byte immutable during repair`` () =
    Assert.Equal("9a98a13213c9a6934b362a6cb75dc3b523800205961e76cd4de984157733dc0b", shaFile "evidence/github-substrate-v2/accepted/GS2-06.7.json")
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-06.7.json")
    Assert.Equal("c6d1662e7df93f8b6ca8f577b5143e1e8a45eb9ac6fe55922488659ff9363036", receipt.RootElement.GetProperty("digest").GetString())

[<Fact>]
let ``retained corpus carries complete impact sentinel and fleet evidence`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-7/corpus.json")
    let value = corpus.RootElement
    Assert.True(value.GetProperty("complete").GetBoolean())
    Assert.True(value.GetProperty("inventoryComplete").GetBoolean())
    Assert.True(value.GetProperty("nonFileInputInventoryComplete").GetBoolean())
    Assert.Equal(4, value.GetProperty("workflows").GetArrayLength())
    Assert.Equal(6, value.GetProperty("obligations").GetArrayLength())
    Assert.Equal(11, value.GetProperty("impactCases").GetArrayLength())
    Assert.Equal(10, value.GetProperty("performance").GetArrayLength())
    Assert.True(value.GetProperty("sentinel").GetProperty("scheduled").GetBoolean())
    Assert.True(value.GetProperty("removalLedgerComplete").GetBoolean())
    Assert.Equal(0, value.GetProperty("removals").GetArrayLength())
    use deletionLedger = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-7/deletion-ledger.json")
    Assert.True(deletionLedger.RootElement.GetProperty("complete").GetBoolean())
    Assert.Empty(deletionLedger.RootElement.GetProperty("removedWorkflows").EnumerateArray())
    Assert.Empty(deletionLedger.RootElement.GetProperty("removedObligations").EnumerateArray())
    Assert.Equal("396645252215e6ff1d904d15cd3667530bf30ce93482df735a9f3ae94c8a5439", value.GetProperty("observationSha256").GetString())
    use observations = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-7/observed-workflow-runs.json")
    Assert.Equal(10, observations.RootElement.GetProperty("repositories").GetArrayLength())
    Assert.All(observations.RootElement.GetProperty("repositories").EnumerateArray(), fun repository ->
        Assert.Equal(8, repository.GetProperty("runs").GetArrayLength()))
    use expectations = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-7/independent-expectations.json")
    Assert.Equal(23, expectations.RootElement.GetProperty("selectionIndependentCases").GetArrayLength())
    Assert.Equal(12, expectations.RootElement.GetProperty("supplyChainIndependentCases").GetArrayLength())
    Assert.Equal(12, expectations.RootElement.GetProperty("shapeCases").GetArrayLength())

[<Fact>]
let ``roadmap registers distinct exact Q3 and Q7 commands`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use catalog = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-06.7")
    let contracts = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList
    Assert.Equal(2, contracts.Length)
    for id in [ "github-workflow-selection-contract"; "github-workflow-selection-supply-chain-contract" ] do
        let command = catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = id)
        let components = command.GetProperty("executable").GetString() :: (command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq)
        let digest = contracts |> Seq.find (fun value -> value.GetProperty("id").GetString() = id) |> _.GetProperty("commandSha256").GetString()
        Assert.Equal(components |> String.concat "\u0000" |> sha, digest)
    Assert.Equal("GS2-06.6", unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.exactlyOne |> _.GetString())

[<Fact>]
let ``exact workflow selection Q3 validator passes`` () =
    runValidator "eng/validate-github-workflow-selection.fsx" "GITHUB_WORKFLOW_SELECTION_OK"

[<Fact>]
let ``exact workflow selection Q7 validator passes`` () =
    runValidator "eng/validate-github-workflow-selection-supply-chain.fsx" "GITHUB_WORKFLOW_SELECTION_SUPPLY_CHAIN_OK"

[<Fact>]
let ``unknown properties are refused at every retained object boundary`` () =
    let validator = read "eng/validate-github-workflow-selection.fsx"
    for required in [ "corpus-top-level-extra"; "workflow-extra"; "edge-extra"; "impact-extra"; "merge-group-extra"; "outcome-extra"; "metric-extra"; "sentinel-extra"; "removal-extra"; "expectations-top-level-extra"; "selection-case-extra"; "supply-case-extra" ] do
        Assert.Contains(required, validator)
    Assert.Contains("unknown-property self-test failed", validator)

[<Fact>]
let ``workflow selection provider evidence is durable in the candidate Git tree`` () =
    let expected =
        [ "readiness/262-workflow-selection/analysis.json", "2c677d743162cdb9fa2f16064b8b95ca94ff1ba5810aa252086708700b4112a0"
          // These are the canonical FS.GG.SDD.Cli 1.0.0 no-change fixed-point bytes. A later
          // ambient provider can only replace them by updating this executable contract.
          "readiness/262-workflow-selection/work-model.json", "52e171dc758d093a0ca24ceb616bc80d7b0299e136374eb5e068dd73dfaea72f"
          "readiness/262-workflow-selection/verify.json", "6be84903f2a31444b7bc04389ac380e0b30afc4c4b8fb51bbe3934cc5aec424f"
          "readiness/262-workflow-selection/ship-verdict.json", "bd78c9834c912c58bc28dc8b12b5799f84db756aa68c6fd14bd5b5045feeefe6"
          "artifacts/test-results/262-workflow-selection/unit-tests.trx", "4e0cf739b8509141ba624149034074d2c8cd67420023a615a52fc8cbc7270c77" ]
    let paths = expected |> List.map fst
    let code, output = tracked paths
    if code <> 0 then failwith output
    for path, digest in expected do
        Assert.True(File.Exists(Path.Combine(root, path)), $"provider evidence is absent: {path}")
        Assert.Equal(digest, shaFile path)

    use workModel = JsonDocument.Parse(read "readiness/262-workflow-selection/work-model.json")
    let modelViews = workModel.RootElement.GetProperty("generatedViews").EnumerateArray() |> Seq.toList
    let modelView = modelViews |> List.exactlyOne
    Assert.Equal("readiness/262-workflow-selection/work-model.json", modelView.GetProperty("path").GetString())
    Assert.Equal("FS.GG.SDD.Artifacts", modelView.GetProperty("generator").GetProperty("id").GetString())
    Assert.Equal("1.0.0", modelView.GetProperty("generator").GetProperty("version").GetString())
    Assert.Equal("current", modelView.GetProperty("currency").GetString())
    Assert.Empty(workModel.RootElement.GetProperty("diagnostics").EnumerateArray())

    use verification = JsonDocument.Parse(read "readiness/262-workflow-selection/verify.json")
    let verified = verification.RootElement
    Assert.Equal("FS.GG.SDD.Artifacts/1.0.0", verified.GetProperty("generator").GetString())
    Assert.Equal("verificationReady", verified.GetProperty("status").GetString())
    Assert.Equal("implementationReady", verified.GetProperty("lifecycleReadiness").GetProperty("status").GetString())
    Assert.Empty(verified.GetProperty("findings").EnumerateArray())
    Assert.Empty(verified.GetProperty("diagnostics").EnumerateArray())
    let verifiedViews = verified.GetProperty("generatedViews").EnumerateArray() |> Seq.toList
    Assert.Equal(2, verifiedViews.Length)
    for view in verifiedViews do
        Assert.Equal("current", view.GetProperty("currency").GetString())
        Assert.Empty(view.GetProperty("diagnosticIds").EnumerateArray())
    let workModelSource =
        verified.GetProperty("sources").EnumerateArray()
        |> Seq.find (fun source -> source.GetProperty("path").GetString() = "readiness/262-workflow-selection/work-model.json")
    Assert.Equal("52e171dc758d093a0ca24ceb616bc80d7b0299e136374eb5e068dd73dfaea72f", workModelSource.GetProperty("digest").GetProperty("value").GetString())

    let evidence = read "work/262-workflow-selection/evidence.yml"
    Assert.Equal(19, evidence.Split("source: artifacts/test-results/262-workflow-selection/unit-tests.trx", StringSplitOptions.None).Length - 1)
    Assert.Equal(19, evidence.Split("sha256:4e0cf739b8509141ba624149034074d2c8cd67420023a615a52fc8cbc7270c77", StringSplitOptions.None).Length - 1)

    let architectureReport = "artifacts/test-results/262-workflow-selection/workflow-selection-architecture.trx"
    let architectureCode, architectureOutput = tracked [ architectureReport ]
    if architectureCode <> 0 then failwith architectureOutput
    Assert.True(File.Exists(Path.Combine(root, architectureReport)), "architecture evidence is absent")
