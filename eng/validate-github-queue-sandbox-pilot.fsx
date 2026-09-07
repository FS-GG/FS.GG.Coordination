#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts

module Sandbox = FS.GG.Coordination.Qualification.Contracts.GitHubQueueSandbox

let root = fsi.CommandLineArgs |> Array.tryItem 1 |> Option.defaultValue (Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..")))
let read relative = File.ReadAllText(Path.Combine(root, relative))
let shaFile relative = File.ReadAllBytes(Path.Combine(root, relative)) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let json relative = JsonDocument.Parse(read relative)
let contract = json "evidence/github-substrate-v2/gs2-07-6/contract.json"
let c = contract.RootElement
let hostedDocument = json "evidence/github-substrate-v2/gs2-07-6/hosted-run.json"
let hosted = hostedDocument.RootElement
let text (name:string) = c.GetProperty(name).GetString()
let boolean (name:string) = c.GetProperty(name).GetBoolean()
let integer (name:string) = c.GetProperty(name).GetInt64()
let sha character = String.replicate 40 character
let digest character = String.replicate 64 character
let checks = [ "architecture"; "queue-growth" ]
let facts =
    { Repository=Sandbox.repository; RepositoryId=Sandbox.repositoryId; IsProduction=false; ProviderCapability=QueueProviderCapability.Supported
      PreVisibility="private"; RepositorySecretCount=0; EnvironmentSecretCount=0; PilotId="gs2-07-6-pilot"
      CandidateSha=sha "1"; CurrentCandidateSha=sha "1"; MergeGroupHeadSha=sha "2"; BaseRef="refs/heads/main"
      ObservedBaseSha=sha "3"; CurrentBaseSha=sha "4"; ReevaluatedBaseSha=sha "4"; BaseObservationRevision=10L; CurrentBaseObservationRevision=11L
      OriginalRequiredChecks=[ "architecture" ]; CurrentRequiredChecks=checks
      CheckResults=checks |> List.map(fun name -> { Name=name; HeadSha=sha "2"; EventName="merge_group"; Conclusion=QueueCheckConclusion.Success })
      ObservedClaimGeneration=5565937139L; CurrentClaimGeneration=5565937139L
      ObservedReviewDigest=digest "a"; CurrentReviewDigest=digest "a"; ObservedDependencyDigest=digest "b"; CurrentDependencyDigest=digest "b"
      ObservedReleaseObligationsMet=true; CurrentReleaseObligationsMet=true; ObservedSettingsDigest=digest "c"; CurrentSettingsDigest=digest "c"
      AdmittedAtUnixSeconds=100L; ExpiresAtUnixSeconds=200L; EvaluatedAtUnixSeconds=150L }
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = Sandbox.compilePilot facts |> get
let bytes = Sandbox.serializePilot baseline
let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fs"
let prerequisite () =
    use receipt = json "evidence/github-substrate-v2/accepted/GS2-07.5.json"
    shaFile "evidence/github-substrate-v2/accepted/GS2-07.5.json"=text "prerequisiteFileSha256"
    && receipt.RootElement.GetProperty("digest").GetString()=text "prerequisiteReceiptDigest"
let roadmap () = text "roadmapRevision"="7e5754e23d274b31d21f9a2b4c0c0a00265ee366" && text "roadmapSha256"="33d303a888752d0b0f53e5443b2322bd601ebce43c86166ab6dc8d8387bd82ee"
let hostedAdmission () =
    hosted.GetProperty("repositoryId").GetInt64()=Sandbox.repositoryId
    && hosted.GetProperty("candidate").GetProperty("sha").GetString()=hosted.GetProperty("candidate").GetProperty("currentShaAtReevaluation").GetString()
    && hosted.GetProperty("initialAdmission").GetProperty("pullRequestRun").GetProperty("conclusion").GetString()="success"
    && hosted.GetProperty("initialAdmission").GetProperty("mergeGroupRun").GetProperty("event").GetString()="merge_group"
    && hosted.GetProperty("recovery").GetProperty("baseReevaluated").GetBoolean()
    && hosted.GetProperty("recovery").GetProperty("requiredChecksGrew").GetBoolean()
let alterFirst change = { facts with CheckResults=change facts.CheckResults.Head::facts.CheckResults.Tail }
let noWriter () =
    let detector (value:string) = Regex.IsMatch(value, "HttpClient|WebRequest|Octokit|GitHubClient|QueueClient|GetEnvironmentVariable", RegexOptions.IgnoreCase)
    not(detector source) && detector(source+"\nGitHubClient")

let generated control =
    match control with
    | "prerequisite" -> prerequisite()
    | "roadmap" -> roadmap()
    | "sandbox-identity" -> Sandbox.repository=text "repository" && Sandbox.repositoryId=integer "repositoryId"
    | "provider-capability" -> Sandbox.compilePilot { facts with ProviderCapability=QueueProviderCapability.Unsupported } |> has GitHubQueueSandboxFinding.UnsupportedCapability
    | "prestate" -> Sandbox.compilePilot { facts with PreVisibility="public" } |> has GitHubQueueSandboxFinding.VisibilityTransitionNotBound
    | "no-secrets" -> Sandbox.compilePilot { facts with RepositorySecretCount=1 } |> has GitHubQueueSandboxFinding.SecretPresent
    | "visibility-transition" -> facts.PreVisibility=text "preVisibility" && Sandbox.compilePilot facts=Ok baseline
    | "admission" -> Sandbox.compilePilot facts=Ok baseline && hostedAdmission()
    | "exact-candidate" -> Sandbox.compilePilot { facts with CurrentCandidateSha=sha "9" } |> has GitHubQueueSandboxFinding.CandidateMoved
    | "forward-base-movement" -> Sandbox.compilePilot { facts with CurrentBaseSha=facts.ObservedBaseSha; ReevaluatedBaseSha=facts.ObservedBaseSha } |> has GitHubQueueSandboxFinding.BaseNotAdvanced
    | "required-check-growth" -> Sandbox.compilePilot { facts with CurrentRequiredChecks=facts.OriginalRequiredChecks; CheckResults=facts.CheckResults.Tail } |> has GitHubQueueSandboxFinding.RequiredChecksNotGrown
    | "expiry" -> Sandbox.compilePilot { facts with EvaluatedAtUnixSeconds=facts.ExpiresAtUnixSeconds } |> has GitHubQueueSandboxFinding.AdmissionExpired
    | "claim" -> Sandbox.compilePilot { facts with CurrentClaimGeneration=facts.CurrentClaimGeneration+1L } |> has (GitHubQueueSandboxFinding.AuthorityChanged "claim")
    | "review" -> Sandbox.compilePilot { facts with CurrentReviewDigest=digest "9" } |> has (GitHubQueueSandboxFinding.AuthorityChanged "review")
    | "dependency" -> Sandbox.compilePilot { facts with CurrentDependencyDigest=digest "9" } |> has (GitHubQueueSandboxFinding.AuthorityChanged "dependency")
    | "release" -> Sandbox.compilePilot { facts with CurrentReleaseObligationsMet=false } |> has (GitHubQueueSandboxFinding.AuthorityChanged "release")
    | "settings" -> Sandbox.compilePilot { facts with CurrentSettingsDigest=digest "9" } |> has (GitHubQueueSandboxFinding.AuthorityChanged "settings")
    | "ordering" -> Sandbox.parsePilot(bytes+" ")=Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
    | "seal" -> Sandbox.verifyPilot (digest "0") baseline=Error [ GitHubQueueSandboxFinding.AlteredSeal ]
    | "no-fleet" -> Sandbox.compilePilot { facts with Repository="FS-GG/production"; RepositoryId=1L; IsProduction=true } |> Result.isError
    | "no-production-writer" -> noWriter()
    | "no-release" -> not(boolean "releaseAuthority")
    | "no-package" -> not(boolean "packageAuthority")
    | "no-successor-authority" -> not(boolean "successorAuthority")
    | _ -> false

let independent control =
    match control with
    | "prerequisite" -> text "prerequisiteUnit"="GS2-07.5" && prerequisite()
    | "roadmap" -> roadmap() && Regex.IsMatch(text "roadmapSha256", "^[0-9a-f]{64}$")
    | "sandbox-identity" -> Sandbox.compilePilot { facts with Repository="FS-GG/Other" } |> Result.isError
    | "provider-capability" -> Sandbox.compilePilot { facts with ProviderCapability=QueueProviderCapability.Unknown } |> has GitHubQueueSandboxFinding.UnknownCapability
    | "prestate" -> Sandbox.compilePilot { facts with PreVisibility="unknown" } |> Result.isError
    | "no-secrets" -> Sandbox.compilePilot { facts with EnvironmentSecretCount=1 } |> has GitHubQueueSandboxFinding.SecretPresent
    | "visibility-transition" -> text "preVisibility"="private" && Sandbox.compilePilot { facts with PreVisibility="internal" } |> Result.isError
    | "admission" -> Sandbox.compilePilot { facts with ExpiresAtUnixSeconds=facts.AdmittedAtUnixSeconds } |> Result.isError
    | "exact-candidate" -> Sandbox.compilePilot { facts with CandidateSha=sha "8" } |> Result.isError
    | "forward-base-movement" -> Sandbox.compilePilot { facts with ReevaluatedBaseSha=facts.ObservedBaseSha } |> has GitHubQueueSandboxFinding.BaseNotReevaluated
    | "required-check-growth" -> alterFirst(fun row -> { row with Conclusion=QueueCheckConclusion.Pending }) |> Sandbox.compilePilot |> Result.isError
    | "expiry" -> Sandbox.compilePilot { facts with EvaluatedAtUnixSeconds=facts.ExpiresAtUnixSeconds+1L } |> Result.isError
    | "claim" -> Sandbox.compilePilot { facts with CurrentClaimGeneration=0L } |> Result.isError
    | "review" -> Sandbox.compilePilot { facts with ObservedReviewDigest="bad" } |> Result.isError
    | "dependency" -> Sandbox.compilePilot { facts with ObservedDependencyDigest="bad" } |> Result.isError
    | "release" -> Sandbox.compilePilot { facts with ObservedReleaseObligationsMet=false } |> Result.isError
    | "settings" -> Sandbox.compilePilot { facts with ObservedSettingsDigest="bad" } |> Result.isError
    | "ordering" -> Sandbox.parsePilot(bytes+"\n") |> Result.isError
    | "seal" -> let changed=(if baseline.Seal[0]='0' then "1" else "0")+baseline.Seal.Substring(1) in Sandbox.parsePilot(bytes.Replace(baseline.Seal,changed))=Error [ GitHubQueueSandboxFinding.AlteredSeal ]
    | "no-fleet" -> not(boolean "fleetAuthority") && Sandbox.compilePilot { facts with IsProduction=true } |> has GitHubQueueSandboxFinding.ProductionTarget
    | "no-production-writer" -> not(boolean "productionWriterAuthority") && noWriter()
    | "no-release" -> boolean "releaseAuthority"=false
    | "no-package" -> boolean "packageAuthority"=false
    | "no-successor-authority" -> boolean "successorAuthority"=false
    | _ -> false

let retained relative =
    use document = json relative
    let root = document.RootElement
    root.GetProperty("pilotControls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList,
    root.GetProperty("caseContract").GetString()
let expected = Sandbox.pilotControlIds
let generatedIds,generatedContract=retained "evidence/github-substrate-v2/gs2-07-6/generated-controls.json"
let independentIds,independentContract=retained "evidence/github-substrate-v2/gs2-07-6/independent-controls.json"
if generatedIds<>expected || independentIds<>expected || generatedContract=independentContract then failwith "retained pilot controls are stale or not independent"
let generatedRows = expected |> List.map(fun id -> { ControlId=id; ControlPassed=generated id; BaselineGreen=(Sandbox.parsePilot bytes=Ok baseline) })
let independentRows = expected |> List.map(fun id -> { ControlId=id; ControlPassed=independent id; BaselineGreen=(Sandbox.verifyPilot baseline.Seal baseline=Ok baseline) })
match Sandbox.validateControls expected generatedRows independentRows with Ok() -> () | Error errors -> failwithf "Q4 controls failed: %A" errors
printfn "GITHUB_QUEUE_SANDBOX_PILOT_OK disposition=%s controls=%d seal=%s" baseline.Disposition expected.Length baseline.Seal
