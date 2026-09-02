namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type FleetShadowCapability = RosterRead | MetadataRead | IssueRead | ProjectRead | JournalRead | CheckRead | MutationCapability of string
type FleetShadowDivergenceClass = V1Defect | V2Defect | IntentionalVersionedChange
type FleetShadowDecision = { Raw: string; Normalized: string; SourceRevision: string }
type FleetShadowDivergence = { Classification: FleetShadowDivergenceClass; AccountableAgent: string; Evidence: string }
type FleetShadowItem = { Repository: string; Item: string; V1: FleetShadowDecision; V2: FleetShadowDecision; Divergence: FleetShadowDivergence option }
type FleetShadowRepository = { Repository: string; ExpectedItemCount: int; TerminalPageObserved: bool; Items: FleetShadowItem list }
type FleetShadowObservation =
    { Complete: bool; RosterRevision: string; Roster: string list; WindowStartedAt: DateTimeOffset
      WindowEndedAt: DateTimeOffset; Capabilities: FleetShadowCapability list; MutationAttempts: string list
      Repositories: FleetShadowRepository list }
type FleetShadowReport =
    { RosterRevision: string; RepositoryCount: int; ItemCount: int; EqualDecisionCount: int
      ClassifiedDivergenceCount: int; UnexplainedDivergenceCount: int; Seal: string }
type FleetShadowRefusal =
    | FleetObservationIncomplete | InvalidFleetRosterRevision | InvalidFleetObservationWindow | StaleFleetObservation
    | InvalidFleetRoster | DuplicateFleetRepository of string | MissingFleetRepository of string
    | UnexpectedFleetRepository of string | IncompleteFleetRepository of string | InvalidFleetItem of string
    | DuplicateFleetItem of string | CrossRepositoryFleetItem of string | InvalidFleetDecision of string
    | EqualDecisionHasDivergence of string | UnclassifiedFleetDivergence of string
    | InvalidFleetDivergenceEvidence of string | InvalidFleetCapabilityManifest
    | FleetMutationCapabilityPresent of string | FleetMutationAttempted of string | AlteredFleetShadowSeal

[<RequireQualifiedAccess>]
module FleetShadowAdapter =
    let requiredCapabilities = [ RosterRead; MetadataRead; IssueRead; ProjectRead; JournalRead; CheckRead ]

    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)
    let private canonical (value: string) = if isNull value then "" else value.Trim().ToLowerInvariant()
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hashParts values =
        values |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()
    let private capabilityName = function
        | RosterRead -> "roster-read" | MetadataRead -> "metadata-read" | IssueRead -> "issue-read"
        | ProjectRead -> "project-read" | JournalRead -> "journal-read" | CheckRead -> "check-read"
        | MutationCapability value -> "mutation:" + value
    let private className = function
        | V1Defect -> "v1-defect" | V2Defect -> "v2-defect" | IntentionalVersionedChange -> "intentional-versioned-change"
    let private decisionParts (decision: FleetShadowDecision) = [ decision.Raw; decision.Normalized; decision.SourceRevision ]
    let private divergenceParts (value: FleetShadowDivergence option) =
        match value with
        | None -> [ "equal" ]
        | Some value -> [ "divergence"; className value.Classification; value.AccountableAgent; value.Evidence ]
    let private itemParts (item: FleetShadowItem) =
        [ canonical item.Repository; item.Item ] @ decisionParts item.V1 @ decisionParts item.V2 @ divergenceParts item.Divergence
    let private repositoryParts (repository: FleetShadowRepository) =
        [ canonical repository.Repository; string repository.ExpectedItemCount; string repository.TerminalPageObserved ]
        @ (repository.Items |> List.collect itemParts)
    let private seal (observation: FleetShadowObservation) =
        hashParts
            ([ observation.RosterRevision; observation.WindowStartedAt.ToUniversalTime().ToString("O")
               observation.WindowEndedAt.ToUniversalTime().ToString("O"); string observation.Complete ]
             @ (observation.Roster |> List.map canonical)
             @ (observation.Capabilities |> List.map capabilityName)
             @ observation.MutationAttempts
             @ (observation.Repositories |> List.collect repositoryParts))

    let private duplicates values =
        values |> List.countBy id |> List.choose (fun (value, count) -> if count > 1 then Some value else None)

    let compare (asOf: DateTimeOffset) (maximumAge: TimeSpan) (observation: FleetShadowObservation) =
        if obj.ReferenceEquals(observation, null) then Error [ FleetObservationIncomplete ]
        else
            let roster = observation.Roster |> List.map canonical
            let repositories = observation.Repositories |> List.map (fun value -> canonical value.Repository)
            let structural =
                [ if not observation.Complete then yield FleetObservationIncomplete
                  if not (validText observation.RosterRevision) then yield InvalidFleetRosterRevision
                  if maximumAge <= TimeSpan.Zero || observation.WindowStartedAt > observation.WindowEndedAt || asOf < observation.WindowEndedAt then
                      yield InvalidFleetObservationWindow
                  elif asOf - observation.WindowEndedAt > maximumAge then yield StaleFleetObservation
                  if roster.IsEmpty || roster |> List.exists (not << validText) || roster <> List.sort roster || not (duplicates roster).IsEmpty then
                      yield InvalidFleetRoster
                  for value in duplicates repositories do yield DuplicateFleetRepository value
                  for value in roster |> List.filter (fun value -> not (List.contains value repositories)) do yield MissingFleetRepository value
                  for value in repositories |> List.filter (fun value -> not (List.contains value roster)) do yield UnexpectedFleetRepository value
                  if repositories <> roster then yield InvalidFleetRoster
                  if observation.Capabilities <> requiredCapabilities then yield InvalidFleetCapabilityManifest
                  for capability in observation.Capabilities do
                      match capability with MutationCapability value -> yield FleetMutationCapabilityPresent value | _ -> ()
                  for attempt in observation.MutationAttempts do yield FleetMutationAttempted attempt ]
            let itemRefusals =
                observation.Repositories
                |> List.collect (fun (repository: FleetShadowRepository) ->
                    let repo = canonical repository.Repository
                    let itemIds = repository.Items |> List.map _.Item
                    [ if repository.ExpectedItemCount < 0 || repository.ExpectedItemCount <> repository.Items.Length || not repository.TerminalPageObserved then
                          yield IncompleteFleetRepository repo
                      if itemIds <> List.sort itemIds then yield IncompleteFleetRepository repo
                      for value in duplicates itemIds do yield DuplicateFleetItem value
                      for item: FleetShadowItem in repository.Items do
                          if not (validText item.Item) then yield InvalidFleetItem item.Item
                          if canonical item.Repository <> repo then yield CrossRepositoryFleetItem item.Item
                          if [ item.V1; item.V2 ] |> List.exists (fun decision -> not (validText decision.Raw) || not (validText decision.Normalized) || not (validText decision.SourceRevision)) then
                              yield InvalidFleetDecision item.Item
                          if item.V1.Normalized = item.V2.Normalized then
                              if item.Divergence.IsSome then yield EqualDecisionHasDivergence item.Item
                          else
                              match item.Divergence with
                              | None -> yield UnclassifiedFleetDivergence item.Item
                              | Some divergence when not (validText divergence.AccountableAgent) || not (validText divergence.Evidence) ->
                                  yield InvalidFleetDivergenceEvidence item.Item
                              | Some _ -> () ])
            let globalItemRefusals =
                observation.Repositories
                |> List.collect _.Items
                |> List.map _.Item
                |> duplicates
                |> List.map DuplicateFleetItem
            let refusals = structural @ itemRefusals @ globalItemRefusals
            if not refusals.IsEmpty then Error refusals
            else
                let items = observation.Repositories |> List.collect _.Items
                let equal = items |> List.filter (fun item -> item.V1.Normalized = item.V2.Normalized) |> List.length
                let classified = items.Length - equal
                Ok
                    { RosterRevision = observation.RosterRevision; RepositoryCount = observation.Repositories.Length
                      ItemCount = items.Length; EqualDecisionCount = equal; ClassifiedDivergenceCount = classified
                      UnexplainedDivergenceCount = 0; Seal = seal observation }

    let verify expectedSeal (asOf: DateTimeOffset) (maximumAge: TimeSpan) (observation: FleetShadowObservation) =
        match compare asOf maximumAge observation with
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredFleetShadowSeal ]
        | Error refusals -> Error refusals
