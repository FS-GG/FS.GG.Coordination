namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubCommentProjectionControl = Pagination | DuplicateIdentity | ReorderedPage | EditedProjection | DeletedProjection | TamperedMarker | MalformedJson | AuthorityDigestMismatch | IncompleteObservation | StaleRevision | ConcurrentChange | NoOpMutation
type GitHubCommentProjectionControlResult = { Control: GitHubCommentProjectionControl; MutationRed: bool; BaselineGreen: bool }
type GitHubCommentProjectionFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubCommentProjectionQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-comment-projection-qualification/1"
    let requiredControls = [ Pagination; DuplicateIdentity; ReorderedPage; EditedProjection; DeletedProjection; TamperedMarker; MalformedJson; AuthorityDigestMismatch; IncompleteObservation; StaleRevision; ConcurrentChange; NoOpMutation ]
    let controlId = function
        | Pagination -> "pagination" | DuplicateIdentity -> "duplicate-identity" | ReorderedPage -> "reordered-page"
        | EditedProjection -> "edited-projection" | DeletedProjection -> "deleted-projection" | TamperedMarker -> "tampered-marker"
        | MalformedJson -> "malformed-json" | AuthorityDigestMismatch -> "authority-digest-mismatch" | IncompleteObservation -> "incomplete-observation"
        | StaleRevision -> "stale-revision" | ConcurrentChange -> "concurrent-change" | NoOpMutation -> "no-op-mutation"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inventory producer (results: GitHubCommentProjectionControlResult list) =
            let observed = results |> List.map (fun value -> controlId value.Control)
            if observed = expected then [] else
                let expectedText = String.concat "," expected
                let observedText = String.concat "," observed
                [ { Code = "GCPQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubCommentProjectionControlResult list) =
            [ for value in results do
                let id = controlId value.Control
                if not value.MutationRed then yield { Code = $"GCPQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = id; Message = $"{producer} mutation did not turn red" }
                if not value.BaselineGreen then yield { Code = $"GCPQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = id; Message = $"{producer} baseline did not remain green" } ]
        let findings = inventory "generated" generated @ inventory "independent" independent @ outcomes "generated" generated @ outcomes "independent" independent
        if List.isEmpty findings then Ok () else Error findings
