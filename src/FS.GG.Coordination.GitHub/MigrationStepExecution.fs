namespace FS.GG.Coordination.GitHub

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type MigrationEffect =
    | SetIssueType of repositoryId:int64 * issueNodeId:string * typeNodeId:string
    | SetIssueField of repositoryId:int64 * issueNodeId:string * fieldNodeId:string * valueSha256:string
    | AddBlockingEdge of repositoryId:int64 * blockerNodeId:string * blockedNodeId:string
    | SetProjectField of projectNodeId:string * itemNodeId:string * fieldNodeId:string * valueNodeId:string
    | ApplyRepositorySettings of repositoryId:int64 * settingsPlanSha256:string
    | AdoptReceiver of repositoryId:int64 * expectedCommit:string * desiredCommit:string
    | SealArchive of authorityId:string * archiveSha256:string * verifierSha256:string

type MigrationExecutionStep =
    { OperationId: string
      IdempotencyKey: string
      ManifestSeal: string
      Effect: MigrationEffect
      TargetIdentity: string
      ExpectedTargetRevision: string
      ExpectedTargetSha256: string
      DesiredTargetSha256: string
      EpochGeneration: int64
      EpochCommit: string
      AuthorityFence: MigrationAuthorityFence
      JournalGeneration: int64
      JournalHead: string
      Seal: string }

and MigrationAuthorityFence =
    { AdmissionGeneration: int64
      AdmissionCommit: string
      OperationGeneration: int64
      OperationCommit: string
      Claim: (int64 * string) option
      SealCommit: string
      RegistryCommit: string }

type MigrationFenceObservation =
    { Fence: MigrationAuthorityFence
      Complete: bool
      Authorized: bool }

type MigrationEpochObservation =
    { Phase: string
      ManifestSeal: string
      Generation: int64
      Commit: string
      Complete: bool
      Authorized: bool }

type MigrationTargetObservation =
    { Identity: string
      Revision: string
      Sha256: string
      Complete: bool
      Authorized: bool }

[<RequireQualifiedAccess>]
type MigrationJournalStage = IntentPersisted | InFlight | Settled

type MigrationJournalAuthority =
    { OperationId: string
      StepSeal: string
      Generation: int64
      Commit: string
      Stage: MigrationJournalStage
      ResultSha256: string option }

[<RequireQualifiedAccess>]
type MigrationCasOutcome =
    | Accepted of MigrationJournalAuthority
    | Conflict
    | Unknown

[<RequireQualifiedAccess>]
type MigrationEffectObservation =
    | Applied of resultSha256:string
    | ProvenAbsent
    | Partial of reason:string
    | Unknown

[<RequireQualifiedAccess>]
type MigrationDispatchOutcome =
    | Applied
    | Refused of reason:string
    | Unknown

[<RequireQualifiedAccess>]
type MigrationAdvanceCut =
    | NoCut
    | StopBeforeIntent
    | StopAfterIntent
    | StopAfterInFlight
    | StopAfterDispatch
    | StopAfterReadback
    | StopBeforeReceipt
    | StopAfterEffect

[<RequireQualifiedAccess>]
type MigrationAdvanceResult =
    | Settled of resultSha256:string
    | AlreadySettled of resultSha256:string
    | Pending of reason:string
    | Interrupted of point:string

[<RequireQualifiedAccess>]
type MigrationExecutionFailure =
    | InvalidStep
    | StaleEpoch
    | UnauthorizedEpoch
    | IncompleteEpoch
    | StaleAuthorityFence
    | UnauthorizedAuthorityFence
    | IncompleteAuthorityFence
    | ChangedTarget
    | UnauthorizedTarget
    | IncompleteTarget
    | JournalConflict
    | JournalUnavailable of reason:string
    | EffectRefused of reason:string

type IMigrationStepRuntime =
    abstract ObserveEpoch: unit -> Result<MigrationEpochObservation, string>
    abstract ObserveAuthorityFence: unit -> Result<MigrationFenceObservation, string>
    abstract ObserveTarget: MigrationEffect -> Result<MigrationTargetObservation, string>
    abstract ObserveJournal: operationId:string -> Result<MigrationJournalAuthority option, string>
    abstract PersistIntent:
        expectedGeneration:int64 * expectedHead:string * operationId:string * stepSeal:string -> MigrationCasOutcome
    abstract MarkInFlight:
        expectedGeneration:int64 * expectedHead:string * operationId:string -> MigrationCasOutcome
    abstract ObserveEffect:
        operationId:string * effect:MigrationEffect -> Result<MigrationEffectObservation, string>
    abstract Dispatch:
        step:MigrationExecutionStep * grantGeneration:int64 * grantCommit:string -> MigrationDispatchOutcome
    abstract PersistSettlement:
        expectedGeneration:int64 * expectedHead:string * operationId:string * resultSha256:string -> MigrationCasOutcome

module MigrationStepExecution =
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private isSha length value =
        not (isNull value) && Regex.IsMatch(value, $"^[0-9a-f]{{{length}}}$", RegexOptions.CultureInvariant)

    let private validText (value: string) =
        not (String.IsNullOrWhiteSpace value) && value = value.Trim()

    let private effectParts = function
        | MigrationEffect.SetIssueType(repository, issue, kind) ->
            $"repository:{repository}/issue:{issue}/type", [ "MUT-Set"; string repository; issue; kind ]
        | MigrationEffect.SetIssueField(repository, issue, field, value) ->
            $"repository:{repository}/issue:{issue}/field:{field}", [ "MUT-Set"; string repository; issue; field; value ]
        | MigrationEffect.AddBlockingEdge(repository, blocker, blocked) ->
            $"repository:{repository}/blocks:{blocker}:{blocked}", [ "MUT-AddEdge"; string repository; blocker; blocked ]
        | MigrationEffect.SetProjectField(project, item, field, value) ->
            $"project:{project}/item:{item}/field:{field}", [ "MUT-Set"; project; item; field; value ]
        | MigrationEffect.ApplyRepositorySettings(repository, settings) ->
            $"repository:{repository}/settings", [ "MUT-Set"; string repository; settings ]
        | MigrationEffect.AdoptReceiver(repository, expected, desired) ->
            $"repository:{repository}/receiver", [ "MUT-Set"; string repository; expected; desired ]
        | MigrationEffect.SealArchive(authority, archive, verifier) ->
            $"archive:{authority}", [ "MUT-Create"; authority; archive; verifier ]

    let private effectValid = function
        | MigrationEffect.SetIssueType(repository, issue, kind) -> repository > 0L && validText issue && validText kind
        | MigrationEffect.SetIssueField(repository, issue, field, value) ->
            repository > 0L && validText issue && validText field && isSha 64 value
        | MigrationEffect.AddBlockingEdge(repository, blocker, blocked) ->
            repository > 0L && validText blocker && validText blocked && blocker <> blocked
        | MigrationEffect.SetProjectField(project, item, field, value) ->
            [ project; item; field; value ] |> List.forall validText
        | MigrationEffect.ApplyRepositorySettings(repository, settings) -> repository > 0L && isSha 64 settings
        | MigrationEffect.AdoptReceiver(repository, expected, desired) ->
            repository > 0L && isSha 40 expected && isSha 40 desired && expected <> desired
        | MigrationEffect.SealArchive(authority, archive, verifier) ->
            validText authority && isSha 64 archive && isSha 64 verifier

    let private fingerprint (step: MigrationExecutionStep) =
        let _, effect = effectParts step.Effect
        let fence = step.AuthorityFence
        let claim =
            match fence.Claim with
            | None -> [ "no-claim-required" ]
            | Some(generation, commit) -> [ "claim-required"; string generation; commit ]
        [ step.OperationId; step.IdempotencyKey; step.ManifestSeal; step.TargetIdentity
          step.ExpectedTargetRevision; step.ExpectedTargetSha256; step.DesiredTargetSha256
          string step.EpochGeneration; step.EpochCommit; string step.JournalGeneration; step.JournalHead
          string fence.AdmissionGeneration; fence.AdmissionCommit
          string fence.OperationGeneration; fence.OperationCommit
          fence.SealCommit; fence.RegistryCommit ] @ claim @ effect
        |> List.map (fun value -> $"{Encoding.UTF8.GetByteCount value}:{value}")
        |> String.concat ""
        |> sha

    let private shapeValid (step: MigrationExecutionStep) =
        let target, _ = effectParts step.Effect
        let fence = step.AuthorityFence
        validText step.OperationId && validText step.IdempotencyKey
        && isSha 64 step.ManifestSeal && effectValid step.Effect
        && step.TargetIdentity = target && validText step.ExpectedTargetRevision
        && isSha 64 step.ExpectedTargetSha256 && isSha 64 step.DesiredTargetSha256
        && step.ExpectedTargetSha256 <> step.DesiredTargetSha256
        && step.EpochGeneration > 0L && isSha 40 step.EpochCommit
        && fence.AdmissionGeneration > 0L && isSha 40 fence.AdmissionCommit
        && fence.OperationGeneration > 0L && isSha 40 fence.OperationCommit
        && (fence.Claim |> Option.forall (fun (generation, commit) -> generation > 0L && isSha 40 commit))
        && isSha 40 fence.SealCommit && isSha 40 fence.RegistryCommit
        && step.JournalGeneration >= 0L && isSha 40 step.JournalHead

    let sealStep step =
        if not (shapeValid step) then Error [ MigrationExecutionFailure.InvalidStep ]
        else Ok { step with Seal=fingerprint step }

    let private inspectStep step =
        match sealStep step with
        | Ok sealedStep when sealedStep.Seal = step.Seal -> Ok step
        | _ -> Error [ MigrationExecutionFailure.InvalidStep ]

    let private journalFailure reason = Error [ MigrationExecutionFailure.JournalUnavailable reason ]

    let private inspectEpoch (step: MigrationExecutionStep) (runtime: IMigrationStepRuntime) =
        match runtime.ObserveEpoch() with
        | Error reason -> journalFailure reason
        | Ok epoch when not epoch.Complete -> Error [ MigrationExecutionFailure.IncompleteEpoch ]
        | Ok epoch when not epoch.Authorized -> Error [ MigrationExecutionFailure.UnauthorizedEpoch ]
        | Ok epoch when epoch.Phase <> "SwitchedV2" || epoch.ManifestSeal <> step.ManifestSeal
                        || epoch.Generation <> step.EpochGeneration || epoch.Commit <> step.EpochCommit ->
            Error [ MigrationExecutionFailure.StaleEpoch ]
        | Ok epoch -> Ok epoch

    let private inspectFence (step: MigrationExecutionStep) (runtime: IMigrationStepRuntime) =
        match runtime.ObserveAuthorityFence() with
        | Error reason -> journalFailure reason
        | Ok observed when not observed.Complete -> Error [ MigrationExecutionFailure.IncompleteAuthorityFence ]
        | Ok observed when not observed.Authorized -> Error [ MigrationExecutionFailure.UnauthorizedAuthorityFence ]
        | Ok observed when observed.Fence <> step.AuthorityFence ->
            Error [ MigrationExecutionFailure.StaleAuthorityFence ]
        | Ok observed -> Ok observed

    let private inspectTarget expectedDesired (step: MigrationExecutionStep) (runtime: IMigrationStepRuntime) =
        match runtime.ObserveTarget step.Effect with
        | Error reason -> journalFailure reason
        | Ok target when not target.Complete -> Error [ MigrationExecutionFailure.IncompleteTarget ]
        | Ok target when not target.Authorized -> Error [ MigrationExecutionFailure.UnauthorizedTarget ]
        | Ok target when target.Identity <> step.TargetIdentity -> Error [ MigrationExecutionFailure.ChangedTarget ]
        | Ok target when expectedDesired ->
            if target.Revision <> step.ExpectedTargetRevision
               && target.Sha256 = step.DesiredTargetSha256 then Ok target
            else Error [ MigrationExecutionFailure.ChangedTarget ]
        | Ok target ->
            if target.Revision = step.ExpectedTargetRevision && target.Sha256 = step.ExpectedTargetSha256 then Ok target
            else Error [ MigrationExecutionFailure.ChangedTarget ]

    let private inspectJournal (step: MigrationExecutionStep) (authority: MigrationJournalAuthority) =
        let expectedGeneration =
            match authority.Stage with
            | MigrationJournalStage.IntentPersisted -> step.JournalGeneration + 1L
            | MigrationJournalStage.InFlight -> step.JournalGeneration + 2L
            | MigrationJournalStage.Settled -> step.JournalGeneration + 3L
        if authority.OperationId <> step.OperationId || authority.StepSeal <> step.Seal
           || authority.Generation <> expectedGeneration || not (isSha 40 authority.Commit)
           || (authority.Stage = MigrationJournalStage.Settled) <> authority.ResultSha256.IsSome then
            Error [ MigrationExecutionFailure.JournalConflict ]
        elif authority.Stage = MigrationJournalStage.Settled
             && authority.ResultSha256 <> Some step.DesiredTargetSha256 then
            Error [ MigrationExecutionFailure.JournalConflict ]
        else Ok authority

    let private observeJournal (step: MigrationExecutionStep) (runtime: IMigrationStepRuntime) =
        match runtime.ObserveJournal step.OperationId with
        | Error reason -> journalFailure reason
        | Ok(Some value) -> inspectJournal step value |> Result.map Some
        | Ok None -> Ok None

    let private inspectCas expectedGeneration priorHead expectedStage (step: MigrationExecutionStep) outcome =
        match outcome with
        | MigrationCasOutcome.Unknown -> Ok None
        | MigrationCasOutcome.Conflict -> Error [ MigrationExecutionFailure.JournalConflict ]
        | MigrationCasOutcome.Accepted authority ->
            match inspectJournal step authority with
            | Ok value when value.Generation = expectedGeneration + 1L
                            && value.Commit <> priorHead && value.Stage = expectedStage -> Ok(Some value)
            | _ -> Error [ MigrationExecutionFailure.JournalConflict ]

    let advance (step: MigrationExecutionStep) cut (runtime: IMigrationStepRuntime) =
        let settle (authority: MigrationJournalAuthority) =
            if authority.Stage <> MigrationJournalStage.InFlight then
                Error [ MigrationExecutionFailure.JournalConflict ]
            elif cut = MigrationAdvanceCut.StopAfterReadback then
                Ok(MigrationAdvanceResult.Interrupted "after-effect-readback-before-target-readback")
            elif cut = MigrationAdvanceCut.StopAfterEffect then
                Ok(MigrationAdvanceResult.Interrupted "after-effect-before-receipt")
            else
                match inspectEpoch step runtime, inspectFence step runtime, inspectTarget true step runtime with
                | Error failures, _, _ | _, Error failures, _ | _, _, Error failures -> Error failures
                | Ok _, Ok _, Ok _ when cut = MigrationAdvanceCut.StopBeforeReceipt ->
                    Ok(MigrationAdvanceResult.Interrupted "after-target-readback-before-receipt")
                | Ok _, Ok _, Ok _ ->
                    match runtime.PersistSettlement(authority.Generation, authority.Commit, step.OperationId, step.DesiredTargetSha256) with
                    | MigrationCasOutcome.Unknown -> Ok(MigrationAdvanceResult.Pending "settlement-outcome-unknown")
                    | outcome ->
                        match inspectCas authority.Generation authority.Commit MigrationJournalStage.Settled step outcome with
                        | Ok(Some _) -> Ok(MigrationAdvanceResult.Settled step.DesiredTargetSha256)
                        | Ok None -> Ok(MigrationAdvanceResult.Pending "settlement-outcome-unknown")
                        | Error _ ->
                            match observeJournal step runtime with
                            | Ok(Some later) when later.Stage = MigrationJournalStage.Settled ->
                                Ok(MigrationAdvanceResult.AlreadySettled step.DesiredTargetSha256)
                            | Error failures -> Error failures
                            | _ -> Error [ MigrationExecutionFailure.JournalConflict ]

        let effectRead () =
            match runtime.ObserveEffect(step.OperationId, step.Effect) with
            | Error reason -> journalFailure reason
            | Ok value -> Ok value

        let continueFrom (authority: MigrationJournalAuthority) =
            match inspectEpoch step runtime, inspectFence step runtime, effectRead () with
            | Error failures, _, _ | _, Error failures, _ | _, _, Error failures -> Error failures
            | Ok _, Ok _, Ok(MigrationEffectObservation.Applied result) when result = step.DesiredTargetSha256 ->
                settle authority
            | Ok _, Ok _, Ok(MigrationEffectObservation.Applied _) ->
                Error [ MigrationExecutionFailure.EffectRefused "wrong-effect-readback" ]
            | Ok _, Ok _, Ok(MigrationEffectObservation.Partial _) ->
                Ok(MigrationAdvanceResult.Pending "effect-partial")
            | Ok _, Ok _, Ok MigrationEffectObservation.Unknown ->
                Ok(MigrationAdvanceResult.Pending "effect-observation-unknown")
            | Ok _, Ok _, Ok MigrationEffectObservation.ProvenAbsent when authority.Stage = MigrationJournalStage.InFlight ->
                // A restored in-flight request is recovery-only. Absence without exclusion of a delayed
                // original cannot authorize another send under the canonical protocol.
                Ok(MigrationAdvanceResult.Pending "in-flight-absence-needs-exclusion")
            | Ok _, Ok _, Ok MigrationEffectObservation.ProvenAbsent ->
                match inspectTarget false step runtime with
                | Error failures -> Error failures
                | Ok _ ->
                    match runtime.MarkInFlight(authority.Generation, authority.Commit, step.OperationId) with
                    | MigrationCasOutcome.Unknown -> Ok(MigrationAdvanceResult.Pending "in-flight-journal-outcome-unknown")
                    | outcome ->
                        match inspectCas authority.Generation authority.Commit MigrationJournalStage.InFlight step outcome with
                        | Error failures -> Error failures
                        | Ok None -> Ok(MigrationAdvanceResult.Pending "in-flight-journal-outcome-unknown")
                        | Ok(Some grant) when cut = MigrationAdvanceCut.StopAfterInFlight ->
                            Ok(MigrationAdvanceResult.Interrupted "after-in-flight-before-dispatch")
                        | Ok(Some grant) ->
                            match inspectEpoch step runtime, inspectFence step runtime,
                                  observeJournal step runtime, inspectTarget false step runtime with
                            | Error failures, _, _, _ | _, Error failures, _, _
                            | _, _, Error failures, _ | _, _, _, Error failures -> Error failures
                            | Ok _, Ok _, Ok(Some fresh), Ok _ when fresh = grant ->
                                match runtime.Dispatch(step, grant.Generation, grant.Commit) with
                                | MigrationDispatchOutcome.Refused reason ->
                                    Error [ MigrationExecutionFailure.EffectRefused reason ]
                                | MigrationDispatchOutcome.Unknown ->
                                    Ok(MigrationAdvanceResult.Pending "dispatch-outcome-unknown")
                                | MigrationDispatchOutcome.Applied when cut = MigrationAdvanceCut.StopAfterDispatch ->
                                    Ok(MigrationAdvanceResult.Interrupted "after-dispatch-before-response")
                                | MigrationDispatchOutcome.Applied ->
                                    match effectRead () with
                                    | Ok(MigrationEffectObservation.Applied result) when result = step.DesiredTargetSha256 -> settle grant
                                    | Ok(MigrationEffectObservation.Applied _) ->
                                        Error [ MigrationExecutionFailure.EffectRefused "wrong-effect-readback" ]
                                    | Ok _ -> Ok(MigrationAdvanceResult.Pending "effect-readback-unsettled")
                                    | Error failures -> Error failures
                            | _ -> Error [ MigrationExecutionFailure.JournalConflict ]

        match inspectStep step with
        | Error failures -> Error failures
        | Ok _ ->
            match inspectEpoch step runtime, inspectFence step runtime, observeJournal step runtime with
            | Error failures, _, _ | _, Error failures, _ | _, _, Error failures -> Error failures
            | Ok _, Ok _, Ok(Some authority) when authority.Stage = MigrationJournalStage.Settled ->
                match effectRead (), inspectTarget true step runtime with
                | Ok(MigrationEffectObservation.Applied result), Ok _ when result = step.DesiredTargetSha256 ->
                    Ok(MigrationAdvanceResult.AlreadySettled result)
                | Error failures, _ | _, Error failures -> Error failures
                | _ -> Error [ MigrationExecutionFailure.JournalConflict ]
            | Ok _, Ok _, Ok(Some authority) -> continueFrom authority
            | Ok _, Ok _, Ok None ->
                match inspectTarget false step runtime with
                | Error failures -> Error failures
                | Ok _ when cut = MigrationAdvanceCut.StopBeforeIntent ->
                    Ok(MigrationAdvanceResult.Interrupted "before-intent")
                | Ok _ ->
                    match runtime.PersistIntent(step.JournalGeneration, step.JournalHead, step.OperationId, step.Seal) with
                    | MigrationCasOutcome.Unknown -> Ok(MigrationAdvanceResult.Pending "intent-outcome-unknown")
                    | MigrationCasOutcome.Conflict ->
                        match observeJournal step runtime with
                        | Ok(Some authority) -> continueFrom authority
                        | Error failures -> Error failures
                        | _ -> Error [ MigrationExecutionFailure.JournalConflict ]
                    | outcome ->
                        match inspectCas step.JournalGeneration step.JournalHead MigrationJournalStage.IntentPersisted step outcome with
                        | Error failures -> Error failures
                        | Ok None -> Ok(MigrationAdvanceResult.Pending "intent-outcome-unknown")
                        | Ok(Some authority) when cut = MigrationAdvanceCut.StopAfterIntent ->
                            Ok(MigrationAdvanceResult.Interrupted "after-intent-before-dispatch")
                        | Ok(Some authority) -> continueFrom authority

type MigrationStepAuthorityPort =
    { ObserveEpoch: unit -> Result<MigrationEpochObservation, string>
      ObserveAuthorityFence: unit -> Result<MigrationFenceObservation, string>
      ObserveJournal: string -> Result<MigrationJournalAuthority option, string>
      PersistIntent: int64 -> string -> string -> string -> MigrationCasOutcome
      MarkInFlight: int64 -> string -> string -> MigrationCasOutcome
      PersistSettlement: int64 -> string -> string -> string -> MigrationCasOutcome }

type IMigrationStepProviderTransport =
    abstract Send: GitHubRequest -> TransportOutcome

type HttpMigrationStepProviderTransport() =
    let maxResponseBytes = 1024 * 1024
    let handler = new HttpClientHandler(AllowAutoRedirect=false)
    let client = new HttpClient(handler, true)

    let readBoundedBody (content: HttpContent) =
        match content.Headers.ContentLength with
        | value when value.HasValue && value.Value > int64 maxResponseBytes ->
            raise (HttpRequestException "migration-step-response-too-large")
        | _ -> ()
        use source = content.ReadAsStream()
        use collected = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let count = source.Read(buffer, 0, buffer.Length)
            if count = 0 then
                finished <- true
            elif collected.Length + int64 count > int64 maxResponseBytes then
                raise (HttpRequestException "migration-step-response-too-large")
            else
                collected.Write(buffer, 0, count)
        Encoding.UTF8.GetString(collected.ToArray())

    interface IMigrationStepProviderTransport with
        member _.Send request =
            match Transport.validateRequest request with
            | Error _ -> NetworkFailure
            | Ok () ->
                try
                    let uri, headers, body =
                        match request with
                        | GraphQL value ->
                            let payload = JsonObject()
                            payload.Add("query", value.Document)
                            let variables = JsonObject()
                            for KeyValue(name, item) in value.Variables do
                                variables.Add(name, item)
                            payload.Add("variables", variables)
                            value.Uri, value.Headers, payload.ToJsonString()
                        | Rest _ -> invalidArg (nameof request) "migration step provider accepts GraphQL only"
                    use message = new HttpRequestMessage(HttpMethod.Post, uri)
                    for KeyValue(name, value) in headers do
                        message.Headers.TryAddWithoutValidation(name, value) |> ignore
                    message.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                    use response = client.Send message
                    if isNull response.RequestMessage
                       || isNull response.RequestMessage.RequestUri
                       || response.RequestMessage.RequestUri.AbsoluteUri <> uri.AbsoluteUri then
                        raise (HttpRequestException "redirected-migration-step-uri")
                    let responseHeaders =
                        Seq.append response.Headers response.Content.Headers
                        |> Seq.map (fun item -> item.Key.ToLowerInvariant(), String.concat "," item.Value)
                        |> Map.ofSeq
                    let tryInt name =
                        Map.tryFind name responseHeaders
                        |> Option.bind (fun value -> match Int32.TryParse value with true, parsed -> Some parsed | _ -> None)
                    let tryDate name =
                        Map.tryFind name responseHeaders
                        |> Option.bind (fun value ->
                            match Int64.TryParse value with
                            | true, parsed -> Some(DateTimeOffset.FromUnixTimeSeconds parsed)
                            | _ -> None)
                    Response
                        { StatusCode=int response.StatusCode
                          Headers=responseHeaders
                          Body=readBoundedBody response.Content
                          ETag=Map.tryFind "etag" responseHeaders
                          RateBudget=
                            { Limit=tryInt "x-ratelimit-limit"
                              Remaining=tryInt "x-ratelimit-remaining"
                              ResetAt=tryDate "x-ratelimit-reset"
                              Cost=Some 1 } }
                with
                | :? TaskCanceledException -> TimedOut
                | :? HttpRequestException -> NetworkFailure
                | :? ArgumentException -> NetworkFailure

    interface IDisposable with
        member _.Dispose() = client.Dispose()

type MigrationStepProviderOptions =
    { GraphQLUri: Uri
      Headers: Map<string, string> }

[<RequireQualifiedAccess>]
module MigrationIssueTypeStepRuntime =
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let targetSha256 (issueNodeId: string) (typeNodeId: string option) =
        let typePart = typeNodeId |> Option.defaultValue "none"
        sha $"issue:{Encoding.UTF8.GetByteCount issueNodeId}:{issueNodeId}\ntype:{Encoding.UTF8.GetByteCount typePart}:{typePart}"

    let private query =
        "query($issueId:ID!) { node(id:$issueId) { __typename ... on Issue { id updatedAt issueType { id } } } }"

    let private mutation =
        "mutation($issueId:ID!,$typeId:ID!,$clientMutationId:String!) { updateIssueIssueType(input:{issueId:$issueId,issueTypeId:$typeId,clientMutationId:$clientMutationId}) { clientMutationId issue { __typename id updatedAt issueType { id } } } }"

    let private tryProperty (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as value ->
            let property: JsonNode = value[name]
            if isNull property then None else Some property
        | _ -> None

    let private tryString (name: string) (node: JsonNode) =
        tryProperty name node
        |> Option.bind (fun (value: JsonNode) ->
            try Some(value.GetValue<string>())
            with _ -> None)

    let private parseRoot (body: string) =
        try
            let root: JsonNode = JsonNode.Parse body
            let hasErrors =
                tryProperty "errors" root
                |> Option.exists (fun (node: JsonNode) ->
                    match node with
                    | :? JsonArray as errors -> errors.Count > 0
                    | _ -> true)
            if hasErrors then Error "provider-graphql-errors" else Ok root
        with
        | :? JsonException -> Error "provider-malformed-response"

    let private responseBody outcome =
        match outcome with
        | Response response when response.StatusCode = 200 -> parseRoot response.Body
        | Response response -> Error $"provider-http-{response.StatusCode}"
        | NetworkFailure -> Error "provider-network-failure"
        | TimedOut -> Error "provider-timeout"

    let private parseIssue node =
        match tryString "__typename" node, tryString "id" node, tryString "updatedAt" node with
        | Some "Issue", Some issueId, Some revision ->
            let typeId = tryProperty "issueType" node |> Option.bind (tryString "id")
            Ok(issueId, revision, typeId)
        | _ -> Error "provider-malformed-issue"

    let private readIssue issueNodeId options (transport: IMigrationStepProviderTransport) =
        let request =
            GraphQL
                { Uri=options.GraphQLUri
                  Document=query
                  Variables=Map.ofList [ "issueId", issueNodeId ]
                  Headers=options.Headers
                  ApiVersion=ApiVersion.required
                  Idempotency=ReplaySafe }
        match transport.Send request |> responseBody with
        | Error reason -> Error reason
        | Ok root ->
            match tryProperty "data" root |> Option.bind (tryProperty "node") with
            | Some issue -> parseIssue issue
            | None -> Error "provider-missing-issue"

    let private dispatch issueNodeId typeNodeId operationId options (transport: IMigrationStepProviderTransport) =
        let request =
            GraphQL
                { Uri=options.GraphQLUri
                  Document=mutation
                  Variables=
                    Map.ofList
                        [ "issueId", issueNodeId
                          "typeId", typeNodeId
                          "clientMutationId", operationId ]
                  Headers=options.Headers
                  ApiVersion=ApiVersion.required
                  Idempotency=NeverReplay }
        match transport.Send request with
        | NetworkFailure | TimedOut -> MigrationDispatchOutcome.Unknown
        | outcome ->
            match responseBody outcome with
            | Error _ -> MigrationDispatchOutcome.Unknown
            | Ok root ->
                match tryProperty "data" root |> Option.bind (tryProperty "updateIssueIssueType") with
                | None -> MigrationDispatchOutcome.Unknown
                | Some update ->
                    match tryString "clientMutationId" update, tryProperty "issue" update with
                    | Some clientId, Some issue when clientId = operationId ->
                        match parseIssue issue with
                        | Ok(observedIssue, _, Some observedType)
                            when observedIssue = issueNodeId && observedType = typeNodeId ->
                            MigrationDispatchOutcome.Applied
                        | _ -> MigrationDispatchOutcome.Unknown
                    | _ -> MigrationDispatchOutcome.Unknown

    let create step options authority transport =
        match MigrationStepExecution.sealStep step, step.Effect with
        | Ok sealedStep, MigrationEffect.SetIssueType(_, issueNodeId, typeNodeId)
            when sealedStep.Seal = step.Seal
                 && step.DesiredTargetSha256 = targetSha256 issueNodeId (Some typeNodeId)
                 && not (isNull options.GraphQLUri)
                 && options.GraphQLUri.IsAbsoluteUri
                 && options.GraphQLUri.Scheme = Uri.UriSchemeHttps ->
            { new IMigrationStepRuntime with
                member _.ObserveEpoch() = authority.ObserveEpoch()
                member _.ObserveAuthorityFence() = authority.ObserveAuthorityFence()
                member _.ObserveJournal operationId = authority.ObserveJournal operationId
                member _.PersistIntent(generation, head, operationId, seal) =
                    authority.PersistIntent generation head operationId seal
                member _.MarkInFlight(generation, head, operationId) =
                    authority.MarkInFlight generation head operationId
                member _.PersistSettlement(generation, head, operationId, result) =
                    authority.PersistSettlement generation head operationId result
                member _.ObserveTarget effect =
                    if effect <> step.Effect then Error "unsupported-or-cross-step-effect"
                    else
                        readIssue issueNodeId options transport
                        |> Result.map (fun (observedIssue, revision, observedType) ->
                            { Identity=$"repository:{match step.Effect with MigrationEffect.SetIssueType(repository, _, _) -> repository | _ -> 0L}/issue:{observedIssue}/type"
                              Revision=revision
                              Sha256=targetSha256 observedIssue observedType
                              Complete=true
                              Authorized=true })
                member _.ObserveEffect(operationId, effect) =
                    if operationId <> step.OperationId || effect <> step.Effect then
                        Error "unsupported-or-cross-step-effect"
                    else
                        readIssue issueNodeId options transport
                        |> Result.map (fun (observedIssue, _, observedType) ->
                            if observedIssue <> issueNodeId then MigrationEffectObservation.Unknown
                            elif observedType = Some typeNodeId then
                                MigrationEffectObservation.Applied step.DesiredTargetSha256
                            elif targetSha256 issueNodeId observedType = step.ExpectedTargetSha256 then
                                MigrationEffectObservation.ProvenAbsent
                            else MigrationEffectObservation.Partial "provider-state-neither-expected-nor-desired")
                member _.Dispatch(candidate, generation, commit) =
                    if candidate <> step then MigrationDispatchOutcome.Refused "cross-step-dispatch"
                    else
                        match authority.ObserveJournal step.OperationId with
                        | Ok(Some observed)
                            when observed.StepSeal = step.Seal
                                 && observed.Stage = MigrationJournalStage.InFlight
                                 && observed.Generation = generation
                                 && observed.Commit = commit ->
                            dispatch issueNodeId typeNodeId step.OperationId options transport
                        | _ -> MigrationDispatchOutcome.Refused "missing-fresh-in-flight-grant" }
            |> Ok
        | Ok _, MigrationEffect.SetIssueType _ -> Error "invalid-issue-type-binding"
        | Ok _, _ -> Error "unsupported-migration-effect"
        | _ -> Error "invalid-migration-step"

[<RequireQualifiedAccess>]
module MigrationBlockingEdgeStepRuntime =
    let private sha (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let targetSha256 (repositoryId: int64) blockerNodeId blockedNodeId blockingNodeIds blockedByNodeIds =
        ([ "repository"; string repositoryId; "blocker"; blockerNodeId; "blocked"; blockedNodeId
           "blocking" ] @ List.sort blockingNodeIds @ [ "blocked-by" ] @ List.sort blockedByNodeIds)
        |> List.map (fun value -> $"{Encoding.UTF8.GetByteCount value}:{value}")
        |> String.concat ""
        |> sha

    let private connectionQuery connection =
        $"query($id:ID!,$after:String) {{ node(id:$id) {{ __typename ... on Issue {{ id updatedAt repository {{ databaseId }} {connection}(first:100,after:$after) {{ totalCount nodes {{ __typename id repository {{ databaseId }} }} pageInfo {{ hasNextPage endCursor }} }} }} }} }}"

    let private mutation =
        "mutation($blockedId:ID!,$blockerId:ID!,$clientMutationId:String!) { addBlockedBy(input:{issueId:$blockedId,blockingIssueId:$blockerId,clientMutationId:$clientMutationId}) { clientMutationId issue { __typename id repository { databaseId } } blockingIssue { __typename id repository { databaseId } } } }"

    let private tryProperty (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as value ->
            let property: JsonNode = value[name]
            if isNull property then None else Some property
        | _ -> None

    let private tryString name node =
        tryProperty name node
        |> Option.bind (fun (value: JsonNode) -> try Some(value.GetValue<string>()) with _ -> None)

    let private tryInt64 name node =
        tryProperty name node
        |> Option.bind (fun (value: JsonNode) -> try Some(value.GetValue<int64>()) with _ -> None)

    let private tryInt name node =
        tryProperty name node
        |> Option.bind (fun (value: JsonNode) -> try Some(value.GetValue<int>()) with _ -> None)

    let private tryBool name node =
        tryProperty name node
        |> Option.bind (fun (value: JsonNode) -> try Some(value.GetValue<bool>()) with _ -> None)

    let private parseRoot (body: string) =
        try
            let root: JsonNode = JsonNode.Parse body
            let hasErrors =
                tryProperty "errors" root
                |> Option.exists (fun node -> match node with :? JsonArray as errors -> errors.Count > 0 | _ -> true)
            if hasErrors then Error "provider-graphql-errors" else Ok root
        with
        | :? JsonException -> Error "provider-malformed-response"

    let private body = function
        | Response response when response.StatusCode = 200 -> parseRoot response.Body
        | Response response -> Error $"provider-http-{response.StatusCode}"
        | NetworkFailure -> Error "provider-network-failure"
        | TimedOut -> Error "provider-timeout"

    let private readConnection repositoryId issueNodeId connection options
                               (transport: IMigrationStepProviderTransport) =
        let rec loop cursor seen pageCount expectedRevision expectedTotal accumulated =
            if pageCount >= 1000 || (cursor |> Option.exists (fun value -> Set.contains value seen)) then
                Error "provider-pagination-refused"
            else
                let variables =
                    [ "id", issueNodeId
                      match cursor with Some value -> "after", value | None -> () ]
                    |> Map.ofList
                let request =
                    GraphQL
                        { Uri=options.GraphQLUri; Document=connectionQuery connection
                          Variables=variables; Headers=options.Headers
                          ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
                match transport.Send request |> body with
                | Error reason -> Error reason
                | Ok root ->
                    match tryProperty "data" root |> Option.bind (tryProperty "node") with
                    | None -> Error "provider-missing-issue"
                    | Some issue ->
                        let observedRepository =
                            tryProperty "repository" issue |> Option.bind (tryInt64 "databaseId")
                        match tryString "__typename" issue, tryString "id" issue,
                              tryString "updatedAt" issue, observedRepository, tryProperty connection issue with
                        | Some "Issue", Some observedId, Some revision, Some observedRepositoryId, Some page
                            when observedId = issueNodeId && observedRepositoryId = repositoryId ->
                            match tryInt "totalCount" page, tryProperty "nodes" page,
                                  tryProperty "pageInfo" page with
                            | Some total, Some (:? JsonArray as nodes), Some pageInfo when total >= 0 ->
                                let parsed =
                                    nodes
                                    |> Seq.map (fun node ->
                                        let endpointRepository =
                                            tryProperty "repository" node |> Option.bind (tryInt64 "databaseId")
                                        match tryString "__typename" node, tryString "id" node, endpointRepository with
                                        | Some "Issue", Some id, Some endpointRepositoryId
                                            when endpointRepositoryId = repositoryId -> Ok id
                                        | _ -> Error "provider-cross-scope-relation")
                                    |> Seq.toList
                                match parsed |> List.tryPick (function Error reason -> Some reason | Ok _ -> None) with
                                | Some reason -> Error reason
                                | None ->
                                    let values = parsed |> List.choose (function Ok value -> Some value | _ -> None)
                                    let all = accumulated @ values
                                    let next =
                                        match tryBool "hasNextPage" pageInfo, tryProperty "endCursor" pageInfo with
                                        | Some true, Some value -> try Some(value.GetValue<string>()) with _ -> None
                                        | Some false, _ -> None
                                        | _ -> None
                                    let validPage =
                                        (expectedRevision |> Option.forall ((=) revision))
                                        && (expectedTotal |> Option.forall ((=) total))
                                        && all.Length <= total
                                        && (if tryBool "hasNextPage" pageInfo = Some true then next.IsSome else all.Length = total)
                                    if not validPage then Error "provider-relation-population-drift"
                                    elif next.IsSome then
                                        loop next (cursor |> Option.fold (fun state value -> Set.add value state) seen)
                                             (pageCount + 1) (Some revision) (Some total) all
                                    elif all.Length <> total || (all |> Set.ofList |> Set.count) <> all.Length then
                                        Error "provider-relation-population-drift"
                                    else Ok(revision, List.sort all)
                            | _ -> Error "provider-malformed-connection"
                        | _ -> Error "provider-cross-scope-issue"
        loop None Set.empty 0 None None []

    let private observeOnce repositoryId blocker blocked options transport =
        match readConnection repositoryId blocker "blocking" options transport,
              readConnection repositoryId blocked "blockedBy" options transport with
        | Ok(blockerRevision, blocking), Ok(blockedRevision, blockedBy) ->
            let digest = targetSha256 repositoryId blocker blocked blocking blockedBy
            Ok(blockerRevision, blockedRevision, blocking, blockedBy, digest)
        | Error reason, _ | _, Error reason -> Error reason

    let private observe repositoryId blocker blocked options transport =
        match observeOnce repositoryId blocker blocked options transport,
              observeOnce repositoryId blocker blocked options transport with
        | Ok first, Ok second when first = second -> Ok first
        | Ok _, Ok _ -> Error "provider-relation-population-drift"
        | Error reason, _ | _, Error reason -> Error reason

    let private parseEndpoint repositoryId expectedId node =
        let observedRepository = tryProperty "repository" node |> Option.bind (tryInt64 "databaseId")
        match tryString "__typename" node, tryString "id" node, observedRepository with
        | Some "Issue", Some id, Some observedRepositoryId
            when id = expectedId && observedRepositoryId = repositoryId -> true
        | _ -> false

    let private dispatch repositoryId blocker blocked operationId desiredSha options
                         (transport: IMigrationStepProviderTransport) =
        let reconcileAmbiguous () =
            match observe repositoryId blocker blocked options transport with
            | Ok(_, _, blocking, blockedBy, digest)
                when List.contains blocked blocking
                     && List.contains blocker blockedBy
                     && digest = desiredSha -> MigrationDispatchOutcome.Applied
            | _ -> MigrationDispatchOutcome.Unknown
        let request =
            GraphQL
                { Uri=options.GraphQLUri; Document=mutation
                  Variables=Map.ofList [ "blockedId", blocked; "blockerId", blocker; "clientMutationId", operationId ]
                  Headers=options.Headers; ApiVersion=ApiVersion.required; Idempotency=NeverReplay }
        match transport.Send request with
        | NetworkFailure | TimedOut -> reconcileAmbiguous ()
        | outcome ->
            match body outcome with
            | Error _ -> reconcileAmbiguous ()
            | Ok root ->
                match tryProperty "data" root |> Option.bind (tryProperty "addBlockedBy") with
                | Some result when tryString "clientMutationId" result = Some operationId ->
                    match tryProperty "blockingIssue" result, tryProperty "issue" result with
                    | Some observedBlocker, Some observedBlocked
                        when parseEndpoint repositoryId blocker observedBlocker
                             && parseEndpoint repositoryId blocked observedBlocked ->
                        MigrationDispatchOutcome.Applied
                    | _ -> reconcileAmbiguous ()
                | _ -> reconcileAmbiguous ()

    let create step options authority transport =
        match MigrationStepExecution.sealStep step, step.Effect with
        | Ok sealedStep, MigrationEffect.AddBlockingEdge(repositoryId, blocker, blocked)
            when sealedStep.Seal = step.Seal
                 && step.ExpectedTargetRevision = step.ExpectedTargetSha256
                 && not (isNull options.GraphQLUri)
                 && options.GraphQLUri.IsAbsoluteUri
                 && options.GraphQLUri.Scheme = Uri.UriSchemeHttps ->
            let observeTarget () =
                observe repositoryId blocker blocked options transport
                |> Result.map (fun (_, _, blocking, blockedBy, digest) ->
                    let sourceHasEdge = List.contains blocked blocking
                    let targetHasEdge = List.contains blocker blockedBy
                    let absent = not sourceHasEdge && not targetHasEdge
                    let desiredFromExpected =
                        targetSha256 repositoryId blocker blocked (blocked :: blocking) (blocker :: blockedBy)
                    { Identity=$"repository:{repositoryId}/blocks:{blocker}:{blocked}"
                      Revision=digest; Sha256=digest; Complete=true
                      Authorized=sourceHasEdge = targetHasEdge
                                 && (not absent || desiredFromExpected = step.DesiredTargetSha256) })
            { new IMigrationStepRuntime with
                member _.ObserveEpoch() = authority.ObserveEpoch()
                member _.ObserveAuthorityFence() = authority.ObserveAuthorityFence()
                member _.ObserveJournal operationId = authority.ObserveJournal operationId
                member _.PersistIntent(generation, head, operationId, seal) =
                    authority.PersistIntent generation head operationId seal
                member _.MarkInFlight(generation, head, operationId) =
                    authority.MarkInFlight generation head operationId
                member _.PersistSettlement(generation, head, operationId, result) =
                    authority.PersistSettlement generation head operationId result
                member _.ObserveTarget effect =
                    if effect <> step.Effect then Error "unsupported-or-cross-step-effect" else observeTarget ()
                member _.ObserveEffect(operationId, effect) =
                    if operationId <> step.OperationId || effect <> step.Effect then
                        Error "unsupported-or-cross-step-effect"
                    else
                        observe repositoryId blocker blocked options transport
                        |> Result.map (fun (_, _, blocking, blockedBy, digest) ->
                            match List.contains blocked blocking, List.contains blocker blockedBy with
                            | true, true -> MigrationEffectObservation.Applied digest
                            | false, false -> MigrationEffectObservation.ProvenAbsent
                            | _ -> MigrationEffectObservation.Partial "provider-reciprocal-edge-partial")
                member _.Dispatch(candidate, generation, commit) =
                    if candidate <> step then MigrationDispatchOutcome.Refused "cross-step-dispatch"
                    else
                        match authority.ObserveJournal step.OperationId with
                        | Ok(Some observed)
                            when observed.StepSeal = step.Seal
                                 && observed.Stage = MigrationJournalStage.InFlight
                                 && observed.Generation = generation
                                 && observed.Commit = commit ->
                            dispatch repositoryId blocker blocked step.OperationId
                                     step.DesiredTargetSha256 options transport
                        | _ -> MigrationDispatchOutcome.Refused "missing-fresh-in-flight-grant" }
            |> Ok
        | Ok _, MigrationEffect.AddBlockingEdge _ -> Error "invalid-blocking-edge-binding"
        | Ok _, _ -> Error "unsupported-migration-effect"
        | _ -> Error "invalid-migration-step"
