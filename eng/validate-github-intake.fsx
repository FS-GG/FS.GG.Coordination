#load "../src/FS.GG.Coordination.GitHub/IntakeAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubIntakeQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args[0]
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-3/corpus.json")
let independentPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-3/independent-expectations.json")
if not (File.Exists corpusPath) then fail "GIAQ-CORPUS-MISSING" corpusPath
if not (File.Exists independentPath) then fail "GIAQ-INDEPENDENT-MISSING" independentPath
let corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
let independentDocument = JsonDocument.Parse(File.ReadAllBytes independentPath)
let json = corpus.RootElement
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-intake-corpus/1" then fail "GIAQ-CORPUS-SCHEMA" corpusPath
if json.GetProperty("registeredContractSha256").GetString() <> "01e29e3b7f0f049364a64b85c3b75f1e47f37f256778052e66d3894b23632ae6" then fail "GIAQ-CONTRACT" "registered contract mismatch"
if json.GetProperty("acceptedPredecessorReceiptSha256").GetString() <> "a8474e696d2c1ff149ec1efb6a4c4b4cb6fe6e56b86ec840871b4430864f0a50" then fail "GIAQ-PREDECESSOR" "accepted predecessor mismatch"
let required = GitHubIntakeQualification.requiredControls |> List.map GitHubIntakeQualification.controlId
let observedControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if observedControls <> required then fail "GIAQ-CORPUS-INVENTORY" (String.concat "," observedControls)
let independentControls = independentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if independentControls <> required then fail "GIAQ-INDEPENDENT-INVENTORY" (String.concat "," independentControls)

let surfaces = [ IssueIdentity; NativeIssueType; OrganizationFields; ProjectMembership; Hierarchy; Dependencies; RepositoryScope; InitialJournal; SchedulingIntent; Contract; TouchSet; Projections ]
let defaultValue = function RepositoryScope -> "FS-GG/FS.GG.Coordination" | surface -> "before-" + string surface
let facts changes = surfaces |> List.map (fun surface -> { Surface = surface; Outcome = changes |> Map.tryFind surface |> Option.defaultValue (Observed(defaultValue surface)) })
let page number cursor nextCursor terminal values = { Number = number; Cursor = cursor; NextCursor = nextCursor; Facts = values; TerminalPage = terminal }
let observation revision changes = { Identity = "FS-GG/FS.GG.Coordination#196"; Revision = revision; Pages = [ page 1 None None true (facts changes) ] }
let baseline = observation "rev-1" Map.empty
let intent = IntakeAdapter.validate { Identity = baseline.Identity; Repository = "FS-GG/FS.GG.Coordination"; Causation = "gate-cause"; Initializations = [ InitializeJournal "journal-ready"; InitializeSchedulingIntent "Ready" ] } |> Result.defaultWith (fail "GIAQ-VALIDATE" << sprintf "%A")
let plan = match IntakeAdapter.plan intent baseline with Ok(IntakePlanned value) -> value | value -> fail "GIAQ-PLAN" (sprintf "%A" value)
let first = observation "rev-2" (Map.ofList [ (InitialJournal, Observed "journal-ready") ])
let completed = observation "rev-3" (Map.ofList [ (InitialJournal, Observed "journal-ready"); (SchedulingIntent, Observed "Ready") ])
let scripted (effect: IntakeEffect) after : ScriptedEffectResult = { Ordinal = effect.Ordinal; OperationIdentity = effect.OperationIdentity; Accepted = true; Reason = None; After = after }
let script = [ scripted plan.Effects[0] first; scripted plan.Effects[1] completed ]
let receipt = IntakeAdapter.applyControlled plan baseline Execute script |> Result.defaultWith (fail "GIAQ-APPLY" << sprintf "%A")
let allFacts = facts Map.empty
let firstFacts, rest = List.splitAt 4 allFacts
let middleFacts, lastFacts = List.splitAt 4 rest
let paged = { baseline with Pages = [ page 1 None (Some "c1") false firstFacts; page 2 (Some "c1") (Some "c2") false middleFacts; page 3 (Some "c2") None true lastFacts ] }
let cyclePaged = { paged with Pages = [ { paged.Pages[0] with NextCursor = Some "c1" }; { paged.Pages[1] with Cursor = Some "c1"; NextCursor = Some "c1" }; { paged.Pages[2] with Cursor = Some "c1" } ] }
let planRefusal surface outcome code = match IntakeAdapter.plan intent (observation "rev-1" (Map.ofList [ (surface, outcome) ])) with Error findings -> findings |> List.exists (fun value -> value.Code = code && value.Surface = Some surface) | _ -> false
let altered = { plan with Effects = [ { plan.Effects[0] with Postcondition = { plan.Effects[0].Postcondition with Outcome = Observed "altered" } }; plan.Effects[1] ] }
let reordered = { plan with Effects = List.rev plan.Effects }
let wrongPost = [ { script[0] with After = { baseline with Revision = "rev-2" } }; script[1] ]
let rejected = [ script[0]; { script[1] with Accepted = false; Reason = Some "partial" } ]
let compensation = IntakeAdapter.applyControlled plan completed (IntakeApplyMode.Compensate receipt.AcceptedEffects) [ scripted plan.Effects[1] first; scripted plan.Effects[0] { baseline with Revision = "rev-4" } ]
let mutationFor = function
    | GitHubIntakeControl.MissingPage -> IntakeAdapter.inspect { baseline with Pages = [] } |> Result.isError
    | GitHubIntakeControl.RepeatedPage -> IntakeAdapter.inspect { paged with Pages = [ paged.Pages[0]; { paged.Pages[1] with Number = 1 }; paged.Pages[2] ] } |> Result.isError
    | GitHubIntakeControl.CursorCycle -> match IntakeAdapter.inspect cyclePaged with Error findings -> findings |> List.exists (fun value -> value.Code = "INTAKE-CURSOR-CYCLE") | _ -> false
    | GitHubIntakeControl.MissingField -> IntakeAdapter.inspect { baseline with Pages = [ page 1 None None true (allFacts |> List.tail) ] } |> Result.isError
    | GitHubIntakeControl.UnknownType -> planRefusal NativeIssueType (Unknown "future") "INTAKE-UNKNOWN-TYPE"
    | GitHubIntakeControl.DuplicateMembership -> planRefusal ProjectMembership (Duplicate "two") "INTAKE-DUPLICATE-MEMBERSHIP"
    | GitHubIntakeControl.HierarchyCycle -> planRefusal Hierarchy (Cycle "parent") "INTAKE-RELATION-CYCLE"
    | GitHubIntakeControl.DependencyCycle -> planRefusal Dependencies (Cycle "dependency") "INTAKE-RELATION-CYCLE"
    | GitHubIntakeControl.StaleRevision -> planRefusal InitialJournal (Stale("rev-0", "rev-1")) "INTAKE-STALE"
    | GitHubIntakeControl.AlteredPlan -> IntakeAdapter.applyControlled altered baseline Execute [] = Error InvalidSealedPlan
    | GitHubIntakeControl.ReorderedOperation -> IntakeAdapter.applyControlled reordered baseline Execute [] = Error InvalidSealedPlan
    | GitHubIntakeControl.PreconditionDrift -> IntakeAdapter.applyControlled plan { baseline with Revision = "drift" } Execute script = Error FullFenceChanged
    | GitHubIntakeControl.PostconditionMismatch -> match IntakeAdapter.applyControlled plan baseline Execute wrongPost with Error(EffectPostStateMismatch(1, [])) -> true | _ -> false
    | GitHubIntakeControl.PartialApply -> match IntakeAdapter.applyControlled plan baseline Execute rejected with Error(EffectRejected(2, "partial", accepted)) -> accepted.Length = 1 | _ -> false
    | GitHubIntakeControl.Replay -> IntakeAdapter.applyControlled plan completed (IntakeApplyMode.Resume receipt.AcceptedEffects) [] |> Result.isOk
    | GitHubIntakeControl.Compensation -> compensation |> Result.exists (fun value -> value.CompensatedOrdinals = [ 2; 1 ])
    | GitHubIntakeControl.Unauthorized -> planRefusal IssueIdentity (IntakeOutcome.Unauthorized "denied") "INTAKE-UNAUTHORIZED"
    | GitHubIntakeControl.Unsupported -> planRefusal TouchSet (IntakeOutcome.Unsupported "feature") "INTAKE-UNSUPPORTED"
    | GitHubIntakeControl.Indeterminate -> planRefusal Projections (IntakeOutcome.Indeterminate "timeout") "INTAKE-INDETERMINATE"
let baselineGreen = IntakeAdapter.inspect baseline |> Result.isOk && receipt.AcceptedEffects.Length = 2 && (match IntakeAdapter.plan intent baseline with Ok(IntakePlanned replayed) -> replayed = plan | _ -> false)
let generated = GitHubIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = mutationFor control; BaselineGreen = baselineGreen })

// Independent producer: a separately owned inventory and separately executed scenarios; it never calls mutationFor.
let independentMutation = function
    | GitHubIntakeControl.MissingPage -> match IntakeAdapter.inspect { baseline with Pages = [] } with Error findings -> findings |> List.exists (fun value -> value.Code = "INTAKE-PAGES") | _ -> false
    | GitHubIntakeControl.RepeatedPage -> match IntakeAdapter.inspect { paged with Pages = [ paged.Pages[0]; { paged.Pages[1] with Number = 1 }; paged.Pages[2] ] } with Error findings -> findings |> List.exists (fun value -> value.Code = "INTAKE-PAGE-CHAIN") | _ -> false
    | GitHubIntakeControl.CursorCycle -> match IntakeAdapter.inspect cyclePaged with Error findings -> findings |> List.exists (fun value -> value.Code = "INTAKE-CURSOR-CYCLE") | _ -> false
    | GitHubIntakeControl.MissingField -> match IntakeAdapter.inspect { baseline with Pages = [ page 1 None None true (allFacts |> List.filter (fun fact -> fact.Surface <> OrganizationFields)) ] } with Error findings -> findings |> List.exists (fun value -> value.Code = "INTAKE-MISSING-SURFACE") | _ -> false
    | GitHubIntakeControl.UnknownType -> planRefusal NativeIssueType (IntakeOutcome.Unknown "unregistered-native-type") "INTAKE-UNKNOWN-TYPE"
    | GitHubIntakeControl.DuplicateMembership -> planRefusal ProjectMembership (IntakeOutcome.Duplicate "duplicate-project-item") "INTAKE-DUPLICATE-MEMBERSHIP"
    | GitHubIntakeControl.HierarchyCycle -> planRefusal Hierarchy (IntakeOutcome.Cycle "self-parent") "INTAKE-RELATION-CYCLE"
    | GitHubIntakeControl.DependencyCycle -> planRefusal Dependencies (IntakeOutcome.Cycle "a-b-a") "INTAKE-RELATION-CYCLE"
    | GitHubIntakeControl.StaleRevision -> planRefusal Contract (IntakeOutcome.Stale("rev-prior", "rev-current")) "INTAKE-STALE"
    | GitHubIntakeControl.AlteredPlan -> let changed = { plan with Causation = "substituted" } in IntakeAdapter.applyControlled changed baseline Execute [] = Error InvalidSealedPlan
    | GitHubIntakeControl.ReorderedOperation -> let changed = { plan with Effects = [ plan.Effects[1]; plan.Effects[0] ] } in IntakeAdapter.applyControlled changed baseline Execute [] = Error InvalidSealedPlan
    | GitHubIntakeControl.PreconditionDrift -> IntakeAdapter.applyControlled plan { baseline with Revision = "rev-concurrent" } Execute script = Error FullFenceChanged
    | GitHubIntakeControl.PostconditionMismatch -> match IntakeAdapter.applyControlled plan baseline Execute [ { script[0] with After = { first with Identity = "other#1" } }; script[1] ] with Error(EffectPostStateMismatch(1, [])) -> true | _ -> false
    | GitHubIntakeControl.PartialApply -> match IntakeAdapter.applyControlled plan baseline Execute [ script[0]; { script[1] with Accepted = false; Reason = Some "independent-partial" } ] with Error(EffectRejected(2, "independent-partial", accepted)) -> accepted.Length = 1 && accepted.Head.Ordinal = 1 | _ -> false
    | GitHubIntakeControl.Replay -> match IntakeAdapter.applyControlled plan completed (IntakeApplyMode.RollForward receipt.AcceptedEffects) [] with Ok replayed -> replayed.AcceptedEffects = receipt.AcceptedEffects | _ -> false
    | GitHubIntakeControl.Compensation -> match compensation with Ok value -> value.CompensatedOrdinals = [ 2; 1 ] | _ -> false
    | GitHubIntakeControl.Unauthorized -> planRefusal OrganizationFields (IntakeOutcome.Unauthorized "scope-denied") "INTAKE-UNAUTHORIZED"
    | GitHubIntakeControl.Unsupported -> planRefusal InitialJournal (IntakeOutcome.Unsupported "host-capability") "INTAKE-UNSUPPORTED"
    | GitHubIntakeControl.Indeterminate -> planRefusal Dependencies (IntakeOutcome.Indeterminate "read-timeout") "INTAKE-INDETERMINATE"
let independentBaseline = IntakeAdapter.inspect paged |> Result.isOk && (match IntakeAdapter.plan intent baseline, IntakeAdapter.plan intent baseline with Ok(IntakePlanned left), Ok(IntakePlanned right) -> left = right | _ -> false)
let independent = independentControls |> List.map (fun id -> let control = GitHubIntakeQualification.requiredControls |> List.find (fun value -> GitHubIntakeQualification.controlId value = id) in { Control = control; MutationRed = independentMutation control; BaselineGreen = independentBaseline })

match GitHubIntakeQualification.validate generated independent with
| Ok () -> printfn "github-intake-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GIAQ-FAILED" $"{findings.Length} finding(s)"
