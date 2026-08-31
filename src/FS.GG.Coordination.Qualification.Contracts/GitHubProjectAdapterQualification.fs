namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubProjectAdapterControl = Pagination | ArchivedItem | DuplicateItem | ExternalItem | DraftItem | MissingItem | UnreadableObservation | StaleRevision | ConcurrentChange | NoOpMutation
type GitHubProjectAdapterControlResult = { Control: GitHubProjectAdapterControl; MutationRed: bool; BaselineGreen: bool }
type GitHubProjectAdapterFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubProjectAdapterQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-project-adapter-qualification/1"
    let requiredControls = [ Pagination; ArchivedItem; DuplicateItem; ExternalItem; DraftItem; MissingItem; UnreadableObservation; StaleRevision; ConcurrentChange; NoOpMutation ]
    let controlId = function
        | Pagination -> "pagination" | ArchivedItem -> "archived-item" | DuplicateItem -> "duplicate-item"
        | ExternalItem -> "external-item" | DraftItem -> "draft-item" | MissingItem -> "missing-item"
        | UnreadableObservation -> "unreadable-observation" | StaleRevision -> "stale-revision"
        | ConcurrentChange -> "concurrent-change" | NoOpMutation -> "no-op-mutation"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inventory producer (results: GitHubProjectAdapterControlResult list) =
            let observed = results |> List.map (fun value -> controlId value.Control)
            if observed = expected then []
            else
                let expectedText = String.concat "," expected
                let observedText = String.concat "," observed
                [ { Code = "GPAQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubProjectAdapterControlResult list) =
            [ for value in results do
                let id = controlId value.Control
                if not value.MutationRed then yield { Code = $"GPAQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = id; Message = $"{producer} mutation did not turn red" }
                if not value.BaselineGreen then yield { Code = $"GPAQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = id; Message = $"{producer} baseline did not remain green" } ]
        let findings = inventory "generated" generated @ inventory "independent" independent @ outcomes "generated" generated @ outcomes "independent" independent
        if List.isEmpty findings then Ok () else Error findings
