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
let pilotFacts =
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
let pilot = Sandbox.compilePilot pilotFacts |> get
let effects = [ "visibility-public"; "base-advance"; "required-check-growth"; "queue-admission" ] |> List.mapi(fun index operation -> { OperationId=operation; Attempt=1; ResultDigest=digest(string(index+1)) })
let recoveryFacts =
    { Repository=Sandbox.repository; RepositoryId=Sandbox.repositoryId; PilotSeal=pilot.Seal
      DurableCheckpointDigest=digest "d"; ResumeCheckpointDigest=digest "d"; Interrupted=true; FailedStepInjected=true
      AppliedEffects=effects; RetryEffects=effects |> List.map(fun effect -> { effect with Attempt=2 })
      Compensations=effects |> List.rev |> List.mapi(fun index effect -> { OperationId=effect.OperationId; CompensationId=$"rollback-{index+1}"; FinalStateDigest=digest "e" })
      DuplicateEffectCount=0; TemporaryResourceCount=0; PreVisibility="private"; FinalVisibility="private"
      PreSettingsDigest=digest "f"; FinalSettingsDigest=digest "f"; Recoverable=true }
let baseline = Sandbox.compileRecovery recoveryFacts |> get
let bytes = Sandbox.serializeRecovery baseline
let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubQueueSandbox.fs"
let prerequisite () =
    use receipt = json "evidence/github-substrate-v2/accepted/GS2-07.5.json"
    shaFile "evidence/github-substrate-v2/accepted/GS2-07.5.json"=text "prerequisiteFileSha256"
    && receipt.RootElement.GetProperty("digest").GetString()=text "prerequisiteReceiptDigest"
let roadmap () = text "roadmapRevision"="7e5754e23d274b31d21f9a2b4c0c0a00265ee366" && text "roadmapSha256"="33d303a888752d0b0f53e5443b2322bd601ebce43c86166ab6dc8d8387bd82ee"
let hostedRecovery () =
    let initial = hosted.GetProperty("initialAdmission")
    let recovery = hosted.GetProperty("recovery")
    let cleanup = hosted.GetProperty("cleanup")
    initial.GetProperty("failedStepInjected").GetBoolean()
    && initial.GetProperty("interrupted").GetBoolean()
    && (initial.GetProperty("jobs").EnumerateArray() |> Seq.exists(fun job -> job.GetProperty("name").GetString()="queue-growth" && job.GetProperty("conclusion").GetString()="failure"))
    && recovery.GetProperty("mergeGroupRun").GetProperty("conclusion").GetString()="success"
    && recovery.GetProperty("newRequiredCheckExecuted").GetBoolean()
    && recovery.GetProperty("duplicateEffectCount").GetInt32()=0
    && cleanup.GetProperty("temporaryBranchCount").GetInt32()=0
    && cleanup.GetProperty("queueRefCount").GetInt32()=0
    && cleanup.GetProperty("temporaryWorkflowFileCount").GetInt32()=0
    && cleanup.GetProperty("finalVisibility").GetString()="private"
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
    | "compensation" -> Sandbox.compileRecovery { recoveryFacts with Compensations=List.rev recoveryFacts.Compensations } |> has GitHubQueueSandboxFinding.CompensationOrderInvalid
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
