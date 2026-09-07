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
let prestateDocument = json "evidence/github-substrate-v2/gs2-07-6/hosted-prestate.json"
let prestate = prestateDocument.RootElement
let proofDocument = json "evidence/github-substrate-v2/gs2-07-6/hosted-proof.json"
let proof = proofDocument.RootElement
let checkpointDocument = json "evidence/github-substrate-v2/gs2-07-6/durable-checkpoint.json"
let checkpoint = checkpointDocument.RootElement
let refusalDocument = json "evidence/github-substrate-v2/gs2-07-6/expired-admission-refusal.json"
let refusal = refusalDocument.RootElement
let handoffDocument = json "evidence/github-substrate-v2/gs2-07-6/process-handoff.json"
let handoff = handoffDocument.RootElement
let observedRecordDocument = json "evidence/github-substrate-v2/gs2-07-6/authority-observed.json"
let observedRecord = observedRecordDocument.RootElement
let currentRecordDocument = json "evidence/github-substrate-v2/gs2-07-6/authority-current.json"
let currentRecord = currentRecordDocument.RootElement
let observedClaimDocument = json "evidence/github-substrate-v2/gs2-07-6/authority-observed-claim.json"
let observedClaim = observedClaimDocument.RootElement
let currentClaimDocument = json "evidence/github-substrate-v2/gs2-07-6/authority-current-claim.json"
let currentClaim = currentClaimDocument.RootElement
let text (name:string) = c.GetProperty(name).GetString()
let boolean (name:string) = c.GetProperty(name).GetBoolean()
let integer (name:string) = c.GetProperty(name).GetInt64()
let sha character = String.replicate 40 character
let digest character = String.replicate 64 character
let stringAt (node:JsonElement) (name:string) = node.GetProperty(name).GetString()
let int64At (node:JsonElement) (name:string) = node.GetProperty(name).GetInt64()
let stringsAt (node:JsonElement) (name:string) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let candidate = hosted.GetProperty("candidate")
let initial = hosted.GetProperty("initialAdmission")
let initialRun = initial.GetProperty("mergeGroupRun")
let recovery = hosted.GetProperty("recovery")
let recoveryRun = recovery.GetProperty("mergeGroupRun")
let authority = hosted.GetProperty("authority")
let observedAuthority = authority.GetProperty("observed")
let currentAuthority = authority.GetProperty("current")
let cleanup = hosted.GetProperty("cleanup")
let settings = prestate.GetProperty("settings")
let secrets = prestate.GetProperty("secrets")
let checks = stringsAt recovery "requiredChecks"
let facts:QueuePilotFacts =
    { Repository=stringAt hosted "repository"; RepositoryId=int64At hosted "repositoryId"; IsProduction=false; ProviderCapability=QueueProviderCapability.Supported
      PreVisibility=stringAt settings "visibility"; RepositorySecretCount=secrets.GetProperty("repositorySecretCount").GetInt32(); EnvironmentSecretCount=secrets.GetProperty("environmentSecretCount").GetInt32(); PilotId=stringAt hosted "pilotId"
      CandidateSha=stringAt candidate "sha"; CurrentCandidateSha=stringAt candidate "currentShaAtReevaluation"; MergeGroupHeadSha=stringAt recoveryRun "headSha"; BaseRef=stringAt recovery "baseRef"
      ObservedBaseSha=stringAt recovery "priorBaseSha"; CurrentBaseSha=stringAt recovery "baseSha"; ReevaluatedBaseSha=stringAt recovery "baseSha"; BaseObservationRevision=int64At initialRun "id"; CurrentBaseObservationRevision=int64At recoveryRun "id"
      OriginalRequiredChecks=stringsAt initial "requiredChecks"; CurrentRequiredChecks=checks
      CheckResults=recovery.GetProperty("jobs").EnumerateArray() |> Seq.map(fun job -> ({ Name=stringAt job "name"; HeadSha=stringAt job "headSha"; EventName=stringAt recoveryRun "event"; Conclusion=if stringAt job "conclusion"="success" then QueueCheckConclusion.Success else QueueCheckConclusion.Failure }:QueueCheckResult)) |> Seq.toList
      ObservedClaimGeneration=int64At observedAuthority "claimGeneration"; CurrentClaimGeneration=int64At currentAuthority "claimGeneration"
      ObservedReviewDigest=stringAt observedAuthority "reviewDigest"; CurrentReviewDigest=stringAt currentAuthority "reviewDigest"; ObservedDependencyDigest=stringAt observedAuthority "dependencyDigest"; CurrentDependencyDigest=stringAt currentAuthority "dependencyDigest"
      ObservedReleaseObligationsMet=observedAuthority.GetProperty("releaseObligationsMet").GetBoolean(); CurrentReleaseObligationsMet=currentAuthority.GetProperty("releaseObligationsMet").GetBoolean(); ObservedSettingsDigest=stringAt observedAuthority "settingsDigest"; CurrentSettingsDigest=stringAt currentAuthority "settingsDigest"
      AdmittedAtUnixSeconds=int64At recovery "admittedAtUnixSeconds"; ExpiresAtUnixSeconds=int64At recovery "expiresAtUnixSeconds"; EvaluatedAtUnixSeconds=int64At recovery "evaluatedAtUnixSeconds" }
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = Sandbox.compilePilot facts |> get
let bytes = Sandbox.serializePilot baseline
let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fs"
let prerequisite () =
    use receipt = json "evidence/github-substrate-v2/accepted/GS2-07.5.json"
    shaFile "evidence/github-substrate-v2/accepted/GS2-07.5.json"=text "prerequisiteFileSha256"
    && receipt.RootElement.GetProperty("digest").GetString()=text "prerequisiteReceiptDigest"
let roadmap () = text "roadmapRevision"="7216ec4aae14b17f151a1ed3616eb8a2f4ed2d47" && text "roadmapSha256"="0498209c27cdf75d3c1067dad2c3b88084b03c20f1bd87dcb99192aa43457c36"
let hostedAdmission () =
    let pullRequestRun = initial.GetProperty("pullRequestRun")
    let pullRequestNumber = candidate.GetProperty("pullRequestNumber").GetInt64()
    let baseRef = stringAt recovery "baseRef"
    let baseName = baseRef.Substring("refs/heads/".Length)
    let recoveryBaseSha = stringAt recovery "baseSha"
    let expectedQueueRef = $"refs/heads/gh-readonly-queue/{baseName}/pr-{pullRequestNumber}-{recoveryBaseSha}"
    let authoritySources (row:JsonElement) =
        stringAt row "claimSource"="https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5575246586"
        && stringAt row "reviewSource"="https://github.com/FS-GG/FS.GG.Coordination/issues/320#issuecomment-5564723174"
        && stringAt row "dependencySource"="evidence/github-substrate-v2/accepted/GS2-07.5.json"
        && stringAt row "settingsSource"="evidence/github-substrate-v2/gs2-07-6/hosted-prestate.json"
    let sameAuthority (left:JsonElement) (right:JsonElement) =
        [ "claimSource"; "reviewDigest"; "reviewSource"; "dependencyDigest"; "dependencySource"; "settingsDigest"; "settingsSource" ]
        |> List.forall(fun name -> stringAt left name=stringAt right name)
        && int64At left "observedAtUnixSeconds"=int64At right "observedAtUnixSeconds"
        && int64At left "claimGeneration"=int64At right "claimGeneration"
        && left.GetProperty("releaseObligationsMet").GetBoolean()=right.GetProperty("releaseObligationsMet").GetBoolean()
    int64At hosted "repositoryId"=Sandbox.repositoryId
    && stringAt hosted "repository"=Sandbox.repository
    && int64At prestate "repositoryId"=Sandbox.repositoryId
    && stringAt prestate "repository"=Sandbox.repository
    && stringAt candidate "sha"=stringAt candidate "currentShaAtReevaluation"
    && stringAt candidate "sha"=stringAt pullRequestRun "headSha"
    && stringAt pullRequestRun "event"="pull_request"
    && stringAt pullRequestRun "status"="completed"
    && stringAt pullRequestRun "conclusion"="success"
    && stringAt initial "baseRef"=baseRef
    && stringAt initial "baseSha"=stringAt recovery "priorBaseSha"
    && stringAt initialRun "event"="merge_group"
    && (initial.GetProperty("jobs").EnumerateArray() |> Seq.forall(fun job -> stringAt job "headSha"=stringAt initialRun "headSha"))
    && stringAt recoveryRun "event"="merge_group"
    && stringAt recoveryRun "conclusion"="success"
    && stringAt proof "fullRef"=expectedQueueRef
    && int64At proof "runId"=int64At recoveryRun "id"
    && stringAt proof "mergeGroupHeadSha"=stringAt recoveryRun "headSha"
    && stringAt proof "workflowSha"=stringAt recoveryRun "headSha"
    && int64At observedAuthority "observedAtUnixSeconds">=int64At initial "admittedAtUnixSeconds"
    && int64At observedAuthority "observedAtUnixSeconds"<=int64At checkpoint "sealedAtUnixSeconds"
    && int64At currentAuthority "observedAtUnixSeconds">=int64At refusal "observedAtUnixSeconds"
    && int64At currentAuthority "observedAtUnixSeconds"<=int64At recovery "admittedAtUnixSeconds"
    && sameAuthority observedAuthority observedRecord
    && sameAuthority currentAuthority currentRecord
    && int64At checkpoint "claimGeneration"=int64At observedAuthority "claimGeneration"
    && stringAt checkpoint "candidateSha"=stringAt candidate "sha"
    && stringAt checkpoint "baseRef"=baseRef
    && stringAt checkpoint "baseSha"=stringAt initial "baseSha"
    && int64At checkpoint "runId"=int64At initialRun "id"
    && shaFile "evidence/github-substrate-v2/gs2-07-6/durable-checkpoint.json"=stringAt initial "durableCheckpointDigest"
    && stringAt initial "durableCheckpointDigest"=stringAt handoff "checkpointDigest"
    && handoff.GetProperty("separateProcesses").GetBoolean()
    && int64At handoff "preparePid"<>int64At handoff "resumePid"
    && stringAt refusal "decision"="refused-expired-admission"
    && refusal.GetProperty("freshAdmissionRequired").GetBoolean()
    && int64At refusal "priorAdmissionExpiresAtUnixSeconds"=int64At initial "expiresAtUnixSeconds"
    && stringAt observedClaim "html_url"=stringAt observedAuthority "claimSource"
    && stringAt currentClaim "html_url"=stringAt currentAuthority "claimSource"
    && stringAt observedClaim "body" |> fun body -> body.Contains("fsgg:claim worker=curlew-2b4b")
    && authoritySources observedAuthority && authoritySources currentAuthority
    && stringAt observedAuthority "dependencyDigest"=text "prerequisiteReceiptDigest"
    && stringAt currentAuthority "dependencyDigest"=text "prerequisiteReceiptDigest"
    && stringAt observedAuthority "settingsDigest"=stringAt settings "digest"
    && stringAt currentAuthority "settingsDigest"=stringAt settings "digest"
    && recovery.GetProperty("baseReevaluated").GetBoolean()
    && recovery.GetProperty("requiredChecksGrew").GetBoolean()
    && recovery.GetProperty("newRequiredCheckExecuted").GetBoolean()
    && checks=[ "queue-growth"; "queue-pilot" ]
    && stringAt cleanup "finalVisibility"=stringAt settings "visibility"
    && stringAt cleanup "finalSettingsDigest"=stringAt settings "digest"
    && stringAt cleanup "finalBranchInventoryDigest"=stringAt prestate "branchInventoryDigest"
    && stringAt cleanup "finalActiveWorkflowInventoryDigest"=stringAt prestate "workflowInventoryDigest"
    && cleanup.GetProperty("repositorySecretCount").GetInt32()=secrets.GetProperty("repositorySecretCount").GetInt32()
    && cleanup.GetProperty("environmentSecretCount").GetInt32()=secrets.GetProperty("environmentSecretCount").GetInt32()
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
