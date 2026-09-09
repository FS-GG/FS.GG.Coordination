namespace FS.GG.Coordination.Qualification.Contracts

type GitHubLedgerProtectionProviderControl =
    | ProviderBinding | PageOrdering | CompletePagination | PayloadDigest | ProviderFreshness
    | ProviderUnknownVsAbsent | ProviderEffectiveComposition | ProviderContinuity | ProviderExactFleetRef
    | ProviderEndpointCorrespondence | ProviderRulesetSemantics | ProviderBindingState
    | DryOperationSeal | ProviderCompleteSeal | DedicatedWriterBlocker | ProviderNoApply

type GitHubLedgerProtectionProviderControlResult =
    { Control: GitHubLedgerProtectionProviderControl
      Passed: bool }

type GitHubLedgerProtectionProviderFinding = { Code: string; ControlId: string }

module GitHubLedgerProtectionProviderQualification =
    val requiredControls: GitHubLedgerProtectionProviderControl list
    val controlId: GitHubLedgerProtectionProviderControl -> string
    val validate: generated: GitHubLedgerProtectionProviderControlResult list -> independent: GitHubLedgerProtectionProviderControlResult list -> Result<unit, GitHubLedgerProtectionProviderFinding list>
