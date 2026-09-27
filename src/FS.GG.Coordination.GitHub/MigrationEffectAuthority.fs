namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography

type MigrationEffectBinding =
    { Repository: string
      RepositoryId: int64
      ProjectNodeId: string
      CandidateSha256: Sha256Digest
      ArtifactSha256: Sha256Digest
      ManifestSeal: string
      OperationId: string
      OperationGeneration: int64
      ClaimGeneration: int64 option
      EpochCommit: GitObjectId
      EpochGeneration: int64
      RegistryHead: GitObjectId
      RegistryGeneration: int64
      RecoveryOwner: string }

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

    let private targetMatches (binding: MigrationEffectBinding) effect =
        match effect with
        | MigrationEffect.SetIssueType(repository, _, _)
        | MigrationEffect.SetIssueField(repository, _, _, _)
        | MigrationEffect.AddBlockingEdge(repository, _, _)
        | MigrationEffect.ApplyRepositorySettings(repository, _)
        | MigrationEffect.AdoptReceiver(repository, _, _) -> repository = binding.RepositoryId
        | MigrationEffect.SetProjectField(project, _, _, _) -> project = binding.ProjectNodeId
        | MigrationEffect.SealArchive _ -> false

    let private staticErrors (binding: MigrationEffectBinding) (step: MigrationExecutionStep) effectId (bytes: byte array) =
        [
            if String.IsNullOrWhiteSpace binding.Repository
               || binding.RepositoryId < 1L
               || String.IsNullOrWhiteSpace binding.ProjectNodeId then
                "sandbox-copy-identity"
            if String.IsNullOrWhiteSpace binding.RecoveryOwner then "recovery-owner"
            if String.IsNullOrWhiteSpace binding.OperationId
               || binding.OperationGeneration < 1L then "operation-binding"
            if binding.EpochGeneration < 1L || binding.RegistryGeneration < 1L then
                "authority-generation"
            if String.IsNullOrWhiteSpace binding.ManifestSeal
               || step.ManifestSeal <> binding.ManifestSeal
               || binding.ManifestSeal <> V1AdmissionRegistry.sha256Value binding.CandidateSha256 then
                "manifest-seal"
            if step.OperationId <> binding.OperationId
               || step.AuthorityFence.OperationGeneration <> binding.OperationGeneration then
                "operation-binding"
            if step.EpochGeneration <> binding.EpochGeneration
               || step.EpochCommit <> V1AdmissionRegistry.gitObjectIdValue binding.EpochCommit then
                "epoch-binding"
            if step.AuthorityFence.RegistryCommit <> V1AdmissionRegistry.gitObjectIdValue binding.RegistryHead
               || step.AuthorityFence.AdmissionGeneration <> binding.RegistryGeneration then
                "registry-binding"
            if (step.AuthorityFence.Claim |> Option.map fst) <> binding.ClaimGeneration then
                "claim-binding"
            if not (targetMatches binding step.Effect) then "sandbox-target"
            if String.IsNullOrWhiteSpace effectId then "effect-id"
            if isNull bytes || bytes.Length = 0 then "request-bytes"
            if not (isNull bytes) && bytes.Length > 0
               && ShardedJournalAdapter.sha256 bytes <> V1AdmissionRegistry.sha256Value binding.ArtifactSha256 then
                "artifact-digest"
            match MigrationStepExecution.sealStep step with
            | Ok sealedStep when sealedStep.Seal = step.Seal -> ()
            | _ -> "step-seal"
        ]

    let private readFresh ports binding =
        let observed = ports.Journal.Read address
        if observed.Repository <> "FS-GG/FS.GG.Coordination.Authority"
           || observed.RepositoryId <> 1351660651L
           || observed.Ref <> address.Ref then
            Error [ "admission-journal-identity" ]
        else
            match V1AdmissionRegistry.restore observed, V1AdmissionRegistry.readVerified ports.Authority with
            | Error _, _ -> Error [ "admission-journal-unverified" ]
            | _, Error _ -> Error [ "epoch-authority-unverified" ]
            | Ok registry, Ok snapshot ->
                if V1AdmissionRegistry.head registry <> binding.RegistryHead
                   || V1AdmissionRegistry.generation registry <> binding.RegistryGeneration then
                    Error [ "stale-admission-head" ]
                else Ok(observed, registry, snapshot)

    let prepare ports binding step effectId canonicalRequestBytes =
        try
            match staticErrors binding step effectId canonicalRequestBytes with
            | _ :: _ as errors -> EffectIntentRefused errors
            | [] ->
                match readFresh ports binding with
                | Error errors -> EffectIntentIndeterminate errors
                | Ok(observed, registry, snapshot) ->
                    match V1AdmissionRegistry.recoverOperation binding.OperationId registry with
                    | Error _ -> EffectIntentRefused [ "admission-missing" ]
                    | Ok handle ->
                        let requestDigest =
                            SHA256.HashData canonicalRequestBytes
                            |> Convert.ToHexString
                            |> _.ToLowerInvariant()
                            |> V1AdmissionRegistry.sha256Digest
                            |> Result.defaultWith invalidOp
                        let request =
                            MutationRequest
                                { EffectId = effectId
                                  RequestDigest = requestDigest
                                  CanonicalRequestBytes = Array.copy canonicalRequestBytes
                                  Preconditions =
                                    { ExpectedEpochCommit = binding.EpochCommit
                                      ExpectedEpochGeneration = binding.EpochGeneration
                                      ExpectedClaimGeneration = binding.ClaimGeneration
                                      ExpectedOperationGeneration = binding.OperationGeneration } }
                        match V1AdmissionRegistry.prepareEffect binding.RegistryHead snapshot handle
                                  binding.RecoveryOwner request registry with
                        | EffectRefused errors -> EffectIntentRefused errors
                        | EffectAlreadyInFlight _ -> EffectIntentRefused [ "effect-already-in-flight" ]
                        | EffectAlreadySettled _ -> EffectIntentRefused [ "effect-already-settled" ]
                        | EffectIntentAppended candidate ->
                            let commandId = "migration-effect-" + effectId
                            match V1AdmissionRegistry.planAppend commandId observed candidate with
                            | Error errors -> EffectIntentRefused errors
                            | Ok proposal ->
                                // A changed authority or parent invalidates this append attempt.
                                match readFresh ports binding with
                                | Error errors -> EffectIntentIndeterminate errors
                                | Ok(_, _, freshSnapshot) when freshSnapshot <> snapshot ->
                                    EffectIntentIndeterminate [ "epoch-authority-moved" ]
                                | Ok _ ->
                                    match V1AdmissionRegistry.appendAndReconcile ports.Journal proposal with
                                    | DurableAppendAccepted(durable, Some _) ->
                                        // The registry-created permit is deliberately not returned to
                                        // the migration executor or stored for another effect.
                                        EffectIntentDurable(V1AdmissionRegistry.head durable,
                                                            V1AdmissionRegistry.generation durable)
                                    | DurableAppendAccepted(_, None) ->
                                        EffectIntentIndeterminate [ "effect-permit-unavailable" ]
                                    | DurableAppendParentConflict _ -> EffectIntentParentConflict
                                    | DurableAppendRefused _ -> EffectIntentRefused [ "effect-append-refused" ]
                                    | DurableAppendIndeterminate _ ->
                                        EffectIntentIndeterminate [ "effect-append-ambiguous" ]
        with _ -> EffectIntentIndeterminate [ "effect-authority-port-exception" ]
