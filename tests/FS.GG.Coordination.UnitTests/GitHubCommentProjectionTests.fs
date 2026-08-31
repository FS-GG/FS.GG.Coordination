module FS.GG.Coordination.GitHubCommentProjectionTests

open System
open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private instant minute = DateTimeOffset(2026, 8, 31, 10, minute, 0, TimeSpan.Zero)
let private identity id node: CommentIdentity = { DatabaseId = id; NodeId = node }
let private comment id node minute body: CommentObservation =
    { Identity = identity id node; CreatedAt = instant minute; UpdatedAt = instant minute; AuthorLogin = "fixture-author"; Body = body }
let private page number terminal cursor comments: CommentPage = { Number = number; TerminalPage = terminal; EndCursor = cursor; Comments = comments }
let private complete revision pages = CommentsComplete(revision, pages)
let private authority digest: JournalAuthority =
    { Subject = "FS-GG/Repo#42"; JournalKind = "coordination"; JournalShard = "0042"; Generation = 7L; JournalCommit = String.replicate 40 "a"; AuthorityDigest = digest }
let private request digest human: ProjectionRequest =
    { Authority = authority digest; Policy = { Version = "projection/v1" }; HumanBody = human; CausationIdentity = "issue-149" }
let private render digest human = CommentProjectionAdapter.renderProjection (request digest human) |> Result.defaultWith (failwithf "%A")
let private snapshot observation = CommentProjectionAdapter.readComments observation |> Result.defaultWith (failwithf "%A")
let private expected (value: CommentObservation): ExpectedProjection = { Identity = value.Identity; UpdatedAt = value.UpdatedAt; BodyDigest = CommentProjectionAdapter.sha256 value.Body }

[<Fact>]
let ``comment reads preserve terminal pages and reject duplicate reordered incomplete unauthorized and unreadable observations`` () =
    let first = comment 10L "NODE_10" 1 (Encoding.UTF8.GetBytes "first")
    let second = comment 20L "NODE_20" 2 (Encoding.UTF8.GetBytes "second")
    match CommentProjectionAdapter.readComments (complete "rev-1" [ page 1 false (Some "cursor-1") [ first ]; page 2 true None [ second ] ]) with
    | Ok observed ->
        Assert.Equal(2, observed.PageCount)
        Assert.Equal(2, observed.NodeCount)
        Assert.Equal<CommentObservation list>([ first; second ], observed.Comments)
    | Error failure -> failwithf "%A" failure
    Assert.Equal(Error(DuplicateCommentDatabaseId 10L), CommentProjectionAdapter.readComments (complete "rev" [ page 1 true None [ first; { second with Identity = identity 10L "NODE_20" } ] ]))
    Assert.Equal(Error ReorderedCommentObservation, CommentProjectionAdapter.readComments (complete "rev" [ page 1 true None [ second; first ] ]))
    Assert.Equal(Error(CommentObservationIncomplete("truncated", Some "cursor")), CommentProjectionAdapter.readComments (CommentsIncomplete("truncated", Some "cursor")))
    Assert.Equal(Error(CommentObservationUnauthorized "denied"), CommentProjectionAdapter.readComments (CommentsUnauthorized "denied"))
    Assert.Equal(Error(CommentObservationUnreadable "transport"), CommentProjectionAdapter.readComments (CommentsUnreadable "transport"))

[<Fact>]
let ``canonical marker parsing and durable authority trust reject malformed tampered edited and deleted projections distinctly`` () =
    let digest = String.replicate 64 "b"
    let current = comment 42L "NODE_42" 3 (render digest "Status: Ready\r\n")
    let observed = snapshot (complete "rev" [ page 1 true None [ current ] ])
    match CommentProjectionAdapter.evaluateTrust (Some(expected current)) (authority digest) observed with
    | Ok trusted ->
        Assert.Equal("Status: Ready\n", Encoding.UTF8.GetString trusted.Projection.HumanBody)
        Assert.Equal(digest, trusted.Projection.Marker.AuthorityDigest)
    | Error failure -> failwithf "%A" failure
    let malformed = { current with Body = Encoding.UTF8.GetBytes "<!-- fsgg:projection/v1 -->\n{bad}\nhuman\n" }
    match CommentProjectionAdapter.evaluateTrust (Some(expected malformed)) (authority digest) (snapshot (complete "rev" [ page 1 true None [ malformed ] ])) with
    | Error(ProjectionMalformed(_, MarkerMalformed _)) -> ()
    | value -> failwithf "%A" value
    let wrongAuthority = authority (String.replicate 64 "c")
    match CommentProjectionAdapter.evaluateTrust (Some(expected current)) wrongAuthority observed with
    | Error(ProjectionAuthorityDigestMismatch _) -> ()
    | value -> failwithf "%A" value
    let editedExpected = { expected current with UpdatedAt = instant 1 }
    Assert.Equal(Error(ProjectionEdited current.Identity), CommentProjectionAdapter.evaluateTrust (Some editedExpected) (authority digest) observed)
    Assert.Equal(Error(ProjectionDeleted current.Identity), CommentProjectionAdapter.evaluateTrust (Some(expected current)) (authority digest) (snapshot (complete "rev" [ page 1 true None [] ])))

[<Fact>]
let ``projection planning is byte deterministic idempotent and distinguishes create replace and no-op`` () =
    let digest = String.replicate 64 "d"
    let desired = render digest "Projection body"
    Assert.True((desired = render digest "Projection body\r\n\r\n"))
    Assert.Equal(byte '\n', Array.last desired)
    let empty = snapshot (complete "rev-1" [ page 1 true None [] ])
    match CommentProjectionAdapter.planProjection None (request digest "Projection body") empty with
    | Ok(ProjectionPlanned plan) ->
        Assert.Matches("^[0-9a-f]{64}$", plan.IdempotencyIdentity)
        match plan.Operation with CreateProjection bytes -> Assert.True((desired = bytes)) | _ -> failwith "expected create"
    | value -> failwithf "%A" value
    let existing = comment 42L "NODE_42" 3 desired
    let before = snapshot (complete "rev-1" [ page 1 true None [ existing ] ])
    match CommentProjectionAdapter.planProjection (Some(expected existing)) (request digest "Projection body") before with
    | Ok(ProjectionNoOp receipt) -> Assert.Matches("^[0-9a-f]{64}$", receipt.IdempotencyIdentity)
    | value -> failwithf "%A" value
    match CommentProjectionAdapter.planProjection (Some(expected existing)) (request digest "Changed body") before with
    | Ok(ProjectionPlanned { Operation = ReplaceProjection(identity, _, _, _); IdempotencyIdentity = key }) ->
        Assert.Equal(existing.Identity, identity)
        Assert.Matches("^[0-9a-f]{64}$", key)
    | value -> failwithf "%A" value

[<Fact>]
let ``mandatory reread and exact post-state verification refuse stale authority and unrelated change`` () =
    let digest = String.replicate 64 "e"
    let existing = comment 42L "NODE_42" 3 (render digest "Old")
    let unrelated = comment 50L "NODE_50" 4 (Encoding.UTF8.GetBytes "unrelated")
    let beforeObservation = complete "rev-1" [ page 1 true None [ existing; unrelated ] ]
    let before = snapshot beforeObservation
    let plan = match CommentProjectionAdapter.planProjection (Some(expected existing)) (request digest "New") before with Ok(ProjectionPlanned value) -> value | value -> failwithf "%A" value
    Assert.True(CommentProjectionAdapter.checkPreState (authority digest) plan beforeObservation |> Result.isOk)
    Assert.Equal(Error DurableAuthorityChanged, CommentProjectionAdapter.checkPreState (authority (String.replicate 64 "f")) plan beforeObservation)
    Assert.Equal(Error(ProjectionReReadRequired("rev-1", "rev-2")), CommentProjectionAdapter.checkPreState (authority digest) plan (complete "rev-2" [ page 1 true None [ existing; unrelated ] ]))
    let result = { existing with UpdatedAt = instant 5; Body = plan.DesiredBody }
    Assert.True(CommentProjectionAdapter.verifyPostState "rev-2" result plan (complete "rev-2" [ page 1 true None [ result; unrelated ] ]) |> Result.isOk)
    let changedUnrelated = { unrelated with Body = Encoding.UTF8.GetBytes "changed" }
    Assert.Equal(Error UnrelatedCommentChanged, CommentProjectionAdapter.verifyPostState "rev-2" result plan (complete "rev-2" [ page 1 true None [ result; changedUnrelated ] ]))

[<Fact>]
let ``comment projection qualification inventory is closed and every mutation must turn red`` () =
    let passing: GitHubCommentProjectionControlResult list =
        GitHubCommentProjectionQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubCommentProjectionQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index result -> if index = 5 then { result with MutationRed = false } else result)
    match GitHubCommentProjectionQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GCPQ-INDEPENDENT-NOT-RED" && finding.ControlId = "tampered-marker")
    | Ok () -> failwith "accepted a mutation that stayed green"
