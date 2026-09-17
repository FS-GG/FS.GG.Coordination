module FS.GG.Coordination.GitHubV1BridgeReceiversArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private bytes path = File.ReadAllBytes(Path.Combine(root, path))
let private sha256 (value: byte array) = SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()

let private validEvidence () =
    let value =
        JsonNode.Parse(read "evidence/github-substrate-v2/gs2-08-8/receiver-qualification.json").AsObject()

    value["state"] <- "qualified"
    value["qualified"] <- true
    value["replaceBeforeQualification"] <- false

    value

let private runValidator (evidence: JsonObject option) =
    let temporary = Path.Combine(Path.GetTempPath(), "gs2-08-8-" + Guid.NewGuid().ToString("N") + ".json")

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
                "eng/validate-github-v1-bridge-receivers.fsx"
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

let private clone (value: JsonObject) = JsonNode.Parse(value.ToJsonString()).AsObject()
let private objectProperty (name: string) (value: JsonObject) = value[name].AsObject()
let private arrayProperty (name: string) (value: JsonObject) = value[name].AsArray()
let private arrayObject (index: int) (value: JsonArray) = value[index].AsObject()
let private receiverAt (index: int) (value: JsonObject) = arrayProperty "receivers" value |> arrayObject index

[<Fact>]
let ``checked-in aggregate qualifies receivers Templates and public SDD 2-0-1`` () =
    let exitCode, output, error = runValidator None
    Assert.True((exitCode = 0), error)
    Assert.Contains("receivers=8 baseline=615 successor=620", output)

[<Fact>]
let ``complete offline aggregate closes all eight receiver and route bindings`` () =
    let exitCode, output, error = runValidator (Some(validEvidence ()))
    Assert.True((exitCode = 0), error)
    Assert.Contains("receivers=8 baseline=615 successor=620 dispositions=3 packages=3", output)

[<Fact>]
let ``independent offline controls reject receiver package revision route and false-adoption substitutions`` () =
    let controls: (string * (JsonObject -> unit)) list =
        [
            "missing-receiver", fun (value: JsonObject) -> arrayProperty "receivers" value |> fun receivers -> receivers.RemoveAt(7)
            "old-package",
            fun value ->
                receiverAt 0 value
                |> arrayProperty "packages"
                |> arrayObject 0
                |> fun package -> package["version"] <- "0.89.0"
            "substituted-package",
            fun value ->
                objectProperty "bridge" value
                |> arrayProperty "packages"
                |> arrayObject 0
                |> fun package -> package["payloadSha256"] <- String('0', 64)
            "duplicate-cli-pin-replacing-kit",
            fun value ->
                receiverAt 0 value
                |> arrayProperty "pins"
                |> arrayObject 1
                |> fun pin -> pin["package"] <- "FS.GG.Coord.Cli"
            "wrong-route-sha",
            fun value ->
                receiverAt 0 value
                |> arrayProperty "routes"
                |> arrayObject 0
                |> fun route -> route["sha256"] <- String('0', 64)
            "swapped-hub-route-dispositions",
            fun value ->
                let routes = receiverAt 0 value |> arrayProperty "routes"
                (arrayObject 0 routes)["disposition"] <- "gs2-08.9-sealing"
                (arrayObject 3 routes)["disposition"] <- "bridge-adopted"
            "stale-head",
            fun value -> receiverAt 0 value |> objectProperty "protected" |> fun state -> state["observedHead"] <- String('f', 40)
            "stale-tree",
            fun value -> receiverAt 0 value |> objectProperty "protected" |> fun state -> state["observedTree"] <- String('e', 40)
            "stale-report",
            fun value -> receiverAt 0 value |> objectProperty "report" |> fun report -> report["observedSha256"] <- String('d', 64)
            "missing-route",
            fun value ->
                receiverAt 0 value
                |> arrayProperty "routes"
                |> fun routes -> routes.RemoveAt(0)
            "mutable-workflow",
            fun value ->
                receiverAt 4 value
                |> arrayProperty "callableRoutes"
                |> arrayObject 1
                |> fun workflow -> workflow["ref"] <- "main"
            "successful-production-mutation",
            fun value ->
                receiverAt 0 value
                |> objectProperty "qualification"
                |> fun qualification -> qualification["productionMutation"] <- "succeeded"
            "dashboard-is-not-adoption",
            fun value ->
                receiverAt 0 value
                |> objectProperty "qualification"
                |> fun qualification -> qualification["dashboardNotificationPresentedAsAdoption"] <- true
        ]

    let valid = validEvidence ()

    for name, mutate in controls do
        let candidate = clone valid
        mutate candidate
        let exitCode, output, error = runValidator (Some candidate)
        Assert.True(exitCode <> 0, $"{name} unexpectedly passed: {output} {error}")

[<Fact>]
let ``validator is offline and contains no provider client boundary`` () =
    let validator = read "eng/validate-github-v1-bridge-receivers.fsx"

    for forbidden in [ "HttpClient"; "api.github.com"; "gh api"; "GITHUB_TOKEN"; "Environment.GetEnvironmentVariable" ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)

[<Fact>]
let ``GS2-08-8 registration binds accepted publication and exact offline command`` () =
    use units = JsonDocument.Parse(bytes "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(bytes "eng/github-substrate-v2-gates.json")

    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.8")

    Assert.Equal<string list>([ "GS2-08.7" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "Q3" ], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)

    use receipt = JsonDocument.Parse(bytes "evidence/github-substrate-v2/accepted/GS2-08.8.json")
    let receiptValue = receipt.RootElement

    Assert.Equal("fsgg.coordination.unit-acceptance/1", receiptValue.GetProperty("schema").GetString())
    Assert.Equal("GS2-08.8", receiptValue.GetProperty("unitId").GetString())
    Assert.Equal("accepted", receiptValue.GetProperty("state").GetString())
    Assert.Equal(unitValue.GetProperty("contractSha256").GetString(), receiptValue.GetProperty("unitContractSha256").GetString())
    Assert.Equal("1d4186ca14b9b5f89453491f2247815c9dfcb64d", receiptValue.GetProperty("sourceRevision").GetString())
    Assert.Equal("319f5482028ef9cf39989308e6455a4610b640b2b32522a7ebe8f823d7b1fb77", receiptValue.GetProperty("digest").GetString())

    let contract = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne

    let command =
        gates.RootElement.GetProperty("commands").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = contract.GetProperty("id").GetString())

    let commandBytes =
        seq {
            command.GetProperty("executable").GetString()
            yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
        }
        |> String.concat "\u0000"
        |> Encoding.UTF8.GetBytes

    Assert.Equal("github-v1-bridge-receivers-contract", contract.GetProperty("id").GetString())
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal(sha256 commandBytes, contract.GetProperty("commandSha256").GetString())

    let exitGate = unitValue.GetProperty("exitGate").GetString()
    Assert.Contains("all eight receivers", exitGate)
    Assert.Contains("615-route baseline", exitGate)
    Assert.Contains("SDD and Templates", exitGate)
