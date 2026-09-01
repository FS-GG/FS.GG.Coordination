#load "../src/FS.GG.Coordination.GitHub/IntakeAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubIntakeQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-3/corpus.json")
if not (File.Exists corpusPath) then fail "GIAQ-CORPUS-MISSING" corpusPath
let corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
let json = corpus.RootElement
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-intake-corpus/1" then fail "GIAQ-CORPUS-SCHEMA" corpusPath
if json.GetProperty("registeredContractSha256").GetString() <> "01e29e3b7f0f049364a64b85c3b75f1e47f37f256778052e66d3894b23632ae6" then fail "GIAQ-CONTRACT" "registered contract mismatch"
if json.GetProperty("acceptedPredecessorReceiptSha256").GetString() <> "a8474fbd59a6643317b1e2e8fc97517a0c8bed6f754865c19d5780e0da1da5d8" then fail "GIAQ-PREDECESSOR" "accepted predecessor mismatch"
let required = GitHubIntakeQualification.requiredControls |> List.map GitHubIntakeQualification.controlId
let observedControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if observedControls <> required then fail "GIAQ-CORPUS-INVENTORY" (String.concat "," observedControls)

let surfaces = [ Issue; IssueFields; ProjectMembership; Hierarchy; Dependencies; ProtocolState ]
let facts changes = surfaces |> List.map (fun surface -> { Surface = surface; Outcome = changes |> Map.tryFind surface |> Option.defaultValue (Observed("before-" + string surface)) })
let observation revision changes = { Identity = "FS-GG/FS.GG.Coordination#196"; Revision = revision; Pages = [ { Number = 1; Facts = facts changes; TerminalPage = true } ] }
let baseline = observation "rev-1" Map.empty
let intents = [ InitializeProtocolIssue "issue-ready"; InitializeRequiredIssueFields "fields-ready" ]
let plan = match IntakeAdapter.plan "gate-cause" intents baseline with Ok(IntakePlanned value) -> value | value -> fail "GIAQ-PLAN" (sprintf "%A" value)
let first = observation "rev-2" (Map.ofList [ (Issue, Observed "issue-ready") ])
let completed = observation "rev-3" (Map.ofList [ (Issue, Observed "issue-ready"); (IssueFields, Observed "fields-ready") ])
let script = [ { Ordinal = 1; Accepted = true; Reason = None; After = first }; { Ordinal = 2; Accepted = true; Reason = None; After = completed } ]
let receipt = IntakeAdapter.applyControlled plan baseline Execute script |> Result.defaultWith (fail "GIAQ-APPLY" << sprintf "%A")
let durableFirst = receipt.AcceptedEffects.Head
let resumed = IntakeAdapter.applyControlled plan first (IntakeApplyMode.Resume [ durableFirst ]) [ script.Tail.Head ]
let compensation = IntakeAdapter.applyControlled plan completed (IntakeApplyMode.Compensate receipt.AcceptedEffects) [ { Ordinal = 2; Accepted = true; Reason = None; After = first }; { Ordinal = 1; Accepted = true; Reason = None; After = { baseline with Revision = "rev-4" } } ]
let duplicate = { baseline with Pages = [ { baseline.Pages.Head with Facts = baseline.Pages.Head.Facts.Head :: baseline.Pages.Head.Facts } ] }
let incomplete = { baseline with Pages = [ { baseline.Pages.Head with TerminalPage = false } ] }
let drift = { baseline with Revision = "rev-drift" }
let wrongOrder = [ { script.Head with Ordinal = 2 }; script.Tail.Head ]
let wrongPost = [ { script.Head with After = observation "rev-2" Map.empty }; script.Tail.Head ]
let canonical = match IntakeAdapter.plan "gate-cause" intents baseline with Ok(IntakePlanned replay) -> replay = plan | _ -> false
let noOp = match IntakeAdapter.plan "gate-cause" intents completed with Ok(IntakeNoOp _) -> true | _ -> false
let typed = IntakeAdapter.observe (observation "typed" (Map.ofList [ Hierarchy, Redacted; Dependencies, Unauthorized "denied" ])) |> Result.isOk
let baselineGreen = IntakeAdapter.observe baseline |> Result.isOk && receipt.AcceptedEffects.Length = 2
let mutationFor = function
    | CompleteObservation -> IntakeAdapter.observe { baseline with Pages = [] } |> Result.isError
    | DuplicateSurface -> IntakeAdapter.observe duplicate |> Result.isError
    | IncompletePagination -> IntakeAdapter.observe incomplete |> Result.isError
    | TypedOutcome -> typed
    | CanonicalPlan -> canonical && noOp
    | FullFence -> IntakeAdapter.applyControlled plan drift Execute script = Error FullFenceChanged
    | EffectOrder -> IntakeAdapter.applyControlled plan baseline Execute wrongOrder = Error(DurableResultMismatch 1)
    | PostState -> match IntakeAdapter.applyControlled plan baseline Execute wrongPost with Error(EffectPostStateMismatch 1) -> true | _ -> false
    | Replay -> canonical
    | GitHubIntakeControl.Resume -> resumed |> Result.exists (fun value -> value.AcceptedEffects.Length = 2)
    | GitHubIntakeControl.Compensation -> compensation |> Result.exists (fun value -> value.CompensatedOrdinals = [ 2; 1 ])
    | IntentBoundary -> match IntakeAdapter.plan "gate-cause" [ InitializeProtocolIssue "one"; InitializeProtocolIssue "two" ] baseline with Error findings -> findings |> List.exists (fun finding -> finding.Code = "INTAKE-DUPLICATE-INTENT") | _ -> false
let generated = GitHubIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = mutationFor control; BaselineGreen = baselineGreen })

// This producer is intentionally fixture-derived and does not call mutationFor.
let independent =
    observedControls
    |> List.map (fun id ->
        let control = GitHubIntakeQualification.requiredControls |> List.find (fun value -> GitHubIntakeQualification.controlId value = id)
        { Control = control; MutationRed = true; BaselineGreen = true })

match GitHubIntakeQualification.validate generated independent with
| Ok () -> printfn "github-intake-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GIAQ-FAILED" $"{findings.Length} finding(s)"
