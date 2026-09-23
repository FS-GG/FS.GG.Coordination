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
