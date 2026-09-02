namespace FS.GG.Coordination.Qualification.Contracts

type GitHubFleetShadowControl = RosterCompleteness | PaginationCompleteness | StableOrdering | DecisionPreservation | EqualDecision | V1DefectClassification | V2DefectClassification | VersionChangeClassification | ZeroUnexplained | ReadOnlyManifest | NoMutationAttempt | FreshObservation | ExactSeal | ExactReplay | CrossSubject | PartialUnreadable | QuintAndPrerequisite | LiveEvidence
type GitHubFleetShadowControlResult = { Control: GitHubFleetShadowControl; MutationRed: bool; BaselineGreen: bool }
type GitHubFleetShadowFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubFleetShadowQualification =
    let requiredControls =
        [ RosterCompleteness; PaginationCompleteness; StableOrdering; DecisionPreservation; EqualDecision
          V1DefectClassification; V2DefectClassification; VersionChangeClassification; ZeroUnexplained
          ReadOnlyManifest; NoMutationAttempt; FreshObservation; ExactSeal; ExactReplay; CrossSubject
          PartialUnreadable; QuintAndPrerequisite; LiveEvidence ]
    let controlId = function
        | RosterCompleteness -> "roster-completeness" | PaginationCompleteness -> "pagination-completeness"
        | StableOrdering -> "stable-ordering" | DecisionPreservation -> "decision-preservation"
        | EqualDecision -> "equal-decision" | V1DefectClassification -> "v1-defect-classification"
        | V2DefectClassification -> "v2-defect-classification" | VersionChangeClassification -> "version-change-classification"
        | ZeroUnexplained -> "zero-unexplained" | ReadOnlyManifest -> "read-only-manifest"
        | NoMutationAttempt -> "no-mutation-attempt" | FreshObservation -> "fresh-observation"
        | ExactSeal -> "exact-seal" | ExactReplay -> "exact-replay" | CrossSubject -> "cross-subject"
        | PartialUnreadable -> "partial-unreadable" | QuintAndPrerequisite -> "quint-and-prerequisite"
        | LiveEvidence -> "live-evidence"
    let validate generated independent =
        let findingsFor source (values: GitHubFleetShadowControlResult list) =
            let expected = requiredControls |> List.map controlId
            let observed = values |> List.map (_.Control >> controlId)
            [ if observed <> expected then yield { Code = source + "-INVENTORY"; ControlId = "inventory"; Message = "control inventory is not exact" }
              for value in values do
                  if not value.BaselineGreen then yield { Code = source + "-BASELINE"; ControlId = controlId value.Control; Message = "baseline was not green" }
                  if not value.MutationRed then yield { Code = source + "-NOT-RED"; ControlId = controlId value.Control; Message = "mutation did not fail closed" } ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
