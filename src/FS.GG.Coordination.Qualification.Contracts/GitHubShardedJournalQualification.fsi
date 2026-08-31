namespace FS.GG.Coordination.Qualification.Contracts

type GitHubShardedJournalControl = WrongShard | MissingParent | DuplicateGeneration | DigestMismatch | UnknownSchema | StaleParent | AmbiguousResponse | Rewind | Deletion | Divergence | StaleFence | TerminalAppend | Compaction | RulesetDrift | TargetPattern | Bypass | AcquisitionOrder | Compensation
type GitHubShardedJournalControlResult = { Control: GitHubShardedJournalControl; MutationRed: bool; BaselineGreen: bool }
type GitHubShardedJournalFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubShardedJournalQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-sharded-journal-qualification/1"
    val requiredControls: GitHubShardedJournalControl list
    val controlId: GitHubShardedJournalControl -> string
    val validate: generated: GitHubShardedJournalControlResult list -> independent: GitHubShardedJournalControlResult list -> Result<unit, GitHubShardedJournalFinding list>
