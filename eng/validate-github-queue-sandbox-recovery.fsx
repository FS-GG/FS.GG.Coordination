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
let cleanup = hosted.GetProperty("cleanup")
let settings = prestate.GetProperty("settings")
let secrets = prestate.GetProperty("secrets")
let checks = stringsAt recovery "requiredChecks"
let pilotFacts:QueuePilotFacts =
    { Repository=stringAt hosted "repository"; RepositoryId=int64At hosted "repositoryId"; IsProduction=false; ProviderCapability=QueueProviderCapability.Supported
      PreVisibility=stringAt settings "visibility"; RepositorySecretCount=secrets.GetProperty("repositorySecretCount").GetInt32(); EnvironmentSecretCount=secrets.GetProperty("environmentSecretCount").GetInt32(); PilotId=stringAt hosted "pilotId"
      CandidateSha=stringAt candidate "sha"; CurrentCandidateSha=stringAt candidate "currentShaAtReevaluation"; MergeGroupHeadSha=stringAt recoveryRun "headSha"; BaseRef=stringAt recovery "baseRef"
      ObservedBaseSha=stringAt recovery "priorBaseSha"; CurrentBaseSha=stringAt recovery "baseSha"; ReevaluatedBaseSha=stringAt recovery "baseSha"; BaseObservationRevision=int64At initialRun "id"; CurrentBaseObservationRevision=int64At recoveryRun "id"
      OriginalRequiredChecks=stringsAt initial "requiredChecks"; CurrentRequiredChecks=checks
      CheckResults=recovery.GetProperty("jobs").EnumerateArray() |> Seq.map(fun job -> ({ Name=stringAt job "name"; HeadSha=stringAt job "headSha"; EventName=stringAt recoveryRun "event"; Conclusion=if stringAt job "conclusion"="success" then QueueCheckConclusion.Success else QueueCheckConclusion.Failure }:QueueCheckResult)) |> Seq.toList
      ObservedClaimGeneration=int64At authority "claimGeneration"; CurrentClaimGeneration=int64At authority "claimGeneration"
      ObservedReviewDigest=stringAt authority "reviewDigest"; CurrentReviewDigest=stringAt authority "reviewDigest"; ObservedDependencyDigest=stringAt authority "dependencyDigest"; CurrentDependencyDigest=stringAt authority "dependencyDigest"
      ObservedReleaseObligationsMet=authority.GetProperty("releaseObligationsMet").GetBoolean(); CurrentReleaseObligationsMet=authority.GetProperty("releaseObligationsMet").GetBoolean(); ObservedSettingsDigest=stringAt authority "settingsDigest"; CurrentSettingsDigest=stringAt authority "settingsDigest"
      AdmittedAtUnixSeconds=int64At recovery "admittedAtUnixSeconds"; ExpiresAtUnixSeconds=int64At recovery "expiresAtUnixSeconds"; EvaluatedAtUnixSeconds=int64At recovery "evaluatedAtUnixSeconds" }
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let pilot = Sandbox.compilePilot pilotFacts |> get
let retryEffect = recovery.GetProperty("retryEffect")
let operation = stringAt retryEffect "operation"
let retryDigest = stringAt retryEffect "firstResultDigest"
let rulesetId = cleanup.GetProperty("rulesetId").GetInt64()
let effects = [ { OperationId=operation; Attempt=1; ResultDigest=retryDigest } ]
let recoveryFacts =
    { Repository=Sandbox.repository; RepositoryId=Sandbox.repositoryId; PilotSeal=pilot.Seal
      DurableCheckpointDigest=stringAt initial "durableCheckpointDigest"; ResumeCheckpointDigest=stringAt initial "resumeCheckpointDigest"; Interrupted=initial.GetProperty("interrupted").GetBoolean(); FailedStepInjected=initial.GetProperty("failedStepInjected").GetBoolean()
      AppliedEffects=effects; RetryEffects=effects |> List.map(fun effect -> { effect with Attempt=2 })
      Compensations=[ { OperationId=operation; CompensationId=$"delete-ruleset-{rulesetId}"; FinalStateDigest=stringAt cleanup "finalSettingsDigest" } ]
      DuplicateEffectCount=recovery.GetProperty("duplicateEffectCount").GetInt32(); TemporaryResourceCount=cleanup.GetProperty("temporaryBranchCount").GetInt32()+cleanup.GetProperty("queueRefCount").GetInt32()+cleanup.GetProperty("temporaryWorkflowFileCount").GetInt32(); PreVisibility=stringAt settings "visibility"; FinalVisibility=stringAt cleanup "finalVisibility"
      PreSettingsDigest=stringAt settings "digest"; FinalSettingsDigest=stringAt cleanup "finalSettingsDigest"; Recoverable=true }
let baseline = Sandbox.compileRecovery recoveryFacts |> get
let bytes = Sandbox.serializeRecovery baseline
let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fs"
let prerequisite () =
    use receipt = json "evidence/github-substrate-v2/accepted/GS2-07.5.json"
    shaFile "evidence/github-substrate-v2/accepted/GS2-07.5.json"=text "prerequisiteFileSha256"
    && receipt.RootElement.GetProperty("digest").GetString()=text "prerequisiteReceiptDigest"
let roadmap () = text "roadmapRevision"="7e5754e23d274b31d21f9a2b4c0c0a00265ee366" && text "roadmapSha256"="33d303a888752d0b0f53e5443b2322bd601ebce43c86166ab6dc8d8387bd82ee"
let hostedRecovery () =
    stringAt hosted "repository"=Sandbox.repository
    && int64At hosted "repositoryId"=Sandbox.repositoryId
    && stringAt prestate "repository"=Sandbox.repository
    && int64At prestate "repositoryId"=Sandbox.repositoryId
    && initial.GetProperty("failedStepInjected").GetBoolean()
    && initial.GetProperty("interrupted").GetBoolean()
    && int64At initial "expiredObservedAtUnixSeconds">=int64At initial "expiresAtUnixSeconds"
    && stringAt initial "durableCheckpointDigest"=stringAt initial "resumeCheckpointDigest"
    && (initial.GetProperty("jobs").EnumerateArray() |> Seq.exists(fun job -> stringAt job "name"="queue-growth" && stringAt job "headSha"=stringAt initialRun "headSha" && stringAt job "conclusion"="failure"))
    && (initial.GetProperty("jobs").EnumerateArray() |> Seq.exists(fun job -> stringAt job "name"="queue-pilot" && stringAt job "headSha"=stringAt initialRun "headSha" && stringAt job "conclusion"="cancelled"))
    && stringAt recoveryRun "event"="merge_group"
    && stringAt recoveryRun "conclusion"="success"
    && (recovery.GetProperty("jobs").EnumerateArray() |> Seq.map(fun job -> stringAt job "name", stringAt job "headSha", stringAt job "conclusion") |> Seq.toList)=[("queue-growth",stringAt recoveryRun "headSha","success");("queue-pilot",stringAt recoveryRun "headSha","success")]
    && recovery.GetProperty("newRequiredCheckExecuted").GetBoolean()
    && recovery.GetProperty("duplicateEffectCount").GetInt32()=0
    && retryEffect.GetProperty("attempts").GetInt32()=2
    && stringAt retryEffect "firstResultDigest"=stringAt retryEffect "secondResultDigest"
    && stringAt candidate "sha"=stringAt candidate "currentShaAtReevaluation"
    && stringAt recovery "priorBaseSha"<>stringAt recovery "baseSha"
    && cleanup.GetProperty("temporaryBranchCount").GetInt32()=0
    && cleanup.GetProperty("queueRefCount").GetInt32()=0
    && cleanup.GetProperty("temporaryWorkflowFileCount").GetInt32()=0
    && stringAt cleanup "finalVisibility"=stringAt settings "visibility"
    && stringAt cleanup "finalSettingsDigest"=stringAt settings "digest"
    && stringAt cleanup "finalBranchInventoryDigest"=stringAt prestate "branchInventoryDigest"
    && stringAt cleanup "finalActiveWorkflowInventoryDigest"=stringAt prestate "workflowInventoryDigest"
    && cleanup.GetProperty("repositorySecretCount").GetInt32()=secrets.GetProperty("repositorySecretCount").GetInt32()
    && cleanup.GetProperty("environmentSecretCount").GetInt32()=secrets.GetProperty("environmentSecretCount").GetInt32()
    && let artifact=recovery.GetProperty("typedArtifact") in
       artifact.GetProperty("id").GetInt64()>0L
       && not(artifact.GetProperty("expired").GetBoolean())
       && stringAt artifact "contentSha256"=shaFile "evidence/github-substrate-v2/gs2-07-6/hosted-proof.json"
       && stringAt proof "repository"=Sandbox.repository
       && int64At proof "repositoryId"=Sandbox.repositoryId
       && int64At proof "runId"=int64At recoveryRun "id"
       && stringAt proof "mergeGroupHeadSha"=stringAt recoveryRun "headSha"
       && stringAt proof "workflowSha"=stringAt recoveryRun "headSha"
       && stringAt proof "event"="merge_group"
       && stringAt proof "requiredCheck"="queue-growth"
let noWriter () =
    let detector (value:string) = Regex.IsMatch(value, "HttpClient|WebRequest|Octokit|GitHubClient|QueueClient|GetEnvironmentVariable", RegexOptions.IgnoreCase)
    not(detector source) && detector(source+"\nHttpClient")

let generated control =
    match control with
    | "prerequisite" -> prerequisite()
    | "roadmap" -> roadmap()
    | "sandbox-identity" -> Sandbox.repository=text "repository" && Sandbox.repositoryId=integer "repositoryId"
    | "interruption" -> Sandbox.compileRecovery { recoveryFacts with Interrupted=false } |> has GitHubQueueSandboxFinding.MissingInterruption
    | "failed-step" -> Sandbox.compileRecovery { recoveryFacts with FailedStepInjected=false } |> has GitHubQueueSandboxFinding.MissingFailedStep
    | "sealed-resume" -> Sandbox.compileRecovery { recoveryFacts with ResumeCheckpointDigest=digest "0" } |> has GitHubQueueSandboxFinding.UnsealedResume
    | "deterministic-retry" -> Sandbox.compileRecovery { recoveryFacts with RetryEffects=recoveryFacts.RetryEffects.Tail } |> has GitHubQueueSandboxFinding.RetryDiverged
    | "duplicate-effect" -> Sandbox.compileRecovery { recoveryFacts with DuplicateEffectCount=1 } |> has GitHubQueueSandboxFinding.DuplicateEffect
    | "compensation" -> let changed={ recoveryFacts.Compensations.Head with OperationId="wrong-operation" } in Sandbox.compileRecovery { recoveryFacts with Compensations=[ changed ] } |> has GitHubQueueSandboxFinding.CompensationOrderInvalid
    | "rollback" -> Sandbox.compileRecovery { recoveryFacts with FinalVisibility="public" } |> has (GitHubQueueSandboxFinding.RollbackMismatch "visibility")
    | "cleanup" -> hostedRecovery() && (Sandbox.compileRecovery { recoveryFacts with TemporaryResourceCount=1 } |> has GitHubQueueSandboxFinding.CleanupIncomplete)
    | "authoritative-readback" -> Sandbox.compileRecovery { recoveryFacts with FinalSettingsDigest=digest "0" } |> has (GitHubQueueSandboxFinding.RollbackMismatch "settings")
    | "ordering" -> Sandbox.parseRecovery(bytes+" ")=Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
    | "seal" -> Sandbox.verifyRecovery (digest "0") baseline=Error [ GitHubQueueSandboxFinding.AlteredSeal ]
    | "unrecoverable" -> Sandbox.compileRecovery { recoveryFacts with Recoverable=false } |> has GitHubQueueSandboxFinding.Unrecoverable
    | "no-fleet" -> Sandbox.compileRecovery { recoveryFacts with Repository="FS-GG/production"; RepositoryId=1L } |> Result.isError
    | "no-production-writer" -> noWriter()
    | "no-release" -> not(boolean "releaseAuthority")
    | "no-package" -> not(boolean "packageAuthority")
    | "no-successor-authority" -> not(boolean "successorAuthority")
    | _ -> false

let independent control =
    match control with
    | "prerequisite" -> text "prerequisiteUnit"="GS2-07.5" && prerequisite()
    | "roadmap" -> roadmap() && Regex.IsMatch(text "roadmapSha256", "^[0-9a-f]{64}$")
    | "sandbox-identity" -> Sandbox.compileRecovery { recoveryFacts with RepositoryId=42L } |> Result.isError
    | "interruption" -> not recoveryFacts.Interrupted |> not && Sandbox.compileRecovery { recoveryFacts with Interrupted=false } |> Result.isError
    | "failed-step" -> Sandbox.compileRecovery { recoveryFacts with FailedStepInjected=false; Interrupted=false } |> Result.isError
    | "sealed-resume" -> Sandbox.compileRecovery { recoveryFacts with DurableCheckpointDigest="bad" } |> Result.isError
    | "deterministic-retry" -> let changed={ recoveryFacts.RetryEffects.Head with ResultDigest=digest "9" } in Sandbox.compileRecovery { recoveryFacts with RetryEffects=changed::recoveryFacts.RetryEffects.Tail } |> Result.isError
    | "duplicate-effect" -> Sandbox.compileRecovery { recoveryFacts with DuplicateEffectCount=2 } |> Result.isError
    | "compensation" -> Sandbox.compileRecovery { recoveryFacts with Compensations=recoveryFacts.Compensations.Tail } |> Result.isError
    | "rollback" -> Sandbox.compileRecovery { recoveryFacts with PreVisibility="public"; FinalVisibility="public" } |> Result.isError
    | "cleanup" -> Sandbox.compileRecovery { recoveryFacts with TemporaryResourceCount=2 } |> Result.isError
    | "authoritative-readback" -> Sandbox.compileRecovery { recoveryFacts with PreSettingsDigest="bad" } |> Result.isError
    | "ordering" -> Sandbox.parseRecovery(bytes+"\n") |> Result.isError
    | "seal" -> let changed=(if baseline.Seal[0]='0' then "1" else "0")+baseline.Seal.Substring(1) in Sandbox.parseRecovery(bytes.Replace(baseline.Seal,changed))=Error [ GitHubQueueSandboxFinding.AlteredSeal ]
    | "unrecoverable" -> Sandbox.compileRecovery { recoveryFacts with Recoverable=false; TemporaryResourceCount=1 } |> Result.isError
    | "no-fleet" -> not(boolean "fleetAuthority") && Sandbox.compileRecovery { recoveryFacts with Repository="FS-GG/Fleet" } |> Result.isError
    | "no-production-writer" -> not(boolean "productionWriterAuthority") && noWriter()
    | "no-release" -> boolean "releaseAuthority"=false
    | "no-package" -> boolean "packageAuthority"=false
    | "no-successor-authority" -> boolean "successorAuthority"=false
    | _ -> false

let retained relative =
    use document=json relative
    let root=document.RootElement
    root.GetProperty("recoveryControls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList,
    root.GetProperty("caseContract").GetString()
let expected=Sandbox.recoveryControlIds
let generatedIds,generatedContract=retained "evidence/github-substrate-v2/gs2-07-6/generated-controls.json"
let independentIds,independentContract=retained "evidence/github-substrate-v2/gs2-07-6/independent-controls.json"
if generatedIds<>expected || independentIds<>expected || generatedContract=independentContract then failwith "retained recovery controls are stale or not independent"
let generatedRows=expected |> List.map(fun id -> { ControlId=id; ControlPassed=generated id; BaselineGreen=(Sandbox.parseRecovery bytes=Ok baseline) })
let independentRows=expected |> List.map(fun id -> { ControlId=id; ControlPassed=independent id; BaselineGreen=(Sandbox.verifyRecovery baseline.Seal baseline=Ok baseline) })
match Sandbox.validateControls expected generatedRows independentRows with Ok() -> () | Error errors -> failwithf "Q6 controls failed: %A" errors
if Sandbox.replayRecovery baseline recoveryFacts<>Ok baseline then failwith "deterministic recovery replay diverged"
printfn "GITHUB_QUEUE_SANDBOX_RECOVERY_OK disposition=%s controls=%d seal=%s" baseline.Disposition expected.Length baseline.Seal
