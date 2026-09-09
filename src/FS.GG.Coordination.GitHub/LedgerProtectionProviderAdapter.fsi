namespace FS.GG.Coordination.GitHub

open System

type LedgerProviderPayload =
    | RulesetsPage of EffectiveLedgerRuleset list
    | PhaseTagsPage of string list
    | EnvironmentsPage of string list
    | OrganizationInstallationsPage of LedgerProviderInstallation list
    | ControlIssuesPage of LedgerProviderIssue list

and LedgerProviderInstallation =
    { InstallationId: int64
      AppId: int64
      Slug: string
      RepositorySelection: string
      Permissions: (string * string) list
      SelectedRepositoriesEndpoint: string option
      SelectedRepositoriesPagesComplete: bool
      SelectedRepositories: LedgerObservation<string list> }

and LedgerProviderIssue = { Number: int64; IsPullRequest: bool }

type LedgerProviderPage =
    { Endpoint: string
      Page: int
      LastPage: int
      IsTerminal: bool
      HttpStatus: int
      ObservedAt: DateTimeOffset
      PayloadSha256: string
      Payload: LedgerProviderPayload }

type LedgerProviderObservation =
    { SchemaVersion: int
      Repository: string
      RepositoryId: int64
      Revision: string
      PreviousObservationSha256: string option
      PreviousObservationEvidenceSha256: string option
      RawSetSha256: string option
      NormalizedSetSha256: string option
      DedicatedWriterAppId: int64 option
      ControlIssueNumber: int64 option
      Pages: LedgerProviderPage list }

type LedgerProviderFinding =
    | UnsupportedLedgerProviderSchema of int
    | InvalidLedgerProviderBinding of string
    | IncompleteLedgerProviderPagination of string
    | StaleLedgerProviderPage of string
    | ContradictoryLedgerProviderPage of string
    | UnknownLedgerProviderResource of string
    | AlteredLedgerProviderPayload of string
    | LedgerProviderPlanRefused of LedgerProtectionFinding list

module LedgerProtectionProviderAdapter =
    val rulesetsEndpoint: string
    val phaseTagsEndpoint: string
    val environmentEndpoint: string
    val installationsEndpoint: string
    val controlIssuesEndpoint: string
    val payloadSha256: LedgerProviderPayload -> string
    val normalize: asOf: DateTimeOffset -> maxAge: TimeSpan -> LedgerProviderObservation -> Result<LedgerProtectionObservation, LedgerProviderFinding list>
    val compile: asOf: DateTimeOffset -> maxAge: TimeSpan -> LedgerProviderObservation -> Result<LedgerProtectionPlan, LedgerProviderFinding list>
