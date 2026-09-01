module FS.GG.Coordination.GitHubLifecycleProjectionArchitectureTests

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
let ``lifecycle projection composes derived facts through the existing Project adapter only`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/LifecycleProjectionAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/LifecycleProjectionAdapter.fs"
    for required in [ "type LifecycleIntent"; "type LifecycleFactOutcome"; "type LifecycleAuthority"; "type DerivedLifecycleStage"; "val derive"; "val plan"; "val authorize"; "val verify" ] do Assert.Contains(required, signature)
    for required in [ "ClaimJournalAuthority"; "ReviewJournalAuthority"; "DeliveryJournalAuthority"; "WrongLifecycleAuthority"; "ProjectAdapter.planStatus"; "ProjectAdapter.checkStatusPreState"; "ProjectAdapter.verifyStatusPostState"; "StatusNoOp" ] do Assert.Contains(required, implementation)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "SetSchedulingIntent" ] do Assert.DoesNotContain(forbidden, implementation)

[<Fact>]
let ``lifecycle projection preserves canonical Quint source`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))

[<Fact>]
let ``GS2-05-7 registration binds accepted predecessor and exact gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-05.7")
    Assert.Equal<string list>([ "GS2-05.6" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "github-lifecycle-projection-contract" ], unitValue.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-lifecycle-projection-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-lifecycle-projection.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(components |> String.concat "\u0000" |> sha256Text, unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString())
    let exitGate = unitValue.GetProperty("exitGate").GetString()
    for required in [ "accepted GS2-05.6 receipt"; "sole human or policy input"; "historical generations"; "zero-effect replay"; "without production writes" ] do Assert.Contains(required, exitGate)

[<Fact>]
let ``lifecycle projection evidence has independent closed controls`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-7/corpus.json")
    use independent = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-7/independent-expectations.json")
    let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    let independentIds = independent.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(generatedIds, independentIds)
    Assert.Equal(18, generatedIds.Length)
    Assert.Equal(generatedIds.Length, generatedIds |> List.distinct |> List.length)
