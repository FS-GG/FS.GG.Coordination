#load "../src/FS.GG.Coordination.GitHub/GitHubSandboxClosure.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubSandboxClosureQualification.fs"

open System
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type Control = GitHubSandboxClosureControl
let fail code message = failwith $"{code}: {message}"
let digest value = GitHubSandboxClosure.sha256 value
let now = DateTimeOffset.Parse "2026-09-01T04:00:00Z"
let candidate = String.replicate 64 "a"
let contract = "00ced881abe69940f8b4663014c8fb1dd1f8d8a586302b4bc9685d9a9f2c0e9e"
let nonce = "gs2-04-9-run-0001"
let surfaces = GitHubSandboxClosure.requiredSurfaces
let operations =
    surfaces
    |> List.mapi (fun index surface ->
        { OperationId = $"operation-{index + 1:D2}"
          Surface = surface
          TargetIdentity = $"fsgg-sandbox-target-{index + 1:D2}"
          PreStateDigest = digest $"pre-{surface}"
          ExpectedRevision = $"revision-{index + 1:D2}"
          CompensationId = $"compensate-{index + 1:D2}" })
let plan =
    { Schema = GitHubSandboxClosure.PlanSchema
      ContractDigest = contract
      CandidateSha = candidate
      RunNonce = nonce
      CreatedAt = now.AddMinutes -1.
      ExpiresAt = now.AddMinutes 30.
      Authority =
        { Actor = "fsgg-sandbox-app[bot]"
          ActorId = "A_sandbox_1"
          Credential = NonProductionInstallationToken
          Purpose = "GS2-04.9 isolated qualification"
          Permissions = Set [ "actions:write"; "contents:write"; "issues:write"; "projects:write" ] }
      Targets =
        { Owner = "FS-GG-Sandbox"
          Repository = "gs2-04-9-disposable"
          RepositoryNodeId = "R_sandbox_1"
          ProjectNodeId = "PVT_sandbox_1"
          PurposeMarker = "fsgg-sandbox-gs2-04-9"
          Production = false }
      Quota = { MaximumOperations = 8; MaximumPerSurface = 1; MaximumCreatedResources = 16 }
      Operations = operations
      RequiredChildGates = GitHubSandboxClosure.requiredChildGates }

let effects =
    operations
    |> List.map (fun operation ->
        { OperationId = operation.OperationId
          Disposition = Applied
          AuthoritativeRevision = Some($"observed-{operation.ExpectedRevision}")
          PostStateDigest = Some(digest $"post-{operation.OperationId}")
          ResponseDigest = Some(digest $"response-{operation.OperationId}") })
let cleanup =
    operations
    |> List.rev
    |> List.map (fun operation ->
        { OperationId = operation.OperationId
          CompensationId = operation.CompensationId
          Disposition = Restored
          AuthoritativeRevision = Some($"cleanup-{operation.ExpectedRevision}")
          FinalStateDigest = Some operation.PreStateDigest })
let children =
    GitHubSandboxClosure.requiredChildGates
    |> List.mapi (fun index gate ->
        { GateId = gate
          ProcessId = 1000 + index
          RunNonce = nonce
          CandidateSha = candidate
          ResultDigest = digest $"{gate}-{nonce}-{candidate}"
          Green = true })
let unsignedReceipt =
    { Schema = GitHubSandboxClosure.ReceiptSchema
      PlanDigest = GitHubSandboxClosure.planDigest plan
      ContractDigest = contract
      CandidateSha = candidate
      RunNonce = nonce
      WorkflowRepository = "FS-GG/FS.GG.Coordination"
      WorkflowRunId = "33470000000"
      ArtifactName = $"github-sandbox-closure-{candidate}"
      CreatedAt = now
      ExpiresAt = plan.ExpiresAt
      Effects = effects
      Cleanup = cleanup
      ChildGates = children
      ResultDigest = "" }
let receipt = { unsignedReceipt with ResultDigest = GitHubSandboxClosure.resultDigest unsignedReceipt }
let sign (value: SandboxClosureReceipt) = { value with ResultDigest = GitHubSandboxClosure.resultDigest { value with ResultDigest = "" } }
let baselineGreen () = GitHubSandboxClosure.validateReceipt now plan receipt |> Result.isOk
let result control red = { Control = control; MutationRed = red; BaselineGreen = baselineGreen () }
let planRed changed = GitHubSandboxClosure.validatePlan now changed |> Result.isError
let receiptRed changed = GitHubSandboxClosure.validateReceipt now plan (sign changed) |> Result.isError
let replaceAt index replacement values = values |> List.mapi (fun current value -> if current = index then replacement value else value)

let generated =
    GitHubSandboxClosureQualification.requiredControls
    |> List.map (fun control ->
        let red =
            match control with
            | Control.ProductionIdentity -> planRed { plan with Authority = { plan.Authority with Actor = "EHotwagner" } }
            | Control.ProductionTarget -> planRed { plan with Targets = { plan.Targets with Production = true } }
            | Control.ProductionCredential -> planRed { plan with Authority = { plan.Authority with Credential = ProductionCapableHuman } }
            | Control.Quota -> planRed { plan with Quota = { plan.Quota with MaximumOperations = 7 } }
            | Control.StaleFence -> receiptRed { receipt with Effects = effects |> replaceAt 0 (fun value -> { value with Disposition = Refused }) }
            | Control.ResponseUnknown -> receiptRed { receipt with Effects = effects |> replaceAt 1 (fun value -> { value with Disposition = Ambiguous }) }
            | Control.PartialCleanup -> receiptRed { receipt with Cleanup = cleanup |> replaceAt 0 (fun value -> { value with Disposition = Residual }) }
            | Control.ReceiptSubstitution -> receiptRed { receipt with CandidateSha = String.replicate 64 "b" }
            | Control.WarmReuse -> receiptRed { receipt with ChildGates = children |> replaceAt 1 (fun value -> { value with ProcessId = children.Head.ProcessId }) }
            | Control.OmittedAdapter -> receiptRed { receipt with ChildGates = children |> List.tail }
        result control red)

let independent =
    GitHubSandboxClosureQualification.requiredControls
    |> List.map (fun control ->
        let red =
            match control with
            | Control.ProductionIdentity -> planRed { plan with Authority = { plan.Authority with ActorId = "" } }
            | Control.ProductionTarget -> planRed { plan with Targets = { plan.Targets with PurposeMarker = "production" } }
            | Control.ProductionCredential -> planRed { plan with Authority = { plan.Authority with Credential = UnknownCredential } }
            | Control.Quota -> planRed { plan with Quota = { plan.Quota with MaximumPerSurface = 0 } }
            | Control.StaleFence -> receiptRed { receipt with Effects = effects |> replaceAt 2 (fun value -> { value with AuthoritativeRevision = None }) }
            | Control.ResponseUnknown -> receiptRed { receipt with Effects = effects |> replaceAt 3 (fun value -> { value with Disposition = Unavailable }) }
            | Control.PartialCleanup -> receiptRed { receipt with Cleanup = cleanup |> List.tail }
            | Control.ReceiptSubstitution -> receiptRed { receipt with PlanDigest = digest "other-plan" }
            | Control.WarmReuse -> receiptRed { receipt with ChildGates = children |> replaceAt 2 (fun value -> { value with RunNonce = "prior-run-nonce-0000" }) }
            | Control.OmittedAdapter -> receiptRed { receipt with ChildGates = children |> List.take 7 }
        result control red)

match GitHubSandboxClosureQualification.validate generated independent with
| Ok () -> printfn "github-sandbox-closure-contract OK controls=%d q=Q4 network=offline provenance=synthetic baseline=green" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GSQ-FAILED" $"{findings.Length} finding(s)"
