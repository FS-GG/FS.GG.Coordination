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
let ``GS2-08-6 registration binds pending Q3 and Q6 producer attack contracts`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.6")
    Assert.Equal<string list>([ "GS2-08.3"; "GS2-08.5" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "Q3"; "Q6" ], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.DoesNotContain("Q4", unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString())
    Assert.Contains("acceptance remains blocked", unitValue.GetProperty("exitGate").GetString())
    Assert.Contains("claims no GS2-08.6 execution or acceptance", unitValue.GetProperty("exitGate").GetString())
    let commands = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.map (fun value -> value.GetProperty("id").GetString(), value) |> Map.ofSeq
    for contract in unitValue.GetProperty("gateContracts").EnumerateArray() do
        let id = contract.GetProperty("id").GetString()
        let command = commands[id]
        let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
        let actual = components |> String.concat "\u0000" |> Encoding.UTF8.GetBytes |> sha256Bytes
        Assert.Equal(contract.GetProperty("commandSha256").GetString(), actual)

[<Fact>]
let ``independent attack evidence binds closed and refreshed populations and blocks old clients`` () =
    use binding = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-6/source-binding.json")
    let value = binding.RootElement
    Assert.Equal(sha256 "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json", value.GetProperty("closedPopulationSha256").GetString())
    Assert.Equal("3355d86beb6df99a66cd8d19fefe24dc574252ab9bdef08ba576216cf2dbbee7", value.GetProperty("producerCensusSha256").GetString())
    Assert.Contains("no provider evidence", value.GetProperty("scope").GetString(), StringComparison.OrdinalIgnoreCase)
    use blockers = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-6/gs2-08-9-blockers.json")
    let clients = blockers.RootElement.GetProperty("clients").EnumerateArray() |> Seq.toList
    Assert.Equal<string list>([ "0.58.0"; "0.75.4" ], clients |> List.map (fun client -> client.GetProperty("version").GetString()))
    Assert.All(clients, fun client -> Assert.Equal("GS2-08.9-retain-or-retire", client.GetProperty("disposition").GetString()))

[<Fact>]
let ``registration validator refuses to substitute generic adapter pass rows for producer evidence`` () =
    let validator = read "eng/validate-github-v1-independent-fence-attacks.fsx"
    Assert.Contains("awaiting-exact-producer-result", validator)
    Assert.Contains("unvalidated producer result was attached", validator)
    Assert.DoesNotContain("V1EffectFenceAdapter.execute", validator)
    Assert.DoesNotContain("passed", read "evidence/github-substrate-v2/gs2-08-6/attack-expectations.json", StringComparison.OrdinalIgnoreCase)
