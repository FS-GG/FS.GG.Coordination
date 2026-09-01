namespace FS.GG.Coordination.Core

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type NativeIssueType = Epic | Feature | Task | Bug | Decision | Register | Directive

[<RequireQualifiedAccess>]
type LifecycleApplicability = Work | StandingExempt

type WorkTaxonomyObservation =
    { StableRowId: string
      RepositoryScope: string
      Revision: string
      NativeIssueType: string option
      LegacyClass: string option
      LegacyKind: string option
      HierarchyPresent: bool
      HierarchyPreservable: bool
      RepositoryScopePreservable: bool
      Complete: bool
      Current: bool
      Readable: bool }

[<RequireQualifiedAccess>]
type WorkTaxonomyDiagnostic =
    | UnreadableObservation
    | IncompleteObservation
    | StaleObservation
    | MissingStableRowId
    | MissingRepositoryScope
    | MissingRevision
    | MissingClassification
    | UnknownLegacyClass of string
    | UnknownLegacyKind of string
    | UnsupportedNativeIssueType of string
    | ContradictorySignals
    | AmbiguousSignals
    | UnsupportedCombination
    | LossyHierarchy
    | LossyRepositoryScope
    | DuplicateStableRowId

type WorkTaxonomyClassification =
    { TargetType: NativeIssueType
      Lifecycle: LifecycleApplicability
      RetiredProjections: string list }

type WorkTaxonomyDisposition =
    { StableRowId: string
      PrestateFingerprint: string
      TargetType: NativeIssueType
      Lifecycle: LifecycleApplicability
      RetiredProjections: string list
      RepositoryScope: string
      HierarchyPreserved: bool
      RepositoryScopePreserved: bool
      NoOp: bool }

type WorkTaxonomyRefusal =
    { StableRowId: string option
      Diagnostics: WorkTaxonomyDiagnostic list }

[<RequireQualifiedAccess>]
module WorkTaxonomy =
    let nativeIssueTypes =
        [ NativeIssueType.Epic
          NativeIssueType.Feature
          NativeIssueType.Task
          NativeIssueType.Bug
          NativeIssueType.Decision
          NativeIssueType.Register
          NativeIssueType.Directive ]

    let nativeIssueTypeName = function
        | NativeIssueType.Epic -> "Epic"
        | NativeIssueType.Feature -> "Feature"
        | NativeIssueType.Task -> "Task"
        | NativeIssueType.Bug -> "Bug"
        | NativeIssueType.Decision -> "Decision"
        | NativeIssueType.Register -> "Register"
        | NativeIssueType.Directive -> "Directive"

    let lifecycle = function
        | NativeIssueType.Register | NativeIssueType.Directive -> LifecycleApplicability.StandingExempt
        | _ -> LifecycleApplicability.Work

    let lifecycleName = function
        | LifecycleApplicability.Work -> "work"
        | LifecycleApplicability.StandingExempt -> "standing-exempt"

    let diagnosticCode = function
        | WorkTaxonomyDiagnostic.UnreadableObservation -> "WTX-UNREADABLE"
        | WorkTaxonomyDiagnostic.IncompleteObservation -> "WTX-INCOMPLETE"
        | WorkTaxonomyDiagnostic.StaleObservation -> "WTX-STALE"
        | WorkTaxonomyDiagnostic.MissingStableRowId -> "WTX-MISSING-STABLE-ID"
        | WorkTaxonomyDiagnostic.MissingRepositoryScope -> "WTX-MISSING-REPOSITORY-SCOPE"
        | WorkTaxonomyDiagnostic.MissingRevision -> "WTX-MISSING-REVISION"
        | WorkTaxonomyDiagnostic.MissingClassification -> "WTX-MISSING-CLASSIFICATION"
        | WorkTaxonomyDiagnostic.UnknownLegacyClass value -> $"WTX-UNKNOWN-CLASS:{value}"
        | WorkTaxonomyDiagnostic.UnknownLegacyKind value -> $"WTX-UNKNOWN-KIND:{value}"
        | WorkTaxonomyDiagnostic.UnsupportedNativeIssueType value -> $"WTX-UNSUPPORTED-NATIVE:{value}"
        | WorkTaxonomyDiagnostic.ContradictorySignals -> "WTX-CONTRADICTORY"
        | WorkTaxonomyDiagnostic.AmbiguousSignals -> "WTX-AMBIGUOUS"
        | WorkTaxonomyDiagnostic.UnsupportedCombination -> "WTX-UNSUPPORTED-COMBINATION"
        | WorkTaxonomyDiagnostic.LossyHierarchy -> "WTX-LOSSY-HIERARCHY"
        | WorkTaxonomyDiagnostic.LossyRepositoryScope -> "WTX-LOSSY-REPOSITORY-SCOPE"
        | WorkTaxonomyDiagnostic.DuplicateStableRowId -> "WTX-DUPLICATE-STABLE-ID"

    let private normalize (value: string option) = value |> Option.bind (fun (value: string) ->
        let trimmed = value.Trim()
        if String.IsNullOrEmpty trimmed then None else Some(trimmed.ToLowerInvariant()))

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private optionFrame = Option.defaultValue "" >> frame
    let private boolText value = if value then "true" else "false"

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private prestateBytes (observation: WorkTaxonomyObservation) =
        [ frame observation.StableRowId
          frame observation.RepositoryScope
          frame observation.Revision
          optionFrame observation.NativeIssueType
          optionFrame observation.LegacyClass
          optionFrame observation.LegacyKind
          boolText observation.HierarchyPresent
          boolText observation.HierarchyPreservable
          boolText observation.RepositoryScopePreservable
          boolText observation.Complete
          boolText observation.Current
          boolText observation.Readable ]
        |> String.concat "|"
        |> Encoding.UTF8.GetBytes

    let prestateFingerprint observation = observation |> prestateBytes |> sha256

    let private parseNative (value: string) =
        nativeIssueTypes
        |> List.tryFind (nativeIssueTypeName >> fun candidate -> String.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))

    let private legacyTarget (legacyClass: string option) (legacyKind: string option) : Result<NativeIssueType, WorkTaxonomyDiagnostic list> =
        match legacyKind, legacyClass with
        | Some "anchor", None -> Ok NativeIssueType.Epic
        | Some "register", None -> Ok NativeIssueType.Register
        | Some "directive", None -> Ok NativeIssueType.Directive
        | Some ("anchor" | "register" | "directive"), Some _ -> Error [ WorkTaxonomyDiagnostic.AmbiguousSignals ]
        | None, Some "capability"
        | Some "work", Some "capability" -> Ok NativeIssueType.Feature
        | None, Some "hardening"
        | Some "work", Some "hardening" -> Ok NativeIssueType.Task
        | None, Some "defect"
        | Some "work", Some "defect" -> Ok NativeIssueType.Bug
        | None, Some "decision"
        | Some "work", Some "decision" -> Ok NativeIssueType.Decision
        | None, None
        | Some "work", None -> Error [ WorkTaxonomyDiagnostic.MissingClassification ]
        | _ -> Error [ WorkTaxonomyDiagnostic.UnsupportedCombination ]

    let private preliminaryDiagnostics (observation: WorkTaxonomyObservation) =
        [ if not observation.Readable then WorkTaxonomyDiagnostic.UnreadableObservation
          if not observation.Complete then WorkTaxonomyDiagnostic.IncompleteObservation
          if not observation.Current then WorkTaxonomyDiagnostic.StaleObservation
          if String.IsNullOrWhiteSpace observation.StableRowId then WorkTaxonomyDiagnostic.MissingStableRowId
          if String.IsNullOrWhiteSpace observation.RepositoryScope then WorkTaxonomyDiagnostic.MissingRepositoryScope
          if String.IsNullOrWhiteSpace observation.Revision then WorkTaxonomyDiagnostic.MissingRevision
          if observation.HierarchyPresent && not observation.HierarchyPreservable then WorkTaxonomyDiagnostic.LossyHierarchy
          if not observation.RepositoryScopePreservable then WorkTaxonomyDiagnostic.LossyRepositoryScope ]

    let private retiredProjections (observation: WorkTaxonomyObservation) =
        [ if normalize observation.LegacyClass |> Option.isSome then "Class"
          if normalize observation.LegacyKind |> Option.isSome then "Kind" ]

    let classify (observation: WorkTaxonomyObservation) : Result<WorkTaxonomyClassification, WorkTaxonomyDiagnostic list> =
        let preliminary = preliminaryDiagnostics observation
        if not (List.isEmpty preliminary) then Error preliminary
        else
            let nativeRaw = normalize observation.NativeIssueType
            let legacyClass = normalize observation.LegacyClass
            let legacyKind = normalize observation.LegacyKind
            let classErrors =
                match legacyClass with
                | Some ("capability" | "hardening" | "defect" | "decision") | None -> []
                | Some value -> [ WorkTaxonomyDiagnostic.UnknownLegacyClass value ]
            let kindErrors =
                match legacyKind with
                | Some ("work" | "anchor" | "register" | "directive") | None -> []
                | Some value -> [ WorkTaxonomyDiagnostic.UnknownLegacyKind value ]
            let nativeValue, nativeErrors =
                match nativeRaw with
                | None -> None, []
                | Some value ->
                    match parseNative value with
                    | Some parsed -> Some parsed, []
                    | None -> None, [ WorkTaxonomyDiagnostic.UnsupportedNativeIssueType value ]
            let tokenErrors = classErrors @ kindErrors @ nativeErrors
            if not (List.isEmpty tokenErrors) then Error tokenErrors
            else
                let legacy = legacyTarget legacyClass legacyKind
                let standingKindConflict =
                    match nativeValue, legacyKind with
                    | Some (NativeIssueType.Register | NativeIssueType.Directive), Some "work" -> true
                    | _ -> false
                match nativeValue, legacy with
                | Some _, _ when standingKindConflict -> Error [ WorkTaxonomyDiagnostic.ContradictorySignals ]
                | Some native, Ok legacy when native <> legacy -> Error [ WorkTaxonomyDiagnostic.ContradictorySignals ]
                | Some native, _ ->
                    Ok { TargetType = native; Lifecycle = lifecycle native; RetiredProjections = retiredProjections observation }
                | None, Ok target ->
                    Ok { TargetType = target; Lifecycle = lifecycle target; RetiredProjections = retiredProjections observation }
                | None, Error diagnostics -> Error diagnostics

    let private disposition (observation: WorkTaxonomyObservation) (classification: WorkTaxonomyClassification) : WorkTaxonomyDisposition =
        { StableRowId = observation.StableRowId
          PrestateFingerprint = prestateFingerprint observation
          TargetType = classification.TargetType
          Lifecycle = classification.Lifecycle
          RetiredProjections = classification.RetiredProjections
          RepositoryScope = observation.RepositoryScope
          HierarchyPreserved = not observation.HierarchyPresent || observation.HierarchyPreservable
          RepositoryScopePreserved = observation.RepositoryScopePreservable
          NoOp = (normalize observation.NativeIssueType |> Option.isSome) && List.isEmpty classification.RetiredProjections }

    let plan (observations: WorkTaxonomyObservation list) : Result<WorkTaxonomyDisposition list, WorkTaxonomyRefusal list> =
        let duplicateIds =
            observations
            |> List.filter (fun item -> not (String.IsNullOrWhiteSpace item.StableRowId))
            |> List.countBy _.StableRowId
            |> List.choose (fun (stableId, count) -> if count > 1 then Some stableId else None)
            |> Set.ofList

        let refusal stableId diagnostics : WorkTaxonomyRefusal =
            { StableRowId = stableId; Diagnostics = diagnostics }

        let evaluated: Choice<WorkTaxonomyDisposition, WorkTaxonomyRefusal> list =
            observations
            |> List.map (fun observation ->
                let duplicate = Set.contains observation.StableRowId duplicateIds
                match classify observation with
                | Ok classification when not duplicate -> Choice1Of2(disposition observation classification)
                | Ok _ -> Choice2Of2(refusal (Some observation.StableRowId) [ WorkTaxonomyDiagnostic.DuplicateStableRowId ])
                | Error diagnostics when duplicate ->
                    Choice2Of2(refusal (if String.IsNullOrWhiteSpace observation.StableRowId then None else Some observation.StableRowId) (diagnostics @ [ WorkTaxonomyDiagnostic.DuplicateStableRowId ]))
                | Error diagnostics ->
                    Choice2Of2(refusal (if String.IsNullOrWhiteSpace observation.StableRowId then None else Some observation.StableRowId) diagnostics))

        let refusals = evaluated |> List.choose (function Choice2Of2 value -> Some value | _ -> None)
        if not (List.isEmpty refusals) then
            refusals
            |> List.sortBy (fun refusal -> refusal.StableRowId |> Option.defaultValue "", refusal.Diagnostics |> List.map diagnosticCode)
            |> Error
        else
            evaluated
            |> List.choose (function Choice1Of2 value -> Some value | _ -> None)
            |> List.sortBy _.StableRowId
            |> Ok

    let canonicalPlanBytes (dispositions: WorkTaxonomyDisposition list) =
        dispositions
        |> List.sortBy _.StableRowId
        |> List.map (fun (disposition: WorkTaxonomyDisposition) ->
            [ frame disposition.StableRowId
              frame disposition.PrestateFingerprint
              frame (nativeIssueTypeName disposition.TargetType)
              frame (lifecycleName disposition.Lifecycle)
              disposition.RetiredProjections |> List.map frame |> String.concat ""
              frame disposition.RepositoryScope
              boolText disposition.HierarchyPreserved
              boolText disposition.RepositoryScopePreserved
              boolText disposition.NoOp ]
            |> String.concat "|")
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let canonicalPlanSha256 dispositions = dispositions |> canonicalPlanBytes |> sha256
