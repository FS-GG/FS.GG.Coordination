module FS.GG.Coordination.HarnessMutationProofArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private inventoryPath = Path.Combine(root, "evidence/github-substrate-v2/qualification-inventories/GS2-03.1.json")
let private baselinePath = Path.Combine(root, "evidence/github-substrate-v2/qualification-manifests/GS2-03.1.json")
let private digest character = String(character, 64)

let private context () =
    let validatorBoundary =
        [ "src/FS.GG.Coordination.Qualification.Contracts/HarnessMutationProof.fs"
          "src/FS.GG.Coordination.Qualification.Contracts/QualificationManifest.fs" ]
        |> List.map (fun path ->
            let bytes = File.ReadAllBytes(Path.Combine(root, path))
            let fileDigest = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
            $"%s{path}:%s{fileDigest}")
        |> String.concat "\n"
        |> fun value -> SHA256.HashData(Encoding.UTF8.GetBytes(value + "\n")) |> Convert.ToHexString |> _.ToLowerInvariant()
    { CandidateCommit = String('1', 40)
      CandidateTreeSha256 = digest '2'
      UnitContractSha256 = "acb013dd87697c21886dca39fa9ca97ff48e24402e964000cdc1d4c4645be40b"
      ValidatorSha256 = validatorBoundary }

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

[<Fact>]
let ``accepted GS2-03.9 proof reproduces from exact protected evidence`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in
        [ "fsi"
          "work/119-gs2-03-9-mutation-proof/acceptance/validate.fsx" ] do
        startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Contains("GS2_03_9_ACCEPTANCE_OK candidate=53f0338dea988fd79b95092286709df7c0fb4745", output)
    Assert.Contains("proof=4585fb2f68700dd8d8f0a470a55591fc0d5b6e8a31d2936ff2388fe655204060", output)
    Assert.Contains("validatorBoundary=22afe424bd4578e987d6c39b6beb52d58b933e491798508cefb4648e96ff3894", output)
    Assert.Equal("", error)
