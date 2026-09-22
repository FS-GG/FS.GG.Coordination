module FS.GG.Coordination.GitHubImmutableManifestArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubImmutableManifestQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))
let private path relative = Path.Combine(root, relative)

let private sha256 (value: byte array) =
    SHA256.HashData(value) |> fun digest -> System.Convert.ToHexString(digest).ToLowerInvariant()

[<Fact>]
let ``GS2-09-2 documentation binds every immutable manifest family`` () =
    let documentation = File.ReadAllText(path "docs/architecture/github-immutable-manifest.md")

    for family in
        [
            "old and new model fingerprints"
            "artifact fingerprints"
            "global IDs"
            "old bytes and values"
            "v2 results"
            "live operations"
            "receiver heads"
            "settings plans"
            "archive digests"
            "dispositions"
            "phase plans"
            "reviewers"
            "rollback inputs"
        ] do
        Assert.Contains(family, documentation)

    Assert.Equal(19, requiredControls.Length)

[<Fact>]
let ``GS2-09-2 implementation has no provider mutation surface`` () =
    let source =
        File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubImmutableManifestQualification.fs")

    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE " ] do
        Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-2 controlled evidence binds the accepted discovery receipt`` () =
    use contract =
        JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-2/contract.json"))

    let value = contract.RootElement
    Assert.Equal("GS2-09.2", value.GetProperty("unit").GetString())
    Assert.Equal("controlled-source-qualification", value.GetProperty("providerState").GetString())
    Assert.False(value.GetProperty("liveManifestCreated").GetBoolean())
    Assert.False(value.GetProperty("acceptanceReceiptCreated").GetBoolean())

    let predecessor = value.GetProperty("predecessor")
    let receiptBytes = File.ReadAllBytes(path (predecessor.GetProperty("path").GetString()))
    Assert.Equal(sha256 receiptBytes, predecessor.GetProperty("fileSha256").GetString())

    use receipt = JsonDocument.Parse(receiptBytes)
    Assert.Equal("GS2-09.1", receipt.RootElement.GetProperty("unitId").GetString())
    Assert.Equal("accepted", receipt.RootElement.GetProperty("state").GetString())
    Assert.Equal(
        receipt.RootElement.GetProperty("digest").GetString(),
        predecessor.GetProperty("receiptDigest").GetString()
    )

[<Fact>]
let ``GS2-09-2 protected acceptance binds the merged immutable manifest`` () =
    let receiptBytes = File.ReadAllBytes(path "evidence/github-substrate-v2/accepted/GS2-09.2.json")
    use receipt = JsonDocument.Parse(receiptBytes)
    let value = receipt.RootElement

    Assert.Equal("accepted", value.GetProperty("state").GetString())
    Assert.Equal("1ec51937bf898ec4ad6facb2aaf1528a98ca0432", value.GetProperty("sourceRevision").GetString())
    Assert.Equal("09ba8dd5283ee6012fafc00ab17b20c5aae52480f4bc60485605b0c9f84b5fd0", value.GetProperty("unitContractSha256").GetString())
    Assert.Equal("c0fb9c28cbcee5d87812a0844da4a65bcf2077a9559224065a603273a87de85d", value.GetProperty("digest").GetString())
    Assert.True(AcceptanceReceiptDigest.verify (System.ReadOnlyMemory receiptBytes) "GS2-09.2" (value.GetProperty("digest").GetString()) value |> Result.isOk)

    let artifacts =
        value.GetProperty("artifacts").EnumerateArray()
        |> Seq.map (fun artifact -> artifact.GetProperty("name").GetString(), artifact.GetProperty("sha256").GetString())
        |> Map.ofSeq

    Assert.Equal("f055ac669d558c807dcc9dfb777676d5a14284a6fd502938d7ef6e2c691f484c", artifacts["protected-acceptance"])
    Assert.Equal("ab9bce120d1a87a4bc97cbd3378da00c2a8d2e756d1d42a30c775601c436ca0c", artifacts["immutable-manifest-contract"])

    use protectedAcceptance =
        JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/gs2-09-2/protected-acceptance.json"))

    let protectedValue = protectedAcceptance.RootElement
    Assert.Equal("1ec51937bf898ec4ad6facb2aaf1528a98ca0432", protectedValue.GetProperty("source").GetProperty("merge").GetString())
    Assert.Equal("ea7a587923a9a07834f1945d074fe5076a111285", protectedValue.GetProperty("source").GetProperty("tree").GetString())
    Assert.Equal("success", protectedValue.GetProperty("hosted").GetProperty("protectedMerge").GetProperty("conclusion").GetString())
    Assert.Equal(35792794541L, protectedValue.GetProperty("hosted").GetProperty("protectedMerge").GetProperty("optimisticValidationRun").GetInt64())
    Assert.False(protectedValue.GetProperty("claims").GetProperty("liveManifestCreated").GetBoolean())
    Assert.False(protectedValue.GetProperty("claims").GetProperty("providerMutation").GetBoolean())

    use index = JsonDocument.Parse(File.ReadAllBytes(path "evidence/github-substrate-v2/index.json"))
    let entry =
        index.RootElement.GetProperty("entries").EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("id").GetString() = "accepted-GS2-09.2")

    Assert.Equal(receiptBytes.Length, entry.GetProperty("bytes").GetInt32())
    Assert.Equal(sha256 receiptBytes, entry.GetProperty("sha256").GetString())
