namespace FS.GG.FourD.Typed

open System

type RunExpectedIdentity = {
    RootJoinIdentity: string
    Repository: string
    RepositoryId: int64
    RunId: int64
    WorkflowPath: string
    Event: string
    HeadBranch: string
    HeadSha: string
    OriginalActorId: int64
    TriggeringActorId: int64
    ProducerSha256: string
    PacketManifestSha256: string
}

type RunSnapshot = {
    Repository: string
    RepositoryId: int64
    RunId: int64
    Attempt: int
    WorkflowPath: string
    Event: string
    HeadBranch: string
    HeadSha: string
    OriginalActorId: int64
    TriggeringActorId: int64
    Status: string
    Conclusion: string option
}

type RunJob = { Id: int64; RunId: int64; Attempt: int; HeadSha: string; Name: string; Status: string; Conclusion: string option }
type RunJobs = { TotalCount: int; PageCount: int; HasNextPage: bool; Jobs: RunJob list }
type ResourceFacts = { SecretCount: int; ReleaseCount: int; AssetStatus: string; ReleaseStatus: string
                       TagStatus: string; PrivateKeyAbsent: bool; PublicKeyAbsent: bool; FailureCount: int }
type RerunState = NotIssued | IntentRecorded | Acknowledged | Uncertain
type CancelState = NoCancel | CancelIntent | CancelAccepted | CancelRefused
type SettlementStage = NoSettlement | CurrentTerminal | TargetTerminal | JobsTerminal | Settled
type RunAction =
    | IssueRerun
    | ObserveCurrent
    | ObserveTargetAndJobs
    | Wait of seconds: int
    | CancelOnce
    | RetireResources
    | ContinueResultReadback
    | Complete
    | FinishRefused
    | Refuse of reason: string

type RunProtocolState = {
    Expected: RunExpectedIdentity
    Rerun: RerunState
    SeenTarget2: bool
    LatestSequence: int
    ElapsedSeconds: int
    NextObservationAt: int
    TargetConclusion: string option
    Cancel: CancelState
    Settlement: SettlementStage
    ResourcesRetired: bool
    ResultAccepted: bool
    StickyCancelled: bool
    CleanupStarted: bool
    CleanupFreshCurrent: bool
    Refusal: string option
}

type RunEvent =
    | RecordRerunIntent of elapsed: int
    | RecordRerunResult of elapsed: int * acknowledged: bool
    | ObserveCurrentRun of elapsed: int * sequence: int * httpStatus: int * snapshot: RunSnapshot option
    | ObserveTargetRun of elapsed: int * sequence: int * httpStatus: int * snapshot: RunSnapshot option
    | ObserveTargetJobs of elapsed: int * sequence: int * httpStatus: int * jobs: RunJobs option
    | EnterCleanup of elapsed: int * signalCancelled: bool
    | RecordSignalCancellation of elapsed: int
    | RecordCancelResult of elapsed: int * accepted: bool
    | RecordResourcesRetired of elapsed: int * facts: ResourceFacts
    | RecordResultReadback of elapsed: int * sealedEvidence: bool * acknowledged: bool
    | ExhaustDeadline of elapsed: int

type RunDecision = { Lifecycle: State; Protocol: RunProtocolState; Action: RunAction }

module RunObservation =
    [<Literal>]
    let TotalBudgetSeconds = 45 * 60
    [<Literal>]
    let EffectCutoffSeconds = 41 * 60
    [<Literal>]
    let MinimumPollSeconds = 20

    let private validConclusion = function
        | "success" | "failure" | "cancelled" | "timed_out" | "action_required"
        | "neutral" | "skipped" | "stale" -> true
        | _ -> false

    let private resourcesClosed facts =
        let gone value = value = "404" || value = "not-created"
        facts.SecretCount = 0 && facts.ReleaseCount = 0 && facts.FailureCount = 0
        && gone facts.AssetStatus && gone facts.ReleaseStatus && gone facts.TagStatus
        && facts.PrivateKeyAbsent && facts.PublicKeyAbsent

    let private nonterminalStatus = function
        | "queued" | "in_progress" | "pending" | "requested" | "waiting" -> true
        | _ -> false

    let private validSnapshot (snapshot: RunSnapshot) =
        let statusShape =
            if snapshot.Status = "completed" then snapshot.Conclusion |> Option.exists validConclusion
            elif nonterminalStatus snapshot.Status then snapshot.Conclusion.IsNone
            else false
        statusShape && snapshot.Attempt > 0

    let private exact (expected: RunExpectedIdentity) (snapshot: RunSnapshot) =
        snapshot.Repository = expected.Repository
        && snapshot.RepositoryId = expected.RepositoryId
        && snapshot.RunId = expected.RunId
        && snapshot.WorkflowPath = expected.WorkflowPath
        && snapshot.Event = expected.Event
        && snapshot.HeadBranch = expected.HeadBranch
        && snapshot.HeadSha = expected.HeadSha
        && snapshot.OriginalActorId = expected.OriginalActorId
        && snapshot.TriggeringActorId = expected.TriggeringActorId

    let private validExpected (expected: RunExpectedIdentity) (lifecycle: State) =
        Policy.validToken expected.RootJoinIdentity
        && lifecycle.CurrentIdentity = Some expected.RootJoinIdentity
        && lifecycle.AdmittedIdentity = Some expected.RootJoinIdentity
        && expected.Repository = "FS-GG/FS.GG.Coordination"
        && expected.RepositoryId = 1346720714L
        && expected.RunId > 0L
        && expected.WorkflowPath = ".github/workflows/fourd-public-provider-qualification.yml"
        && expected.Event = "workflow_dispatch"
        && expected.HeadBranch = "qualification/fourd-native-20261001"
        && expected.HeadSha.Length = 40
        && expected.HeadSha |> Seq.forall (fun c -> Char.IsDigit c || c >= 'a' && c <= 'f')
        && expected.OriginalActorId > 0L && expected.TriggeringActorId > 0L
        && expected.ProducerSha256.Length = 64 && expected.PacketManifestSha256.Length = 64
        && (expected.ProducerSha256 |> Seq.forall (fun c -> Char.IsDigit c || c >= 'a' && c <= 'f'))
        && (expected.PacketManifestSha256 |> Seq.forall (fun c -> Char.IsDigit c || c >= 'a' && c <= 'f'))

    let initial expected lifecycle =
        if not (validExpected expected lifecycle) then Error "run-identity-refused"
        else
          let consumed = TotalBudgetSeconds - lifecycle.BudgetRemaining
          if consumed < 0 || consumed > TotalBudgetSeconds then Error "run-clock-refused" else Ok {
            Expected = expected; Rerun = NotIssued; SeenTarget2 = false; LatestSequence = 0
            ElapsedSeconds = consumed; NextObservationAt = consumed; TargetConclusion = None
            Cancel = NoCancel; Settlement = NoSettlement; ResourcesRetired = false
            ResultAccepted = false; StickyCancelled = false; CleanupStarted = false
            CleanupFreshCurrent = false; Refusal = None
          }

    let validateState lifecycle state =
        validExpected state.Expected lifecycle
        && state.LatestSequence >= 0
        && state.ElapsedSeconds >= 0 && state.ElapsedSeconds <= TotalBudgetSeconds
        && state.NextObservationAt >= state.ElapsedSeconds
        && (state.SeenTarget2 || state.TargetConclusion.IsNone)
        && (state.Settlement = NoSettlement || state.SeenTarget2)
        && (state.Settlement <> Settled || state.TargetConclusion.IsSome)
        && (state.ResultAccepted |> not || state.Settlement = Settled && state.ResourcesRetired
                                           && state.TargetConclusion = Some "success"
                                           && state.Cancel = NoCancel && not state.StickyCancelled && state.Refusal.IsNone)
        && (state.Rerun <> NotIssued || (state.LatestSequence = 0 && not state.SeenTarget2
                                         && state.Settlement = NoSettlement && state.TargetConclusion.IsNone
                                         && state.Cancel = NoCancel && not state.ResourcesRetired && not state.ResultAccepted))
        && (state.SeenTarget2 = (state.LatestSequence > 0 && state.Rerun = Acknowledged)
            || (not state.SeenTarget2 && state.LatestSequence >= 0))
        && (not state.CleanupFreshCurrent || state.CleanupStarted)
        && (not state.ResourcesRetired || state.CleanupStarted)
        && (state.Refusal.IsNone || not state.ResultAccepted)

    let private elapsed = function
        | RecordRerunIntent value | RecordSignalCancellation value | ExhaustDeadline value -> value
        | EnterCleanup(value,_) -> value
        | RecordRerunResult(value,_) | RecordCancelResult(value,_)
        | RecordResourcesRetired(value,_) | RecordResultReadback(value,_,_) -> value
        | ObserveCurrentRun(value,_,_,_) | ObserveTargetRun(value,_,_,_)
        | ObserveTargetJobs(value,_,_,_) -> value

    let private sequence = function
        | ObserveCurrentRun(_,value,_,_) | ObserveTargetRun(_,value,_,_)
        | ObserveTargetJobs(_,value,_,_) -> Some value
        | _ -> None

    let private refuse reason lifecycle state =
        let first = state.Refusal |> Option.defaultValue reason
        Ok { Lifecycle = lifecycle; Protocol = { state with Refusal = Some first }; Action = Refuse first }

    let private closeIfProven lifecycle state action =
        if state.Settlement = Settled && state.ResourcesRetired then
            match Policy.closeComposedResource "remote-operation" lifecycle with
            | Ok closed -> Ok { Lifecycle = closed; Protocol = state; Action = action }
            | Error reason -> refuse reason lifecycle state
        else Ok { Lifecycle = lifecycle; Protocol = state; Action = action }

    let private classifyCurrent (lifecycle: State) (state: RunProtocolState) (snapshot: RunSnapshot) =
        if not (validSnapshot snapshot) || not (exact state.Expected snapshot) then
            refuse "run-observation-identity-refused" lifecycle state
        elif snapshot.Attempt >= 3 then refuse "run-superseded-refused" lifecycle state
        elif snapshot.Attempt = 1 then
            if state.SeenTarget2 || state.Settlement <> NoSettlement then
                refuse "run-stale-regression-refused" lifecycle state
            elif state.Rerun <> Acknowledged then refuse "run-rerun-uncertain-refused" lifecycle state
            elif snapshot.Status = "completed" && snapshot.Conclusion <> Some "success" then
                refuse "reservation-observation-refused" lifecycle state
            else
                Ok { Lifecycle = lifecycle
                     Protocol = { state with NextObservationAt = state.ElapsedSeconds + MinimumPollSeconds }
                     Action = Wait MinimumPollSeconds }
        elif snapshot.Attempt <> 2 then refuse "run-attempt-refused" lifecycle state
        else
            let seen = { state with SeenTarget2 = true }
            if snapshot.Status <> "completed" then
                Ok { Lifecycle = lifecycle
                     Protocol = { seen with NextObservationAt = seen.ElapsedSeconds + MinimumPollSeconds }
                     Action = Wait MinimumPollSeconds }
            else
                let conclusion = snapshot.Conclusion
                if seen.Settlement = JobsTerminal then
                    if conclusion <> seen.TargetConclusion then refuse "run-terminal-contradiction-refused" lifecycle seen
                    else
                        let settled = { seen with Settlement = Settled; TargetConclusion = conclusion }
                        let action = if settled.ResourcesRetired then
                                         if conclusion = Some "success" && settled.Cancel = NoCancel && settled.Refusal.IsNone && not settled.StickyCancelled then ContinueResultReadback else FinishRefused
                                     else RetireResources
                        closeIfProven lifecycle settled action
                elif seen.TargetConclusion.IsSome && seen.TargetConclusion <> conclusion then
                    refuse "run-terminal-contradiction-refused" lifecycle seen
                else
                    Ok { Lifecycle = lifecycle
                         Protocol = { seen with Settlement = CurrentTerminal; TargetConclusion = conclusion }
                         Action = ObserveTargetAndJobs }

    let reduce lifecycle state event =
        if not (Policy.validateState lifecycle) || not (validateState lifecycle state) then
            Error "run-state-refused"
        else
            let now = elapsed event
            let sequenceValue = sequence event
            if now < state.ElapsedSeconds || now > TotalBudgetSeconds then Error "run-clock-refused"
            elif sequenceValue |> Option.exists (fun value -> value <= state.LatestSequence) then Error "run-sequence-refused"
            elif sequenceValue.IsSome && now < state.NextObservationAt then Error "run-poll-interval-refused"
            else
                let cost = now - state.ElapsedSeconds
                match Policy.charge cost lifecycle with
                | Error reason -> Error reason
                | Ok charged ->
                    let advanced = { state with ElapsedSeconds = now
                                                LatestSequence = Option.defaultValue state.LatestSequence sequenceValue
                                                NextObservationAt = now }
                    match event with
                    | RecordRerunIntent _ when state.Rerun = NotIssued && now < EffectCutoffSeconds ->
                        Ok { Lifecycle = charged; Protocol = { advanced with Rerun = IntentRecorded }; Action = IssueRerun }
                    | RecordRerunResult(_,true) when state.Rerun = IntentRecorded ->
                        Ok { Lifecycle = charged; Protocol = { advanced with Rerun = Acknowledged }; Action = ObserveCurrent }
                    | RecordRerunResult(_,false) when state.Rerun = IntentRecorded ->
                        Ok { Lifecycle = charged; Protocol = { advanced with Rerun = Uncertain; Refusal = Some "rerun-response-unknown" }; Action = RetireResources }
                    | ObserveCurrentRun(_,_,200,Some snapshot) when state.CleanupStarted ->
                        let fresh = { advanced with CleanupFreshCurrent = true }
                        if not (validSnapshot snapshot) || not (exact state.Expected snapshot) || snapshot.Attempt >= 3 then
                            refuse "run-observation-identity-refused" charged fresh
                        elif snapshot.Attempt = 2 && snapshot.Status = "completed" then classifyCurrent charged fresh snapshot
                        elif snapshot.Attempt = 2 && state.Rerun = Acknowledged && state.Cancel = NoCancel && state.Refusal.IsNone
                             && now < EffectCutoffSeconds then
                            Ok { Lifecycle = charged; Protocol = { fresh with Cancel = CancelIntent }; Action = CancelOnce }
                        elif state.ResourcesRetired then
                            Ok { Lifecycle = charged
                                 Protocol = { fresh with NextObservationAt = now + MinimumPollSeconds }
                                 Action = Wait MinimumPollSeconds }
                        else Ok { Lifecycle = charged; Protocol = fresh; Action = RetireResources }
                    | ObserveCurrentRun(_,_,200,Some snapshot) -> classifyCurrent charged advanced snapshot
                    | ObserveCurrentRun _ -> refuse "run-current-transport-refused" charged advanced
                    | ObserveTargetRun(_,_,200,Some snapshot) when state.Settlement = CurrentTerminal ->
                        if not (validSnapshot snapshot) || not (exact state.Expected snapshot) || snapshot.Attempt <> 2
                           || snapshot.Status <> "completed" || snapshot.Conclusion <> state.TargetConclusion then
                            refuse "run-target-observation-refused" charged advanced
                        else Ok { Lifecycle = charged; Protocol = { advanced with Settlement = TargetTerminal }; Action = ObserveTargetAndJobs }
                    | ObserveTargetRun _ -> refuse "run-target-transport-refused" charged advanced
                    | ObserveTargetJobs(_,_,200,Some jobs) when state.Settlement = TargetTerminal ->
                        let names = jobs.Jobs |> List.map (_.Name) |> Set.ofList
                        let ids = jobs.Jobs |> List.map (_.Id) |> Set.ofList
                        let terminal = jobs.Jobs |> List.forall (fun job -> job.Status = "completed" && job.Conclusion |> Option.exists validConclusion)
                        let identity = jobs.Jobs |> List.forall (fun job -> job.RunId = state.Expected.RunId
                                                                           && job.Attempt = 2 && job.HeadSha = state.Expected.HeadSha)
                        if jobs.TotalCount <> 2 || jobs.PageCount < 1 || jobs.PageCount > 2 || jobs.HasNextPage
                           || jobs.Jobs.Length <> 2 || ids.Count <> 2 || ids |> Set.exists (fun id -> id <= 0L)
                           || names <> Set ["capacity";"qualification"] || not terminal || not identity then
                            refuse "run-jobs-census-refused" charged advanced
                        else Ok { Lifecycle = charged; Protocol = { advanced with Settlement = JobsTerminal }; Action = ObserveCurrent }
                    | ObserveTargetJobs _ -> refuse "run-jobs-transport-refused" charged advanced
                    | EnterCleanup(_,signalCancelled) ->
                        let sticky = state.StickyCancelled || signalCancelled
                        let lifecycleResult =
                            match Policy.beginComposedCleanup sticky charged with
                            | Error reason -> Error reason
                            | Ok cleaning when sticky && not cleaning.Cancelled -> Policy.markComposedCancellation cleaning
                            | Ok cleaning -> Ok cleaning
                        match lifecycleResult with
                        | Error reason -> refuse reason charged advanced
                        | Ok cleaning ->
                            let cleanup = { advanced with StickyCancelled = sticky; CleanupStarted = true }
                            if state.Rerun = Acknowledged && state.Settlement = NoSettlement && not state.ResourcesRetired then
                                Ok { Lifecycle = cleaning; Protocol = { cleanup with NextObservationAt = now }; Action = ObserveCurrent }
                            else Ok { Lifecycle = cleaning; Protocol = cleanup; Action = RetireResources }
                    | RecordSignalCancellation _ ->
                        match Policy.markComposedCancellation charged with
                        | Error reason -> refuse reason charged advanced
                        | Ok cancelled ->
                            let revoked = { advanced with StickyCancelled = true; ResultAccepted = false }
                            if state.ResourcesRetired && state.Settlement <> Settled then
                                Ok { Lifecycle = cancelled
                                     Protocol = { revoked with NextObservationAt = now + MinimumPollSeconds }
                                     Action = Wait MinimumPollSeconds }
                            else Ok { Lifecycle = cancelled; Protocol = revoked
                                      Action = if state.ResourcesRetired then FinishRefused else RetireResources }
                    | RecordCancelResult(_,accepted) when state.Cancel = CancelIntent ->
                        Ok { Lifecycle = charged
                             Protocol = { advanced with Cancel = if accepted then CancelAccepted else CancelRefused }
                             Action = RetireResources }
                    | RecordResourcesRetired(_,facts) when resourcesClosed facts ->
                        let retired = { advanced with ResourcesRetired = true }
                        let action = if retired.Settlement = Settled then
                                         if retired.TargetConclusion = Some "success" && retired.Cancel = NoCancel && retired.Refusal.IsNone && not retired.StickyCancelled then ContinueResultReadback else FinishRefused
                                     elif now < TotalBudgetSeconds then ObserveCurrent else Refuse "run-settlement-unproven"
                        let scheduled = if action = ObserveCurrent then { retired with NextObservationAt = now + MinimumPollSeconds } else retired
                        closeIfProven charged scheduled action
                    | RecordResourcesRetired _ -> refuse "resource-retirement-refused" charged advanced
                    | RecordResultReadback(_,true,true) when state.Settlement = Settled && state.ResourcesRetired
                                                            && state.TargetConclusion = Some "success" && state.Cancel = NoCancel
                                                            && state.Refusal.IsNone && not state.StickyCancelled ->
                        match Policy.acknowledgeComposedSuccess charged with
                        | Ok acknowledged -> Ok { Lifecycle = acknowledged; Protocol = { advanced with ResultAccepted = true }; Action = Complete }
                        | Error reason -> refuse reason charged advanced
                    | RecordResultReadback _ -> refuse "result-readback-refused" charged advanced
                    | ExhaustDeadline _ -> refuse "run-deadline-refused" charged advanced
                    | _ -> Error "run-transition-refused"
