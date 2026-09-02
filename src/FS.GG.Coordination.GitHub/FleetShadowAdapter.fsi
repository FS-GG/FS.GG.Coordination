namespace FS.GG.Coordination.GitHub

open System

type FleetShadowCapability = RosterRead | MetadataRead | IssueRead | ProjectRead | JournalRead | CheckRead | MutationCapability of string
type FleetShadowDivergenceClass = V1Defect | V2Defect | IntentionalVersionedChange
type FleetShadowDecision = { Raw: string; Normalized: string; SourceRevision: string }
type FleetShadowDivergence = { Classification: FleetShadowDivergenceClass; AccountableAgent: string; Evidence: string }
type FleetShadowItem =
    { Repository: string
      Item: string
      V1: FleetShadowDecision
      V2: FleetShadowDecision
      Divergence: FleetShadowDivergence option }
type FleetShadowRepository =
    { Repository: string
      ExpectedItemCount: int
      TerminalPageObserved: bool
      Items: FleetShadowItem list }
type FleetShadowObservation =
    { Complete: bool
      RosterRevision: string
      Roster: string list
      WindowStartedAt: DateTimeOffset
      WindowEndedAt: DateTimeOffset
      Capabilities: FleetShadowCapability list
      MutationAttempts: string list
      Repositories: FleetShadowRepository list }
type FleetShadowReport =
    { RosterRevision: string
      RepositoryCount: int
      ItemCount: int
      EqualDecisionCount: int
      ClassifiedDivergenceCount: int
      UnexplainedDivergenceCount: int
      Seal: string }
type FleetShadowRefusal =
    | FleetObservationIncomplete
    | InvalidFleetRosterRevision
    | InvalidFleetObservationWindow
    | StaleFleetObservation
    | InvalidFleetRoster
    | DuplicateFleetRepository of string
    | MissingFleetRepository of string
    | UnexpectedFleetRepository of string
    | IncompleteFleetRepository of string
    | InvalidFleetItem of string
    | DuplicateFleetItem of string
    | CrossRepositoryFleetItem of string
    | InvalidFleetDecision of string
    | EqualDecisionHasDivergence of string
    | UnclassifiedFleetDivergence of string
    | InvalidFleetDivergenceEvidence of string
    | InvalidFleetCapabilityManifest
    | FleetMutationCapabilityPresent of string
    | FleetMutationAttempted of string
    | AlteredFleetShadowSeal

[<RequireQualifiedAccess>]
module FleetShadowAdapter =
    val requiredCapabilities: FleetShadowCapability list
    val compare: asOf: DateTimeOffset -> maximumAge: TimeSpan -> FleetShadowObservation -> Result<FleetShadowReport, FleetShadowRefusal list>
    val verify: expectedSeal: string -> asOf: DateTimeOffset -> maximumAge: TimeSpan -> FleetShadowObservation -> Result<FleetShadowReport, FleetShadowRefusal list>
