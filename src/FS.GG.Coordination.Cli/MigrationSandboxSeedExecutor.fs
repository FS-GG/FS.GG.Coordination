namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type MigrationSandboxSeedEffectKind =
    | CreateNonceIssue
    | AddProjectMembership
    | RemoveProjectMembership
    | DeleteNonceIssue

type MigrationSandboxSeedPrestate =
    {
        Complete: bool
        RepositoryId: int64
        ProjectNodeId: string
        NonceIssueCount: int
        NonceProjectItemCount: int
        SnapshotSha256: string
    }

type MigrationSandboxSeedExecutionBinding =
    {
        Request: MigrationSandboxSeedRequest
        WorkflowPath: string
        WorkflowRef: string
        WorkflowSha: string
        RepositoryId: int64
        RepositoryNodeId: string
        ProjectNodeId: string
        MintProofSha256: string
        ProtectedHostReceiptSha256: string
        SeedPlanSha256: string
        CorpusSha256: string
        Prestate: MigrationSandboxSeedPrestate
        AdmittedEffects: MigrationSandboxSeedEffectKind list
        Seal: string
    }

type MigrationSandboxOwnedResource =
    {
        EffectId: string
        Kind: string
        ResourceId: string
        ParentResourceId: string option
        RunNonce: string
        ReadbackSha256: string
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedEffectStage =
    | Planned
    | IntentPersisted
    | InFlight
    | RecoveryPending
    | Settled

type MigrationSandboxSeedEffectState =
    {
        EffectId: string
        IdempotencyKey: string
        Kind: MigrationSandboxSeedEffectKind
        OriginalEffectId: string option
        Stage: MigrationSandboxSeedEffectStage
        Ownership: MigrationSandboxOwnedResource option
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedExecutionMode =
    | Forward
    | Compensation
    | Complete
    | Compensated

type MigrationSandboxSeedExecution =
    {
        Binding: MigrationSandboxSeedExecutionBinding
        Mode: MigrationSandboxSeedExecutionMode
        Effects: MigrationSandboxSeedEffectState list
        ActiveIndex: int
        Generation: int64
        Head: string
    }

type MigrationSandboxSeedCas =
    {
        ExpectedGeneration: int64
        ExpectedHead: string
        NextHead: string
    }

[<RequireQualifiedAccess>]
type MigrationSandboxSeedAction =
    | PersistIntent of effectId: string
    | MarkInFlight of effectId: string
    | RecordResponseUnknown of effectId: string
    | SettleApplied of effectId: string * ownership: MigrationSandboxOwnedResource
    | SettleAbsent of effectId: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedExecutorResult =
    | Advanced
    | Pending of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedExecutorFailure =
    | InvalidBinding
    | UnsafePrestate
    | StaleCas
    | WrongEffect
    | InvalidTransition
    | ForeignOwnership

[<RequireQualifiedAccess>]
module MigrationSandboxSeedExecutor =
    let private shaBytes (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private shaText (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private hex n (value: string) =
        not (isNull value)
        && value.Length = n
        && value |> Seq.forall Uri.IsHexDigit
        && value = value.ToLowerInvariant()

    let private text (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private requestValid request =
        hex 40 request.CandidateSha
        && hex 64 request.CorpusSha256
        && request.WorkflowRunId > 0L
        && request.WorkflowRunAttempt > 0
        && request.RunNonce = $"{request.WorkflowRunId}-{request.WorkflowRunAttempt}-{request.CandidateSha}"

    let private frame (value: string) =
        $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private bindingSeal (b: MigrationSandboxSeedExecutionBinding) =
        [
            b.Request.CandidateSha
            string b.Request.WorkflowRunId
            string b.Request.WorkflowRunAttempt
            b.Request.RunNonce
            b.WorkflowPath
            b.WorkflowRef
            b.WorkflowSha
            string b.RepositoryId
            b.RepositoryNodeId
            b.ProjectNodeId
            b.MintProofSha256
            b.ProtectedHostReceiptSha256
            b.SeedPlanSha256
            b.CorpusSha256
            b.Prestate.SnapshotSha256
            string b.Prestate.NonceIssueCount
            string b.Prestate.NonceProjectItemCount
            yield! b.AdmittedEffects |> List.map string
        ]
        |> List.map frame
        |> String.concat ""
        |> shaText

    let private shape (b: MigrationSandboxSeedExecutionBinding) =
        requestValid b.Request
        && b.RepositoryId = 1353050537L
        && b.RepositoryNodeId = "R_kgDOUKXpqQ"
        && b.ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
        && b.WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
        && b.WorkflowRef = "refs/heads/main"
        && hex 40 b.WorkflowSha
        && ([
                b.MintProofSha256
                b.ProtectedHostReceiptSha256
                b.SeedPlanSha256
                b.CorpusSha256
                b.Prestate.SnapshotSha256
            ]
            |> List.forall (hex 64))
        && b.CorpusSha256 = b.Request.CorpusSha256
        && b.Prestate.Complete
        && b.Prestate.RepositoryId = b.RepositoryId
        && b.Prestate.ProjectNodeId = b.ProjectNodeId
        && b.Prestate.NonceIssueCount = 0
        && b.Prestate.NonceProjectItemCount = 0
        && b.AdmittedEffects =
            [
                MigrationSandboxSeedEffectKind.CreateNonceIssue
                MigrationSandboxSeedEffectKind.AddProjectMembership
                MigrationSandboxSeedEffectKind.RemoveProjectMembership
                MigrationSandboxSeedEffectKind.DeleteNonceIssue
            ]

    let sealBinding
        (mintProofBytes: ReadOnlyMemory<byte>)
        (protectedHostReceiptBytes: ReadOnlyMemory<byte>)
        (seedPlanBytes: ReadOnlyMemory<byte>)
        (corpusBytes: ReadOnlyMemory<byte>)
        (binding: MigrationSandboxSeedExecutionBinding)
        =
        if
            mintProofBytes.IsEmpty
            || protectedHostReceiptBytes.IsEmpty
            || seedPlanBytes.IsEmpty
            || corpusBytes.IsEmpty
            || not (shape binding)
            || shaBytes mintProofBytes <> binding.MintProofSha256
            || shaBytes protectedHostReceiptBytes <> binding.ProtectedHostReceiptSha256
            || shaBytes seedPlanBytes <> binding.SeedPlanSha256
            || shaBytes corpusBytes <> binding.CorpusSha256
        then
            Error MigrationSandboxSeedExecutorFailure.InvalidBinding
        else
            Ok
                { binding with
                    Seal = bindingSeal binding
                }

    let private effect
        (binding: MigrationSandboxSeedExecutionBinding)
        (index: int)
        (kind: MigrationSandboxSeedEffectKind)
        (original: string option)
        : MigrationSandboxSeedEffectState =
        let identity = shaText $"{binding.Seal}:{index}:{kind}:{binding.Request.RunNonce}"

        {
            EffectId = identity
            IdempotencyKey = shaText $"idempotency:{identity}"
            Kind = kind
            OriginalEffectId = original
            Stage = MigrationSandboxSeedEffectStage.Planned
            Ownership = None
        }

    let create (binding: MigrationSandboxSeedExecutionBinding) (generation: int64) (head: string) =
        if
            not (shape binding)
            || binding.Seal <> bindingSeal binding
            || generation < 0L
            || not (hex 40 head)
        then
            Error MigrationSandboxSeedExecutorFailure.InvalidBinding
        elif
            not binding.Prestate.Complete
            || binding.Prestate.NonceIssueCount <> 0
            || binding.Prestate.NonceProjectItemCount <> 0
        then
            Error MigrationSandboxSeedExecutorFailure.UnsafePrestate
        else
            Ok
                {
                    Binding = binding
                    Mode = MigrationSandboxSeedExecutionMode.Forward
                    Effects =
                        [
                            effect binding 0 MigrationSandboxSeedEffectKind.CreateNonceIssue None
                            effect binding 1 MigrationSandboxSeedEffectKind.AddProjectMembership None
                        ]
                    ActiveIndex = 0
                    Generation = generation
                    Head = head
                }

    let private validCas (cas: MigrationSandboxSeedCas) (state: MigrationSandboxSeedExecution) =
        cas.ExpectedGeneration = state.Generation
        && cas.ExpectedHead = state.Head
        && hex 40 cas.NextHead
        && cas.NextHead <> state.Head

    let private owned
        (state: MigrationSandboxSeedExecution)
        (effect: MigrationSandboxSeedEffectState)
        (receipt: MigrationSandboxOwnedResource)
        =
        receipt.EffectId = effect.EffectId
        && receipt.RunNonce = state.Binding.Request.RunNonce
        && text receipt.ResourceId
        && hex 64 receipt.ReadbackSha256
        && match effect.Kind with
           | MigrationSandboxSeedEffectKind.CreateNonceIssue ->
               receipt.Kind = "issue" && receipt.ParentResourceId.IsNone
           | MigrationSandboxSeedEffectKind.AddProjectMembership ->
               receipt.Kind = "project-item"
               && (state.Effects.Head.Ownership
                   |> Option.exists (fun issue -> receipt.ParentResourceId = Some issue.ResourceId))
           | _ -> false

    let private stableEffect (expected: MigrationSandboxSeedEffectState) (actual: MigrationSandboxSeedEffectState) =
        expected.EffectId = actual.EffectId
        && expected.IdempotencyKey = actual.IdempotencyKey
        && expected.Kind = actual.Kind
        && expected.OriginalEffectId = actual.OriginalEffectId

    let private orderedStages active (effects: MigrationSandboxSeedEffectState list) =
        effects
        |> List.mapi (fun index effect ->
            if index < active then
                effect.Stage = MigrationSandboxSeedEffectStage.Settled
            elif index = active then
                effect.Stage <> MigrationSandboxSeedEffectStage.Settled
            else
                effect.Stage = MigrationSandboxSeedEffectStage.Planned)
        |> List.forall id

    let private validForwardState (state: MigrationSandboxSeedExecution) =
        let expected =
            [
                effect state.Binding 0 MigrationSandboxSeedEffectKind.CreateNonceIssue None
                effect state.Binding 1 MigrationSandboxSeedEffectKind.AddProjectMembership None
            ]

        state.Effects.Length = expected.Length
        && List.forall2 stableEffect expected state.Effects
        && state.Effects
           |> List.forall (fun item ->
               match item.Stage, item.Ownership with
               | MigrationSandboxSeedEffectStage.Settled, Some receipt -> owned state item receipt
               | MigrationSandboxSeedEffectStage.Settled, None -> false
               | _, None -> true
               | _, Some _ -> false)
        && match state.Mode with
           | MigrationSandboxSeedExecutionMode.Forward ->
               state.ActiveIndex >= 0
               && state.ActiveIndex < state.Effects.Length
               && orderedStages state.ActiveIndex state.Effects
           | MigrationSandboxSeedExecutionMode.Complete ->
               state.ActiveIndex = state.Effects.Length
               && state.Effects
                  |> List.forall (fun item -> item.Stage = MigrationSandboxSeedEffectStage.Settled)
           | _ -> false

    let private validCompensationState (state: MigrationSandboxSeedExecution) =
        let issue =
            effect state.Binding 0 MigrationSandboxSeedEffectKind.CreateNonceIssue None

        let membership =
            effect state.Binding 1 MigrationSandboxSeedEffectKind.AddProjectMembership None

        let remove =
            effect state.Binding 2 MigrationSandboxSeedEffectKind.RemoveProjectMembership (Some membership.EffectId)

        let delete =
            effect state.Binding 3 MigrationSandboxSeedEffectKind.DeleteNonceIssue (Some issue.EffectId)

        let custody =
            match
                state.Effects |> List.tryItem 0 |> Option.bind _.Ownership,
                state.Effects |> List.tryItem 1 |> Option.bind _.Ownership
            with
            | Some item, Some ownedIssue ->
                item.EffectId = membership.EffectId
                && item.Kind = "project-item"
                && item.RunNonce = state.Binding.Request.RunNonce
                && text item.ResourceId
                && hex 64 item.ReadbackSha256
                && ownedIssue.EffectId = issue.EffectId
                && ownedIssue.Kind = "issue"
                && ownedIssue.RunNonce = state.Binding.Request.RunNonce
                && text ownedIssue.ResourceId
                && ownedIssue.ParentResourceId.IsNone
                && hex 64 ownedIssue.ReadbackSha256
                && item.ParentResourceId = Some ownedIssue.ResourceId
            | _ -> false

        state.Effects.Length = 2
        && List.forall2 stableEffect [ remove; delete ] state.Effects
        && custody
        && match state.Mode with
           | MigrationSandboxSeedExecutionMode.Compensation ->
               state.ActiveIndex >= 0
               && state.ActiveIndex < state.Effects.Length
               && orderedStages state.ActiveIndex state.Effects
           | MigrationSandboxSeedExecutionMode.Compensated ->
               state.ActiveIndex = state.Effects.Length
               && state.Effects
                  |> List.forall (fun item -> item.Stage = MigrationSandboxSeedEffectStage.Settled)
           | _ -> false

    let private validState (state: MigrationSandboxSeedExecution) =
        shape state.Binding
        && state.Binding.Seal = bindingSeal state.Binding
        && state.Generation >= 0L
        && hex 40 state.Head
        && match state.Mode with
           | MigrationSandboxSeedExecutionMode.Forward
           | MigrationSandboxSeedExecutionMode.Complete -> validForwardState state
           | MigrationSandboxSeedExecutionMode.Compensation
           | MigrationSandboxSeedExecutionMode.Compensated -> validCompensationState state

    let transition
        (cas: MigrationSandboxSeedCas)
        (action: MigrationSandboxSeedAction)
        (state: MigrationSandboxSeedExecution)
        =
        if not (validState state) then
            Error MigrationSandboxSeedExecutorFailure.InvalidTransition
        elif not (validCas cas state) then
            Error MigrationSandboxSeedExecutorFailure.StaleCas
        elif state.ActiveIndex < 0 || state.ActiveIndex >= state.Effects.Length then
            Error MigrationSandboxSeedExecutorFailure.WrongEffect
        else
            let current = state.Effects[state.ActiveIndex]

            let actionId =
                match action with
                | MigrationSandboxSeedAction.PersistIntent x
                | MigrationSandboxSeedAction.MarkInFlight x
                | MigrationSandboxSeedAction.RecordResponseUnknown x
                | MigrationSandboxSeedAction.SettleAbsent x -> x
                | MigrationSandboxSeedAction.SettleApplied(x, _) -> x

            if actionId <> current.EffectId then
                Error MigrationSandboxSeedExecutorFailure.WrongEffect
            else
                let next result updated settled =
                    let effects =
                        state.Effects
                        |> List.mapi (fun i value -> if i = state.ActiveIndex then updated else value)

                    let index = if settled then state.ActiveIndex + 1 else state.ActiveIndex

                    let mode =
                        if settled && index = effects.Length then
                            (if state.Mode = MigrationSandboxSeedExecutionMode.Forward then
                                 MigrationSandboxSeedExecutionMode.Complete
                             else
                                 MigrationSandboxSeedExecutionMode.Compensated)
                        else
                            state.Mode

                    Ok(
                        { state with
                            Effects = effects
                            ActiveIndex = index
                            Generation = state.Generation + 1L
                            Head = cas.NextHead
                            Mode = mode
                        },
                        result
                    )

                match action, current.Stage with
                | MigrationSandboxSeedAction.PersistIntent _, MigrationSandboxSeedEffectStage.Planned ->
                    next
                        MigrationSandboxSeedExecutorResult.Advanced
                        { current with
                            Stage = MigrationSandboxSeedEffectStage.IntentPersisted
                        }
                        false
                | MigrationSandboxSeedAction.MarkInFlight _, MigrationSandboxSeedEffectStage.IntentPersisted ->
                    next
                        MigrationSandboxSeedExecutorResult.Advanced
                        { current with
                            Stage = MigrationSandboxSeedEffectStage.InFlight
                        }
                        false
                | MigrationSandboxSeedAction.RecordResponseUnknown _, MigrationSandboxSeedEffectStage.InFlight ->
                    next
                        (MigrationSandboxSeedExecutorResult.Pending "response-unknown")
                        { current with
                            Stage = MigrationSandboxSeedEffectStage.RecoveryPending
                        }
                        false
                | MigrationSandboxSeedAction.SettleApplied(_, receipt),
                  (MigrationSandboxSeedEffectStage.InFlight | MigrationSandboxSeedEffectStage.RecoveryPending) when
                    state.Mode = MigrationSandboxSeedExecutionMode.Forward
                    ->
                    if owned state current receipt then
                        next
                            MigrationSandboxSeedExecutorResult.Advanced
                            { current with
                                Stage = MigrationSandboxSeedEffectStage.Settled
                                Ownership = Some receipt
                            }
                            true
                    else
                        Error MigrationSandboxSeedExecutorFailure.ForeignOwnership
                | MigrationSandboxSeedAction.SettleAbsent _,
                  (MigrationSandboxSeedEffectStage.InFlight | MigrationSandboxSeedEffectStage.RecoveryPending) when
                    state.Mode = MigrationSandboxSeedExecutionMode.Compensation
                    ->
                    if current.OriginalEffectId.IsSome && current.Ownership.IsSome then
                        next
                            MigrationSandboxSeedExecutorResult.Advanced
                            { current with
                                Stage = MigrationSandboxSeedEffectStage.Settled
                            }
                            true
                    else
                        Error MigrationSandboxSeedExecutorFailure.ForeignOwnership
                | _ -> Error MigrationSandboxSeedExecutorFailure.InvalidTransition

    let beginCompensation (cas: MigrationSandboxSeedCas) (state: MigrationSandboxSeedExecution) =
        if not (validState state) then
            Error MigrationSandboxSeedExecutorFailure.InvalidTransition
        elif not (validCas cas state) then
            Error MigrationSandboxSeedExecutorFailure.StaleCas
        elif
            state.Mode <> MigrationSandboxSeedExecutionMode.Complete
            || state.Effects
               |> List.exists (fun e -> e.Stage <> MigrationSandboxSeedEffectStage.Settled || e.Ownership.IsNone)
        then
            Error MigrationSandboxSeedExecutorFailure.InvalidTransition
        else
            let membership = state.Effects[1]
            let issue = state.Effects[0]

            let remove =
                { effect
                      state.Binding
                      2
                      MigrationSandboxSeedEffectKind.RemoveProjectMembership
                      (Some membership.EffectId) with
                    Ownership = membership.Ownership
                }

            let delete =
                { effect state.Binding 3 MigrationSandboxSeedEffectKind.DeleteNonceIssue (Some issue.EffectId) with
                    Ownership = issue.Ownership
                }

            Ok
                { state with
                    Mode = MigrationSandboxSeedExecutionMode.Compensation
                    Effects = [ remove; delete ]
                    ActiveIndex = 0
                    Generation = state.Generation + 1L
                    Head = cas.NextHead
                }
