namespace FS.GG.Coordination.Qualification.Contracts

open System

type GitHubShardedJournalControl = WrongShard | MissingParent | DuplicateGeneration | DigestMismatch | UnknownSchema | StaleParent | AmbiguousResponse | Rewind | Deletion | Divergence | StaleFence | TerminalAppend | Compaction | RulesetDrift | TargetPattern | Bypass | AcquisitionOrder | Compensation
type GitHubShardedJournalControlResult = { Control: GitHubShardedJournalControl; MutationRed: bool; BaselineGreen: bool }
type GitHubShardedJournalFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubShardedJournalQualification =
    [<Literal>]
    let Schema = "fsgg.coordination.github-sharded-journal-qualification/1"
    let requiredControls = [ WrongShard; MissingParent; DuplicateGeneration; DigestMismatch; UnknownSchema; StaleParent; AmbiguousResponse; Rewind; Deletion; Divergence; StaleFence; TerminalAppend; Compaction; RulesetDrift; TargetPattern; Bypass; AcquisitionOrder; Compensation ]
    let controlId = function
        | WrongShard -> "wrong-shard" | MissingParent -> "missing-parent" | DuplicateGeneration -> "duplicate-generation" | DigestMismatch -> "digest-mismatch"
        | UnknownSchema -> "unknown-schema" | StaleParent -> "stale-parent" | AmbiguousResponse -> "ambiguous-response" | Rewind -> "rewind"
        | Deletion -> "deletion" | Divergence -> "divergence" | StaleFence -> "stale-fence" | TerminalAppend -> "terminal-append"
        | Compaction -> "compaction" | RulesetDrift -> "ruleset-drift" | TargetPattern -> "target-pattern" | Bypass -> "bypass"
        | AcquisitionOrder -> "acquisition-order" | Compensation -> "compensation"
    let validate generated independent =
        let expected = requiredControls |> List.map controlId
        let inventory (producer: string) (results: GitHubShardedJournalControlResult list) =
            let observed = results |> List.map (fun result -> controlId result.Control)
            let expectedText = String.concat "," expected
            let observedText = String.concat "," observed
            if observed = expected then [] else [ { Code = "GSJQ-INVENTORY"; ControlId = producer; Message = $"expected {expectedText}; observed {observedText}" } ]
        let outcomes (producer: string) (results: GitHubShardedJournalControlResult list) =
            [ for result in results do
                if not result.MutationRed then yield { Code = $"GSJQ-{producer.ToUpperInvariant()}-NOT-RED"; ControlId = controlId result.Control; Message = "mutation did not turn red" }
                if not result.BaselineGreen then yield { Code = $"GSJQ-{producer.ToUpperInvariant()}-BASELINE-NOT-GREEN"; ControlId = controlId result.Control; Message = "baseline did not remain green" } ]
        let findings = inventory "generated" generated @ inventory "independent" independent @ outcomes "generated" generated @ outcomes "independent" independent
        if List.isEmpty findings then Ok () else Error findings
