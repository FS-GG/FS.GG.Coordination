#load "../src/FS.GG.Coordination.GitHub/IssueFields.fs"
#load "../src/FS.GG.Coordination.GitHub/NativeRelations.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubNativeRelationQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-native-relation/contract.json")
if not (File.Exists fixturePath) then fail "GNRQ-FIXTURE-MISSING" fixturePath
let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
let exactNames = json.EnumerateObject() |> Seq.map _.Name |> Seq.toList
if exactNames <> [ "controls"; "generated"; "schema"; "synthetic" ] then fail "GNRQ-FIXTURE-SHAPE" (String.concat "," exactNames)
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-native-relation-fixture/1" then fail "GNRQ-FIXTURE-SCHEMA" fixturePath
if not (json.GetProperty("synthetic").GetBoolean()) then fail "GNRQ-FIXTURE-PROVENANCE" "Q3 fixture must disclose synthetic provenance"
let required = GitHubNativeRelationQualification.requiredControls |> List.map GitHubNativeRelationQualification.controlId
let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if fixtureControls <> required then fail "GNRQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

let liveId value = LiveId.tryCreate value |> Result.defaultWith (fail "GNRQ-ID")
let edge kind source target = { Kind = kind; Source = liveId source; Target = liveId target }
let page number terminal edges = { Number = number; TerminalPage = terminal; Edges = edges }
let complete revision scope pages = RelationsComplete(revision, scope, pages)
let result red green control: GitHubNativeRelationControlResult = { Control = control; MutationRed = red; BaselineGreen = green }
let snapshot observation = NativeRelations.read observation |> Result.defaultWith (fail "GNRQ-SNAPSHOT" << sprintf "%A")
let planned expected causation intent before =
    match NativeRelations.plan expected causation intent before with
    | Ok(RelationPlanned value) -> value
    | value -> fail "GNRQ-PLAN" (sprintf "%A" value)

let controls revision causation main unrelated =
    let baselineObservation = complete revision Blocks [ page 1 true [ main; unrelated ] ]
    let baseline = snapshot baselineObservation
    let addable = edge Blocks "I_new_blocker" "I_new_blocked"
    let addPlan = planned revision causation (AddEdge addable) baseline
    [ result
          (NativeRelations.read (complete revision Blocks [ page 2 true [ main; unrelated ] ]) = Error InvalidRelationPageChain)
          (NativeRelations.read baselineObservation |> Result.isOk)
          GitHubNativeRelationControl.Pagination
      result
          (NativeRelations.read (complete revision Blocks [ page 1 true [ main; main ] ]) = Error(DuplicateRelationEdge main))
          (NativeRelations.read baselineObservation |> Result.isOk)
          GitHubNativeRelationControl.DuplicateEdge
      let reversed = { main with Source = main.Target; Target = main.Source }
      result
          (match NativeRelations.plan revision causation (RemoveEdge reversed) baseline with Ok(RelationNoOp _) -> true | _ -> false)
          (match NativeRelations.plan revision causation (RemoveEdge main) baseline with Ok(RelationPlanned _) -> true | _ -> false)
          GitHubNativeRelationControl.ReversedEndpoint
      result
          (match NativeRelations.read (complete revision ParentChild [ page 1 true [ main ] ]) with Error(RelationKindMismatch(ParentChild, Blocks)) -> true | _ -> false)
          (NativeRelations.read baselineObservation |> Result.isOk)
          GitHubNativeRelationControl.RelationKind
      result
          (NativeRelations.plan (revision + "-new") causation (RemoveEdge main) baseline = Error(RelationStaleExpectedRevision revision))
          (NativeRelations.plan revision causation (RemoveEdge main) baseline |> Result.isOk)
          GitHubNativeRelationControl.StaleRevision
      result
          (match NativeRelations.read (RelationsIncomplete("page missing", Some "cursor")) with Error(RelationObservationRefused(ObservationIncomplete _)) -> true | _ -> false)
          (NativeRelations.read baselineObservation |> Result.isOk)
          GitHubNativeRelationControl.IncompleteObservation
      result
          (match NativeRelations.checkPreState addPlan (complete revision Blocks [ page 1 true [ main ] ]) with Error(ConcurrentPreStateChange _) -> true | _ -> false)
          (NativeRelations.checkPreState addPlan baselineObservation |> Result.isOk)
          GitHubNativeRelationControl.ConcurrentChange
      result
          (match NativeRelations.plan revision causation (AddEdge main) baseline with Ok(RelationNoOp _) -> true | _ -> false)
          (match NativeRelations.plan revision causation (RemoveEdge main) baseline with Ok(RelationPlanned _) -> true | _ -> false)
          GitHubNativeRelationControl.NoOpMutation ]

let generatedResults () =
    let data = json.GetProperty("generated")
    controls
        (data.GetProperty("revision").GetString())
        (data.GetProperty("causation").GetString())
        (edge Blocks (data.GetProperty("source").GetString()) (data.GetProperty("target").GetString()))
        (edge Blocks (data.GetProperty("unrelatedSource").GetString()) (data.GetProperty("unrelatedTarget").GetString()))

// Separately authored identities and revisions prevent the fixture producer from proving itself.
let independentResults () =
    controls "independent-rev-17" "independent-cause" (edge Blocks "I_independent_blocker" "I_independent_blocked") (edge Blocks "I_other_blocker" "I_other_blocked")

let generated = generatedResults ()
let independent = independentResults ()
match GitHubNativeRelationQualification.validate generated independent with
| Ok () -> printfn "github-native-relation-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GNRQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
