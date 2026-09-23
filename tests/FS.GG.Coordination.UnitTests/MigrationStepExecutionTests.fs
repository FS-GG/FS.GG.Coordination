module FS.GG.Coordination.MigrationStepExecutionTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.GitHub.MigrationStepExecution

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private revision letter = String.replicate 40 letter
let private get = function Ok value -> value | Error failures -> failwithf "unexpected refusal: %A" failures

let private step () =
    { OperationId="migration:issue-type:1"
      IdempotencyKey="migration:manifest:issue-type:1"
      ManifestSeal=sha "manifest"
      Effect=MigrationEffect.SetIssueType(42L, "ISSUE_1", "TYPE_2")
      TargetIdentity="repository:42/issue:ISSUE_1/type"
      ExpectedTargetRevision="issue-v1"
      ExpectedTargetSha256=sha "old-type"
      DesiredTargetSha256=sha "new-type"
      EpochGeneration=7L
      EpochCommit=revision "a"
      JournalGeneration=11L
      JournalHead=revision "b"
      Seal="" }
    |> sealStep
    |> get

type private ControlledRuntime(step: MigrationExecutionStep) =
    let mutable epoch =
        { Phase="SwitchedV2"; ManifestSeal=step.ManifestSeal; Generation=step.EpochGeneration
          Commit=step.EpochCommit; Complete=true; Authorized=true }
    let mutable target =
        { Identity=step.TargetIdentity; Revision=step.ExpectedTargetRevision
          Sha256=step.ExpectedTargetSha256; Complete=true; Authorized=true }
    let mutable journal: MigrationJournalAuthority option = None
    let mutable effect = MigrationEffectObservation.ProvenAbsent
    let mutable dispatches = 0
    let mutable journalWrites = 0
    let mutable dispatchResult = MigrationDispatchOutcome.Applied

    member _.Epoch with get() = epoch and set value = epoch <- value
    member _.Target with get() = target and set value = target <- value
    member _.Effect with get() = effect and set value = effect <- value
    member _.DispatchResult with get() = dispatchResult and set value = dispatchResult <- value
    member _.Dispatches = dispatches
    member _.JournalWrites = journalWrites
    member _.Journal = journal
    member _.InjectJournal value = journal <- value

    interface IMigrationStepRuntime with
        member _.ObserveEpoch() = Ok epoch
        member _.ObserveTarget _ = Ok target
        member _.ObserveJournal _ = Ok journal
        member _.PersistIntent(expected, head, operation, seal) =
            if journal.IsSome || expected <> step.JournalGeneration || head <> step.JournalHead then
                MigrationCasOutcome.Conflict
            else
                let authority =
                    { OperationId=operation; StepSeal=seal; Generation=expected + 1L; Commit=revision "c"
                      Stage=MigrationJournalStage.IntentPersisted; ResultSha256=None }
                journal <- Some authority
                journalWrites <- journalWrites + 1
                MigrationCasOutcome.Accepted authority
        member _.MarkInFlight(expected, head, operation) =
            match journal with
            | Some current when current.Generation = expected && current.Commit = head
                                && current.OperationId = operation && current.Stage = MigrationJournalStage.IntentPersisted ->
                let authority =
                    { current with Generation=expected + 1L; Commit=revision "d"
                                   Stage=MigrationJournalStage.InFlight }
                journal <- Some authority
                journalWrites <- journalWrites + 1
                MigrationCasOutcome.Accepted authority
            | _ -> MigrationCasOutcome.Conflict
        member _.ObserveEffect(_, _) = Ok effect
        member _.Dispatch(_, generation, commit) =
            match journal with
            | Some current when current.Generation = generation && current.Commit = commit
                                && current.Stage = MigrationJournalStage.InFlight ->
                dispatches <- dispatches + 1
                match dispatchResult with
                | MigrationDispatchOutcome.Applied ->
                    target <- { target with Revision="issue-v2"; Sha256=step.DesiredTargetSha256 }
                    effect <- MigrationEffectObservation.Applied step.DesiredTargetSha256
                | _ -> ()
                dispatchResult
            | _ -> MigrationDispatchOutcome.Refused "missing-fenced-grant"
        member _.PersistSettlement(expected, head, operation, result) =
            match journal with
            | Some current when current.Generation = expected && current.Commit = head
                                && current.OperationId = operation && current.Stage = MigrationJournalStage.InFlight ->
                let authority =
                    { current with Generation=expected + 1L; Commit=revision "e"
                                   Stage=MigrationJournalStage.Settled; ResultSha256=Some result }
                journal <- Some authority
                journalWrites <- journalWrites + 1
                MigrationCasOutcome.Accepted authority
            | _ -> MigrationCasOutcome.Conflict

[<Fact>]
let ``typed migration effect persists intent fences dispatch and replays without another send`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    let first = advance selected MigrationAdvanceCut.NoCut runtime |> get
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256, first)
    Assert.Equal(1, runtime.Dispatches)
    Assert.Equal(3, runtime.JournalWrites)
    Assert.Equal(MigrationAdvanceResult.AlreadySettled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, runtime.Dispatches)

[<Fact>]
let ``interruption after intent resumes once and after dispatch settles from readback`` () =
    let selected = step ()
    let beforeSend = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "after-intent-before-dispatch",
                 advance selected MigrationAdvanceCut.StopAfterIntent beforeSend |> get)
    Assert.Equal(0, beforeSend.Dispatches)
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut beforeSend |> get)
    Assert.Equal(1, beforeSend.Dispatches)

    let lostResponse = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "after-dispatch-before-response",
                 advance selected MigrationAdvanceCut.StopAfterDispatch lostResponse |> get)
    Assert.Equal(1, lostResponse.Dispatches)
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut lostResponse |> get)
    Assert.Equal(1, lostResponse.Dispatches)

[<Fact>]
let ``restored in flight effect is recovery only even when a read reports absence`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "after-in-flight-before-dispatch",
                 advance selected MigrationAdvanceCut.StopAfterInFlight runtime |> get)
    Assert.Equal(MigrationAdvanceResult.Pending "in-flight-absence-needs-exclusion",
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(0, runtime.Dispatches)

[<Fact>]
let ``unknown and partial outcomes stay pending without duplicate dispatch`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    runtime.DispatchResult <- MigrationDispatchOutcome.Unknown
    Assert.Equal(MigrationAdvanceResult.Pending "dispatch-outcome-unknown",
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, runtime.Dispatches)
    runtime.Effect <- MigrationEffectObservation.Partial "provider-partial"
    Assert.Equal(MigrationAdvanceResult.Pending "effect-partial",
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, runtime.Dispatches)
    runtime.Effect <- MigrationEffectObservation.Unknown
    Assert.Equal(MigrationAdvanceResult.Pending "effect-observation-unknown",
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, runtime.Dispatches)

[<Fact>]
let ``stale epoch changed target and altered plan refuse before journal or provider writes`` () =
    let selected = step ()
    let stale = ControlledRuntime(selected)
    stale.Epoch <- { stale.Epoch with Generation=selected.EpochGeneration + 1L }
    Assert.Equal(Error [ MigrationExecutionFailure.StaleEpoch ], advance selected MigrationAdvanceCut.NoCut stale)
    Assert.Equal(0, stale.JournalWrites)
    let changed = ControlledRuntime(selected)
    changed.Target <- { changed.Target with Revision="unexpected" }
    Assert.Equal(Error [ MigrationExecutionFailure.ChangedTarget ], advance selected MigrationAdvanceCut.NoCut changed)
    Assert.Equal(0, changed.JournalWrites)
    let altered = ControlledRuntime(selected)
    let wrongPlan = { selected with DesiredTargetSha256=sha "substituted" }
    Assert.Equal(Error [ MigrationExecutionFailure.InvalidStep ], advance wrongPlan MigrationAdvanceCut.NoCut altered)
    Assert.Equal(0, altered.JournalWrites)

[<Fact>]
let ``intent cannot settle an externally applied effect without an in flight grant`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "after-intent-before-dispatch",
                 advance selected MigrationAdvanceCut.StopAfterIntent runtime |> get)
    runtime.Target <- { runtime.Target with Revision="external-v2"; Sha256=selected.DesiredTargetSha256 }
    runtime.Effect <- MigrationEffectObservation.Applied selected.DesiredTargetSha256
    Assert.Equal(Error [ MigrationExecutionFailure.JournalConflict ],
                 advance selected MigrationAdvanceCut.NoCut runtime)
    Assert.Equal(0, runtime.Dispatches)
    Assert.Equal(1, runtime.JournalWrites)

[<Fact>]
let ``journal stages require their exact chained generation`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    runtime.InjectJournal
        (Some { OperationId=selected.OperationId; StepSeal=selected.Seal
                Generation=selected.JournalGeneration + 3L; Commit=revision "c"
                Stage=MigrationJournalStage.InFlight; ResultSha256=None })
    Assert.Equal(Error [ MigrationExecutionFailure.JournalConflict ],
                 advance selected MigrationAdvanceCut.NoCut runtime)
    Assert.Equal(0, runtime.Dispatches)
