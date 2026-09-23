namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

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
      JournalGeneration: int64
      JournalHead: string
      Seal: string }

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
type MigrationAdvanceCut = NoCut | StopAfterIntent | StopAfterInFlight | StopAfterDispatch | StopAfterEffect

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
    | ChangedTarget
    | UnauthorizedTarget
    | IncompleteTarget
    | JournalConflict
    | JournalUnavailable of reason:string
    | EffectRefused of reason:string

type IMigrationStepRuntime =
    abstract ObserveEpoch: unit -> Result<MigrationEpochObservation, string>
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
        [ step.OperationId; step.IdempotencyKey; step.ManifestSeal; step.TargetIdentity
          step.ExpectedTargetRevision; step.ExpectedTargetSha256; step.DesiredTargetSha256
          string step.EpochGeneration; step.EpochCommit; string step.JournalGeneration; step.JournalHead ] @ effect
        |> List.map (fun value -> $"{Encoding.UTF8.GetByteCount value}:{value}")
        |> String.concat ""
        |> sha

    let private shapeValid (step: MigrationExecutionStep) =
        let target, _ = effectParts step.Effect
        validText step.OperationId && validText step.IdempotencyKey
        && isSha 64 step.ManifestSeal && effectValid step.Effect
        && step.TargetIdentity = target && validText step.ExpectedTargetRevision
        && isSha 64 step.ExpectedTargetSha256 && isSha 64 step.DesiredTargetSha256
        && step.ExpectedTargetSha256 <> step.DesiredTargetSha256
        && step.EpochGeneration > 0L && isSha 40 step.EpochCommit
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
        if authority.OperationId <> step.OperationId || authority.StepSeal <> step.Seal
           || authority.Generation <= step.JournalGeneration || not (isSha 40 authority.Commit)
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
            if cut = MigrationAdvanceCut.StopAfterEffect then
                Ok(MigrationAdvanceResult.Interrupted "after-effect-before-receipt")
            else
                match inspectEpoch step runtime, inspectTarget true step runtime with
                | Error failures, _ | _, Error failures -> Error failures
                | Ok _, Ok _ ->
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
            match inspectEpoch step runtime, effectRead () with
            | Error failures, _ | _, Error failures -> Error failures
            | Ok _, Ok(MigrationEffectObservation.Applied result) when result = step.DesiredTargetSha256 ->
                settle authority
            | Ok _, Ok(MigrationEffectObservation.Applied _) ->
                Error [ MigrationExecutionFailure.EffectRefused "wrong-effect-readback" ]
            | Ok _, Ok(MigrationEffectObservation.Partial _) ->
                Ok(MigrationAdvanceResult.Pending "effect-partial")
            | Ok _, Ok MigrationEffectObservation.Unknown ->
                Ok(MigrationAdvanceResult.Pending "effect-observation-unknown")
            | Ok _, Ok MigrationEffectObservation.ProvenAbsent when authority.Stage = MigrationJournalStage.InFlight ->
                // A restored in-flight request is recovery-only. Absence without exclusion of a delayed
                // original cannot authorize another send under the canonical protocol.
                Ok(MigrationAdvanceResult.Pending "in-flight-absence-needs-exclusion")
            | Ok _, Ok MigrationEffectObservation.ProvenAbsent ->
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
                            match inspectEpoch step runtime, observeJournal step runtime, inspectTarget false step runtime with
                            | Error failures, _, _ | _, Error failures, _ | _, _, Error failures -> Error failures
                            | Ok _, Ok(Some fresh), Ok _ when fresh = grant ->
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
            match inspectEpoch step runtime, observeJournal step runtime with
            | Error failures, _ | _, Error failures -> Error failures
            | Ok _, Ok(Some authority) when authority.Stage = MigrationJournalStage.Settled ->
                match effectRead (), inspectTarget true step runtime with
                | Ok(MigrationEffectObservation.Applied result), Ok _ when result = step.DesiredTargetSha256 ->
                    Ok(MigrationAdvanceResult.AlreadySettled result)
                | Error failures, _ | _, Error failures -> Error failures
                | _ -> Error [ MigrationExecutionFailure.JournalConflict ]
            | Ok _, Ok(Some authority) -> continueFrom authority
            | Ok _, Ok None ->
                match inspectTarget false step runtime with
                | Error failures -> Error failures
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
