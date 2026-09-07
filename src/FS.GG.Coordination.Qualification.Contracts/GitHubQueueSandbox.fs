namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type QueueProviderCapability = Supported | Unsupported | Unknown
[<RequireQualifiedAccess>]
type QueueCheckConclusion = Success | Pending | Failure
type QueueCheckResult = { Name:string; HeadSha:string; EventName:string; Conclusion:QueueCheckConclusion }
type QueuePilotFacts =
    { Repository:string; RepositoryId:int64; IsProduction:bool; ProviderCapability:QueueProviderCapability
      PreVisibility:string; RepositorySecretCount:int; EnvironmentSecretCount:int; PilotId:string
      CandidateSha:string; CurrentCandidateSha:string; MergeGroupHeadSha:string; BaseRef:string; ObservedBaseSha:string; CurrentBaseSha:string
      ReevaluatedBaseSha:string; BaseObservationRevision:int64; CurrentBaseObservationRevision:int64
      OriginalRequiredChecks:string list; CurrentRequiredChecks:string list; CheckResults:QueueCheckResult list
      ObservedClaimGeneration:int64; CurrentClaimGeneration:int64
      ObservedReviewDigest:string; CurrentReviewDigest:string
      ObservedDependencyDigest:string; CurrentDependencyDigest:string
      ObservedReleaseObligationsMet:bool; CurrentReleaseObligationsMet:bool
      ObservedSettingsDigest:string; CurrentSettingsDigest:string
      AdmittedAtUnixSeconds:int64; ExpiresAtUnixSeconds:int64; EvaluatedAtUnixSeconds:int64 }
type QueuePilotPlan =
    { SchemaVersion:int; Repository:string; RepositoryId:int64; PilotId:string; CandidateSha:string
      MergeGroupHeadSha:string; BaseRef:string; PriorBaseSha:string; BaseSha:string; BaseObservationRevision:int64
      RequiredChecks:string list; CheckResults:QueueCheckResult list; ClaimGeneration:int64; ReviewDigest:string
      DependencyDigest:string; ReleaseObligationsMet:bool; SettingsDigest:string
      AdmittedAtUnixSeconds:int64; ExpiresAtUnixSeconds:int64; EvaluatedAtUnixSeconds:int64
      Disposition:string; Seal:string }
type QueueEffect = { OperationId:string; Attempt:int; ResultDigest:string }
type QueueCompensation = { OperationId:string; CompensationId:string; FinalStateDigest:string }
type QueueRecoveryFacts =
    { Repository:string; RepositoryId:int64; PilotSeal:string; DurableCheckpointDigest:string; ResumeCheckpointDigest:string
      Interrupted:bool; FailedStepInjected:bool; AppliedEffects:QueueEffect list; RetryEffects:QueueEffect list
      Compensations:QueueCompensation list; DuplicateEffectCount:int; TemporaryResourceCount:int
      PreVisibility:string; FinalVisibility:string; PreSettingsDigest:string; FinalSettingsDigest:string; Recoverable:bool }
type QueueRecoveryReceipt =
    { SchemaVersion:int; Repository:string; RepositoryId:int64; PilotSeal:string; CheckpointDigest:string
      AppliedEffects:QueueEffect list; RetryEffects:QueueEffect list; Compensations:QueueCompensation list
      FinalVisibility:string; FinalSettingsDigest:string
      Disposition:string; Seal:string }
[<RequireQualifiedAccess>]
type GitHubQueueSandboxFinding =
    | InvalidField of string | WrongRepository | ProductionTarget | UnsupportedCapability | UnknownCapability
    | SecretPresent | VisibilityTransitionNotBound | CandidateMoved | BaseNotAdvanced | BaseNotReevaluated
    | RequiredChecksNotGrown | CheckInventoryIncomplete | CheckNotSuccessful of string
    | AuthorityChanged of string | AdmissionExpired | MissingInterruption | MissingFailedStep | UnsealedResume
    | RetryDiverged | DuplicateEffect | CompensationOrderInvalid | CleanupIncomplete
    | RollbackMismatch of string | Unrecoverable | AlteredSeal | ReplayConflict | InvalidSerialization
type QueueSandboxControlResult = { ControlId:string; ControlPassed:bool; BaselineGreen:bool }

module GitHubQueueSandbox =
    let repository = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
    let repositoryId = 1353050537L
    let pilotDisposition = "queue-pilot-qualified"
    let recoveryDisposition = "queue-sandbox-recovered"
    let pilotControlIds =
        [ "prerequisite"; "roadmap"; "sandbox-identity"; "provider-capability"; "prestate"; "no-secrets"
          "visibility-transition"; "admission"; "exact-candidate"; "forward-base-movement"; "required-check-growth"
          "expiry"; "claim"; "review"; "dependency"; "release"; "settings"; "ordering"; "seal"
          "no-fleet"; "no-production-writer"; "no-release"; "no-package"; "no-successor-authority" ]
    let recoveryControlIds =
        [ "prerequisite"; "roadmap"; "sandbox-identity"; "interruption"; "failed-step"; "sealed-resume"
          "deterministic-retry"; "duplicate-effect"; "compensation"; "rollback"; "cleanup"
          "authoritative-readback"; "ordering"; "seal"; "unrecoverable"; "no-fleet"; "no-production-writer"
          "no-release"; "no-package"; "no-successor-authority" ]

    let private sha = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
    let private digest = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private token = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private frame (value:string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private join (values:string seq) = values |> Seq.map frame |> String.concat ""
    let private hash (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private conclusion = function QueueCheckConclusion.Success -> "success" | QueueCheckConclusion.Pending -> "pending" | QueueCheckConclusion.Failure -> "failure"
    let private canonical (values:string list) = not(List.isEmpty values) && values = (values |> List.distinct |> List.sort)
    let private checkFrame (c:QueueCheckResult) = join [ c.Name; c.HeadSha; c.EventName; conclusion c.Conclusion ]
    let private effectFrame (effect:QueueEffect) = join [ effect.OperationId; string effect.Attempt; effect.ResultDigest ]
    let private compensationFrame (compensation:QueueCompensation) =
        join [ compensation.OperationId; compensation.CompensationId; compensation.FinalStateDigest ]
    let private pilotSeal (p:QueuePilotPlan) =
        join [ "github-queue-sandbox-pilot/v1"; p.Repository; string p.RepositoryId; p.PilotId; p.CandidateSha
               p.MergeGroupHeadSha; p.BaseRef; p.PriorBaseSha; p.BaseSha; string p.BaseObservationRevision
               join p.RequiredChecks; join (p.CheckResults |> List.map checkFrame); string p.ClaimGeneration
               p.ReviewDigest; p.DependencyDigest; string p.ReleaseObligationsMet; p.SettingsDigest
               string p.AdmittedAtUnixSeconds; string p.ExpiresAtUnixSeconds; string p.EvaluatedAtUnixSeconds; p.Disposition ] |> hash
    let private recoverySeal (r:QueueRecoveryReceipt) =
        join [ "github-queue-sandbox-recovery/v1"; r.Repository; string r.RepositoryId; r.PilotSeal; r.CheckpointDigest
               join (r.AppliedEffects |> List.map effectFrame); join (r.RetryEffects |> List.map effectFrame)
               join (r.Compensations |> List.map compensationFrame); r.FinalVisibility; r.FinalSettingsDigest; r.Disposition ] |> hash
    let private addInvalid (errors:ResizeArray<GitHubQueueSandboxFinding>) (name:string) (value:string) (regex:Regex) =
        if String.IsNullOrWhiteSpace value || not(regex.IsMatch value) then errors.Add(GitHubQueueSandboxFinding.InvalidField name)

    let compilePilot (facts:QueuePilotFacts) =
        let e = ResizeArray<GitHubQueueSandboxFinding>()
        if facts.Repository <> repository || facts.RepositoryId <> repositoryId then e.Add GitHubQueueSandboxFinding.WrongRepository
        if facts.IsProduction then e.Add GitHubQueueSandboxFinding.ProductionTarget
        match facts.ProviderCapability with
        | QueueProviderCapability.Unsupported -> e.Add GitHubQueueSandboxFinding.UnsupportedCapability
        | QueueProviderCapability.Unknown -> e.Add GitHubQueueSandboxFinding.UnknownCapability
        | QueueProviderCapability.Supported -> ()
        if facts.PreVisibility <> "private" then e.Add GitHubQueueSandboxFinding.VisibilityTransitionNotBound
        if facts.RepositorySecretCount <> 0 || facts.EnvironmentSecretCount <> 0 then e.Add GitHubQueueSandboxFinding.SecretPresent
        addInvalid e "pilotId" facts.PilotId token; addInvalid e "candidateSha" facts.CandidateSha sha
        addInvalid e "currentCandidateSha" facts.CurrentCandidateSha sha
        addInvalid e "mergeGroupHeadSha" facts.MergeGroupHeadSha sha
        if facts.CandidateSha <> facts.CurrentCandidateSha then e.Add GitHubQueueSandboxFinding.CandidateMoved
        if not(facts.BaseRef.StartsWith("refs/heads/", StringComparison.Ordinal)) || not(token.IsMatch facts.BaseRef) then e.Add(GitHubQueueSandboxFinding.InvalidField "baseRef")
        addInvalid e "observedBaseSha" facts.ObservedBaseSha sha; addInvalid e "currentBaseSha" facts.CurrentBaseSha sha
        addInvalid e "reevaluatedBaseSha" facts.ReevaluatedBaseSha sha
        if facts.ObservedBaseSha = facts.CurrentBaseSha then e.Add GitHubQueueSandboxFinding.BaseNotAdvanced
        if facts.ReevaluatedBaseSha <> facts.CurrentBaseSha then e.Add GitHubQueueSandboxFinding.BaseNotReevaluated
        if facts.BaseObservationRevision <= 0L || facts.CurrentBaseObservationRevision <= facts.BaseObservationRevision then e.Add(GitHubQueueSandboxFinding.InvalidField "baseObservationRevision")
        if not(canonical facts.OriginalRequiredChecks) || not(canonical facts.CurrentRequiredChecks)
           || not(Set.isSubset (Set.ofList facts.OriginalRequiredChecks) (Set.ofList facts.CurrentRequiredChecks))
           || facts.CurrentRequiredChecks.Length <= facts.OriginalRequiredChecks.Length then e.Add GitHubQueueSandboxFinding.RequiredChecksNotGrown
        facts.OriginalRequiredChecks |> List.iter (fun name -> addInvalid e "originalRequiredCheck" name token)
        facts.CurrentRequiredChecks |> List.iter (fun name -> addInvalid e "currentRequiredCheck" name token)
        if facts.CheckResults |> List.map _.Name <> facts.CurrentRequiredChecks then e.Add GitHubQueueSandboxFinding.CheckInventoryIncomplete
        for c in facts.CheckResults do
            addInvalid e "checkName" c.Name token
            if c.HeadSha <> facts.MergeGroupHeadSha || c.EventName <> "merge_group" || c.Conclusion <> QueueCheckConclusion.Success then e.Add(GitHubQueueSandboxFinding.CheckNotSuccessful c.Name)
        if facts.ObservedClaimGeneration <= 0L || facts.ObservedClaimGeneration <> facts.CurrentClaimGeneration then e.Add(GitHubQueueSandboxFinding.AuthorityChanged "claim")
        let authority (name:string) (observed:string) (current:string) =
            addInvalid e name observed digest; addInvalid e name current digest
            if observed <> current then e.Add(GitHubQueueSandboxFinding.AuthorityChanged name)
        authority "review" facts.ObservedReviewDigest facts.CurrentReviewDigest
        authority "dependency" facts.ObservedDependencyDigest facts.CurrentDependencyDigest
        authority "settings" facts.ObservedSettingsDigest facts.CurrentSettingsDigest
        if not facts.ObservedReleaseObligationsMet || not facts.CurrentReleaseObligationsMet then e.Add(GitHubQueueSandboxFinding.AuthorityChanged "release")
        if facts.AdmittedAtUnixSeconds < 0L || facts.ExpiresAtUnixSeconds <= facts.AdmittedAtUnixSeconds || facts.EvaluatedAtUnixSeconds < facts.AdmittedAtUnixSeconds then e.Add(GitHubQueueSandboxFinding.InvalidField "admission")
        elif facts.EvaluatedAtUnixSeconds >= facts.ExpiresAtUnixSeconds then e.Add GitHubQueueSandboxFinding.AdmissionExpired
        if e.Count > 0 then Error(List.ofSeq e) else
        let unsigned =
            { SchemaVersion=1; Repository=facts.Repository; RepositoryId=facts.RepositoryId; PilotId=facts.PilotId
              CandidateSha=facts.CandidateSha; MergeGroupHeadSha=facts.MergeGroupHeadSha; BaseRef=facts.BaseRef
              PriorBaseSha=facts.ObservedBaseSha; BaseSha=facts.CurrentBaseSha; BaseObservationRevision=facts.CurrentBaseObservationRevision
              RequiredChecks=facts.CurrentRequiredChecks; CheckResults=facts.CheckResults; ClaimGeneration=facts.CurrentClaimGeneration
              ReviewDigest=facts.CurrentReviewDigest; DependencyDigest=facts.CurrentDependencyDigest
              ReleaseObligationsMet=facts.CurrentReleaseObligationsMet; SettingsDigest=facts.CurrentSettingsDigest
              AdmittedAtUnixSeconds=facts.AdmittedAtUnixSeconds; ExpiresAtUnixSeconds=facts.ExpiresAtUnixSeconds
              EvaluatedAtUnixSeconds=facts.EvaluatedAtUnixSeconds; Disposition=pilotDisposition; Seal="" }
        Ok { unsigned with Seal=pilotSeal unsigned }

    let serializePilot (p:QueuePilotPlan) =
        JsonSerializer.Serialize
            {| schemaVersion = p.SchemaVersion; repository = p.Repository; repositoryId = p.RepositoryId; pilotId = p.PilotId
               candidateSha = p.CandidateSha; mergeGroupHeadSha = p.MergeGroupHeadSha; baseRef = p.BaseRef; priorBaseSha = p.PriorBaseSha
               baseSha = p.BaseSha; baseObservationRevision = p.BaseObservationRevision; requiredChecks = p.RequiredChecks
               checkResults = p.CheckResults |> List.map(fun c -> {| name = c.Name; headSha = c.HeadSha; eventName = c.EventName; conclusion = conclusion c.Conclusion |})
               claimGeneration = p.ClaimGeneration; reviewDigest = p.ReviewDigest; dependencyDigest = p.DependencyDigest
               releaseObligationsMet = p.ReleaseObligationsMet; settingsDigest = p.SettingsDigest; admittedAtUnixSeconds = p.AdmittedAtUnixSeconds
               expiresAtUnixSeconds = p.ExpiresAtUnixSeconds; evaluatedAtUnixSeconds = p.EvaluatedAtUnixSeconds; disposition = p.Disposition; seal = p.Seal |}
    let verifyPilot (expectedSeal:string) (p:QueuePilotPlan) =
        let invalid =
            p.SchemaVersion<>1 || p.Repository<>repository || p.RepositoryId<>repositoryId || p.Disposition<>pilotDisposition
            || not(token.IsMatch p.PilotId) || not(sha.IsMatch p.CandidateSha) || not(sha.IsMatch p.MergeGroupHeadSha)
            || not(p.BaseRef.StartsWith("refs/heads/", StringComparison.Ordinal)) || not(token.IsMatch p.BaseRef)
            || not(sha.IsMatch p.PriorBaseSha) || not(sha.IsMatch p.BaseSha) || p.PriorBaseSha=p.BaseSha
            || p.BaseObservationRevision<=0L || not(canonical p.RequiredChecks)
            || p.RequiredChecks |> List.exists (token.IsMatch >> not)
            || p.CheckResults |> List.map _.Name <> p.RequiredChecks
            || p.CheckResults |> List.exists(fun c -> not(token.IsMatch c.Name) || c.HeadSha<>p.MergeGroupHeadSha || c.EventName<>"merge_group" || c.Conclusion<>QueueCheckConclusion.Success)
            || p.ClaimGeneration<=0L || not(digest.IsMatch p.ReviewDigest) || not(digest.IsMatch p.DependencyDigest)
            || not p.ReleaseObligationsMet || not(digest.IsMatch p.SettingsDigest)
            || p.AdmittedAtUnixSeconds<0L || p.ExpiresAtUnixSeconds<=p.AdmittedAtUnixSeconds
            || p.EvaluatedAtUnixSeconds<p.AdmittedAtUnixSeconds || p.EvaluatedAtUnixSeconds>=p.ExpiresAtUnixSeconds
        if invalid then Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
        elif p.Seal<>expectedSeal || p.Seal<>pilotSeal p then Error [ GitHubQueueSandboxFinding.AlteredSeal ] else Ok p
    let parsePilot (value:string) =
        try
            use d = JsonDocument.Parse value
            let r = d.RootElement
            let text (n:string) = r.GetProperty(n).GetString()
            let checks =
                r.GetProperty("checkResults").EnumerateArray()
                |> Seq.map(fun c ->
                    let x =
                        match c.GetProperty("conclusion").GetString() with
                        | "success" -> QueueCheckConclusion.Success
                        | "pending" -> QueueCheckConclusion.Pending
                        | "failure" -> QueueCheckConclusion.Failure
                        | _ -> raise(JsonException())
                    { Name=c.GetProperty("name").GetString(); HeadSha=c.GetProperty("headSha").GetString(); EventName=c.GetProperty("eventName").GetString(); Conclusion=x })
                |> Seq.toList
            let p={ SchemaVersion=r.GetProperty("schemaVersion").GetInt32(); Repository=text "repository"; RepositoryId=r.GetProperty("repositoryId").GetInt64(); PilotId=text "pilotId"; CandidateSha=text "candidateSha"; MergeGroupHeadSha=text "mergeGroupHeadSha"; BaseRef=text "baseRef"; PriorBaseSha=text "priorBaseSha"; BaseSha=text "baseSha"; BaseObservationRevision=r.GetProperty("baseObservationRevision").GetInt64(); RequiredChecks=r.GetProperty("requiredChecks").EnumerateArray()|>Seq.map(_.GetString())|>Seq.toList; CheckResults=checks; ClaimGeneration=r.GetProperty("claimGeneration").GetInt64(); ReviewDigest=text "reviewDigest"; DependencyDigest=text "dependencyDigest"; ReleaseObligationsMet=r.GetProperty("releaseObligationsMet").GetBoolean(); SettingsDigest=text "settingsDigest"; AdmittedAtUnixSeconds=r.GetProperty("admittedAtUnixSeconds").GetInt64(); ExpiresAtUnixSeconds=r.GetProperty("expiresAtUnixSeconds").GetInt64(); EvaluatedAtUnixSeconds=r.GetProperty("evaluatedAtUnixSeconds").GetInt64(); Disposition=text "disposition"; Seal=text "seal" }
            match verifyPilot p.Seal p with
            | Ok plan when serializePilot plan = value -> Ok plan
            | Ok _ -> Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
            | Error findings -> Error findings
        with _ -> Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
    let replayPilot (prior:QueuePilotPlan) (facts:QueuePilotFacts) = match compilePilot facts with Ok p when serializePilot p=serializePilot prior -> Ok p | Ok _ -> Error [ GitHubQueueSandboxFinding.ReplayConflict ] | Error e -> Error e

    let compileRecovery (f:QueueRecoveryFacts) =
        let e=ResizeArray<GitHubQueueSandboxFinding>()
        if f.Repository<>repository || f.RepositoryId<>repositoryId then e.Add GitHubQueueSandboxFinding.WrongRepository
        addInvalid e "pilotSeal" f.PilotSeal digest; addInvalid e "checkpoint" f.DurableCheckpointDigest digest
        if f.ResumeCheckpointDigest<>f.DurableCheckpointDigest then e.Add GitHubQueueSandboxFinding.UnsealedResume
        if not f.Interrupted then e.Add GitHubQueueSandboxFinding.MissingInterruption
        if not f.FailedStepInjected then e.Add GitHubQueueSandboxFinding.MissingFailedStep
        let appliedIds=f.AppliedEffects|>List.map _.OperationId
        let retryIds=f.RetryEffects|>List.map _.OperationId
        let validEffect (effect:QueueEffect) = token.IsMatch effect.OperationId && effect.Attempt > 0 && digest.IsMatch effect.ResultDigest
        if List.isEmpty appliedIds || appliedIds|>List.distinct|>List.length<>appliedIds.Length || retryIds<>appliedIds
           || not(List.forall validEffect f.AppliedEffects) || not(List.forall validEffect f.RetryEffects)
           || f.AppliedEffects.Length<>f.RetryEffects.Length
           || List.map2 (fun a b -> a.ResultDigest<>b.ResultDigest || b.Attempt<>a.Attempt+1) f.AppliedEffects f.RetryEffects |> List.exists id then e.Add GitHubQueueSandboxFinding.RetryDiverged
        if f.DuplicateEffectCount<>0 then e.Add GitHubQueueSandboxFinding.DuplicateEffect
        let compensationIds = f.Compensations |> List.map _.CompensationId
        if f.Compensations|>List.map _.OperationId <> List.rev appliedIds
           || compensationIds |> List.distinct |> List.length <> compensationIds.Length
           || f.Compensations |> List.exists (fun compensation -> not(token.IsMatch compensation.OperationId) || not(token.IsMatch compensation.CompensationId) || not(digest.IsMatch compensation.FinalStateDigest)) then e.Add GitHubQueueSandboxFinding.CompensationOrderInvalid
        if f.TemporaryResourceCount<>0 then e.Add GitHubQueueSandboxFinding.CleanupIncomplete
        if f.PreVisibility<>"private" || f.FinalVisibility<>f.PreVisibility then e.Add(GitHubQueueSandboxFinding.RollbackMismatch "visibility")
        if not(digest.IsMatch f.PreSettingsDigest) || f.FinalSettingsDigest<>f.PreSettingsDigest then e.Add(GitHubQueueSandboxFinding.RollbackMismatch "settings")
        if not f.Recoverable then e.Add GitHubQueueSandboxFinding.Unrecoverable
        if e.Count>0 then Error(List.ofSeq e) else
        let unsigned={ SchemaVersion=1; Repository=f.Repository; RepositoryId=f.RepositoryId; PilotSeal=f.PilotSeal; CheckpointDigest=f.DurableCheckpointDigest; AppliedEffects=f.AppliedEffects; RetryEffects=f.RetryEffects; Compensations=f.Compensations; FinalVisibility=f.FinalVisibility; FinalSettingsDigest=f.FinalSettingsDigest; Disposition=recoveryDisposition; Seal="" }
        Ok { unsigned with Seal=recoverySeal unsigned }
    let serializeRecovery (r:QueueRecoveryReceipt) =
        let effects (values:QueueEffect list) = values |> List.map (fun effect -> {| operationId=effect.OperationId; attempt=effect.Attempt; resultDigest=effect.ResultDigest |})
        let compensations = r.Compensations |> List.map (fun compensation -> {| operationId=compensation.OperationId; compensationId=compensation.CompensationId; finalStateDigest=compensation.FinalStateDigest |})
        JsonSerializer.Serialize {| schemaVersion = r.SchemaVersion; repository = r.Repository; repositoryId = r.RepositoryId; pilotSeal = r.PilotSeal; checkpointDigest = r.CheckpointDigest; appliedEffects = effects r.AppliedEffects; retryEffects = effects r.RetryEffects; compensations = compensations; finalVisibility = r.FinalVisibility; finalSettingsDigest = r.FinalSettingsDigest; disposition = r.Disposition; seal = r.Seal |}
    let verifyRecovery (expectedSeal:string) (r:QueueRecoveryReceipt) =
        let appliedIds = r.AppliedEffects |> List.map _.OperationId
        let retryIds = r.RetryEffects |> List.map _.OperationId
        let effectValid (effect:QueueEffect) = token.IsMatch effect.OperationId && effect.Attempt>0 && digest.IsMatch effect.ResultDigest
        if r.SchemaVersion<>1 || r.Repository<>repository || r.RepositoryId<>repositoryId || r.Disposition<>recoveryDisposition || not(digest.IsMatch r.PilotSeal) || not(digest.IsMatch r.CheckpointDigest)
           || List.isEmpty appliedIds || appliedIds<>retryIds || appliedIds|>List.distinct|>List.length<>appliedIds.Length
           || not(List.forall effectValid r.AppliedEffects) || not(List.forall effectValid r.RetryEffects)
           || r.AppliedEffects.Length<>r.RetryEffects.Length
           || List.map2 (fun a b -> a.ResultDigest<>b.ResultDigest || b.Attempt<>a.Attempt+1) r.AppliedEffects r.RetryEffects |> List.exists id
           || r.Compensations|>List.map _.OperationId<>List.rev appliedIds
           || (r.Compensations |> List.map _.CompensationId |> List.distinct |> List.length) <> r.Compensations.Length
           || r.Compensations|>List.exists(fun c -> not(token.IsMatch c.OperationId) || not(token.IsMatch c.CompensationId) || not(digest.IsMatch c.FinalStateDigest))
           || r.FinalVisibility<>"private" || not(digest.IsMatch r.FinalSettingsDigest) then Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
        elif r.Seal<>expectedSeal || r.Seal<>recoverySeal r then Error [ GitHubQueueSandboxFinding.AlteredSeal ] else Ok r
    let parseRecovery (value:string) =
        try
            use d = JsonDocument.Parse value
            let x = d.RootElement
            let text (n:string) = x.GetProperty(n).GetString()
            let effects (n:string) = x.GetProperty(n).EnumerateArray() |> Seq.map(fun e -> { OperationId=e.GetProperty("operationId").GetString(); Attempt=e.GetProperty("attempt").GetInt32(); ResultDigest=e.GetProperty("resultDigest").GetString() }) |> Seq.toList
            let compensations = x.GetProperty("compensations").EnumerateArray() |> Seq.map(fun c -> { OperationId=c.GetProperty("operationId").GetString(); CompensationId=c.GetProperty("compensationId").GetString(); FinalStateDigest=c.GetProperty("finalStateDigest").GetString() }) |> Seq.toList
            let r={ SchemaVersion=x.GetProperty("schemaVersion").GetInt32(); Repository=text "repository"; RepositoryId=x.GetProperty("repositoryId").GetInt64(); PilotSeal=text "pilotSeal"; CheckpointDigest=text "checkpointDigest"; AppliedEffects=effects "appliedEffects"; RetryEffects=effects "retryEffects"; Compensations=compensations; FinalVisibility=text "finalVisibility"; FinalSettingsDigest=text "finalSettingsDigest"; Disposition=text "disposition"; Seal=text "seal" }
            match verifyRecovery r.Seal r with
            | Ok receipt when serializeRecovery receipt = value -> Ok receipt
            | Ok _ -> Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
            | Error findings -> Error findings
        with _ -> Error [ GitHubQueueSandboxFinding.InvalidSerialization ]
    let replayRecovery (prior:QueueRecoveryReceipt) (facts:QueueRecoveryFacts) =
        match compileRecovery facts with
        | Ok receipt when serializeRecovery receipt=serializeRecovery prior -> Ok receipt
        | Ok _ -> Error [ GitHubQueueSandboxFinding.ReplayConflict ]
        | Error findings -> Error findings
    let validateControls (expected:string list) (generated:QueueSandboxControlResult list) (independent:QueueSandboxControlResult list) =
        let validate (producer:string) (rows:QueueSandboxControlResult list) =
            [ if rows|>List.map _.ControlId<>expected then $"{producer}: inventory mismatch"
              for r in rows do
                  if not r.ControlPassed then $"{producer}:{r.ControlId}: control did not turn red"
                  if not r.BaselineGreen then $"{producer}:{r.ControlId}: baseline not green" ]
        let errors=validate "generated" generated @ validate "independent" independent
        if errors.IsEmpty then Ok() else Error errors
