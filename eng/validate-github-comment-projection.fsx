#load "../src/FS.GG.Coordination.GitHub/CommentProjectionAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubCommentProjectionQualification.fs"

open System
open System.IO
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-comment-projection/contract.json")
if not (File.Exists fixturePath) then fail "GCPQ-FIXTURE-MISSING" fixturePath
let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
let names = json.EnumerateObject() |> Seq.map _.Name |> Seq.toList
if names <> [ "controls"; "generated"; "schema"; "synthetic" ] then fail "GCPQ-FIXTURE-SHAPE" (String.concat "," names)
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-comment-projection-fixture/1" then fail "GCPQ-FIXTURE-SCHEMA" fixturePath
if not (json.GetProperty("synthetic").GetBoolean()) then fail "GCPQ-FIXTURE-PROVENANCE" "Q3 fixture must disclose synthetic provenance"
let required = GitHubCommentProjectionQualification.requiredControls |> List.map GitHubCommentProjectionQualification.controlId
let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if fixtureControls <> required then fail "GCPQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

let instant minute = DateTimeOffset(2026, 8, 31, 12, minute, 0, TimeSpan.Zero)
let identity id node: CommentIdentity = { DatabaseId = id; NodeId = node }
let comment id node minute body: CommentObservation = { Identity = identity id node; CreatedAt = instant minute; UpdatedAt = instant minute; AuthorLogin = "synthetic-author"; Body = body }
let page number terminal cursor comments: CommentPage = { Number = number; TerminalPage = terminal; EndCursor = cursor; Comments = comments }
let complete revision pages = CommentsComplete(revision, pages)
let authority subject digest: JournalAuthority = { Subject = subject; JournalKind = "coordination"; JournalShard = "0042"; Generation = 11L; JournalCommit = String.replicate 40 "a"; AuthorityDigest = digest }
let request subject digest causation body: ProjectionRequest = { Authority = authority subject digest; Policy = { Version = "projection/v1" }; HumanBody = body; CausationIdentity = causation }
let render request = CommentProjectionAdapter.renderProjection request |> Result.defaultWith (fail "GCPQ-RENDER" << sprintf "%A")
let snapshot observation = CommentProjectionAdapter.readComments observation |> Result.defaultWith (fail "GCPQ-SNAPSHOT" << sprintf "%A")
let expected (value: CommentObservation): ExpectedProjection = { Identity = value.Identity; UpdatedAt = value.UpdatedAt; BodyDigest = CommentProjectionAdapter.sha256 value.Body }
let outcome control red green: GitHubCommentProjectionControlResult = { Control = control; MutationRed = red; BaselineGreen = green }

let generatedResults () =
    let data = json.GetProperty("generated")
    let revision = data.GetProperty("revision").GetString()
    let subject = data.GetProperty("subject").GetString()
    let digest = data.GetProperty("authorityDigest").GetString()
    let causation = data.GetProperty("causation").GetString()
    let id = data.GetProperty("commentDatabaseId").GetInt64()
    let node = data.GetProperty("commentNodeId").GetString()
    let req = request subject digest causation "Generated projection"
    let main = comment id node 1 (render req)
    let other = comment (id + 1L) (node + "_OTHER") 2 (Encoding.UTF8.GetBytes "unrelated")
    let baselineObservation = complete revision [ page 1 true None [ main; other ] ]
    let baseline = snapshot baselineObservation
    let projectionPlan = match CommentProjectionAdapter.planProjection (Some(expected main)) { req with HumanBody = "Regenerated" } baseline with Ok(ProjectionPlanned value) -> value | value -> fail "GCPQ-PLAN" (sprintf "%A" value)
    let malformed = { main with Body = Encoding.UTF8.GetBytes "<!-- fsgg:projection/v1 -->\n{bad}\nhuman\n" }
    [ outcome Pagination
          (CommentProjectionAdapter.readComments (complete revision [ page 2 true None [ main ] ]) = Error InvalidCommentPageChain)
          (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome DuplicateIdentity
          (CommentProjectionAdapter.readComments (complete revision [ page 1 true None [ main; { other with Identity = { other.Identity with DatabaseId = id } } ] ]) = Error(DuplicateCommentDatabaseId id))
          (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome ReorderedPage
          (CommentProjectionAdapter.readComments (complete revision [ page 1 true None [ other; main ] ]) = Error ReorderedCommentObservation)
          (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome EditedProjection
          (match CommentProjectionAdapter.evaluateTrust (Some { expected main with UpdatedAt = instant 0 }) req.Authority baseline with Error(ProjectionEdited _) -> true | _ -> false)
          (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome DeletedProjection
          (match CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority (snapshot (complete revision [ page 1 true None [] ])) with Error(ProjectionDeleted _) -> true | _ -> false)
          (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome TamperedMarker
          (match CommentProjectionAdapter.evaluateTrust (Some(expected main)) { req.Authority with Subject = subject + "-tampered" } baseline with Error(ProjectionTampered _) -> true | _ -> false)
          (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome MalformedJson
          (match CommentProjectionAdapter.parseProjection malformed with Error(MarkerMalformed _) -> true | _ -> false)
          (CommentProjectionAdapter.parseProjection main |> Result.isOk)
      outcome AuthorityDigestMismatch
          (match CommentProjectionAdapter.evaluateTrust (Some(expected main)) { req.Authority with AuthorityDigest = String.replicate 64 "c" } baseline with Error(ProjectionAuthorityDigestMismatch _) -> true | _ -> false)
          (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome IncompleteObservation
          (match CommentProjectionAdapter.readComments (CommentsIncomplete("truncated", Some "cursor")) with Error(CommentObservationIncomplete _) -> true | _ -> false)
          (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome StaleRevision
          (match CommentProjectionAdapter.checkPreState req.Authority projectionPlan (complete (revision + "-new") [ page 1 true None [ main; other ] ]) with Error(ProjectionReReadRequired _) -> true | _ -> false)
          (CommentProjectionAdapter.checkPreState req.Authority projectionPlan baselineObservation |> Result.isOk)
      outcome ConcurrentChange
          (match CommentProjectionAdapter.checkPreState req.Authority projectionPlan (complete revision [ page 1 true None [ main; { other with Body = Encoding.UTF8.GetBytes "changed" } ] ]) with Error ConcurrentProjectionChange -> true | _ -> false)
          (CommentProjectionAdapter.checkPreState req.Authority projectionPlan baselineObservation |> Result.isOk)
      outcome NoOpMutation
          (match CommentProjectionAdapter.planProjection (Some(expected main)) req baseline with Ok(ProjectionNoOp _) -> true | _ -> false)
          (match CommentProjectionAdapter.planProjection (Some(expected main)) { req with HumanBody = "changed" } baseline with Ok(ProjectionPlanned _) -> true | _ -> false) ]

// Independently authored controls use distinct facts and assertions; they do not call generatedResults.
let independentResults () =
    let revision = "independent-rev-9"
    let subject = "Independent/Repo#9"
    let digest = String.replicate 64 "d"
    let req = request subject digest "independent-cause" "Independent projection"
    let main = comment 900L "COMMENT_INDEPENDENT" 10 (render req)
    let other = comment 901L "COMMENT_OTHER_INDEPENDENT" 11 (Encoding.UTF8.GetBytes "other")
    let baselineObservation = complete revision [ page 1 true None [ main; other ] ]
    let baseline = snapshot baselineObservation
    let plan = match CommentProjectionAdapter.planProjection (Some(expected main)) { req with HumanBody = "replacement" } baseline with Ok(ProjectionPlanned value) -> value | value -> fail "GCPQ-INDEPENDENT-PLAN" (sprintf "%A" value)
    let malformed = { main with Body = Encoding.UTF8.GetBytes "<!-- fsgg:projection/v1 -->\n[]\nhuman\n" }
    [ outcome Pagination (CommentProjectionAdapter.readComments (complete revision [ page 1 false None [ main ] ]) = Error InvalidCommentPageChain) (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome DuplicateIdentity (match CommentProjectionAdapter.readComments (complete revision [ page 1 true None [ main; { other with Identity = identity 902L main.Identity.NodeId } ] ]) with Error(DuplicateCommentNodeId _) -> true | _ -> false) (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome ReorderedPage (CommentProjectionAdapter.readComments (complete revision [ page 1 true None [ other; main ] ]) = Error ReorderedCommentObservation) (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome EditedProjection (match CommentProjectionAdapter.evaluateTrust (Some { expected main with BodyDigest = String.replicate 64 "0" }) req.Authority baseline with Error(ProjectionEdited _) -> true | _ -> false) (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome DeletedProjection (match CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority (snapshot (complete revision [ page 1 true None [ other ] ])) with Error(ProjectionDeleted _) -> true | _ -> false) (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome TamperedMarker (match CommentProjectionAdapter.evaluateTrust (Some(expected main)) { req.Authority with JournalCommit = String.replicate 40 "f" } baseline with Error(ProjectionTampered _) -> true | _ -> false) (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome MalformedJson (match CommentProjectionAdapter.parseProjection malformed with Error(MarkerMalformed _) -> true | _ -> false) (CommentProjectionAdapter.parseProjection main |> Result.isOk)
      outcome AuthorityDigestMismatch (match CommentProjectionAdapter.evaluateTrust (Some(expected main)) { req.Authority with AuthorityDigest = String.replicate 64 "e" } baseline with Error(ProjectionAuthorityDigestMismatch _) -> true | _ -> false) (CommentProjectionAdapter.evaluateTrust (Some(expected main)) req.Authority baseline |> Result.isOk)
      outcome IncompleteObservation (match CommentProjectionAdapter.readComments (CommentsIncomplete("independent", None)) with Error(CommentObservationIncomplete _) -> true | _ -> false) (CommentProjectionAdapter.readComments baselineObservation |> Result.isOk)
      outcome StaleRevision (match CommentProjectionAdapter.checkPreState req.Authority plan (complete "independent-rev-10" [ page 1 true None [ main; other ] ]) with Error(ProjectionReReadRequired _) -> true | _ -> false) (CommentProjectionAdapter.checkPreState req.Authority plan baselineObservation |> Result.isOk)
      outcome ConcurrentChange (match CommentProjectionAdapter.checkPreState req.Authority plan (complete revision [ page 1 true None [ main ] ]) with Error ConcurrentProjectionChange -> true | _ -> false) (CommentProjectionAdapter.checkPreState req.Authority plan baselineObservation |> Result.isOk)
      outcome NoOpMutation (match CommentProjectionAdapter.planProjection (Some(expected main)) req baseline with Ok(ProjectionNoOp _) -> true | _ -> false) (match CommentProjectionAdapter.planProjection (Some(expected main)) { req with HumanBody = "new" } baseline with Ok(ProjectionPlanned _) -> true | _ -> false) ]

let generated = generatedResults ()
let independent = independentResults ()
match GitHubCommentProjectionQualification.validate generated independent with
| Ok () -> printfn "github-comment-projection-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings ->
    findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message)
    fail "GCPQ-FAILED" $"{findings.Length} finding(s)"
fixture.Dispose()
