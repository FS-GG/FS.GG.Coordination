#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubMergeGroupQualification

let root = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue (Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..")))
let path relative = Path.Combine(root, relative)
let read relative = File.ReadAllText(path relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let readJson relative = JsonDocument.Parse(read relative)
let contract = readJson "evidence/github-substrate-v2/gs2-07-5/contract.json"
let c = contract.RootElement
let text (name: string) = c.GetProperty(name).GetString()
let head = "1111111111111111111111111111111111111111"
let baseSha = "2222222222222222222222222222222222222222"
let digest ch = String.replicate 64 ch
let checks = [ "architecture"; "unit" ]
let baselineFacts =
    { EventName = eventName; Repository = "FS-GG/FS.GG.Coordination"; MergeGroupId = "mg-315"; MergeGroupHeadSha = head
      BaseRepository = "FS-GG/FS.GG.Coordination"; BaseRef = "refs/heads/main"
      ObservedBaseSha = baseSha; CurrentBaseSha = baseSha; BaseObservationRevision = 17L; CurrentBaseObservationRevision = 17L
      ObservedAtUnixSeconds = 1000L; FreshUntilUnixSeconds = 1100L; EvaluatedAtUnixSeconds = 1050L
      RequiredChecks = checks
      CheckResults = checks |> List.map (fun name -> { Name = name; EventName = "merge_group"; HeadSha = head; Conclusion = MergeGroupCheckConclusion.Success })
      ObservedClaimGeneration = 5561468721L; CurrentClaimGeneration = 5561468721L
      ObservedReviewDigest = digest "a"; CurrentReviewDigest = digest "a"; ObservedCandidateHeadSha = head; CurrentCandidateHeadSha = head
      ObservedDependencyDigest = digest "b"; CurrentDependencyDigest = digest "b"
      ObservedReleaseObligationsMet = true; CurrentReleaseObligationsMet = true
      ObservedSettingsDigest = digest "c"; CurrentSettingsDigest = digest "c"; HasConflictingGroup = false; AttemptsDirectMerge = false }
let get = function Ok value -> value | Error errors -> failwithf "unexpected refusal: %A" errors
let has expected = function Error findings -> List.contains expected findings | Ok _ -> false
let baseline = compile baselineFacts |> get
let bytes = serialize baseline
let sourceText = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubMergeGroupQualification.fs"
let alterFirst change = { baselineFacts with CheckResults = change baselineFacts.CheckResults.Head :: baselineFacts.CheckResults.Tail }

let executeGenerated control =
    match control with
    | MergeGroupPrerequisite -> shaFile "evidence/github-substrate-v2/accepted/GS2-07.4.json" = text "prerequisiteFileSha256"
    | MergeGroupRoadmap -> text "roadmapRevision" = "64e9a2b7753f438f8ad31298fd17698ff2a142e6" && text "roadmapSha256" = "da6477affae014d9ef5cc473f608ca1c1cb25bcffe51f99ed9e5b22994844a0a"
    | MergeGroupPositive -> parse bytes = Ok baseline && verify baseline.Seal baseline = Ok baseline
    | MergeGroupEvent -> compile { baselineFacts with EventName = "merge_group:destroyed" } |> has (GitHubMergeGroupFinding.UnknownEvent "merge_group:destroyed")
    | MergeGroupIdentity -> compile { baselineFacts with MergeGroupId = "" } |> has (GitHubMergeGroupFinding.MissingField "mergeGroupId")
    | MergeGroupHead -> compile { baselineFacts with MergeGroupHeadSha = "bad" } |> has (GitHubMergeGroupFinding.MalformedField "mergeGroupHeadSha")
    | BaseRepositoryIdentity -> compile { baselineFacts with BaseRepository = "" } |> has (GitHubMergeGroupFinding.MissingField "baseRepository")
    | BaseRef -> compile { baselineFacts with BaseRef = "main" } |> has (GitHubMergeGroupFinding.MalformedField "baseRef")
    | BaseSha -> compile { baselineFacts with ObservedBaseSha = "bad" } |> has (GitHubMergeGroupFinding.MalformedField "observedBaseSha")
    | BaseRevision -> compile { baselineFacts with CurrentBaseObservationRevision = 18L } |> has (GitHubMergeGroupFinding.BaseRevisionChanged(17L, 18L))
    | BaseFreshness -> compile { baselineFacts with EvaluatedAtUnixSeconds = 1101L } |> has (GitHubMergeGroupFinding.BaseObservationStale 1100L)
    | BaseChanged -> compile { baselineFacts with CurrentBaseSha = head } |> has (GitHubMergeGroupFinding.BaseChanged(baseSha, head))
    | RequiredCheckInventory -> compile { baselineFacts with CheckResults = baselineFacts.CheckResults.Tail } |> has GitHubMergeGroupFinding.IncompleteCheckInventory
    | RequiredCheckEvent -> alterFirst (fun row -> { row with EventName = "pull_request" }) |> compile |> has (GitHubMergeGroupFinding.CheckDidNotRunForMergeGroup "architecture")
    | RequiredCheckHead -> alterFirst (fun row -> { row with HeadSha = baseSha }) |> compile |> has (GitHubMergeGroupFinding.CheckHeadMismatch "architecture")
    | RequiredCheckPending -> alterFirst (fun row -> { row with Conclusion = MergeGroupCheckConclusion.Pending }) |> compile |> has (GitHubMergeGroupFinding.CheckPending "architecture")
    | RequiredCheckFailure -> alterFirst (fun row -> { row with Conclusion = MergeGroupCheckConclusion.Failure }) |> compile |> has (GitHubMergeGroupFinding.CheckFailed "architecture")
    | ClaimFreshness -> compile { baselineFacts with CurrentClaimGeneration = 5561468722L } |> has (GitHubMergeGroupFinding.ClaimChanged(5561468721L, 5561468722L))
    | ReviewFreshness -> compile { baselineFacts with CurrentReviewDigest = digest "d" } |> has GitHubMergeGroupFinding.ReviewChanged
    | CandidateHeadFreshness -> compile { baselineFacts with CurrentCandidateHeadSha = baseSha } |> has GitHubMergeGroupFinding.CandidateHeadChanged
    | DependencyFreshness -> compile { baselineFacts with CurrentDependencyDigest = digest "d" } |> has GitHubMergeGroupFinding.DependencyChanged
    | ReleaseFreshness -> compile { baselineFacts with CurrentReleaseObligationsMet = false } |> has GitHubMergeGroupFinding.ReleaseObligationsUnmet
    | SettingsFreshness -> compile { baselineFacts with CurrentSettingsDigest = digest "d" } |> has GitHubMergeGroupFinding.SettingsChanged
    | ConflictingGroup -> compile { baselineFacts with HasConflictingGroup = true } |> has GitHubMergeGroupFinding.ConflictingGroup
    | DirectMerge -> compile { baselineFacts with AttemptsDirectMerge = true } |> has GitHubMergeGroupFinding.DirectMergeAttempt
    | MergeGroupOrdering -> parse (bytes + " ") = Error [ GitHubMergeGroupFinding.InvalidSerialization "non-canonical bytes" ]
    | MergeGroupSeal -> verify (digest "0") baseline = Error [ GitHubMergeGroupFinding.AlteredSeal ]
    | MergeGroupReplay -> replay baseline baselineFacts = Ok baseline && serialize (replay baseline baselineFacts |> get) = bytes
    | MergeGroupQuintPreservation -> shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" = text "protocolSha256"
    | MergeGroupNoNetwork -> not(Regex.IsMatch(sourceText, "HttpClient|WebRequest", RegexOptions.IgnoreCase))
    | MergeGroupNoProductionMutation -> not(Regex.IsMatch(sourceText, "Octokit|GitHubClient|QueueClient|\\b(PATCH|POST|PUT|DELETE)\\b", RegexOptions.IgnoreCase))

let executeIndependent control =
    match control with
    | MergeGroupPrerequisite -> text "prerequisiteReceiptDigest" = "d2cf3b943fc153047652d73de77bfdcb35fe6a087f414a494eec35542edd2a50"
    | MergeGroupRoadmap -> text "roadmapSha256" = "da6477affae014d9ef5cc473f608ca1c1cb25bcffe51f99ed9e5b22994844a0a"
    | MergeGroupPositive -> compile baselineFacts |> Result.map serialize = Ok bytes
    | MergeGroupEvent -> compile { baselineFacts with EventName = "pull_request:closed" } |> Result.isError
    | MergeGroupIdentity -> compile { baselineFacts with MergeGroupId = " " } |> Result.isError
    | MergeGroupHead -> compile { baselineFacts with MergeGroupHeadSha = String.replicate 40 "A" } |> Result.isError
    | BaseRepositoryIdentity -> compile { baselineFacts with BaseRepository = "bad repo" } |> Result.isError
    | BaseRef -> compile { baselineFacts with BaseRef = "refs/pull/1/head" } |> Result.isError
    | BaseSha -> compile { baselineFacts with CurrentBaseSha = String.replicate 40 "B" } |> Result.isError
    | BaseRevision -> compile { baselineFacts with BaseObservationRevision = 0L } |> Result.isError
    | BaseFreshness -> compile { baselineFacts with EvaluatedAtUnixSeconds = baselineFacts.FreshUntilUnixSeconds + 1L } |> Result.isError
    | BaseChanged -> compile { baselineFacts with ObservedBaseSha = head } |> Result.isError
    | RequiredCheckInventory -> compile { baselineFacts with RequiredChecks = [ "unit"; "unit" ] } |> Result.isError
    | RequiredCheckEvent -> alterFirst (fun row -> { row with EventName = "workflow_dispatch" }) |> compile |> Result.isError
    | RequiredCheckHead -> alterFirst (fun row -> { row with HeadSha = "3333333333333333333333333333333333333333" }) |> compile |> Result.isError
    | RequiredCheckPending -> alterFirst (fun row -> { row with Conclusion = MergeGroupCheckConclusion.Pending }) |> compile |> Result.isError
    | RequiredCheckFailure -> alterFirst (fun row -> { row with Conclusion = MergeGroupCheckConclusion.Failure }) |> compile |> Result.isError
    | ClaimFreshness -> compile { baselineFacts with CurrentClaimGeneration = 0L } |> Result.isError
    | ReviewFreshness -> compile { baselineFacts with CurrentReviewDigest = "bad" } |> Result.isError
    | CandidateHeadFreshness -> compile { baselineFacts with ObservedCandidateHeadSha = baseSha } |> Result.isError
    | DependencyFreshness -> compile { baselineFacts with CurrentDependencyDigest = "bad" } |> Result.isError
    | ReleaseFreshness -> compile { baselineFacts with ObservedReleaseObligationsMet = false } |> Result.isError
    | SettingsFreshness -> compile { baselineFacts with CurrentSettingsDigest = "bad" } |> Result.isError
    | ConflictingGroup -> compile { baselineFacts with HasConflictingGroup = true; MergeGroupId = "mg-other" } |> Result.isError
    | DirectMerge -> compile { baselineFacts with AttemptsDirectMerge = true; EventName = "pull_request:closed" } |> Result.isError
    | MergeGroupOrdering -> parse (bytes + "\n") |> Result.isError
    | MergeGroupSeal ->
        let changed = (if baseline.Seal[0] = '0' then "1" else "0") + baseline.Seal.Substring 1
        parse (bytes.Replace(baseline.Seal, changed)) = Error [ GitHubMergeGroupFinding.AlteredSeal ]
    | MergeGroupReplay -> replay baseline { baselineFacts with EvaluatedAtUnixSeconds = 1051L } = Error [ GitHubMergeGroupFinding.ReplayConflict ]
    | MergeGroupQuintPreservation -> text "protocolSha256" = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
    | MergeGroupNoNetwork ->
        let detector (value: string) = Regex.IsMatch(value, "httpclient|webrequest", RegexOptions.IgnoreCase)
        not(detector sourceText) && detector(sourceText + "\nHttpCLIENT")
    | MergeGroupNoProductionMutation ->
        let detector (value: string) = Regex.IsMatch(value, "octokit|githubclient|queueclient|\\b(patch|post|put|delete)\\b", RegexOptions.IgnoreCase)
        not(detector sourceText) && detector(sourceText + "\nGitHubClient")

let baselineGreen = parse bytes = Ok baseline && verify baseline.Seal baseline = Ok baseline
let generated: GitHubMergeGroupControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeGenerated control; BaselineGreen = baselineGreen })
let independent: GitHubMergeGroupControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeIndependent control; BaselineGreen = baselineGreen })
let retained relative =
    use document = readJson relative
    let node = document.RootElement
    let strings (name: string) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    strings "controls", strings "cases", node.GetProperty("caseContract").GetString()
let expectedIds = requiredControls |> List.map controlId
let generatedIds, generatedCases, generatedContract = retained "evidence/github-substrate-v2/gs2-07-5/generated-controls.json"
let independentIds, independentCases, independentContract = retained "evidence/github-substrate-v2/gs2-07-5/independent-controls.json"
if generatedIds <> expectedIds || generatedCases.Length <> expectedIds.Length || generatedCases |> List.exists String.IsNullOrWhiteSpace then failwith "generated retained inventory differs"
if independentIds <> expectedIds || independentCases.Length <> expectedIds.Length || independentCases |> List.exists String.IsNullOrWhiteSpace then failwith "independent retained inventory differs"
if generatedCases = independentCases || generatedContract = independentContract then failwith "control authorship is not independent"
match validateControls generated independent with
| Ok () -> ()
| Error errors -> failwithf "Q3 controls failed: %A; generated=%A; independent=%A" errors generated independent
printfn "GITHUB_MERGE_GROUP_OK disposition=%s controls=%d seal=%s" baseline.Disposition expectedIds.Length baseline.Seal
