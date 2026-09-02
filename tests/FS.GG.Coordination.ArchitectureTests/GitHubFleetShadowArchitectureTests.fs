module FS.GG.Coordination.GitHubFleetShadowArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha256Text (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``fleet shadow is a pure comparison surface with no apply path`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/FleetShadowAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/FleetShadowAdapter.fs"
    for required in [ "type FleetShadowObservation"; "type FleetShadowDecision"; "type FleetShadowDivergenceClass"; "val compare"; "val verify" ] do Assert.Contains(required, signature)
    for required in [ "requiredCapabilities"; "V1Defect"; "V2Defect"; "IntentionalVersionedChange"; "UnclassifiedFleetDivergence"; "FleetMutationAttempted"; "SHA256.HashData" ] do Assert.Contains(required, implementation)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "let apply"; "PATCH"; "POST"; "DELETE" ] do Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``fleet shadow preserves canonical Quint source`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))

[<Fact>]
let ``GS2-05-8 registration binds accepted predecessor and exact Q4 gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-05.8")
    Assert.Equal<string list>([ "GS2-05.7" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "github-fleet-shadow-contract" ], unitValue.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-fleet-shadow-contract")
    Assert.Equal("Q4", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-fleet-shadow.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(components |> String.concat "\u0000" |> sha256Text, unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString())
    let exitGate = unitValue.GetProperty("exitGate").GetString()
    for required in [ "accepted GS2-05.7 receipt"; "zero-item repositories"; "v1-defect"; "zero unexplained divergence"; "no mutation capability"; "180-item"; "no apply path" ] do Assert.Contains(required, exitGate)

[<Fact>]
let ``fleet shadow evidence covers the complete roster and closed control inventory`` () =
    use evidence = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-8-fleet-shadow.json")
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-8/corpus.json")
    use independent = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-8/independent-expectations.json")
    let observation = evidence.RootElement.GetProperty("observation")
    let report = evidence.RootElement.GetProperty("report")
    Assert.Equal(10, observation.GetProperty("roster").GetArrayLength())
    Assert.Equal(10, observation.GetProperty("repositories").GetArrayLength())
    Assert.Equal(180, report.GetProperty("itemCount").GetInt32())
    Assert.Equal(0, report.GetProperty("unexplainedDivergenceCount").GetInt32())
    Assert.False(observation.GetProperty("source").GetProperty("credentialsRetained").GetBoolean())
    Assert.Equal("value-independent", observation.GetProperty("source").GetProperty("independence").GetProperty("highestReached").GetString())
    Assert.Equal("fsgg-coord ready --all --json", observation.GetProperty("source").GetProperty("v1").GetProperty("command").GetString())
    Assert.Equal("gh api graphql --paginate --slurp", observation.GetProperty("source").GetProperty("v2").GetProperty("command").GetString())
    Assert.Equal(0, observation.GetProperty("mutationAttempts").GetArrayLength())
    let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    let independentIds = independent.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(generatedIds, independentIds)
    Assert.Equal(18, generatedIds.Length)
    Assert.Equal(generatedIds.Length, generatedIds |> List.distinct |> List.length)
