namespace FS.GG.Coordination.Core

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
    val nativeIssueTypes: NativeIssueType list
    val nativeIssueTypeName: NativeIssueType -> string
    val lifecycleName: LifecycleApplicability -> string
    val diagnosticCode: WorkTaxonomyDiagnostic -> string
    val prestateFingerprint: WorkTaxonomyObservation -> string
    val classify: WorkTaxonomyObservation -> Result<WorkTaxonomyClassification, WorkTaxonomyDiagnostic list>
    val plan: WorkTaxonomyObservation list -> Result<WorkTaxonomyDisposition list, WorkTaxonomyRefusal list>
    val canonicalPlanBytes: WorkTaxonomyDisposition list -> byte array
    val canonicalPlanSha256: WorkTaxonomyDisposition list -> string
