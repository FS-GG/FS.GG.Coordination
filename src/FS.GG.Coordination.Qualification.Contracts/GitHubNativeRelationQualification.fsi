namespace FS.GG.Coordination.Qualification.Contracts

type GitHubNativeRelationControl =
    | Pagination
    | DuplicateEdge
    | ReversedEndpoint
    | RelationKind
    | StaleRevision
    | IncompleteObservation
    | ConcurrentChange
    | NoOpMutation

type GitHubNativeRelationControlResult =
    { Control: GitHubNativeRelationControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubNativeRelationFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubNativeRelationQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-native-relation-qualification/1"
    val requiredControls: GitHubNativeRelationControl list
    val controlId: GitHubNativeRelationControl -> string
    val validate: generated: GitHubNativeRelationControlResult list -> independent: GitHubNativeRelationControlResult list -> Result<unit, GitHubNativeRelationFinding list>
