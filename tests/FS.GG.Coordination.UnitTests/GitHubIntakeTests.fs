module FS.GG.Coordination.GitHubIntakeTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private surfaces = [ IssueIdentity; NativeIssueType; OrganizationFields; ProjectMembership; Hierarchy; Dependencies; RepositoryScope; InitialJournal; SchedulingIntent; Contract; TouchSet; Projections ]
let private defaultValue = function RepositoryScope -> "FS-GG/FS.GG.Coordination" | surface -> "before-" + string surface
let private facts changes = surfaces |> List.map (fun surface -> { Surface = surface; Outcome = changes |> Map.tryFind surface |> Option.defaultValue (Observed(defaultValue surface)) })
let private page number cursor nextCursor terminal values = { Number = number; Cursor = cursor; NextCursor = nextCursor; Facts = values; TerminalPage = terminal }
let private observation revision values = { Identity = "FS-GG/FS.GG.Coordination#196"; Revision = revision; Pages = [ page 1 None None true (facts values) ] }
let private baseline = observation "rev-1" Map.empty
let private request initializations = { Identity = baseline.Identity; Repository = "FS-GG/FS.GG.Coordination"; Causation = "cause-196"; Initializations = initializations }
let private canonical initializations = IntakeAdapter.validate (request initializations) |> Result.defaultWith (failwithf "%A")
let private intents = [ InitializeJournal "journal-ready"; InitializeSchedulingIntent "Ready" ]
let private planned () = match IntakeAdapter.plan (canonical intents) baseline with Ok(IntakePlanned value) -> value | value -> failwithf "%A" value
let private result (effect: IntakeEffect) after : ScriptedEffectResult = { Ordinal = effect.Ordinal; OperationIdentity = effect.OperationIdentity; Accepted = true; Reason = None; After = after }

[<Fact>]
let ``validation is pure canonical and permits only five initialization families`` () =
    let full = canonical [ InitializeProjections [ "status"; "type"; "status" ]; InitializeTouchSet [ "src/**" ]; InitializeContract "sha256:abc"; InitializeSchedulingIntent "Ready"; InitializeJournal "journal" ]
    Assert.Equal(5, full.Initializations.Length)
    Assert.Matches("^[0-9a-f]{64}$", full.Digest)
    match full.Initializations with
    | [ InitializeContract _; InitializeJournal _; InitializeProjections [ "status"; "type" ]; InitializeSchedulingIntent _; InitializeTouchSet _ ] -> ()
    | value -> failwithf "%A" value
    match IntakeAdapter.validate (request [ InitializeJournal "one"; InitializeJournal "two" ]) with Error findings -> Assert.Contains(findings, fun value -> value.Code = "INTAKE-DUPLICATE-INTENT") | Ok _ -> failwith "accepted duplicate family"
    match IntakeAdapter.validate (request [ InitializeJournal null ]) with Error findings -> Assert.Contains(findings, fun value -> value.Code = "INTAKE-INTENT-VALUE") | Ok _ -> failwith "accepted null value"

[<Fact>]
let ``inspection proves pages cursors completeness and typed failure outcomes`` () =
    let all = facts Map.empty
    let split = all |> List.splitAt 4
    let left, remaining = split
    let middle, right = remaining |> List.splitAt 4
    let complete = { baseline with Pages = [ page 1 None (Some "c1") false left; page 2 (Some "c1") (Some "c2") false middle; page 3 (Some "c2") None true right ] }
    Assert.True(IntakeAdapter.inspect complete |> Result.isOk)
    Assert.True(IntakeAdapter.inspect { complete with Pages = complete.Pages |> List.tail } |> Result.isError)
    let repeated = { complete with Pages = [ complete.Pages[0]; { complete.Pages[1] with Number = 1 }; complete.Pages[2] ] }
    Assert.True(IntakeAdapter.inspect repeated |> Result.isError)
    let cycle = { complete with Pages = [ { complete.Pages[0] with NextCursor = Some "c1" }; { complete.Pages[1] with Cursor = Some "c1"; NextCursor = Some "c1" }; { complete.Pages[2] with Cursor = Some "c1" } ] }
    match IntakeAdapter.inspect cycle with Error findings -> Assert.Contains(findings, fun value -> value.Code = "INTAKE-CURSOR-CYCLE") | Ok _ -> failwith "accepted cursor cycle"
    let missing = all |> List.filter (fun fact -> fact.Surface <> OrganizationFields)
    Assert.True(IntakeAdapter.inspect { baseline with Pages = [ page 1 None None true missing ] } |> Result.isError)
    Assert.True(IntakeAdapter.inspect { baseline with Pages = [ Unchecked.defaultof<IntakePage> ] } |> Result.isError)

[<Fact>]
let ``planning preserves every typed refusal family and repository scope`` () =
    let cases =
        [ NativeIssueType, IntakeOutcome.Unknown "future", "INTAKE-UNKNOWN-TYPE"
          ProjectMembership, IntakeOutcome.Duplicate "two-items", "INTAKE-DUPLICATE-MEMBERSHIP"
          Hierarchy, IntakeOutcome.Cycle "parent-cycle", "INTAKE-RELATION-CYCLE"
          Dependencies, IntakeOutcome.Cycle "dependency-cycle", "INTAKE-RELATION-CYCLE"
          OrganizationFields, IntakeOutcome.Partial "truncated", "INTAKE-PARTIAL"
          InitialJournal, IntakeOutcome.Stale("rev-0", "rev-1"), "INTAKE-STALE"
          IssueIdentity, IntakeOutcome.Unauthorized "denied", "INTAKE-UNAUTHORIZED"
          TouchSet, IntakeOutcome.Unsupported "feature", "INTAKE-UNSUPPORTED"
          Projections, IntakeOutcome.Indeterminate "timeout", "INTAKE-INDETERMINATE" ]
    for surface, outcome, code in cases do
        match IntakeAdapter.plan (canonical intents) (observation "rev-1" (Map.ofList [ surface, outcome ])) with Error findings -> Assert.Contains(findings, fun value -> value.Code = code && value.Surface = Some surface) | Ok _ -> failwithf "accepted %A" outcome
    match IntakeAdapter.plan (canonical intents) (observation "rev-1" (Map.ofList [ RepositoryScope, Observed "FS-GG/Other" ])) with Error findings -> Assert.Contains(findings, fun value -> value.Code = "INTAKE-REPOSITORY-DRIFT") | Ok _ -> failwith "accepted repository drift"

[<Fact>]
let ``sealed plan has stable operation identities dependencies and exact contracts`` () =
    let first, second = planned (), planned ()
    Assert.Equal(first, second)
    Assert.Equal(2, first.Effects.Length)
    Assert.Empty(first.Effects[0].Dependencies)
    Assert.Equal<string list>([ first.Effects[0].OperationIdentity ], first.Effects[1].Dependencies)
    Assert.All(first.Effects, fun effect -> Assert.Equal("rev-1", effect.ExpectedRevision); Assert.Matches("^[0-9a-f]{64}$", effect.OperationIdentity))
    let altered = { first with Effects = [ { first.Effects[0] with Postcondition = { first.Effects[0].Postcondition with Outcome = Observed "altered" } }; first.Effects[1] ] }
    Assert.Equal(Error InvalidSealedPlan, IntakeAdapter.applyControlled altered baseline Execute [])
    let reordered = { first with Effects = List.rev first.Effects }
    Assert.Equal(Error InvalidSealedPlan, IntakeAdapter.applyControlled reordered baseline Execute [])

[<Fact>]
let ``controlled apply fences order post state and partial failures`` () =
    let plan = planned ()
    let first = observation "rev-2" (Map.ofList [ InitialJournal, Observed "journal-ready" ])
    let completed = observation "rev-3" (Map.ofList [ InitialJournal, Observed "journal-ready"; SchedulingIntent, Observed "Ready" ])
    let script = [ result plan.Effects[0] first; result plan.Effects[1] completed ]
    let receipt = IntakeAdapter.applyControlled plan baseline Execute script |> Result.defaultWith (failwithf "%A")
    Assert.Equal(2, receipt.AcceptedEffects.Length)
    Assert.Equal(Error FullFenceChanged, IntakeAdapter.applyControlled plan { baseline with Revision = "rev-drift" } Execute script)
    let wrongOrder = [ { script[0] with Ordinal = 2 }; script[1] ]
    Assert.Equal(Error(DurableResultMismatch 1), IntakeAdapter.applyControlled plan baseline Execute wrongOrder)
    let mismatch = [ { script[0] with After = { baseline with Revision = "rev-2" } }; script[1] ]
    Assert.Equal(Error(EffectPostStateMismatch 1), IntakeAdapter.applyControlled plan baseline Execute mismatch)
    let rejected = [ script[0]; { script[1] with Accepted = false; Reason = Some "fixture-failure" } ]
    match IntakeAdapter.applyControlled plan baseline Execute rejected with
    | Error(EffectRejected(2, "fixture-failure", accepted)) -> Assert.Single(accepted) |> ignore
    | value -> failwithf "%A" value

[<Fact>]
let ``replay resume roll forward and reverse compensation require durable correspondence`` () =
    let plan = planned ()
    let first = observation "rev-2" (Map.ofList [ InitialJournal, Observed "journal-ready" ])
    let completed = observation "rev-3" (Map.ofList [ InitialJournal, Observed "journal-ready"; SchedulingIntent, Observed "Ready" ])
    let initial = IntakeAdapter.applyControlled plan baseline Execute [ result plan.Effects[0] first; result plan.Effects[1] completed ] |> Result.defaultWith (failwithf "%A")
    let durable = initial.AcceptedEffects.Head
    Assert.True(IntakeAdapter.applyControlled plan first (Resume [ durable ]) [ result plan.Effects[1] completed ] |> Result.isOk)
    Assert.True(IntakeAdapter.applyControlled plan first (RollForward [ durable ]) [ result plan.Effects[1] completed ] |> Result.isOk)
    Assert.True(IntakeAdapter.applyControlled plan completed (Resume initial.AcceptedEffects) [] |> Result.isOk)
    let forged = { durable with PostStateDigest = String.replicate 64 "0" }
    Assert.Equal(Error(DurableResultMismatch 1), IntakeAdapter.applyControlled plan first (Resume [ forged ]) [ result plan.Effects[1] completed ])
    let undone = { baseline with Revision = "rev-4" }
    let compensation = [ result plan.Effects[1] first; result plan.Effects[0] undone ]
    let receipt = IntakeAdapter.applyControlled plan completed (Compensate initial.AcceptedEffects) compensation |> Result.defaultWith (failwithf "%A")
    Assert.Equal<int list>([ 2; 1 ], receipt.CompensatedOrdinals)

[<Fact>]
let ``intake qualification inventory is independently exact`` () =
    let passing: GitHubIntakeControlResult list = GitHubIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(19, passing.Length)
    Assert.Equal(Ok (), GitHubIntakeQualification.validate passing passing)
    match GitHubIntakeQualification.validate passing (List.tail passing) with Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GIAQ-INVENTORY") | Ok () -> failwith "accepted omitted control"
