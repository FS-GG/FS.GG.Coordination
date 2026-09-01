module FS.GG.Coordination.GitHubSandboxClosureTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private now = DateTimeOffset.Parse "2026-09-01T04:00:00Z"
let private candidate = String.replicate 64 "a"
let private digest = GitHubSandboxClosure.sha256
let private operations =
    GitHubSandboxClosure.requiredSurfaces
    |> List.mapi (fun index surface ->
        { OperationId = $"operation-{index}"
          Surface = surface
          TargetIdentity = $"sandbox-{index}"
          PreStateDigest = digest $"pre-{index}"
          ExpectedRevision = $"revision-{index}"
          CompensationId = $"compensate-{index}" })
let private plan =
    { Schema = GitHubSandboxClosure.PlanSchema
      ContractDigest = "00ced881abe69940f8b4663014c8fb1dd1f8d8a586302b4bc9685d9a9f2c0e9e"
      CandidateSha = candidate
      RunNonce = "unit-test-nonce-0001"
      CreatedAt = now.AddMinutes -1.
      ExpiresAt = now.AddMinutes 20.
      Authority = { Actor = "sandbox-app[bot]"; ActorId = "A_sandbox"; Credential = NonProductionApp; Purpose = "isolated qualification"; Permissions = Set [ "contents:write" ] }
      Targets = { Owner = "FS-GG-Sandbox"; Repository = "disposable"; RepositoryNodeId = "R_sandbox"; ProjectNodeId = "PVT_sandbox"; PurposeMarker = "fsgg-sandbox-unit"; Production = false }
      Quota = { MaximumOperations = 8; MaximumPerSurface = 1; MaximumCreatedResources = 16 }
      Operations = operations
      RequiredChildGates = GitHubSandboxClosure.requiredChildGates }
let private effects disposition =
    operations |> List.map (fun operation -> { OperationId = operation.OperationId; Disposition = disposition; AuthoritativeRevision = Some operation.ExpectedRevision; PostStateDigest = Some(digest $"post-{operation.OperationId}"); ResponseDigest = Some(digest $"response-{operation.OperationId}") })
let private cleanup disposition =
    operations |> List.rev |> List.map (fun operation -> { OperationId = operation.OperationId; CompensationId = operation.CompensationId; Disposition = disposition; AuthoritativeRevision = Some operation.ExpectedRevision; FinalStateDigest = Some operation.PreStateDigest })
let private children =
    GitHubSandboxClosure.requiredChildGates |> List.mapi (fun index gate -> { GateId = gate; ProcessId = index + 100; RunNonce = plan.RunNonce; CandidateSha = candidate; ResultDigest = digest gate; Green = true })
let private receipt () =
    let unsigned =
        { Schema = GitHubSandboxClosure.ReceiptSchema; PlanDigest = GitHubSandboxClosure.planDigest plan; ContractDigest = plan.ContractDigest; CandidateSha = candidate; RunNonce = plan.RunNonce
          WorkflowRepository = "FS-GG/FS.GG.Coordination"; WorkflowRunId = "1"; ArtifactName = "github-sandbox-closure"; CreatedAt = now; ExpiresAt = plan.ExpiresAt
          Effects = effects Applied; Cleanup = cleanup Restored; ChildGates = children; ResultDigest = "" }
    { unsigned with ResultDigest = GitHubSandboxClosure.resultDigest unsigned }
let private sign (value: SandboxClosureReceipt) = { value with ResultDigest = GitHubSandboxClosure.resultDigest { value with ResultDigest = "" } }

[<Fact>]
let exactPlanAndClosureValidate () =
    Assert.Equal(Ok plan, GitHubSandboxClosure.validatePlan now plan)
    let value = receipt ()
    Assert.Equal(Ok value, GitHubSandboxClosure.validateReceipt now plan value)

[<Fact>]
let productionOrUnknownAuthorityFailsBeforeEffects () =
    let production = { plan with Authority = { plan.Authority with Credential = ProductionCapableHuman } }
    let unknown = { plan with Authority = { plan.Authority with Credential = UnknownCredential } }
    match GitHubSandboxClosure.validatePlan now production with
    | Error values -> Assert.Contains(ProductionAuthority, values)
    | Ok _ -> failwith "production authority was accepted"
    match GitHubSandboxClosure.validatePlan now unknown with
    | Error values -> Assert.Contains(UnknownAuthority, values)
    | Ok _ -> failwith "unknown authority was accepted"

[<Fact>]
let ambiguousEffectResidualCleanupAndWarmReuseFailIndependently () =
    let baseline = receipt ()
    let firstEffect = baseline.Effects.Head
    let ambiguous = sign { baseline with Effects = { firstEffect with Disposition = Ambiguous } :: baseline.Effects.Tail }
    match GitHubSandboxClosure.validateReceipt now plan ambiguous with
    | Error values -> Assert.Contains(EffectNotAuthoritativelyObserved firstEffect.OperationId, values)
    | Ok _ -> failwith "ambiguous effect was accepted"
    let firstCleanup = baseline.Cleanup.Head
    let residual = sign { baseline with Cleanup = { firstCleanup with Disposition = Residual } :: baseline.Cleanup.Tail }
    match GitHubSandboxClosure.validateReceipt now plan residual with
    | Error values -> Assert.Contains(CleanupNotProven firstCleanup.OperationId, values)
    | Ok _ -> failwith "residual cleanup was accepted"
    let repeated = sign { baseline with ChildGates = { baseline.ChildGates.[1] with ProcessId = baseline.ChildGates.Head.ProcessId } :: baseline.ChildGates.Tail }
    Assert.True(GitHubSandboxClosure.validateReceipt now plan repeated |> Result.isError)

[<Fact>]
let qualificationInventoryRequiresEveryIndependentControl () =
    let passing: GitHubSandboxClosureControlResult list = GitHubSandboxClosureQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubSandboxClosureQualification.validate passing passing)
    Assert.True(GitHubSandboxClosureQualification.validate passing passing.Tail |> Result.isError)
