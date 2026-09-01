module FS.GG.Coordination.GitHubClaimTouchSetArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha256Text (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private gateCommandSha256 (command: JsonElement) =
    seq {
        command.GetProperty("executable").GetString()
        yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
    }
    |> String.concat "\u0000"
    |> sha256Text

[<Fact>]
let ``claim adapter is additive pure and composes canonical journal primitives`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/ClaimTouchSetAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/ClaimTouchSetAdapter.fs"
    Assert.Contains("type ClaimGrant", signature)
    Assert.Contains("val authorizeEffect", signature)
    for required in
        [ "ShardedJournalAdapter.address"
          "ShardedJournalAdapter.validate"
          "ShardedJournalAdapter.planCas"
          "ShardedJournalAdapter.reconcile"
          "ShardedJournalAdapter.authorizeEffect"
          "ShardedJournalAdapter.planSaga"
          "ShardedJournalAdapter.planConflict" ] do
        Assert.Contains(required, implementation)
    for forbidden in
        [ "HttpClient"
          "GITHUB_TOKEN"
          "GetEnvironmentVariable"
          "api.github.com"
          "ProjectStatusAuthorizes"
          "CommentOrderAuthorizes" ] do
        Assert.DoesNotContain(forbidden, implementation)

[<Fact>]
let ``claim authority and projection hints are structurally separate`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/ClaimTouchSetAdapter.fsi"
    let authorityStart = signature.IndexOf("type ClaimAuthorityObservation", StringComparison.Ordinal)
    let projectionStart = signature.IndexOf("type ClaimProjectionHints", StringComparison.Ordinal)
    let acquireStart = signature.IndexOf("type ClaimAcquireIntent", StringComparison.Ordinal)
    Assert.True(authorityStart >= 0 && projectionStart > authorityStart && acquireStart > projectionStart)
    let projectionBlock = signature.Substring(projectionStart, acquireStart - projectionStart)
    Assert.DoesNotContain("JournalObservation", projectionBlock)
    Assert.DoesNotContain("JournalCommit", projectionBlock)
    Assert.DoesNotContain("Generation", projectionBlock)

[<Fact>]
let ``canonical Quint protocol source remains byte-identical`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))

[<Fact>]
let ``GS2-05-5 registration binds accepted predecessor and exact gate`` () =
    use unitsDocument = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gatesDocument = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unit =
        unitsDocument.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-05.5")
    Assert.Equal<string list>([ "GS2-05.4" ], unit.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "github-claim-touch-set-contract" ], unit.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "Q3" ], unit.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command =
        gatesDocument.RootElement.GetProperty("commands").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-claim-touch-set-contract")
    let gateContract = unit.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    Assert.Equal(gateCommandSha256 command, gateContract.GetProperty("commandSha256").GetString())
    let exitGate = unit.GetProperty("exitGate").GetString()
    for required in
        [ "accepted GS2-05.4 receipt"
          "expected-parent CAS"
          "lease expiry grants only successor eligibility"
          "persists the complete touch set"
          "reverse-order fenced compensation"
          "without production writes"
          "successor-unit authority" ] do
        Assert.Contains(required, exitGate)

[<Fact>]
let ``claim qualification evidence has independent closed controls`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-5/corpus.json")
    use independent = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-05-5/independent-expectations.json")
    let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    let independentIds = independent.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(generatedIds, independentIds)
    Assert.Equal(18, generatedIds.Length)
    Assert.Equal(generatedIds.Length, generatedIds |> List.distinct |> List.length)
