module FS.GG.Coordination.GitHubSealedHistoryArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubSealedHistoryQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))
let private path relative = Path.Combine(root, relative)
let private sha256 (bytes: byte array) = SHA256.HashData(bytes) |> System.Convert.ToHexString |> _.ToLowerInvariant()
let private gateCommandSha256 (command: JsonElement) =
    command.GetProperty("executable").GetString() :: (command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    |> String.concat "\u0000" |> Encoding.UTF8.GetBytes |> sha256

[<Fact>]
let ``GS2-09-5 seals every required history control without production upcasters`` () =
    Assert.Equal(15, requiredControls.Length)
    let documentation = File.ReadAllText(path "docs/architecture/github-sealed-history.md")
    Assert.Contains("`V1UpcasterCount = 0`", documentation)
    for subject in [ "source bytes"; "expected outcome"; "lookup index"; "verifier" ] do Assert.Contains(subject, documentation)

[<Fact>]
let ``GS2-09-5 implementation has no provider or upcaster surface`` () =
    let source = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubSealedHistoryQualification.fs")
    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE "; "upcastV1" ] do Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-5 controlled evidence binds accepted live operations and sealed outputs`` () =
    use contract = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-5/contract.json"))
    let value = contract.RootElement
    Assert.Equal("GS2-09.5", value.GetProperty("unit").GetString())
    Assert.Equal("controlled-source-qualification", value.GetProperty("providerState").GetString())
    Assert.False(value.GetProperty("archivePublished").GetBoolean())
    Assert.Equal(0, value.GetProperty("productionV1Upcasters").GetInt32())
    Assert.Equal("88771aa83f88da07ec3eee618b7f11414dae4a23fb7b766f66255df60af5d3e0", value.GetProperty("archiveDigest").GetString())
    Assert.Equal("bf02922de1f502c6de5db1edb2ab9ba82728612d3b0d8076b58f8b8dd5b6c7a2", value.GetProperty("lookupDigest").GetString())
    let predecessor = value.GetProperty("predecessor")
    let receiptBytes = File.ReadAllBytes(path (predecessor.GetProperty("path").GetString()))
    Assert.Equal(sha256 receiptBytes, predecessor.GetProperty("fileSha256").GetString())
    use receipt = JsonDocument.Parse(receiptBytes)
    Assert.Equal("GS2-09.4", receipt.RootElement.GetProperty("unitId").GetString())
    Assert.Equal(receipt.RootElement.GetProperty("digest").GetString(), predecessor.GetProperty("receiptDigest").GetString())

[<Fact>]
let ``GS2-09-5 gate identities bind the registered literal commands`` () =
    use gates = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-gates.json"))
    use index = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-units.json"))
    let unitValue = index.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("id").GetString() = "GS2-09.5")
    let contracts = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList
    let commands = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList
    for contract in contracts do
        let command = commands |> List.find (fun item -> item.GetProperty("id").GetString() = contract.GetProperty("id").GetString())
        Assert.Equal(command.GetProperty("qGate").GetString(), contract.GetProperty("qGate").GetString())
        Assert.Equal(gateCommandSha256 command, contract.GetProperty("commandSha256").GetString())

[<Fact>]
let ``formal base parity uses supported reproducibility options and preserves failure diagnostics`` () =
    let verifier = File.ReadAllText(path "eng/verify-choreo-c5-parity.py")
    Assert.DoesNotContain("--backend=tlc', '--seed", verifier)
    Assert.Contains("--seed=0xC5F1", verifier)
    Assert.Contains("check=False, capture_output=True", verifier)
    Assert.Contains("print(diagnostic, file=sys.stderr)", verifier)

[<Fact>]
let ``optimistic architecture provisioning reuses the pinned evaluator without a network fetch`` () =
    let provisioner = File.ReadAllText(path "eng/bootstrap-gates/provision-quint.sh")
    Assert.Contains("home/.quint/rust-evaluator-v0.6.0/quint_evaluator", provisioner)
    Assert.Contains("$evaluator_sha", provisioner)
    Assert.Contains("QUINT_HOME=\"$quint_home\"", provisioner)
