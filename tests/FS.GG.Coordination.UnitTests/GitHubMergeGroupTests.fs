module FS.GG.Coordination.GitHubMergeGroupTests

open Xunit
open System
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubMergeGroupQualification

let private head = "1111111111111111111111111111111111111111"
let private baseSha = "2222222222222222222222222222222222222222"
let private digest c = String.replicate 64 c
let private checks = [ "architecture"; "unit" ]
let private facts () =
    { EventName = eventName; Repository = "FS-GG/FS.GG.Coordination"; MergeGroupId = "mg-315"; MergeGroupHeadSha = head
      ObservedBaseRepository = "FS-GG/FS.GG.Coordination"; CurrentBaseRepository = "FS-GG/FS.GG.Coordination"
      ObservedBaseRef = "refs/heads/main"; CurrentBaseRef = "refs/heads/main"
      ObservedBaseSha = baseSha; CurrentBaseSha = baseSha; BaseObservationRevision = 17L; CurrentBaseObservationRevision = 17L
      ObservedAtUnixSeconds = 1000L; FreshUntilUnixSeconds = 1100L; EvaluatedAtUnixSeconds = 1050L
      ExpectedRequiredChecks = checks; ObservedRequiredChecks = checks
      CheckResults = checks |> List.map (fun name -> { Name = name; EventName = "merge_group"; HeadSha = head; Conclusion = MergeGroupCheckConclusion.Success })
      ObservedClaimGeneration = 5561468721L; CurrentClaimGeneration = 5561468721L
      ObservedReviewDigest = digest "a"; CurrentReviewDigest = digest "a"
      ObservedCandidateHeadSha = head; CurrentCandidateHeadSha = head
      ObservedDependencyDigest = digest "b"; CurrentDependencyDigest = digest "b"
      ObservedReleaseObligationsMet = true; CurrentReleaseObligationsMet = true
      ObservedSettingsDigest = digest "c"; CurrentSettingsDigest = digest "c"
      HasConflictingGroup = false; AttemptsDirectMerge = false }
let private get = function Ok value -> value | Error errors -> failwithf "unexpected refusal: %A" errors
let private findings = function Error errors -> errors | Ok value -> failwithf "expected refusal: %A" value
let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
let private strings values = values |> List.map frame |> String.concat ""
let private conclusion = function MergeGroupCheckConclusion.Success -> "success" | MergeGroupCheckConclusion.Pending -> "pending" | MergeGroupCheckConclusion.Failure -> "failure"
let private reseal (plan: GitHubMergeGroupPlan) =
    let checkFrame (check: MergeGroupCheckFact) = strings [ check.Name; check.EventName; check.HeadSha; conclusion check.Conclusion ]
    let payload =
        [ "github-merge-group/v1"; plan.EventName; plan.Repository; plan.MergeGroupId; plan.MergeGroupHeadSha
          plan.BaseRepository; plan.BaseRef; plan.BaseSha; string plan.BaseObservationRevision
          string plan.ObservedAtUnixSeconds; string plan.FreshUntilUnixSeconds; string plan.EvaluatedAtUnixSeconds
          strings plan.RequiredChecks; strings (plan.CheckResults |> List.map checkFrame); string plan.ClaimGeneration
          plan.ReviewDigest; plan.CandidateHeadSha; plan.DependencyDigest; string plan.ReleaseObligationsMet
          plan.SettingsDigest; plan.Disposition ] |> strings
    let seal = payload |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    { plan with Seal = seal }

[<Fact>]
let ``fresh complete merge-group authority produces sealed decision`` () =
    let plan = facts () |> compile |> get
    Assert.Equal(disposition, plan.Disposition)
    Assert.Equal("refs/heads/main", plan.BaseRef)
    Assert.Equal(baseSha, plan.BaseSha)
    Assert.Equal(17L, plan.BaseObservationRevision)
    Assert.Equal<string list>(checks, plan.RequiredChecks)
    Assert.Equal<MergeGroupCheckFact list>((facts ()).CheckResults, plan.CheckResults)

[<Fact>]
let ``base identity is fresh and race-safe`` () =
    let baseline = facts ()
    Assert.Contains(GitHubMergeGroupFinding.BaseObservationStale 1100L, compile { baseline with EvaluatedAtUnixSeconds = 1101L } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.BaseChanged(baseSha, "3333333333333333333333333333333333333333"), compile { baseline with CurrentBaseSha = "3333333333333333333333333333333333333333" } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.BaseRevisionChanged(17L, 18L), compile { baseline with CurrentBaseObservationRevision = 18L } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.BaseRepositoryChanged("FS-GG/FS.GG.Coordination", "FS-GG/Other"), compile { baseline with CurrentBaseRepository = "FS-GG/Other" } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.BaseRefChanged("refs/heads/main", "refs/heads/release"), compile { baseline with CurrentBaseRef = "refs/heads/release" } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.MalformedField "currentBaseRef", compile { baseline with CurrentBaseRef = "main" } |> findings)

[<Fact>]
let ``all required checks must run successfully for exact merge-group head`` () =
    let baseline = facts ()
    Assert.Contains(GitHubMergeGroupFinding.IncompleteCheckInventory, compile { baseline with CheckResults = baseline.CheckResults.Tail } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.IncompleteCheckInventory, compile { baseline with ObservedRequiredChecks = [ "architecture" ] } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.NonCanonicalCheckInventory, compile { baseline with ExpectedRequiredChecks = List.rev checks; ObservedRequiredChecks = List.rev checks; CheckResults = List.rev baseline.CheckResults } |> findings)
    let alter change = { baseline with CheckResults = change baseline.CheckResults.Head :: baseline.CheckResults.Tail } |> compile |> findings
    Assert.Contains(GitHubMergeGroupFinding.CheckDidNotRunForMergeGroup "architecture", alter (fun row -> { row with EventName = "pull_request" }))
    Assert.Contains(GitHubMergeGroupFinding.CheckHeadMismatch "architecture", alter (fun row -> { row with HeadSha = baseSha }))
    Assert.Contains(GitHubMergeGroupFinding.CheckPending "architecture", alter (fun row -> { row with Conclusion = MergeGroupCheckConclusion.Pending }))
    Assert.Contains(GitHubMergeGroupFinding.CheckFailed "architecture", alter (fun row -> { row with Conclusion = MergeGroupCheckConclusion.Failure }))

[<Fact>]
let ``every temporal authority is re-evaluated fail closed`` () =
    let baseline = facts ()
    Assert.Contains(GitHubMergeGroupFinding.ClaimChanged(5561468721L, 5561468722L), compile { baseline with CurrentClaimGeneration = 5561468722L } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.ReviewChanged, compile { baseline with CurrentReviewDigest = digest "d" } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.CandidateHeadChanged, compile { baseline with CurrentCandidateHeadSha = baseSha } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.DependencyChanged, compile { baseline with CurrentDependencyDigest = digest "d" } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.ReleaseObligationsUnmet, compile { baseline with CurrentReleaseObligationsMet = false } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.SettingsChanged, compile { baseline with CurrentSettingsDigest = digest "d" } |> findings)

[<Fact>]
let ``event conflict and direct merge paths refuse`` () =
    let baseline = facts ()
    Assert.Contains(GitHubMergeGroupFinding.UnknownEvent "pull_request:closed", compile { baseline with EventName = "pull_request:closed" } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.ConflictingGroup, compile { baseline with HasConflictingGroup = true } |> findings)
    Assert.Contains(GitHubMergeGroupFinding.DirectMergeAttempt, compile { baseline with AttemptsDirectMerge = true } |> findings)

[<Fact>]
let ``canonical bytes seal and replay are exact`` () =
    let baseline = facts ()
    let plan = compile baseline |> get
    let bytes = serialize plan
    Assert.Equal(Ok plan, parse bytes)
    Assert.Equal(Error [ GitHubMergeGroupFinding.InvalidSerialization "non-canonical bytes" ], parse (bytes + " "))
    Assert.Equal(Error [ GitHubMergeGroupFinding.AlteredSeal ], verify (digest "0") plan)
    Assert.Equal(bytes, serialize (replay plan baseline |> get))
    Assert.Equal(Error [ GitHubMergeGroupFinding.ReplayConflict ], replay plan { baseline with EvaluatedAtUnixSeconds = 1051L })

[<Fact>]
let ``public verification rejects correctly resealed malformed plans`` () =
    let plan = facts () |> compile |> get
    let rejects expected changed =
        let malformed = reseal changed
        Assert.Contains(expected, verify malformed.Seal malformed |> findings)
        Assert.Contains(expected, parse (serialize malformed) |> findings)
    rejects (GitHubMergeGroupFinding.MissingField "repository") { plan with Repository = "" }
    rejects (GitHubMergeGroupFinding.MalformedField "baseRepositoryScope") { plan with BaseRepository = "FS-GG/Other" }
    rejects (GitHubMergeGroupFinding.MalformedField "baseRef") { plan with BaseRef = "refs/pull/315/head" }
    rejects (GitHubMergeGroupFinding.MalformedField "baseFreshness") { plan with ObservedAtUnixSeconds = 1101L; FreshUntilUnixSeconds = 1100L }
    rejects GitHubMergeGroupFinding.IncompleteCheckInventory { plan with CheckResults = plan.CheckResults.Tail }

[<Fact>]
let ``control inventory mutation is detected`` () =
    let green: GitHubMergeGroupControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), validateControls green green)
    Assert.Contains("generated control inventory differs", validateControls green.Tail green |> findings)
    Assert.Contains("independent control failed", validateControls green ({ green.Head with ControlPassed = false } :: green.Tail) |> findings)
