namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type LedgerObservation<'a> = Observed of 'a | ProvenAbsent | Unknown of string
type LedgerRule = Creation | Update | Deletion | NonFastForward
type LedgerActor = App of int64 | OrganizationAdmin
type DedicatedLedgerApp = { Id: int64; ContentsPermission: string; AdditionalWritePermissions: string list }
type EffectiveLedgerRuleset = { Id: int64; Name: string; Includes: string list; Excludes: string list; Rules: LedgerRule list; Bypass: LedgerActor list }
type LedgerProtectionObservation =
    { SchemaVersion: int; Repository: string; RepositoryId: int64; Revision: string; ObservedAt: DateTimeOffset
      PagesComplete: bool; PageSha256: string list; PageDigestSha256: string; PreviousObservationSha256: string option
      Rulesets: LedgerObservation<EffectiveLedgerRuleset list>; PhaseTags: LedgerObservation<string list>
      Environment: LedgerObservation<string>; DedicatedWriterApp: LedgerObservation<DedicatedLedgerApp>; ControlIssue: LedgerObservation<int64> }
type LedgerProtectionIntent = { Kind: string; Target: string; Rules: LedgerRule list; Bypass: LedgerActor list }
type LedgerProtectionPlan =
    { Repository: string; FleetRef: string; PhaseTagPattern: string; ApplyAuthorized: bool
      Intents: LedgerProtectionIntent list; ProductionBlockers: string list; ObservationSha256: string; Seal: string }
type LedgerProtectionFinding =
    | UnsupportedLedgerProtectionSchema of int | InvalidLedgerProtectionBinding of string
    | IncompleteLedgerProtectionObservation of string | StaleLedgerProtectionObservation
    | LedgerProtectionContinuityFailure | ContradictoryLedgerProtectionRules of string
    | DedicatedWriterIdentityMissing | SharedWriterStillCoversFleetRef | AlteredLedgerProtectionSeal

module LedgerProtectionPlanAdapter =
    let fleetIdentity = "fleet-cutover:fs-gg-production"
    let fleetRef =
        match ShardedJournalAdapter.address Cutover fleetIdentity with
        | Ok address -> address.Ref
        | Error _ -> invalidOp "the canonical fleet identity must be addressable"
    let phaseTagPattern = "refs/tags/fsgg/v2/epoch/fs-gg-production/phase/*"
    let private digest (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value:string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digestLike (value:string) = value.Length = 64 && Seq.forall Uri.IsHexDigit value
    let private revisionLike (value:string) = value.Length = 40 && Seq.forall Uri.IsHexDigit value
    let private ruleText = function Creation -> "creation" | Update -> "update" | Deletion -> "deletion" | NonFastForward -> "non-fast-forward"
    let private actorText = function App id -> $"app:{id}" | OrganizationAdmin -> "organization-admin"
    let private ruleSetText r =
        [ string r.Id; r.Name; String.concat "," (List.sort r.Includes); String.concat "," (List.sort r.Excludes)
          r.Rules |> List.map ruleText |> List.sort |> String.concat ","; r.Bypass |> List.map actorText |> List.sort |> String.concat "," ] |> String.concat "|"
    let private observedText observation =
        let rules = match observation.Rulesets with Observed xs -> xs |> List.sortBy _.Id |> List.map ruleSetText |> String.concat ";" | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
        let tags = match observation.PhaseTags with Observed xs -> xs |> List.sort |> String.concat "," | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
        let state render = function Observed x -> "observed:" + render x | ProvenAbsent -> "absent" | Unknown why -> "unknown:" + why
        String.concat "\n" [ observation.Repository; string observation.RepositoryId; observation.Revision; observation.ObservedAt.ToUniversalTime().ToString("O"); string observation.PagesComplete; String.concat "," observation.PageSha256; observation.PageDigestSha256; defaultArg observation.PreviousObservationSha256 ""; rules; tags
                             state id observation.Environment; state (fun (x:DedicatedLedgerApp) -> sprintf "%d:%s:%s" x.Id x.ContentsPermission (String.concat "," x.AdditionalWritePermissions)) observation.DedicatedWriterApp; state string observation.ControlIssue ]
    let private patternMatches (value:string) (pattern:string) =
        if pattern.EndsWith("/**", StringComparison.Ordinal) then value.StartsWith(pattern.Substring(0, pattern.Length-2), StringComparison.Ordinal)
        elif pattern.EndsWith("/*", StringComparison.Ordinal) then value.StartsWith(pattern.Substring(0, pattern.Length-1), StringComparison.Ordinal)
        else value = pattern
    let private includes target (r:EffectiveLedgerRuleset) = List.exists (patternMatches target) r.Includes && not (List.exists (patternMatches target) r.Excludes)
    let compile asOf maxAge observation =
        let rules = match observation.Rulesets with Observed xs -> xs | _ -> []
        let sharedWriter = rules |> List.tryFind (fun r -> r.Id = 21872113L)
        let integrity = rules |> List.filter (fun r -> List.contains "refs/heads/fsgg/v2/journal/**" r.Includes && not (List.contains "refs/heads/fsgg/v2/journal/**" r.Excludes))
        let fleetComposition = rules |> List.filter (includes fleetRef)
        let required = [ Creation; Update; Deletion; NonFastForward ] |> Set.ofList
        let findings =
            [ if observation.SchemaVersion <> 1 then UnsupportedLedgerProtectionSchema observation.SchemaVersion
              if observation.Repository <> "FS-GG/FS.GG.Coordination" || observation.RepositoryId <= 0L then InvalidLedgerProtectionBinding "repository"
              if not (revisionLike observation.Revision) then InvalidLedgerProtectionBinding "revision"
              if observation.PageSha256.IsEmpty || observation.PageSha256 |> List.exists (digestLike >> not) then InvalidLedgerProtectionBinding "pageSha256"
              if not (digestLike observation.PageDigestSha256) || digest (observation.PageSha256 |> List.map frame |> String.concat "") <> observation.PageDigestSha256 then InvalidLedgerProtectionBinding "pageDigestSha256"
              if not observation.PagesComplete then IncompleteLedgerProtectionObservation "pagination"
              if observation.ObservedAt > asOf || asOf - observation.ObservedAt > maxAge then StaleLedgerProtectionObservation
              match observation.PreviousObservationSha256 with Some x when digestLike x -> () | _ -> LedgerProtectionContinuityFailure
              match observation.Rulesets with Unknown _ -> IncompleteLedgerProtectionObservation "rulesets" | ProvenAbsent -> ContradictoryLedgerProtectionRules "namespace-integrity-absent" | _ -> ()
              match observation.PhaseTags with Unknown _ -> IncompleteLedgerProtectionObservation "phaseTags" | _ -> ()
              match observation.Environment with Unknown _ -> IncompleteLedgerProtectionObservation "environment" | _ -> ()
              match observation.ControlIssue with Unknown _ -> IncompleteLedgerProtectionObservation "controlIssue" | _ -> ()
              match observation.DedicatedWriterApp with Observed app when app.Id <= 0L || app.ContentsPermission <> "write" || not app.AdditionalWritePermissions.IsEmpty -> InvalidLedgerProtectionBinding "dedicatedWriterApp" | _ -> ()
              let effective = integrity |> List.collect _.Rules |> Set.ofList
              if integrity.IsEmpty || not (Set.isSubset required effective) then ContradictoryLedgerProtectionRules "namespace-integrity"
              if fleetComposition |> List.groupBy _.Id |> List.exists (fun (_,xs) -> xs.Length > 1) then ContradictoryLedgerProtectionRules "duplicate-effective-ruleset" ]
        let writer = match observation.DedicatedWriterApp with Observed app -> [ App app.Id ] | _ -> []
        let intents =
            [ { Kind="shared-writer-carve-out"; Target=fleetRef; Rules=[]; Bypass=[] }
              { Kind="dedicated-contents-writer"; Target=fleetRef; Rules=[ Creation; Update ]; Bypass=writer }
              { Kind="namespace-integrity"; Target="refs/heads/fsgg/v2/journal/**"; Rules=[ Deletion; NonFastForward ]; Bypass=[] }
              { Kind="phase-tag-create"; Target=phaseTagPattern; Rules=[ Creation ]; Bypass=writer }
              { Kind="phase-tag-integrity"; Target=phaseTagPattern; Rules=[ Update; Deletion ]; Bypass=[] }
              { Kind="protected-environment"; Target="fleet-cutover"; Rules=[]; Bypass=[] }
              { Kind="control-issue"; Target="fleet-cutover"; Rules=[]; Bypass=[] } ]
        let blockers = [ if writer.IsEmpty then "dedicated contents-only GitHub App identity is missing"; "shared journal App use remains a production blocker without later security acceptance" ]
        let observationSha = digest (observedText observation)
        let seal = digest (String.concat "\n" ([ observationSha; fleetRef; phaseTagPattern; "apply-authorized:false" ] @ (intents |> List.map (fun i -> i.Kind + ":" + i.Target)) @ blockers))
        if findings.IsEmpty then Ok { Repository=observation.Repository; FleetRef=fleetRef; PhaseTagPattern=phaseTagPattern; ApplyAuthorized=false; Intents=intents; ProductionBlockers=blockers; ObservationSha256=observationSha; Seal=seal }
        else Error findings
    let verify expectedSeal asOf maxAge observation =
        match compile asOf maxAge observation with
        | Ok plan when String.Equals(plan.Seal, expectedSeal, StringComparison.OrdinalIgnoreCase) -> Ok plan
        | Ok _ -> Error [ AlteredLedgerProtectionSeal ]
        | Error findings -> Error findings
