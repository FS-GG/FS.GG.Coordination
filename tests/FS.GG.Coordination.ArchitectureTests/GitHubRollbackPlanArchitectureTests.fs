module FS.GG.Coordination.GitHubRollbackPlanArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackPlanQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))
let private path relative = Path.Combine(root, relative)
let private sha256 (bytes: byte array) = SHA256.HashData(bytes) |> System.Convert.ToHexString |> _.ToLowerInvariant()
let private gateCommandSha256 (command: JsonElement) =
    command.GetProperty("executable").GetString() :: (command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    |> String.concat "\u0000" |> Encoding.UTF8.GetBytes |> sha256

[<Fact>]
let ``GS2-09-6 covers every restoration domain and resumable receipt control`` () =
    Assert.Equal(5, requiredDomains.Length)
    Assert.Equal(17, requiredControls.Length)
    let documentation = File.ReadAllText(path "docs/architecture/github-rollback-plans.md")
    for subject in [ "settings"; "receiver pins"; "v1 projections"; "schedules"; "authority snapshot"; "`VerifiedV2`"; "receipt" ] do
        Assert.Contains(subject, documentation)

[<Fact>]
let ``GS2-09-6 implementation is source qualification without provider or execution surface`` () =
    let source = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubRollbackPlanQualification.fs")
    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE "; "Process.Start" ] do
        Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-6 evidence binds accepted sealed history and deterministic plan`` () =
    use contract = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-6/contract.json"))
    let value = contract.RootElement
    Assert.Equal("GS2-09.6", value.GetProperty("unit").GetString())
    Assert.Equal("VerifiedV2", value.GetProperty("startEpoch").GetString())
    Assert.Equal("OperatingV1", value.GetProperty("terminalEpoch").GetString())
    Assert.Equal("6e030d4f99da811fe94fd0e18d6454774226651c55b4793b9d0d34b8397707c1", value.GetProperty("rollbackNormalizedDigest").GetString())
    Assert.Equal("e475b68ab2b65e1df253a0cfa007714a03f0c1bee65786d806857ed018fa676f", value.GetProperty("rollbackSeal").GetString())
    Assert.False(value.GetProperty("rollbackExecuted").GetBoolean())
    Assert.False(value.GetProperty("providerMutation").GetBoolean())
    let predecessor = value.GetProperty("predecessor")
    let receiptBytes = File.ReadAllBytes(path (predecessor.GetProperty("path").GetString()))
    Assert.Equal(sha256 receiptBytes, predecessor.GetProperty("fileSha256").GetString())
    use receipt = JsonDocument.Parse(receiptBytes)
    Assert.Equal("GS2-09.5", receipt.RootElement.GetProperty("unitId").GetString())
    Assert.Equal(receipt.RootElement.GetProperty("digest").GetString(), predecessor.GetProperty("receiptDigest").GetString())

[<Fact>]
let ``GS2-09-6 gate identities bind the registered literal commands`` () =
    use gates = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-gates.json"))
    use index = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-units.json"))
    let unitValue = index.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("id").GetString() = "GS2-09.6")
    let contracts = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList
    let commands = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList
    for contract in contracts do
        let command = commands |> List.find (fun item -> item.GetProperty("id").GetString() = contract.GetProperty("id").GetString())
        Assert.Equal(command.GetProperty("qGate").GetString(), contract.GetProperty("qGate").GetString())
        Assert.Equal(gateCommandSha256 command, contract.GetProperty("commandSha256").GetString())
