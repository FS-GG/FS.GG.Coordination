namespace FS.GG.Coordination.Core

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type SchedulingIntent = Backlog | Ready | Paused | Cancelled

[<RequireQualifiedAccess>]
type LifecycleStatus = Backlog | Ready | Blocked | Done

[<RequireQualifiedAccess>]
type HoldReason = NotYetActionable | Dependency | Decision | External | Operator

[<RequireQualifiedAccess>]
type WorkPriority = Critical | High | Normal | Low

[<RequireQualifiedAccess>]
type WorkEffort = S | M | L | XL

[<RequireQualifiedAccess>]
type WorkSeverity = Critical | High | Medium | Low | Unset

[<RequireQualifiedAccess>]
type WorkPhase = Planning | Execution | Verification | Operations

[<RequireQualifiedAccess>]
type Workstream = Composition | Coordination | Docs | Governance | Lifecycle | Versioning

type OrganizationIssueFieldObservation =
    { StableRowId: string; Revision: string; RepositoryScope: string; NativeIssueType: string
      SchedulingIntent: string option; LifecycleStatus: string option; HoldReason: string option
      Priority: string option; Effort: string option; StartDate: string option; TargetDate: string option
      Severity: string option; Phase: string option; Workstream: string option
      ContractReference: string option; ContractAuthorityDigest: string option
      TouchSet: string list; TouchSetAuthorityDigest: string option
      HierarchyPresent: bool; HierarchyPreservable: bool; Dependencies: string list
      DependenciesPreservable: bool; RepositoryScopePreservable: bool; LifecycleExempt: bool
      Complete: bool; Current: bool; Readable: bool }

[<RequireQualifiedAccess>]
type OrganizationIssueFieldDiagnostic =
    | UnreadableObservation | IncompleteObservation | StaleObservation
    | MissingStableRowId | MissingRevision | MissingRepositoryScope | MissingNativeIssueType
    | MissingSchedulingIntent | UnknownSchedulingIntent of string
    | MissingLifecycleStatus | UnknownLifecycleStatus of string | IntentStatusAuthorityConflict
    | MissingHoldReason | UnexpectedHoldReason | UnknownHoldReason of string
    | MissingPriority | UnknownPriority of string | MissingEffort | UnknownEffort of string
    | InvalidStartDate | InvalidTargetDate | ReversedDateRange
    | MissingSeverity | UnknownSeverity of string | MissingPhase | UnknownPhase of string
    | MissingWorkstream | UnknownWorkstream of string
    | InvalidContractReference | UnboundContractProjection
    | NoncanonicalTouchSet | UnboundTouchSetProjection
    | LossyHierarchy | LossyDependencies | LossyRepositoryScope | DuplicateStableRowId

type NormalizedOrganizationIssueFields =
    { SchedulingIntent: SchedulingIntent; LifecycleStatus: LifecycleStatus; HoldReason: HoldReason option
      Priority: WorkPriority; Effort: WorkEffort; StartDate: string option; TargetDate: string option
      Severity: WorkSeverity; Phase: WorkPhase; Workstream: Workstream
      ContractReference: string option; TouchSet: string list; TouchSetDigest: string option }

type OrganizationIssueFieldDisposition =
    { StableRowId: string; PrestateFingerprint: string; Fields: NormalizedOrganizationIssueFields
      RepositoryScope: string; NativeIssueType: string; HierarchyPreserved: bool
      DependenciesPreserved: bool; RepositoryScopePreserved: bool; LifecycleExempt: bool; NoOp: bool }

type OrganizationIssueFieldRefusal =
    { StableRowId: string option; Diagnostics: OrganizationIssueFieldDiagnostic list }

[<RequireQualifiedAccess>]
module OrganizationIssueFields =
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private optionFrame = Option.defaultValue "" >> frame
    let private strings values = values |> List.map frame |> String.concat ""
    let private boolText value = if value then "true" else "false"
    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private normalize = Option.bind (fun (value: string) -> let value = value.Trim() in if value = "" then None else Some(value.ToLowerInvariant()))
    let private isDigest (value: string) = value.Length = 64 && value |> Seq.forall (fun c -> Char.IsAsciiHexDigit c && not (Char.IsUpper c))

    let schedulingIntentName = function SchedulingIntent.Backlog -> "Backlog" | SchedulingIntent.Ready -> "Ready" | SchedulingIntent.Paused -> "Paused" | SchedulingIntent.Cancelled -> "Cancelled"
    let lifecycleStatusName = function LifecycleStatus.Backlog -> "Backlog" | LifecycleStatus.Ready -> "Ready" | LifecycleStatus.Blocked -> "Blocked" | LifecycleStatus.Done -> "Done"
    let private holdName = function HoldReason.NotYetActionable -> "not-yet-actionable" | HoldReason.Dependency -> "dependency" | HoldReason.Decision -> "decision" | HoldReason.External -> "external" | HoldReason.Operator -> "operator"
    let private priorityName = function WorkPriority.Critical -> "Critical" | WorkPriority.High -> "High" | WorkPriority.Normal -> "Normal" | WorkPriority.Low -> "Low"
    let private effortName = function WorkEffort.S -> "S" | WorkEffort.M -> "M" | WorkEffort.L -> "L" | WorkEffort.XL -> "XL"
    let private severityName = function WorkSeverity.Critical -> "Critical" | WorkSeverity.High -> "High" | WorkSeverity.Medium -> "Medium" | WorkSeverity.Low -> "Low" | WorkSeverity.Unset -> "Unset"
    let private phaseName = function WorkPhase.Planning -> "Planning" | WorkPhase.Execution -> "Execution" | WorkPhase.Verification -> "Verification" | WorkPhase.Operations -> "Operations"
    let private workstreamName = function Workstream.Composition -> "Composition" | Workstream.Coordination -> "Coordination" | Workstream.Docs -> "Docs" | Workstream.Governance -> "Governance" | Workstream.Lifecycle -> "Lifecycle" | Workstream.Versioning -> "Versioning"

    let diagnosticCode = function
        | OrganizationIssueFieldDiagnostic.UnreadableObservation -> "OIF-UNREADABLE" | OrganizationIssueFieldDiagnostic.IncompleteObservation -> "OIF-INCOMPLETE" | OrganizationIssueFieldDiagnostic.StaleObservation -> "OIF-STALE"
        | OrganizationIssueFieldDiagnostic.MissingStableRowId -> "OIF-MISSING-STABLE-ID" | OrganizationIssueFieldDiagnostic.MissingRevision -> "OIF-MISSING-REVISION" | OrganizationIssueFieldDiagnostic.MissingRepositoryScope -> "OIF-MISSING-REPOSITORY-SCOPE" | OrganizationIssueFieldDiagnostic.MissingNativeIssueType -> "OIF-MISSING-NATIVE-TYPE"
        | OrganizationIssueFieldDiagnostic.MissingSchedulingIntent -> "OIF-MISSING-INTENT" | OrganizationIssueFieldDiagnostic.UnknownSchedulingIntent v -> $"OIF-UNKNOWN-INTENT:{v}"
        | OrganizationIssueFieldDiagnostic.MissingLifecycleStatus -> "OIF-MISSING-STATUS" | OrganizationIssueFieldDiagnostic.UnknownLifecycleStatus v -> $"OIF-UNKNOWN-STATUS:{v}" | OrganizationIssueFieldDiagnostic.IntentStatusAuthorityConflict -> "OIF-INTENT-STATUS-AUTHORITY"
        | OrganizationIssueFieldDiagnostic.MissingHoldReason -> "OIF-MISSING-HOLD" | OrganizationIssueFieldDiagnostic.UnexpectedHoldReason -> "OIF-UNEXPECTED-HOLD" | OrganizationIssueFieldDiagnostic.UnknownHoldReason v -> $"OIF-UNKNOWN-HOLD:{v}"
        | OrganizationIssueFieldDiagnostic.MissingPriority -> "OIF-MISSING-PRIORITY" | OrganizationIssueFieldDiagnostic.UnknownPriority v -> $"OIF-UNKNOWN-PRIORITY:{v}" | OrganizationIssueFieldDiagnostic.MissingEffort -> "OIF-MISSING-EFFORT" | OrganizationIssueFieldDiagnostic.UnknownEffort v -> $"OIF-UNKNOWN-EFFORT:{v}"
        | OrganizationIssueFieldDiagnostic.InvalidStartDate -> "OIF-INVALID-START-DATE" | OrganizationIssueFieldDiagnostic.InvalidTargetDate -> "OIF-INVALID-TARGET-DATE" | OrganizationIssueFieldDiagnostic.ReversedDateRange -> "OIF-REVERSED-DATE-RANGE"
        | OrganizationIssueFieldDiagnostic.MissingSeverity -> "OIF-MISSING-SEVERITY" | OrganizationIssueFieldDiagnostic.UnknownSeverity v -> $"OIF-UNKNOWN-SEVERITY:{v}" | OrganizationIssueFieldDiagnostic.MissingPhase -> "OIF-MISSING-PHASE" | OrganizationIssueFieldDiagnostic.UnknownPhase v -> $"OIF-UNKNOWN-PHASE:{v}"
        | OrganizationIssueFieldDiagnostic.MissingWorkstream -> "OIF-MISSING-WORKSTREAM" | OrganizationIssueFieldDiagnostic.UnknownWorkstream v -> $"OIF-UNKNOWN-WORKSTREAM:{v}"
        | OrganizationIssueFieldDiagnostic.InvalidContractReference -> "OIF-INVALID-CONTRACT" | OrganizationIssueFieldDiagnostic.UnboundContractProjection -> "OIF-UNBOUND-CONTRACT"
        | OrganizationIssueFieldDiagnostic.NoncanonicalTouchSet -> "OIF-NONCANONICAL-TOUCH-SET" | OrganizationIssueFieldDiagnostic.UnboundTouchSetProjection -> "OIF-UNBOUND-TOUCH-SET"
        | OrganizationIssueFieldDiagnostic.LossyHierarchy -> "OIF-LOSSY-HIERARCHY" | OrganizationIssueFieldDiagnostic.LossyDependencies -> "OIF-LOSSY-DEPENDENCIES" | OrganizationIssueFieldDiagnostic.LossyRepositoryScope -> "OIF-LOSSY-REPOSITORY-SCOPE" | OrganizationIssueFieldDiagnostic.DuplicateStableRowId -> "OIF-DUPLICATE-STABLE-ID"

    let touchSetDigest paths = paths |> strings |> Encoding.UTF8.GetBytes |> sha256

    let private prestateBytes (o: OrganizationIssueFieldObservation) =
        [ frame o.StableRowId; frame o.Revision; frame o.RepositoryScope; frame o.NativeIssueType
          optionFrame o.SchedulingIntent; optionFrame o.LifecycleStatus; optionFrame o.HoldReason
          optionFrame o.Priority; optionFrame o.Effort; optionFrame o.StartDate; optionFrame o.TargetDate
          optionFrame o.Severity; optionFrame o.Phase; optionFrame o.Workstream
          optionFrame o.ContractReference; optionFrame o.ContractAuthorityDigest; strings o.TouchSet
          optionFrame o.TouchSetAuthorityDigest; boolText o.HierarchyPresent; boolText o.HierarchyPreservable
          strings o.Dependencies; boolText o.DependenciesPreservable; boolText o.RepositoryScopePreservable
          boolText o.LifecycleExempt; boolText o.Complete; boolText o.Current; boolText o.Readable ]
        |> String.concat "|" |> Encoding.UTF8.GetBytes

    let prestateFingerprint observation = observation |> prestateBytes |> sha256

    let private parseIntent = function "backlog" -> Some SchedulingIntent.Backlog | "ready" -> Some SchedulingIntent.Ready | "paused" -> Some SchedulingIntent.Paused | "cancelled" -> Some SchedulingIntent.Cancelled | _ -> None
    let private derivedStatus = function SchedulingIntent.Backlog -> LifecycleStatus.Backlog | SchedulingIntent.Ready -> LifecycleStatus.Ready | SchedulingIntent.Paused -> LifecycleStatus.Blocked | SchedulingIntent.Cancelled -> LifecycleStatus.Done
    let private parseStatus = function "backlog" -> Some LifecycleStatus.Backlog | "ready" -> Some LifecycleStatus.Ready | "blocked" -> Some LifecycleStatus.Blocked | "done" -> Some LifecycleStatus.Done | _ -> None
    let private parseHold = function "not-yet-actionable" -> Some HoldReason.NotYetActionable | "dependency" -> Some HoldReason.Dependency | "decision" -> Some HoldReason.Decision | "external" -> Some HoldReason.External | "operator" -> Some HoldReason.Operator | _ -> None
    let private parsePriority = function "critical" -> Some WorkPriority.Critical | "high" -> Some WorkPriority.High | "normal" -> Some WorkPriority.Normal | "low" -> Some WorkPriority.Low | _ -> None
    let private parseEffort = function "s" -> Some WorkEffort.S | "m" -> Some WorkEffort.M | "l" -> Some WorkEffort.L | "xl" -> Some WorkEffort.XL | _ -> None
    let private parseSeverity = function "critical" -> Some WorkSeverity.Critical | "high" -> Some WorkSeverity.High | "medium" -> Some WorkSeverity.Medium | "low" -> Some WorkSeverity.Low | "unset" -> Some WorkSeverity.Unset | _ -> None
    let private parsePhase = function "planning" -> Some WorkPhase.Planning | "execution" -> Some WorkPhase.Execution | "verification" -> Some WorkPhase.Verification | "operations" -> Some WorkPhase.Operations | _ -> None
    let private parseWorkstream = function "composition" -> Some Workstream.Composition | "coordination" -> Some Workstream.Coordination | "docs" -> Some Workstream.Docs | "governance" -> Some Workstream.Governance | "lifecycle" -> Some Workstream.Lifecycle | "versioning" -> Some Workstream.Versioning | _ -> None
    let private parseDate value diagnostic =
        match value with
        | None -> Ok None
        | Some value -> match DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with | true, date -> Ok(Some date) | _ -> Error diagnostic
    let private canonicalTouchSet paths =
        not (List.isEmpty paths)
        && paths = List.sort paths && paths.Length = (paths |> Set.ofList |> Set.count)
        && paths |> List.forall (fun p -> not (String.IsNullOrWhiteSpace p) && not (p.Contains('\\')) && not (p.StartsWith('/')) && not (p.Split('/') |> Array.contains ".."))

    let validate (o: OrganizationIssueFieldObservation) =
        let errors = ResizeArray<OrganizationIssueFieldDiagnostic>()
        if not o.Readable then errors.Add OrganizationIssueFieldDiagnostic.UnreadableObservation
        if not o.Complete then errors.Add OrganizationIssueFieldDiagnostic.IncompleteObservation
        if not o.Current then errors.Add OrganizationIssueFieldDiagnostic.StaleObservation
        if String.IsNullOrWhiteSpace o.StableRowId then errors.Add OrganizationIssueFieldDiagnostic.MissingStableRowId
        if String.IsNullOrWhiteSpace o.Revision then errors.Add OrganizationIssueFieldDiagnostic.MissingRevision
        if String.IsNullOrWhiteSpace o.RepositoryScope then errors.Add OrganizationIssueFieldDiagnostic.MissingRepositoryScope
        if String.IsNullOrWhiteSpace o.NativeIssueType then errors.Add OrganizationIssueFieldDiagnostic.MissingNativeIssueType
        if o.HierarchyPresent && not o.HierarchyPreservable then errors.Add OrganizationIssueFieldDiagnostic.LossyHierarchy
        if not o.DependenciesPreservable then errors.Add OrganizationIssueFieldDiagnostic.LossyDependencies
        if not o.RepositoryScopePreservable then errors.Add OrganizationIssueFieldDiagnostic.LossyRepositoryScope
        let parseRequired raw missing unknown parser = match normalize raw with | None -> errors.Add missing; None | Some value -> match parser value with | Some parsed -> Some parsed | None -> errors.Add(unknown value); None
        let intent = parseRequired o.SchedulingIntent OrganizationIssueFieldDiagnostic.MissingSchedulingIntent OrganizationIssueFieldDiagnostic.UnknownSchedulingIntent parseIntent
        let status = parseRequired o.LifecycleStatus OrganizationIssueFieldDiagnostic.MissingLifecycleStatus OrganizationIssueFieldDiagnostic.UnknownLifecycleStatus parseStatus
        match intent, status with | Some i, Some s when derivedStatus i <> s -> errors.Add OrganizationIssueFieldDiagnostic.IntentStatusAuthorityConflict | _ -> ()
        let hold = match normalize o.HoldReason with | None -> None | Some value -> match parseHold value with | Some parsed -> Some parsed | None -> errors.Add(OrganizationIssueFieldDiagnostic.UnknownHoldReason value); None
        match intent, hold with | Some (SchedulingIntent.Backlog | SchedulingIntent.Paused), None -> errors.Add OrganizationIssueFieldDiagnostic.MissingHoldReason | Some (SchedulingIntent.Ready | SchedulingIntent.Cancelled), Some _ -> errors.Add OrganizationIssueFieldDiagnostic.UnexpectedHoldReason | _ -> ()
        let priority = parseRequired o.Priority OrganizationIssueFieldDiagnostic.MissingPriority OrganizationIssueFieldDiagnostic.UnknownPriority parsePriority
        let effort = parseRequired o.Effort OrganizationIssueFieldDiagnostic.MissingEffort OrganizationIssueFieldDiagnostic.UnknownEffort parseEffort
        let severity = parseRequired o.Severity OrganizationIssueFieldDiagnostic.MissingSeverity OrganizationIssueFieldDiagnostic.UnknownSeverity parseSeverity
        let phase = parseRequired o.Phase OrganizationIssueFieldDiagnostic.MissingPhase OrganizationIssueFieldDiagnostic.UnknownPhase parsePhase
        let workstream = parseRequired o.Workstream OrganizationIssueFieldDiagnostic.MissingWorkstream OrganizationIssueFieldDiagnostic.UnknownWorkstream parseWorkstream
        let startDate = match parseDate o.StartDate OrganizationIssueFieldDiagnostic.InvalidStartDate with | Ok v -> v | Error e -> errors.Add e; None
        let targetDate = match parseDate o.TargetDate OrganizationIssueFieldDiagnostic.InvalidTargetDate with | Ok v -> v | Error e -> errors.Add e; None
        match startDate, targetDate with | Some startValue, Some targetValue when targetValue < startValue -> errors.Add OrganizationIssueFieldDiagnostic.ReversedDateRange | _ -> ()
        let contractReference = normalize o.ContractReference
        let contractAuthority = normalize o.ContractAuthorityDigest
        match contractReference, contractAuthority with | None, None -> () | Some reference, Some authority when isDigest reference && reference = authority -> () | Some reference, _ when not (isDigest reference) -> errors.Add OrganizationIssueFieldDiagnostic.InvalidContractReference | _ -> errors.Add OrganizationIssueFieldDiagnostic.UnboundContractProjection
        let touchDigest = if List.isEmpty o.TouchSet then None else Some(touchSetDigest o.TouchSet)
        match o.TouchSet, normalize o.TouchSetAuthorityDigest with | [], None -> () | [], Some _ -> errors.Add OrganizationIssueFieldDiagnostic.UnboundTouchSetProjection | paths, Some authority when canonicalTouchSet paths && Some authority = touchDigest -> () | paths, _ when not (canonicalTouchSet paths) -> errors.Add OrganizationIssueFieldDiagnostic.NoncanonicalTouchSet | _ -> errors.Add OrganizationIssueFieldDiagnostic.UnboundTouchSetProjection
        if errors.Count > 0 then Error(List.ofSeq errors) else
            Ok { SchedulingIntent = intent.Value; LifecycleStatus = status.Value; HoldReason = hold; Priority = priority.Value; Effort = effort.Value
                 StartDate = startDate |> Option.map (_.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); TargetDate = targetDate |> Option.map (_.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                 Severity = severity.Value; Phase = phase.Value; Workstream = workstream.Value; ContractReference = contractReference; TouchSet = o.TouchSet; TouchSetDigest = touchDigest }

    let private isCanonicalNoOp (o: OrganizationIssueFieldObservation) (fields: NormalizedOrganizationIssueFields) =
        o.SchedulingIntent = Some(schedulingIntentName fields.SchedulingIntent)
        && o.LifecycleStatus = Some(lifecycleStatusName fields.LifecycleStatus)
        && o.HoldReason = (fields.HoldReason |> Option.map holdName)
        && o.Priority = Some(priorityName fields.Priority)
        && o.Effort = Some(effortName fields.Effort)
        && o.StartDate = fields.StartDate
        && o.TargetDate = fields.TargetDate
        && o.Severity = Some(severityName fields.Severity)
        && o.Phase = Some(phaseName fields.Phase)
        && o.Workstream = Some(workstreamName fields.Workstream)
        && o.ContractReference = fields.ContractReference
        && o.ContractAuthorityDigest = fields.ContractReference
        && o.TouchSet = fields.TouchSet
        && o.TouchSetAuthorityDigest = fields.TouchSetDigest

    let plan (observations: OrganizationIssueFieldObservation list) =
        let duplicates = observations |> List.filter (fun o -> not (String.IsNullOrWhiteSpace o.StableRowId)) |> List.countBy _.StableRowId |> List.choose (fun (id, n) -> if n > 1 then Some id else None) |> Set.ofList
        let evaluated: Choice<OrganizationIssueFieldDisposition, OrganizationIssueFieldRefusal> list = observations |> List.map (fun (o: OrganizationIssueFieldObservation) ->
            let duplicate = Set.contains o.StableRowId duplicates
            match validate o with
            | Ok fields when not duplicate -> Choice1Of2 { StableRowId = o.StableRowId; PrestateFingerprint = prestateFingerprint o; Fields = fields; RepositoryScope = o.RepositoryScope; NativeIssueType = o.NativeIssueType; HierarchyPreserved = not o.HierarchyPresent || o.HierarchyPreservable; DependenciesPreserved = o.DependenciesPreservable; RepositoryScopePreserved = o.RepositoryScopePreservable; LifecycleExempt = o.LifecycleExempt; NoOp = isCanonicalNoOp o fields }
            | Ok _ -> Choice2Of2 { StableRowId = Some o.StableRowId; Diagnostics = [ OrganizationIssueFieldDiagnostic.DuplicateStableRowId ] }
            | Error diagnostics ->
                Choice2Of2
                    { StableRowId =
                        (if String.IsNullOrWhiteSpace o.StableRowId then None else Some o.StableRowId)
                      Diagnostics =
                        (if duplicate then diagnostics @ [ OrganizationIssueFieldDiagnostic.DuplicateStableRowId ] else diagnostics) })
        let refusals = evaluated |> List.choose (function Choice2Of2 v -> Some v | _ -> None)
        if not (List.isEmpty refusals) then Error(refusals |> List.sortBy (fun r -> r.StableRowId |> Option.defaultValue "", r.Diagnostics |> List.map diagnosticCode))
        else Ok(evaluated |> List.choose (function Choice1Of2 v -> Some v | _ -> None) |> List.sortBy _.StableRowId)

    let canonicalPlanBytes (dispositions: OrganizationIssueFieldDisposition list) =
        dispositions |> List.sortBy _.StableRowId |> List.map (fun (d: OrganizationIssueFieldDisposition) ->
            let f = d.Fields
            [ frame d.StableRowId; frame d.PrestateFingerprint; frame (schedulingIntentName f.SchedulingIntent); frame (lifecycleStatusName f.LifecycleStatus); optionFrame (f.HoldReason |> Option.map holdName)
              frame (priorityName f.Priority); frame (effortName f.Effort); optionFrame f.StartDate; optionFrame f.TargetDate; frame (severityName f.Severity); frame (phaseName f.Phase); frame (workstreamName f.Workstream)
              optionFrame f.ContractReference; strings f.TouchSet; optionFrame f.TouchSetDigest; frame d.RepositoryScope; frame d.NativeIssueType; boolText d.HierarchyPreserved; boolText d.DependenciesPreserved; boolText d.RepositoryScopePreserved; boolText d.LifecycleExempt; boolText d.NoOp ] |> String.concat "|")
        |> String.concat "\n" |> Encoding.UTF8.GetBytes
    let canonicalPlanSha256 dispositions = dispositions |> canonicalPlanBytes |> sha256
