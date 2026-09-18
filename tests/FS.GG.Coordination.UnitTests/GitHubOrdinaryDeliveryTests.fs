#nowarn "3391"

module FS.GG.Coordination.GitHubOrdinaryDeliveryTests

open System
open Xunit
open FS.GG.Coordination.GitHub

let private sha character = String.replicate 40 character

let private observation epoch =
    {
        Repository = "FS-GG/FS.GG.Coordination"
        RepositoryId = 101L
        PullRequestNumber = 421
        PullRequestNodeId = "PR_kwDOordinary"
        BaseRef = "main"
        BaseSha = sha "a"
        HeadSha = sha "b"
        PolicyRevision = sha "c"
        Checks =
            [
                { Identity = "bootstrap-qualification"; AppId = 10L; Conclusion = CheckPassed }
                { Identity = "routine-eligibility"; AppId = 11L; Conclusion = CheckPassed }
            ]
        Epoch = epoch
        EpochGeneration = 3L
        EpochCommit = sha "9"
        JournalGeneration = 7L
        JournalHead = sha "d"
        SourceComplete = true
        ChecksComplete = true
        Authorized = true
        Supported = true
    }

type private Runtime(initialObservation: OrdinaryDeliveryObservation) =
    let mutable currentObservation = initialObservation
    let mutable journal: OrdinaryJournalAuthority option = None
    let mutable effect: OrdinaryEffectObservation = OrdinaryEffectObservation.EffectProvenAbsent
    let mutable dispatches = 0
    let mutable mutations = 0
    let mutable unavailable = false

    member _.Observation with get () = currentObservation and set value = currentObservation <- value
    member _.Journal with get () = journal and set value = journal <- value
    member _.Effect with get () = effect and set value = effect <- value
    member _.Dispatches = dispatches
    member _.Mutations = mutations
    member _.Unavailable with get () = unavailable and set value = unavailable <- value

    interface IOrdinaryDeliveryRuntime with
        member _.Observe() = if unavailable then Error "observer-outage" else Ok currentObservation
        member _.ObserveJournal _ = Ok journal
        member _.PersistIntent(expected, digest, operation) =
            if journal.IsSome || expected <> currentObservation.JournalGeneration then CasConflict else
            let value = { OperationId = operation; PlanDigest = digest; Generation = expected + 1L; Stage = OrdinaryIntentPersisted; MergeCommit = None }
            journal <- Some value
            mutations <- mutations + 1
            CasAccepted value
        member _.MarkPending(expected, operation) =
            match journal with
            | Some current when current.Generation = expected && current.OperationId = operation ->
                let value = { current with Generation = expected + 1L; Stage = OrdinaryEffectPending }
                journal <- Some value
                mutations <- mutations + 1
                CasAccepted value
            | _ -> CasConflict
        member _.ObserveEffect _ = Ok effect
        member _.DispatchMerge(_, _, _, expectedHead) =
            dispatches <- dispatches + 1
            effect <- OrdinaryEffectObservation.EffectApplied expectedHead
            DispatchApplied expectedHead
        member _.PersistSettlement(expected, operation, commit) =
            match journal with
            | Some current when current.Generation = expected && current.OperationId = operation ->
                let value = { current with Generation = expected + 1L; Stage = OrdinarySettled; MergeCommit = Some commit }
                journal <- Some value
                mutations <- mutations + 1
                CasAccepted value
            | _ -> CasConflict

let private planned observation =
    OrdinaryDelivery.plan observation |> Result.defaultWith (sprintf "%A" >> failwith)

[<Fact>]
let ``identical facts produce identical sealed plan bytes regardless of check order`` () =
    let first = observation "OperatingV1"
    let second = { first with Checks = List.rev first.Checks }
    let firstPlan, firstBytes = planned first
    let secondPlan, secondBytes = planned second
    Assert.Equal(firstPlan.Seal, secondPlan.Seal)
    Assert.Equal<byte>(firstBytes, secondBytes)
    Assert.Equal(firstPlan, OrdinaryDelivery.readPlan firstBytes |> Result.defaultWith (sprintf "%A" >> failwith))

[<Fact>]
let ``altered plan and incomplete unauthorized unsupported observations fail distinctly`` () =
    let _, bytes = planned (observation "OperatingV1")
    let altered = Array.copy bytes
    altered[altered.Length - 2] <- byte '0'
    Assert.Equal(Error [ AlteredPlan ], OrdinaryDelivery.readPlan altered)

    let invalid =
        { observation "OperatingV1" with SourceComplete = false; ChecksComplete = false; Authorized = false; Supported = false }
    match OrdinaryDelivery.inspect invalid with
    | Ok _ -> Assert.Fail "invalid observation accepted"
    | Error failures ->
        Assert.Contains(MissingSourcePages, failures)
        Assert.Contains(MissingCheckPages, failures)
        Assert.Contains(UnauthorizedObservation, failures)
        Assert.Contains(UnsupportedObservation, failures)

[<Fact>]
let ``pre OpenV2 and failed required check refuse with zero mutation`` () =
    let closed = observation "OperatingV1"
    let _, bytes = planned closed
    let runtime = Runtime closed
    Assert.Equal(Error [ PreOpenV2Refusal ], OrdinaryDelivery.advance bytes NoCut runtime)
    Assert.Equal(0, runtime.Mutations)
    Assert.Equal(0, runtime.Dispatches)

    let failed =
        { observation "OpenV2" with Checks = [ { Identity = "required"; AppId = 10L; Conclusion = CheckFailed } ] }
    let _, failedBytes = planned failed
    let failedRuntime = Runtime failed
    Assert.Equal(Error [ RequiredCheckNotPassed "required" ], OrdinaryDelivery.advance failedBytes NoCut failedRuntime)
    Assert.Equal(0, failedRuntime.Dispatches)

[<Fact>]
let ``fresh process recovers every interruption without duplicate merge effect`` () =
    let observed = observation "OpenV2"
    let _, bytes = planned observed

    let beforeDispatch = Runtime observed
    Assert.Equal(Ok(AdvanceInterrupted "before-dispatch"), OrdinaryDelivery.advance bytes StopAfterIntent beforeDispatch)
    Assert.Equal(0, beforeDispatch.Dispatches)
    Assert.Equal(Ok(AdvanceSettled observed.HeadSha), OrdinaryDelivery.advance bytes NoCut beforeDispatch)
    Assert.Equal(1, beforeDispatch.Dispatches)

    let afterDispatch = Runtime observed
    Assert.Equal(Ok(AdvanceInterrupted "after-dispatch-before-response"), OrdinaryDelivery.advance bytes StopAfterDispatch afterDispatch)
    Assert.Equal(1, afterDispatch.Dispatches)
    afterDispatch.Unavailable <- true
    Assert.Equal(Error [ JournalUnavailable "observer-outage" ], OrdinaryDelivery.advance bytes NoCut afterDispatch)
    Assert.Equal(OrdinaryEffectPending, afterDispatch.Journal.Value.Stage)
    Assert.Equal(1, afterDispatch.Dispatches)
    afterDispatch.Unavailable <- false
    Assert.Equal(Ok(AdvanceSettled observed.HeadSha), OrdinaryDelivery.advance bytes NoCut afterDispatch)
    Assert.Equal(1, afterDispatch.Dispatches)

    let afterEffect = Runtime observed
    Assert.Equal(Ok(AdvanceInterrupted "after-effect-before-receipt"), OrdinaryDelivery.advance bytes StopAfterEffect afterEffect)
    Assert.Equal(1, afterEffect.Dispatches)
    Assert.Equal(Ok(AdvanceSettled observed.HeadSha), OrdinaryDelivery.advance bytes NoCut afterEffect)
    Assert.Equal(1, afterEffect.Dispatches)
    Assert.Equal(Ok(AdvanceAlreadySettled observed.HeadSha), OrdinaryDelivery.advance bytes NoCut afterEffect)
    Assert.Equal(1, afterEffect.Dispatches)

[<Fact>]
let ``changed source policy subject outage and competing generation cannot advance`` () =
    let observed = observation "OpenV2"
    let plan, bytes = planned observed

    let changed = Runtime { observed with HeadSha = sha "e" }
    Assert.Equal(Error [ ChangedSource ], OrdinaryDelivery.advance bytes NoCut changed)

    let stale = Runtime { observed with PolicyRevision = sha "f" }
    Assert.Equal(Error [ StalePolicy ], OrdinaryDelivery.advance bytes NoCut stale)

    let staleEpoch = Runtime { observed with EpochGeneration = 4L }
    Assert.Equal(Error [ StaleEpoch ], OrdinaryDelivery.advance bytes NoCut staleEpoch)

    let crossed = Runtime { observed with PullRequestNumber = 422 }
    Assert.Equal(Error [ CrossSubjectObservation ], OrdinaryDelivery.advance bytes NoCut crossed)

    let outage = Runtime observed
    outage.Unavailable <- true
    Assert.Equal(Error [ JournalUnavailable "observer-outage" ], OrdinaryDelivery.advance bytes NoCut outage)

    let competing = Runtime observed
    competing.Journal <- Some { OperationId = plan.OperationId; PlanDigest = sha "0" + String.replicate 24 "0"; Generation = 8L; Stage = OrdinaryIntentPersisted; MergeCommit = None }
    Assert.Equal(Error [ JournalConflict ], OrdinaryDelivery.advance bytes NoCut competing)
    Assert.Equal(0, competing.Dispatches)
