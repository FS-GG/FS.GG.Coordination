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

[<Fact>]
let ``workflow selection public surface is pure and mutation free`` () =
    let surface = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubWorkflowSelectionQualification.fsi"
    for required in [ "type GitHubWorkflowObligation"; "type GitHubWorkflowSelectionSnapshot"; "type GitHubWorkflowSelectionReport"; "val compile"; "val verify"; "val validateSelection"; "val validateSupplyChain" ] do
        Assert.Contains(required, surface)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "val provision"; "PATCH"; "POST"; "DELETE" ] do
        Assert.DoesNotContain(forbidden, surface)

[<Fact>]
let ``retained corpus carries complete impact sentinel and fleet evidence`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-7/corpus.json")
    let value = corpus.RootElement
    Assert.True(value.GetProperty("complete").GetBoolean())
    Assert.True(value.GetProperty("inventoryComplete").GetBoolean())
    Assert.True(value.GetProperty("nonFileInputInventoryComplete").GetBoolean())
    Assert.Equal(2, value.GetProperty("workflows").GetArrayLength())
    Assert.Equal(6, value.GetProperty("obligations").GetArrayLength())
    Assert.Equal(11, value.GetProperty("impactCases").GetArrayLength())
    Assert.Equal(10, value.GetProperty("performance").GetArrayLength())
    Assert.True(value.GetProperty("sentinel").GetProperty("scheduled").GetBoolean())
    Assert.True(value.GetProperty("removalLedgerComplete").GetBoolean())
    Assert.Equal(3, value.GetProperty("removals").GetArrayLength())
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
        [ "readiness/262-workflow-selection/analysis.json", "35486521af635d52719802e20471a30d4f8b67f7bf57460f8c9606195500ebbd"
          // These are the canonical FS.GG.SDD.Cli 1.0.0 no-change fixed-point bytes. A later
          // ambient provider can only replace them by updating this executable contract.
          "readiness/262-workflow-selection/work-model.json", "a2b96fd1c4c2180a5cad0fd359c78de3ab2f9e26d48be2965314fd73eff7175a"
          "readiness/262-workflow-selection/verify.json", "b5d87f7ca2784e0edee9e90656b016f45d247e06cde663850e6d047d86aea1dc"
          "readiness/262-workflow-selection/ship-verdict.json", "13fb2777d7c7a6ad570803bcc39e15b1c249fcb30f03158b268d977c4ec209c1"
          "artifacts/test-results/262-workflow-selection/unit-tests.trx", "e5e82831872d0ee3c26182ee768f7b0bfcb1feb9f59e575f6fd83b8981a8fbae"
          "artifacts/test-results/262-workflow-selection/workflow-selection-architecture.trx", "ad66e4aeb9a9ee31c351221ab45f27db4d1b22d45ad9cbcc62e558032a4c7ccb" ]
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
    Assert.Equal("a2b96fd1c4c2180a5cad0fd359c78de3ab2f9e26d48be2965314fd73eff7175a", workModelSource.GetProperty("digest").GetProperty("value").GetString())

    let evidence = read "work/262-workflow-selection/evidence.yml"
    Assert.Equal(19, evidence.Split("source: artifacts/test-results/262-workflow-selection/unit-tests.trx", StringSplitOptions.None).Length - 1)
    Assert.Equal(19, evidence.Split("sha256:e5e82831872d0ee3c26182ee768f7b0bfcb1feb9f59e575f6fd83b8981a8fbae", StringSplitOptions.None).Length - 1)
