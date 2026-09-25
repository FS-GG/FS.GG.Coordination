module FS.GG.Coordination.GitHubLedgerProtectionArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private read path =
    File.ReadAllText(Path.Combine(root, path))

let private sha256Text (value: string) =
    value
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

[<Fact>]
let ``ledger protection surface is deterministic and has no apply or provider path`` () =
    let text =
        read "src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fsi"
        + read "src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fs"

    for required in
        [
            "ApplyAuthorized"
            "DedicatedWriterApp"
            "AdditionalWritePermissions"
            "PreviousObservationEvidenceSha256"
            "ProviderEnvelopeSha256"
            "PagesComplete"
            "ShardedJournalAdapter.address Cutover"
            "FS-GG/FS.GG.Coordination.Authority"
            "1351660651"
            "refs/heads/fsgg/v2/journal/**/*"
            "refs/tags/fsgg/v2/fleet-cutover/**/*"
        ] do
        Assert.Contains(required, text)

    for forbidden in
        [
            "HttpClient"
            "api.github.com"
            "GITHUB_TOKEN"
            "GetEnvironmentVariable"
            "let apply"
            "val apply"
        ] do
        Assert.DoesNotContain(forbidden, text)

[<Fact>]
let ``historical sharded journal implementation remains unmodified`` () =
    Assert.Equal(
        "f0b6a5854c208c4cc88cccf003516d80d781ae5c4140ae9c123dc82942a17e09",
        sha256Text (read "src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs")
    )

    Assert.Equal(
        "790f6b3d6d787a0e613f4eacd97dceee79cdca17518a365d5c2f981df8f1136b",
        sha256Text (read "src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fsi")
    )

[<Fact>]
let ``GS2-08-2 registration binds accepted predecessor roadmap and exact Q3 Q4 and Q6 commands`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let roadmap = units.RootElement.GetProperty("roadmap")
    Assert.Equal("15de6f92e501a4416e782c0d9c111351d697c3a1", roadmap.GetProperty("revision").GetString())

    Assert.Equal(
        "9e9e91383d81b9aa49fb8324a6a621bfc318e41ce5e4b3bb909c18d1e7f403d8",
        roadmap.GetProperty("sha256").GetString()
    )

    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun x -> x.GetProperty("id").GetString() = "GS2-08.2")

    Assert.Equal<string list>(
        [ "GS2-08.1" ],
        unitValue.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let contracts =
        unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    let commands =
        contracts
        |> List.map (fun contract ->
            gates.RootElement.GetProperty("commands").EnumerateArray()
            |> Seq.find (fun command -> command.GetProperty("id").GetString() = contract.GetProperty("id").GetString()))

    Assert.Equal<string list>(
        [ "Q3"; "Q4"; "Q6" ],
        commands |> List.map (fun command -> command.GetProperty("qGate").GetString())
    )

    for command, contract in List.zip commands contracts do
        let components =
            seq {
                command.GetProperty("executable").GetString()
                yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
            }

        Assert.Equal(
            contract.GetProperty("commandSha256").GetString(),
            components |> String.concat "\u0000" |> sha256Text
        )

    use receipt =
        JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.1.json")

    Assert.Equal(
        "49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31",
        receipt.RootElement.GetProperty("digest").GetString()
    )
