module FS.GG.Coordination.MigrationStepExecutionTests

open System
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
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
      AuthorityFence=
        { AdmissionGeneration=3L; AdmissionCommit=revision "f"
          OperationGeneration=4L; OperationCommit=revision "1"
          Claim=None; SealCommit=revision "2"; RegistryCommit=revision "3" }
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
    let mutable fence = { Fence=step.AuthorityFence; Complete=true; Authorized=true }
    let mutable fenceAfterInFlight: MigrationFenceObservation option = None
    let mutable journal: MigrationJournalAuthority option = None
    let mutable effect = MigrationEffectObservation.ProvenAbsent
    let mutable dispatches = 0
    let mutable journalWrites = 0
    let mutable effectReads = 0
    let mutable targetReads = 0
    let mutable dispatchResult = MigrationDispatchOutcome.Applied

    member _.Epoch with get() = epoch and set value = epoch <- value
    member _.Target with get() = target and set value = target <- value
    member _.Fence with get() = fence and set value = fence <- value
    member _.FenceAfterInFlight with get() = fenceAfterInFlight and set value = fenceAfterInFlight <- value
    member _.Effect with get() = effect and set value = effect <- value
    member _.DispatchResult with get() = dispatchResult and set value = dispatchResult <- value
    member _.Dispatches = dispatches
    member _.JournalWrites = journalWrites
    member _.EffectReads = effectReads
    member _.TargetReads = targetReads
    member _.Journal = journal
    member _.InjectJournal value = journal <- value

    interface IMigrationStepRuntime with
        member _.ObserveEpoch() = Ok epoch
        member _.ObserveAuthorityFence() = Ok fence
        member _.ObserveTarget _ =
            targetReads <- targetReads + 1
            Ok target
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
                fenceAfterInFlight |> Option.iter (fun value -> fence <- value)
                MigrationCasOutcome.Accepted authority
            | _ -> MigrationCasOutcome.Conflict
        member _.ObserveEffect(_, _) =
            effectReads <- effectReads + 1
            Ok effect
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
let ``before intent cut leaves no durable or provider mutation and can resume`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "before-intent",
                 advance selected MigrationAdvanceCut.StopBeforeIntent runtime |> get)
    Assert.Equal(0, runtime.JournalWrites)
    Assert.Equal(0, runtime.Dispatches)
    Assert.Equal(None, runtime.Journal)
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, runtime.Dispatches)

[<Fact>]
let ``after effect readback cut retains in flight intent and refuses changed target on restart`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "after-effect-readback-before-target-readback",
                 advance selected MigrationAdvanceCut.StopAfterReadback runtime |> get)
    Assert.Equal(1, runtime.Dispatches)
    Assert.Equal(2, runtime.EffectReads)
    Assert.Equal(3, runtime.TargetReads)
    Assert.Equal(2, runtime.JournalWrites)
    Assert.Equal(Some MigrationJournalStage.InFlight, runtime.Journal |> Option.map _.Stage)
    runtime.Target <- { runtime.Target with Sha256=sha "foreign-target" }
    Assert.Equal(Error [ MigrationExecutionFailure.ChangedTarget ],
                 advance selected MigrationAdvanceCut.NoCut runtime)
    Assert.Equal(1, runtime.Dispatches)
    Assert.Equal(2, runtime.JournalWrites)

[<Fact>]
let ``before receipt cut follows target readback and restart settles without redispatch`` () =
    let selected = step ()
    let runtime = ControlledRuntime(selected)
    Assert.Equal(MigrationAdvanceResult.Interrupted "after-target-readback-before-receipt",
                 advance selected MigrationAdvanceCut.StopBeforeReceipt runtime |> get)
    Assert.Equal(1, runtime.Dispatches)
    Assert.Equal(2, runtime.EffectReads)
    Assert.Equal(4, runtime.TargetReads)
    Assert.Equal(2, runtime.JournalWrites)
    Assert.Equal(Some MigrationJournalStage.InFlight, runtime.Journal |> Option.map _.Stage)
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, runtime.Dispatches)
    Assert.Equal(3, runtime.JournalWrites)

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

[<Fact>]
let ``foreign receipt prefix cannot resume an in flight migration effect`` () =
    let selected = step ()
    for operation, seal in
        [ "migration:foreign:1", selected.Seal
          selected.OperationId, sha "foreign-step-seal" ] do
        let runtime = ControlledRuntime(selected)
        runtime.InjectJournal
            (Some { OperationId=operation; StepSeal=seal
                    Generation=selected.JournalGeneration + 2L; Commit=revision "d"
                    Stage=MigrationJournalStage.InFlight; ResultSha256=None })
        Assert.Equal(Error [ MigrationExecutionFailure.JournalConflict ],
                     advance selected MigrationAdvanceCut.NoCut runtime)
        Assert.Equal(0, runtime.Dispatches)
        Assert.Equal(0, runtime.JournalWrites)

[<Fact>]
let ``authority fence drift refuses before intent and immediately before provider dispatch`` () =
    let selected = step ()
    let stale = ControlledRuntime(selected)
    stale.Fence <- { stale.Fence with Fence={ stale.Fence.Fence with OperationGeneration=5L } }
    Assert.Equal(Error [ MigrationExecutionFailure.StaleAuthorityFence ],
                 advance selected MigrationAdvanceCut.NoCut stale)
    Assert.Equal(0, stale.JournalWrites)
    Assert.Equal(0, stale.Dispatches)

    let late = ControlledRuntime(selected)
    late.FenceAfterInFlight <-
        Some { late.Fence with Fence={ late.Fence.Fence with RegistryCommit=revision "4" } }
    Assert.Equal(Error [ MigrationExecutionFailure.StaleAuthorityFence ],
                 advance selected MigrationAdvanceCut.NoCut late)
    Assert.Equal(2, late.JournalWrites)
    Assert.Equal(0, late.Dispatches)

let private issueTypeStep () =
    let baseline = step ()
    { baseline with
        ExpectedTargetRevision="2026-09-27T08:00:00Z"
        ExpectedTargetSha256=MigrationIssueTypeStepRuntime.targetSha256 "ISSUE_1" (Some "TYPE_1")
        DesiredTargetSha256=MigrationIssueTypeStepRuntime.targetSha256 "ISSUE_1" (Some "TYPE_2")
        Seal="" }
    |> sealStep
    |> get

type private IssueTypeAuthority(step: MigrationExecutionStep) =
    let mutable journal: MigrationJournalAuthority option = None

    member _.Journal = journal

    member _.Port =
        { ObserveEpoch = fun () ->
            Ok { Phase="SwitchedV2"; ManifestSeal=step.ManifestSeal; Generation=step.EpochGeneration
                 Commit=step.EpochCommit; Complete=true; Authorized=true }
          ObserveAuthorityFence = fun () -> Ok { Fence=step.AuthorityFence; Complete=true; Authorized=true }
          ObserveJournal = fun _ -> Ok journal
          PersistIntent = fun generation head operation seal ->
            if journal.IsSome || generation <> step.JournalGeneration || head <> step.JournalHead then
                MigrationCasOutcome.Conflict
            else
                let accepted =
                    { OperationId=operation; StepSeal=seal; Generation=generation + 1L; Commit=revision "c"
                      Stage=MigrationJournalStage.IntentPersisted; ResultSha256=None }
                journal <- Some accepted
                MigrationCasOutcome.Accepted accepted
          MarkInFlight = fun generation head operation ->
            match journal with
            | Some current when current.Generation = generation && current.Commit = head
                                && current.OperationId = operation ->
                let accepted =
                    { current with Generation=generation + 1L; Commit=revision "d"
                                   Stage=MigrationJournalStage.InFlight }
                journal <- Some accepted
                MigrationCasOutcome.Accepted accepted
            | _ -> MigrationCasOutcome.Conflict
          PersistSettlement = fun generation head operation result ->
            match journal with
            | Some current when current.Generation = generation && current.Commit = head
                                && current.OperationId = operation ->
                let accepted =
                    { current with Generation=generation + 1L; Commit=revision "e"
                                   Stage=MigrationJournalStage.Settled; ResultSha256=Some result }
                journal <- Some accepted
                MigrationCasOutcome.Accepted accepted
            | _ -> MigrationCasOutcome.Conflict }

type private IssueTypeProvider(step: MigrationExecutionStep, mutationResponse: string,
                               ?foreignAfterMutation: bool) =
    let mutable typeId = "TYPE_1"
    let mutable revisionValue = step.ExpectedTargetRevision
    let mutable mutationCount = 0
    let mutable requests: GitHubRequest list = []
    let foreignAfterMutation = defaultArg foreignAfterMutation false

    member _.MutationCount = mutationCount
    member _.Requests = List.rev requests

    interface IMigrationStepProviderTransport with
        member _.Send request =
            requests <- request :: requests
            match request with
            | GraphQL graph when graph.Document.StartsWith("query", StringComparison.Ordinal) ->
                Assert.Equal(ReplaySafe, graph.Idempotency)
                Assert.Equal(Some "ISSUE_1", Map.tryFind "issueId" graph.Variables)
                let repositoryId = if foreignAfterMutation && mutationCount > 0 then 77 else 42
                Response
                    { StatusCode=200; Headers=Map.empty
                      Body=$"{{\"data\":{{\"node\":{{\"__typename\":\"Issue\",\"id\":\"ISSUE_1\",\"updatedAt\":\"{revisionValue}\",\"repository\":{{\"databaseId\":{repositoryId}}},\"issueType\":{{\"id\":\"{typeId}\"}}}}}}}}"
                      ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
            | GraphQL graph when graph.Document.StartsWith("mutation", StringComparison.Ordinal) ->
                Assert.Equal(NeverReplay, graph.Idempotency)
                Assert.Equal(Some step.OperationId, Map.tryFind "clientMutationId" graph.Variables)
                Assert.Equal(Some "ISSUE_1", Map.tryFind "issueId" graph.Variables)
                Assert.Equal(Some "TYPE_2", Map.tryFind "typeId" graph.Variables)
                mutationCount <- mutationCount + 1
                typeId <- "TYPE_2"
                revisionValue <- "2026-09-27T08:01:00Z"
                match mutationResponse with
                | "lost" -> TimedOut
                | "500" ->
                    Response
                        { StatusCode=500; Headers=Map.empty; Body="provider-failed-after-apply"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
                | "graphql-error" ->
                    Response
                        { StatusCode=200; Headers=Map.empty
                          Body=$"{{\"data\":{{\"updateIssueIssueType\":{{\"clientMutationId\":\"{step.OperationId}\",\"issue\":{{\"__typename\":\"Issue\",\"id\":\"ISSUE_1\",\"updatedAt\":\"{revisionValue}\",\"repository\":{{\"databaseId\":42}},\"issueType\":{{\"id\":\"TYPE_2\"}}}}}}}},\"errors\":[{{\"message\":\"late resolver failure\"}}]}}"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
                | "malformed" ->
                    Response
                        { StatusCode=200; Headers=Map.empty; Body="{not-json"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
                | "partial" ->
                    Response
                        { StatusCode=200; Headers=Map.empty
                          Body=$"{{\"data\":{{\"updateIssueIssueType\":{{\"clientMutationId\":\"{step.OperationId}\"}}}}}}"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
                | "mismatch" ->
                    Response
                        { StatusCode=200; Headers=Map.empty
                          Body=$"{{\"data\":{{\"updateIssueIssueType\":{{\"clientMutationId\":\"other-operation\",\"issue\":{{\"__typename\":\"Issue\",\"id\":\"ISSUE_1\",\"updatedAt\":\"{revisionValue}\",\"repository\":{{\"databaseId\":42}},\"issueType\":{{\"id\":\"TYPE_2\"}}}}}}}}}}"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
                | _ ->
                    Response
                        { StatusCode=200; Headers=Map.empty
                          Body=$"{{\"data\":{{\"updateIssueIssueType\":{{\"clientMutationId\":\"{step.OperationId}\",\"issue\":{{\"__typename\":\"Issue\",\"id\":\"ISSUE_1\",\"updatedAt\":\"{revisionValue}\",\"repository\":{{\"databaseId\":42}},\"issueType\":{{\"id\":\"TYPE_2\"}}}}}}}}}}"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
            | _ -> failwith "unexpected provider request"

let private issueTypeRuntime selected authority provider =
    MigrationIssueTypeStepRuntime.create
        selected
        { GraphQLUri=Uri "https://api.github.test/graphql"; Headers=Map.ofList [ "authorization", "Bearer controlled" ] }
        authority
        provider
    |> function Ok runtime -> runtime | Error reason -> failwith reason

[<Fact>]
let ``issue type accepts explicit null but refuses missing malformed and foreign prestate`` () =
    let selected =
        { issueTypeStep () with
            ExpectedTargetSha256=MigrationIssueTypeStepRuntime.targetSha256 "ISSUE_1" None
            Seal="" }
        |> sealStep |> get
    Assert.NotEqual(selected.ExpectedTargetSha256,
                    MigrationIssueTypeStepRuntime.targetSha256 "ISSUE_1" (Some "none"))
    for issueTypeField, repositoryId, accepted in
        [ "\"issueType\":null", 42, true
          "", 42, false
          "\"issueType\":{}", 42, false
          "\"issueType\":{\"id\":null}", 42, false
          "\"issueType\":{\"id\":\"\"}", 42, false
          "\"issueType\":\"TYPE_1\"", 42, false
          "\"issueType\":null", 77, false ] do
        let authority = IssueTypeAuthority(selected)
        let mutable requests = 0
        let field = if issueTypeField = "" then "" else "," + issueTypeField
        let body =
            sprintf
                """{"data":{"node":{"__typename":"Issue","id":"ISSUE_1","updatedAt":"2026-09-27T08:00:00Z","repository":{"databaseId":%d}%s}}}"""
                repositoryId field
        let provider =
            { new IMigrationStepProviderTransport with
                member _.Send _ =
                    requests <- requests + 1
                    Response
                        { StatusCode=200; Headers=Map.empty; Body=body; ETag=None
                          RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } } }
        let runtime = issueTypeRuntime selected authority.Port provider
        let outcome = MigrationStepExecution.advance selected MigrationAdvanceCut.StopBeforeIntent runtime
        if accepted then
            Assert.Equal(Ok(MigrationAdvanceResult.Interrupted "before-intent"), outcome)
        else
            match outcome with
            | Error [ MigrationExecutionFailure.JournalUnavailable _ ] -> ()
            | value -> failwithf "expected source refusal, got %A" value
        Assert.Equal(1, requests)
        Assert.Equal(None, authority.Journal)

[<Fact>]
let ``foreign issue repository on post-dispatch readback cannot settle`` () =
    let selected = issueTypeStep ()
    let authority = IssueTypeAuthority(selected)
    let provider = IssueTypeProvider(selected, "complete", foreignAfterMutation=true)
    let runtime = issueTypeRuntime selected authority.Port provider
    Assert.Equal(Error [ MigrationExecutionFailure.JournalUnavailable "provider-malformed-issue" ],
                 MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut runtime)
    Assert.Equal(Some MigrationJournalStage.InFlight, authority.Journal |> Option.map _.Stage)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(6, provider.Requests.Length)

[<Fact>]
let ``issue type provider runtime closes durable intent dispatch and authoritative readback`` () =
    let selected = issueTypeStep ()
    let authority = IssueTypeAuthority(selected)
    let provider = IssueTypeProvider(selected, "complete")
    let runtime = issueTypeRuntime selected authority.Port provider

    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(Some MigrationJournalStage.Settled, authority.Journal |> Option.map _.Stage)
    Assert.Equal(7, provider.Requests.Length)
    Assert.Equal(MigrationAdvanceResult.AlreadySettled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(9, provider.Requests.Length)

[<Theory>]
[<InlineData("lost")>]
[<InlineData("500")>]
[<InlineData("graphql-error")>]
[<InlineData("malformed")>]
[<InlineData("partial")>]
[<InlineData("mismatch")>]
let ``ambiguous issue type mutation responses recover by readback without blind replay`` response =
    let selected = issueTypeStep ()
    let authority = IssueTypeAuthority(selected)
    let provider = IssueTypeProvider(selected, response)
    let runtime = issueTypeRuntime selected authority.Port provider

    Assert.Equal(MigrationAdvanceResult.Pending "dispatch-outcome-unknown",
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(Some MigrationJournalStage.InFlight, authority.Journal |> Option.map _.Stage)
    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, provider.MutationCount)

[<Fact>]
let ``issue type runtime refuses unsupported effects before provider use`` () =
    let selected = issueTypeStep ()
    let unsupported =
        { selected with
            Effect=MigrationEffect.ApplyRepositorySettings(42L, sha "settings")
            TargetIdentity="repository:42/settings"
            ExpectedTargetSha256=sha "old-settings"
            DesiredTargetSha256=sha "settings"
            Seal="" }
        |> sealStep
        |> get
    let authority = IssueTypeAuthority(unsupported)
    let provider = IssueTypeProvider(unsupported, "complete")

    Assert.Equal(
        Error "unsupported-migration-effect",
        MigrationIssueTypeStepRuntime.create
            unsupported
            { GraphQLUri=Uri "https://api.github.test/graphql"; Headers=Map.empty }
            authority.Port
            provider)
    Assert.Empty(provider.Requests)

[<Fact>]
let ``provider HTTP transport returns redirect without forwarding mutation`` () =
    let reservation = new TcpListener(IPAddress.Loopback, 0)
    reservation.Start()
    let port = (reservation.LocalEndpoint :?> IPEndPoint).Port
    reservation.Stop()
    let prefix = $"http://127.0.0.1:{port}/"
    use listener = new HttpListener()
    listener.Prefixes.Add prefix
    listener.Start()
    let mutable requestCount = 0
    let server =
        task {
            let! first = listener.GetContextAsync()
            requestCount <- requestCount + 1
            first.Response.StatusCode <- 307
            first.Response.RedirectLocation <- prefix + "redirected"
            first.Response.Close()
            let second = listener.GetContextAsync()
            let! completed = Task.WhenAny(second, Task.Delay 500)
            if Object.ReferenceEquals(completed, second) then
                let context = second.Result
                requestCount <- requestCount + 1
                context.Response.StatusCode <- 200
                context.Response.Close()
        }
    use transport = new HttpMigrationStepProviderTransport()
    let outcome =
        (transport :> IMigrationStepProviderTransport).Send(
            GraphQL
                { Uri=Uri(prefix + "graphql")
                  Document="mutation { synthetic }"
                  Variables=Map.empty
                  Headers=Map.empty
                  ApiVersion=ApiVersion.required
                  Idempotency=NeverReplay })
    server.GetAwaiter().GetResult()

    match outcome with
    | Response response -> Assert.Equal(307, response.StatusCode)
    | value -> failwithf "expected the original redirect response, got %A" value
    Assert.Equal(1, requestCount)

let private blockingStep blocking blockedBy =
    let baseline = step ()
    let expected = MigrationBlockingEdgeStepRuntime.targetSha256 42L "ISSUE_1" "ISSUE_2" blocking blockedBy
    let desired =
        MigrationBlockingEdgeStepRuntime.targetSha256
            42L "ISSUE_1" "ISSUE_2" ("ISSUE_2" :: blocking) ("ISSUE_1" :: blockedBy)
    { baseline with
        OperationId="migration:blocking-edge:1"
        IdempotencyKey="migration:manifest:blocking-edge:1"
        Effect=MigrationEffect.AddBlockingEdge(42L, "ISSUE_1", "ISSUE_2")
        TargetIdentity="repository:42/blocks:ISSUE_1:ISSUE_2"
        ExpectedTargetRevision=expected
        ExpectedTargetSha256=expected
        DesiredTargetSha256=desired
        Seal="" }
    |> sealStep
    |> get

type private BlockingProvider(step: MigrationExecutionStep, responseKind: string,
                              initialBlocking: string list, initialBlockedBy: string list,
                              ?pageFault: string * string) =
    let mutable blocking = initialBlocking
    let mutable blockedBy = initialBlockedBy
    let mutable mutationCount = 0
    let mutable requestCount = 0

    member _.MutationCount = mutationCount
    member _.RequestCount = requestCount

    interface IMigrationStepProviderTransport with
        member _.Send request =
            requestCount <- requestCount + 1
            match request with
            | GraphQL graph when graph.Document.StartsWith("query", StringComparison.Ordinal) ->
                Assert.Equal(ReplaySafe, graph.Idempotency)
                let issueId = Map.find "id" graph.Variables
                let connection, values =
                    if graph.Document.Contains(" blocking(", StringComparison.Ordinal) then
                        Assert.Equal("ISSUE_1", issueId)
                        "blocking", blocking
                    else
                        Assert.Contains(" blockedBy(", graph.Document)
                        Assert.Equal("ISSUE_2", issueId)
                        "blockedBy", blockedBy
                let index = Map.tryFind "after" graph.Variables |> Option.map Int32.Parse |> Option.defaultValue 0
                let hasNext = index + 1 < values.Length
                let nodes =
                    if index < values.Length then
                        $"[{{\"__typename\":\"Issue\",\"id\":\"{values[index]}\",\"repository\":{{\"databaseId\":42}}}}]"
                    else "[]"
                let endCursor = if hasNext then $"\"{index + 1}\"" else "null"
                let pageInfo =
                    match pageFault with
                    | Some(faultConnection, "missing-boolean") when faultConnection = connection ->
                        $"{{\"endCursor\":{endCursor}}}"
                    | Some(faultConnection, "wrong-boolean") when faultConnection = connection ->
                        $"{{\"hasNextPage\":\"false\",\"endCursor\":{endCursor}}}"
                    | Some(faultConnection, "empty-cursor") when faultConnection = connection && hasNext ->
                        "{\"hasNextPage\":true,\"endCursor\":\"\"}"
                    | _ ->
                        $"{{\"hasNextPage\":{hasNext.ToString().ToLowerInvariant()},\"endCursor\":{endCursor}}}"
                Response
                    { StatusCode=200; Headers=Map.empty
                      Body=
                        sprintf
                            """{"data":{"node":{"__typename":"Issue","id":"%s","updatedAt":"2026-09-27T08:00:00Z","repository":{"databaseId":42},"%s":{"totalCount":%d,"nodes":%s,"pageInfo":%s}}}}"""
                            issueId connection values.Length nodes pageInfo
                      ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
            | GraphQL graph when graph.Document.StartsWith("mutation", StringComparison.Ordinal) ->
                Assert.Equal(NeverReplay, graph.Idempotency)
                Assert.Equal(Some "ISSUE_1", Map.tryFind "blockerId" graph.Variables)
                Assert.Equal(Some "ISSUE_2", Map.tryFind "blockedId" graph.Variables)
                Assert.Equal(Some step.OperationId, Map.tryFind "clientMutationId" graph.Variables)
                mutationCount <- mutationCount + 1
                if not (List.contains "ISSUE_2" blocking) then blocking <- "ISSUE_2" :: blocking
                if not (List.contains "ISSUE_1" blockedBy) then blockedBy <- "ISSUE_1" :: blockedBy
                match responseKind with
                | "lost" -> TimedOut
                | "partial" ->
                    Response
                        { StatusCode=200; Headers=Map.empty
                          Body=$"{{\"data\":{{\"addBlockedBy\":{{\"clientMutationId\":\"{step.OperationId}\",\"issue\":null}}}}}}"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
                | _ ->
                    Response
                        { StatusCode=200; Headers=Map.empty
                          Body=$"{{\"data\":{{\"addBlockedBy\":{{\"clientMutationId\":\"{step.OperationId}\",\"issue\":{{\"__typename\":\"Issue\",\"id\":\"ISSUE_2\",\"repository\":{{\"databaseId\":42}}}},\"blockingIssue\":{{\"__typename\":\"Issue\",\"id\":\"ISSUE_1\",\"repository\":{{\"databaseId\":42}}}}}}}}}}"
                          ETag=None; RateBudget={ Limit=None; Remaining=None; ResetAt=None; Cost=None } }
            | _ -> failwith "unexpected blocking provider request"

let private blockingRuntime selected authority provider =
    MigrationBlockingEdgeStepRuntime.create
        selected
        { GraphQLUri=Uri "https://api.github.test/graphql"; Headers=Map.empty }
        authority
        provider
    |> function Ok runtime -> runtime | Error reason -> failwith reason

[<Fact>]
let ``blocking edge runtime paginates reciprocal state and settles exact provider effect`` () =
    let blocking = [ "ISSUE_3"; "ISSUE_4" ]
    let blockedBy = [ "ISSUE_5"; "ISSUE_6" ]
    let selected = blockingStep blocking blockedBy
    let authority = IssueTypeAuthority(selected)
    let provider = BlockingProvider(selected, "complete", blocking, blockedBy)
    let runtime = blockingRuntime selected authority.Port provider

    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(57, provider.RequestCount)
    Assert.Equal(MigrationAdvanceResult.AlreadySettled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, provider.MutationCount)
    Assert.Equal(81, provider.RequestCount)

[<Theory>]
[<InlineData("lost")>]
[<InlineData("partial")>]
let ``ambiguous blocking edge response reconciles without a second mutation`` responseKind =
    let selected = blockingStep [] []
    let authority = IssueTypeAuthority(selected)
    let provider = BlockingProvider(selected, responseKind, [], [])
    let runtime = blockingRuntime selected authority.Port provider

    Assert.Equal(MigrationAdvanceResult.Settled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(Some MigrationJournalStage.Settled, authority.Journal |> Option.map _.Stage)
    Assert.Equal(MigrationAdvanceResult.AlreadySettled selected.DesiredTargetSha256,
                 advance selected MigrationAdvanceCut.NoCut runtime |> get)
    Assert.Equal(1, provider.MutationCount)

[<Fact>]
let ``blocking edge runtime refuses a desired digest not derived from exact prestate`` () =
    let selected = blockingStep [] []
    let altered = { selected with DesiredTargetSha256=sha "foreign-edge-population"; Seal="" } |> sealStep |> get
    let authority = IssueTypeAuthority(altered)
    let provider = BlockingProvider(altered, "complete", [], [])
    let runtime = blockingRuntime altered authority.Port provider

    Assert.Equal(Error [ MigrationExecutionFailure.UnauthorizedTarget ],
                 advance altered MigrationAdvanceCut.NoCut runtime)
    Assert.Equal(0, provider.MutationCount)
    Assert.Equal(None, authority.Journal)

[<Fact>]
let ``blocking edge runtime refuses partial reciprocal prestate before intent`` () =
    let selected = blockingStep [] []
    let partial = MigrationBlockingEdgeStepRuntime.targetSha256 42L "ISSUE_1" "ISSUE_2" [ "ISSUE_2" ] []
    let altered =
        { selected with ExpectedTargetRevision=partial; ExpectedTargetSha256=partial; Seal="" }
        |> sealStep
        |> get
    let authority = IssueTypeAuthority(altered)
    let provider = BlockingProvider(altered, "complete", [ "ISSUE_2" ], [])
    let runtime = blockingRuntime altered authority.Port provider

    Assert.Equal(Error [ MigrationExecutionFailure.UnauthorizedTarget ],
                 advance altered MigrationAdvanceCut.NoCut runtime)
    Assert.Equal(0, provider.MutationCount)
    Assert.Equal(None, authority.Journal)

[<Fact>]
let ``blocking edge refuses missing Boolean and empty cursor on either endpoint before intent`` () =
    for connection in [ "blocking"; "blockedBy" ] do
        for fault, values in
            [ "missing-boolean", []
              "wrong-boolean", []
              "empty-cursor", [ "ISSUE_3"; "ISSUE_4" ] ] do
            let blocking, blockedBy =
                if connection = "blocking" then values, [] else [], values
            let selected = blockingStep blocking blockedBy
            let authority = IssueTypeAuthority(selected)
            let provider = BlockingProvider(selected, "complete", blocking, blockedBy,
                                            pageFault=(connection, fault))
            let runtime = blockingRuntime selected authority.Port provider
            match MigrationStepExecution.advance selected MigrationAdvanceCut.NoCut runtime with
            | Error [ MigrationExecutionFailure.JournalUnavailable _ ] -> ()
            | result -> failwithf "expected page-info refusal, got %A" result
            Assert.Equal(0, provider.MutationCount)
            Assert.Equal(None, authority.Journal)

[<Fact>]
let ``issue type and blocking edge reject insecure provider URI before transport`` () =
    let options = { GraphQLUri=Uri "http://provider.example/graphql"; Headers=Map.empty }
    let issue = issueTypeStep ()
    let issueProvider = IssueTypeProvider(issue, "complete")
    match MigrationIssueTypeStepRuntime.create issue options (IssueTypeAuthority(issue)).Port issueProvider with
    | Error "invalid-issue-type-binding" -> ()
    | result -> failwithf "expected issue-type URI refusal, got %A" result
    Assert.Empty(issueProvider.Requests)

    let edge = blockingStep [] []
    let edgeProvider = BlockingProvider(edge, "complete", [], [])
    match MigrationBlockingEdgeStepRuntime.create edge options (IssueTypeAuthority(edge)).Port edgeProvider with
    | Error "invalid-blocking-edge-binding" -> ()
    | result -> failwithf "expected blocking-edge URI refusal, got %A" result
    Assert.Equal(0, edgeProvider.RequestCount)

[<Fact>]
let ``foreign final journal grant refuses issue and blocking mutations after valid prefix`` () =
    let foreignFinalGrant (authority: IssueTypeAuthority) =
        let source = authority.Port
        let mutable reads = 0
        { source with
            ObserveJournal = fun operation ->
                reads <- reads + 1
                match source.ObserveJournal operation with
                | Ok(Some value) when reads = 3 ->
                    Ok(Some { value with OperationId="migration:foreign:1" })
                | result -> result }

    let issue = issueTypeStep ()
    let issueAuthority = IssueTypeAuthority(issue)
    let issueProvider = IssueTypeProvider(issue, "complete")
    let issueRuntime = issueTypeRuntime issue (foreignFinalGrant issueAuthority) issueProvider
    Assert.Equal(Error [ MigrationExecutionFailure.EffectRefused "missing-fresh-in-flight-grant" ],
                 MigrationStepExecution.advance issue MigrationAdvanceCut.NoCut issueRuntime)
    Assert.Equal(Some MigrationJournalStage.InFlight, issueAuthority.Journal |> Option.map _.Stage)
    Assert.Equal(0, issueProvider.MutationCount)
    Assert.Equal(4, issueProvider.Requests.Length)

    let edge = blockingStep [] []
    let edgeAuthority = IssueTypeAuthority(edge)
    let edgeProvider = BlockingProvider(edge, "complete", [], [])
    let edgeRuntime = blockingRuntime edge (foreignFinalGrant edgeAuthority) edgeProvider
    Assert.Equal(Error [ MigrationExecutionFailure.EffectRefused "missing-fresh-in-flight-grant" ],
                 MigrationStepExecution.advance edge MigrationAdvanceCut.NoCut edgeRuntime)
    Assert.Equal(Some MigrationJournalStage.InFlight, edgeAuthority.Journal |> Option.map _.Stage)
    Assert.Equal(0, edgeProvider.MutationCount)
    Assert.Equal(16, edgeProvider.RequestCount)
