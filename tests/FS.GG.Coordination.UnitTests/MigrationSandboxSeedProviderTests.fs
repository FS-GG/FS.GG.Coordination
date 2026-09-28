module FS.GG.Coordination.MigrationSandboxSeedProviderTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private bytes (value: string) =
    ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes value)

let private sha (value: ReadOnlyMemory<byte>) =
    SHA256.HashData(value.Span) |> Convert.ToHexString |> _.ToLowerInvariant()

let private candidate = String.replicate 40 "a"
let private workflow = String.replicate 40 "b"
let private mint = bytes "mint-v1"
let private seedPlan = bytes "seed-plan"
let private corpus = bytes "corpus"

let private request =
    {
        CandidateSha = candidate
        WorkflowRunId = 71L
        WorkflowRunAttempt = 2
        RunNonce = $"71-2-{candidate}"
        CorpusSha256 = sha corpus
    }

let private hostReceipt () =
    let fingerprint = String.replicate 64 "f"
    let builderSha = String.replicate 64 "1"
    let protectedBuilderSha = String.replicate 64 "2"
    let protectedWorkflowSha = String.replicate 64 "3"
    let tokenSha = String.replicate 64 "4"
    let approvedArtifactSha = String.replicate 64 "5"

    bytes
        $"{{\"schema\":\"fsgg.github-substrate-v2.sandbox-seed-execution-binding/2\",\"status\":\"bound-no-write-authority\",\"activation\":false,\"authority\":\"unavailable-without-protected-host-install-and-native-readback\",\"schemaJoin\":\"coordination-s1-provenance-interface-v1\",\"fingerprint\":\"{fingerprint}\",\"source\":{{\"repository\":\"FS-GG/.github\",\"builderPath\":\"scripts/gs2-09-7-seed-execution-binding.py\",\"builderSha256\":\"{builderSha}\",\"workflowRepository\":\"FS-GG/.github\",\"workflowPath\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"workflowRef\":\"refs/heads/main\",\"workflowSha\":\"{workflow}\",\"providerWorkflowSha\":\"{workflow}\",\"candidateSha\":\"{candidate}\",\"runId\":71,\"runAttempt\":2,\"runNonce\":\"{request.RunNonce}\",\"protectedCheckout\":{{\"checkoutHead\":\"{workflow}\",\"builder\":{{\"path\":\"scripts/gs2-09-7-seed-execution-binding.py\",\"sha256\":\"{protectedBuilderSha}\"}},\"workflow\":{{\"path\":\".github/workflows/github-substrate-v2-sandbox-qualification.yml\",\"sha256\":\"{protectedWorkflowSha}\"}}}}}},\"provenanceInterface\":{{\"workflowRunId\":71,\"workflowRunAttempt\":2,\"workflowSha\":\"{workflow}\",\"approvedArtifactSourceSha256\":\"{approvedArtifactSha}\"}},\"sandbox\":{{\"repositoryId\":1353050537,\"repositoryNodeId\":\"R_kgDOUKXpqQ\",\"projectNodeId\":\"PVT_kwDOEYAWY84BiESo\"}},\"mint\":{{\"schema\":\"fsgg.github-substrate-v2.sandbox-mint-grants/1\",\"proofSha256\":\"{sha mint}\",\"tokenSha256\":\"{tokenSha}\",\"expiresAt\":\"2026-09-28T09:00:00Z\",\"appId\":4166418,\"installationId\":143110413}},\"artifacts\":{{\"seedPlan\":{{\"request\":\"FSGG_SEED_PLAN_PATH\",\"path\":\"seed-plan.json\",\"contentSchema\":\"opaque-retained-bytes-pending-coordination-s1\",\"byteLength\":9,\"sha256\":\"{sha seedPlan}\"}},\"corpus\":{{\"request\":\"FSGG_SEED_CORPUS_PATH\",\"path\":\"corpus.json\",\"contentSchema\":\"opaque-retained-bytes-pending-coordination-s1\",\"byteLength\":6,\"sha256\":\"{sha corpus}\"}}}},\"journal\":{{\"profile\":{{\"ref\":\"refs/heads/gs2-09-7/{request.RunNonce}/seed-journal\"}},\"allowedClosedEffectKinds\":[\"CreateNonceIssue\",\"AddProjectMembership\",\"RemoveProjectMembership\",\"DeleteNonceIssue\"]}}}}"

let private binding () =
    let host = hostReceipt ()

    let draft =
        {
            Request = request
            WorkflowPath = ".github/workflows/github-substrate-v2-sandbox-qualification.yml"
            WorkflowRef = "refs/heads/main"
            WorkflowSha = workflow
            RepositoryId = 1353050537L
            RepositoryNodeId = "R_kgDOUKXpqQ"
            ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
            MintProofSha256 = sha mint
            ProtectedHostReceiptSha256 = sha host
            SeedPlanSha256 = sha seedPlan
            CorpusSha256 = sha corpus
            Prestate =
                {
                    Complete = true
                    RepositoryId = 1353050537L
                    ProjectNodeId = "PVT_kwDOEYAWY84BiESo"
                    NonceIssueCount = 0
                    NonceProjectItemCount = 0
                    SnapshotSha256 = sha (bytes "empty")
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

    MigrationSandboxSeedExecutor.sealBinding mint host seedPlan corpus draft
    |> Result.defaultWith (failwithf "%A")

let private snapshot (plan: MigrationSandboxSeedJournalPlan) =
    {
        RefName = plan.RefName
        JournalGeneration = plan.JournalGeneration
        StateGeneration = plan.StateGeneration
        RunNonce = plan.RunNonce
        BindingSeal = plan.BindingSeal
        CommitOid = plan.CommitOid
        ParentOid = plan.ExpectedParent
        StateSha256 = plan.StateSha256
        StateBytes = plan.StateBytes
        BlobOid = plan.BlobOid
        TreeOid = plan.TreeOid
        TreeBytes = plan.TreeBytes
        CommitBytes = plan.CommitBytes
    }

let private advance (nextHead: string) action (state: MigrationSandboxSeedExecution) =
    MigrationSandboxSeedExecutor.transition
        {
            ExpectedGeneration = state.Generation
            ExpectedHead = state.Head
            NextHead = nextHead
        }
        action
        state
    |> Result.defaultWith (failwithf "%A")
    |> fst

let private createEvidence () =
    let bound = binding ()

    let initial =
        MigrationSandboxSeedExecutor.create bound 0L (String.replicate 40 "c")
        |> Result.defaultWith (failwithf "%A")

    let plan0 =
        MigrationSandboxSeedJournal.plan None initial
        |> Result.defaultWith (failwithf "%A")

    let snap0 = snapshot plan0
    let effect = initial.Effects.Head

    let intent =
        advance (String.replicate 40 "d") (MigrationSandboxSeedAction.PersistIntent effect.EffectId) initial

    let plan1 =
        MigrationSandboxSeedJournal.plan (Some snap0) intent
        |> Result.defaultWith (failwithf "%A")

    let snap1 = snapshot plan1

    let inflight =
        advance (String.replicate 40 "e") (MigrationSandboxSeedAction.MarkInFlight effect.EffectId) intent

    let plan2 =
        MigrationSandboxSeedJournal.plan (Some snap1) inflight
        |> Result.defaultWith (failwithf "%A")

    {
        PreviousSnapshot = Some snap1
        CurrentSnapshot = snapshot plan2
        MintProofBytes = mint
        ProtectedHostReceiptBytes = hostReceipt ()
        SeedPlanBytes = seedPlan
        CorpusBytes = corpus
    },
    inflight.Effects.Head

let private evidenceFrom previous current =
    {
        PreviousSnapshot = Some previous
        CurrentSnapshot = current
        MintProofBytes = mint
        ProtectedHostReceiptBytes = hostReceipt ()
        SeedPlanBytes = seedPlan
        CorpusBytes = corpus
    }

let private laterEvidence target =
    let bound = binding ()

    let initial =
        MigrationSandboxSeedExecutor.create bound 0L (String.replicate 40 "c")
        |> Result.defaultWith (failwithf "%A")

    let snap0 =
        MigrationSandboxSeedJournal.plan None initial
        |> Result.defaultWith (failwithf "%A")
        |> snapshot

    let create = initial.Effects.Head

    let createIntent =
        advance (String.replicate 40 "d") (MigrationSandboxSeedAction.PersistIntent create.EffectId) initial

    let snap1 =
        MigrationSandboxSeedJournal.plan (Some snap0) createIntent
        |> Result.defaultWith (failwithf "%A")
        |> snapshot

    let createFlight =
        advance (String.replicate 40 "e") (MigrationSandboxSeedAction.MarkInFlight create.EffectId) createIntent

    let snap2 =
        MigrationSandboxSeedJournal.plan (Some snap1) createFlight
        |> Result.defaultWith (failwithf "%A")
        |> snapshot

    let issue =
        {
            EffectId = create.EffectId
            Kind = "issue"
            ResourceId = "I_kwDOSeed"
            ParentResourceId = None
            RunNonce = request.RunNonce
            ReadbackSha256 = String.replicate 64 "5"
        }

    let afterCreate =
        advance
            (String.replicate 40 "f")
            (MigrationSandboxSeedAction.SettleApplied(create.EffectId, issue))
            createFlight

    let snap3 =
        MigrationSandboxSeedJournal.plan (Some snap2) afterCreate
        |> Result.defaultWith (failwithf "%A")
        |> snapshot

    let add = afterCreate.Effects[afterCreate.ActiveIndex]

    let addIntent =
        advance (String.replicate 40 "1") (MigrationSandboxSeedAction.PersistIntent add.EffectId) afterCreate

    let snap4 =
        MigrationSandboxSeedJournal.plan (Some snap3) addIntent
        |> Result.defaultWith (failwithf "%A")
        |> snapshot

    let addFlight =
        advance (String.replicate 40 "2") (MigrationSandboxSeedAction.MarkInFlight add.EffectId) addIntent

    let snap5 =
        MigrationSandboxSeedJournal.plan (Some snap4) addFlight
        |> Result.defaultWith (failwithf "%A")
        |> snapshot

    if target = MigrationSandboxSeedEffectKind.AddProjectMembership then
        evidenceFrom snap4 snap5, addFlight.Effects[addFlight.ActiveIndex]
    else
        let item =
            {
                EffectId = add.EffectId
                Kind = "project-item"
                ResourceId = "PVTI_seed"
                ParentResourceId = Some issue.ResourceId
                RunNonce = request.RunNonce
                ReadbackSha256 = String.replicate 64 "6"
            }

        let complete =
            advance (String.replicate 40 "3") (MigrationSandboxSeedAction.SettleApplied(add.EffectId, item)) addFlight

        let snap6 =
            MigrationSandboxSeedJournal.plan (Some snap5) complete
            |> Result.defaultWith (failwithf "%A")
            |> snapshot

        let compensation =
            MigrationSandboxSeedExecutor.beginCompensation
                {
                    ExpectedGeneration = complete.Generation
                    ExpectedHead = complete.Head
                    NextHead = String.replicate 40 "4"
                }
                complete
            |> Result.defaultWith (failwithf "%A")

        let snap7 =
            MigrationSandboxSeedJournal.plan (Some snap6) compensation
            |> Result.defaultWith (failwithf "%A")
            |> snapshot

        let remove = compensation.Effects.Head

        let removeIntent =
            advance (String.replicate 40 "5") (MigrationSandboxSeedAction.PersistIntent remove.EffectId) compensation

        let snap8 =
            MigrationSandboxSeedJournal.plan (Some snap7) removeIntent
            |> Result.defaultWith (failwithf "%A")
            |> snapshot

        let removeFlight =
            advance (String.replicate 40 "6") (MigrationSandboxSeedAction.MarkInFlight remove.EffectId) removeIntent

        let snap9 =
            MigrationSandboxSeedJournal.plan (Some snap8) removeFlight
            |> Result.defaultWith (failwithf "%A")
            |> snapshot

        if target = MigrationSandboxSeedEffectKind.RemoveProjectMembership then
            evidenceFrom snap8 snap9, removeFlight.Effects.Head
        else
            let afterRemove =
                advance (String.replicate 40 "7") (MigrationSandboxSeedAction.SettleAbsent remove.EffectId) removeFlight

            let snap10 =
                MigrationSandboxSeedJournal.plan (Some snap9) afterRemove
                |> Result.defaultWith (failwithf "%A")
                |> snapshot

            let delete = afterRemove.Effects[afterRemove.ActiveIndex]

            let deleteIntent =
                advance (String.replicate 40 "8") (MigrationSandboxSeedAction.PersistIntent delete.EffectId) afterRemove

            let snap11 =
                MigrationSandboxSeedJournal.plan (Some snap10) deleteIntent
                |> Result.defaultWith (failwithf "%A")
                |> snapshot

            let deleteFlight =
                advance (String.replicate 40 "9") (MigrationSandboxSeedAction.MarkInFlight delete.EffectId) deleteIntent

            let snap12 =
                MigrationSandboxSeedJournal.plan (Some snap11) deleteFlight
                |> Result.defaultWith (failwithf "%A")
                |> snapshot

            evidenceFrom snap11 snap12, deleteFlight.Effects[deleteFlight.ActiveIndex]

let private responsePage request body =
    {
        Request = request
        StatusCode = 200
        ResponseBody = bytes body
    }

let private valuesFromMutation (plan: MigrationSandboxSeedProviderPlan) =
    use document = JsonDocument.Parse(plan.MutationTemplate.Body.Value)
    let variables = document.RootElement.GetProperty "variables"
    variables.GetProperty("title").GetString(), variables.GetProperty("body").GetString()

let private capture (plan: MigrationSandboxSeedProviderPlan) title body issueNode projectItems =
    let issues =
        $"{{\"data\":{{\"node\":{{\"id\":\"R_kgDOUKXpqQ\",\"databaseId\":1353050537,\"issues\":{{\"nodes\":[{{\"id\":\"{issueNode}\",\"databaseId\":9001,\"number\":17,\"title\":{JsonSerializer.Serialize title},\"body\":{JsonSerializer.Serialize body},\"state\":\"OPEN\",\"author\":{{\"login\":\"fs-gg-cross-repo-dispatch[bot]\",\"databaseId\":297630107}}}}],\"pageInfo\":{{\"hasNextPage\":false,\"endCursor\":\"terminal-issue\"}}}}}}}}}}"

    let project =
        $"{{\"data\":{{\"node\":{{\"id\":\"PVT_kwDOEYAWY84BiESo\",\"items\":{{\"nodes\":{projectItems},\"pageInfo\":{{\"hasNextPage\":false,\"endCursor\":null}}}}}}}}}}"

    let pass =
        {
            IssuePages = [ responsePage plan.FirstIssueRead issues ]
            ProjectPages = [ responsePage plan.FirstProjectRead project ]
        }

    {
        MutationResponse = None
        FirstPass = pass
        SecondPass = pass
    }

let private emptyCapture (plan: MigrationSandboxSeedProviderPlan) =
    let issues =
        "{\"data\":{\"node\":{\"id\":\"R_kgDOUKXpqQ\",\"databaseId\":1353050537,\"issues\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}}"

    let project =
        "{\"data\":{\"node\":{\"id\":\"PVT_kwDOEYAWY84BiESo\",\"items\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}}"

    let pass =
        {
            IssuePages = [ responsePage plan.FirstIssueRead issues ]
            ProjectPages = [ responsePage plan.FirstProjectRead project ]
        }

    {
        MutationResponse = None
        FirstPass = pass
        SecondPass = pass
    }

[<Fact>]
let ``restored in-flight state exposes exact template but cannot dispatch`` () =
    let evidence, effect = createEvidence ()

    let plan =
        MigrationSandboxSeedProvider.plan evidence
        |> Result.defaultWith (failwithf "%A")

    Assert.Equal("POST", plan.MutationTemplate.Method)
    Assert.Equal("/graphql", plan.MutationTemplate.Uri)
    Assert.Equal(effect.IdempotencyKey, plan.MutationTemplate.IdempotencyKey)
    Assert.Contains("createIssue", Encoding.UTF8.GetString plan.MutationTemplate.Body.Value)
    Assert.True(plan.DispatchRequest.IsNone)
    Assert.Equal(MigrationSandboxSeedProviderAuthority.SourceOnlyUnavailable, plan.DispatchAuthority)

[<Fact>]
let ``exact stable issue and project censuses prove nonce issue ownership`` () =
    let evidence, effect = createEvidence ()

    let plan =
        MigrationSandboxSeedProvider.plan evidence
        |> Result.defaultWith (failwithf "%A")

    let title, body = valuesFromMutation plan

    match MigrationSandboxSeedProvider.reconcile evidence (capture plan title body "I_kwDOSeed" "[]") with
    | Ok(MigrationSandboxSeedProviderDisposition.Applied owned) ->
        Assert.Equal(effect.EffectId, owned.EffectId)
        Assert.Equal("issue", owned.Kind)
        Assert.Equal("I_kwDOSeed", owned.ResourceId)
        Assert.Equal(request.RunNonce, owned.RunNonce)
        Assert.Matches("^[0-9a-f]{64}$", owned.ReadbackSha256)
    | other -> failwithf "%A" other

[<Fact>]
let ``changed second pass refuses stable ownership`` () =
    let evidence, effect = createEvidence ()

    let plan =
        MigrationSandboxSeedProvider.plan evidence
        |> Result.defaultWith (failwithf "%A")

    let title, body = valuesFromMutation plan
    let observed = capture plan title body "I_kwDOSeed" "[]"

    let changedBody =
        Encoding.UTF8.GetString(observed.SecondPass.IssuePages.Head.ResponseBody.Span).Replace("\"OPEN\"", "\"CLOSED\"")

    let changedPass =
        { observed.SecondPass with
            IssuePages = [ responsePage plan.FirstIssueRead changedBody ]
        }

    Assert.Equal(
        Error MigrationSandboxSeedProviderFailure.CaptureDrift,
        MigrationSandboxSeedProvider.reconcile
            evidence
            { observed with
                SecondPass = changedPass
            }
    )

[<Fact>]
let ``partial pagination and foreign request refuse`` () =
    let evidence, _ = createEvidence ()

    let plan =
        MigrationSandboxSeedProvider.plan evidence
        |> Result.defaultWith (failwithf "%A")

    let title, body = valuesFromMutation plan
    let observed = capture plan title body "I_kwDOSeed" "[]"

    let partialBody =
        Encoding.UTF8
            .GetString(observed.FirstPass.IssuePages.Head.ResponseBody.Span)
            .Replace("\"hasNextPage\":false", "\"hasNextPage\":true")

    let partialPass =
        { observed.FirstPass with
            IssuePages = [ responsePage plan.FirstIssueRead partialBody ]
        }

    match
        MigrationSandboxSeedProvider.reconcile
            evidence
            { observed with
                FirstPass = partialPass
                SecondPass = partialPass
            }
    with
    | Error MigrationSandboxSeedProviderFailure.CaptureIncomplete -> ()
    | other -> failwithf "%A" other

    let foreign =
        { plan.FirstIssueRead with
            Uri = "/graphql?foreign=true"
        }

    let foreignPass =
        { observed.FirstPass with
            IssuePages =
                [
                    responsePage foreign (Encoding.UTF8.GetString observed.FirstPass.IssuePages.Head.ResponseBody.Span)
                ]
        }

    Assert.Equal(
        Error MigrationSandboxSeedProviderFailure.ForeignRead,
        MigrationSandboxSeedProvider.reconcile
            evidence
            { observed with
                FirstPass = foreignPass
                SecondPass = foreignPass
            }
    )

[<Fact>]
let ``tampered S2 bytes and broken journal ancestry refuse`` () =
    let evidence, _ = createEvidence ()

    let badHost =
        { evidence with
            ProtectedHostReceiptBytes = bytes "{}"
        }

    Assert.Equal(
        Error MigrationSandboxSeedProviderFailure.InvalidSealedExecution,
        MigrationSandboxSeedProvider.plan badHost
    )

    let broken =
        { evidence with
            PreviousSnapshot = None
        }

    Assert.Equal(
        Error MigrationSandboxSeedProviderFailure.InvalidSealedExecution,
        MigrationSandboxSeedProvider.plan broken
    )

[<Fact>]
let ``all later effects have exact recovery-only templates and conservative readback`` () =
    let addEvidence, addEffect =
        laterEvidence MigrationSandboxSeedEffectKind.AddProjectMembership

    let addPlan =
        MigrationSandboxSeedProvider.plan addEvidence
        |> Result.defaultWith (failwithf "%A")

    let addBody = Encoding.UTF8.GetString addPlan.MutationTemplate.Body.Value
    Assert.Contains("addProjectV2ItemById", addBody)
    Assert.Contains("I_kwDOSeed", addBody)
    Assert.True(addPlan.DispatchRequest.IsNone)
    let title, body = "unrelated", "unrelated"

    let item =
        "[{\"id\":\"PVTI_seed\",\"content\":{\"id\":\"I_kwDOSeed\",\"databaseId\":9001,\"number\":17,\"repository\":{\"id\":\"R_kgDOUKXpqQ\",\"databaseId\":1353050537}}}]"

    let addCapture = capture addPlan title body "I_kwDOSeed" item

    match MigrationSandboxSeedProvider.reconcile addEvidence addCapture with
    | Ok(MigrationSandboxSeedProviderDisposition.RecoveryPending "membership-response-unknown") -> ()
    | other -> failwithf "%A" other

    let response =
        responsePage
            addPlan.MutationTemplate
            $"{{\"data\":{{\"addProjectV2ItemById\":{{\"clientMutationId\":\"{addEffect.IdempotencyKey}\",\"item\":{{\"id\":\"PVTI_seed\"}}}}}}}}"

    match
        MigrationSandboxSeedProvider.reconcile
            addEvidence
            { addCapture with
                MutationResponse = Some response
            }
    with
    | Ok(MigrationSandboxSeedProviderDisposition.Applied owned) -> Assert.Equal("PVTI_seed", owned.ResourceId)
    | other -> failwithf "%A" other

    let removeEvidence, _ =
        laterEvidence MigrationSandboxSeedEffectKind.RemoveProjectMembership

    let removePlan =
        MigrationSandboxSeedProvider.plan removeEvidence
        |> Result.defaultWith (failwithf "%A")

    Assert.Contains("deleteProjectV2Item", Encoding.UTF8.GetString removePlan.MutationTemplate.Body.Value)
    Assert.True(removePlan.DispatchRequest.IsNone)

    match MigrationSandboxSeedProvider.reconcile removeEvidence (capture removePlan title body "I_kwDOSeed" "[]") with
    | Ok MigrationSandboxSeedProviderDisposition.ProvenAbsent -> ()
    | other -> failwithf "%A" other

    let deleteEvidence, _ =
        laterEvidence MigrationSandboxSeedEffectKind.DeleteNonceIssue

    let deletePlan =
        MigrationSandboxSeedProvider.plan deleteEvidence
        |> Result.defaultWith (failwithf "%A")

    Assert.Contains("deleteIssue", Encoding.UTF8.GetString deletePlan.MutationTemplate.Body.Value)
    Assert.True(deletePlan.DispatchRequest.IsNone)

    match MigrationSandboxSeedProvider.reconcile deleteEvidence (emptyCapture deletePlan) with
    | Ok MigrationSandboxSeedProviderDisposition.ProvenAbsent -> ()
    | other -> failwithf "%A" other

[<Fact>]
let ``duplicate population refuses and foreign nonce marker conflicts`` () =
    let evidence, effect = createEvidence ()

    let plan =
        MigrationSandboxSeedProvider.plan evidence
        |> Result.defaultWith (failwithf "%A")

    let title, body = valuesFromMutation plan
    let observed = capture plan title body "I_kwDOSeed" "[]"

    let raw =
        Encoding.UTF8.GetString observed.FirstPass.IssuePages.Head.ResponseBody.Span

    let duplicate =
        raw.Replace(
            "],\"pageInfo\"",
            ","
            + raw.Substring(
                raw.IndexOf("{\"id\":\"I_kwDOSeed\"", StringComparison.Ordinal),
                raw.IndexOf("],\"pageInfo\"", StringComparison.Ordinal)
                - raw.IndexOf("{\"id\":\"I_kwDOSeed\"", StringComparison.Ordinal)
            )
            + "],\"pageInfo\""
        )

    let pass =
        { observed.FirstPass with
            IssuePages = [ responsePage plan.FirstIssueRead duplicate ]
        }

    Assert.Equal(
        Error MigrationSandboxSeedProviderFailure.MalformedResponse,
        MigrationSandboxSeedProvider.reconcile
            evidence
            { observed with
                FirstPass = pass
                SecondPass = pass
            }
    )

    let foreignMarker = raw.Replace(effect.EffectId, String.replicate 64 "9")

    let foreignPass =
        { observed.FirstPass with
            IssuePages = [ responsePage plan.FirstIssueRead foreignMarker ]
        }

    match
        MigrationSandboxSeedProvider.reconcile
            evidence
            { observed with
                FirstPass = foreignPass
                SecondPass = foreignPass
            }
    with
    | Ok(MigrationSandboxSeedProviderDisposition.Conflict _) -> ()
    | other -> failwithf "%A" other
