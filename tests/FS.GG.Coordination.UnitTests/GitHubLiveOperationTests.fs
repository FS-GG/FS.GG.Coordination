module FS.GG.Coordination.GitHubLiveOperationTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubLiveOperationQualification

let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private revision character = String.replicate 40 character
let private digest character = String.replicate 64 character

let private obligations =
    requiredFamilies |> List.map (fun family -> { OperationIdentity = $"operation:{familyId family}"; Family = family })

let private decisions =
    obligations
    |> List.mapi (fun index obligation ->
        let globalId = $"GLOBAL_{index:D2}"
        let disposition =
            match index % 4 with
            | 0 -> GitHubLiveOperationDisposition.Drain { CompletionReceiptSha256 = sha $"receipt:{index}"; DrainFence = $"fence:{index}" }
            | 1 -> GitHubLiveOperationDisposition.Migrate { TargetOperationIdentity = $"v2:{index}"; GlobalId = globalId; TargetSchema = "github-v2-operation/1"; PayloadSha256 = sha $"payload:{index}"; MappingSha256 = sha $"mapping:{index}" }
            | 2 -> GitHubLiveOperationDisposition.Park { ParkingIdentity = $"park:{index}"; ResumeCondition = $"after-open-v2:{index}"; PayloadSha256 = sha $"park-payload:{index}"; EvidenceSha256 = sha $"park-evidence:{index}" }
            | _ -> GitHubLiveOperationDisposition.Invalid { Code = "INVALID-STALE-AUTHORITY"; Reason = "authority epoch is no longer live"; EvidenceSha256 = sha $"invalid:{index}" }
        { OperationIdentity = obligation.OperationIdentity; GlobalId = globalId; Family = obligation.Family; SourceState = "pending"
          SourceBytesSha256 = sha $"source:{index}"; DependencySetSha256 = sha $"dependencies:{index}"; Disposition = disposition })

let private qualifyBaseline () =
    qualify "live-operation-gs2-09-4-fixture" (revision "a") (digest "b") (digest "c") (digest "d")
        (digest "e") (digest "f") (digest "1") (digest "2")
        { Name = "github-v2-live-operation-planner"; Version = "1.0.0"; Sha256 = sha "planner"; Bytes = 42L }
        obligations decisions (DateTimeOffset.Parse "2026-09-23T02:00:00Z")

let private get = function Ok value -> value | Error findings -> failwithf "unexpected refusal: %A" findings
let private refusal = function Error findings -> findings | Ok _ -> failwith "invalid live-operation plan qualified"

[<Fact>]
let ``all live operation families qualify and deterministically replay`` () =
    let first = qualifyBaseline () |> get
    Assert.Equal(first, qualifyBaseline () |> get)
    Assert.Equal(6, requiredFamilies.Length)
    Assert.Equal(Ok first, verify obligations first.Seal first)

[<Fact>]
let ``missing duplicate and reordered decisions refuse`` () =
    let baseline = qualifyBaseline () |> get
    Assert.Contains(GitHubLiveOperationFinding.InvalidDecisionPopulation, verify obligations baseline.Seal { baseline with Decisions = baseline.Decisions.Tail } |> refusal)
    Assert.Contains(GitHubLiveOperationFinding.InvalidDecisionPopulation, verify obligations baseline.Seal { baseline with Decisions = baseline.Decisions.Head :: baseline.Decisions } |> refusal)
    Assert.Contains(GitHubLiveOperationFinding.InvalidObligationPopulation, verify obligations baseline.Seal { baseline with Obligations = List.rev baseline.Obligations; Decisions = List.rev baseline.Decisions } |> refusal)

[<Fact>]
let ``malformed dispositions and migrated identity mismatch refuse`` () =
    let baseline = qualifyBaseline () |> get
    let badDrain = { baseline.Decisions[0] with Disposition = GitHubLiveOperationDisposition.Drain { CompletionReceiptSha256 = "bad"; DrainFence = "" } }
    Assert.Contains(GitHubLiveOperationFinding.InvalidDrainDisposition "operation:claim:claim", verify obligations baseline.Seal { baseline with Decisions = baseline.Decisions |> List.updateAt 0 badDrain } |> refusal)
    let migrated = baseline.Decisions[1]
    let badMigrate =
        match migrated.Disposition with
        | GitHubLiveOperationDisposition.Migrate value -> { migrated with Disposition = GitHubLiveOperationDisposition.Migrate { value with GlobalId = "DIFFERENT" } }
        | _ -> failwith "fixture differs"
    Assert.Contains(GitHubLiveOperationFinding.InvalidMigrateDisposition "operation:cutover-adjacent:cutover-adjacent", verify obligations baseline.Seal { baseline with Decisions = baseline.Decisions |> List.updateAt 1 badMigrate } |> refusal)
    let badPark = { baseline.Decisions[2] with Disposition = GitHubLiveOperationDisposition.Park { ParkingIdentity = ""; ResumeCondition = ""; PayloadSha256 = digest "1"; EvidenceSha256 = digest "2" } }
    Assert.Contains(GitHubLiveOperationFinding.InvalidParkDisposition "operation:delivery:delivery", verify obligations baseline.Seal { baseline with Decisions = baseline.Decisions |> List.updateAt 2 badPark } |> refusal)
    let badInvalid = { baseline.Decisions[3] with Disposition = GitHubLiveOperationDisposition.Invalid { Code = ""; Reason = ""; EvidenceSha256 = digest "3" } }
    Assert.Contains(GitHubLiveOperationFinding.InvalidExplicitInvalidDisposition "operation:queued-write:queued-write", verify obligations baseline.Seal { baseline with Decisions = baseline.Decisions |> List.updateAt 3 badInvalid } |> refusal)

[<Fact>]
let ``altered decision and seal refuse`` () =
    let baseline = qualifyBaseline () |> get
    let changed = { baseline.Decisions.Head with SourceState = "changed" }
    Assert.Equal(Error [ GitHubLiveOperationFinding.AlteredLiveOperationDigest ], verify obligations baseline.Seal { baseline with Decisions = changed :: baseline.Decisions.Tail })
    Assert.Equal(Error [ GitHubLiveOperationFinding.AlteredLiveOperationSeal ], verify obligations (digest "9") baseline)

[<Fact>]
let ``live operation controls require complete green independent inventories`` () =
    let passing: GitHubLiveOperationControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok(), validateControls passing passing)
    Assert.True(validateControls passing passing.Tail |> Result.isError)
