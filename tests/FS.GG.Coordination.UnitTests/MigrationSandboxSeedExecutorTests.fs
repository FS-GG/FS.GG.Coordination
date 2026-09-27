module FS.GG.Coordination.MigrationSandboxSeedExecutorTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli

let private bytes (v: string) =
    ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes v)

let private sha (v: string) =
    SHA256.HashData((bytes v).Span) |> Convert.ToHexString |> _.ToLowerInvariant()

let private candidate = String.replicate 40 "a"

let private mint, host, plan, corpus =
    bytes "mint-v1", bytes "protected-host", bytes "seed-plan", bytes "corpus"

let private request: MigrationSandboxSeedRequest =
    {
        CandidateSha = candidate
        WorkflowRunId = 7L
        WorkflowRunAttempt = 2
        RunNonce = $"7-2-{candidate}"
        CorpusSha256 = sha "corpus"
    }

let private draft: MigrationSandboxSeedExecutionBinding =
    {
        Request = request
        WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
        WorkflowRef = "refs/heads/main"
        WorkflowSha = candidate
        RepositoryId = 1353050537L
        RepositoryNodeId = "R_kgDOUKXpqQ"
        ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
        MintProofSha256 = sha "mint-v1"
        ProtectedHostReceiptSha256 = sha "protected-host"
        SeedPlanSha256 = sha "seed-plan"
        CorpusSha256 = sha "corpus"
        Prestate =
            {
                Complete = true
                RepositoryId = 1353050537L
                ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
                NonceIssueCount = 0
                NonceProjectItemCount = 0
                SnapshotSha256 = sha "empty"
            }
        AdmittedEffects =
            [
                MigrationSandboxSeedEffectKind.CreateNonceIssue
                MigrationSandboxSeedEffectKind.AddProjectMembership
                MigrationSandboxSeedEffectKind.RemoveProjectMembership
                MigrationSandboxSeedEffectKind.DeleteNonceIssue
            ]
        Seal = ""
    }

let private binding () : MigrationSandboxSeedExecutionBinding =
    MigrationSandboxSeedExecutor.sealBinding mint host plan corpus draft
    |> Result.defaultWith (fun e -> failwithf "%A" e)

let private initial () : MigrationSandboxSeedExecution =
    MigrationSandboxSeedExecutor.create (binding ()) 10L (String.replicate 40 "b")
    |> Result.defaultWith (fun e -> failwithf "%A" e)

let private apply
    (letter: string)
    (action: MigrationSandboxSeedAction)
    (state: MigrationSandboxSeedExecution)
    : MigrationSandboxSeedExecution =
    MigrationSandboxSeedExecutor.transition
        {
            ExpectedGeneration = state.Generation
            ExpectedHead = state.Head
            NextHead = String.replicate 40 letter
        }
        action
        state
    |> Result.defaultWith (fun e -> failwithf "%A" e)
    |> fst

let private settleForward () : MigrationSandboxSeedExecution =
    let s0 = initial ()
    let issue = s0.Effects.Head
    let s1 = apply "c" (MigrationSandboxSeedAction.PersistIntent issue.EffectId) s0
    let s2 = apply "d" (MigrationSandboxSeedAction.MarkInFlight issue.EffectId) s1

    let issueOwned =
        {
            EffectId = issue.EffectId
            Kind = "issue"
            ResourceId = "ISSUE_NONCE"
            ParentResourceId = None
            RunNonce = request.RunNonce
            ReadbackSha256 = sha "issue-readback"
        }

    let s3 =
        apply "e" (MigrationSandboxSeedAction.SettleApplied(issue.EffectId, issueOwned)) s2

    let item = s3.Effects[1]
    let s4 = apply "f" (MigrationSandboxSeedAction.PersistIntent item.EffectId) s3
    let s5 = apply "1" (MigrationSandboxSeedAction.MarkInFlight item.EffectId) s4

    let itemOwned =
        {
            EffectId = item.EffectId
            Kind = "project-item"
            ResourceId = "ITEM_NONCE"
            ParentResourceId = Some "ISSUE_NONCE"
            RunNonce = request.RunNonce
            ReadbackSha256 = sha "item-readback"
        }

    apply "2" (MigrationSandboxSeedAction.SettleApplied(item.EffectId, itemOwned)) s5

[<Fact>]
let ``binding joins exact retained bytes host run and empty nonce prestate`` () =
    let sealedBinding = binding ()
    Assert.Equal(request.RunNonce, sealedBinding.Request.RunNonce)
    Assert.NotEqual("", sealedBinding.Seal)

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidBinding,
        MigrationSandboxSeedExecutor.sealBinding mint host (bytes "changed") corpus draft
    )

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidBinding,
        MigrationSandboxSeedExecutor.sealBinding
            mint
            host
            plan
            corpus
            { draft with
                WorkflowPath = ".github/workflows/foreign.yml"
            }
    )

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidBinding,
        MigrationSandboxSeedExecutor.sealBinding
            mint
            host
            plan
            corpus
            { draft with
                Prestate =
                    { draft.Prestate with
                        NonceIssueCount = 1
                    }
            }
    )

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidBinding,
        MigrationSandboxSeedExecutor.create
            { sealedBinding with
                WorkflowSha = String.replicate 40 "f"
            }
            10L
            (String.replicate 40 "b")
    )

[<Fact>]
let ``forward effects require planned intent and in flight CAS order`` () =
    let s = initial ()
    let effect = s.Effects.Head
    let replay = initial ()
    Assert.Equal(effect.EffectId, replay.Effects.Head.EffectId)
    Assert.Equal(effect.IdempotencyKey, replay.Effects.Head.IdempotencyKey)

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidTransition,
        MigrationSandboxSeedExecutor.transition
            {
                ExpectedGeneration = s.Generation
                ExpectedHead = s.Head
                NextHead = String.replicate 40 "c"
            }
            (MigrationSandboxSeedAction.MarkInFlight effect.EffectId)
            s
    )

    let intent = apply "c" (MigrationSandboxSeedAction.PersistIntent effect.EffectId) s
    Assert.Equal(MigrationSandboxSeedEffectStage.IntentPersisted, intent.Effects.Head.Stage)

    let flight =
        apply "d" (MigrationSandboxSeedAction.MarkInFlight effect.EffectId) intent

    Assert.Equal(MigrationSandboxSeedEffectStage.InFlight, flight.Effects.Head.Stage)

[<Fact>]
let ``lost response persists pending and never reopens dispatch transition`` () =
    let s = initial ()
    let effect = s.Effects.Head

    let pending =
        s
        |> apply "c" (MigrationSandboxSeedAction.PersistIntent effect.EffectId)
        |> apply "d" (MigrationSandboxSeedAction.MarkInFlight effect.EffectId)
        |> apply "e" (MigrationSandboxSeedAction.RecordResponseUnknown effect.EffectId)

    Assert.Equal(MigrationSandboxSeedEffectStage.RecoveryPending, pending.Effects.Head.Stage)

    let retry =
        {
            ExpectedGeneration = pending.Generation
            ExpectedHead = pending.Head
            NextHead = String.replicate 40 "f"
        }

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidTransition,
        MigrationSandboxSeedExecutor.transition retry (MigrationSandboxSeedAction.MarkInFlight effect.EffectId) pending
    )

    let recovered =
        {
            EffectId = effect.EffectId
            Kind = "issue"
            ResourceId = "ISSUE_NONCE"
            ParentResourceId = None
            RunNonce = request.RunNonce
            ReadbackSha256 = sha "recovered-readback"
        }

    let settled =
        apply "f" (MigrationSandboxSeedAction.SettleApplied(effect.EffectId, recovered)) pending

    Assert.Equal(MigrationSandboxSeedEffectStage.Settled, settled.Effects.Head.Stage)

[<Fact>]
let ``stale parent or generation refuses without transition`` () =
    let s = initial ()
    let effect = s.Effects.Head

    for cas in
        [
            {
                ExpectedGeneration = 9L
                ExpectedHead = s.Head
                NextHead = String.replicate 40 "c"
            }
            {
                ExpectedGeneration = 10L
                ExpectedHead = String.replicate 40 "9"
                NextHead = String.replicate 40 "c"
            }
        ] do
        Assert.Equal(
            Error MigrationSandboxSeedExecutorFailure.StaleCas,
            MigrationSandboxSeedExecutor.transition cas (MigrationSandboxSeedAction.PersistIntent effect.EffectId) s
        )

[<Fact>]
let ``foreign Project membership cannot settle ownership`` () =
    let s = initial ()
    let issue = s.Effects.Head

    let issueOwned =
        {
            EffectId = issue.EffectId
            Kind = "issue"
            ResourceId = "ISSUE_NONCE"
            ParentResourceId = None
            RunNonce = request.RunNonce
            ReadbackSha256 = sha "issue"
        }

    let afterIssue =
        s
        |> apply "c" (MigrationSandboxSeedAction.PersistIntent issue.EffectId)
        |> apply "d" (MigrationSandboxSeedAction.MarkInFlight issue.EffectId)
        |> apply "e" (MigrationSandboxSeedAction.SettleApplied(issue.EffectId, issueOwned))

    let item = afterIssue.Effects[1]

    let flight =
        afterIssue
        |> apply "f" (MigrationSandboxSeedAction.PersistIntent item.EffectId)
        |> apply "1" (MigrationSandboxSeedAction.MarkInFlight item.EffectId)

    let foreign =
        {
            EffectId = item.EffectId
            Kind = "project-item"
            ResourceId = "FOREIGN"
            ParentResourceId = Some "OTHER_ISSUE"
            RunNonce = request.RunNonce
            ReadbackSha256 = sha "foreign"
        }

    let cas =
        {
            ExpectedGeneration = flight.Generation
            ExpectedHead = flight.Head
            NextHead = String.replicate 40 "2"
        }

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.ForeignOwnership,
        MigrationSandboxSeedExecutor.transition
            cas
            (MigrationSandboxSeedAction.SettleApplied(item.EffectId, foreign))
            flight
    )

[<Fact>]
let ``compensation derives reverse order only from settled owned effects`` () =
    let complete = settleForward ()
    Assert.Equal(MigrationSandboxSeedExecutionMode.Complete, complete.Mode)

    let compensation =
        MigrationSandboxSeedExecutor.beginCompensation
            {
                ExpectedGeneration = complete.Generation
                ExpectedHead = complete.Head
                NextHead = String.replicate 40 "3"
            }
            complete
        |> Result.defaultWith (fun e -> failwithf "%A" e)

    Assert.True(
        [
            MigrationSandboxSeedEffectKind.RemoveProjectMembership
            MigrationSandboxSeedEffectKind.DeleteNonceIssue
        ] =
            (compensation.Effects |> List.map _.Kind)
    )

    Assert.Equal(Some complete.Effects[1].EffectId, compensation.Effects[0].OriginalEffectId)
    Assert.Equal(Some complete.Effects[0].EffectId, compensation.Effects[1].OriginalEffectId)
    Assert.Equal(complete.Effects[1].Ownership, compensation.Effects[0].Ownership)
    Assert.Equal(complete.Effects[0].Ownership, compensation.Effects[1].Ownership)
    let remove = compensation.Effects[0]

    let missingCustody =
        { compensation with
            Effects = { remove with Ownership = None } :: compensation.Effects.Tail
        }

    Assert.Equal(
        Error MigrationSandboxSeedExecutorFailure.InvalidTransition,
        MigrationSandboxSeedExecutor.transition
            {
                ExpectedGeneration = missingCustody.Generation
                ExpectedHead = missingCustody.Head
                NextHead = String.replicate 40 "4"
            }
            (MigrationSandboxSeedAction.PersistIntent remove.EffectId)
            missingCustody
    )

    let afterRemove =
        compensation
        |> apply "4" (MigrationSandboxSeedAction.PersistIntent remove.EffectId)
        |> apply "5" (MigrationSandboxSeedAction.MarkInFlight remove.EffectId)
        |> apply "6" (MigrationSandboxSeedAction.SettleAbsent remove.EffectId)

    Assert.Equal(MigrationSandboxSeedEffectKind.DeleteNonceIssue, afterRemove.Effects[afterRemove.ActiveIndex].Kind)
    let delete = afterRemove.Effects[afterRemove.ActiveIndex]

    let compensated =
        afterRemove
        |> apply "7" (MigrationSandboxSeedAction.PersistIntent delete.EffectId)
        |> apply "8" (MigrationSandboxSeedAction.MarkInFlight delete.EffectId)
        |> apply "9" (MigrationSandboxSeedAction.SettleAbsent delete.EffectId)

    Assert.Equal(MigrationSandboxSeedExecutionMode.Compensated, compensated.Mode)
