namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type LedgerProviderPayload =
    | RulesetsPage of EffectiveLedgerRuleset list
    | PhaseTagsPage of string list
    | EnvironmentsPage of string list
    | OrganizationInstallationsPage of LedgerProviderInstallation list
    | ControlIssuesPage of LedgerProviderIssue list
and LedgerProviderInstallation =
    { InstallationId: int64; AppId: int64; Slug: string; RepositorySelection: string
      Permissions: (string * string) list; SelectedRepositoriesEndpoint: string option; SelectedRepositoriesPagesComplete: bool
      SelectedRepositories: LedgerObservation<string list> }
and LedgerProviderIssue = { Number: int64; IsPullRequest: bool }
type LedgerProviderPage =
    { Endpoint: string; Page: int; LastPage: int; IsTerminal: bool; HttpStatus: int; ObservedAt: DateTimeOffset
      PayloadSha256: string; Payload: LedgerProviderPayload }
type LedgerProviderObservation =
    { SchemaVersion: int; Repository: string; RepositoryId: int64; Revision: string
      PreviousObservationSha256: string option; PreviousObservationEvidenceSha256: string option
      DedicatedWriterAppId: int64 option; ControlIssueNumber: int64 option; Pages: LedgerProviderPage list }
type LedgerProviderFinding =
    | UnsupportedLedgerProviderSchema of int | InvalidLedgerProviderBinding of string
    | IncompleteLedgerProviderPagination of string | StaleLedgerProviderPage of string
    | ContradictoryLedgerProviderPage of string | UnknownLedgerProviderResource of string
    | AlteredLedgerProviderPayload of string
    | LedgerProviderPlanRefused of LedgerProtectionFinding list

module LedgerProtectionProviderAdapter =
    let rulesetsEndpoint = "GET /repos/FS-GG/FS.GG.Coordination.Authority/rulesets?per_page=100"
    let phaseTagsEndpoint = "GET /repos/FS-GG/FS.GG.Coordination.Authority/git/matching-refs/tags/fsgg/v2/fleet-cutover/?per_page=100"
    let environmentEndpoint = "GET /repos/FS-GG/.github/environments?per_page=100"
    let installationsEndpoint = "GET /orgs/FS-GG/installations?per_page=100"
    let controlIssuesEndpoint = "GET /repos/FS-GG/FS.GG.Coordination.Authority/issues?state=all&per_page=100"
    let private digest (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value:string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private ruleText = function Creation -> "creation" | Update -> "update" | Deletion -> "deletion" | NonFastForward -> "non-fast-forward"
    let private actorText = function App id -> $"app:{id}" | OrganizationAdmin -> "organization-admin"
    let private targetText = function Branch -> "branch" | Tag -> "tag"
    let private enforcementText = function Active -> "active" | Evaluate -> "evaluate" | Disabled -> "disabled"
    let private bypassText value = actorText value.Actor + ":" + (match value.Mode with Always -> "always" | PullRequest -> "pull-request")
    let private observationText render = function Observed value -> "observed:" + render value | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
    let private rulesetText r =
        [ string r.Id; r.Name; targetText r.Target; enforcementText r.Enforcement; string r.Inherited
          String.concat "," (List.sort r.Includes); String.concat "," (List.sort r.Excludes)
          r.Rules |> List.map ruleText |> List.sort |> String.concat ","; r.Bypass |> List.map bypassText |> List.sort |> String.concat "," ] |> String.concat "|"
    let private payloadText = function
        | RulesetsPage values -> "rulesets\n" + (values |> List.sortBy _.Id |> List.map rulesetText |> String.concat "\n")
        | PhaseTagsPage values -> "phase-tags\n" + String.concat "\n" (List.sort values)
        | EnvironmentsPage values -> "environments\n" + String.concat "\n" (List.sort values)
        | OrganizationInstallationsPage values ->
            let render value =
                let repositories = observationText (List.sort >> String.concat ",") value.SelectedRepositories
                let permissions = value.Permissions |> List.sort |> List.map (fun (name,access) -> name+":"+access) |> String.concat ","
                let endpoint = defaultArg value.SelectedRepositoriesEndpoint ""
                $"{value.InstallationId}|{value.AppId}|{value.Slug}|{value.RepositorySelection}|{permissions}|{endpoint}|{value.SelectedRepositoriesPagesComplete}|{repositories}"
            "installations\n" + (values |> List.sortBy _.InstallationId |> List.map render |> String.concat "\n")
        | ControlIssuesPage values ->
            "control-issues\n" + (values |> List.sortBy _.Number |> List.map (fun value -> $"{value.Number}|{value.IsPullRequest}") |> String.concat "\n")
    let payloadSha256 payload = payloadText payload |> digest
    let private expectedEndpoint = function
        | RulesetsPage _ -> rulesetsEndpoint | PhaseTagsPage _ -> phaseTagsEndpoint
        | EnvironmentsPage _ -> environmentEndpoint | OrganizationInstallationsPage _ -> installationsEndpoint
        | ControlIssuesPage _ -> controlIssuesEndpoint
    let private digestLike (value:string) = value.Length = 64 && Seq.forall Uri.IsHexDigit value
    let private revisionLike (value:string) = value.Length = 40 && Seq.forall Uri.IsHexDigit value
    let private statusMatches (page:LedgerProviderPage) = page.HttpStatus = 200
    let normalize asOf maxAge observation =
        let groups = observation.Pages |> List.groupBy _.Endpoint |> Map.ofList
        let expected = [ rulesetsEndpoint; phaseTagsEndpoint; environmentEndpoint; installationsEndpoint; controlIssuesEndpoint ]
        let findings =
            [ if observation.SchemaVersion <> 1 then UnsupportedLedgerProviderSchema observation.SchemaVersion
              if observation.Repository <> LedgerProtectionPlanAdapter.authorityRepository || observation.RepositoryId <> LedgerProtectionPlanAdapter.authorityRepositoryId then InvalidLedgerProviderBinding "authority-repository"
              if not (revisionLike observation.Revision) then InvalidLedgerProviderBinding "revision"
              match observation.PreviousObservationSha256, observation.PreviousObservationEvidenceSha256 with
              | Some declared, Some evidence when digestLike declared && String.Equals(declared,evidence,StringComparison.OrdinalIgnoreCase) -> ()
              | _ -> InvalidLedgerProviderBinding "previousObservationContinuity"
              for endpoint in expected do
                  match Map.tryFind endpoint groups with
                  | None -> IncompleteLedgerProviderPagination endpoint
                  | Some pages ->
                      let ordered = pages |> List.sortBy _.Page
                      let last = ordered |> List.tryHead |> Option.map _.LastPage |> Option.defaultValue 0
                      if last <= 0 || ordered.Length <> last || (ordered |> List.map _.Page) <> [1..last] || ordered |> List.exists (fun p -> p.LastPage <> last || p.IsTerminal <> (p.Page=last)) then
                          IncompleteLedgerProviderPagination endpoint
                      for page in ordered do
                          if not (statusMatches page) then InvalidLedgerProviderBinding ($"httpStatus:{endpoint}")
                          if page.ObservedAt > asOf || asOf - page.ObservedAt > maxAge then StaleLedgerProviderPage endpoint
                          if page.Endpoint <> expectedEndpoint page.Payload then ContradictoryLedgerProviderPage endpoint
                          if not (digestLike page.PayloadSha256) || not (String.Equals(payloadSha256 page.Payload, page.PayloadSha256, StringComparison.OrdinalIgnoreCase)) then AlteredLedgerProviderPayload endpoint
              for endpoint in Map.keys groups do if not (List.contains endpoint expected) then InvalidLedgerProviderBinding ($"endpoint:{endpoint}") ]
        let allRulesets = observation.Pages |> List.collect (fun page -> match page.Payload with RulesetsPage values -> values | _ -> [])
        let allTags = observation.Pages |> List.collect (fun page -> match page.Payload with PhaseTagsPage values -> values | _ -> [])
        let allInstallations = observation.Pages |> List.collect (fun page -> match page.Payload with OrganizationInstallationsPage values -> values | _ -> [])
        let allIssues = observation.Pages |> List.collect (fun page -> match page.Payload with ControlIssuesPage values -> values | _ -> [])
        let duplicateBy key values = values |> List.groupBy key |> List.exists (fun (_, matches) -> matches.Length <> 1)
        let findings =
            findings @
            [ if duplicateBy _.Id allRulesets then ContradictoryLedgerProviderPage rulesetsEndpoint
              if allTags |> List.exists (fun value -> not (value.StartsWith("refs/tags/fsgg/v2/fleet-cutover/", StringComparison.Ordinal))) || duplicateBy id allTags then ContradictoryLedgerProviderPage phaseTagsEndpoint
              if duplicateBy _.InstallationId allInstallations then ContradictoryLedgerProviderPage installationsEndpoint
              if duplicateBy _.Number allIssues then ContradictoryLedgerProviderPage controlIssuesEndpoint ]
        if not findings.IsEmpty then Error findings else
        let pages endpoint = groups[endpoint] |> List.sortBy _.Page
        let rulesets = pages rulesetsEndpoint |> List.collect (fun p -> match p.Payload with RulesetsPage values -> values | _ -> [])
        let tags = pages phaseTagsEndpoint |> List.collect (fun p -> match p.Payload with PhaseTagsPage values -> values | _ -> [])
        let environments = pages environmentEndpoint |> List.collect (fun p -> match p.Payload with EnvironmentsPage values -> values | _ -> [])
        let installations = pages installationsEndpoint |> List.collect (fun p -> match p.Payload with OrganizationInstallationsPage values -> values | _ -> [])
        let issues = pages controlIssuesEndpoint |> List.collect (fun p -> match p.Payload with ControlIssuesPage values -> values | _ -> [])
        let environment = if List.contains "fleet-cutover" environments then Observed "fleet-cutover" else ProvenAbsent
        let app =
            match observation.DedicatedWriterAppId with
            | None -> Unknown "dedicated-writer-unbound"
            | Some id ->
                match installations |> List.filter (fun value -> value.AppId=id) with
                | [value] when value.RepositorySelection="selected" && value.Permissions=[("contents","write")] && value.SelectedRepositoriesPagesComplete && value.SelectedRepositoriesEndpoint=Some($"GET /user/installations/{value.InstallationId}/repositories?per_page=100") ->
                    match value.SelectedRepositories with
                    | Observed repositories when List.contains LedgerProtectionPlanAdapter.authorityRepository repositories -> Observed {Id=id;ContentsPermission="write";AdditionalWritePermissions=[]}
                    | Unknown why -> Unknown why
                    | _ -> ProvenAbsent
                | [] -> ProvenAbsent
                | _ -> Unknown "dedicated-writer-ambiguous"
        let issue =
            match observation.ControlIssueNumber with
            | None -> Unknown "control-issue-unbound"
            | Some number ->
                match issues |> List.filter (fun value -> value.Number=number) with
                | [{IsPullRequest=false}] -> Observed number
                | [] -> ProvenAbsent
                | _ -> Unknown "control-issue-ambiguous-or-pull-request"
        let ordered = expected |> List.collect pages
        let pageDigests = ordered |> List.map _.PayloadSha256
        let aggregate = pageDigests |> List.map frame |> String.concat "" |> digest
        let envelope =
            let pageText page = String.concat "|" [page.Endpoint;string page.Page;string page.LastPage;string page.IsTerminal;string page.HttpStatus;page.ObservedAt.ToUniversalTime().ToString("O");page.PayloadSha256]
            [ observation.Repository; string observation.RepositoryId; observation.Revision; defaultArg observation.PreviousObservationSha256 ""; defaultArg observation.PreviousObservationEvidenceSha256 ""; observation.DedicatedWriterAppId |> Option.map string |> Option.defaultValue ""; observation.ControlIssueNumber |> Option.map string |> Option.defaultValue ""
              yield! ordered |> List.map pageText ] |> List.map frame |> String.concat "" |> digest
        Ok { SchemaVersion=1; Repository=observation.Repository; RepositoryId=observation.RepositoryId; Revision=observation.Revision
             ObservedAt=ordered |> List.map _.ObservedAt |> List.max; PagesComplete=true; PageSha256=pageDigests
             PageDigestSha256=aggregate; PreviousObservationSha256=observation.PreviousObservationSha256; PreviousObservationEvidenceSha256=observation.PreviousObservationEvidenceSha256; ProviderEnvelopeSha256=Some envelope
             Rulesets=Observed rulesets; PhaseTags=if tags.IsEmpty then ProvenAbsent else Observed tags
             Environment=environment; DedicatedWriterApp=app; ControlIssue=issue }
    let compile asOf maxAge observation =
        match normalize asOf maxAge observation with
        | Error findings -> Error findings
        | Ok normalized ->
            match LedgerProtectionPlanAdapter.compile asOf maxAge normalized with
            | Ok plan -> Ok plan
            | Error findings -> Error [ LedgerProviderPlanRefused findings ]
