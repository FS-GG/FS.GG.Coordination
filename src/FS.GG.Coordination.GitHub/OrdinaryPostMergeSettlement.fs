#nowarn "3391"

namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type OrdinaryMergedPullRequest =
    {
        Number: int
        NodeId: string
        Repository: string
        BaseRef: string
        MergeCommit: string
        Merged: bool
    }

type OrdinarySettlementCredentialBinding =
    {
        AppId: int64
        InstallationId: int64
        RepositoryIds: int64 list
        Permissions: Map<string, string>
    }

type OrdinarySettlementReadBinding =
    {
        AppId: int64
        InstallationId: int64
        RepositoryIds: int64 list
        Permissions: Map<string, string>
    }

type OrdinarySettlementTrustAnchor =
    {
        KeyId: string
        PublicKeySpkiSha256: string
        AppId: int64
        InstallationId: int64
        RepositoryId: int64
        Permissions: Map<string, string>
    }

type OrdinarySettlementAuthorization =
    {
        KeyId: string
        PublicKeyPem: string
        IntentSha256: string
        Signature: byte array
    }

type OrdinarySettlementPlan =
    {
        Schema: string
        OperationId: string
        AttemptId: string
        OriginalPlanId: string
        Repository: string
        RepositoryId: int64
        AuthorityRepositoryId: int64
        PullRequestNumber: int
        PullRequestNodeId: string
        SourceCommit: string
        SourceTree: string
        WorkflowRevision: string
        PolicyRevision: string
        Environment: string
        OperationClass: string
        Epoch: string
        EpochGeneration: int64
        EpochCommit: string
        SourcePlanSeal: string
        JournalAddress: AggregateAddress
        Seal: string
    }

type OrdinarySettlementFailure =
    | InvalidSettlementIdentity of string
    | UnsupportedSettlementClass
    | AmbiguousMergedPullRequest
    | MissingMergedPullRequest
    | MismatchedMergedPullRequest
    | SettlementSourceFailure of OrdinaryDeliveryFailure
    | SettlementPreOpenV2
    | SettlementCredentialMismatch
    | SettlementTrustMismatch
    | SettlementSignatureInvalid
    | SettlementAlteredPlan
    | SettlementProviderUnavailable of string
    | SettlementJournalConflict
    | SettlementJournalUnavailable of string
    | SettlementEffectRefused of string
    | SettlementReadbackMismatch

type OrdinarySettlementStage = SettlementIntentPersisted | SettlementEffectPending | SettlementComplete

type OrdinarySettlementEntry =
    {
        OperationId: string
        AttemptId: string
        PlanDigest: string
        Generation: int64
        Stage: OrdinarySettlementStage
        ReceiptDigest: string option
    }

type OrdinarySettlementShard =
    {
        Address: AggregateAddress
        Revision: string option
        Entries: Map<string, OrdinarySettlementEntry>
    }

type OrdinarySettlementShardRead =
    | SettlementShardObserved of OrdinarySettlementShard
    | SettlementShardUnknown of string

type OrdinarySettlementCasOutcome = SettlementCasAccepted | SettlementCasConflict | SettlementCasUnknown
type OrdinarySettlementEffectObservation = SettlementEffectAbsent | SettlementEffectApplied of string | SettlementEffectUnknown
type OrdinarySettlementEffectOutcome = SettlementEffectAccepted of string | SettlementEffectRejected of string | SettlementEffectResponseUnknown
type OrdinarySettlementCut = SettlementNoCut | StopBeforeJournal | StopAfterIntent | StopAfterEffect
type OrdinarySettlementResult = SettlementSucceeded of string | SettlementAlreadyComplete of string | SettlementPending of string | SettlementInterrupted of string

type IOrdinaryPostMergeSettlementRuntime =
    abstract ReadShard: AggregateAddress -> OrdinarySettlementShardRead
    abstract CompareExchangeShard:
        expectedParent: string option * proposed: OrdinarySettlementShard -> OrdinarySettlementCasOutcome
    abstract ObserveEffect: OrdinarySettlementPlan -> Result<OrdinarySettlementEffectObservation, string>
    abstract ApplyEffect:
        OrdinarySettlementPlan * OrdinarySettlementCredentialBinding -> OrdinarySettlementEffectOutcome
    abstract ReadBack: OrdinarySettlementPlan -> Result<string option, string>

type OrdinarySettlementInvocation =
    {
        Plan: OrdinarySettlementPlan
        Credential: OrdinarySettlementCredentialBinding
        Anchor: OrdinarySettlementTrustAnchor
        Authorization: OrdinarySettlementAuthorization
        Runtime: IOrdinaryPostMergeSettlementRuntime
    }

/// The installed workflow supplies this provider. It owns fresh native reads and the
/// two separately scoped tokens; the CLI deliberately accepts no caller supplied plan.
type IOrdinarySettlementCommandProvider =
    abstract LoadOneAttempt: unit -> Result<OrdinarySettlementInvocation, string>

[<RequireQualifiedAccess>]
module OrdinaryPostMergeSettlement =
    let private schema = "fsgg.coordination.ordinary-post-merge-settlement-plan/1"
    let private operationClass = "ordinary-post-merge-delivery-settlement"
    let private environment = "ordinary-v2"

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validHex length (value: string) =
        validText value && value.Length = length && value |> Seq.forall Uri.IsHexDigit

    let private validOid value = validHex 40 value || validHex 64 value
    let private validDigest value = validHex 64 value

    let private sha256 (bytes: byte array) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private canonical (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson
        |> Result.defaultWith invalidOp

    let private sourceFailures observation =
        match OrdinaryDelivery.inspect observation with
        | Ok value ->
            [
                if value.Epoch <> "OpenV2" then yield SettlementPreOpenV2
                for check in value.Checks do
                    if check.Conclusion <> CheckPassed then
                        yield SettlementSourceFailure(RequiredCheckNotPassed check.Identity)
            ]
        | Error failures -> failures |> List.map SettlementSourceFailure

    let private expectedPermissions =
        Map [ "contents", "write"; "metadata", "read" ]

    let private expectedReadPermissions =
        Map
            [ "administration", "read"; "checks", "read"; "contents", "read"
              "metadata", "read"; "pull_requests", "read" ]

    let private bindingValid authorityRepositoryId (binding: OrdinarySettlementCredentialBinding) =
        binding.AppId > 0L
        && binding.InstallationId > 0L
        && binding.RepositoryIds = [ authorityRepositoryId ]
        && binding.Permissions = expectedPermissions

    let private readBindingValid sourceRepositoryId (binding: OrdinarySettlementReadBinding) =
        binding.AppId > 0L
        && binding.InstallationId > 0L
        && binding.RepositoryIds = [ sourceRepositoryId ]
        && binding.Permissions = expectedReadPermissions

    let private associationFor (observation: OrdinaryDeliveryObservation) associations =
        match associations with
        | [] -> Error MissingMergedPullRequest
        | [ association ] when not association.Merged -> Error MissingMergedPullRequest
        | [ association ] when
            association.Number <> observation.PullRequestNumber
            || association.NodeId <> observation.PullRequestNodeId
            || not (String.Equals(association.Repository, observation.Repository, StringComparison.OrdinalIgnoreCase))
            || association.BaseRef <> observation.BaseRef
            || association.BaseRef <> "main"
            || not (String.Equals(association.MergeCommit, observation.HeadSha, StringComparison.OrdinalIgnoreCase))
            -> Error MismatchedMergedPullRequest
        | [ association ] -> Ok association
        | _ -> Error AmbiguousMergedPullRequest

    let private unsignedNode
        (originalPlanId: string)
        (authorityRepositoryId: int64)
        (sourceTree: string)
        (workflowRevision: string)
        (observation: OrdinaryDeliveryObservation)
        (association: OrdinaryMergedPullRequest)
        (sourcePlanSeal: string)
        (operationId: string)
        (attemptId: string)
        (address: AggregateAddress)
        =
        let root = JsonObject()
        root.Add("attemptId", attemptId)
        root.Add("authorityRepositoryId", authorityRepositoryId)
        root.Add("environment", environment)
        root.Add("epoch", observation.Epoch)
        root.Add("epochCommit", observation.EpochCommit.ToLowerInvariant())
        root.Add("epochGeneration", observation.EpochGeneration)
        root.Add("journalRef", address.Ref)
        root.Add("operationClass", operationClass)
        root.Add("operationId", operationId)
        root.Add("originalPlanId", originalPlanId)
        root.Add("policyRevision", observation.PolicyRevision.ToLowerInvariant())
        root.Add("pullRequestNodeId", association.NodeId)
        root.Add("pullRequestNumber", association.Number)
        root.Add("repository", observation.Repository.ToLowerInvariant())
        root.Add("repositoryId", observation.RepositoryId)
        root.Add("schema", schema)
        root.Add("sourceCommit", association.MergeCommit.ToLowerInvariant())
        root.Add("sourcePlanSeal", sourcePlanSeal)
        root.Add("sourceTree", sourceTree.ToLowerInvariant())
        root.Add("workflowRevision", workflowRevision.ToLowerInvariant())
        root

    let prepare
        (originalPlanId: string)
        (authorityRepositoryId: int64)
        (sourceTree: string)
        (workflowRevision: string)
        (operationClassValue: string)
        (environmentValue: string)
        (observation: OrdinaryDeliveryObservation)
        (associations: OrdinaryMergedPullRequest list)
        (readBinding: OrdinarySettlementReadBinding)
        (binding: OrdinarySettlementCredentialBinding)
        =
        let failures =
            [
                yield! sourceFailures observation
                if not (validText originalPlanId) || originalPlanId.Length > 200 then
                    yield InvalidSettlementIdentity "original-plan-id"
                if not (validOid sourceTree) then yield InvalidSettlementIdentity "source-tree"
                if not (validOid workflowRevision) then yield InvalidSettlementIdentity "workflow-revision"
                if operationClassValue <> operationClass then yield UnsupportedSettlementClass
                if environmentValue <> environment then yield InvalidSettlementIdentity "environment"
                if authorityRepositoryId <= 0L then yield InvalidSettlementIdentity "authority-repository-id"
                if not (readBindingValid observation.RepositoryId readBinding) then yield SettlementCredentialMismatch
                if not (bindingValid authorityRepositoryId binding) then yield SettlementCredentialMismatch
            ]

        match failures, associationFor observation associations, OrdinaryDelivery.plan observation with
        | _ :: _, _, _ -> Error failures
        | [], Error failure, _ -> Error [ failure ]
        | [], _, Error source -> Error(source |> List.map SettlementSourceFailure)
        | [], Ok association, Ok(sourcePlan, _) ->
            let seed =
                Encoding.UTF8.GetBytes(
                    String.concat "\n"
                        [ originalPlanId; observation.Repository.ToLowerInvariant(); string association.Number
                          association.MergeCommit.ToLowerInvariant(); sourcePlan.Seal; observation.PolicyRevision.ToLowerInvariant()
                          observation.EpochCommit.ToLowerInvariant(); string observation.EpochGeneration ]
                )
            let operationId = "ordinary-settlement:" + sha256 seed
            let attemptId = operationId + ":attempt:1"
            match ShardedJournalAdapter.address Operation $"ordinary:{observation.RepositoryId}:{association.NodeId}" with
            | Error _ -> Error [ InvalidSettlementIdentity "journal-address" ]
            | Ok address ->
                let unsigned =
                    unsignedNode originalPlanId authorityRepositoryId sourceTree workflowRevision observation association sourcePlan.Seal operationId attemptId address
                let seal = sha256 (canonical unsigned)
                unsigned.Add("seal", seal)
                let bytes = canonical unsigned
                Ok(
                    {
                        Schema = schema
                        OperationId = operationId
                        AttemptId = attemptId
                        OriginalPlanId = originalPlanId
                        Repository = observation.Repository.ToLowerInvariant()
                        RepositoryId = observation.RepositoryId
                        AuthorityRepositoryId = authorityRepositoryId
                        PullRequestNumber = association.Number
                        PullRequestNodeId = association.NodeId
                        SourceCommit = association.MergeCommit.ToLowerInvariant()
                        SourceTree = sourceTree.ToLowerInvariant()
                        WorkflowRevision = workflowRevision.ToLowerInvariant()
                        PolicyRevision = observation.PolicyRevision.ToLowerInvariant()
                        Environment = environment
                        OperationClass = operationClass
                        Epoch = observation.Epoch
                        EpochGeneration = observation.EpochGeneration
                        EpochCommit = observation.EpochCommit.ToLowerInvariant()
                        SourcePlanSeal = sourcePlan.Seal
                        JournalAddress = address
                        Seal = seal
                    },
                    bytes
                )

    let canonicalIntent (plan: OrdinarySettlementPlan) (binding: OrdinarySettlementCredentialBinding) =
        let permissions = JsonObject()
        for KeyValue(name, value) in binding.Permissions do permissions.Add(name, value)
        let repositories = JsonArray()
        for value in binding.RepositoryIds do repositories.Add value
        let root = JsonObject()
        root.Add("appId", binding.AppId)
        root.Add("attemptId", plan.AttemptId)
        root.Add("environment", plan.Environment)
        root.Add("epoch", plan.Epoch)
        root.Add("epochCommit", plan.EpochCommit)
        root.Add("epochGeneration", plan.EpochGeneration)
        root.Add("installationId", binding.InstallationId)
        root.Add("operationClass", plan.OperationClass)
        root.Add("operationId", plan.OperationId)
        root.Add("originalPlanId", plan.OriginalPlanId)
        root.Add("permissions", permissions)
        root.Add("planDigest", plan.Seal)
        root.Add("policyRevision", plan.PolicyRevision)
        root.Add("pullRequestNodeId", plan.PullRequestNodeId)
        root.Add("pullRequestNumber", plan.PullRequestNumber)
        root.Add("repository", plan.Repository)
        root.Add("repositoryId", plan.RepositoryId)
        root.Add("repositoryIds", repositories)
        root.Add("schema", "fsgg.coordination.ordinary-post-merge-settlement-intent/1")
        root.Add("sourceCommit", plan.SourceCommit)
        root.Add("sourceTree", plan.SourceTree)
        root.Add("workflowRevision", plan.WorkflowRevision)
        canonical root

    let verifyAuthorization
        (plan: OrdinarySettlementPlan)
        (binding: OrdinarySettlementCredentialBinding)
        (anchor: OrdinarySettlementTrustAnchor)
        (authorization: OrdinarySettlementAuthorization)
        =
        let intent = canonicalIntent plan binding
        let failures =
            [
                if not (bindingValid plan.AuthorityRepositoryId binding) then yield SettlementCredentialMismatch
                if
                    anchor.KeyId <> authorization.KeyId
                    || anchor.AppId <> binding.AppId
                    || anchor.InstallationId <> binding.InstallationId
                    || anchor.RepositoryId <> plan.AuthorityRepositoryId
                    || anchor.Permissions <> binding.Permissions
                then yield SettlementTrustMismatch
                if authorization.IntentSha256 <> sha256 intent then yield SettlementAlteredPlan
            ]
        if not failures.IsEmpty then Error failures
        else
            try
                use rsa = RSA.Create()
                rsa.ImportFromPem authorization.PublicKeyPem
                if sha256 (rsa.ExportSubjectPublicKeyInfo()) <> anchor.PublicKeySpkiSha256 then
                    Error [ SettlementTrustMismatch ]
                elif obj.ReferenceEquals(authorization.Signature, null)
                     || not (rsa.VerifyData(intent, authorization.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) then
                    Error [ SettlementSignatureInvalid ]
                else Ok()
            with _ -> Error [ SettlementSignatureInvalid ]

    let private entryMatches (plan: OrdinarySettlementPlan) (entry: OrdinarySettlementEntry) =
        entry.OperationId = plan.OperationId
        && entry.AttemptId = plan.AttemptId
        && entry.PlanDigest = plan.Seal

    let private readShard (runtime: IOrdinaryPostMergeSettlementRuntime) (plan: OrdinarySettlementPlan) =
        match runtime.ReadShard plan.JournalAddress with
        | SettlementShardUnknown reason -> Error [ SettlementJournalUnavailable reason ]
        | SettlementShardObserved shard when shard.Address <> plan.JournalAddress -> Error [ SettlementJournalConflict ]
        | SettlementShardObserved shard -> Ok shard

    let private transition
        (runtime: IOrdinaryPostMergeSettlementRuntime)
        (plan: OrdinarySettlementPlan)
        (expectedStage: OrdinarySettlementStage)
        (nextStage: OrdinarySettlementStage)
        (receipt: string option)
        =
        readShard runtime plan
        |> Result.bind (fun shard ->
            match Map.tryFind plan.OperationId shard.Entries with
            | Some current when entryMatches plan current && current.Stage = expectedStage ->
                let next = { current with Generation = current.Generation + 1L; Stage = nextStage; ReceiptDigest = receipt }
                let proposed = { shard with Entries = Map.add plan.OperationId next shard.Entries }
                match runtime.CompareExchangeShard(shard.Revision, proposed) with
                | SettlementCasAccepted -> Ok next
                | SettlementCasUnknown ->
                    match readShard runtime plan with
                    | Ok reread ->
                        match Map.tryFind plan.OperationId reread.Entries with
                        | Some value when value = next -> Ok value
                        | _ -> Error [ SettlementJournalUnavailable "cas-outcome-unknown" ]
                    | Error error -> Error error
                | SettlementCasConflict -> Error [ SettlementJournalConflict ]
            | Some current when entryMatches plan current && current.Stage = nextStage -> Ok current
            | _ -> Error [ SettlementJournalConflict ])

    let private ensureIntent (runtime: IOrdinaryPostMergeSettlementRuntime) (plan: OrdinarySettlementPlan) =
        readShard runtime plan
        |> Result.bind (fun shard ->
            match Map.tryFind plan.OperationId shard.Entries with
            | Some current when entryMatches plan current -> Ok current
            | Some _ -> Error [ SettlementJournalConflict ]
            | None ->
                let entry =
                    { OperationId = plan.OperationId; AttemptId = plan.AttemptId; PlanDigest = plan.Seal
                      Generation = 1L; Stage = SettlementIntentPersisted; ReceiptDigest = None }
                let proposed = { shard with Entries = Map.add plan.OperationId entry shard.Entries }
                match runtime.CompareExchangeShard(shard.Revision, proposed) with
                | SettlementCasAccepted -> Ok entry
                | SettlementCasUnknown
                | SettlementCasConflict ->
                    match readShard runtime plan with
                    | Ok reread ->
                        match Map.tryFind plan.OperationId reread.Entries with
                        | Some current when entryMatches plan current -> Ok current
                        | _ when proposed.Entries |> Map.forall (fun key value -> Map.tryFind key reread.Entries = Some value) -> Ok entry
                        | _ -> Error [ SettlementJournalConflict ]
                    | Error error -> Error error)

    let private settle
        (runtime: IOrdinaryPostMergeSettlementRuntime)
        (plan: OrdinarySettlementPlan)
        (entry: OrdinarySettlementEntry)
        (receipt: string)
        =
        match runtime.ReadBack plan with
        | Error reason -> Error [ SettlementJournalUnavailable reason ]
        | Ok(Some observed) when observed <> receipt -> Error [ SettlementReadbackMismatch ]
        | Ok None -> Error [ SettlementReadbackMismatch ]
        | Ok(Some _) ->
            transition runtime plan entry.Stage SettlementComplete (Some receipt)
            |> Result.map (fun _ -> SettlementSucceeded receipt)

    let execute
        (plan: OrdinarySettlementPlan)
        (binding: OrdinarySettlementCredentialBinding)
        (anchor: OrdinarySettlementTrustAnchor)
        (authorization: OrdinarySettlementAuthorization)
        (cut: OrdinarySettlementCut)
        (runtime: IOrdinaryPostMergeSettlementRuntime)
        =
        verifyAuthorization plan binding anchor authorization
        |> Result.bind (fun () ->
            if plan.Epoch <> "OpenV2" then Error [ SettlementPreOpenV2 ]
            elif cut = StopBeforeJournal then Ok(SettlementInterrupted "before-journal")
            else
                ensureIntent runtime plan
                |> Result.bind (fun entry ->
                    if entry.Stage = SettlementComplete then
                        match entry.ReceiptDigest with
                        | Some receipt -> Ok(SettlementAlreadyComplete receipt)
                        | None -> Error [ SettlementJournalConflict ]
                    elif cut = StopAfterIntent then Ok(SettlementInterrupted "after-intent")
                    else
                        match runtime.ObserveEffect plan with
                        | Error reason -> Error [ SettlementJournalUnavailable reason ]
                        | Ok SettlementEffectUnknown -> Ok(SettlementPending "effect-observation-unknown")
                        | Ok(SettlementEffectApplied receipt) -> settle runtime plan entry receipt
                        | Ok SettlementEffectAbsent ->
                            let pending =
                                if entry.Stage = SettlementEffectPending then Ok entry
                                else transition runtime plan entry.Stage SettlementEffectPending None
                            pending
                            |> Result.bind (fun pendingEntry ->
                                match runtime.ApplyEffect(plan, binding) with
                                | SettlementEffectRejected reason -> Error [ SettlementEffectRefused reason ]
                                | SettlementEffectResponseUnknown -> Ok(SettlementPending "effect-response-unknown")
                                | SettlementEffectAccepted receipt when cut = StopAfterEffect ->
                                    Ok(SettlementInterrupted "after-effect")
                                | SettlementEffectAccepted receipt -> settle runtime plan pendingEntry receipt)))

[<RequireQualifiedAccess>]
module OrdinarySettlementCommandContract =
    let executeOneAttempt (provider: IOrdinarySettlementCommandProvider option) =
        match provider with
        | None -> Error [ SettlementProviderUnavailable "installed-provider-unavailable" ]
        | Some value ->
            match value.LoadOneAttempt() with
            | Error reason -> Error [ SettlementProviderUnavailable reason ]
            | Ok invocation ->
                OrdinaryPostMergeSettlement.execute
                    invocation.Plan
                    invocation.Credential
                    invocation.Anchor
                    invocation.Authorization
                    SettlementNoCut
                    invocation.Runtime
