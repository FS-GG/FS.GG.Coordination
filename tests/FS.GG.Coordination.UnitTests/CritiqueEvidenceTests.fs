module FS.GG.Coordination.CritiqueEvidenceTests

open System
open System.Text
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private digest character = String(character, 64)
let private completed minute = DateTimeOffset(2026, 8, 31, 0, minute, 0, TimeSpan.Zero)

let private finding id perspective phase character =
    { Id = id
      Perspective = perspective
      PhaseId = phase
      Author = "accountable-owner"
      Decision = CritiqueDecision.Passed
      ContentSha256 = digest character
      CompletedAt = completed 5 }

let private validInput () =
    { Candidate =
        { CommitSha = String('a', 40)
          TreeSha256 = digest 'b'
          UnitContractSha256 = digest 'c' }
      Evidence =
        [ { Id = "architecture-tests"; Sha256 = digest 'd' }
          { Id = "evidence-storage"; Sha256 = digest 'e' } ]
      AccountableOwner = "accountable-owner"
      Findings =
        [ finding "finding-architecture" CritiquePerspective.Architecture "phase-architecture" '1'
          finding "finding-security" CritiquePerspective.Security "phase-security" '2'
          finding "finding-adapter" CritiquePerspective.Adapter "phase-adapter" '3'
          finding "finding-migration" CritiquePerspective.Migration "phase-migration" '4'
          finding "finding-cutover" CritiquePerspective.Cutover "phase-cutover" '5' ]
      CreatedAt = completed 6 }

let private generated input =
    match CritiqueEvidence.generate input with
    | Ok bytes -> bytes
    | Error findings -> failwithf "%A" findings

let private validate input bytes =
    CritiqueEvidence.validate input (ReadOnlyMemory<byte>(bytes))

let private mutate (bytes: byte array) action =
    let root = JsonNode.Parse(ReadOnlySpan<byte>(bytes)).AsObject()
    action root
    Encoding.UTF8.GetBytes(root.ToJsonString() + "\n")

[<Fact>]
let ``five perspectives generate one canonical derived green rollup`` () =
    let input = validInput ()
    let first = generated input
    let reordered = generated { input with Evidence = List.rev input.Evidence; Findings = List.rev input.Findings }
    Assert.Equal<byte>(first, reordered)
    match validate input first with
    | Error findings -> Assert.Fail(sprintf "%A" findings)
    | Ok summary ->
        Assert.Equal("passed", summary.Outcome)
        Assert.Equal(64, summary.CandidateFingerprintSha256.Length)
        Assert.Equal(64, summary.EvidenceSetSha256.Length)
        Assert.Equal(64, summary.FindingSetSha256.Length)
        Assert.Equal(64, summary.Digest.Length)
    let text = Encoding.UTF8.GetString first
    Assert.Contains("\"acceptanceAuthority\":\"accountable-owner-only\"", text)
    Assert.Contains("\"derivation\":\"all-required-bound-green/1\"", text)
    Assert.DoesNotContain("reviewer quorum", text)

[<Fact>]
let ``changes required remains valid evidence but cannot derive green`` () =
    let input = validInput ()
    let red = { input.Findings.Head with Decision = CritiqueDecision.ChangesRequired }
    let changed = { input with Findings = red :: input.Findings.Tail }
    let bytes = generated changed
    match validate changed bytes with
    | Error findings -> Assert.Fail(sprintf "%A" findings)
    | Ok summary -> Assert.Equal("changes-required", summary.Outcome)
    let text = Encoding.UTF8.GetString bytes
    Assert.Contains("\"outcome\":\"changes-required\"", text)
    Assert.DoesNotContain("\"outcome\":\"accepted\"", text)

[<Fact>]
let ``invalid authority inventory phase evidence and time are refused before rendering`` () =
    let baseline = validInput ()
    let cases =
        [ "CE-PERSPECTIVE-INVENTORY", { baseline with Findings = baseline.Findings.Tail }
          "CE-PHASE-IDENTITY", { baseline with Findings = baseline.Findings |> List.map (fun item -> { item with PhaseId = "same-phase" }) }
          "CE-AUTHORITY", { baseline with Findings = { baseline.Findings.Head with Author = "another-authority" } :: baseline.Findings.Tail }
          "CE-EVIDENCE-EMPTY", { baseline with Evidence = [] }
          "CE-TIME-ORDER", { baseline with Findings = { baseline.Findings.Head with CompletedAt = completed 7 } :: baseline.Findings.Tail } ]
    for expected, input in cases do
        match CritiqueEvidence.generate input with
        | Ok _ -> Assert.Fail($"%s{expected} unexpectedly rendered")
        | Error findings -> Assert.Contains(expected, findings |> List.map _.Code)

[<Fact>]
let ``absent stale duplicate forged truncated and prose only bundles are red`` () =
    let input = validInput ()
    let baseline = generated input
    let cases =
        [ "absent-finding", fun (root: JsonObject) -> root["findings"].AsArray().RemoveAt(0)
          "duplicate-finding", fun root -> root["findings"].AsArray().Add((root["findings"].AsArray()[0]).DeepClone())
          "stale-candidate", fun root -> root["candidate"].AsObject()["commitSha"] <- String('9', 40)
          "substituted-evidence", fun root ->
              let item = (root["evidence"].AsArray()[0]).AsObject()
              item["sha256"] <- digest '9'
          "forged-finding", fun root ->
              let item = (root["findings"].AsArray()[0]).AsObject()
              item["contentSha256"] <- digest '8'
          "prose-only", fun root -> root["findings"] <- JsonArray(JsonValue.Create("looks good"))
          "truncated", fun root -> root.Remove("rollup") |> ignore ]
    for name, mutation in cases do
        let changed = mutate baseline mutation
        match validate input changed with
        | Ok _ -> Assert.Fail($"%s{name} unexpectedly validated")
        | Error findings -> Assert.Contains("CE-BUNDLE-MISMATCH", findings |> List.map _.Code)
    let redFinding = { input.Findings.Head with Decision = CritiqueDecision.ChangesRequired }
    let redInput = { input with Findings = redFinding :: input.Findings.Tail }
    let assertedGreen =
        mutate (generated redInput) (fun root -> root["rollup"].AsObject()["outcome"] <- "passed")
    match validate redInput assertedGreen with
    | Ok _ -> Assert.Fail("asserted green unexpectedly validated")
    | Error findings -> Assert.Contains("CE-BUNDLE-MISMATCH", findings |> List.map _.Code)

[<Fact>]
let ``candidate evidence and finding content change bundle identity`` () =
    let input = validInput ()
    let baseline = generated input
    let variants =
        [ { input with Candidate = { input.Candidate with TreeSha256 = digest '9' } }
          { input with Evidence = { input.Evidence.Head with Sha256 = digest '9' } :: input.Evidence.Tail }
          { input with Findings = { input.Findings.Head with ContentSha256 = digest '9' } :: input.Findings.Tail } ]
    for variant in variants do Assert.NotEqual<byte>(baseline, generated variant)
