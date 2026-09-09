namespace FS.GG.Coordination.Qualification.Contracts

type GitHubLedgerProtectionControl = AuthorityIdentity | ExactFleetRef | SelectorSemantics | RulesetSemantics | SharedWriterCarveOut | DedicatedWriterIdentity | NamespaceIntegrity | PhaseTagCreation | PhaseTagImmutability | EffectiveComposition | UnknownVsAbsent | ObservationFreshness | Pagination | ObservationDigest | Continuity | CompleteSeal | Tamper | ProtectedEnvironment | ControlIssue | NoApply
type GitHubLedgerProtectionControlResult = { Control: GitHubLedgerProtectionControl; Passed: bool }
type GitHubLedgerProtectionFinding = { Code: string; ControlId: string }
module GitHubLedgerProtectionQualification =
    val requiredControls: GitHubLedgerProtectionControl list
    val controlId: GitHubLedgerProtectionControl -> string
    val validate: generated: GitHubLedgerProtectionControlResult list -> independent: GitHubLedgerProtectionControlResult list -> Result<unit, GitHubLedgerProtectionFinding list>
