namespace FS.GG.Coordination.Qualification.Contracts

type GitHubFleetShadowControl = RosterCompleteness | PaginationCompleteness | StableOrdering | DecisionPreservation | EqualDecision | V1DefectClassification | V2DefectClassification | VersionChangeClassification | ZeroUnexplained | ReadOnlyManifest | NoMutationAttempt | FreshObservation | ExactSeal | ExactReplay | CrossSubject | PartialUnreadable | QuintAndPrerequisite | LiveEvidence
type GitHubFleetShadowControlResult = { Control: GitHubFleetShadowControl; MutationRed: bool; BaselineGreen: bool }
type GitHubFleetShadowFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubFleetShadowQualification =
    val requiredControls: GitHubFleetShadowControl list
    val controlId: GitHubFleetShadowControl -> string
    val validate: generated: GitHubFleetShadowControlResult list -> independent: GitHubFleetShadowControlResult list -> Result<unit, GitHubFleetShadowFinding list>
