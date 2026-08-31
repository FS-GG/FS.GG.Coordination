module FS.GG.Coordination.CritiqueEvidenceArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private digest character = String(character, 64)
let private time minute = DateTimeOffset(2026, 8, 31, 0, minute, 0, TimeSpan.Zero)

let private input () =
    let finding id perspective phase character =
        { Id = id
          Perspective = perspective
          PhaseId = phase
          Author = "accountable-delivery-owner"
          Decision = CritiqueDecision.Passed
          ContentSha256 = digest character
          CompletedAt = time 5 }
    { Candidate =
        { CommitSha = String('a', 40)
          TreeSha256 = digest 'b'
          UnitContractSha256 = "c38b9ed4a473a99ee1bb6b43dddcafc06c153e6f1b698fbb44f45af018f52920" }
      Evidence =
        [ { Id = "architecture-tests"; Sha256 = digest 'd' }
          { Id = "evidence-storage-contract"; Sha256 = digest 'e' } ]
      AccountableOwner = "accountable-delivery-owner"
      Findings =
        [ finding "architecture-finding" CritiquePerspective.Architecture "phase-architecture" '1'
          finding "security-finding" CritiquePerspective.Security "phase-security" '2'
          finding "adapter-finding" CritiquePerspective.Adapter "phase-adapter" '3'
          finding "migration-finding" CritiquePerspective.Migration "phase-migration" '4'
          finding "cutover-finding" CritiquePerspective.Cutover "phase-cutover" '5' ]
      CreatedAt = time 6 }

[<Fact>]
let ``critique bundle binds five phases to one owner candidate and evidence set`` () =
    let expected = input ()
    let bytes =
        match CritiqueEvidence.generate expected with
        | Ok value -> value
        | Error findings -> failwithf "%A" findings
    let summary =
        match CritiqueEvidence.validate expected (ReadOnlyMemory<byte>(bytes)) with
        | Ok value -> value
        | Error findings -> failwithf "%A" findings
    Assert.Equal("passed", summary.Outcome)
    use document = JsonDocument.Parse bytes
    let bundle = document.RootElement
    Assert.Equal(CritiqueEvidence.Schema, bundle.GetProperty("schema").GetString())
    Assert.Equal("accountable-delivery-owner", bundle.GetProperty("accountableOwner").GetString())
    let candidateFingerprint = bundle.GetProperty("candidate").GetProperty("fingerprintSha256").GetString()
    let evidenceSet = bundle.GetProperty("evidenceSetSha256").GetString()
    let findings = bundle.GetProperty("findings").EnumerateArray() |> Seq.toList
    Assert.Equal(5, findings.Length)
    Assert.Equal<string list>(
        [ "adapter"; "architecture"; "cutover"; "migration"; "security" ],
        findings |> List.map (fun item -> item.GetProperty("perspective").GetString())
    )
    Assert.Equal(5, findings |> List.map (fun item -> item.GetProperty("phaseId").GetString()) |> List.distinct |> List.length)
    for finding in findings do
        Assert.Equal("accountable-delivery-owner", finding.GetProperty("author").GetString())
        Assert.Equal(candidateFingerprint, finding.GetProperty("candidateFingerprintSha256").GetString())
        Assert.Equal(evidenceSet, finding.GetProperty("evidenceSetSha256").GetString())
        Assert.Equal("passed", finding.GetProperty("decision").GetString())
    let rollup = bundle.GetProperty("rollup")
    Assert.Equal("accountable-owner-only", rollup.GetProperty("acceptanceAuthority").GetString())
    Assert.Equal("all-required-bound-green/1", rollup.GetProperty("derivation").GetString())
    Assert.Equal("passed", rollup.GetProperty("outcome").GetString())

[<Fact>]
let ``review schema evolves additively and storage policy executes v2`` () =
    let v1 = Path.Combine(root, "evidence/github-substrate-v2/schemas/v1/reviews.schema.json")
    let v2 = Path.Combine(root, "evidence/github-substrate-v2/schemas/v2/reviews.schema.json")
    Assert.True(File.Exists v1)
    Assert.True(File.Exists v2)
    let v1Digest = SHA256.HashData(File.ReadAllBytes v1) |> Convert.ToHexString |> _.ToLowerInvariant()
    Assert.Equal("d3e7fc7b2055fba2db1e48d99139fcdeabc8f58f426e34f139fd6e46f11d921a", v1Digest)
    use policy = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/storage-policy.json")))
    let reviews =
        policy.RootElement.GetProperty("categories").EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("name").GetString() = "reviews")
    Assert.Equal("schemas/v2/reviews.schema.json", reviews.GetProperty("schema").GetString())
    use schema = JsonDocument.Parse(File.ReadAllBytes v2)
    Assert.Equal("https://fs-gg.github.io/schemas/evidence/reviews/v2", schema.RootElement.GetProperty("$id").GetString())
    let findings = schema.RootElement.GetProperty("properties").GetProperty("findings")
    Assert.Equal(5, findings.GetProperty("minItems").GetInt32())
    Assert.Equal(5, findings.GetProperty("maxItems").GetInt32())
    Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean())

[<Fact>]
let ``critique architecture documents evidence authority and frozen diagnostics boundaries`` () =
    let text = File.ReadAllText(Path.Combine(root, "docs/architecture/critique-evidence.md"))
    for required in
        [ "not five people"
          "accountable-owner-only"
          "valid critique evidence"
          "reviews/v1"
          "schemas/v2/reviews.schema.json"
          "frozen corpus keeps its stronger provenance-specific validator"
          "Neither layer consults GitHub approval counts" ] do
        Assert.Contains(required, text)

[<Fact>]
let ``accepted GS2-03.8 bundle reproduces from exact protected evidence`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in
        [ "fsi"
          "work/115-gs2-03-8-critique-evidence/acceptance/validate.fsx" ] do
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
    Assert.Contains("CRITIQUE_ACCEPTANCE_OK candidate=2427478b6fffba470e86ff46cf2ca22106a11a6d", output)
    Assert.Contains("evidenceSet=48f16c2af28b9de96f4bb2f01bf4968ff3cfdc1f44e76a5bc55ef40e1fe714ce", output)
    Assert.Contains("findingSet=d5bfc82512b82ec522ee7cf2798fc603da664c9c1a36dfbcf2dc8e1a311bf438", output)
    Assert.Equal("", error)
