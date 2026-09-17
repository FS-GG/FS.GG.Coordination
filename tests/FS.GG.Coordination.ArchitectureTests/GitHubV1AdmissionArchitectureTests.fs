module FS.GG.Coordination.GitHubV1AdmissionArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private read path =
    File.ReadAllText(Path.Combine(root, path))

let private digestText (value: string) =
    Text.Encoding.UTF8.GetBytes value
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

[<Fact>]
let ``admission registry boundary is typed journal only and has no provider capability`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/V1AdmissionRegistry.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/V1AdmissionRegistry.fs"

    for required in
        [
            "MutationContext"
            "OperationHandle"
            "OutboundRequest"
            "AuthorityGitPort"
            "AdmissionsClosing"
            "EffectSettlementProvenAbsent"
        ] do
        Assert.Contains(required, signature)

    for forbidden in
        [
            "HttpClient"
            "api.github.com"
            "GITHUB_TOKEN"
            "Authorization:"
            "Process.Start"
        ] do
        Assert.DoesNotContain(forbidden, signature + implementation, StringComparison.OrdinalIgnoreCase)

    Assert.Contains("lowerHex 40", implementation)
    Assert.Contains("lowerHex 64", implementation)
    Assert.Contains("authority-head-moved", implementation)

[<Fact>]
let ``GS2-08-5 registration requires installed ledger and accepted common fence`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")

    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.5")

    Assert.Equal<string list>(
        [ "GS2-08.2"; "GS2-08.4" ],
        unitValue.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(
        "0ff1ee7944b734937938b9cb24c2cea6f26e1c617f5313f9be5eda67176153e3",
        unitValue.GetProperty("contractSha256").GetString()
    )

    Assert.Equal<string list>(
        [ "Q1"; "Q2"; "Q3" ],
        unitValue.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let command =
        gates.RootElement.GetProperty("commands").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-admission-contract")

    let components =
        seq {
            command.GetProperty("executable").GetString()
            yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
        }

    let contract =
        unitValue.GetProperty("gateContracts").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-admission-contract")

    Assert.Equal(contract.GetProperty("commandSha256").GetString(), components |> String.concat "\u0000" |> digestText)
    Assert.Contains("future producer behavior", unitValue.GetProperty("exitGate").GetString())

[<Fact>]
let ``canonical generated receipt binds admission model source`` () =
    use receipt =
        JsonDocument.Parse(read "src/FS.GG.Coordination.Protocol/Generated/receipt.json")

    let source =
        File.ReadAllBytes(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"))
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    Assert.Equal(source, receipt.RootElement.GetProperty("sourceSha256").GetString())
    let protocol = read "src/FS.GG.Coordination.Protocol/Protocol.md"
    Assert.Contains("fleetAdmissionMayAppend", protocol)
    Assert.Contains("admissionRoundMaySeal", protocol)
    Assert.Contains("admittedEffectMayDispatch", protocol)
