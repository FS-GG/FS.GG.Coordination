open System
open FsQuint
open FS.GG.FourD.Typed
open System.Text.Json.Nodes

let fail message = raise (InvalidOperationException message)
let policyOk (result: Result<'a, string>) = match result with Ok value -> value | Error reason -> fail reason
let replayOk (result: Result<'a, QuintReplayDiagnostic list>) = match result with Ok value -> value | Error reason -> fail $"%A{reason}"
let source (name: string) = { Path = "eng/fourd-public-provider/typed/Policy.fs"; Line = 1; Column = name.Length }
let text value = QuintReplayValue.Text value
let boolean value = QuintReplayValue.Boolean value
let integer value = QuintReplayValue.Integer(string value)

let identityNumber = function None -> 0 | Some "id-a" -> 1 | Some "id-b" -> 2 | Some _ -> 3
let phaseText = function
    | Idle -> "idle" | Acquired -> "acquired" | Validated -> "validated" | Admitted -> "admitted"
    | Effect -> "effect" | Cleanup -> "cleanup" | Finished -> "finished" | CleanupFailed -> "cleanup-failed"
let outcomeText = function NoneObserved -> "none" | Unknown -> "unknown" | Success -> "success" | Refused -> "refused"

let project state =
    let draft = {
        Identity = String.replicate 64 "0"
        Bindings = [
            "state", QuintReplayValue.Record [
                "acquiredIdentity", integer (identityNumber state.AcquiredIdentity)
                "admittedIdentity", integer (identityNumber state.AdmittedIdentity)
                "budget", integer state.BudgetRemaining
                "cancelled", boolean state.Cancelled
                "closed", integer state.Closed.Count
                "currentIdentity", integer (identityNumber state.CurrentIdentity)
                "effectAcknowledged", boolean state.EffectAcknowledged
                "outcome", text (outcomeText state.Outcome)
                "owned", integer state.Owned.Count
                "phase", text (phaseText state.Phase)
                "validatedIdentity", integer (identityNumber state.ValidatedIdentity)
            ]
        ]
    }
    { draft with Identity = QuintReplay.stateFingerprint draft |> replayOk }

let apply (name: string) observation (states: State list, observations: QuintReplayObservation list) =
    let next = Policy.reduce (List.last states) observation |> policyOk
    let index = observations.Length + 1
    let actual = project next
    let observed = { Index = index; Action = name; Source = source name; Actual = actual }
    states @ [next], observations @ [observed]

let start = ([Policy.initial 12], [])
let success = start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
              |> apply "admit" (Admit("id-a",1)) |> apply "beginEffect" (BeginEffect("effect",false,1))
              |> apply "observeSuccess" (ObserveSuccess 1) |> apply "beginCleanup" (BeginCleanup(false,1))
              |> apply "closePlaintext" (Close("plaintext",1)) |> apply "closeEffect" (Close("effect",1)) |> apply "finish" (Finish 1)

let runSource (name: string) = { Path = "eng/fourd-public-provider/typed/RunObservation.fs"; Line = 1; Column = name.Length }
let runExpected = {
    RootJoinIdentity="id-a"; Repository="FS-GG/FS.GG.Coordination"; RepositoryId=1346720714L
    RunId=12345L; WorkflowPath=".github/workflows/fourd-public-provider-qualification.yml"
    Event="workflow_dispatch"; HeadBranch="qualification/fourd-native-20261001"
    HeadSha=String.replicate 40 "b"; OriginalActorId=17L; TriggeringActorId=17L
    ProducerSha256=String.replicate 64 "c"; PacketManifestSha256=String.replicate 64 "d" }
let runSnapshot attempt status conclusion = {
    Repository=runExpected.Repository; RepositoryId=runExpected.RepositoryId; RunId=runExpected.RunId
    Attempt=attempt; WorkflowPath=runExpected.WorkflowPath; Event=runExpected.Event
    HeadBranch=runExpected.HeadBranch; HeadSha=runExpected.HeadSha
    OriginalActorId=runExpected.OriginalActorId; TriggeringActorId=runExpected.TriggeringActorId
    Status=status; Conclusion=conclusion }
let jobs success = { TotalCount=2; PageCount=1; HasNextPage=false; Jobs=[
    {Id=1L;RunId=12345L;Attempt=2;HeadSha=String.replicate 40 "b";Name="capacity";Status="completed";Conclusion=Some "skipped"}
    {Id=2L;RunId=12345L;Attempt=2;HeadSha=String.replicate 40 "b";Name="qualification";Status="completed";Conclusion=Some success}] }
let runLifecycle = {
    Phase=Effect; AcquiredIdentity=Some "id-a"; ValidatedIdentity=Some "id-a"
    AdmittedIdentity=Some "id-a"; CurrentIdentity=Some "id-a"; EffectAcknowledged=false
    Outcome=Unknown; Owned=Set.ofList ["transport-intent";"remote-operation"]; Closed=Set.empty
    Cancelled=false; BudgetRemaining=2700 }
let runInitial = RunObservation.initial runExpected runLifecycle |> policyOk
let runActionText = function
    | IssueRerun->"issue-rerun"|ObserveCurrent->"observe-current"|ObserveTargetAndJobs->"observe-target-and-jobs"
    | Wait _->"wait"|CancelOnce->"cancel-once"|RetireResources->"retire-resources"
    | ContinueResultReadback->"continue-result-readback"|Complete->"complete"|FinishRefused->"finish-refused"|Refuse _->"refuse"
let rerunText = function NotIssued->"not-issued"|IntentRecorded->"intent-recorded"|Acknowledged->"acknowledged"|Uncertain->"uncertain"
let cancelText = function NoCancel->"none"|CancelIntent->"intent"|CancelAccepted->"accepted"|CancelRefused->"refused"
let settlementText = function NoSettlement->"none"|CurrentTerminal->"current-terminal"|TargetTerminal->"target-terminal"|JobsTerminal->"jobs-terminal"|Settled->"settled"
let runProject (decision: RunDecision) =
    let s=decision.Protocol
    let draft={ Identity=String.replicate 64 "0"; Bindings=["state",QuintReplayValue.Record [
        "budget",integer decision.Lifecycle.BudgetRemaining
        "cancel",text (cancelText s.Cancel)
        "cleanupFreshCurrent",boolean s.CleanupFreshCurrent
        "cleanupStarted",boolean s.CleanupStarted
        "elapsed",integer s.ElapsedSeconds
        "identity",integer 1
        "nextAction",text (runActionText decision.Action)
        "nextPoll",integer s.NextObservationAt
        "refused",boolean s.Refusal.IsSome
        "remoteClosed",boolean (decision.Lifecycle.Closed.Contains "remote-operation")
        "resourceFactsClosed",boolean s.ResourcesRetired
        "rerun",text (rerunText s.Rerun)
        "rerunSends",integer (if s.Rerun=NotIssued then 0 else 1)
        "resourcesRetired",boolean s.ResourcesRetired
        "resultAccepted",boolean s.ResultAccepted
        "seenTarget2",boolean s.SeenTarget2
        "signalCancelled",boolean s.StickyCancelled
        "sequence",integer s.LatestSequence
        "settlement",text (settlementText s.Settlement)
        "target",text (Option.defaultValue "none" s.TargetConclusion)
    ]] }
    {draft with Identity=QuintReplay.stateFingerprint draft |> replayOk}
let applyRun name event ((lifecycle,protocol),(observations: QuintReplayObservation list)) =
    let decision=RunObservation.reduce lifecycle protocol event |> policyOk
    let observed={Index=observations.Length+1;Action=name;Source=runSource name;Actual=runProject decision}
    (decision.Lifecycle,decision.Protocol),observations@[observed]
let runStart=((runLifecycle,runInitial),[])
let runSuccess =
    runStart
    |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "stale1" (ObserveCurrentRun(3,1,200,Some(runSnapshot 1 "queued" None)))
    |> applyRun "active2" (ObserveCurrentRun(23,2,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "terminal2" (ObserveCurrentRun(43,3,200,Some(runSnapshot 2 "completed" (Some "success"))))
    |> applyRun "target2" (ObserveTargetRun(44,4,200,Some(runSnapshot 2 "completed" (Some "success"))))
    |> applyRun "jobs2" (ObserveTargetJobs(45,5,200,Some(jobs "success")))
    |> applyRun "bracket2" (ObserveCurrentRun(46,6,200,Some(runSnapshot 2 "completed" (Some "success"))))
    |> applyRun "cleanup" (EnterCleanup(47,false)) |> applyRun "retire" (RecordResourcesRetired(48,{SecretCount=0;ReleaseCount=0;AssetStatus="404";ReleaseStatus="404";TagStatus="404";PrivateKeyAbsent=true;PublicKeyAbsent=true;FailureCount=0}))
    |> applyRun "readback" (RecordResultReadback(49,true,true))
let runCancelRace =
    runStart
    |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "active2" (ObserveCurrentRun(3,1,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cleanup" (EnterCleanup(23,false)) |> applyRun "fresh2" (ObserveCurrentRun(24,2,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cancel-refused" (RecordCancelResult(25,false)) |> applyRun "retire" (RecordResourcesRetired(26,{SecretCount=0;ReleaseCount=0;AssetStatus="404";ReleaseStatus="404";TagStatus="404";PrivateKeyAbsent=true;PublicKeyAbsent=true;FailureCount=0}))
    |> applyRun "terminal2" (ObserveCurrentRun(46,3,200,Some(runSnapshot 2 "completed" (Some "failure"))))
    |> applyRun "target2" (ObserveTargetRun(47,4,200,Some(runSnapshot 2 "completed" (Some "failure"))))
    |> applyRun "jobs2" (ObserveTargetJobs(48,5,200,Some(jobs "failure")))
    |> applyRun "bracket2" (ObserveCurrentRun(49,6,200,Some(runSnapshot 2 "completed" (Some "failure"))))
let runDeadline =
    runStart
    |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "stale1" (ObserveCurrentRun(3,1,200,Some(runSnapshot 1 "completed" (Some "success"))))
    |> applyRun "stale1-again" (ObserveCurrentRun(23,2,200,Some(runSnapshot 1 "completed" (Some "success"))))
    |> applyRun "deadline" (ExhaustDeadline 2700)
let runCancelAccepted =
    runStart
    |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "active2" (ObserveCurrentRun(3,1,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cleanup" (EnterCleanup(23,false)) |> applyRun "fresh2" (ObserveCurrentRun(24,2,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cancel-accepted" (RecordCancelResult(25,true)) |> applyRun "retire" (RecordResourcesRetired(26,{SecretCount=0;ReleaseCount=0;AssetStatus="404";ReleaseStatus="404";TagStatus="404";PrivateKeyAbsent=true;PublicKeyAbsent=true;FailureCount=0}))
    |> applyRun "terminal2" (ObserveCurrentRun(46,3,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
    |> applyRun "target2" (ObserveTargetRun(47,4,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
    |> applyRun "jobs2" (ObserveTargetJobs(48,5,200,Some(jobs "cancelled")))
    |> applyRun "bracket2" (ObserveCurrentRun(49,6,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
let wrongSnapshot={runSnapshot 2 "in_progress" None with OriginalActorId=99L}
let runWrong =
    runStart |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "wrong" (ObserveCurrentRun(3,1,200,Some wrongSnapshot))
let partialJobs={jobs "failure" with TotalCount=1;Jobs=[{Id=2L;RunId=12345L;Attempt=2;HeadSha=String.replicate 40 "b";Name="qualification";Status="completed";Conclusion=Some "failure"}]}
let runPartial =
    runStart |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "active2" (ObserveCurrentRun(3,1,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "terminal2" (ObserveCurrentRun(23,2,200,Some(runSnapshot 2 "completed" (Some "failure"))))
    |> applyRun "target2" (ObserveTargetRun(24,3,200,Some(runSnapshot 2 "completed" (Some "failure"))))
    |> applyRun "partial-jobs" (ObserveTargetJobs(25,4,200,Some partialJobs))
    |> applyRun "cleanup" (EnterCleanup(26,false))
    |> applyRun "retire" (RecordResourcesRetired(27,{SecretCount=0;ReleaseCount=0;AssetStatus="404";ReleaseStatus="404";TagStatus="404";PrivateKeyAbsent=true;PublicKeyAbsent=true;FailureCount=0}))
    |> applyRun "unknown" (ObserveCurrentRun(47,5,503,None))
    |> applyRun "deadline" (ExhaustDeadline 2700)
let closedFacts={SecretCount=0;ReleaseCount=0;AssetStatus="404";ReleaseStatus="404";TagStatus="404";PrivateKeyAbsent=true;PublicKeyAbsent=true;FailureCount=0}
let runSignalCancel =
    runStart |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "active2" (ObserveCurrentRun(3,1,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "signal" (RecordSignalCancellation 4) |> applyRun "cleanup" (EnterCleanup(5,true))
    |> applyRun "fresh2" (ObserveCurrentRun(6,2,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cancel-accepted" (RecordCancelResult(7,true)) |> applyRun "retire" (RecordResourcesRetired(8,closedFacts))
    |> applyRun "terminal2" (ObserveCurrentRun(28,3,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
    |> applyRun "target2" (ObserveTargetRun(29,4,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
    |> applyRun "jobs2" (ObserveTargetJobs(30,5,200,Some(jobs "cancelled")))
    |> applyRun "bracket2" (ObserveCurrentRun(31,6,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
let runRetiredActiveSignal =
    runStart |> applyRun "intent" (RecordRerunIntent 1) |> applyRun "ack" (RecordRerunResult(2,true))
    |> applyRun "active2" (ObserveCurrentRun(3,1,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cleanup" (EnterCleanup(23,false)) |> applyRun "fresh2" (ObserveCurrentRun(24,2,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "cancel-accepted" (RecordCancelResult(25,true)) |> applyRun "retire" (RecordResourcesRetired(26,closedFacts))
    |> applyRun "active-after-retire" (ObserveCurrentRun(46,3,200,Some(runSnapshot 2 "in_progress" None)))
    |> applyRun "signal" (RecordSignalCancellation 47)
    |> applyRun "terminal2" (ObserveCurrentRun(67,4,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
    |> applyRun "target2" (ObserveTargetRun(68,5,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
    |> applyRun "jobs2" (ObserveTargetJobs(69,6,200,Some(jobs "cancelled")))
    |> applyRun "bracket2" (ObserveCurrentRun(70,7,200,Some(runSnapshot 2 "completed" (Some "cancelled"))))
let runLateSignal = runSuccess |> applyRun "signal" (RecordSignalCancellation 50)
let scenario = Environment.GetEnvironmentVariable "FSGG_FOURD_QUINT_SCENARIO"
let states, observations =
    match scenario with
    | "success" -> success
    | "stale" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
                       |> apply "admit" (Admit("id-a",1)) |> apply "invalidate" (Invalidate("id-b",1))
    | "cancelled" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "cancel" (BeginCleanup(true,1))
                           |> apply "closePlaintext" (Close("plaintext",1)) |> apply "finish" (Finish 1)
    | "unknown" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
                         |> apply "admit" (Admit("id-a",1)) |> apply "beginEffect" (BeginEffect("effect",false,1))
                         |> apply "beginCleanup" (BeginCleanup(false,1)) |> apply "closePlaintext" (Close("plaintext",1))
                         |> apply "closeEffect" (Close("effect",1)) |> apply "finish" (Finish 1)
    | "cleanup-failure" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
                                 |> apply "admit" (Admit("id-a",1)) |> apply "beginEffect" (BeginEffect("effect",false,1))
                                 |> apply "beginCleanup" (BeginCleanup(false,1)) |> apply "closePlaintext" (Close("plaintext",1))
                                 |> apply "finish" (Finish 1)
    | "run-success" -> [],snd runSuccess
    | "run-cancel-race" -> [],snd runCancelRace
    | "run-deadline" -> [],snd runDeadline
    | "run-cancel-accepted" -> [],snd runCancelAccepted
    | "run-wrong-identity" -> [],snd runWrong
    | "run-partial-jobs" -> [],snd runPartial
    | "run-signal-cancel" -> [],snd runSignalCancel
    | "run-retired-active-signal" -> [],snd runRetiredActiveSignal
    | "run-late-signal" -> [],snd runLateSignal
    | _ -> fail "FSGG_FOURD_QUINT_SCENARIO is required"

let fingerprint name =
    let value = Environment.GetEnvironmentVariable name
    if String.IsNullOrWhiteSpace value || value.Length <> 64
       || value |> Seq.exists (fun c -> not (Char.IsDigit c || c >= 'a' && c <= 'f')) then
        fail $"{name} must contain an observed SHA-256 fingerprint"
    value
let environment = {
    Seed = "20261001"; Bounds = ["steps", 16L]
    ToolFingerprint = fingerprint "FSGG_FOURD_QUINT_TOOL_SHA256"
    ProfileFingerprint = fingerprint "FSGG_FOURD_QUINT_PROFILE_SHA256"
    ContractFingerprint = fingerprint "FSGG_FOURD_QUINT_CONTRACT_SHA256"
    AdapterFingerprint = fingerprint "FSGG_FOURD_QUINT_ADAPTER_SHA256"
    ImplementationFingerprint = fingerprint "FSGG_FOURD_QUINT_IMPLEMENTATION_SHA256"
}
let bindings = observations |> List.map (fun item -> { Index=item.Index; Action=item.Action; Source=item.Source })
let trace =
    let path = Environment.GetEnvironmentVariable "FSGG_FOURD_QUINT_ITF"
    if String.IsNullOrWhiteSpace path then fail "FSGG_FOURD_QUINT_ITF is required"
    let context = { Environment=environment; Steps=bindings }
    let root = JsonNode.Parse(IO.File.ReadAllText(path)).AsObject()
    let metadata = root["#meta"].AsObject()
    metadata.Remove("description") |> ignore
    metadata.Remove("timestamp") |> ignore
    root.ToJsonString() |> QuintReplay.decodeItf context |> replayOk

match QuintReplay.compare trace observations with
| Ok QuintReplayResult.Equivalent -> ()
| value -> fail $"FsQuint correspondence failed: %A{value}"

let rewriteRunField field value actual =
    let bindings=actual.Bindings |> List.map (fun (name,binding) ->
        match name,binding with
        | "state",QuintReplayValue.Record entries ->
            let changed=entries |> List.map (fun (key,current) -> if key=field then key,value else key,current)
            "state",QuintReplayValue.Record changed
        | _ -> name,binding)
    let draft={actual with Bindings=bindings;Identity=String.replicate 64 "0"}
    {draft with Identity=QuintReplay.stateFingerprint draft |> replayOk}
let requireRunMutationDiverges label index field value =
    let mutated=observations |> List.mapi(fun offset item -> if offset=index then {item with Actual=rewriteRunField field value item.Actual} else item)
    match QuintReplay.compare trace mutated with
    | Ok (QuintReplayResult.Diverged _) -> ()
    | result -> fail $"{label} mutation did not diverge: %A{result}"
if scenario="run-success" then
    requireRunMutationDiverges "stale-attempt-success" 2 "resultAccepted" (boolean true)
    requireRunMutationDiverges "target-attempt-three" 4 "identity" (integer 2)
    requireRunMutationDiverges "budget-renewal" 10 "budget" (integer 2700)
if scenario="run-cancel-race" then
    requireRunMutationDiverges "cancel-refused-closed" 4 "remoteClosed" (boolean true)

let (lateLifecycle,lateProtocol),_ = runLateSignal
if lateProtocol.ResultAccepted || not lateProtocol.StickyCancelled || not (RunObservation.validateState lateLifecycle lateProtocol) then
    fail "late signal did not emit a valid revoked-success state"
let replayedLate=RunObservation.reduce lateLifecycle lateProtocol (RecordSignalCancellation lateProtocol.ElapsedSeconds) |> policyOk
if not (RunObservation.validateState replayedLate.Lifecycle replayedLate.Protocol) then fail "late signal replay state refused"
match RunObservation.reduce runLifecycle runInitial (RecordCancelResult(0,true)) with
| Error "run-transition-refused" -> ()
| value -> fail $"cancel result without typed intent bypassed production guard: %A{value}"

if scenario="success" && not (Policy.successful (List.last states)) then fail "acknowledged effect with complete cleanup was not successful"

let successStates,_ = success
let admitted = successStates[3]
match Policy.reduce admitted (Invalidate("id-b", 1)) with
| Ok stale when Policy.effectEligible stale -> fail "stale identity remained eligible"
| Ok _ -> ()
| Error reason -> fail reason

let unacknowledged = Policy.reduce admitted (BeginEffect("effect", false, 1)) |> policyOk
let cleaning = Policy.reduce unacknowledged (Cancel 1) |> policyOk
let onlyPlaintext = Policy.reduce cleaning (Close("plaintext", 1)) |> policyOk
let incomplete = Policy.reduce onlyPlaintext (Finish 1) |> policyOk
if Policy.cleanupComplete incomplete || Policy.successful incomplete then fail "false cleanup or success accepted"

let mutated = observations |> List.mapi (fun index item ->
    if index = 2 then
        if scenario.StartsWith("run-") then {item with Actual=observations[1].Actual}
        else { item with Actual = project { states[3] with CurrentIdentity = Some "stale" } }
    else item)
match QuintReplay.compare trace mutated with
| Ok (QuintReplayResult.Diverged _) -> ()
| value -> fail $"negative correspondence control did not diverge: %A{value}"

printfn "FourD typed reducer and FsQuint correspondence: PASS (%d transitions)" observations.Length
