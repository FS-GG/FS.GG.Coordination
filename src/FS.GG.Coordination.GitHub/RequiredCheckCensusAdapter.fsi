namespace FS.GG.Coordination.GitHub

open System

type RequiredCheckSource = ClassicProtection | Ruleset of int64

type RequiredCheckRequirement =
    { Repository: string
      Context: string
      IntegrationId: int64 option
      Source: RequiredCheckSource }

type RequiredCheckEventProduction =
    { Declared: bool
      BranchFilters: string list
      PathFilters: string list
      ActivityTypes: string list }

type RequiredCheckProducer =
    { Repository: string
      Context: string
      IntegrationId: int64 option
      Workflow: string
      Job: string
      WorkflowRevision: string
      WorkflowSha256: string
      PullRequest: RequiredCheckEventProduction
      MergeGroup: RequiredCheckEventProduction
      DependenciesComplete: bool
      Conditional: bool
      ContinueOnError: bool }

type RequiredCheckCensusSnapshot =
    { SchemaVersion: int
      Repository: string
      ProfileSeal: string
      PrerequisiteReceiptDigest: string
      AuthorityEvidenceSha256: string
      SourceRevision: string
      ObservedAt: DateTimeOffset
      Complete: bool
      ClassicComplete: bool
      RulesetsComplete: bool
      ProducersComplete: bool
      Requirements: RequiredCheckRequirement list
      Producers: RequiredCheckProducer list }

type RequiredCheckCensusEntry =
    { Context: string
      IntegrationId: int64 option
      Sources: RequiredCheckSource list
      ProducerWorkflow: string
      ProducerJob: string
      ProducerRevision: string
      ProducerWorkflowSha256: string
      PullRequest: RequiredCheckEventProduction
      MergeGroup: RequiredCheckEventProduction
      DependenciesComplete: bool
      Conditional: bool
      ContinueOnError: bool
      PullRequestUnconditional: bool
      MergeGroupUnconditional: bool }

type RequiredCheckCensusAggregate =
    { RequiredCount: int
      ClassicOnlyCount: int
      RulesetOnlyCount: int
      DualSourceCount: int
      IntegrationBoundCount: int
      PullRequestUnconditionalCount: int
      MergeGroupUnconditionalCount: int
      PullRequestReady: bool
      MergeGroupReady: bool }

type RequiredCheckCensusReport =
    { Repository: string
      ProfileSeal: string
      PrerequisiteReceiptDigest: string
      AuthorityEvidenceSha256: string
      SourceRevision: string
      Entries: RequiredCheckCensusEntry list
      Aggregate: RequiredCheckCensusAggregate
      Seal: string }

type RequiredCheckCensusFinding =
    | UnsupportedCensusSchema of int
    | InvalidCensusRepository of string
    | IncompleteCensusObservation
    | StaleCensusObservation
    | InvalidCensusBinding of string
    | CrossRepositoryRequirement of string
    | InvalidRequiredCheckContext of string
    | InvalidRequiredCheckIntegration of string
    | DuplicateRequiredCheck of string
    | AmbiguousRequiredCheckContext of string
    | CrossRepositoryProducer of string
    | InvalidRequiredCheckProducer of string
    | DuplicateRequiredCheckProducer of string
    | MissingRequiredCheckProducer of string
    | OrphanRequiredCheckProducer of string
    | ConditionalRequiredCheckProducer of string
    | PullRequestProductionMissing of string
    | MergeGroupProductionMissing of string
    | AlteredRequiredCheckCensusSeal

module RequiredCheckCensusAdapter =
    val compile: asOf: DateTimeOffset -> maxAge: TimeSpan -> RequiredCheckCensusSnapshot -> Result<RequiredCheckCensusReport, RequiredCheckCensusFinding list>
    val verify: expectedSeal: string -> asOf: DateTimeOffset -> maxAge: TimeSpan -> RequiredCheckCensusSnapshot -> Result<RequiredCheckCensusReport, RequiredCheckCensusFinding list>
