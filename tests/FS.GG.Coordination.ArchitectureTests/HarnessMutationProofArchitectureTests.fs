module FS.GG.Coordination.HarnessMutationProofArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private inventoryPath = Path.Combine(root, "evidence/github-substrate-v2/qualification-inventories/GS2-03.1.json")
let private baselinePath = Path.Combine(root, "evidence/github-substrate-v2/qualification-manifests/GS2-03.1.json")
let private digest character = String(character, 64)

let private context () =
    let validator = File.ReadAllBytes(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/QualificationManifest.fs"))
    { CandidateCommit = String('1', 40)
      CandidateTreeSha256 = digest '2'
      UnitContractSha256 = "acb013dd87697c21886dca39fa9ca97ff48e24402e964000cdc1d4c4645be40b"
      ValidatorSha256 = SHA256.HashData validator |> Convert.ToHexString |> _.ToLowerInvariant() }

let private generate () =
    let inventory = File.ReadAllBytes inventoryPath
    let baseline = File.ReadAllBytes baselinePath
    let value = context ()
    let proof =
        HarnessMutationProof.generate value (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(baseline))
        |> Result.defaultWith (failwithf "%A")
    value, inventory, baseline, proof

[<Fact>]
let ``retained qualification boundary produces the closed mutation matrix`` () =
    let value, inventory, baseline, proof = generate ()
    Assert.True(HarnessMutationProof.validate value (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(baseline)) (ReadOnlyMemory<byte>(proof)) |> Result.isOk)
    use document = JsonDocument.Parse proof
    let rootElement = document.RootElement
    Assert.Equal(10, rootElement.GetProperty("gateClasses").GetArrayLength())
    Assert.Equal(6, rootElement.GetProperty("mutationKinds").GetArrayLength())
    Assert.Equal(10, rootElement.GetProperty("controls").GetArrayLength())
    Assert.Equal(60, rootElement.GetProperty("observations").GetArrayLength())
    let observations = rootElement.GetProperty("observations").EnumerateArray() |> Seq.toList
    Assert.All(observations, fun item -> Assert.Equal("rejected", item.GetProperty("outcome").GetString()))
    let forged = observations |> List.filter (fun item -> item.GetProperty("mutationKind").GetString() = "forged")
    Assert.Equal(10, forged.Length)
    Assert.All(forged, fun item -> Assert.Contains(item.GetProperty("diagnostics").EnumerateArray(), fun code -> code.GetString() = "HMP-FORGED-FINGERPRINT"))

[<Fact>]
let ``proof coverage candidate and independent inputs cannot be asserted or substituted`` () =
    let value, inventory, baseline, proof = generate ()
    let mutations =
        [ "missing-observation", fun (node: JsonObject) -> node["observations"].AsArray().RemoveAt(0)
          "asserted-green", fun node -> node["observations"].AsArray().[0].AsObject()["outcome"] <- "passed"
          "duplicate-control", fun node -> node["controls"].AsArray().Add(node["controls"].AsArray().[0].DeepClone())
          "forged-digest", fun node -> node["digest"] <- digest '9' ]
    for name, mutate in mutations do
        let node = JsonNode.Parse(proof).AsObject()
        mutate node
        let bytes = Text.Encoding.UTF8.GetBytes(node.ToJsonString() + "\n")
        match HarnessMutationProof.validate value (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(baseline)) (ReadOnlyMemory<byte>(bytes)) with
        | Ok _ -> Assert.Fail($"%s{name} unexpectedly validated")
        | Error findings -> Assert.Contains(findings, fun item -> item.Code = "HMP-PROOF-MISMATCH")
    let staleContext = { value with CandidateCommit = String('9', 40) }
    Assert.True(HarnessMutationProof.validate staleContext (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(baseline)) (ReadOnlyMemory<byte>(proof)) |> Result.isError)
    let changedBaseline = Array.copy baseline
    changedBaseline[0] <- byte ' '
    Assert.True(HarnessMutationProof.validate value (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(changedBaseline)) (ReadOnlyMemory<byte>(proof)) |> Result.isError)
