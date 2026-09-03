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

let private runBash arguments =
    let startInfo = ProcessStartInfo("bash")
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
    let common request currentSettings currentQueuedHead =
        [ "workflow-select"; "--inventory"; "evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json"
          "--request"; request; "--current-base"; "57305e540267f3f4696ba5a6cdfc84361de577d3"
          "--current-settings"; currentSettings; "--current-queued-head"; currentQueuedHead
          "--expected-inventory-version"; "coordination-workflows/1"
          "--expected-graph-version"; "fsgg.workflow-impact/1"
          "--expected-seal"; "ba78404be6abddc7f4bd2c057b19468b226f9b51fd9012a48b9d630ef5829421" ]
    let goodCode, goodOutput, goodError =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-arbitrary.json" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518" "none")
    Assert.Equal(0, goodCode)
    Assert.Empty(goodError)
    use good = JsonDocument.Parse(goodOutput)
    Assert.Equal("fsgg.coordination.workflow-selection-decision/1", good.RootElement.GetProperty("schema").GetString())
    Assert.Equal(6, good.RootElement.GetProperty("closure").GetArrayLength())
    let mergeCode, mergeOutput, _ =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-merge-group.json" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518" (String.replicate 40 "c"))
    Assert.Equal(0, mergeCode)
    Assert.Contains(String.replicate 40 "c", mergeOutput)
    let staleCode, _, staleError =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-merge-group.json" (String.replicate 64 "d") (String.replicate 40 "c"))
    Assert.Equal(3, staleCode)
    Assert.Contains("stale-settings", staleError)
    let staleHeadCode, _, staleHeadError =
        runCli (common "evidence/github-substrate-v2/gs2-06-7/runtime-request-merge-group.json" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518" (String.replicate 40 "d"))
    Assert.Equal(3, staleHeadCode)
    Assert.Contains("invalid-merge-group", staleHeadError)
    let inventedVersion =
        common "evidence/github-substrate-v2/gs2-06-7/runtime-request-arbitrary.json" "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518" "none"
        |> List.map (fun value -> if value = "coordination-workflows/1" then "invented-v999" else value)
    let inventedCode, _, inventedError = runCli inventedVersion
    Assert.Equal(3, inventedCode)
    Assert.Contains("unsupported-inventory-version", inventedError)

[<Fact>]
let ``repository owns callable reusable composite aggregate and sentinel contracts`` () =
    let reusable = read ".github/workflows/reusable-obligation-selection.yml"
    let composite = read ".github/actions/coordination-setup/action.yml"
    let sentinel = read ".github/workflows/workflow-selection-sentinel.yml"
    Assert.Contains("workflow_call:", reusable)
    Assert.Contains("workflow-select --inventory", reusable)
    Assert.Contains("git ls-files --error-unmatch", reusable)
    Assert.Contains("realpath -e", reusable)
    Assert.Contains("CURRENT_QUEUED_HEAD\" != \"$CANDIDATE_SHA", reusable)
    Assert.Contains("BUILD_SELECTION", reusable)
    Assert.Contains("BUILD_RESULT", reusable)
    Assert.Contains("workflow-selection-aggregate.sh", reusable)
    Assert.Contains("if: ${{ always() }}", reusable)
    Assert.Contains("outcome: ${{ steps.aggregate.outputs.outcome }}", reusable)
    Assert.Contains("using: composite", composite)
    Assert.Contains("Verify the exact candidate", composite)
    Assert.Contains("schedule:", sentinel)
    Assert.Contains("workflow-selection-sentinel.sh", sentinel)
    Assert.DoesNotContain("gh api", reusable + sentinel)
    Assert.DoesNotContain("fleetSelectionEnabled=true", reusable + sentinel)

[<Fact>]
let ``sentinel consumes the typed Q7 missed-obligation decision and disables selection`` () =
    let temporary = Path.Combine(Path.GetTempPath(), $"fsgg-gs267-sentinel-{Guid.NewGuid():N}")
    Directory.CreateDirectory(temporary) |> ignore
    let q7Decision = Path.Combine(temporary, "q7.json")
    let sentinelDecision = Path.Combine(temporary, "sentinel.json")
    let q7 = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; "eng/validate-github-workflow-selection-supply-chain.fsx"; "--"; "."; "--decision"; q7Decision ] do
        q7.ArgumentList.Add(argument)
    q7.WorkingDirectory <- root
    q7.RedirectStandardOutput <- true
    q7.RedirectStandardError <- true
    q7.UseShellExecute <- false
    use q7Child = Process.Start(q7)
    let q7Output = q7Child.StandardOutput.ReadToEnd() + q7Child.StandardError.ReadToEnd()
    q7Child.WaitForExit()
    Assert.True(q7Child.ExitCode = 0, q7Output)

    let sentinelCode, sentinelOutput, sentinelError =
        runBash [ "eng/workflow-selection-sentinel.sh"; "--decision-only"; q7Decision; sentinelDecision ]
    Assert.True((sentinelCode = 1), sentinelOutput + sentinelError)
    use decision = JsonDocument.Parse(File.ReadAllText sentinelDecision)
    Assert.True(decision.RootElement.GetProperty("missedObligation").GetBoolean())
    Assert.Equal("disabled", decision.RootElement.GetProperty("fleetSelection").GetString())
    Assert.Contains("release", decision.RootElement.GetProperty("missedObligations").EnumerateArray() |> Seq.map _.GetString())
    Assert.False(decision.RootElement.GetProperty("productionMutation").GetBoolean())

    let invalidCases =
        [ "non-hex-seal", """{"schema":"fsgg.coordination.workflow-selection-supply-chain-decision/1","seal":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz","missedObligations":[],"fleetSelection":"eligible","productionMutation":false}"""
          "unknown-obligation", """{"schema":"fsgg.coordination.workflow-selection-supply-chain-decision/1","seal":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","missedObligations":["invented"],"fleetSelection":"disabled","productionMutation":false}"""
          "duplicate-obligation", """{"schema":"fsgg.coordination.workflow-selection-supply-chain-decision/1","seal":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","missedObligations":["release","release"],"fleetSelection":"disabled","productionMutation":false}"""
          "extra-property", """{"schema":"fsgg.coordination.workflow-selection-supply-chain-decision/1","seal":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","missedObligations":[],"fleetSelection":"eligible","productionMutation":false,"invented":true}""" ]
    for name, payload in invalidCases do
        let input = Path.Combine(temporary, $"{name}.json")
        let output = Path.Combine(temporary, $"{name}-decision.json")
        File.WriteAllText(input, payload)
        let code, stdout, stderr = runBash [ "eng/workflow-selection-sentinel.sh"; "--decision-only"; input; output ]
        Assert.True((code = 1), stdout + stderr)
        use invalidDecision = JsonDocument.Parse(File.ReadAllText output)
        Assert.Equal("disabled", invalidDecision.RootElement.GetProperty("fleetSelection").GetString())
        Assert.Equal("invalid-q7-decision", invalidDecision.RootElement.GetProperty("reason").GetString())

    let selectionPath = Path.Combine(temporary, "selection.json")
    let selectionCode, selectionOutput, selectionError =
        runCli
            [ "workflow-select"; "--inventory"; "evidence/github-substrate-v2/gs2-06-7/runtime-inventory.json"
              "--request"; "evidence/github-substrate-v2/gs2-06-7/runtime-request-sentinel.json"
              "--expected-inventory-version"; "coordination-workflows/1"
              "--expected-graph-version"; "fsgg.workflow-impact/1"
              "--expected-seal"; "ba78404be6abddc7f4bd2c057b19468b226f9b51fd9012a48b9d630ef5829421"
              "--current-base"; "57305e540267f3f4696ba5a6cdfc84361de577d3"
              "--current-settings"; "5c7cd805ec9924c1895749df66fc0fd49eedbfeadd8721baafd75ced79a89518"
              "--current-queued-head"; "none" ]
    Assert.True((selectionCode = 0), selectionError)
    File.WriteAllText(selectionPath, selectionOutput)
    let missedFailures = Path.Combine(temporary, "missed-failures.json")
    File.WriteAllText(missedFailures, "[\"release\"]")
    let missedDecision = Path.Combine(temporary, "missed-decision.json")
    let missedCode, missedOutput, missedError =
        runBash [ "eng/workflow-selection-sentinel.sh"; "--compare-selection"; selectionPath; missedFailures; q7Decision; missedDecision ]
    Assert.True((missedCode = 1), missedOutput + missedError)
    use currentMiss = JsonDocument.Parse(File.ReadAllText missedDecision)
    Assert.Equal("disabled", currentMiss.RootElement.GetProperty("fleetSelection").GetString())
    Assert.Contains("release", currentMiss.RootElement.GetProperty("missedObligations").EnumerateArray() |> Seq.map _.GetString())

    let passingFailures = Path.Combine(temporary, "passing-failures.json")
    File.WriteAllText(passingFailures, "[]")
    let passingDecision = Path.Combine(temporary, "passing-decision.json")
    let passingCode, passingOutput, passingError =
        runBash [ "eng/workflow-selection-sentinel.sh"; "--compare-selection"; selectionPath; passingFailures; q7Decision; passingDecision ]
    Assert.True((passingCode = 0), passingOutput + passingError)
    use currentPass = JsonDocument.Parse(File.ReadAllText passingDecision)
    Assert.Equal("eligible", currentPass.RootElement.GetProperty("fleetSelection").GetString())
    Directory.Delete(temporary, true)

[<Fact>]
let ``stable aggregate correlates every child result with its selection disposition`` () =
    let passing =
        [ "eng/workflow-selection-aggregate.sh"; "success"
          "selected"; "success"; "selected"; "success"; "selected"; "success"
          "selected"; "success"; "not-applicable"; "skipped"; "not-applicable"; "skipped" ]
    let passingCode, passingOutput, passingError = runBash passing
    Assert.True((passingCode = 0), passingOutput + passingError)
    Assert.Contains("outcome=passed", passingOutput)
    let skippedSelected = passing |> List.mapi (fun index value -> if index = 3 then "skipped" else value)
    let failingCode, failingOutput, failingError = runBash skippedSelected
    Assert.True((failingCode = 1), failingOutput + failingError)
    Assert.Contains("selection/result mismatch", failingError)

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
        [ "readiness/262-workflow-selection/analysis.json", "4b6ce85f7b36fdb7e5803888a483d691e386991352fefc97102be74c10ced1b1"
          // These are the canonical FS.GG.SDD.Cli 1.0.0 no-change fixed-point bytes. A later
          // ambient provider can only replace them by updating this executable contract.
          "readiness/262-workflow-selection/work-model.json", "93c160daac181327f2ca6b054d450743f6e31ead397ec73fa1bba464136def97"
          "readiness/262-workflow-selection/verify.json", "7052b9549137650fccf71c207eb4042ef6c545729a37bab4b9af3b972016f136"
          "readiness/262-workflow-selection/ship-verdict.json", "57700e22ffa7aa7f5343d5eb4fc9b8e0e204cf548a00ac21086a9f8cbd33377e"
          "artifacts/test-results/262-workflow-selection/unit-tests.trx", "a2746a37b8b8d477a00b43fa702e1c5161c567e0898b2dbf8537e517cbce53cd" ]
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
    Assert.Equal("93c160daac181327f2ca6b054d450743f6e31ead397ec73fa1bba464136def97", workModelSource.GetProperty("digest").GetProperty("value").GetString())

    let evidence = read "work/262-workflow-selection/evidence.yml"
    Assert.Equal(19, evidence.Split("source: artifacts/test-results/262-workflow-selection/unit-tests.trx", StringSplitOptions.None).Length - 1)
    Assert.Equal(19, evidence.Split("sha256:a2746a37b8b8d477a00b43fa702e1c5161c567e0898b2dbf8537e517cbce53cd", StringSplitOptions.None).Length - 1)

    let architectureReport = "artifacts/test-results/262-workflow-selection/workflow-selection-architecture.trx"
    let architectureCode, architectureOutput = tracked [ architectureReport ]
    if architectureCode <> 0 then failwith architectureOutput
    Assert.True(File.Exists(Path.Combine(root, architectureReport)), "architecture evidence is absent")
