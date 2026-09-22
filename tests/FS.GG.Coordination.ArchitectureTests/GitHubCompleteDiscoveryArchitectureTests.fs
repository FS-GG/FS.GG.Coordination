module FS.GG.Coordination.GitHubCompleteDiscoveryArchitectureTests

open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubCompleteDiscoveryQualification

let private root = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "../../../../.."))

let private bytes path = File.ReadAllBytes(Path.Combine(root, path))

let private sha256 (value: byte array) =
    SHA256.HashData(value) |> fun digest -> System.Convert.ToHexString(digest).ToLowerInvariant()

[<Fact>]
let ``GS2-09-1 authority population is closed and roadmap-derived`` () =
    Assert.Equal<string list>(
        [
            "issues-open-and-relevant-closed"
            "project-items"
            "project-fields"
            "hierarchy-and-dependencies"
            "claim-and-event-streams"
            "review-delivery-release-records"
            "repository-settings"
            "workflow-pins"
            "receiver-identities"
        ],
        expectedAuthorities
    )

    let roadmap = File.ReadAllText(Path.Combine(root, "docs/architecture/github-complete-discovery.md"))
    expectedAuthorities |> List.iter (fun authority -> Assert.Contains($"`{authority}`", roadmap))

[<Fact>]
let ``GS2-09-1 has no production mutation implementation`` () =
    let source =
        File.ReadAllText(
            Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubCompleteDiscoveryQualification.fs")
        )

    for forbidden in [ "HttpClient"; "Octokit"; "mutation {"; "POST "; "PATCH "; "DELETE " ] do
        Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``GS2-09-1 protected acceptance binds the merged discovery qualification`` () =
    let receiptBytes = bytes "evidence/github-substrate-v2/accepted/GS2-09.1.json"
    use receipt = JsonDocument.Parse(receiptBytes)
    let value = receipt.RootElement

    Assert.Equal("accepted", value.GetProperty("state").GetString())
    Assert.Equal("49f4f7ad3a1f616a771645570e317f98bfd1a676", value.GetProperty("sourceRevision").GetString())
    Assert.Equal("7110789f6b224da1c707c8cb2277b9751037d724c79655fb1c4660fd8191972c", value.GetProperty("unitContractSha256").GetString())
    Assert.Equal("9cb3eb947ea4637ddb8452c9fdecd781d46101187e35958620884f4348651613", value.GetProperty("digest").GetString())
    Assert.True(AcceptanceReceiptDigest.verify (System.ReadOnlyMemory receiptBytes) "GS2-09.1" (value.GetProperty("digest").GetString()) value |> Result.isOk)

    let artifacts =
        value.GetProperty("artifacts").EnumerateArray()
        |> Seq.map (fun artifact -> artifact.GetProperty("name").GetString(), artifact.GetProperty("sha256").GetString())
        |> Map.ofSeq

    Assert.Equal("d355fe928c567e8a8f06a25c79e1a8e53e84486551878712837b7660cab40dee", artifacts["protected-acceptance"])
    Assert.Equal("b7cb73218c80b5b2b6536e648542a2afbaefc672897aeb510cdc67fa33767f27", artifacts["complete-discovery-contract"])

    use protectedAcceptance = JsonDocument.Parse(bytes "evidence/github-substrate-v2/gs2-09-1/protected-acceptance.json")
    let protectedValue = protectedAcceptance.RootElement
    Assert.Equal("49f4f7ad3a1f616a771645570e317f98bfd1a676", protectedValue.GetProperty("source").GetProperty("merge").GetString())
    Assert.Equal("10d655b9d771aa689d2f305ab68439cd3d9ef592", protectedValue.GetProperty("source").GetProperty("tree").GetString())
    Assert.Equal("success", protectedValue.GetProperty("hosted").GetProperty("protectedMerge").GetProperty("conclusion").GetString())
    Assert.Equal(35781025813L, protectedValue.GetProperty("hosted").GetProperty("protectedMerge").GetProperty("optimisticValidationRun").GetInt64())
    Assert.False(protectedValue.GetProperty("claims").GetProperty("liveCompleteReads").GetBoolean())
    Assert.False(protectedValue.GetProperty("claims").GetProperty("providerMutation").GetBoolean())

    use index = JsonDocument.Parse(bytes "evidence/github-substrate-v2/index.json")
    let entry =
        index.RootElement.GetProperty("entries").EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("id").GetString() = "accepted-GS2-09.1")

    Assert.Equal(receiptBytes.Length, entry.GetProperty("bytes").GetInt32())
    Assert.Equal(sha256 receiptBytes, entry.GetProperty("sha256").GetString())
