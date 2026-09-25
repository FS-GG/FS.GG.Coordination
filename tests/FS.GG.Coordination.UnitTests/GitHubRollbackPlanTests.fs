module FS.GG.Coordination.GitHubRollbackPlanTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubRollbackPlanQualification

let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private digest character = String.replicate 64 character
let private revision character = String.replicate 40 character
let private step order id domain =
    { Order=order; StepId=id; Domain=domain; TargetIdentity=$"target:{id}"
      CapturedStateSha256=sha $"captured:{id}"; RestorePayloadSha256=sha $"restore:{id}" }
let private steps =
    [ step 5 "restore-authority" AuthoritySnapshot; step 4 "restore-schedules" Schedule
      step 3 "restore-v1-projections" V1Projection; step 2 "restore-receiver-pins" ReceiverPin
      step 1 "restore-settings" Settings ]
let private withIdentityAndSteps identity candidateSteps =
    qualify identity (revision "a") (digest "b") (digest "c") (digest "d")
        (digest "e") (digest "f") (digest "1") (digest "2") "VerifiedV2" candidateSteps
        (DateTimeOffset.Parse "2026-09-23T10:00:00Z")
let private withSteps candidateSteps = withIdentityAndSteps "rollback-gs2-09-6-fixture" candidateSteps
let private baseline () = withSteps steps
let private get = function Ok value -> value | Error findings -> failwithf "unexpected refusal: %A" findings
let private refusal = function Error findings -> findings | Ok _ -> failwith "invalid rollback plan qualified"

[<Fact>]
let ``rollback plan covers all restoration domains through VerifiedV2`` () =
    let plan = baseline () |> get
    Assert.Equal("VerifiedV2", plan.StartEpoch)
    Assert.Equal("OperatingV1", plan.TerminalEpoch)
    Assert.True((requiredDomains |> Set.ofList) = (plan.Steps |> List.map _.Domain |> Set.ofList))
    Assert.Equal(Ok plan, verify plan.Seal plan)

[<Fact>]
let ``missing domain forward order and altered seal refuse`` () =
    let plan = baseline () |> get
    Assert.Contains(MissingDomain Settings, verify plan.Seal { plan with Steps=plan.Steps |> List.filter (fun value -> value.Domain <> Settings) } |> refusal)
    Assert.Contains(InvalidStepPopulation, verify plan.Seal { plan with Steps=List.rev plan.Steps } |> refusal)
    Assert.Equal(Error [ AlteredSeal ], verify (digest "9") plan)

[<Fact>]
let ``resealed five-domain plan refuses a swapped restoration sequence`` () =
    let swapped =
        [ { steps[0] with Domain=Schedule }; { steps[1] with Domain=AuthoritySnapshot } ]
        @ (steps |> List.skip 2)
    Assert.Contains(InvalidStepPopulation, withSteps swapped |> refusal)

[<Fact>]
let ``resealed plan refuses a sixth repeated domain despite contiguous reverse order`` () =
    let repeated = step 6 "restore-authority-twice" AuthoritySnapshot :: steps
    Assert.Contains(InvalidStepPopulation, withSteps repeated |> refusal)

[<Fact>]
let ``distinct rollback domains cannot alias one target identity`` () =
    let aliased = { steps[1] with TargetIdentity=steps[0].TargetIdentity }
    Assert.NotEqual(steps[0].RestorePayloadSha256, aliased.RestorePayloadSha256)
    let candidate = steps[0] :: aliased :: (steps |> List.skip 2)
    Assert.Contains(InvalidStepPopulation, withSteps candidate |> refusal)

[<Fact>]
let ``two distinct rollback targets cannot share a normalized seal through pipe injection`` () =
    let original = { steps[0] with StepId="unit"; TargetIdentity="left|authority-snapshot|right" }
    let altered = { original with StepId="unit|authority-snapshot|left"; TargetIdentity="right" }
    match withSteps (original :: steps.Tail) with
    | Error findings -> Assert.Contains(InvalidStep original.StepId, findings)
    | Ok plan ->
        let forged = { plan with Steps=altered :: plan.Steps.Tail }
        Assert.True(verify plan.Seal forged |> Result.isError)

[<Fact>]
let ``rollback step identity refuses embedded record separators`` () =
    let malformed = { steps[0] with StepId="restore-authority\nforged-step" }
    Assert.Contains(InvalidStep malformed.StepId, withSteps (malformed :: steps.Tail) |> refusal)

[<Fact>]
let ``rollback plan identity refuses an embedded field separator`` () =
    Assert.Contains(InvalidIdentity, withIdentityAndSteps "rollback\nforeign-plan" steps |> refusal)

[<Fact>]
let ``receipt prefix resumes at exactly the next reverse step`` () =
    let plan = baseline () |> get
    Assert.Equal(Ok(Some plan.Steps[0]), resume plan [])
    let first = createReceipt plan None plan.Steps[0] (sha "result:5")
    Assert.Equal(Ok(Some plan.Steps[1]), resume plan [ first ])
    let second = createReceipt plan (Some first) plan.Steps[1] (sha "result:4")
    Assert.Equal(Ok(Some plan.Steps[2]), resume plan [ first; second ])

[<Fact>]
let ``every interrupted rollback prefix resumes deterministically and a completed replay is inert`` () =
    let plan = baseline () |> get
    let restored = plan.Steps |> List.map (fun step -> step.TargetIdentity, step.RestorePayloadSha256) |> Map.ofList

    for interruptedAfter in 0 .. plan.Steps.Length do
        let mutable copy =
            plan.Steps
            |> List.map (fun step -> step.TargetIdentity, sha $"mutated:{step.StepId}")
            |> Map.ofList
        let mutable receipts: GitHubRollbackReceipt list = []

        let applyOne () =
            match resume plan receipts |> get with
            | None -> false
            | Some step ->
                copy <- copy |> Map.add step.TargetIdentity step.RestorePayloadSha256
                let receipt = createReceipt plan (List.tryLast receipts) step copy[step.TargetIdentity]
                receipts <- receipts @ [ receipt ]
                true

        for _ in 1 .. interruptedAfter do
            Assert.True(applyOne ())

        Assert.Equal(Ok(List.tryItem interruptedAfter plan.Steps), resume plan receipts)

        while applyOne () do ()

        Assert.True((copy = restored))
        Assert.Equal(Ok None, resume plan receipts)
        Assert.False(applyOne ())
        Assert.True((copy = restored))
        Assert.Contains(InvalidReceipt receipts.Head.StepId, resume plan [ receipts.Head; receipts.Head ] |> refusal)

[<Fact>]
let ``receipt gaps reordering and foreign plan binding refuse`` () =
    let plan = baseline () |> get
    let first = createReceipt plan None plan.Steps[0] (sha "result:5")
    let second = createReceipt plan (Some first) plan.Steps[1] (sha "result:4")
    Assert.Contains(InvalidReceipt second.StepId, resume plan [ second ] |> refusal)
    Assert.Contains(InvalidReceipt first.StepId, resume plan [ { first with PlanSeal=digest "8" } ] |> refusal)
    Assert.True(resume plan [ first; { second with PreviousReceiptSha256=None } ] |> Result.isError)

[<Fact>]
let ``rollback controls require two complete green inventories`` () =
    let passing: GitHubRollbackPlanControlResult list = requiredControls |> List.map (fun control -> { Control=control; ControlPassed=true; BaselineGreen=true })
    Assert.Equal(Ok(), validateControls passing passing)
    Assert.True(validateControls passing passing.Tail |> Result.isError)
