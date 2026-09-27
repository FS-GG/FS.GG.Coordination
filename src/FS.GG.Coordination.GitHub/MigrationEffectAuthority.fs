namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text.Json

type MigrationVerifiedEffectSelection =
    { Repository: string
      RepositoryId: int64
      ProjectNodeId: string
      CandidateSha256: Sha256Digest
      ArtifactSha256: Sha256Digest
      RecoveryOwner: string
      AdmissionCommit: GitObjectId
      AdmissionGeneration: int64
      OperationCommit: GitObjectId
      OperationGeneration: int64
      SealCommit: GitObjectId }

type MigrationEffectAuthorityPorts =
    { Admission: AdmissionServicePorts
      ReadVerifiedSelection: unit -> Result<MigrationVerifiedEffectSelection, string list> }

type MigrationEffectAuthorityDecision =
    | EffectIntentDurable of head:GitObjectId * generation:int64
    | EffectIntentParentConflict
    | EffectIntentRefused of string list
    | EffectIntentIndeterminate of string list

[<RequireQualifiedAccess>]
module MigrationEffectAuthority =
    let private address =
        ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
        |> Result.defaultWith (string >> invalidOp)

    let private oid = V1AdmissionRegistry.gitObjectIdValue
    let private digest = V1AdmissionRegistry.sha256Value

    let private targetMatches (selection: MigrationVerifiedEffectSelection) effect =
        match effect with
        | MigrationEffect.SetIssueType(repository, _, _)
        | MigrationEffect.SetIssueField(repository, _, _, _)
        | MigrationEffect.AddBlockingEdge(repository, _, _)
        | MigrationEffect.ApplyRepositorySettings(repository, _)
        | MigrationEffect.AdoptReceiver(repository, _, _) -> repository = selection.RepositoryId
        | MigrationEffect.SetProjectField(project, _, _, _) -> project = selection.ProjectNodeId
        | MigrationEffect.SealArchive _ -> false

    let private effectFields = function
        | MigrationEffect.SetIssueType(repo, issue, kind) ->
            [| "set-issue-type"; string repo; issue; kind |]
        | MigrationEffect.SetIssueField(repo, issue, field, value) ->
            [| "set-issue-field"; string repo; issue; field; value |]
        | MigrationEffect.AddBlockingEdge(repo, blocker, blocked) ->
            [| "add-blocking-edge"; string repo; blocker; blocked |]
        | MigrationEffect.SetProjectField(project, item, field, value) ->
            [| "set-project-field"; project; item; field; value |]
        | MigrationEffect.ApplyRepositorySettings(repo, settings) ->
            [| "apply-repository-settings"; string repo; settings |]
        | MigrationEffect.AdoptReceiver(repo, expected, desired) ->
            [| "adopt-receiver"; string repo; expected; desired |]
        | MigrationEffect.SealArchive(authority, archive, verifier) ->
            [| "seal-archive"; authority; archive; verifier |]

    let canonicalRequest (selection: MigrationVerifiedEffectSelection) (step: MigrationExecutionStep) =
        let fence = step.AuthorityFence
        let claim =
            match fence.Claim with
            | None -> [| "none"; ""; "" |]
            | Some(generation, commit) -> [| "typed"; string generation; commit |]
        Array.concat
            [ [| "fsgg.migration-effect-request/1"; selection.Repository
                 string selection.RepositoryId; selection.ProjectNodeId
                 digest selection.CandidateSha256
                 selection.RecoveryOwner; oid selection.AdmissionCommit
                 string selection.AdmissionGeneration; oid selection.OperationCommit
                 string selection.OperationGeneration; oid selection.SealCommit
                 step.OperationId; step.IdempotencyKey; step.ManifestSeal; step.Seal
                 step.TargetIdentity; step.ExpectedTargetRevision
                 step.ExpectedTargetSha256; step.DesiredTargetSha256
                 string step.EpochGeneration; step.EpochCommit
                 string step.JournalGeneration; step.JournalHead
                 string fence.AdmissionGeneration; fence.AdmissionCommit
                 string fence.OperationGeneration; fence.OperationCommit
                 fence.SealCommit; fence.RegistryCommit |]
              claim; effectFields step.Effect ]
        |> JsonSerializer.SerializeToUtf8Bytes

    let private selectionErrors (selection: MigrationVerifiedEffectSelection) (step: MigrationExecutionStep) =
        let fence = step.AuthorityFence
        let commits = [ selection.AdmissionCommit; selection.OperationCommit; selection.SealCommit ]
        [
            if String.IsNullOrWhiteSpace selection.Repository || selection.RepositoryId < 1L
               || String.IsNullOrWhiteSpace selection.ProjectNodeId then "sandbox-copy-identity"
            if String.IsNullOrWhiteSpace selection.RecoveryOwner then "recovery-owner"
            if selection.AdmissionGeneration < 1L || selection.OperationGeneration < 1L then
                "authority-generation"
            if commits |> List.distinct |> List.length <> 3 then "authority-commit-alias"
            if step.ManifestSeal <> digest selection.CandidateSha256 then "manifest-seal"
            if fence.AdmissionGeneration <> selection.AdmissionGeneration
               || fence.AdmissionCommit <> oid selection.AdmissionCommit then "admission-binding"
            if fence.OperationGeneration <> selection.OperationGeneration
               || fence.OperationCommit <> oid selection.OperationCommit then "operation-binding"
            if fence.SealCommit <> oid selection.SealCommit then "seal-binding"
            if not (targetMatches selection step.Effect) then "sandbox-target"
            match MigrationStepExecution.sealStep step with
            | Ok sealedStep when sealedStep.Seal = step.Seal -> ()
            | _ -> "step-seal"
        ]

    let private readFresh (ports: AdmissionServicePorts) =
        let observed = ports.Journal.Read address
        if observed.Repository <> "FS-GG/FS.GG.Coordination.Authority"
           || observed.RepositoryId <> 1351660651L
           || observed.Ref <> address.Ref then
            Error [ "admission-journal-identity" ]
        else
            match V1AdmissionRegistry.restore observed, V1AdmissionRegistry.readVerified ports.Authority with
            | Error _, _ -> Error [ "admission-journal-unverified" ]
            | _, Error _ -> Error [ "epoch-authority-unverified" ]
            | Ok registry, Ok snapshot -> Ok(observed, registry, snapshot)

    let private admissionErrors selection step registry snapshot handle =
        let context = V1AdmissionRegistry.operationContext handle
        let epochCommit, epochGeneration, epochManifest = V1AdmissionRegistry.authorityCoordinates snapshot
        let fence = step.AuthorityFence
        let expectedTarget = $"repository:{selection.RepositoryId}/name:{selection.Repository}/project:{selection.ProjectNodeId}"
        [
            if context.Manifest <> selection.CandidateSha256
               || epochManifest <> selection.CandidateSha256 then
                "candidate-admission-binding"
            if context.CanonicalTarget <> expectedTarget then "copy-admission-binding"
            if context.Actor <> selection.RecoveryOwner then "recovery-owner-admission-binding"
            if context.OperationId <> step.OperationId
               || context.OperationGeneration <> selection.OperationGeneration then
                "operation-admission-binding"
            if context.OriginatingEpochCommit <> epochCommit
               || context.OriginatingEpochGeneration <> epochGeneration
               || step.EpochCommit <> oid context.OriginatingEpochCommit
               || step.EpochGeneration <> context.OriginatingEpochGeneration then
                "epoch-admission-binding"
            if fence.RegistryCommit <> oid (V1AdmissionRegistry.head registry) then
                "registry-binding"
            match context.Claim, fence.Claim with
            | NoClaimRequired, None -> ()
            | TypedClaim(_, generation), Some(actual, _) when generation = actual -> ()
            | _ -> "claim-admission-binding"
        ]

    let prepare (ports: MigrationEffectAuthorityPorts) step =
        try
            match ports.ReadVerifiedSelection() with
            | Error errors -> EffectIntentIndeterminate("selection-unverified" :: errors)
            | Ok selection ->
                match selectionErrors selection step with
                | _ :: _ as errors -> EffectIntentRefused errors
                | [] ->
                    match readFresh ports.Admission with
                    | Error errors -> EffectIntentIndeterminate errors
                    | Ok(observed, registry, snapshot) ->
                        match V1AdmissionRegistry.recoverOperation step.OperationId registry with
                        | Error _ -> EffectIntentRefused [ "admission-missing" ]
                        | Ok handle ->
                            let context = V1AdmissionRegistry.operationContext handle
                            let fence = step.AuthorityFence
                            match admissionErrors selection step registry snapshot handle with
                            | _ :: _ as errors -> EffectIntentRefused errors
                            | [] ->
                                let bytes = canonicalRequest selection step
                                let requestDigest =
                                    SHA256.HashData bytes |> Convert.ToHexString
                                    |> _.ToLowerInvariant() |> V1AdmissionRegistry.sha256Digest
                                    |> Result.defaultWith invalidOp
                                if requestDigest <> selection.ArtifactSha256 then
                                    EffectIntentRefused [ "artifact-digest" ]
                                else
                                    let request =
                                        MutationRequest
                                            { EffectId = step.IdempotencyKey
                                              RequestDigest = requestDigest
                                              CanonicalRequestBytes = bytes
                                              Preconditions =
                                                { ExpectedEpochCommit = context.OriginatingEpochCommit
                                                  ExpectedEpochGeneration = context.OriginatingEpochGeneration
                                                  ExpectedClaimGeneration = fence.Claim |> Option.map fst
                                                  ExpectedOperationGeneration = context.OperationGeneration } }
                                    match V1AdmissionRegistry.prepareEffect (V1AdmissionRegistry.head registry)
                                              snapshot handle selection.RecoveryOwner request registry with
                                    | EffectDecision.EffectRefused errors -> EffectIntentRefused errors
                                    | EffectDecision.EffectAlreadyInFlight _ -> EffectIntentRefused [ "effect-already-in-flight" ]
                                    | EffectDecision.EffectAlreadySettled _ -> EffectIntentRefused [ "effect-already-settled" ]
                                    | EffectDecision.EffectIntentAppended candidate ->
                                        let commandId = "migration-effect-" + step.IdempotencyKey
                                        match V1AdmissionRegistry.planAppend commandId observed candidate with
                                        | Error errors -> EffectIntentRefused errors
                                        | Ok proposal ->
                                            match ports.ReadVerifiedSelection(), readFresh ports.Admission with
                                            | Ok freshSelection, Ok(_, freshRegistry, freshSnapshot)
                                                when freshSelection = selection && freshSnapshot = snapshot
                                                     && V1AdmissionRegistry.head freshRegistry = V1AdmissionRegistry.head registry ->
                                                match V1AdmissionRegistry.appendAndReconcile ports.Admission.Journal proposal with
                                                | DurableAppendAccepted(durable, Some _) ->
                                                    EffectIntentDurable(V1AdmissionRegistry.head durable,
                                                                        V1AdmissionRegistry.generation durable)
                                                | DurableAppendAccepted(_, None) ->
                                                    EffectIntentIndeterminate [ "effect-permit-unavailable" ]
                                                | DurableAppendParentConflict _ -> EffectIntentParentConflict
                                                | DurableAppendRefused _ -> EffectIntentRefused [ "effect-append-refused" ]
                                                | DurableAppendIndeterminate _ ->
                                                    EffectIntentIndeterminate [ "effect-append-ambiguous" ]
                                            | _ -> EffectIntentIndeterminate [ "effect-authority-moved" ]
        with _ -> EffectIntentIndeterminate [ "effect-authority-port-exception" ]
