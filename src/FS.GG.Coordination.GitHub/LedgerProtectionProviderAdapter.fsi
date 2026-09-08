namespace FS.GG.Coordination.GitHub

open System

type LedgerProviderPayload =
    | RulesetsPage of EffectiveLedgerRuleset list
    | PhaseTagsPage of string list
    | EnvironmentState of LedgerObservation<string>
    | DedicatedWriterAppState of LedgerObservation<DedicatedLedgerApp>
    | ControlIssueState of LedgerObservation<int64>

type LedgerProviderPage =
    { Endpoint: string
      Page: int
      LastPage: int
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
    val dedicatedWriterAppEndpoint: string
    val controlIssueEndpoint: string
    val payloadSha256: LedgerProviderPayload -> string
    val normalize: asOf: DateTimeOffset -> maxAge: TimeSpan -> LedgerProviderObservation -> Result<LedgerProtectionObservation, LedgerProviderFinding list>
    val compile: asOf: DateTimeOffset -> maxAge: TimeSpan -> LedgerProviderObservation -> Result<LedgerProtectionPlan, LedgerProviderFinding list>
