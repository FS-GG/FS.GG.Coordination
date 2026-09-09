namespace FS.GG.Coordination.GitHub

open System

type LedgerObservation<'a> = Observed of 'a | ProvenAbsent | Unknown of string
type LedgerRule = Creation | Update | Deletion | NonFastForward
type LedgerActor = App of int64 | OrganizationAdmin
type LedgerRulesetTarget = Branch | Tag
type LedgerRulesetEnforcement = Active | Evaluate | Disabled
type LedgerBypassMode = Always | PullRequest
type LedgerBypass = { Actor: LedgerActor; Mode: LedgerBypassMode }
type DedicatedLedgerApp = { Id: int64; ContentsPermission: string; AdditionalWritePermissions: string list }

type EffectiveLedgerRuleset =
    { Id: int64
      Name: string
      Target: LedgerRulesetTarget
      Enforcement: LedgerRulesetEnforcement
      Inherited: bool
      Includes: string list
      Excludes: string list
      Rules: LedgerRule list
      Bypass: LedgerBypass list }

type LedgerProtectionObservation =
    { SchemaVersion: int
      Repository: string
      RepositoryId: int64
      Revision: string
      ObservedAt: DateTimeOffset
      PagesComplete: bool
      PageSha256: string list
      PageDigestSha256: string
      PreviousObservationSha256: string option
      PreviousObservationEvidenceSha256: string option
      ProviderEnvelopeSha256: string option
      Rulesets: LedgerObservation<EffectiveLedgerRuleset list>
      PhaseTags: LedgerObservation<string list>
      Environment: LedgerObservation<string>
      DedicatedWriterApp: LedgerObservation<DedicatedLedgerApp>
      ControlIssue: LedgerObservation<int64> }

type LedgerProtectionIntent =
    { Kind: string
      Target: string
      Rules: LedgerRule list
      Bypass: LedgerBypass list }

type LedgerProtectionPlan =
    { Repository: string
      FleetRef: string
      PhaseTagPattern: string
      ApplyAuthorized: bool
      Intents: LedgerProtectionIntent list
      ProductionBlockers: string list
      ObservationSha256: string
      Seal: string }

type LedgerProtectionFinding =
    | UnsupportedLedgerProtectionSchema of int
    | InvalidLedgerProtectionBinding of string
    | IncompleteLedgerProtectionObservation of string
    | StaleLedgerProtectionObservation
    | LedgerProtectionContinuityFailure
    | ContradictoryLedgerProtectionRules of string
    | DedicatedWriterIdentityMissing
    | SharedWriterStillCoversFleetRef
    | AlteredLedgerProtectionSeal

module LedgerProtectionPlanAdapter =
    val authorityRepository: string
    val authorityRepositoryId: int64
    val fleetIdentity: string
    val fleetRef: string
    val journalPattern: string
    val phaseTagPattern: string
    val compile: asOf: DateTimeOffset -> maxAge: TimeSpan -> LedgerProtectionObservation -> Result<LedgerProtectionPlan, LedgerProtectionFinding list>
    val verify: expectedSeal: string -> asOf: DateTimeOffset -> maxAge: TimeSpan -> LedgerProtectionObservation -> Result<LedgerProtectionPlan, LedgerProtectionFinding list>
