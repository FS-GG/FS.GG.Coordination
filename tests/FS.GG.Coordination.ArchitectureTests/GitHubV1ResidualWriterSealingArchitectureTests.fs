module FS.GG.Coordination.GitHubV1ResidualWriterSealingArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private bytes path = File.ReadAllBytes(Path.Combine(root, path))

let private sha256 (value: byte array) =
    value |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private aggregate () =
    JsonNode.Parse(read "evidence/github-substrate-v2/gs2-08-9/sealing-qualification.json").AsObject()

let private clone (value: JsonObject) = JsonNode.Parse(value.ToJsonString()).AsObject()
let private objectProperty (name: string) (value: JsonObject) = value[name].AsObject()
let private arrayProperty (name: string) (value: JsonObject) = value[name].AsArray()
let private arrayObject (index: int) (value: JsonArray) = value[index].AsObject()

let private runValidator (evidence: JsonObject option) =
    let temporary = Path.Combine(Path.GetTempPath(), "gs2-08-9-" + Guid.NewGuid().ToString("N") + ".json")

    try
        evidence |> Option.iter (fun value -> File.WriteAllText(temporary, value.ToJsonString()))
        let start = ProcessStartInfo("dotnet")
        start.WorkingDirectory <- root
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true

        for argument in
            [
                "fsi"
                "eng/validate-github-v1-residual-writer-sealing.fsx"
                "--"
                "."
                if evidence.IsSome then temporary
            ] do
            start.ArgumentList.Add argument

        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, output, error
    finally
        if File.Exists temporary then File.Delete temporary

[<Fact>]
let ``checked-in GS2-08-9 aggregate qualifies without minting a receipt`` () =
    let exitCode, output, error = runValidator None
    Assert.Equal(0, exitCode)
    Assert.Contains("GS2089-QUALIFIED", output)
    Assert.Contains("Q4 remains unclaimed", output)
    Assert.Empty(error)
    Assert.True(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.9.json")))

[<Fact>]
let ``aggregate binds the protected seals disabled workflows and retained Q4 boundary`` () =
    use document = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-9/sealing-qualification.json")
    let value = document.RootElement
    Assert.Equal("qualified", value.GetProperty("state").GetString())
    Assert.Equal(22, value.GetProperty("routes").GetArrayLength())
    Assert.Equal(3, value.GetProperty("sourceSeals").GetArrayLength())
    Assert.Equal(5, value.GetProperty("workflowAdministration").GetProperty("workflows").GetArrayLength())

    for workflow in value.GetProperty("workflowAdministration").GetProperty("workflows").EnumerateArray() do
        Assert.True(workflow.GetProperty("sourceSealed").GetBoolean())
        Assert.Equal("disabled_manually", workflow.GetProperty("state").GetString())

    Assert.Equal("unclaimed", value.GetProperty("q4").GetProperty("state").GetString())
    Assert.False(value.GetProperty("q4").GetProperty("providerMutationClaimed").GetBoolean())
    Assert.False(value.GetProperty("acceptanceReceiptCreated").GetBoolean())

    let dispatch = value.GetProperty("dispatchAdministration")
    Assert.Equal("8cea64e7dc551fbec46479331f71787384b4c7495f6b75b012d7a0d8d3295a1c", dispatch.GetProperty("evidenceSha256").GetString())
    Assert.All(
        dispatch.GetProperty("secretScopes").EnumerateArray(),
        fun scope ->
            Assert.Equal(204, scope.GetProperty("deleteStatus").GetInt32())
            Assert.False(scope.GetProperty("renderingPresentAfter").GetBoolean())
    )
    Assert.False(dispatch.GetProperty("historicalCaller").GetProperty("executed").GetBoolean())

    let receiver = value.GetProperty("receiverAdoption")
    Assert.Equal("accepted", receiver.GetProperty("state").GetString())
    Assert.Equal("ff0afa32fbcd55247ead2d1321d2df72ada91600", receiver.GetProperty("protectedMerge").GetString())
    Assert.Equal(35227810436L, receiver.GetProperty("qualificationRun").GetInt64())
    Assert.True(receiver.GetProperty("acceptedReceiptPresent").GetBoolean())

    let helper = value.GetProperty("helperBoundary")
    Assert.Equal("4d92bd4181725745fb9517437aa31d58f0668a12", helper.GetProperty("protectedMerge").GetString())
    Assert.True(helper.GetProperty("mergedToMain").GetBoolean())
    Assert.True(helper.GetProperty("publishedCopiesRetired").GetBoolean())
    Assert.True(helper.GetProperty("callersRetired").GetBoolean())

    let runtime = value.GetProperty("runtimeRetirement")
    Assert.Equal("fd3b625f5938840d4d63b9a650326799bcabae024ec964a731ab923765112549", runtime.GetProperty("evidenceSha256").GetString())
    Assert.Equal("17c49c59aae3e07b82707c307dc9b88d04270efc", runtime.GetProperty("systemAdmin").GetProperty("revision").GetString())
    Assert.Equal(2, runtime.GetProperty("uninstallAttempts").GetArrayLength())
    Assert.All(runtime.GetProperty("uninstallAttempts").EnumerateArray(), fun attempt -> Assert.Equal(1, attempt.GetProperty("exitStatus").GetInt32()))
    Assert.Equal(0, runtime.GetProperty("manualRemediation").GetProperty("exitStatus").GetInt32())
    Assert.True(runtime.GetProperty("manualRemediation").GetProperty("manifestAndPathAbsenceVerified").GetBoolean())

[<Fact>]
let ``GS2-08-9 registration gate receipt and storage index bind the qualified source revision`` () =
    use units = JsonDocument.Parse(bytes "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(bytes "eng/github-substrate-v2-gates.json")

    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.9")

    Assert.Equal<string list>([ "GS2-08.8" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "Q3" ], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Contains("all 22 residual writer routes", unitValue.GetProperty("exitGate").GetString())
    Assert.Contains("Q4 remains unclaimed", unitValue.GetProperty("exitGate").GetString())

    let calculatedContract =
        AcceptanceReceiptDigest.canonicalBytesOmitting "contractSha256" unitValue |> sha256

    Assert.Equal(calculatedContract, unitValue.GetProperty("contractSha256").GetString())

    let contract = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    let command =
        gates.RootElement.GetProperty("commands").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-residual-writer-sealing-contract")

    let commandBytes =
        seq {
            command.GetProperty("executable").GetString()
            yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
        }
        |> String.concat "\u0000"
        |> Encoding.UTF8.GetBytes

    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal(sha256 commandBytes, contract.GetProperty("commandSha256").GetString())

    let receiptBytes = bytes "evidence/github-substrate-v2/accepted/GS2-08.9.json"
    use receipt = JsonDocument.Parse(receiptBytes)
    let receiptValue = receipt.RootElement
    Assert.Equal("accepted", receiptValue.GetProperty("state").GetString())
    Assert.Equal("179a485e249d39794f5f1b02fe7ddbbfbf1e88a1", receiptValue.GetProperty("sourceRevision").GetString())
    Assert.Equal(unitValue.GetProperty("contractSha256").GetString(), receiptValue.GetProperty("unitContractSha256").GetString())
    Assert.Equal("dfee1381892be08a5a3ced90a596c4084fba81c4f702f930ccc6f0b784d4470c", receiptValue.GetProperty("digest").GetString())
    Assert.True(AcceptanceReceiptDigest.verify (ReadOnlyMemory receiptBytes) "GS2-08.9" (receiptValue.GetProperty("digest").GetString()) receiptValue |> Result.isOk)

    let artifacts =
        receiptValue.GetProperty("artifacts").EnumerateArray()
        |> Seq.map (fun artifact -> artifact.GetProperty("name").GetString(), artifact.GetProperty("sha256").GetString())
        |> Map.ofSeq

    Assert.Equal("44abcf15afa2cdd14ffee9ee41c6e3820dcf4d8a7876015c5b15b05f19f08d71", artifacts["residual-writer-sealing-aggregate"])
    Assert.Equal("fd3b625f5938840d4d63b9a650326799bcabae024ec964a731ab923765112549", artifacts["host-helper-retirement-mailbox-evidence"])

    use index = JsonDocument.Parse(bytes "evidence/github-substrate-v2/index.json")
    let entry =
        index.RootElement.GetProperty("entries").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "accepted-GS2-08.9")

    Assert.Equal(receiptBytes.Length, entry.GetProperty("bytes").GetInt32())
    Assert.Equal(sha256 receiptBytes, entry.GetProperty("sha256").GetString())

[<Fact>]
let ``bounded controls reject route capability identity telemetry and helper misstatements`` () =
    let controls: (string * (JsonObject -> unit)) list =
        [
            "missing-route", fun value -> arrayProperty "routes" value |> fun routes -> routes.RemoveAt(0)
            "duplicate-route",
            fun value ->
                let routes = arrayProperty "routes" value
                routes.Add(JsonNode.Parse(routes[0].ToJsonString()))
            "false-artifact-unavailable-refusal",
            fun value ->
                arrayProperty "historicalClients" value
                |> arrayObject 0
                |> fun client -> client["refusalAttempted"] <- true
            "historical-sha-still-credentialed-without-blocker",
            fun value ->
                arrayProperty "historicalClients" value
                |> arrayObject 1
                |> fun client -> client["historicalShaCredentialed"] <- true
            "dispatch-admin-false-readback",
            fun value ->
                objectProperty "dispatchAdministration" value
                |> arrayProperty "secretScopes"
                |> arrayObject 0
                |> fun scope -> scope["renderingPresentAfter"] <- true
            "workflow-source-sealed-but-admin-active",
            fun value ->
                objectProperty "workflowAdministration" value
                |> arrayProperty "workflows"
                |> arrayObject 0
                |> fun workflow -> workflow["state"] <- "active"
            "stale-source-identity", fun value -> value["sourceHead"] <- String('f', 40)
            "missing-receiver-receipt",
            fun value -> objectProperty "receiverAdoption" value |> fun receiver -> receiver["acceptedReceiptPresent"] <- false
            "helper-source-not-merged",
            fun value -> objectProperty "helperBoundary" value |> fun helper -> helper["mergedToMain"] <- false
            "false-zero-effect",
            fun value -> objectProperty "telemetryBoundary" value |> fun boundary -> boundary["providerMutationCount"] <- 1
            "telemetry-scope-escalation",
            fun value -> objectProperty "telemetryBoundary" value |> fun boundary -> boundary["authorityEscalation"] <- "acknowledged"
            "telemetry-redirect-followed",
            fun value -> objectProperty "telemetryBoundary" value |> fun boundary -> boundary["redirect"] <- "followed"
            "helper-receipt-attempt",
            fun value -> objectProperty "helperBoundary" value |> fun boundary -> boundary["mutationAttempts"] <- 1
            "helper-readback-misattribution",
            fun value ->
                objectProperty "helperBoundary" value
                |> fun boundary -> boundary["admissionRefusalReadbackAttributedAsDelivery"] <- true
            "failed-uninstall-observation-erased",
            fun value ->
                objectProperty "runtimeRetirement" value
                |> arrayProperty "uninstallAttempts"
                |> arrayObject 0
                |> fun attempt -> attempt["exitStatus"] <- 0
            "manual-remediation-not-verified",
            fun value ->
                objectProperty "runtimeRetirement" value
                |> objectProperty "manualRemediation"
                |> fun remediation -> remediation["manifestAndPathAbsenceVerified"] <- false
            "retained-helper-selected",
            fun value ->
                objectProperty "runtimeRetirement" value
                |> objectProperty "inventory"
                |> fun inventory -> inventory["retainedHelpersSelectedByActiveCaller"] <- true
            "runtime-conclusion-not-accepted",
            fun value ->
                objectProperty "runtimeRetirement" value
                |> objectProperty "conclusion"
                |> fun conclusion -> conclusion["accepted"] <- false
        ]

    let valid = aggregate ()

    for name, mutate in controls do
        let candidate = clone valid
        mutate candidate
        let exitCode, output, error = runValidator (Some candidate)
        Assert.True(exitCode <> 0, $"{name} unexpectedly passed: {output} {error}")

[<Fact>]
let ``helper evidence template permits immutable history only after callers and credentials are retired`` () =
    use document = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-9/sealing-qualification.json")
    let helper = document.RootElement.GetProperty("helperBoundary")
    let template = helper.GetProperty("requiredEvidenceTemplate")
    Assert.Equal("retained-history-allowed", template.GetProperty("immutablePublicPackages").GetString())
    Assert.Contains("no trusted runner", template.GetProperty("sufficientWhen").GetString())
    Assert.Equal(6, template.GetProperty("inventories").GetArrayLength())
    Assert.Equal(6, template.GetProperty("readOnlyCommands").GetArrayLength())
    Assert.Contains("SystemAdmin/Containers/Containerfile.fsharp", template.GetProperty("knownCaller").GetString())

[<Fact>]
let ``validator is offline and cannot create a native receipt`` () =
    let validator = read "eng/validate-github-v1-residual-writer-sealing.fsx"

    for forbidden in
        [
            "HttpClient"
            "api.github.com"
            "gh api"
            "GITHUB_TOKEN"
            "Environment.GetEnvironmentVariable"
            "accepted/GS2-08.9.json"
        ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
