module FS.GG.Coordination.GitHubSealedHistoryTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubSealedHistoryQualification

let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private digest character = String.replicate 64 character
let private revision character = String.replicate 40 character
let private sourceBytes (value: string) = Encoding.UTF8.GetBytes value
let private record identity source outcome =
    let bytes = sourceBytes source
    { ArchiveIdentity=identity; SourceIdentity=$"source:{identity}"; SourceSchema="github-v1/1"
      SourceBytesBase64=Convert.ToBase64String bytes; SourceBytesSha256=(bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant())
      SourceValueSha256=sha $"value:{identity}"; ExpectedOutcome=outcome }
let private records =
    [ record "archive:1" "source one" (GitHubHistoryExpectedOutcome.Verified { OutputSchema="github-v2/1"; OutputSha256=sha "output:1" })
      record "archive:2" "source two" (GitHubHistoryExpectedOutcome.Rejected { Code="LEGACY-INVALID"; Reason="invalid legacy value"; EvidenceSha256=sha "evidence:2" }) ]
let private lookups = records |> List.map (fun value -> { LookupKey=value.SourceIdentity; ArchiveIdentity=value.ArchiveIdentity; RecordSha256=recordSha256 value })
let private fingerprint name = { Name=name; Version="1.0.0"; Sha256=sha name; Bytes=64L }
let private qualifyBaseline () =
    qualify "history-gs2-09-5-fixture" (revision "a") (digest "b") (digest "c") (digest "d")
        (digest "e") (digest "f") (digest "1") (digest "2") (digest "3") (digest "4")
        (fingerprint "archive-verifier") { Artifact=fingerprint "v2-production-closure"; V1UpcasterCount=0; ArchiveVerifierOnly=true; LookupReadOnly=true }
        records lookups (DateTimeOffset.Parse "2026-09-23T04:00:00Z")
let private get = function Ok value -> value | Error findings -> failwithf "unexpected refusal: %A" findings
let private refusal = function Error findings -> findings | Ok _ -> failwith "invalid history qualified"

[<Fact>]
let ``sealed history preserves exact bytes and deterministically replays`` () =
    let first = qualifyBaseline () |> get
    Assert.Equal(first, qualifyBaseline () |> get)
    Assert.Equal(Ok first, verify records first.Seal first)

[<Fact>]
let ``changed bytes missing records and reordered records refuse`` () =
    let baseline = qualifyBaseline () |> get
    let changed = { baseline.Records.Head with SourceBytesBase64=Convert.ToBase64String(sourceBytes "changed") }
    Assert.Contains(GitHubSealedHistoryFinding.InvalidSourceBytes "archive:1", verify records baseline.Seal { baseline with Records=changed::baseline.Records.Tail } |> refusal)
    Assert.Contains(GitHubSealedHistoryFinding.InvalidHistoryPopulation, verify records baseline.Seal { baseline with Records=baseline.Records.Tail } |> refusal)
    Assert.Contains(GitHubSealedHistoryFinding.InvalidHistoryPopulation, verify records baseline.Seal { baseline with Records=List.rev baseline.Records } |> refusal)

[<Fact>]
let ``malformed outcome and repointed lookup refuse`` () =
    let baseline = qualifyBaseline () |> get
    let bad = { baseline.Records.Head with ExpectedOutcome=GitHubHistoryExpectedOutcome.Verified { OutputSchema=""; OutputSha256="bad" } }
    Assert.Contains(GitHubSealedHistoryFinding.InvalidExpectedOutcome "archive:1", verify records baseline.Seal { baseline with Records=bad::baseline.Records.Tail } |> refusal)
    let repointed = { baseline.LookupIndex.Head with ArchiveIdentity="archive:2" }
    Assert.Contains(GitHubSealedHistoryFinding.InvalidLookupIndex, verify records baseline.Seal { baseline with LookupIndex=repointed::baseline.LookupIndex.Tail } |> refusal)

[<Fact>]
let ``production v1 upcaster and digest tampering refuse`` () =
    let baseline = qualifyBaseline () |> get
    Assert.Contains(GitHubSealedHistoryFinding.InvalidProductionClosure, verify records baseline.Seal { baseline with ProductionClosure={ baseline.ProductionClosure with V1UpcasterCount=1 } } |> refusal)
    Assert.Equal(Error [ GitHubSealedHistoryFinding.AlteredArchiveDigest ], verify records baseline.Seal { baseline with ArchiveDigest=digest "9" })
    Assert.Equal(Error [ GitHubSealedHistoryFinding.AlteredHistorySeal ], verify records (digest "8") baseline)

[<Fact>]
let ``sealed history controls require complete green independent inventories`` () =
    let passing: GitHubSealedHistoryControlResult list = requiredControls |> List.map (fun control -> { Control=control; ControlPassed=true; BaselineGreen=true })
    Assert.Equal(Ok(), validateControls passing passing)
    Assert.True(validateControls passing passing.Tail |> Result.isError)
