namespace FS.GG.Coordination.GitHub

open System

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
    val PlanSchema: string = "fsgg.coordination.github-sandbox-plan/1"
    [<Literal>]
    val ReceiptSchema: string = "fsgg.coordination.github-sandbox-closure/1"
    val requiredSurfaces: string list
    val requiredChildGates: string list
    val sha256: string -> string
    val planDigest: SandboxPlan -> string
    val resultDigest: SandboxClosureReceipt -> string
    val validatePlan: now: DateTimeOffset -> SandboxPlan -> Result<SandboxPlan, SandboxClosureFailure list>
    val validateReceipt: now: DateTimeOffset -> SandboxPlan -> SandboxClosureReceipt -> Result<SandboxClosureReceipt, SandboxClosureFailure list>
