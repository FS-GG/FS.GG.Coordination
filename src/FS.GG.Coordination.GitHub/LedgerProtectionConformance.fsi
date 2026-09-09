namespace FS.GG.Coordination.GitHub

type LedgerProtectionConformanceState =
    | CurrentPreInstall
    | InstalledFleetProtection
    | InstalledProductionProtection
    | IncompleteOrUnknown
    | DriftOrTamper

type LedgerGitRef = { Name: string; ObjectSha: string }

type LedgerEnvironmentProtection =
    { Repository: string
      Name: string
      ReviewerIds: int64 list
      PreventSelfReview: bool
      CanAdminsBypass: bool
      ProtectedBranches: bool
      CustomBranchPolicies: bool
      DeploymentBranchPatterns: string list }

type LedgerProtectionBindings =
    { OrdinaryWriterAppId: int64 option
      CutoverWriterAppId: int64 option
      ControlIssueNumber: int64 option }

type LedgerProtectionOperationalState =
    { SettingsApplied: LedgerObservation<bool>
      AppCustodyReady: LedgerObservation<bool>
      FleetInitialized: LedgerObservation<bool>
      MonitoringReady: LedgerObservation<bool> }

type LedgerProtectionConformanceSnapshot =
    { Provider: LedgerProviderObservation
      FleetHeads: LedgerObservation<LedgerGitRef list>
      PhaseTags: LedgerObservation<LedgerGitRef list>
      EffectiveRules: LedgerObservation<LedgerRule list>
      ClassicProtection: LedgerObservation<bool>
      FleetEnvironment: LedgerObservation<LedgerEnvironmentProtection>
      BranchProtectionRulePatterns: LedgerObservation<string list>
      Bindings: LedgerProtectionBindings
      Operational: LedgerProtectionOperationalState }

type LedgerProtectionPreparation =
    { State: LedgerProtectionConformanceState
      ApplyAuthorized: bool
      Operations: string list
      Blockers: string list
      Seal: string }

module LedgerProtectionConformance =
    val classify: asOf: System.DateTimeOffset -> maxAge: System.TimeSpan -> LedgerProtectionConformanceSnapshot -> LedgerProtectionConformanceState
    val prepare: asOf: System.DateTimeOffset -> maxAge: System.TimeSpan -> LedgerProtectionConformanceSnapshot -> LedgerProtectionPreparation
