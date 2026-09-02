namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

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
    let private sha256 (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digestLike (value: string) = value.Length = 64 && value |> Seq.forall Uri.IsHexDigit
    let private revisionLike (value: string) = value.Length = 40 && value |> Seq.forall Uri.IsHexDigit
    let private repositoryLike (value: string) =
        match value.Split('/') with
        | [| owner; repository |] -> owner <> "" && repository <> "" && value.Trim() = value
        | _ -> false

    let private sourceText = function ClassicProtection -> "classic" | Ruleset id -> $"ruleset:{id}"
    let private sourceOrder = function ClassicProtection -> 0L, 0L | Ruleset id -> 1L, id
    let private identity context integrationId =
        let integration = integrationId |> Option.map string |> Option.defaultValue "any"
        $"{context}@{integration}"
    let private validText (value: string) = value <> "" && value.Trim() = value
    let private eventUnconditional event =
        event.Declared && event.BranchFilters.IsEmpty && event.PathFilters.IsEmpty && event.ActivityTypes.IsEmpty

    let private duplicates projection values =
        values |> List.groupBy projection |> List.choose (fun (key, rows) -> if rows.Length > 1 then Some key else None)

    let private entryText (entry: RequiredCheckCensusEntry) =
        [ entry.Context
          entry.IntegrationId |> Option.map string |> Option.defaultValue ""
          entry.Sources |> List.map sourceText |> String.concat ","
          entry.ProducerWorkflow
          entry.ProducerJob
          entry.ProducerRevision
          string entry.PullRequestUnconditional
          string entry.MergeGroupUnconditional ]
        |> List.map frame
        |> String.concat ""

    let private seal (snapshot: RequiredCheckCensusSnapshot) (entries: RequiredCheckCensusEntry list) (aggregate: RequiredCheckCensusAggregate) =
        [ snapshot.Repository
          snapshot.ProfileSeal.ToLowerInvariant()
          snapshot.PrerequisiteReceiptDigest.ToLowerInvariant()
          snapshot.SourceRevision.ToLowerInvariant()
          entries |> List.map entryText |> String.concat ""
          string aggregate.RequiredCount
          string aggregate.ClassicOnlyCount
          string aggregate.RulesetOnlyCount
          string aggregate.DualSourceCount
          string aggregate.IntegrationBoundCount
          string aggregate.PullRequestUnconditionalCount
          string aggregate.MergeGroupUnconditionalCount
          string aggregate.PullRequestReady
          string aggregate.MergeGroupReady ]
        |> List.map frame
        |> String.concat ""
        |> sha256

    let compile (asOf: DateTimeOffset) (maxAge: TimeSpan) (snapshot: RequiredCheckCensusSnapshot) =
        let requirements = snapshot.Requirements
        let producers = snapshot.Producers
        let key (requirement: RequiredCheckRequirement) = requirement.Context, requirement.IntegrationId
        let producerKey (producer: RequiredCheckProducer) = producer.Context, producer.IntegrationId
        let findings =
            [ if snapshot.SchemaVersion <> 1 then yield UnsupportedCensusSchema snapshot.SchemaVersion
              if not (repositoryLike snapshot.Repository) then yield InvalidCensusRepository snapshot.Repository
              if not snapshot.Complete || not snapshot.ClassicComplete || not snapshot.RulesetsComplete || not snapshot.ProducersComplete then
                  yield IncompleteCensusObservation
              if snapshot.ObservedAt > asOf || asOf - snapshot.ObservedAt > maxAge then yield StaleCensusObservation
              if not (digestLike snapshot.ProfileSeal) then yield InvalidCensusBinding "profileSeal"
              if not (digestLike snapshot.PrerequisiteReceiptDigest) then yield InvalidCensusBinding "prerequisiteReceiptDigest"
              if not (revisionLike snapshot.SourceRevision) then yield InvalidCensusBinding "sourceRevision"
              for requirement in requirements do
                  if requirement.Repository <> snapshot.Repository then yield CrossRepositoryRequirement requirement.Repository
                  if not (validText requirement.Context) then yield InvalidRequiredCheckContext requirement.Context
                  if requirement.IntegrationId |> Option.exists ((>=) 0L) then yield InvalidRequiredCheckIntegration requirement.Context
                  match requirement.Source with Ruleset id when id <= 0L -> yield InvalidCensusBinding "rulesetId" | _ -> ()
              for _, (context, _) in requirements |> duplicates (fun (requirement: RequiredCheckRequirement) -> sourceText requirement.Source, key requirement) do
                  yield DuplicateRequiredCheck context
              for context, rows in requirements |> List.groupBy _.Context do
                  if rows |> List.map _.IntegrationId |> Set.ofList |> Set.count > 1 then yield AmbiguousRequiredCheckContext context
              for producer in producers do
                  if producer.Repository <> snapshot.Repository then yield CrossRepositoryProducer producer.Repository
                  if not (validText producer.Context) || not (validText producer.Workflow) || not (validText producer.Job) || not (revisionLike producer.WorkflowRevision) then
                      yield InvalidRequiredCheckProducer (identity producer.Context producer.IntegrationId)
                  if producer.IntegrationId |> Option.exists ((>=) 0L) then yield InvalidRequiredCheckIntegration producer.Context
              for context, _ in producers |> duplicates producerKey do yield DuplicateRequiredCheckProducer context
              let requiredKeys = requirements |> List.map key |> Set.ofList
              let producerKeys = producers |> List.map producerKey |> Set.ofList
              for context, integrationId in Set.difference requiredKeys producerKeys do yield MissingRequiredCheckProducer (identity context integrationId)
              for context, integrationId in Set.difference producerKeys requiredKeys do yield OrphanRequiredCheckProducer (identity context integrationId) ]
        if not findings.IsEmpty then Error findings
        else
            let producerByKey = producers |> List.map (fun producer -> producerKey producer, producer) |> Map.ofList
            let entries =
                requirements
                |> List.groupBy key
                |> List.map (fun ((context, integrationId), rows) ->
                    let producer = producerByKey[context, integrationId]
                    let pullRequest = eventUnconditional producer.PullRequest
                    let mergeGroup = eventUnconditional producer.MergeGroup
                    { Context = context
                      IntegrationId = integrationId
                      Sources = rows |> List.map _.Source |> List.sortBy sourceOrder
                      ProducerWorkflow = producer.Workflow
                      ProducerJob = producer.Job
                      ProducerRevision = producer.WorkflowRevision.ToLowerInvariant()
                      PullRequestUnconditional = pullRequest && producer.DependenciesComplete && not producer.Conditional && not producer.ContinueOnError
                      MergeGroupUnconditional = mergeGroup && producer.DependenciesComplete && not producer.Conditional && not producer.ContinueOnError })
                |> List.sortBy (fun entry -> entry.Context, entry.IntegrationId)
            let productionFindings =
                List.zip entries (entries |> List.map (fun entry -> producerByKey[entry.Context, entry.IntegrationId]))
                |> List.collect (fun (entry, producer) ->
                    [ if not producer.DependenciesComplete || producer.Conditional || producer.ContinueOnError then
                          yield ConditionalRequiredCheckProducer (identity entry.Context entry.IntegrationId)
                      if not entry.PullRequestUnconditional then yield PullRequestProductionMissing (identity entry.Context entry.IntegrationId)
                      if not entry.MergeGroupUnconditional then yield MergeGroupProductionMissing (identity entry.Context entry.IntegrationId) ])
            if not productionFindings.IsEmpty then Error productionFindings
            else
                let sourceShape entry = entry.Sources |> List.map (function ClassicProtection -> 1 | Ruleset _ -> 2) |> Set.ofList
                let aggregate =
                    { RequiredCount = entries.Length
                      ClassicOnlyCount = entries |> List.filter (fun entry -> sourceShape entry = Set.singleton 1) |> List.length
                      RulesetOnlyCount = entries |> List.filter (fun entry -> sourceShape entry = Set.singleton 2) |> List.length
                      DualSourceCount = entries |> List.filter (fun entry -> sourceShape entry = Set [ 1; 2 ]) |> List.length
                      IntegrationBoundCount = entries |> List.filter (_.IntegrationId >> Option.isSome) |> List.length
                      PullRequestUnconditionalCount = entries |> List.filter _.PullRequestUnconditional |> List.length
                      MergeGroupUnconditionalCount = entries |> List.filter _.MergeGroupUnconditional |> List.length
                      PullRequestReady = entries |> List.forall _.PullRequestUnconditional
                      MergeGroupReady = entries |> List.forall _.MergeGroupUnconditional }
                let report =
                    { Repository = snapshot.Repository
                      ProfileSeal = snapshot.ProfileSeal.ToLowerInvariant()
                      PrerequisiteReceiptDigest = snapshot.PrerequisiteReceiptDigest.ToLowerInvariant()
                      SourceRevision = snapshot.SourceRevision.ToLowerInvariant()
                      Entries = entries
                      Aggregate = aggregate
                      Seal = seal snapshot entries aggregate }
                Ok report

    let verify expectedSeal asOf maxAge snapshot =
        match compile asOf maxAge snapshot with
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredRequiredCheckCensusSeal ]
        | Error findings -> Error findings
