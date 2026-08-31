namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubNativeRelationControl = Pagination | DuplicateEdge | ReversedEndpoint | RelationKind | StaleRevision | IncompleteObservation | ConcurrentChange | NoOpMutation
type GitHubNativeRelationControlResult = { Control: GitHubNativeRelationControl; MutationRed: bool; BaselineGreen: bool }
type GitHubNativeRelationFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubNativeRelationQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-native-relation-qualification/1"
    let requiredControls = [ Pagination; DuplicateEdge; ReversedEndpoint; RelationKind; StaleRevision; IncompleteObservation; ConcurrentChange; NoOpMutation ]
    let controlId = function
        | Pagination -> "pagination" | DuplicateEdge -> "duplicate-edge" | ReversedEndpoint -> "reversed-endpoint"
        | RelationKind -> "relation-kind" | StaleRevision -> "stale-revision" | IncompleteObservation -> "incomplete-observation"
        | ConcurrentChange -> "concurrent-change" | NoOpMutation -> "no-op-mutation"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inventory (producer: string) (results: GitHubNativeRelationControlResult list) =
            let observed = results |> List.map (fun value -> controlId value.Control)
            let expectedText = String.concat "," expected
            let observedText = String.concat "," observed
            if observed = expected then [] else [ { Code = "GNRQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubNativeRelationControlResult list) =
            [ for value in results do
                let id = controlId value.Control
                if not value.MutationRed then yield { Code = $"GNRQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = id; Message = $"{producer} mutation did not turn red" }
                if not value.BaselineGreen then yield { Code = $"GNRQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = id; Message = $"{producer} baseline did not remain green" } ]
        let findings = inventory "generated" generated @ inventory "independent" independent @ outcomes "generated" generated @ outcomes "independent" independent
        if List.isEmpty findings then Ok () else Error findings
