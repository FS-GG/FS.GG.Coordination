namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type GitHubLifecycleProjectionControl = IntentAuthority | CompleteKnowledge | FormalPrecedence | HoldDependency | Claim | PullRequest | Review | Delivery | IssueState | StatusMapping | StatusNotIntent | HistoricalFact | ProtectedDelivery | ExactPlan | RevisionFence | ExactReplay | BoundedCost | QuintAndPrerequisite
type GitHubLifecycleProjectionControlResult = { Control: GitHubLifecycleProjectionControl; MutationRed: bool; BaselineGreen: bool }
type GitHubLifecycleProjectionFinding = { Code: string; ControlId: string; Message: string }
[<RequireQualifiedAccess>]
module GitHubLifecycleProjectionQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-lifecycle-projection-qualification/1"
    val requiredControls: GitHubLifecycleProjectionControl list
    val controlId: GitHubLifecycleProjectionControl -> string
    val validate: generated: GitHubLifecycleProjectionControlResult list -> independent: GitHubLifecycleProjectionControlResult list -> Result<unit, GitHubLifecycleProjectionFinding list>
