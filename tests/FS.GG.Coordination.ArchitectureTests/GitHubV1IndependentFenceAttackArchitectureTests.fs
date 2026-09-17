module FS.GG.Coordination.GitHubV1IndependentFenceAttackArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha256Bytes (value: byte array) = SHA256.HashData value |> Convert.ToHexString |> _.ToLowerInvariant()
let private sha256 path = File.ReadAllBytes(Path.Combine(root, path)) |> sha256Bytes

[<Fact>]
let ``GS2-08-5 acceptance binds protected producer integration without live activation`` () =
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.5.json")
    let value = receipt.RootElement
    Assert.Equal("accepted", value.GetProperty("state").GetString())
    Assert.Equal("cc70abbacf31a7f7ff45aadb2aedbf37e8a8999c", value.GetProperty("sourceRevision").GetString())
    Assert.Equal("5189e9336df0c049410bb78d027e4c08759ee2da53f1eb692c8b019740e8756b", value.GetProperty("digest").GetString())
    let names = value.GetProperty("artifacts").EnumerateArray() |> Seq.map (fun artifact -> artifact.GetProperty("name").GetString()) |> Set.ofSeq
    Assert.Contains("producer-protected-merge-cc70abbacf31a7f7ff45aadb2aedbf37e8a8999c", names)
    Assert.Contains("independent-review-head-6b48448cf580fc848178e370f1f1c2c4003c2c4d", names)
    Assert.DoesNotContain(names, fun name -> name.Contains("activation", StringComparison.OrdinalIgnoreCase))

[<Fact>]
let ``GS2-08-6 accepts exact Q3 and Q6 attack evidence without a Q4 claim`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.6")
    Assert.Equal<string list>([ "GS2-08.3"; "GS2-08.5" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "Q3"; "Q6" ], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.DoesNotContain("Q4", unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString())
    Assert.Contains("executes 66 exact independently authored tests", unitValue.GetProperty("exitGate").GetString())
    Assert.Contains("GS2-08.9 inputs rather than false passes", unitValue.GetProperty("exitGate").GetString())
    Assert.Contains("Q4 and provider-specific delayed-response exclusion remain unclaimed", unitValue.GetProperty("exitGate").GetString())
    let commands = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.map (fun value -> value.GetProperty("id").GetString(), value) |> Map.ofSeq
    for contract in unitValue.GetProperty("gateContracts").EnumerateArray() do
        let id = contract.GetProperty("id").GetString()
        let command = commands[id]
        let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
        let actual = components |> String.concat "\u0000" |> Encoding.UTF8.GetBytes |> sha256Bytes
        Assert.Equal(contract.GetProperty("commandSha256").GetString(), actual)

[<Fact>]
let ``independent attack evidence binds refreshed populations and preserves GS2-08-9 findings`` () =
    use binding = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-6/source-binding.json")
    let value = binding.RootElement
    Assert.Equal(sha256 "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json", value.GetProperty("closedPopulationSha256").GetString())
    Assert.Equal("a0b381fa9c5c1d43c143b09a722a2bc394b75d3d46ebe1d899d9af58580357f7", value.GetProperty("producerCensusSha256").GetString())
    Assert.Contains("no live GitHub provider evidence", value.GetProperty("scope").GetString(), StringComparison.OrdinalIgnoreCase)
    use blockers = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-6/gs2-08-9-blockers.json")
    let clients = blockers.RootElement.GetProperty("clients").EnumerateArray() |> Seq.toList
    Assert.Equal<string list>([ "0.58.0"; "0.75.4" ], clients |> List.map (fun client -> client.GetProperty("version").GetString()))
    Assert.All(clients, fun client -> Assert.Equal("GS2-08.9-retain-or-retire", client.GetProperty("disposition").GetString()))
    Assert.Equal("artifact-unavailable", clients[0].GetProperty("status").GetString())
    Assert.Equal("bypass-observed", clients[1].GetProperty("status").GetString())
    Assert.Equal(1, clients[1].GetProperty("providerMutationCount").GetInt32())
    Assert.Equal(22, blockers.RootElement.GetProperty("externalWriterSources").GetArrayLength())

[<Fact>]
let ``validator binds real producer evidence and refuses generic adapter substitution`` () =
    let validator = read "eng/validate-github-v1-independent-fence-attacks.fsx"
    Assert.Contains("producer-attack-result.json", validator)
    Assert.Contains("canonical producer execution digest differs", validator)
    Assert.DoesNotContain("V1EffectFenceAdapter.execute", validator)
    Assert.DoesNotContain("\"status\": \"pass\"", read "evidence/github-substrate-v2/gs2-08-6/attack-expectations.json", StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``producer result records complete unit execution and separately owned residuals`` () =
    use result = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-6/producer-attack-result.json")
    let value = result.RootElement
    let execution = value.GetProperty("executionEvidence")
    Assert.Equal(66, execution.GetProperty("compiledTests").GetInt32())
    Assert.Equal(0, execution.GetProperty("compiledFailures").GetInt32())
    Assert.Equal(16, execution.GetProperty("writerCallsites").GetInt32())
    Assert.Equal(6, execution.GetProperty("executedBoundaries").GetInt32())
    Assert.Equal(22, execution.GetProperty("externalWriterResiduals").GetInt32())
    Assert.Equal(1, execution.GetProperty("legacyBypasses").GetInt32())
    Assert.Equal("unclaimed", execution.GetProperty("q4").GetString())
    Assert.Equal("unit-exit-evidence-complete", value.GetProperty("disposition").GetString())

[<Fact>]
let ``GS2-08-6 native receipt accepts the attack result and no successor operation`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.6.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.6")
    let value = receipt.RootElement
    Assert.Equal("accepted", value.GetProperty("state").GetString())
    Assert.Equal("bc881a6ed1b4ce32e99d4c30a664b1db686280d3", value.GetProperty("sourceRevision").GetString())
    Assert.Equal(unitValue.GetProperty("contractSha256").GetString(), value.GetProperty("unitContractSha256").GetString())
    let names = value.GetProperty("artifacts").EnumerateArray() |> Seq.map (fun artifact -> artifact.GetProperty("name").GetString()) |> Set.ofSeq
    Assert.Contains("producer-execution-evidence", names)
    Assert.Contains("producer-legacy-probe", names)
    Assert.DoesNotContain(names, fun name -> name.Contains("publication", StringComparison.OrdinalIgnoreCase) || name.Contains("activation", StringComparison.OrdinalIgnoreCase))
