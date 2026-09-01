module FS.GG.Coordination.GitHubIntakeTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private allFacts values =
    [ IntakeSurface.Issue; IntakeSurface.IssueFields; IntakeSurface.ProjectMembership; IntakeSurface.Hierarchy; IntakeSurface.Dependencies; IntakeSurface.ProtocolState ]
    |> List.map (fun surface -> { Surface = surface; Outcome = values |> Map.tryFind surface |> Option.defaultValue (IntakeOutcome.Observed("before-" + string surface)) })

let private observation revision facts = { Identity = "FS-GG/FS.GG.Coordination#196"; Revision = revision; Pages = [ { Number = 1; Facts = facts; TerminalPage = true } ] }
let private baseline = observation "rev-1" (allFacts Map.empty)
let private planned () =
    match IntakeAdapter.plan "cause-196" [ InitializeProtocolIssue "issue-ready"; InitializeRequiredIssueFields "fields-ready" ] baseline with
    | Ok(IntakePlanned value) -> value
    | value -> failwithf "%A" value
let private after revision updates =
    allFacts updates |> observation revision

[<Fact>]
let ``intake observation is exhaustive ordered and preserves typed outcomes`` () =
    let typed = allFacts (Map.ofList [ (IntakeSurface.Hierarchy, IntakeOutcome.Redacted); (IntakeSurface.Dependencies, IntakeOutcome.Unauthorized "denied") ])
    let snapshot = IntakeAdapter.observe (observation "rev" typed) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(6, snapshot.Facts.Length)
    Assert.Matches("^[0-9a-f]{64}$", snapshot.Digest)
    let missing = typed |> List.filter (fun fact -> fact.Surface <> IntakeSurface.ProtocolState)
    match IntakeAdapter.observe (observation "rev" missing) with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "INTAKE-MISSING-SURFACE" && finding.Surface = Some IntakeSurface.ProtocolState)
    | Ok _ -> failwith "accepted an incomplete observation"
    let duplicate = typed.Head :: typed
    match IntakeAdapter.observe (observation "rev" duplicate) with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "INTAKE-DUPLICATE-SURFACE")
    | Ok _ -> failwith "accepted a duplicate surface"

[<Fact>]
let ``planning is sealed canonical bounded and no-op aware`` () =
    let first = planned ()
    let second = planned ()
    Assert.Equal(first, second)
    Assert.Equal<IntakeSurface list>([ IntakeSurface.Issue; IntakeSurface.IssueFields ], first.Effects |> List.map _.Surface)
    Assert.Matches("^[0-9a-f]{64}$", first.Digest)
    let satisfied = after "rev-2" (Map.ofList [ (IntakeSurface.Issue, IntakeOutcome.Observed "issue-ready"); (IntakeSurface.IssueFields, IntakeOutcome.Observed "fields-ready") ])
    match IntakeAdapter.plan "cause-196" [ InitializeProtocolIssue "issue-ready"; InitializeRequiredIssueFields "fields-ready" ] satisfied with
    | Ok(IntakeNoOp receipt) -> Assert.Matches("^[0-9a-f]{64}$", receipt.Digest)
    | value -> failwithf "%A" value

[<Fact>]
let ``controlled apply fences orders rereads and verifies exact post state`` () =
    let plan = planned ()
    let first = after "rev-2" (Map.ofList [ (IntakeSurface.Issue, IntakeOutcome.Observed "issue-ready") ])
    let second = after "rev-3" (Map.ofList [ (IntakeSurface.Issue, IntakeOutcome.Observed "issue-ready"); (IntakeSurface.IssueFields, IntakeOutcome.Observed "fields-ready") ])
    let script = [ { Ordinal = 1; Accepted = true; Reason = None; After = first }; { Ordinal = 2; Accepted = true; Reason = None; After = second } ]
    let receipt = IntakeAdapter.applyControlled plan baseline Execute script |> Result.defaultWith (failwithf "%A")
    Assert.Equal("rev-3", receipt.FinalRevision)
    Assert.Equal(2, receipt.AcceptedEffects.Length)
    let drifted = { baseline with Revision = "rev-drift" }
    Assert.Equal(Error FullFenceChanged, IntakeAdapter.applyControlled plan drifted Execute script)
    Assert.Equal(Error(DurableResultMismatch 1), IntakeAdapter.applyControlled plan baseline Execute [ { script.Head with Ordinal = 2 }; script.Tail.Head ])

[<Fact>]
let ``resume skips proven effects and compensation is reverse ordered`` () =
    let plan = planned ()
    let first = after "rev-2" (Map.ofList [ (IntakeSurface.Issue, IntakeOutcome.Observed "issue-ready") ])
    let durable = { PlanDigest = plan.Digest; Ordinal = 1; ResultRevision = "rev-2"; PostStateDigest = (IntakeAdapter.observe first |> Result.defaultWith (failwithf "%A")).Digest }
    let completed = after "rev-3" (Map.ofList [ (IntakeSurface.Issue, IntakeOutcome.Observed "issue-ready"); (IntakeSurface.IssueFields, IntakeOutcome.Observed "fields-ready") ])
    let resumed = IntakeAdapter.applyControlled plan first (IntakeApplyMode.Resume [ durable ]) [ { Ordinal = 2; Accepted = true; Reason = None; After = completed } ] |> Result.defaultWith (failwithf "%A")
    Assert.Equal(2, resumed.AcceptedEffects.Length)
    let forged = { durable with PostStateDigest = String.replicate 64 "0" }
    Assert.Equal(Error(DurableResultMismatch 1), IntakeAdapter.applyControlled plan first (IntakeApplyMode.Resume [ forged ]) [ { Ordinal = 2; Accepted = true; Reason = None; After = completed } ])
    let undoSecond = first
    let undone = baseline |> fun value -> { value with Revision = "rev-4" }
    let compensation = [ { Ordinal = 2; Accepted = true; Reason = None; After = undoSecond }; { Ordinal = 1; Accepted = true; Reason = None; After = undone } ]
    let durableAll = resumed.AcceptedEffects
    let receipt = IntakeAdapter.applyControlled plan completed (IntakeApplyMode.Compensate durableAll) compensation |> Result.defaultWith (failwithf "%A")
    Assert.Equal<int list>([ 2; 1 ], receipt.CompensatedOrdinals)

[<Fact>]
let ``intake qualification inventory is independently exact`` () =
    let passing: GitHubIntakeControlResult list = GitHubIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubIntakeQualification.validate passing passing)
    let broken = passing |> List.tail
    match GitHubIntakeQualification.validate passing broken with Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GIAQ-INVENTORY") | Ok () -> failwith "accepted omitted control"
