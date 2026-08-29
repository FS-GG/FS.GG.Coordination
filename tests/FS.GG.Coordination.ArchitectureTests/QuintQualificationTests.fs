module FS.GG.Coordination.QuintQualificationTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private executeWith selfTest extraArguments =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    // The ordinary architecture lane deliberately has no formal compiler. Future
    // admission must fail closed here; the canonical lane supplies the digest-pinned binary.
    info.Environment["FSGG_QUINT_BIN"] <- ""
    info.Environment["FSGG_QUINT_CACHE"] <- ""
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add "eng/validate-quint-qualification.fsx"
    info.ArgumentList.Add "--"
    if selfTest then info.ArgumentList.Add "--self-test"
    info.ArgumentList.Add "--root"
    info.ArgumentList.Add "."
    info.ArgumentList.Add "--config"
    info.ArgumentList.Add "eng/quint-qualification.json"
    for argument in extraArguments do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

let private execute selfTest = executeWith selfTest []

[<Fact>]
let ``bounded roots classifications selection and admission are complete`` () =
    let exitCode, output, error = execute false
    Assert.True((exitCode = 0), $"%s{output}\n%s{error}")
    Assert.Contains("roots=7 selected=authority,desired-state,lifecycle,mutation-saga,protocol-streams,qualification,relations oracles=11 negativeControls=0", output)

[<Fact>]
let ``independent oracles and qualification contracts reject every focused mutation`` () =
    let exitCode, output, error = execute true
    Assert.True((exitCode = 0), $"%s{output}\n%s{error}")
    Assert.Contains("roots=7 selected=authority,desired-state,lifecycle,mutation-saga,protocol-streams,qualification,relations oracles=11 negativeControls=20", output)

[<Fact>]
let ``changed paths surfaces reuse and future proposals bind selection inputs`` () =
    let plan = Path.GetTempFileName()
    let proposal = Path.GetTempFileName()
    let invalidProposedSource = Path.GetTempFileName()
    let proposedSource = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
    let compilerReceipt = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/receipt.json")
    let reuseReceipt = Path.GetTempFileName()
    try
        let behaviorSha =
            SHA256.HashData(File.ReadAllBytes proposedSource) |> Convert.ToHexString |> _.ToLowerInvariant()
        let compilerReceiptSha =
            SHA256.HashData(File.ReadAllBytes compilerReceipt) |> Convert.ToHexString |> _.ToLowerInvariant()
        let proposalText = """{
          "schema":"fsgg.coordination.quint-proposal/1",
          "owner":"future-audit", "source":"SOURCE_PATH", "behaviorSha256":"BEHAVIOR_SHA",
          "compilerReceipt":"COMPILER_RECEIPT", "compilerReceiptSha256":"COMPILER_RECEIPT_SHA",
          "imports":["qualification"],
          "invariants":["mutationIntentsConflict"], "independentOracles":["abstraction-equivalence"],
          "root":"CoordinationProtocol",
          "bounds":{"depth":6,"states":100000,"samples":200,"elapsedMs":60000,"peakMiB":2048,"artifactBytes":6291456},
          "witnesses":["createIntent","parentChildEdge","init"],
          "projections":["qualification-manifest"], "ciImpact":"bounded-state-root",
          "budgetEffect":"within-calibrated-envelope"
        }"""
        let validProposal =
            proposalText
                .Replace("SOURCE_PATH", proposedSource)
                .Replace("BEHAVIOR_SHA", behaviorSha)
                .Replace("COMPILER_RECEIPT_SHA", compilerReceiptSha)
                .Replace("COMPILER_RECEIPT", compilerReceipt)
        File.WriteAllText(proposal, validProposal)
        let pullExit, pullOutput, pullError =
            executeWith false [ "--mode"; "pull-request"; "--changed-path"; "eng/quint-qualification.json"; "--plan-out"; plan ]
        Assert.True((pullExit = 0), $"%s{pullOutput}\n%s{pullError}")
        let pullPlan = JsonNode.Parse(File.ReadAllText plan).AsObject()
        Assert.Equal(7, pullPlan["roots"].AsArray().Count)

        let oracleExit, oracleOutput, oracleError =
            executeWith false [ "--mode"; "pull-request"; "--changed-surface"; "oracle:dependency-concurrency" ]
        Assert.True((oracleExit = 0), $"%s{oracleOutput}\n%s{oracleError}")
        Assert.Contains("selected=qualification,relations", oracleOutput)

        let configuration = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/quint-qualification.json")))
        let sourceSha = configuration["sourceSha256"].GetValue<string>()
        let fileSha path =
            SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString |> _.ToLowerInvariant()
        File.WriteAllText(reuseReceipt, $"""{{
          "schema":"fsgg.coordination.quint-reuse/1", "sourceSha256":"%s{sourceSha}",
          "configurationSha256":"%s{fileSha (Path.Combine(root, "eng/quint-qualification.json"))}",
          "baselineSha256":"%s{fileSha (Path.Combine(root, "eng/quint-qualification-baseline.json"))}",
          "backendIdentity":"quint-rust-apalache",
          "toolchainIdentity":"79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486",
          "selectedRoots":["qualification","relations"]
        }}""")
        let reuseExit, reuseOutput, reuseError =
            executeWith false [ "--mode"; "reuse"; "--changed-surface"; "budget:relations"; "--reuse-source-sha256"; sourceSha; "--reuse-receipt"; reuseReceipt ]
        Assert.True((reuseExit = 0), $"%s{reuseOutput}\n%s{reuseError}")
        Assert.Contains("selected=qualification,relations", reuseOutput)

        let futureExit, futureOutput, futureError =
            executeWith false [ "--mode"; "future-behavior"; "--proposal"; proposal ]
        Assert.NotEqual(0, futureExit)
        Assert.Contains("QQ-PROPOSAL-COMPILER-UNAVAILABLE", futureOutput + futureError)

        File.WriteAllText(proposal, File.ReadAllText(proposal).Replace(behaviorSha, String.replicate 64 "b"))
        let badFutureExit, badFutureOutput, badFutureError =
            executeWith false [ "--mode"; "future-behavior"; "--proposal"; proposal ]
        Assert.NotEqual(0, badFutureExit)
        Assert.Contains("QQ-PROPOSAL-BEHAVIOR", badFutureOutput + badFutureError)

        File.WriteAllText(invalidProposedSource, "module QualificationFutureAuditRoot { !!! invalid Quint !!! }")
        let invalidBehaviorSha =
            SHA256.HashData(File.ReadAllBytes invalidProposedSource) |> Convert.ToHexString |> _.ToLowerInvariant()
        File.WriteAllText(proposal, validProposal.Replace(proposedSource, invalidProposedSource).Replace(behaviorSha, invalidBehaviorSha))
        let invalidFutureExit, invalidFutureOutput, invalidFutureError =
            executeWith false [ "--mode"; "future-behavior"; "--proposal"; proposal ]
        Assert.NotEqual(0, invalidFutureExit)
        Assert.Contains("QQ-PROPOSAL-SOURCE", invalidFutureOutput + invalidFutureError)

        let missingPathExit, missingPathOutput, missingPathError =
            executeWith false [ "--mode"; "pull-request"; "--changed-path"; "eng/does-not-exist.qnt" ]
        Assert.NotEqual(0, missingPathExit)
        Assert.Contains("QQ-SELECTION-PATH-MISSING", missingPathOutput + missingPathError)

        let unboundReuseExit, unboundReuseOutput, unboundReuseError =
            executeWith false [ "--mode"; "reuse"; "--changed-surface"; "budget:relations"; "--reuse-source-sha256"; sourceSha ]
        Assert.NotEqual(0, unboundReuseExit)
        Assert.Contains("QQ-SELECTION-INPUT", unboundReuseOutput + unboundReuseError)
    finally
        File.Delete plan
        File.Delete proposal
        File.Delete invalidProposedSource
        File.Delete reuseReceipt

[<Fact>]
let ``imports from any Quint module are part of the executable closure`` () =
    let scratch = Directory.CreateTempSubdirectory("fsgg-quint-import-closure-")
    try
        let protocolDirectory = Path.Combine(scratch.FullName, "src/FS.GG.Coordination.Protocol")
        let engDirectory = Path.Combine(scratch.FullName, "eng")
        Directory.CreateDirectory protocolDirectory |> ignore
        Directory.CreateDirectory engDirectory |> ignore
        let sourcePath = Path.Combine(protocolDirectory, "Protocol.md")
        let configPath = Path.Combine(engDirectory, "quint-qualification.json")
        let baselinePath = Path.Combine(engDirectory, "quint-qualification-baseline.json")
        let mutatedSource =
            File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"))
                .Replace(
                    "module QualificationAuthorityRoot {",
                    "module QualificationInjectedHelper { pure val injected = true }\nmodule QualificationAuthorityRoot {\n  import QualificationInjectedHelper.injected")
        File.WriteAllText(sourcePath, mutatedSource)
        let fileSha path =
            SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString |> _.ToLowerInvariant()
        let config = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/quint-qualification.json"))).AsObject()
        config["sourceSha256"] <- JsonValue.Create(fileSha sourcePath)
        File.WriteAllText(configPath, config.ToJsonString())
        let baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/quint-qualification-baseline.json"))).AsObject()
        baseline["sourceSha256"] <- JsonValue.Create(fileSha sourcePath)
        baseline["configurationSha256"] <- JsonValue.Create(fileSha configPath)
        File.WriteAllText(baselinePath, baseline.ToJsonString())
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in [ "fsi"; "eng/validate-quint-qualification.fsx"; "--"; "--root"; scratch.FullName; "--config"; "eng/quint-qualification.json"; "--mode"; "protected"; "--protected-mode"; "main" ] do
            info.ArgumentList.Add argument
        use child = Process.Start info
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.NotEqual(0, child.ExitCode)
        Assert.Contains("QQ-ROOT-EXECUTABLE-CLOSURE", output + error)
    finally
        scratch.Delete true

[<Fact>]
let ``missing or over budget measurements are rejected before reuse`` () =
    let scratch = Directory.CreateTempSubdirectory("fsgg-quint-budget-")
    try
        let protocolDirectory = Path.Combine(scratch.FullName, "src/FS.GG.Coordination.Protocol")
        let engDirectory = Path.Combine(scratch.FullName, "eng")
        Directory.CreateDirectory protocolDirectory |> ignore
        Directory.CreateDirectory engDirectory |> ignore
        File.Copy(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"), Path.Combine(protocolDirectory, "Protocol.md"))
        File.Copy(Path.Combine(root, "eng/quint-qualification.json"), Path.Combine(engDirectory, "quint-qualification.json"))
        let baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/quint-qualification-baseline.json"))).AsObject()
        (((baseline["measurements"].AsArray())[0]).AsObject())["elapsedMs"] <- JsonValue.Create(30001)
        File.WriteAllText(Path.Combine(engDirectory, "quint-qualification-baseline.json"), baseline.ToJsonString())
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in [ "fsi"; "eng/validate-quint-qualification.fsx"; "--"; "--root"; scratch.FullName; "--config"; "eng/quint-qualification.json" ] do
            info.ArgumentList.Add argument
        use child = Process.Start info
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.NotEqual(0, child.ExitCode)
        Assert.Contains("QQ-BASELINE-BUDGET", output + error)
    finally
        scratch.Delete true
