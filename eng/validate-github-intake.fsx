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
| Ok () -> ()
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GIAQ-FAILED" $"{findings.Length} finding(s)"

let stagedCorpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-9/corpus.json")
let stagedIndependentPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-9/independent-expectations.json")
if not (File.Exists stagedCorpusPath) then fail "GIAQ-STAGED-CORPUS-MISSING" stagedCorpusPath
if not (File.Exists stagedIndependentPath) then fail "GIAQ-STAGED-INDEPENDENT-MISSING" stagedIndependentPath
let stagedCorpus = JsonDocument.Parse(File.ReadAllBytes stagedCorpusPath)
let stagedIndependentDocument = JsonDocument.Parse(File.ReadAllBytes stagedIndependentPath)
let stagedJson = stagedCorpus.RootElement
if stagedJson.GetProperty("schema").GetString() <> "fsgg.coordination.github-staged-intake-corpus/1" then fail "GIAQ-STAGED-CORPUS-SCHEMA" stagedCorpusPath
if stagedIndependentDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-staged-intake-expectations/1" then fail "GIAQ-STAGED-INDEPENDENT-SCHEMA" stagedIndependentPath
if stagedJson.GetProperty("registeredContractSha256").GetString() <> "594702c526db8858ba5fd82ed4d4f18c0300ad0c8b33c7cb0608356d748db6c8" then fail "GIAQ-STAGED-CONTRACT" "registered contract mismatch"
if stagedJson.GetProperty("acceptedPredecessorReceiptSha256").GetString() <> "f5833e55b7a90a1986693f81ef26e9cc5e82a0991d563ba4accbb5ea5048dd48" then fail "GIAQ-STAGED-PREDECESSOR" "accepted predecessor mismatch"
if stagedJson.GetProperty("roadmapRevision").GetString() <> "2ff646743e770f0ec6be5566acd04df0b1a83dec" || stagedJson.GetProperty("roadmapSha256").GetString() <> "e10e4a4245d11d1ae955d3a11c7cc25aa92e52a1c1b6bf6398e4249acc8ee581" then fail "GIAQ-STAGED-ROADMAP" "roadmap authority mismatch"
let stagedControls = stagedJson.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let stagedIndependentControls = stagedIndependentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if stagedControls <> stagedIndependentControls || stagedControls.Length <> 18 || stagedControls |> List.distinct |> List.length <> 18 then fail "GIAQ-STAGED-INVENTORY" (String.concat "," stagedControls)

let stagedRequest rootCause verification touchSet =
    { Identity = baseline.Identity
      IdentityMode = CaptureIdentityMode.CreateOrReuse
      Repository = "FS-GG/FS.GG.Coordination"
      Causation = "GS2-05.9"
      RootCause = rootCause
      Verification = verification
      TouchSet = touchSet }
let captureObservation projectItems backlogItems reads =
    { Intake = baseline
      AuthorityReads = reads
      UnrelatedProjectItems = projectItems
      UnrelatedBacklogItems = backlogItems }
let captureRequest = stagedRequest (DiscoveryDetail.Known "roadmap admission gap") (DiscoveryDetail.Known "Q3") (Some [ "eng/github-substrate-v2-units.json" ])
let captureBaseline = captureObservation 0 0 IntakeAdapter.requiredCaptureReads
let captureLarge = captureObservation 1000000 2000000 IntakeAdapter.requiredCaptureReads
let capturePlan observation = IntakeAdapter.planCapture captureRequest observation
let promotionFacts =
    IntakeAdapter.requiredReadyPromotionSurfaces
    |> List.map (fun surface ->
        { Surface = surface
          Value = if surface = ReadyPromotionSurface.TouchSet then "eng/github-substrate-v2-units.json" else "known-" + string surface })
let missingPromotion surface =
    match IntakeAdapter.prepareReadyPromotion baseline.Identity "FS-GG/FS.GG.Coordination" "GS2-05.9" (promotionFacts |> List.filter (fun fact -> fact.Surface <> surface)) with
    | Error findings -> findings |> List.exists (fun finding -> finding.Code.StartsWith("INTAKE-PROMOTION-MISSING-", StringComparison.Ordinal))
    | Ok _ -> false
let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/IntakeAdapter.fs"))
let closedVocabulary (text: string) =
    let lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n') |> Set.ofArray
    lines.Contains "type CaptureAuthorityRead = IssueIdentity | NativeTypeAndFields | ProjectMembership | Relations | RepositoryScope | ProtocolState"
    && lines.Contains "type ReadyPromotionSurface = RootCause | TouchSet | VerificationContract | Dependencies | RouteDecision | NativeIssueType | OrganizationFields | RepositoryScope | WorkClassification"
let generatedStaged control =
    match control with
    | "unknown-root-cause" -> IntakeAdapter.validateCapture (stagedRequest (DiscoveryDetail.ExplicitlyUnknown "not yet diagnosed") (DiscoveryDetail.Known "Q3") None) |> Result.isOk
    | "deferred-verification" -> IntakeAdapter.validateCapture (stagedRequest (DiscoveryDetail.Known "cause") (DiscoveryDetail.Deferred "route first") None) |> Result.isOk
    | "unspecified-touch-set" -> IntakeAdapter.validateCapture (stagedRequest (DiscoveryDetail.Known "cause") (DiscoveryDetail.Known "Q3") None) |> Result.isOk
    | "cardinality-invariance" -> capturePlan captureBaseline = capturePlan captureLarge
    | "authority-read-budget" -> IntakeAdapter.planCapture captureRequest (captureObservation 0 0 (CaptureAuthorityRead.ProtocolState :: IntakeAdapter.requiredCaptureReads)) |> Result.isError
    | "forbidden-global-operation" -> closedVocabulary source && not (closedVocabulary (source.Replace("| ProtocolState", "| ProtocolState | OrganizationWideReconcile", StringComparison.Ordinal)))
    | "missing-promotion-root-cause" -> missingPromotion ReadyPromotionSurface.RootCause
    | "missing-promotion-touch-set" -> missingPromotion ReadyPromotionSurface.TouchSet
    | "missing-promotion-verification-contract" -> missingPromotion ReadyPromotionSurface.VerificationContract
    | "missing-promotion-dependencies" -> missingPromotion ReadyPromotionSurface.Dependencies
    | "missing-promotion-route-decision" -> missingPromotion ReadyPromotionSurface.RouteDecision
    | "missing-promotion-native-issue-type" -> missingPromotion ReadyPromotionSurface.NativeIssueType
    | "missing-promotion-organization-fields" -> missingPromotion ReadyPromotionSurface.OrganizationFields
    | "missing-promotion-repository-scope" -> missingPromotion ReadyPromotionSurface.RepositoryScope
    | "missing-promotion-work-classification" -> missingPromotion ReadyPromotionSurface.WorkClassification
    | "duplicate-promotion" -> IntakeAdapter.prepareReadyPromotion baseline.Identity "FS-GG/FS.GG.Coordination" "GS2-05.9" (promotionFacts.Head :: promotionFacts) |> Result.isError
    | "invalid-promotion" -> IntakeAdapter.prepareReadyPromotion baseline.Identity "FS-GG/FS.GG.Coordination" "GS2-05.9" ({ promotionFacts.Head with Value = " " } :: promotionFacts.Tail) |> Result.isError
    | "v1-application" -> match capturePlan captureBaseline with Ok value -> value.ContractSchema = "fsgg.coord.intake/v1" && value.Budget.AuthorityReads = 6 && value.Budget.Mutations <= 6 | Error _ -> false
    | _ -> false
let independentStaged control =
    match control with
    | "unknown-root-cause" -> match IntakeAdapter.validateCapture (stagedRequest (DiscoveryDetail.ExplicitlyUnknown "explicit gap") (DiscoveryDetail.Known "test") None) with Ok value -> value.Initializations |> List.exists (function InitializeContract value -> value.Contains("root-cause=unknown:", StringComparison.Ordinal) | _ -> false) | Error _ -> false
    | "deferred-verification" -> match IntakeAdapter.validateCapture (stagedRequest (DiscoveryDetail.Known "cause") (DiscoveryDetail.Deferred "later evidence") None) with Ok value -> value.Initializations |> List.exists (function InitializeContract value -> value.Contains("verification=deferred:", StringComparison.Ordinal) | _ -> false) | Error _ -> false
    | "unspecified-touch-set" -> match IntakeAdapter.validateCapture (stagedRequest (DiscoveryDetail.Known "cause") (DiscoveryDetail.Known "test") None) with Ok value -> value.Initializations |> List.forall (function InitializeTouchSet _ -> false | _ -> true) | Error _ -> false
    | "cardinality-invariance" -> match capturePlan captureBaseline, capturePlan captureLarge with Ok left, Ok right -> left.Intent.Digest = right.Intent.Digest && left.Decision = right.Decision && left.Budget = right.Budget | _ -> false
    | "authority-read-budget" -> IntakeAdapter.planCapture captureRequest (captureObservation 0 0 (IntakeAdapter.requiredCaptureReads |> List.tail)) |> Result.isError
    | "forbidden-global-operation" -> closedVocabulary source && not (closedVocabulary (source.Replace("| WorkClassification", "| WorkClassification | Claim | PullRequest", StringComparison.Ordinal)))
    | value when value.StartsWith("missing-promotion-", StringComparison.Ordinal) ->
        let surfaceName = value.Substring("missing-promotion-".Length)
        let surface = IntakeAdapter.requiredReadyPromotionSurfaces |> List.find (fun item -> (string item).Replace("_", "-").ToLowerInvariant() = surfaceName.Replace("-", "").ToLowerInvariant() || match item, surfaceName with | ReadyPromotionSurface.RootCause, "root-cause" | ReadyPromotionSurface.TouchSet, "touch-set" | ReadyPromotionSurface.VerificationContract, "verification-contract" | ReadyPromotionSurface.Dependencies, "dependencies" | ReadyPromotionSurface.RouteDecision, "route-decision" | ReadyPromotionSurface.NativeIssueType, "native-issue-type" | ReadyPromotionSurface.OrganizationFields, "organization-fields" | ReadyPromotionSurface.RepositoryScope, "repository-scope" | ReadyPromotionSurface.WorkClassification, "work-classification" -> true | _ -> false)
        missingPromotion surface
    | "duplicate-promotion" -> match IntakeAdapter.prepareReadyPromotion baseline.Identity "FS-GG/FS.GG.Coordination" "GS2-05.9" (promotionFacts @ [ promotionFacts.Head ]) with Error findings -> findings |> List.exists (fun finding -> finding.Code.StartsWith("INTAKE-PROMOTION-DUPLICATE-", StringComparison.Ordinal)) | Ok _ -> false
    | "invalid-promotion" -> match IntakeAdapter.prepareReadyPromotion baseline.Identity "FS-GG/FS.GG.Coordination" "GS2-05.9" (promotionFacts |> List.map (fun fact -> if fact.Surface = ReadyPromotionSurface.RouteDecision then { fact with Value = "" } else fact)) with Error findings -> findings |> List.exists (fun finding -> finding.Code.Contains("VALUE", StringComparison.Ordinal)) | Ok _ -> false
    | "v1-application" -> match capturePlan captureBaseline with Ok value -> value.Intent.Initializations |> List.exists (function InitializeContract contract -> contract.StartsWith("schema=fsgg.coord.intake/v1;", StringComparison.Ordinal) | _ -> false) | Error _ -> false
    | _ -> false
let stagedGeneratedResults = stagedControls |> List.map (fun control -> control, generatedStaged control)
let stagedIndependentResults = stagedIndependentControls |> List.map (fun control -> control, independentStaged control)
for control, passed in stagedGeneratedResults @ stagedIndependentResults do if not passed then fail "GIAQ-STAGED-CONTROL" control
printfn "github-intake-contract OK controls=%d q=Q3 network=offline provenance=synthetic staged-controls=%d staged-provenance=generated+independent" generated.Length stagedControls.Length
