namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type SandboxCredentialClass =
    | NonProductionApp
    | NonProductionInstallationToken
    | ProductionCapableHuman
    | UnknownCredential

type SandboxAuthority =
    { Actor: string
      ActorId: string
      Credential: SandboxCredentialClass
      Purpose: string
      Permissions: Set<string> }

type SandboxTargets =
    { Owner: string
      Repository: string
      RepositoryNodeId: string
      ProjectNodeId: string
      PurposeMarker: string
      Production: bool }

type SandboxQuota =
    { MaximumOperations: int
      MaximumPerSurface: int
      MaximumCreatedResources: int }

type SandboxOperation =
    { OperationId: string
      Surface: string
      TargetIdentity: string
      PreStateDigest: string
      ExpectedRevision: string
      CompensationId: string }

type SandboxPlan =
    { Schema: string
      ContractDigest: string
      CandidateSha: string
      RunNonce: string
      CreatedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Authority: SandboxAuthority
      Targets: SandboxTargets
      Quota: SandboxQuota
      Operations: SandboxOperation list
      RequiredChildGates: string list }

type EffectDisposition = Applied | Refused | Ambiguous | Unavailable
type CleanupDisposition = Absent | Restored | Residual | CleanupAmbiguous

type SandboxEffectResult =
    { OperationId: string
      Disposition: EffectDisposition
      AuthoritativeRevision: string option
      PostStateDigest: string option
      ResponseDigest: string option }

type SandboxCleanupResult =
    { OperationId: string
      CompensationId: string
      Disposition: CleanupDisposition
      AuthoritativeRevision: string option
      FinalStateDigest: string option }

type SandboxChildGateResult =
    { GateId: string
      ProcessId: int
      RunNonce: string
      CandidateSha: string
      ResultDigest: string
      Green: bool }

type SandboxClosureReceipt =
    { Schema: string
      PlanDigest: string
      ContractDigest: string
      CandidateSha: string
      RunNonce: string
      WorkflowRepository: string
      WorkflowRunId: string
      ArtifactName: string
      CreatedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Effects: SandboxEffectResult list
      Cleanup: SandboxCleanupResult list
      ChildGates: SandboxChildGateResult list
      ResultDigest: string }

type SandboxClosureFailure =
    | InvalidSchema
    | InvalidContractDigest
    | InvalidCandidate
    | InvalidRunNonce
    | ExpiredPlan
    | ProductionAuthority
    | UnknownAuthority
    | InvalidSandboxTarget
    | InvalidQuota
    | MissingSurface of string
    | DuplicateOperation of string
    | InvalidOperation of string
    | SecretMaterialForbidden
    | ReceiptBindingMismatch of string
    | ChildGateInventoryMismatch
    | ChildGateNotCold of string
    | ChildGateNotGreen of string
    | EffectNotAuthoritativelyObserved of string
    | CleanupNotProven of string
    | InvalidResultDigest

[<RequireQualifiedAccess>]
module GitHubSandboxClosure =
    [<Literal>]
    let PlanSchema = "fsgg.coordination.github-sandbox-plan/1"
    [<Literal>]
    let ReceiptSchema = "fsgg.coordination.github-sandbox-closure/1"

    let requiredSurfaces =
        [ "transport"; "issue-field"; "native-relation"; "project"; "comment-projection"; "sharded-journal"; "repository-settings"; "actions-release-feed" ]

    let requiredChildGates =
        requiredSurfaces |> List.map (fun surface -> "github-" + surface + "-contract")

    let sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private digestValue value =
        let text = if isNull value then "" else value
        $"{Encoding.UTF8.GetByteCount text}:{text}"

    let private credentialId = function
        | NonProductionApp -> "non-production-app"
        | NonProductionInstallationToken -> "non-production-installation-token"
        | ProductionCapableHuman -> "production-capable-human"
        | UnknownCredential -> "unknown"

    let private effectId = function Applied -> "applied" | Refused -> "refused" | Ambiguous -> "ambiguous" | Unavailable -> "unavailable"
    let private cleanupId = function Absent -> "absent" | Restored -> "restored" | Residual -> "residual" | CleanupAmbiguous -> "ambiguous"
    let private optionValue = Option.defaultValue ""
    let private join values = values |> Seq.map digestValue |> String.concat "|"
    let private validDigest (value: string) = value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let private validCandidate (value: string) = (value.Length = 40 || value.Length = 64) && value |> Seq.forall Uri.IsHexDigit
    let private hasSecretName (value: string) =
        let lowered = value.ToLowerInvariant()
        [ "token"; "secret"; "password"; "authorization"; "bearer" ] |> List.exists lowered.Contains

    let planDigest (plan: SandboxPlan) =
        let authority =
            [ plan.Authority.Actor; plan.Authority.ActorId; credentialId plan.Authority.Credential; plan.Authority.Purpose
              yield! plan.Authority.Permissions |> Set.toList |> List.sort ]
        let targets = [ plan.Targets.Owner; plan.Targets.Repository; plan.Targets.RepositoryNodeId; plan.Targets.ProjectNodeId; plan.Targets.PurposeMarker; string plan.Targets.Production ]
        let quota = [ string plan.Quota.MaximumOperations; string plan.Quota.MaximumPerSurface; string plan.Quota.MaximumCreatedResources ]
        let operations =
            plan.Operations
            |> List.map (fun operation -> join [ operation.OperationId; operation.Surface; operation.TargetIdentity; operation.PreStateDigest; operation.ExpectedRevision; operation.CompensationId ])
        [ plan.Schema; plan.ContractDigest; plan.CandidateSha; plan.RunNonce; plan.CreatedAt.ToUniversalTime().ToString("O"); plan.ExpiresAt.ToUniversalTime().ToString("O")
          join authority; join targets; join quota; yield! operations; yield! plan.RequiredChildGates ]
        |> join |> sha256

    let resultDigest (receipt: SandboxClosureReceipt) =
        let effects =
            receipt.Effects
            |> List.map (fun result -> join [ result.OperationId; effectId result.Disposition; result.AuthoritativeRevision |> optionValue; result.PostStateDigest |> optionValue; result.ResponseDigest |> optionValue ])
        let cleanup =
            receipt.Cleanup
            |> List.map (fun result -> join [ result.OperationId; result.CompensationId; cleanupId result.Disposition; result.AuthoritativeRevision |> optionValue; result.FinalStateDigest |> optionValue ])
        let children =
            receipt.ChildGates
            |> List.map (fun result -> join [ result.GateId; string result.ProcessId; result.RunNonce; result.CandidateSha; result.ResultDigest; string result.Green ])
        [ receipt.Schema; receipt.PlanDigest; receipt.ContractDigest; receipt.CandidateSha; receipt.RunNonce; receipt.WorkflowRepository; receipt.WorkflowRunId; receipt.ArtifactName
          receipt.CreatedAt.ToUniversalTime().ToString("O"); receipt.ExpiresAt.ToUniversalTime().ToString("O"); yield! effects; yield! cleanup; yield! children ]
        |> join |> sha256

    let validatePlan (now: DateTimeOffset) (plan: SandboxPlan) =
        let duplicateOperations = plan.Operations |> List.countBy _.OperationId |> List.choose (fun (id, count) -> if count > 1 then Some id else None)
        let observedSurfaces = plan.Operations |> List.map _.Surface |> Set.ofList
        let findings =
            [ if plan.Schema <> PlanSchema then InvalidSchema
              if not (validDigest plan.ContractDigest) then InvalidContractDigest
              if not (validCandidate plan.CandidateSha) then InvalidCandidate
              if String.IsNullOrWhiteSpace plan.RunNonce || plan.RunNonce.Length < 16 then InvalidRunNonce
              if plan.CreatedAt >= plan.ExpiresAt || now < plan.CreatedAt || now >= plan.ExpiresAt || plan.ExpiresAt - plan.CreatedAt > TimeSpan.FromHours 2. then ExpiredPlan
              match plan.Authority.Credential with
              | ProductionCapableHuman -> ProductionAuthority
              | UnknownCredential -> UnknownAuthority
              | _ -> ()
              if String.IsNullOrWhiteSpace plan.Authority.Actor || String.IsNullOrWhiteSpace plan.Authority.ActorId || plan.Authority.Actor.Equals("EHotwagner", StringComparison.OrdinalIgnoreCase) then ProductionAuthority
              if String.IsNullOrWhiteSpace plan.Authority.Purpose || plan.Authority.Permissions.IsEmpty then UnknownAuthority
              if [ plan.Authority.Actor; plan.Authority.ActorId; plan.Authority.Purpose; plan.Targets.Owner; plan.Targets.Repository; plan.Targets.RepositoryNodeId; plan.Targets.ProjectNodeId; plan.Targets.PurposeMarker
                   yield! plan.Authority.Permissions |> Set.toList ] |> List.exists hasSecretName then SecretMaterialForbidden
              if plan.Targets.Production || String.IsNullOrWhiteSpace plan.Targets.RepositoryNodeId || String.IsNullOrWhiteSpace plan.Targets.ProjectNodeId || not (plan.Targets.PurposeMarker.StartsWith("fsgg-sandbox-", StringComparison.Ordinal)) then InvalidSandboxTarget
              if plan.Quota.MaximumOperations <= 0 || plan.Quota.MaximumPerSurface <= 0 || plan.Quota.MaximumCreatedResources <= 0 || plan.Operations.Length > plan.Quota.MaximumOperations then InvalidQuota
              for surface in requiredSurfaces do if not (observedSurfaces.Contains surface) then MissingSurface surface
              for id in duplicateOperations do DuplicateOperation id
              for operation in plan.Operations do
                  if String.IsNullOrWhiteSpace operation.OperationId || not (requiredSurfaces |> List.contains operation.Surface) || String.IsNullOrWhiteSpace operation.TargetIdentity || not (validDigest operation.PreStateDigest) || String.IsNullOrWhiteSpace operation.ExpectedRevision || String.IsNullOrWhiteSpace operation.CompensationId then InvalidOperation operation.OperationId
                  if [ operation.OperationId; operation.TargetIdentity; operation.ExpectedRevision; operation.CompensationId ] |> List.exists hasSecretName then SecretMaterialForbidden
              if plan.Operations |> List.countBy _.Surface |> List.exists (fun (_, count) -> count > plan.Quota.MaximumPerSurface) then InvalidQuota
              if plan.RequiredChildGates <> requiredChildGates then ChildGateInventoryMismatch ]
            |> List.distinct
        if findings.IsEmpty then Ok plan else Error findings

    let validateReceipt (now: DateTimeOffset) (plan: SandboxPlan) (receipt: SandboxClosureReceipt) =
        match validatePlan now plan with
        | Error errors -> Error errors
        | Ok _ ->
            let operationById = plan.Operations |> List.map (fun value -> value.OperationId, value) |> Map.ofList
            let childIds = receipt.ChildGates |> List.map _.GateId
            let processIds = receipt.ChildGates |> List.map _.ProcessId
            let findings =
                [ if receipt.Schema <> ReceiptSchema then InvalidSchema
                  if receipt.PlanDigest <> planDigest plan then ReceiptBindingMismatch "plan-digest"
                  if receipt.ContractDigest <> plan.ContractDigest then ReceiptBindingMismatch "contract-digest"
                  if receipt.CandidateSha <> plan.CandidateSha then ReceiptBindingMismatch "candidate"
                  if receipt.RunNonce <> plan.RunNonce then ReceiptBindingMismatch "run-nonce"
                  if receipt.CreatedAt < plan.CreatedAt || receipt.ExpiresAt <> plan.ExpiresAt || now >= receipt.ExpiresAt then ReceiptBindingMismatch "time-bound"
                  if String.IsNullOrWhiteSpace receipt.WorkflowRepository || String.IsNullOrWhiteSpace receipt.WorkflowRunId || String.IsNullOrWhiteSpace receipt.ArtifactName then ReceiptBindingMismatch "immutable-evidence"
                  if childIds <> requiredChildGates || childIds |> List.distinct |> List.length <> requiredChildGates.Length then ChildGateInventoryMismatch
                  for child in receipt.ChildGates do
                      if child.ProcessId <= 0 || processIds |> List.filter ((=) child.ProcessId) |> List.length <> 1 || child.RunNonce <> plan.RunNonce || child.CandidateSha <> plan.CandidateSha || not (validDigest child.ResultDigest) then ChildGateNotCold child.GateId
                      if not child.Green then ChildGateNotGreen child.GateId
                  if (receipt.Effects |> List.map _.OperationId) <> (plan.Operations |> List.map _.OperationId) then ReceiptBindingMismatch "effect-inventory"
                  for effect in receipt.Effects do
                      match effect.Disposition, effect.AuthoritativeRevision, effect.PostStateDigest with
                      | Applied, Some revision, Some digest when not (String.IsNullOrWhiteSpace revision) && validDigest digest -> ()
                      | _ -> EffectNotAuthoritativelyObserved effect.OperationId
                  let expectedCleanup = plan.Operations |> List.rev
                  if (receipt.Cleanup |> List.map _.OperationId) <> (expectedCleanup |> List.map _.OperationId) then ReceiptBindingMismatch "cleanup-order"
                  for cleanup in receipt.Cleanup do
                      match operationById |> Map.tryFind cleanup.OperationId with
                      | None -> CleanupNotProven cleanup.OperationId
                      | Some operation when operation.CompensationId <> cleanup.CompensationId -> CleanupNotProven cleanup.OperationId
                      | Some _ ->
                          match cleanup.Disposition, cleanup.AuthoritativeRevision, cleanup.FinalStateDigest with
                          | (Absent | Restored), Some revision, Some digest when not (String.IsNullOrWhiteSpace revision) && validDigest digest -> ()
                          | _ -> CleanupNotProven cleanup.OperationId
                  if receipt.ResultDigest <> resultDigest { receipt with ResultDigest = "" } then InvalidResultDigest ]
                |> List.distinct
            if findings.IsEmpty then Ok receipt else Error findings
