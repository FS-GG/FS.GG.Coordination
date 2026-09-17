#nowarn "3391"

namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type OrdinaryCheckConclusion =
    | CheckPassed
    | CheckFailed
    | CheckPending
    | CheckUnknown

type OrdinaryCheckFact =
    { Identity: string; AppId: int64; Conclusion: OrdinaryCheckConclusion }

type OrdinaryDeliveryObservation =
    {
        Repository: string
        RepositoryId: int64
        PullRequestNumber: int
        PullRequestNodeId: string
        BaseRef: string
        BaseSha: string
        HeadSha: string
        PolicyRevision: string
        Checks: OrdinaryCheckFact list
        Epoch: string
        JournalGeneration: int64
        JournalHead: string
        SourceComplete: bool
        ChecksComplete: bool
        Authorized: bool
        Supported: bool
    }

type OrdinaryDeliveryPlan =
    {
        Schema: string
        OperationId: string
        Repository: string
        RepositoryId: int64
        PullRequestNumber: int
        PullRequestNodeId: string
        BaseRef: string
        BaseSha: string
        HeadSha: string
        PolicyRevision: string
        CheckIdentities: string list
        Epoch: string
        JournalGeneration: int64
        JournalHead: string
        Effect: string
        Seal: string
    }

type OrdinaryDeliveryFailure =
    | MissingSourcePages
    | MissingCheckPages
    | UnauthorizedObservation
    | UnsupportedObservation
    | CrossSubjectObservation
    | StalePolicy
    | ChangedSource
    | InvalidIdentity of string
    | RequiredCheckNotPassed of string
    | AlteredPlan
    | PreOpenV2Refusal
    | JournalConflict
    | JournalUnavailable of string
    | EffectRefused of string

type OrdinaryJournalStage = OrdinaryIntentPersisted | OrdinaryEffectPending | OrdinarySettled

type OrdinaryJournalAuthority =
    { OperationId: string; PlanDigest: string; Generation: int64; Stage: OrdinaryJournalStage; MergeCommit: string option }

type OrdinaryCasOutcome = CasAccepted of OrdinaryJournalAuthority | CasConflict | CasUnknown
type OrdinaryEffectObservation = EffectApplied of string | EffectProvenAbsent | EffectOutcomeUnknown
type OrdinaryDispatchOutcome = DispatchApplied of string | DispatchRefused of string | DispatchOutcomeUnknown
type OrdinaryAdvanceCut = NoCut | StopAfterIntent | StopAfterDispatch | StopAfterEffect
type OrdinaryAdvanceResult = AdvanceSettled of string | AdvanceAlreadySettled of string | AdvancePending of string | AdvanceInterrupted of string

type IOrdinaryDeliveryRuntime =
    abstract Observe: unit -> Result<OrdinaryDeliveryObservation, string>
    abstract ObserveJournal: operationId: string -> Result<OrdinaryJournalAuthority option, string>
    abstract PersistIntent: expectedGeneration: int64 * planDigest: string * operationId: string -> OrdinaryCasOutcome
    abstract MarkPending: expectedGeneration: int64 * operationId: string -> OrdinaryCasOutcome
    abstract ObserveEffect: operationId: string -> Result<OrdinaryEffectObservation, string>
    abstract DispatchMerge: operationId: string * repositoryId: int64 * pullRequestNumber: int * expectedHead: string -> OrdinaryDispatchOutcome
    abstract PersistSettlement: expectedGeneration: int64 * operationId: string * mergeCommit: string -> OrdinaryCasOutcome

[<RequireQualifiedAccess>]
module OrdinaryDelivery =
    let private schema = "fsgg.coordination.ordinary-delivery-plan/1"

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private validSha length (value: string) =
        validText value
        && value.Length = length
        && value |> Seq.forall Uri.IsHexDigit

    let private checkText =
        function
        | CheckPassed -> "passed"
        | CheckFailed -> "failed"
        | CheckPending -> "pending"
        | CheckUnknown -> "unknown"

    let private sha256 (bytes: byte array) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let planDigest bytes = sha256 bytes

    let private canonical (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = false))
        |> ShardedJournalAdapter.canonicalJson

    let private normalizedChecks (observation: OrdinaryDeliveryObservation) =
        observation.Checks
        |> List.sortBy (fun item -> item.Identity, item.AppId)

    let inspect (observation: OrdinaryDeliveryObservation) =
        let checks = normalizedChecks observation

        let failures =
            [
                if not observation.SourceComplete then yield MissingSourcePages
                if not observation.ChecksComplete then yield MissingCheckPages
                if not observation.Authorized then yield UnauthorizedObservation
                if not observation.Supported then yield UnsupportedObservation
                if
                    not (validText observation.Repository)
                    || observation.Repository.Split('/').Length <> 2
                    || observation.RepositoryId <= 0L
                    || observation.PullRequestNumber <= 0
                    || not (validText observation.PullRequestNodeId)
                then
                    yield InvalidIdentity "repository-or-pull-request"
                if not (validText observation.BaseRef) then yield InvalidIdentity "base-ref"
                if not (validSha 40 observation.BaseSha || validSha 64 observation.BaseSha) then
                    yield InvalidIdentity "base-sha"
                if not (validSha 40 observation.HeadSha || validSha 64 observation.HeadSha) then
                    yield InvalidIdentity "head-sha"
                if not (validSha 40 observation.PolicyRevision || validSha 64 observation.PolicyRevision) then
                    yield InvalidIdentity "policy-revision"
                if observation.JournalGeneration < 0L then yield InvalidIdentity "journal-generation"
                if not (validSha 40 observation.JournalHead || validSha 64 observation.JournalHead) then
                    yield InvalidIdentity "journal-head"
                if not (Set.contains observation.Epoch (Set.ofList [ "OperatingV1"; "Frozen"; "OpenV2" ])) then
                    yield InvalidIdentity "epoch"
                if List.isEmpty checks then yield MissingCheckPages
                for check in checks do
                    if not (validText check.Identity) || check.AppId <= 0L then
                        yield InvalidIdentity "check"
                if checks |> List.map (fun item -> item.Identity, item.AppId) |> List.distinct |> List.length <> checks.Length then
                    yield InvalidIdentity "duplicate-check-identity"
            ]

        if List.isEmpty failures then Ok { observation with Checks = checks } else Error failures

    let private unsignedNode (observation: OrdinaryDeliveryObservation) operationId =
        let checks = JsonArray()

        for check in normalizedChecks observation do
            let item = JsonObject()
            item.Add("appId", check.AppId)
            item.Add("conclusion", checkText check.Conclusion)
            item.Add("identity", check.Identity)
            checks.Add item

        let root = JsonObject()
        root.Add("baseRef", observation.BaseRef)
        root.Add("baseSha", observation.BaseSha.ToLowerInvariant())
        root.Add("checks", checks)
        root.Add("effect", "ordinary-source-delivery")
        root.Add("epoch", observation.Epoch)
        root.Add("headSha", observation.HeadSha.ToLowerInvariant())
        root.Add("journalGeneration", observation.JournalGeneration)
        root.Add("journalHead", observation.JournalHead.ToLowerInvariant())
        root.Add("operationId", operationId)
        root.Add("policyRevision", observation.PolicyRevision.ToLowerInvariant())
        root.Add("pullRequestNodeId", observation.PullRequestNodeId)
        root.Add("pullRequestNumber", observation.PullRequestNumber)
        root.Add("repository", observation.Repository.ToLowerInvariant())
        root.Add("repositoryId", observation.RepositoryId)
        root.Add("schema", schema)
        root

    let private operationId (observation: OrdinaryDeliveryObservation) =
        let root = unsignedNode observation "pending"
        canonical root |> Result.defaultWith (fun _ -> invalidOp "canonical plan") |> sha256 |> (+) "ordinary-delivery:"

    let plan (observation: OrdinaryDeliveryObservation) =
        match inspect observation with
        | Error failures -> Error failures
        | Ok observed ->
            let operation = operationId observed
            let unsigned = unsignedNode observed operation
            let unsignedBytes = canonical unsigned |> Result.defaultWith (fun _ -> invalidOp "canonical plan")
            let seal = sha256 unsignedBytes
            unsigned.AsObject().Add("seal", seal)
            let bytes = canonical unsigned |> Result.defaultWith (fun _ -> invalidOp "canonical plan")

            Ok(
                {
                    Schema = schema
                    OperationId = operation
                    Repository = observed.Repository.ToLowerInvariant()
                    RepositoryId = observed.RepositoryId
                    PullRequestNumber = observed.PullRequestNumber
                    PullRequestNodeId = observed.PullRequestNodeId
                    BaseRef = observed.BaseRef
                    BaseSha = observed.BaseSha.ToLowerInvariant()
                    HeadSha = observed.HeadSha.ToLowerInvariant()
                    PolicyRevision = observed.PolicyRevision.ToLowerInvariant()
                    CheckIdentities = observed.Checks |> List.map (fun check -> $"{check.Identity}@{check.AppId}:{checkText check.Conclusion}")
                    Epoch = observed.Epoch
                    JournalGeneration = observed.JournalGeneration
                    JournalHead = observed.JournalHead.ToLowerInvariant()
                    Effect = "ordinary-source-delivery"
                    Seal = seal
                },
                bytes
            )

    let private requiredString (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None

    let readPlan (bytes: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement
            let expectedNames =
                set [ "baseRef"; "baseSha"; "checks"; "effect"; "epoch"; "headSha"; "journalGeneration"; "journalHead"; "operationId"; "policyRevision"; "pullRequestNodeId"; "pullRequestNumber"; "repository"; "repositoryId"; "schema"; "seal" ]
            let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if root.ValueKind <> JsonValueKind.Object || Set.ofList names <> expectedNames || List.distinct names <> names then
                Error [ AlteredPlan ]
            else
                let checks = root.GetProperty("checks").EnumerateArray() |> Seq.toList
                let checkIdentities =
                    checks
                    |> List.map (fun item ->
                        let identity = item.GetProperty("identity").GetString()
                        let appId = item.GetProperty("appId").GetInt64()
                        let conclusion = item.GetProperty("conclusion").GetString()
                        $"{identity}@{appId}:{conclusion}")
                let unsigned = JsonNode.Parse(bytes.Span).AsObject()
                let seal = unsigned["seal"].GetValue<string>()
                unsigned.Remove("seal") |> ignore
                let unsignedBytes = canonical unsigned |> Result.defaultWith (fun _ -> Array.empty)
                let canonicalFull = canonical (JsonNode.Parse(bytes.Span)) |> Result.defaultWith (fun _ -> Array.empty)

                if sha256 unsignedBytes <> seal || not (bytes.Span.SequenceEqual(canonicalFull)) then
                    Error [ AlteredPlan ]
                else
                    let value =
                        {
                            Schema = requiredString "schema" root |> Option.defaultValue ""
                            OperationId = requiredString "operationId" root |> Option.defaultValue ""
                            Repository = requiredString "repository" root |> Option.defaultValue ""
                            RepositoryId = root.GetProperty("repositoryId").GetInt64()
                            PullRequestNumber = root.GetProperty("pullRequestNumber").GetInt32()
                            PullRequestNodeId = requiredString "pullRequestNodeId" root |> Option.defaultValue ""
                            BaseRef = requiredString "baseRef" root |> Option.defaultValue ""
                            BaseSha = requiredString "baseSha" root |> Option.defaultValue ""
                            HeadSha = requiredString "headSha" root |> Option.defaultValue ""
                            PolicyRevision = requiredString "policyRevision" root |> Option.defaultValue ""
                            CheckIdentities = checkIdentities
                            Epoch = requiredString "epoch" root |> Option.defaultValue ""
                            JournalGeneration = root.GetProperty("journalGeneration").GetInt64()
                            JournalHead = requiredString "journalHead" root |> Option.defaultValue ""
                            Effect = requiredString "effect" root |> Option.defaultValue ""
                            Seal = seal
                        }

                    if value.Schema <> schema || value.Effect <> "ordinary-source-delivery" || not (value.OperationId.StartsWith("ordinary-delivery:", StringComparison.Ordinal)) then
                        Error [ AlteredPlan ]
                    else Ok value
        with _ -> Error [ AlteredPlan ]

    let private decisionMatches (plan: OrdinaryDeliveryPlan) (observation: OrdinaryDeliveryObservation) =
        match inspect observation with
        | Error failures -> Error failures
        | Ok observed ->
            let checks = observed.Checks |> List.map (fun check -> $"{check.Identity}@{check.AppId}:{checkText check.Conclusion}")
            if observed.Repository.ToLowerInvariant() <> plan.Repository || observed.RepositoryId <> plan.RepositoryId || observed.PullRequestNumber <> plan.PullRequestNumber || observed.PullRequestNodeId <> plan.PullRequestNodeId then
                Error [ CrossSubjectObservation ]
            elif observed.PolicyRevision.ToLowerInvariant() <> plan.PolicyRevision then Error [ StalePolicy ]
            elif observed.HeadSha.ToLowerInvariant() <> plan.HeadSha || observed.BaseSha.ToLowerInvariant() <> plan.BaseSha || observed.BaseRef <> plan.BaseRef then Error [ ChangedSource ]
            elif checks <> plan.CheckIdentities then Error [ ChangedSource ]
            elif observed.Epoch <> "OpenV2" then Error [ PreOpenV2Refusal ]
            else
                let failed = observed.Checks |> List.tryFind (fun check -> check.Conclusion <> CheckPassed)
                match failed with
                | Some check -> Error [ RequiredCheckNotPassed check.Identity ]
                | None -> Ok observed

    let advance bytes cut (runtime: IOrdinaryDeliveryRuntime) =
        let bindRuntime result = result |> Result.mapError (fun reason -> [ JournalUnavailable reason ])

        let settle (plan: OrdinaryDeliveryPlan) (authority: OrdinaryJournalAuthority) mergeCommit =
            if cut = StopAfterEffect then Ok(AdvanceInterrupted "after-effect-before-receipt")
            else
                match runtime.PersistSettlement(authority.Generation, plan.OperationId, mergeCommit) with
                | CasAccepted settled when settled.Stage = OrdinarySettled -> Ok(AdvanceSettled mergeCommit)
                | CasConflict ->
                    match runtime.ObserveJournal plan.OperationId |> bindRuntime with
                    | Ok(Some settled) when settled.Stage = OrdinarySettled && settled.PlanDigest = plan.Seal && settled.MergeCommit = Some mergeCommit -> Ok(AdvanceAlreadySettled mergeCommit)
                    | Ok _ -> Error [ JournalConflict ]
                    | Error error -> Error error
                | CasUnknown -> Ok(AdvancePending "settlement-outcome-unknown")
                | _ -> Error [ JournalConflict ]

        let continueFrom (plan: OrdinaryDeliveryPlan) (authority: OrdinaryJournalAuthority) =
            match runtime.Observe() |> bindRuntime |> Result.bind (decisionMatches plan) with
            | Error failures -> Error failures
            | Ok _ ->
                match runtime.ObserveEffect plan.OperationId |> bindRuntime with
                | Error failures -> Error failures
                | Ok(EffectApplied mergeCommit) -> settle plan authority mergeCommit
                | Ok EffectOutcomeUnknown -> Ok(AdvancePending "effect-observation-unknown")
                | Ok EffectProvenAbsent ->
                    let pending =
                        if authority.Stage = OrdinaryEffectPending then CasAccepted authority
                        else runtime.MarkPending(authority.Generation, plan.OperationId)

                    match pending with
                    | CasConflict -> Error [ JournalConflict ]
                    | CasUnknown -> Ok(AdvancePending "pending-journal-outcome-unknown")
                    | CasAccepted pendingAuthority ->
                        if cut = StopAfterIntent then Ok(AdvanceInterrupted "before-dispatch")
                        else
                            match runtime.Observe() |> bindRuntime |> Result.bind (decisionMatches plan) with
                            | Error failures -> Error failures
                            | Ok _ ->
                                match runtime.DispatchMerge(plan.OperationId, plan.RepositoryId, plan.PullRequestNumber, plan.HeadSha) with
                                | DispatchRefused reason -> Error [ EffectRefused reason ]
                                | DispatchOutcomeUnknown -> Ok(AdvancePending "dispatch-outcome-unknown")
                                | DispatchApplied mergeCommit ->
                                    if cut = StopAfterDispatch then Ok(AdvanceInterrupted "after-dispatch-before-response")
                                    else settle plan pendingAuthority mergeCommit

        match readPlan bytes with
        | Error failures -> Error failures
        | Ok plan ->
            match runtime.Observe() |> bindRuntime |> Result.bind (decisionMatches plan) with
            | Error failures -> Error failures
            | Ok _ ->
                match runtime.ObserveJournal plan.OperationId |> bindRuntime with
                | Error failures -> Error failures
                | Ok(Some authority) when authority.PlanDigest <> plan.Seal -> Error [ JournalConflict ]
                | Ok(Some authority) when authority.Stage = OrdinarySettled ->
                    match authority.MergeCommit with
                    | Some commit -> Ok(AdvanceAlreadySettled commit)
                    | None -> Error [ JournalConflict ]
                | Ok(Some authority) -> continueFrom plan authority
                | Ok None ->
                    match runtime.PersistIntent(plan.JournalGeneration, plan.Seal, plan.OperationId) with
                    | CasAccepted authority -> continueFrom plan authority
                    | CasUnknown -> Ok(AdvancePending "intent-outcome-unknown")
                    | CasConflict ->
                        match runtime.ObserveJournal plan.OperationId |> bindRuntime with
                        | Ok(Some authority) when authority.PlanDigest = plan.Seal -> continueFrom plan authority
                        | Ok _ -> Error [ JournalConflict ]
                        | Error failures -> Error failures
