module FS.GG.Coordination.GitHubTypedTransformArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubTypedTransformQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))
let private path relative = Path.Combine(root, relative)

let private sha256 (value: byte array) =
    SHA256.HashData(value) |> fun digest -> System.Convert.ToHexString(digest).ToLowerInvariant()

[<Fact>]
let ``GS2-09-3 has the exact roadmap transform family population`` () =
    Assert.Equal<GitHubTransformFamily list>(
        [
            GitHubTransformFamily.Blockers
            GitHubTransformFamily.BodyMetadata
            GitHubTransformFamily.DesiredSettings
            GitHubTransformFamily.Hierarchy
            GitHubTransformFamily.LifecycleReceipts
            GitHubTransformFamily.PlanningFields
            GitHubTransformFamily.RepositoryScope
            GitHubTransformFamily.SchedulingHolds
            GitHubTransformFamily.Taxonomy
            GitHubTransformFamily.TouchSets
        ],
        requiredFamilies
    )

    let documentation = File.ReadAllText(path "docs/architecture/github-typed-transforms.md")
    requiredFamilies |> List.iter (fun family -> Assert.Contains($"`{familyId family}`", documentation))
    Assert.Equal(17, requiredControls.Length)

[<Fact>]
let ``GS2-09-3 implementation has no provider mutation surface`` () =
    let source =
        File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubTypedTransformQualification.fs")

    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE " ] do
        Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-3 controlled evidence binds the accepted immutable manifest`` () =
    use contract = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-3/contract.json"))
    let value = contract.RootElement
    Assert.Equal("GS2-09.3", value.GetProperty("unit").GetString())
    Assert.Equal("controlled-source-qualification", value.GetProperty("providerState").GetString())
    Assert.False(value.GetProperty("liveTransformsCreated").GetBoolean())
    Assert.False(value.GetProperty("acceptanceReceiptCreated").GetBoolean())

    let predecessor = value.GetProperty("predecessor")
    let receiptBytes = File.ReadAllBytes(path (predecessor.GetProperty("path").GetString()))
    Assert.Equal(sha256 receiptBytes, predecessor.GetProperty("fileSha256").GetString())

    use receipt = JsonDocument.Parse(receiptBytes)
    Assert.Equal("GS2-09.2", receipt.RootElement.GetProperty("unitId").GetString())
    Assert.Equal("accepted", receipt.RootElement.GetProperty("state").GetString())
    Assert.Equal(receipt.RootElement.GetProperty("digest").GetString(), predecessor.GetProperty("receiptDigest").GetString())

[<Fact>]
let ``GS2-09-3 protected acceptance binds the merged typed transforms`` () =
    let receiptBytes = File.ReadAllBytes(path "evidence/github-substrate-v2/accepted/GS2-09.3.json")
    use receipt = JsonDocument.Parse(receiptBytes)
    let value = receipt.RootElement

    Assert.Equal("accepted", value.GetProperty("state").GetString())
    Assert.Equal("ac98b04e50165855c71425f99c1477b6e1ef31ef", value.GetProperty("sourceRevision").GetString())
    Assert.Equal("5a2121bcd893ad5ca018d444edba1acf062fd6f2fffd56b8e63ff28e842d2a2a", value.GetProperty("unitContractSha256").GetString())
    Assert.Equal("b3634a5c4334e2a112307136755dbdb3577e76181b852a7782689c873196ba71", value.GetProperty("digest").GetString())
    Assert.True(AcceptanceReceiptDigest.verify (System.ReadOnlyMemory receiptBytes) "GS2-09.3" (value.GetProperty("digest").GetString()) value |> Result.isOk)

    let artifacts =
        value.GetProperty("artifacts").EnumerateArray()
        |> Seq.map (fun artifact -> artifact.GetProperty("name").GetString(), artifact.GetProperty("sha256").GetString())
        |> Map.ofSeq

    Assert.Equal("300d43f62f6028f686ffcca0186159224d47264be480eb8a9a73cb35cf95fb33", artifacts["protected-acceptance"])
    Assert.Equal("52b7829710502bea25e3ad974d0c2aacba1cec4aab8832db443554e411ebd406", artifacts["typed-transform-contract"])

    use protectedAcceptance =
        JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-3/protected-acceptance.json"))

    let protectedValue = protectedAcceptance.RootElement
    Assert.Equal("ac98b04e50165855c71425f99c1477b6e1ef31ef", protectedValue.GetProperty("source").GetProperty("merge").GetString())
    Assert.Equal("8a89e3705156c92254dc4d497b0c45ecad9ce9ce", protectedValue.GetProperty("source").GetProperty("tree").GetString())
    Assert.Equal("success", protectedValue.GetProperty("hosted").GetProperty("protectedMerge").GetProperty("conclusion").GetString())
    Assert.Equal(35801953346L, protectedValue.GetProperty("hosted").GetProperty("protectedMerge").GetProperty("optimisticValidationRun").GetInt64())
    Assert.False(protectedValue.GetProperty("claims").GetProperty("liveTransformsCreated").GetBoolean())
    Assert.False(protectedValue.GetProperty("claims").GetProperty("providerMutation").GetBoolean())

    use index = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/index.json"))
    let entry =
        index.RootElement.GetProperty("entries").EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("id").GetString() = "accepted-GS2-09.3")

    Assert.Equal(receiptBytes.Length, entry.GetProperty("bytes").GetInt32())
    Assert.Equal(sha256 receiptBytes, entry.GetProperty("sha256").GetString())
