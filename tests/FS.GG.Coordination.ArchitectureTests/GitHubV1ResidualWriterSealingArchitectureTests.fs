module FS.GG.Coordination.GitHubV1ResidualWriterSealingArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))

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
let ``checked-in GS2-08-9 aggregate refuses closure with the exact pending code`` () =
    let exitCode, output, error = runValidator None
    Assert.Equal(78, exitCode)
    Assert.Empty(output)
    Assert.Contains("GS2089-PENDING", error)
    Assert.Contains("helper PR #3530 merge", error)
    Assert.Contains("GS2-08.8 PR #417 merge/receipt", error)
    Assert.False(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.9.json")))

[<Fact>]
let ``aggregate binds the protected seals disabled workflows and retained Q4 boundary`` () =
    use document = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-9/sealing-qualification.json")
    let value = document.RootElement
    Assert.Equal("pending-external-evidence", value.GetProperty("state").GetString())
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
        ]

    let valid = aggregate ()

    for name, mutate in controls do
        let candidate = clone valid
        mutate candidate
        let exitCode, output, error = runValidator (Some candidate)
        Assert.NotEqual(78, exitCode)
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
