module FS.GG.Coordination.GitHubLiveOperationArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubLiveOperationQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))
let private path relative = Path.Combine(root, relative)
let private sha256 (value: byte array) = SHA256.HashData(value) |> fun digest -> System.Convert.ToHexString(digest).ToLowerInvariant()

let private gateCommandSha256 (command: JsonElement) =
    let values =
        command.GetProperty("executable").GetString()
        :: (command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    values |> String.concat "\u0000" |> Encoding.UTF8.GetBytes |> sha256

[<Fact>]
let ``GS2-09-4 has the exact roadmap live operation family population`` () =
    Assert.Equal<GitHubLiveOperationFamily list>(
        [ GitHubLiveOperationFamily.Claim; GitHubLiveOperationFamily.CutoverAdjacent
          GitHubLiveOperationFamily.Delivery; GitHubLiveOperationFamily.QueuedWrite
          GitHubLiveOperationFamily.Release; GitHubLiveOperationFamily.Review ], requiredFamilies)
    let documentation = File.ReadAllText(path "docs/architecture/github-live-operation-handling.md")
    requiredFamilies |> List.iter (fun family -> Assert.Contains($"`{familyId family}`", documentation))
    Assert.Equal(14, requiredControls.Length)

[<Fact>]
let ``GS2-09-4 implementation has no provider mutation surface`` () =
    let source = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubLiveOperationQualification.fs")
    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE " ] do Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-4 controlled evidence binds accepted typed transforms and refuses execution claims`` () =
    use contract = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-4/contract.json"))
    let value = contract.RootElement
    Assert.Equal("GS2-09.4", value.GetProperty("unit").GetString())
    Assert.Equal("controlled-source-qualification", value.GetProperty("providerState").GetString())
    Assert.False(value.GetProperty("liveOperationsExecuted").GetBoolean())
    Assert.False(value.GetProperty("acceptanceReceiptCreated").GetBoolean())
    Assert.Equal("aec2736dd8c6194e9682b3f54ffa840c620185c0bb329334feb667a09fd1b41a", value.GetProperty("transformNormalizedDigest").GetString())
    Assert.Equal("8b54160ae1bf62734ec524df064f0fe54de58a17755a2a509ae8e5014b961b59", value.GetProperty("transformSeal").GetString())
    Assert.Equal("9f5f1b5b29c2abf58c0fed6cdb34eb840e786615dd80a234ecde1b1373947dfe", value.GetProperty("liveOperationNormalizedDigest").GetString())
    Assert.Equal("d4b949ccae34c39daf091b96c66b0324a137b072d1e93f4bfc4e07911453eb7c", value.GetProperty("liveOperationSeal").GetString())
    let predecessor = value.GetProperty("predecessor")
    let receiptBytes = File.ReadAllBytes(path (predecessor.GetProperty("path").GetString()))
    Assert.Equal(sha256 receiptBytes, predecessor.GetProperty("fileSha256").GetString())
    use receipt = JsonDocument.Parse(receiptBytes)
    Assert.Equal("GS2-09.3", receipt.RootElement.GetProperty("unitId").GetString())
    Assert.Equal("accepted", receipt.RootElement.GetProperty("state").GetString())
    Assert.Equal(receipt.RootElement.GetProperty("digest").GetString(), predecessor.GetProperty("receiptDigest").GetString())

[<Fact>]
let ``GS2-09-4 gate identities bind the registered literal commands`` () =
    use gates = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-gates.json"))
    use index = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-units.json"))
    let unitValue = index.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("id").GetString() = "GS2-09.4")
    let contracts = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList
    let commands = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList
    for contract in contracts do
        let command = commands |> List.find (fun item -> item.GetProperty("id").GetString() = contract.GetProperty("id").GetString())
        Assert.Equal(command.GetProperty("qGate").GetString(), contract.GetProperty("qGate").GetString())
        Assert.Equal(gateCommandSha256 command, contract.GetProperty("commandSha256").GetString())
