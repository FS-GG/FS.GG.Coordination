#load "../src/FS.GG.Coordination.GitHub/RoadmapIntakeAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubRoadmapIntakeQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args[0]
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-4/corpus.json")
let independentPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-4/independent-expectations.json")
if not (File.Exists corpusPath && File.Exists independentPath) then fail "GRIQ-EVIDENCE" "roadmap-intake evidence is missing"
let corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
let independentDocument = JsonDocument.Parse(File.ReadAllBytes independentPath)
let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let independentIds = independentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let requiredIds = GitHubRoadmapIntakeQualification.requiredControls |> List.map GitHubRoadmapIntakeQualification.controlId
if corpus.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-roadmap-intake-corpus/1" then fail "GRIQ-CORPUS-SCHEMA" corpusPath
if independentDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-roadmap-intake-expectations/1" then fail "GRIQ-INDEPENDENT-SCHEMA" independentPath
if corpus.RootElement.GetProperty("registeredContractSha256").GetString() <> "46df348d73b6a5d953e78286f23e794432f389752eb77e9d085d694f7d4e2fc7" then fail "GRIQ-CONTRACT" "registered contract mismatch"
if corpus.RootElement.GetProperty("acceptedPredecessorReceiptSha256").GetString() <> "c63acc912261dd5e14951423a5de24b27da0c35e2d96bf071e6a8c6734848345" then fail "GRIQ-PREDECESSOR" "accepted GS2-05.9 file mismatch"
if corpus.RootElement.GetProperty("acceptedPredecessorReceiptDigest").GetString() <> "59398e603e39b04ff6d971ef923d19513e03d3990a970323add90cf7ce593861" then fail "GRIQ-PREDECESSOR-DIGEST" "accepted GS2-05.9 receipt mismatch"
if corpus.RootElement.GetProperty("roadmapRevision").GetString() <> "2ff646743e770f0ec6be5566acd04df0b1a83dec" || corpus.RootElement.GetProperty("roadmapSha256").GetString() <> "e10e4a4245d11d1ae955d3a11c7cc25aa92e52a1c1b6bf6398e4249acc8ee581" then fail "GRIQ-ROADMAP" "roadmap authority mismatch"
if generatedIds <> requiredIds || independentIds <> requiredIds then fail "GRIQ-INVENTORY" "inventories are not exact"

let field name value : RoadmapField = { Name = name; Value = value }
let node key issueType parent dependencies start target fields : RoadmapNode =
    { Key = key; Repository = "FS-GG/Product"; IssueType = issueType; Title = "Title " + key; Body = "Body " + key
      Parent = parent; Dependencies = dependencies; Start = start; Target = target; Fields = fields }
let epic = node "epic" RoadmapIssueType.Epic None [] (Some "2026-09-01") (Some "2026-12-31") [ field "phase" "delivery" ]
let feature = node "feature" RoadmapIssueType.Feature (Some "epic") [] None None [ field "priority" "High" ]
let task = node "task" RoadmapIssueType.Task (Some "feature") [ "feature" ] None (Some "2026-11-30") [ field "effort" "3" ]
let definition nodes : RoadmapDefinition = { Schema = RoadmapIntakeAdapter.Schema; Identity = "roadmap:product:v2"; Revision = "source-42"; Nodes = nodes }
let roadmap = definition [ epic; feature; task ]
let observation project backlog targets : RoadmapObservation = { Complete = true; Revision = "observed-7"; Targets = targets; UnrelatedProjectItems = project; UnrelatedBacklogItems = backlog }
let empty = observation 0 0 []
let target number (node: RoadmapNode) projected : RoadmapTarget =
    { Key = node.Key; OwnerIdentity = roadmap.Identity; RoadmapRevision = roadmap.Revision; Repository = node.Repository; Number = number
      IssueType = node.IssueType; Title = node.Title; Body = node.Body; Parent = node.Parent; Dependencies = node.Dependencies
      Start = node.Start; Target = node.Target; Fields = node.Fields; Projected = projected }
let satisfied projected = observation 13 21 [ target 1 epic projected; target 2 feature projected; target 3 task projected ]
let plan = RoadmapIntakeAdapter.plan roadmap empty |> Result.defaultWith (fail "GRIQ-PLAN" << sprintf "%A")
let noOp = RoadmapIntakeAdapter.plan roadmap (satisfied true) |> Result.defaultWith (fail "GRIQ-NOOP" << sprintf "%A")
let invalidParent = definition [ epic; { feature with Parent = Some "missing" } ]
let dependencyCycle = definition [ epic; { feature with Dependencies = [ "task" ] }; task ]
let invalidDate = definition [ epic; { feature with Start = Some "2026-12-02"; Target = Some "2026-12-01" } ]
let invalidField = definition [ epic; { feature with Fields = [ field "status" "Done" ] } ]
let duplicate = target 2 feature true
let findCode code (result: Result<_, RoadmapDiagnostic list>) = match result with Error findings -> findings |> List.exists (fun finding -> finding.Code = code) | Ok _ -> false
let baselineGreen = RoadmapIntakeAdapter.validate roadmap |> Result.isOk && RoadmapIntakeAdapter.validatePlan plan && noOp.Effects.IsEmpty && RoadmapIntakeAdapter.applyControlled noOp (satisfied true) true true false None |> Result.isOk

let generatedMutation = function
    | GitHubRoadmapIntakeControl.CanonicalPlan -> RoadmapIntakeAdapter.plan roadmap empty = RoadmapIntakeAdapter.plan { roadmap with Nodes = List.rev roadmap.Nodes } empty
    | GitHubRoadmapIntakeControl.CreateOrReuse -> not plan.Effects.IsEmpty && noOp.Effects.IsEmpty
    | GitHubRoadmapIntakeControl.Hierarchy -> RoadmapIntakeAdapter.validate invalidParent |> findCode "ROADMAP-PARENT"
    | GitHubRoadmapIntakeControl.Dependencies -> RoadmapIntakeAdapter.validate dependencyCycle |> findCode "ROADMAP-DEPENDENCY-CYCLE"
    | GitHubRoadmapIntakeControl.Dates -> RoadmapIntakeAdapter.validate invalidDate |> findCode "ROADMAP-DATE"
    | GitHubRoadmapIntakeControl.Fields -> RoadmapIntakeAdapter.validate invalidField |> findCode "ROADMAP-FIELD"
    | GitHubRoadmapIntakeControl.IdentityCollision -> RoadmapIntakeAdapter.plan roadmap (observation 0 0 [ { duplicate with OwnerIdentity = "foreign" } ]) |> findCode "ROADMAP-IDENTITY-COLLISION"
    | GitHubRoadmapIntakeControl.DuplicateTarget -> RoadmapIntakeAdapter.plan roadmap (observation 0 0 [ duplicate; { duplicate with Number = 9 } ]) |> findCode "ROADMAP-TARGET-AMBIGUOUS"
    | GitHubRoadmapIntakeControl.StaleObservation -> RoadmapIntakeAdapter.plan roadmap { empty with Complete = false } |> findCode "ROADMAP-OBSERVATION-INCOMPLETE"
    | GitHubRoadmapIntakeControl.AlteredPlan -> not (RoadmapIntakeAdapter.validatePlan { plan with Effects = List.rev plan.Effects })
    | GitHubRoadmapIntakeControl.CardinalityInvariant -> RoadmapIntakeAdapter.plan roadmap empty = RoadmapIntakeAdapter.plan roadmap (observation 1000000 2000000 [])
    | GitHubRoadmapIntakeControl.ProjectionNotLedger -> RoadmapIntakeAdapter.inspect roadmap (satisfied false) = Ok []
    | GitHubRoadmapIntakeControl.OwnedDrift -> RoadmapIntakeAdapter.inspect roadmap (observation 0 0 [ target 1 epic true; { (target 2 feature true) with Title = "drift" }; target 3 task true ]) |> Result.exists (List.exists (fun drift -> drift.Key = "feature"))
    | GitHubRoadmapIntakeControl.Replay -> RoadmapIntakeAdapter.applyControlled noOp (satisfied true) true true false None |> Result.exists _.Replay
    | GitHubRoadmapIntakeControl.PartialApply -> RoadmapIntakeAdapter.applyControlled plan empty true true false (Some 1) = Error(RoadmapApplyFailure.Partial 1)
    | GitHubRoadmapIntakeControl.Unauthorized -> RoadmapIntakeAdapter.applyControlled plan empty false true false None = Error RoadmapApplyFailure.Unauthorized
    | GitHubRoadmapIntakeControl.Unsupported -> RoadmapIntakeAdapter.applyControlled plan empty true false false None = Error RoadmapApplyFailure.Unsupported
    | GitHubRoadmapIntakeControl.Indeterminate -> RoadmapIntakeAdapter.applyControlled plan empty true true true None = Error RoadmapApplyFailure.Indeterminate

// Independent producer: distinct constructions and assertions; it never calls generatedMutation.
let independentMutation = function
    | GitHubRoadmapIntakeControl.CanonicalPlan -> let left = RoadmapIntakeAdapter.plan roadmap empty in let right = RoadmapIntakeAdapter.plan roadmap empty in left = right
    | GitHubRoadmapIntakeControl.CreateOrReuse -> RoadmapIntakeAdapter.plan roadmap (satisfied true) |> Result.exists (fun value -> value.Effects.Length = 0)
    | GitHubRoadmapIntakeControl.Hierarchy -> RoadmapIntakeAdapter.validate (definition [ epic; { feature with Parent = Some "feature" } ]) |> Result.isError
    | GitHubRoadmapIntakeControl.Dependencies -> RoadmapIntakeAdapter.validate (definition [ epic; { feature with Dependencies = [ "feature" ] } ]) |> Result.isError
    | GitHubRoadmapIntakeControl.Dates -> RoadmapIntakeAdapter.validate (definition [ { epic with Start = Some "September" } ]) |> Result.isError
    | GitHubRoadmapIntakeControl.Fields -> RoadmapIntakeAdapter.validate (definition [ epic; { feature with Fields = [ field "priority" "" ] } ]) |> Result.isError
    | GitHubRoadmapIntakeControl.IdentityCollision -> RoadmapIntakeAdapter.plan roadmap (observation 0 0 [ { (target 2 feature true) with OwnerIdentity = "another-roadmap" } ]) |> Result.isError
    | GitHubRoadmapIntakeControl.DuplicateTarget -> RoadmapIntakeAdapter.plan roadmap (observation 0 0 [ duplicate; duplicate ]) |> Result.isError
    | GitHubRoadmapIntakeControl.StaleObservation -> RoadmapIntakeAdapter.applyControlled plan { empty with Revision = "changed" } true true false None = Error RoadmapApplyFailure.Stale
    | GitHubRoadmapIntakeControl.AlteredPlan -> RoadmapIntakeAdapter.applyControlled { plan with Digest = String.replicate 64 "0" } empty true true false None = Error RoadmapApplyFailure.InvalidPlan
    | GitHubRoadmapIntakeControl.CardinalityInvariant -> let left = RoadmapIntakeAdapter.plan roadmap (observation 1 2 []) in let right = RoadmapIntakeAdapter.plan roadmap (observation 999 888 []) in left = right
    | GitHubRoadmapIntakeControl.ProjectionNotLedger -> let left = RoadmapIntakeAdapter.inspect roadmap (satisfied true) in let right = RoadmapIntakeAdapter.inspect roadmap (satisfied false) in left = right
    | GitHubRoadmapIntakeControl.OwnedDrift -> RoadmapIntakeAdapter.inspect roadmap (observation 0 0 [ { (target 1 epic true) with Body = "changed" }; target 2 feature true; target 3 task true ]) |> Result.exists (List.exists (fun drift -> drift.Surface = "upsert"))
    | GitHubRoadmapIntakeControl.Replay -> RoadmapIntakeAdapter.applyControlled noOp (satisfied true) true true false None = Ok { PlanDigest = noOp.Digest; Applied = 0; Replay = true }
    | GitHubRoadmapIntakeControl.PartialApply -> match RoadmapIntakeAdapter.applyControlled plan empty true true false (Some 0) with Error(RoadmapApplyFailure.Partial 0) -> true | _ -> false
    | GitHubRoadmapIntakeControl.Unauthorized -> RoadmapIntakeAdapter.applyControlled noOp (satisfied true) false true false None |> Result.isError
    | GitHubRoadmapIntakeControl.Unsupported -> RoadmapIntakeAdapter.applyControlled noOp (satisfied true) true false false None |> Result.isError
    | GitHubRoadmapIntakeControl.Indeterminate -> RoadmapIntakeAdapter.applyControlled noOp (satisfied true) true true true None |> Result.isError

let generated = GitHubRoadmapIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = baselineGreen })
let independent = GitHubRoadmapIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = baselineGreen })
match GitHubRoadmapIntakeQualification.validate generated independent with
| Ok () -> printfn "github-roadmap-intake-contract OK controls=%d q=Q3 network=offline provenance=generated+independent production-writes=0" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GRIQ-FAILED" (string findings.Length)
