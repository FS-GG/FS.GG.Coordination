namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type LedgerProviderPayload =
    | RulesetsPage of EffectiveLedgerRuleset list
    | PhaseTagsPage of string list
    | EnvironmentState of LedgerObservation<string>
    | DedicatedWriterAppState of LedgerObservation<DedicatedLedgerApp>
    | ControlIssueState of LedgerObservation<int64>
type LedgerProviderPage =
    { Endpoint: string; Page: int; LastPage: int; HttpStatus: int; ObservedAt: DateTimeOffset
      PayloadSha256: string; Payload: LedgerProviderPayload }
type LedgerProviderObservation =
    { SchemaVersion: int; Repository: string; RepositoryId: int64; Revision: string
      PreviousObservationSha256: string option; Pages: LedgerProviderPage list }
type LedgerProviderFinding =
    | UnsupportedLedgerProviderSchema of int | InvalidLedgerProviderBinding of string
    | IncompleteLedgerProviderPagination of string | StaleLedgerProviderPage of string
    | ContradictoryLedgerProviderPage of string | UnknownLedgerProviderResource of string
    | AlteredLedgerProviderPayload of string
    | LedgerProviderPlanRefused of LedgerProtectionFinding list

module LedgerProtectionProviderAdapter =
    let rulesetsEndpoint = "GET /repos/FS-GG/FS.GG.Coordination/rulesets"
    let phaseTagsEndpoint = "GET /repos/FS-GG/FS.GG.Coordination/git/matching-refs/tags/fsgg/v2/epoch/fs-gg-production/phase/"
    let environmentEndpoint = "GET /repos/FS-GG/FS.GG.Coordination/environments/fleet-cutover"
    let dedicatedWriterAppEndpoint = "GET /orgs/FS-GG/installations/dedicated-ledger-writer"
    let controlIssueEndpoint = "GET /repos/FS-GG/FS.GG.Coordination/issues/2964"
    let private digest (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value:string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private ruleText = function Creation -> "creation" | Update -> "update" | Deletion -> "deletion" | NonFastForward -> "non-fast-forward"
    let private actorText = function App id -> $"app:{id}" | OrganizationAdmin -> "organization-admin"
    let private observationText render = function Observed value -> "observed:" + render value | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
    let private rulesetText r =
        [ string r.Id; r.Name; String.concat "," (List.sort r.Includes); String.concat "," (List.sort r.Excludes)
          r.Rules |> List.map ruleText |> List.sort |> String.concat ","; r.Bypass |> List.map actorText |> List.sort |> String.concat "," ] |> String.concat "|"
    let private payloadText = function
        | RulesetsPage values -> "rulesets\n" + (values |> List.sortBy _.Id |> List.map rulesetText |> String.concat "\n")
        | PhaseTagsPage values -> "phase-tags\n" + String.concat "\n" (List.sort values)
        | EnvironmentState state -> "environment\n" + observationText id state
        | DedicatedWriterAppState state ->
            let render value =
                let permissions = String.concat "," (List.sort value.AdditionalWritePermissions)
                $"{value.Id}|{value.ContentsPermission}|{permissions}"
            "dedicated-app\n" + observationText render state
        | ControlIssueState state -> "control-issue\n" + observationText string state
    let payloadSha256 payload = payloadText payload |> digest
    let private expectedEndpoint = function
        | RulesetsPage _ -> rulesetsEndpoint | PhaseTagsPage _ -> phaseTagsEndpoint
        | EnvironmentState _ -> environmentEndpoint | DedicatedWriterAppState _ -> dedicatedWriterAppEndpoint
        | ControlIssueState _ -> controlIssueEndpoint
    let private digestLike (value:string) = value.Length = 64 && Seq.forall Uri.IsHexDigit value
    let private revisionLike (value:string) = value.Length = 40 && Seq.forall Uri.IsHexDigit value
    let private statusMatches (page:LedgerProviderPage) =
        match page.Payload with
        | RulesetsPage _ | PhaseTagsPage _ -> page.HttpStatus = 200
        | EnvironmentState (Observed _) | DedicatedWriterAppState (Observed _) | ControlIssueState (Observed _) -> page.HttpStatus = 200
        | EnvironmentState ProvenAbsent | DedicatedWriterAppState ProvenAbsent | ControlIssueState ProvenAbsent -> page.HttpStatus = 404
        | EnvironmentState (Unknown _) | DedicatedWriterAppState (Unknown _) | ControlIssueState (Unknown _) -> false
    let private isUnknown = function
        | EnvironmentState (Unknown _) | DedicatedWriterAppState (Unknown _) | ControlIssueState (Unknown _) -> true
        | _ -> false
    let normalize asOf maxAge observation =
        let groups = observation.Pages |> List.groupBy _.Endpoint |> Map.ofList
        let expected = [ rulesetsEndpoint; phaseTagsEndpoint; environmentEndpoint; dedicatedWriterAppEndpoint; controlIssueEndpoint ]
        let findings =
            [ if observation.SchemaVersion <> 1 then UnsupportedLedgerProviderSchema observation.SchemaVersion
              if observation.Repository <> "FS-GG/FS.GG.Coordination" || observation.RepositoryId <= 0L then InvalidLedgerProviderBinding "repository"
              if not (revisionLike observation.Revision) then InvalidLedgerProviderBinding "revision"
              match observation.PreviousObservationSha256 with Some value when digestLike value -> () | _ -> InvalidLedgerProviderBinding "previousObservationSha256"
              for endpoint in expected do
                  match Map.tryFind endpoint groups with
                  | None -> IncompleteLedgerProviderPagination endpoint
                  | Some pages ->
                      let ordered = pages |> List.sortBy _.Page
                      let last = ordered |> List.tryHead |> Option.map _.LastPage |> Option.defaultValue 0
                      if last <= 0 || ordered.Length <> last || (ordered |> List.map _.Page) <> [1..last] || ordered |> List.exists (fun p -> p.LastPage <> last) then
                          IncompleteLedgerProviderPagination endpoint
                      if endpoint <> rulesetsEndpoint && endpoint <> phaseTagsEndpoint && (last <> 1 || ordered.Length <> 1) then
                          IncompleteLedgerProviderPagination endpoint
                      for page in ordered do
                          if not (statusMatches page) then InvalidLedgerProviderBinding ($"httpStatus:{endpoint}")
                          if isUnknown page.Payload then UnknownLedgerProviderResource endpoint
                          if page.ObservedAt > asOf || asOf - page.ObservedAt > maxAge then StaleLedgerProviderPage endpoint
                          if page.Endpoint <> expectedEndpoint page.Payload then ContradictoryLedgerProviderPage endpoint
                          if not (digestLike page.PayloadSha256) || not (String.Equals(payloadSha256 page.Payload, page.PayloadSha256, StringComparison.OrdinalIgnoreCase)) then AlteredLedgerProviderPayload endpoint
              for endpoint in Map.keys groups do if not (List.contains endpoint expected) then InvalidLedgerProviderBinding ($"endpoint:{endpoint}") ]
        if not findings.IsEmpty then Error findings else
        let pages endpoint = groups[endpoint] |> List.sortBy _.Page
        let rulesets = pages rulesetsEndpoint |> List.collect (fun p -> match p.Payload with RulesetsPage values -> values | _ -> [])
        let tags = pages phaseTagsEndpoint |> List.collect (fun p -> match p.Payload with PhaseTagsPage values -> values | _ -> [])
        let singleton endpoint pick =
            match pages endpoint with
            | [page] -> pick page.Payload
            | _ -> Unknown "provider-singleton-cardinality"
        let environment = singleton environmentEndpoint (function EnvironmentState value -> value | _ -> Unknown "provider-payload-kind")
        let app = singleton dedicatedWriterAppEndpoint (function DedicatedWriterAppState value -> value | _ -> Unknown "provider-payload-kind")
        let issue = singleton controlIssueEndpoint (function ControlIssueState value -> value | _ -> Unknown "provider-payload-kind")
        let ordered = expected |> List.collect pages
        let pageDigests = ordered |> List.map _.PayloadSha256
        let aggregate = pageDigests |> List.map frame |> String.concat "" |> digest
        Ok { SchemaVersion=1; Repository=observation.Repository; RepositoryId=observation.RepositoryId; Revision=observation.Revision
             ObservedAt=ordered |> List.map _.ObservedAt |> List.max; PagesComplete=true; PageSha256=pageDigests
             PageDigestSha256=aggregate; PreviousObservationSha256=observation.PreviousObservationSha256
             Rulesets=Observed rulesets; PhaseTags=if tags.IsEmpty then ProvenAbsent else Observed tags
             Environment=environment; DedicatedWriterApp=app; ControlIssue=issue }
    let compile asOf maxAge observation =
        match normalize asOf maxAge observation with
        | Error findings -> Error findings
        | Ok normalized ->
            match LedgerProtectionPlanAdapter.compile asOf maxAge normalized with
            | Ok plan -> Ok plan
            | Error findings -> Error [ LedgerProviderPlanRefused findings ]
