module FS.GG.Coordination.GitHubImmutableManifestTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubImmutableManifestQualification

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private revision character = String.replicate 40 character
let private digest character = String.replicate 64 character

let private discovered =
    [ "issues-open-and-relevant-closed:1"; "project-items:1"; "receiver-identities:1" ]

let private fingerprint name version (content: string) =
    { Name = name; Version = version; Sha256 = sha content; Bytes = int64 (Encoding.UTF8.GetByteCount content) }

let private subjects =
    discovered
    |> List.mapi (fun index identity ->
        {
            Old =
                {
                    Identity = identity
                    GlobalId = $"OLD_{index}"
                    SourceRevision = $"source-{index}"
                    Schema = "github-v1/1"
                    BytesSha256 = sha $"old-bytes-{index}"
                    ValueSha256 = sha $"old-value-{index}"
                }
            Result =
                {
                    Identity = $"v2:{identity}"
                    GlobalId = $"NEW_{index}"
                    Revision = $"result-{index}"
                    PayloadSha256 = sha $"result-{index}"
                    Outcome = "planned"
                }
            Disposition = "pending-typed-transform"
        })

let private rollbackInputs =
    [
        { Identity = "rollback-receivers"; Kind = "receiver-heads"; Revision = revision "1"; PayloadSha256 = sha "receivers" }
        { Identity = "rollback-settings"; Kind = "repository-settings"; Revision = revision "2"; PayloadSha256 = sha "settings" }
    ]

let private qualifyBaseline () =
    qualify
        "manifest-gs2-09-2-fixture"
        (revision "a")
        (digest "b")
        (digest "c")
        (digest "d")
        (revision "e")
        (digest "f")
        (digest "1")
        discovered
        (fingerprint "github-v1-model" "1" "old-model")
        (fingerprint "github-v2-model" "2" "new-model")
        [ fingerprint "migration-cli" "0.90.0" "cli"; fingerprint "verifier" "1.0.0" "verifier" ]
        subjects
        [ { Identity = "operation:1"; Generation = 3L; Kind = "queued-write"; Target = "FS-GG/repo#1"; State = "observed"; PayloadSha256 = sha "operation" } ]
        [ { Receiver = "FS-GG/.github"; RepositoryId = "R_1"; CommitSha = revision "3"; TreeSha = revision "4"; PinsSha256 = sha "pins" } ]
        [ { Repository = "FS-GG/.github"; PrestateSha256 = sha "prestate"; DesiredSha256 = sha "desired"; PlanSha256 = sha "settings-plan" } ]
        (GitHubCompleteDiscoveryQualification.expectedAuthorities
         |> List.map (fun authority ->
             {
                 Authority = authority
                 SourceSchema = "github-v1/1"
                 SourceBytesSha256 = sha $"source:{authority}"
                 ArchiveSha256 = sha $"archive:{authority}"
                 LookupIndexSha256 = sha $"lookup:{authority}"
                 VerifierSha256 = sha $"verifier:{authority}"
             }))
        [
            { Order = 1; PhaseId = "phase-01-prepare"; InputSha256 = sha "phase-1-input"; OperationsSha256 = sha "phase-1-ops"; ReceiptSha256 = sha "phase-1-receipt"; RollbackInputIds = [ "rollback-receivers" ] }
            { Order = 2; PhaseId = "phase-02-apply"; InputSha256 = sha "phase-2-input"; OperationsSha256 = sha "phase-2-ops"; ReceiptSha256 = sha "phase-2-receipt"; RollbackInputIds = [ "rollback-receivers"; "rollback-settings" ] }
        ]
        [
            { Login = "reviewer-a"; GlobalId = "U_A"; DecisionSha256 = sha "review-a" }
            { Login = "reviewer-b"; GlobalId = "U_B"; DecisionSha256 = sha "review-b" }
        ]
        rollbackInputs
        (DateTimeOffset.Parse "2026-09-22T22:00:00Z")

let private get =
    function
    | Ok value -> value
    | Error findings -> failwithf "unexpected immutable-manifest refusal: %A" findings

let private refusal =
    function
    | Error findings -> findings
    | Ok _ -> failwith "invalid immutable manifest qualified"

[<Fact>]
let ``complete immutable manifest qualifies and deterministically replays`` () =
    let first = qualifyBaseline () |> get
    let second = qualifyBaseline () |> get

    Assert.Equal(first, second)
    Assert.Equal(1, first.SchemaVersion)
    Assert.Equal(Ok first, verify discovered first.Seal first)

[<Fact>]
let ``omitted discovered subject and duplicate global id refuse`` () =
    let baseline = qualifyBaseline () |> get
    let omitted = { baseline with Subjects = baseline.Subjects.Tail }
    Assert.Contains(GitHubImmutableManifestFinding.InvalidManifestPopulation "subjects", verify discovered baseline.Seal omitted |> refusal)

    let duplicate =
        { baseline with
            Subjects =
                baseline.Subjects
                |> List.mapi (fun index subject ->
                    if index = 1 then { subject with Old = { subject.Old with GlobalId = baseline.Subjects.Head.Old.GlobalId } }
                    else subject)
        }

    Assert.Contains(GitHubImmutableManifestFinding.InvalidManifestPopulation "globalIds", verify discovered baseline.Seal duplicate |> refusal)

[<Fact>]
let ``fresh unknown subject refuses an otherwise sealed manifest`` () =
    let baseline = qualifyBaseline () |> get
    let freshDiscovery = discovered @ [ "workflow-pins:unexpected" ] |> List.sort

    Assert.Equal(Ok baseline, verify discovered baseline.Seal baseline)
    Assert.Contains(
        GitHubImmutableManifestFinding.InvalidManifestPopulation "subjects",
        verify freshDiscovery baseline.Seal baseline |> refusal
    )

[<Fact>]
let ``exact manifest requalification is stable and changes no planned result`` () =
    let first = qualifyBaseline () |> get
    let replay = qualifyBaseline () |> get

    Assert.Equal(first.Seal, replay.Seal)
    Assert.Equal(first.NormalizedDigest, replay.NormalizedDigest)
    Assert.True(first.Subjects = replay.Subjects)
    Assert.True(first.PhasePlans = replay.PhasePlans)

[<Fact>]
let ``reordered artifacts and incomplete archives refuse`` () =
    let baseline = qualifyBaseline () |> get
    let reordered = { baseline with ArtifactFingerprints = List.rev baseline.ArtifactFingerprints }
    Assert.Contains(GitHubImmutableManifestFinding.InvalidManifestPopulation "artifactFingerprints", verify discovered baseline.Seal reordered |> refusal)

    let incomplete = { baseline with Archives = baseline.Archives.Tail }
    Assert.Contains(GitHubImmutableManifestFinding.InvalidManifestPopulation "archives", verify discovered baseline.Seal incomplete |> refusal)

[<Fact>]
let ``phase gap and unknown rollback input refuse`` () =
    let baseline = qualifyBaseline () |> get
    let phase = baseline.PhasePlans.Head

    let invalid =
        { baseline with
            PhasePlans =
                { phase with Order = 2; RollbackInputIds = [ "rollback-unknown" ] }
                :: baseline.PhasePlans.Tail
        }

    let findings = verify discovered baseline.Seal invalid |> refusal
    Assert.Contains(GitHubImmutableManifestFinding.InvalidManifestPopulation "phasePlans", findings)
    Assert.Contains(GitHubImmutableManifestFinding.MissingManifestRollbackInput "rollback-unknown", findings)

[<Fact>]
let ``changed bound value and changed seal refuse replay`` () =
    let baseline = qualifyBaseline () |> get
    let receiver = baseline.ReceiverHeads.Head
    let changed = { baseline with ReceiverHeads = [ { receiver with PinsSha256 = sha "changed" } ] }
    Assert.Equal(Error [ GitHubImmutableManifestFinding.AlteredManifestDigest ], verify discovered baseline.Seal changed)
    Assert.Equal(Error [ GitHubImmutableManifestFinding.AlteredManifestSeal ], verify discovered (digest "9") baseline)

[<Fact>]
let ``manifest controls require both complete green inventories`` () =
    let passing: GitHubImmutableManifestControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })

    Assert.Equal(Ok(), validateControls passing passing)
    Assert.True(validateControls passing passing.Tail |> Result.isError)
