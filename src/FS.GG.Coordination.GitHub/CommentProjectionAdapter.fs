namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

type CommentIdentity = { DatabaseId: int64; NodeId: string }
type CommentObservation = { Identity: CommentIdentity; CreatedAt: DateTimeOffset; UpdatedAt: DateTimeOffset; AuthorLogin: string; Body: byte array }
type CommentPage = { Number: int; Comments: CommentObservation list; EndCursor: string option; TerminalPage: bool }
type CommentReadObservation = CommentsComplete of revision: string * pages: CommentPage list | CommentsIncomplete of reason: string * cursor: string option | CommentsUnsupported of reason: string | CommentsUnauthorized of reason: string | CommentsUnreadable of reason: string | CommentsIndeterminate of reason: string
type CommentSnapshot = { Revision: string; PageCount: int; NodeCount: int; TerminalCursor: string option; Comments: CommentObservation list }
type CommentReadFailure = CommentObservationIncomplete of string * string option | CommentObservationUnsupported of string | CommentObservationUnauthorized of string | CommentObservationUnreadable of string | CommentObservationIndeterminate of string | InvalidCommentRevision | InvalidCommentPageChain | InvalidCommentObservation of int64 | DuplicateCommentDatabaseId of int64 | DuplicateCommentNodeId of string | ReorderedCommentObservation
type JournalAuthority = { Subject: string; JournalKind: string; JournalShard: string; Generation: int64; JournalCommit: string; AuthorityDigest: string }
type ProjectionMarker = { Schema: string; Subject: string; JournalKind: string; JournalShard: string; Generation: int64; JournalCommit: string; AuthorityDigest: string; ProjectionDigest: string }
type ParsedProjection = { Marker: ProjectionMarker; HumanBody: byte array; FullBodyDigest: string }
type MarkerFailure = MarkerMissing | MarkerMalformed of string | MarkerUnsupported of string | ProjectionDigestMismatch of expected: string * observed: string
type ExpectedProjection = { Identity: CommentIdentity; UpdatedAt: DateTimeOffset; BodyDigest: string }
type TrustedProjection = { Comment: CommentObservation; Projection: ParsedProjection; Authority: JournalAuthority }
type ProjectionTrustFailure = ProjectionMissing | ProjectionDeleted of CommentIdentity | ProjectionEdited of CommentIdentity | ProjectionMalformed of CommentIdentity * MarkerFailure | ProjectionTampered of CommentIdentity | ProjectionAuthorityDigestMismatch of expected: string * observed: string | ProjectionAmbiguous of CommentIdentity
type RenderingPolicy = { Version: string }
type ProjectionRequest = { Authority: JournalAuthority; Policy: RenderingPolicy; HumanBody: string; CausationIdentity: string }
type ProjectionOperation = CreateProjection of body: byte array | ReplaceProjection of identity: CommentIdentity * expectedUpdatedAt: DateTimeOffset * expectedBodyDigest: string * body: byte array
type ProjectionPlan = { Before: CommentSnapshot; Authority: JournalAuthority; Policy: RenderingPolicy; CausationIdentity: string; DesiredBody: byte array; DesiredBodyDigest: string; IdempotencyIdentity: string; Operation: ProjectionOperation }
type ProjectionNoOpReceipt = { ObservedRevision: string; Identity: CommentIdentity; IdempotencyIdentity: string }
type ProjectionPlanDecision = ProjectionPlanned of ProjectionPlan | ProjectionNoOp of ProjectionNoOpReceipt
type ProjectionPlanFailure = InvalidProjectionRequest of string | ProjectionSelectionFailed of ProjectionTrustFailure
type ProjectionPreStateFailure = ProjectionPreStateReadFailed of CommentReadFailure | ProjectionReReadRequired of plannedRevision: string * observedRevision: string | ConcurrentProjectionChange | DurableAuthorityChanged
type ProjectionPostStateFailure = ProjectionPostStateReadFailed of CommentReadFailure | ProjectionResultRevisionDidNotAdvance of string | ProjectionResultRevisionMismatch of expected: string * observed: string | ProjectionPostStateMismatch | UnrelatedCommentChanged

[<RequireQualifiedAccess>]
module CommentProjectionAdapter =
    [<Literal>]
    let MarkerPrefix = "<!-- fsgg:projection/v1 -->"
    [<Literal>]
    let MarkerSchema = "fsgg.coordination.projection-marker/1"
    let private utf8 = UTF8Encoding(false, true)
    let sha256 (bytes: byte array) = SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value) && value = value.Trim()
    let private isLowerHex length (value: string) = not (isNull value) && value.Length = length && value |> Seq.forall (fun c -> Char.IsDigit c || c >= 'a' && c <= 'f')
    let private duplicateBy key values = values |> List.groupBy key |> List.tryPick (fun (_, xs) -> if xs.Length > 1 then Some xs.Head else None)
    let private commentKey (comment: CommentObservation) = comment.CreatedAt, comment.Identity.DatabaseId
    let private validComment (comment: CommentObservation) =
        not (obj.ReferenceEquals(comment, null)) && comment.Identity.DatabaseId > 0L && validText comment.Identity.NodeId && validText comment.AuthorLogin && not (isNull comment.Body) && comment.UpdatedAt >= comment.CreatedAt

    let readComments (observation: CommentReadObservation) =
        if obj.ReferenceEquals(observation, null) then Error InvalidCommentPageChain else
        match observation with
        | CommentsIncomplete(reason, cursor) -> Error(CommentObservationIncomplete(reason, cursor))
        | CommentsUnsupported reason -> Error(CommentObservationUnsupported reason)
        | CommentsUnauthorized reason -> Error(CommentObservationUnauthorized reason)
        | CommentsUnreadable reason -> Error(CommentObservationUnreadable reason)
        | CommentsIndeterminate reason -> Error(CommentObservationIndeterminate reason)
        | CommentsComplete(revision, _) when not (validText revision) -> Error InvalidCommentRevision
        | CommentsComplete(_, pages) when obj.ReferenceEquals(pages, null) || List.isEmpty pages -> Error InvalidCommentPageChain
        | CommentsComplete(revision, pages) ->
            let validChain =
                pages
                |> List.mapi (fun index (page: CommentPage) ->
                    not (obj.ReferenceEquals(page, null)) && not (obj.ReferenceEquals(page.Comments, null)) && page.Number = index + 1
                    && page.TerminalPage = (index = pages.Length - 1)
                    && (page.EndCursor |> Option.forall validText)
                    && (page.TerminalPage || page.EndCursor.IsSome))
                |> List.forall id
            if not validChain then Error InvalidCommentPageChain else
            let comments = pages |> List.collect _.Comments
            match comments |> List.tryFind (validComment >> not) with
            | Some comment -> Error(InvalidCommentObservation comment.Identity.DatabaseId)
            | None ->
                match duplicateBy (fun (comment: CommentObservation) -> comment.Identity.DatabaseId) comments with
                | Some comment -> Error(DuplicateCommentDatabaseId comment.Identity.DatabaseId)
                | None ->
                    match duplicateBy (fun (comment: CommentObservation) -> comment.Identity.NodeId) comments with
                    | Some comment -> Error(DuplicateCommentNodeId comment.Identity.NodeId)
                    | None when comments <> List.sortBy commentKey comments -> Error ReorderedCommentObservation
                    | None -> Ok { Revision = revision; PageCount = pages.Length; NodeCount = comments.Length; TerminalCursor = pages |> List.last |> _.EndCursor; Comments = comments }

    let private jsonString value = JsonSerializer.Serialize(value: string)
    let private canonicalMarker (marker: ProjectionMarker) =
        $"{{\"schema\":{jsonString marker.Schema},\"subject\":{jsonString marker.Subject},\"journalKind\":{jsonString marker.JournalKind},\"journalShard\":{jsonString marker.JournalShard},\"generation\":{marker.Generation.ToString(CultureInfo.InvariantCulture)},\"journalCommit\":{jsonString marker.JournalCommit},\"authorityDigest\":{jsonString marker.AuthorityDigest},\"projectionDigest\":{jsonString marker.ProjectionDigest}}}"
    let private validAuthority (authority: JournalAuthority) =
        not (obj.ReferenceEquals(authority, null)) && validText authority.Subject && validText authority.JournalKind && validText authority.JournalShard
        && authority.Generation > 0L && isLowerHex 40 authority.JournalCommit && isLowerHex 64 authority.AuthorityDigest
    let private markerFromAuthority (digest: string) (authority: JournalAuthority): ProjectionMarker =
        { Schema = MarkerSchema; Subject = authority.Subject; JournalKind = authority.JournalKind; JournalShard = authority.JournalShard; Generation = authority.Generation; JournalCommit = authority.JournalCommit; AuthorityDigest = authority.AuthorityDigest; ProjectionDigest = digest }
    let private splitProjection (body: string) =
        let first = body.IndexOf('\n')
        let second = if first < 0 then -1 else body.IndexOf('\n', first + 1)
        if first < 0 || second < 0 || body.Substring(0, first) <> MarkerPrefix then None
        else Some(body.Substring(first + 1, second - first - 1), body.Substring(second + 1))
    let private requiredNames = [ "schema"; "subject"; "journalKind"; "journalShard"; "generation"; "journalCommit"; "authorityDigest"; "projectionDigest" ]

    let parseProjection (comment: CommentObservation) =
        try
            let body = utf8.GetString comment.Body
            match splitProjection body with
            | None -> Error MarkerMissing
            | Some(jsonLine, human) ->
                use document = JsonDocument.Parse jsonLine
                let root = document.RootElement
                if root.ValueKind <> JsonValueKind.Object || (root.EnumerateObject() |> Seq.map _.Name |> Seq.toList) <> requiredNames then Error(MarkerMalformed "non-canonical property inventory") else
                let marker: ProjectionMarker =
                    { Schema = root.GetProperty("schema").GetString(); Subject = root.GetProperty("subject").GetString(); JournalKind = root.GetProperty("journalKind").GetString(); JournalShard = root.GetProperty("journalShard").GetString(); Generation = root.GetProperty("generation").GetInt64(); JournalCommit = root.GetProperty("journalCommit").GetString(); AuthorityDigest = root.GetProperty("authorityDigest").GetString(); ProjectionDigest = root.GetProperty("projectionDigest").GetString() }
                if marker.Schema <> MarkerSchema then Error(MarkerUnsupported marker.Schema)
                elif not (validText marker.Subject && validText marker.JournalKind && validText marker.JournalShard && marker.Generation > 0L && isLowerHex 40 marker.JournalCommit && isLowerHex 64 marker.AuthorityDigest && isLowerHex 64 marker.ProjectionDigest) then Error(MarkerMalformed "invalid marker value")
                elif canonicalMarker marker <> jsonLine then Error(MarkerMalformed "marker JSON is not canonical")
                else
                    let humanBytes = utf8.GetBytes human
                    let observed = sha256 humanBytes
                    if observed <> marker.ProjectionDigest then Error(ProjectionDigestMismatch(marker.ProjectionDigest, observed))
                    else Ok { Marker = marker; HumanBody = humanBytes; FullBodyDigest = sha256 comment.Body }
        with
        | :? DecoderFallbackException -> Error(MarkerMalformed "body is not UTF-8")
        | :? JsonException -> Error(MarkerMalformed "marker JSON is malformed")
        | :? InvalidOperationException -> Error(MarkerMalformed "marker JSON has invalid value kinds")

    let private markerMatches (authority: JournalAuthority) (marker: ProjectionMarker) =
        marker.Subject = authority.Subject && marker.JournalKind = authority.JournalKind && marker.JournalShard = authority.JournalShard
        && marker.Generation = authority.Generation && marker.JournalCommit = authority.JournalCommit
    let evaluateTrust (expected: ExpectedProjection option) (authority: JournalAuthority) (snapshot: CommentSnapshot) =
        if not (validAuthority authority) then Error(ProjectionTampered { DatabaseId = 0L; NodeId = "invalid-authority" }) else
        match expected with
        | None -> Error ProjectionMissing
        | Some expected ->
            let candidates = snapshot.Comments |> List.filter (fun (comment: CommentObservation) -> comment.Identity = expected.Identity)
            match candidates with
            | [] -> Error(ProjectionDeleted expected.Identity)
            | _ :: _ :: _ -> Error(ProjectionAmbiguous expected.Identity)
            | [ comment ] when comment.UpdatedAt <> expected.UpdatedAt || sha256 comment.Body <> expected.BodyDigest -> Error(ProjectionEdited expected.Identity)
            | [ comment ] ->
                match parseProjection comment with
                | Error failure -> Error(ProjectionMalformed(expected.Identity, failure))
                | Ok projection when not (markerMatches authority projection.Marker) -> Error(ProjectionTampered expected.Identity)
                | Ok projection when projection.Marker.AuthorityDigest <> authority.AuthorityDigest -> Error(ProjectionAuthorityDigestMismatch(authority.AuthorityDigest, projection.Marker.AuthorityDigest))
                | Ok projection -> Ok { Comment = comment; Projection = projection; Authority = authority }

    let private normalizeHuman (value: string) =
        value.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n') + "\n"
    let renderProjection (request: ProjectionRequest) =
        if obj.ReferenceEquals(request, null) || not (validAuthority request.Authority) || obj.ReferenceEquals(request.Policy, null) || not (validText request.Policy.Version) || isNull request.HumanBody || not (validText request.CausationIdentity) then Error(InvalidProjectionRequest "invalid authority, policy, body, or causation")
        else
            let human = normalizeHuman request.HumanBody
            let humanBytes = utf8.GetBytes human
            let marker = markerFromAuthority (sha256 humanBytes) request.Authority
            Ok(utf8.GetBytes($"{MarkerPrefix}\n{canonicalMarker marker}\n{human}"))
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private planIdentity (action: string) (expected: ExpectedProjection option) (request: ProjectionRequest) (desiredDigest: string) =
        let expectedParts = match expected with None -> [ "absent" ] | Some value -> [ string value.Identity.DatabaseId; value.Identity.NodeId; value.UpdatedAt.ToUniversalTime().ToString("O"); value.BodyDigest ]
        [ action; request.Authority.Subject; request.Authority.JournalKind; request.Authority.JournalShard; string request.Authority.Generation; request.Authority.JournalCommit; request.Authority.AuthorityDigest; request.Policy.Version; desiredDigest; request.CausationIdentity ] @ expectedParts
        |> List.map frame |> String.concat "" |> utf8.GetBytes |> sha256
    let planProjection (expected: ExpectedProjection option) (request: ProjectionRequest) (snapshot: CommentSnapshot) =
        match renderProjection request with
        | Error failure -> Error failure
        | Ok desired ->
            let digest = sha256 desired
            match expected with
            | None ->
                let key = planIdentity "create" None request digest
                Ok(ProjectionPlanned { Before = snapshot; Authority = request.Authority; Policy = request.Policy; CausationIdentity = request.CausationIdentity; DesiredBody = desired; DesiredBodyDigest = digest; IdempotencyIdentity = key; Operation = CreateProjection desired })
            | Some expected ->
                match evaluateTrust (Some expected) request.Authority snapshot with
                | Ok trusted when trusted.Projection.FullBodyDigest = digest -> Ok(ProjectionNoOp { ObservedRevision = snapshot.Revision; Identity = expected.Identity; IdempotencyIdentity = planIdentity "no-op" (Some expected) request digest })
                | Ok _ -> Ok(ProjectionPlanned { Before = snapshot; Authority = request.Authority; Policy = request.Policy; CausationIdentity = request.CausationIdentity; DesiredBody = desired; DesiredBodyDigest = digest; IdempotencyIdentity = planIdentity "replace" (Some expected) request digest; Operation = ReplaceProjection(expected.Identity, expected.UpdatedAt, expected.BodyDigest, desired) })
                | Error(ProjectionMalformed _) | Error(ProjectionTampered _) | Error(ProjectionAuthorityDigestMismatch _) -> Ok(ProjectionPlanned { Before = snapshot; Authority = request.Authority; Policy = request.Policy; CausationIdentity = request.CausationIdentity; DesiredBody = desired; DesiredBodyDigest = digest; IdempotencyIdentity = planIdentity "replace" (Some expected) request digest; Operation = ReplaceProjection(expected.Identity, expected.UpdatedAt, expected.BodyDigest, desired) })
                | Error failure -> Error(ProjectionSelectionFailed failure)

    let checkPreState (currentAuthority: JournalAuthority) (plan: ProjectionPlan) (observation: CommentReadObservation) =
        if currentAuthority <> plan.Authority then Error DurableAuthorityChanged else
        match readComments observation with
        | Error failure -> Error(ProjectionPreStateReadFailed failure)
        | Ok observed when observed.Revision <> plan.Before.Revision -> Error(ProjectionReReadRequired(plan.Before.Revision, observed.Revision))
        | Ok observed when observed <> plan.Before -> Error ConcurrentProjectionChange
        | Ok observed -> Ok observed

    let verifyPostState (expectedResultRevision: string) (resultingComment: CommentObservation) (plan: ProjectionPlan) (observation: CommentReadObservation) =
        if expectedResultRevision = plan.Before.Revision then Error(ProjectionResultRevisionDidNotAdvance expectedResultRevision) else
        match readComments observation with
        | Error failure -> Error(ProjectionPostStateReadFailed failure)
        | Ok observed when observed.Revision <> expectedResultRevision -> Error(ProjectionResultRevisionMismatch(expectedResultRevision, observed.Revision))
        | Ok observed ->
            let expectedUnrelated =
                match plan.Operation with
                | CreateProjection _ -> plan.Before.Comments
                | ReplaceProjection(identity, _, _, _) -> plan.Before.Comments |> List.filter (fun (comment: CommentObservation) -> comment.Identity <> identity)
            let observedUnrelated = observed.Comments |> List.filter (fun (comment: CommentObservation) -> comment.Identity <> resultingComment.Identity)
            if expectedUnrelated <> observedUnrelated then Error UnrelatedCommentChanged
            elif resultingComment.Body <> plan.DesiredBody || sha256 resultingComment.Body <> plan.DesiredBodyDigest then Error ProjectionPostStateMismatch
            else
                match plan.Operation with
                | CreateProjection _ when plan.Before.Comments |> List.exists (fun (comment: CommentObservation) -> comment.Identity = resultingComment.Identity) -> Error ProjectionPostStateMismatch
                | ReplaceProjection(identity, updated, _, _) when resultingComment.Identity <> identity || resultingComment.UpdatedAt <= updated -> Error ProjectionPostStateMismatch
                | _ when observed.Comments |> List.filter (fun (comment: CommentObservation) -> comment.Identity = resultingComment.Identity) |> List.length <> 1 -> Error ProjectionPostStateMismatch
                | _ -> Ok observed
