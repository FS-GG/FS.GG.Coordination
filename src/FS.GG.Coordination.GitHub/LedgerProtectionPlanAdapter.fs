namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type LedgerObservation<'a> = Observed of 'a | ProvenAbsent | Unknown of string
type LedgerRule = Creation | Update | Deletion | NonFastForward
type LedgerActor = App of int64 | OrganizationAdmin
type LedgerRulesetTarget = Branch | Tag
type LedgerRulesetEnforcement = Active | Evaluate | Disabled
type LedgerBypassMode = Always | PullRequest
type LedgerBypass = { Actor: LedgerActor; Mode: LedgerBypassMode }
type DedicatedLedgerApp = { Id: int64; ContentsPermission: string; AdditionalWritePermissions: string list }
type EffectiveLedgerRuleset =
    { Id: int64; Name: string; Target: LedgerRulesetTarget; Enforcement: LedgerRulesetEnforcement; Inherited: bool
      Includes: string list; Excludes: string list; Rules: LedgerRule list; Bypass: LedgerBypass list }
type LedgerProtectionObservation =
    { SchemaVersion: int; Repository: string; RepositoryId: int64; Revision: string; ObservedAt: DateTimeOffset
      PagesComplete: bool; PageSha256: string list; PageDigestSha256: string; PreviousObservationSha256: string option
      PreviousObservationEvidenceSha256: string option; ProviderEnvelopeSha256: string option
      ProviderRawSetSha256: string option; ProviderNormalizedSetSha256: string option
      Rulesets: LedgerObservation<EffectiveLedgerRuleset list>; PhaseTags: LedgerObservation<string list>
      Environment: LedgerObservation<string>; DedicatedWriterApp: LedgerObservation<DedicatedLedgerApp>; ControlIssue: LedgerObservation<int64> }
type LedgerProtectionIntent = { Kind: string; Target: string; Rules: LedgerRule list; Bypass: LedgerBypass list }
type LedgerProtectionPlan =
    { Repository: string; FleetRef: string; PhaseTagPattern: string; ApplyAuthorized: bool
      Intents: LedgerProtectionIntent list; ProductionBlockers: string list; ObservationSha256: string; Seal: string }
type LedgerProtectionFinding =
    | UnsupportedLedgerProtectionSchema of int | InvalidLedgerProtectionBinding of string
    | IncompleteLedgerProtectionObservation of string | StaleLedgerProtectionObservation
    | LedgerProtectionContinuityFailure | ContradictoryLedgerProtectionRules of string
    | DedicatedWriterIdentityMissing | SharedWriterStillCoversFleetRef | AlteredLedgerProtectionSeal

module LedgerProtectionPlanAdapter =
    let authorityRepository = "FS-GG/FS.GG.Coordination.Authority"
    let authorityRepositoryId = 1351660651L
    let fleetIdentity = "fleet-cutover:fs-gg-production"
    let fleetRef =
        match ShardedJournalAdapter.address Cutover fleetIdentity with
        | Ok address -> address.Ref
        | Error _ -> invalidOp "the canonical fleet identity must be addressable"
    let journalPattern = "refs/heads/fsgg/v2/journal/**/*"
    let phaseTagPattern = "refs/tags/fsgg/v2/fleet-cutover/**/*"
    let private digest (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value:string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digestLike (value:string) = value.Length = 64 && Seq.forall Uri.IsHexDigit value
    let private revisionLike (value:string) = value.Length = 40 && Seq.forall Uri.IsHexDigit value
    let private ruleText = function Creation -> "creation" | Update -> "update" | Deletion -> "deletion" | NonFastForward -> "non-fast-forward"
    let private actorText = function App id -> $"app:{id}" | OrganizationAdmin -> "organization-admin"
    let private targetText = function Branch -> "branch" | Tag -> "tag"
    let private enforcementText = function Active -> "active" | Evaluate -> "evaluate" | Disabled -> "disabled"
    let private bypassModeText = function Always -> "always" | PullRequest -> "pull-request"
    let private bypassText value = actorText value.Actor + ":" + bypassModeText value.Mode
    let private ruleSetText r =
        [ string r.Id; r.Name; targetText r.Target; enforcementText r.Enforcement; string r.Inherited
          String.concat "," (List.sort r.Includes); String.concat "," (List.sort r.Excludes)
          r.Rules |> List.map ruleText |> List.sort |> String.concat ","; r.Bypass |> List.map bypassText |> List.sort |> String.concat "," ] |> String.concat "|"
    let private observedText observation =
        let rules = match observation.Rulesets with Observed xs -> xs |> List.sortBy _.Id |> List.map ruleSetText |> String.concat ";" | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
        let tags = match observation.PhaseTags with Observed xs -> xs |> List.sort |> String.concat "," | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
        let state render = function Observed x -> "observed:" + render x | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
        String.concat "\n" [ observation.Repository; string observation.RepositoryId; observation.Revision; observation.ObservedAt.ToUniversalTime().ToString("O"); string observation.PagesComplete; String.concat "," observation.PageSha256; observation.PageDigestSha256; defaultArg observation.PreviousObservationSha256 ""; defaultArg observation.PreviousObservationEvidenceSha256 ""; defaultArg observation.ProviderEnvelopeSha256 ""; defaultArg observation.ProviderRawSetSha256 ""; defaultArg observation.ProviderNormalizedSetSha256 ""; rules; tags
                             state id observation.Environment; state (fun (x:DedicatedLedgerApp) -> sprintf "%d:%s:%s" x.Id x.ContentsPermission (String.concat "," x.AdditionalWritePermissions)) observation.DedicatedWriterApp; state string observation.ControlIssue ]
    let private supportedPatternMatches (value:string) pattern =
        if pattern = journalPattern then Some(value.StartsWith("refs/heads/fsgg/v2/journal/", StringComparison.Ordinal))
        elif pattern = phaseTagPattern then Some(value.StartsWith("refs/tags/fsgg/v2/fleet-cutover/", StringComparison.Ordinal))
        elif pattern.Contains("*") then None
        else Some(value = pattern)
    let private includes target (r:EffectiveLedgerRuleset) =
        List.exists (fun pattern -> supportedPatternMatches target pattern = Some true) r.Includes
        && not (List.exists (fun pattern -> supportedPatternMatches target pattern = Some true) r.Excludes)
    let compile asOf maxAge observation =
        let rules = match observation.Rulesets with Observed xs -> xs | _ -> []
        let sharedWriter = rules |> List.tryFind (fun r -> r.Id = 21872113L)
        let integrity = rules |> List.tryFind (fun r -> r.Id = 21872115L)
        let fleetComposition = rules |> List.filter (includes fleetRef)
        let exactValues expected actual = Set.ofList expected = Set.ofList actual && expected.Length = actual.Length
        let validBase (r:EffectiveLedgerRuleset) = r.Target=Branch && r.Enforcement=Active && not r.Inherited && r.Includes=[journalPattern]
        let unsupportedSelectors = rules |> List.collect (fun r -> r.Includes @ r.Excludes) |> List.filter (fun pattern -> pattern.Contains("*") && pattern <> journalPattern && pattern <> phaseTagPattern)
        let findings =
            [ if observation.SchemaVersion <> 1 then UnsupportedLedgerProtectionSchema observation.SchemaVersion
              if observation.Repository <> authorityRepository || observation.RepositoryId <> authorityRepositoryId then InvalidLedgerProtectionBinding "authority-repository"
              if not (revisionLike observation.Revision) then InvalidLedgerProtectionBinding "revision"
              if observation.PageSha256.IsEmpty || observation.PageSha256 |> List.exists (digestLike >> not) then InvalidLedgerProtectionBinding "pageSha256"
              if not (digestLike observation.PageDigestSha256) || digest (observation.PageSha256 |> List.map frame |> String.concat "") <> observation.PageDigestSha256 then InvalidLedgerProtectionBinding "pageDigestSha256"
              if not observation.PagesComplete then IncompleteLedgerProtectionObservation "pagination"
              if observation.ObservedAt > asOf || asOf - observation.ObservedAt > maxAge then StaleLedgerProtectionObservation
              match observation.PreviousObservationSha256, observation.PreviousObservationEvidenceSha256 with
              | Some declared, Some evidence when digestLike declared && String.Equals(declared,evidence,StringComparison.OrdinalIgnoreCase) -> ()
              | _ -> LedgerProtectionContinuityFailure
              match observation.ProviderEnvelopeSha256 with Some x when digestLike x -> () | Some _ -> InvalidLedgerProtectionBinding "providerEnvelopeSha256" | None -> ()
              match observation.ProviderRawSetSha256, observation.ProviderNormalizedSetSha256 with
              | None, None -> ()
              | Some raw, Some normalized when digestLike raw && digestLike normalized -> ()
              | _ -> InvalidLedgerProtectionBinding "provider-set-digests"
              match observation.Rulesets with Unknown _ -> IncompleteLedgerProtectionObservation "rulesets" | ProvenAbsent -> ContradictoryLedgerProtectionRules "namespace-integrity-absent" | _ -> ()
              match observation.PhaseTags with Unknown _ -> IncompleteLedgerProtectionObservation "phaseTags" | _ -> ()
              match observation.Environment with Unknown _ -> IncompleteLedgerProtectionObservation "environment" | _ -> ()
              match observation.ControlIssue with Unknown why when why <> "control-issue-unbound" -> IncompleteLedgerProtectionObservation "controlIssue" | _ -> ()
              match observation.DedicatedWriterApp with
              | Unknown why when why <> "dedicated-writer-unbound" -> IncompleteLedgerProtectionObservation "dedicatedWriterApp"
              | Observed app when app.Id <= 0L || app.ContentsPermission <> "write" || not app.AdditionalWritePermissions.IsEmpty -> InvalidLedgerProtectionBinding "dedicatedWriterApp"
              | _ -> ()
              if not unsupportedSelectors.IsEmpty then ContradictoryLedgerProtectionRules "unsupported-selector"
              match sharedWriter with
              | Some value when validBase value && value.Excludes=[] && exactValues [Creation;Update] value.Rules && exactValues [{Actor=App 4166418L;Mode=Always}] value.Bypass -> ()
              | _ -> ContradictoryLedgerProtectionRules "shared-writer"
              match integrity with
              | Some value when validBase value && value.Excludes=[] && exactValues [Deletion;NonFastForward] value.Rules && value.Bypass.IsEmpty -> ()
              | _ -> ContradictoryLedgerProtectionRules "namespace-integrity"
              if fleetComposition |> List.map _.Id |> Set.ofList <> Set.ofList [21872113L;21872115L] then ContradictoryLedgerProtectionRules "effective-composition"
              if fleetComposition |> List.groupBy _.Id |> List.exists (fun (_,xs) -> xs.Length > 1) then ContradictoryLedgerProtectionRules "duplicate-effective-ruleset" ]
        let writer = match observation.DedicatedWriterApp with Observed app -> [{Actor=App app.Id;Mode=Always}] | _ -> []
        let controlIssueTarget = match observation.ControlIssue with Observed number -> authorityRepository + "#" + string number | _ -> "unbound"
        let intents =
            [ { Kind="shared-writer-carve-out"; Target=fleetRef; Rules=[]; Bypass=[] }
              { Kind="dedicated-contents-writer"; Target=fleetRef; Rules=[ Creation; Update ]; Bypass=writer }
              { Kind="namespace-integrity"; Target=journalPattern; Rules=[ Deletion; NonFastForward ]; Bypass=[] }
              { Kind="phase-tag-create"; Target=phaseTagPattern; Rules=[ Creation ]; Bypass=writer }
              { Kind="phase-tag-integrity"; Target=phaseTagPattern; Rules=[ Update; Deletion ]; Bypass=[] }
              { Kind="protected-environment"; Target="FS-GG/.github:fleet-cutover"; Rules=[]; Bypass=[] }
              { Kind="control-issue"; Target=controlIssueTarget; Rules=[]; Bypass=[] } ]
        let blockers = [ if writer.IsEmpty then "dedicated contents-only GitHub App identity is missing or unbound"; match observation.ControlIssue with Observed _ -> () | _ -> "Authority control issue identity is unbound"; "shared journal App use remains a production blocker without later security acceptance" ]
        let observationSha = digest (observedText observation)
        let intentText i = String.concat "|" [i.Kind;i.Target;i.Rules |> List.map ruleText |> List.sort |> String.concat ",";i.Bypass |> List.map bypassText |> List.sort |> String.concat ","]
        let seal = digest (String.concat "\n" ([ observationSha; authorityRepository; string authorityRepositoryId; fleetRef; journalPattern; phaseTagPattern; "apply-authorized:false" ] @ (intents |> List.map intentText) @ blockers))
        if findings.IsEmpty then Ok { Repository=observation.Repository; FleetRef=fleetRef; PhaseTagPattern=phaseTagPattern; ApplyAuthorized=false; Intents=intents; ProductionBlockers=blockers; ObservationSha256=observationSha; Seal=seal }
        else Error findings
    let verify expectedSeal asOf maxAge observation =
        match compile asOf maxAge observation with
        | Ok plan when String.Equals(plan.Seal, expectedSeal, StringComparison.OrdinalIgnoreCase) -> Ok plan
        | Ok _ -> Error [ AlteredLedgerProtectionSeal ]
        | Error findings -> Error findings
