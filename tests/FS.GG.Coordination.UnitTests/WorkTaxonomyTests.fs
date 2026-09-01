module FS.GG.Coordination.WorkTaxonomyTests

open Xunit
open FS.GG.Coordination.Core

let private observation stableId nativeType legacyClass legacyKind =
    { StableRowId = stableId
      RepositoryScope = "FS-GG/repository"
      Revision = "node-1@2026-09-01T00:00:00Z"
      NativeIssueType = nativeType
      LegacyClass = legacyClass
      LegacyKind = legacyKind
      HierarchyPresent = false
      HierarchyPreservable = true
      RepositoryScopePreservable = true
      Complete = true
      Current = true
      Readable = true }

let private target item =
    match WorkTaxonomy.classify item with
    | Ok classification -> classification.TargetType, classification.Lifecycle
    | Error diagnostics -> failwith (diagnostics |> List.map WorkTaxonomy.diagnosticCode |> String.concat ",")

let private codes item =
    match WorkTaxonomy.classify item with
    | Ok _ -> failwith "expected classification refusal"
    | Error diagnostics -> diagnostics |> List.map WorkTaxonomy.diagnosticCode

[<Fact>]
let ``legacy authority maps to the seven native issue types`` () =
    let cases =
        [ observation "1" None None (Some "anchor"), (NativeIssueType.Epic, LifecycleApplicability.Work)
          observation "2" None (Some "capability") None, (NativeIssueType.Feature, LifecycleApplicability.Work)
          observation "3" None (Some "hardening") (Some "work"), (NativeIssueType.Task, LifecycleApplicability.Work)
          observation "4" None (Some "defect") None, (NativeIssueType.Bug, LifecycleApplicability.Work)
          observation "5" None (Some "decision") None, (NativeIssueType.Decision, LifecycleApplicability.Work)
          observation "6" None None (Some "register"), (NativeIssueType.Register, LifecycleApplicability.StandingExempt)
          observation "7" None None (Some "directive"), (NativeIssueType.Directive, LifecycleApplicability.StandingExempt) ]
    for item, expected in cases do Assert.Equal(expected, target item)

[<Fact>]
let ``already native rows are explicit no ops and native authority rejects contradiction`` () =
    let native = observation "8" (Some "Bug") None None
    match WorkTaxonomy.plan [ native ] with
    | Ok [ disposition ] ->
        Assert.True(disposition.NoOp)
        Assert.Empty disposition.RetiredProjections
        Assert.Equal(NativeIssueType.Bug, disposition.TargetType)
    | other -> failwithf "unexpected plan %A" other

    let contradictory = observation "9" (Some "Feature") (Some "defect") None
    Assert.Contains("WTX-CONTRADICTORY", codes contradictory)
    let standingAsWork = observation "9b" (Some "Register") None (Some "work")
    Assert.Contains("WTX-CONTRADICTORY", codes standingAsWork)

[<Fact>]
let ``every fail closed observation family has a stable diagnostic`` () =
    let valid = observation "10" None (Some "hardening") None
    let cases =
        [ { valid with Readable = false }, "WTX-UNREADABLE"
          { valid with Complete = false }, "WTX-INCOMPLETE"
          { valid with Current = false }, "WTX-STALE"
          { valid with StableRowId = "" }, "WTX-MISSING-STABLE-ID"
          { valid with RepositoryScope = "" }, "WTX-MISSING-REPOSITORY-SCOPE"
          { valid with Revision = "" }, "WTX-MISSING-REVISION"
          observation "11" None None None, "WTX-MISSING-CLASSIFICATION"
          observation "12" None (Some "mystery") None, "WTX-UNKNOWN-CLASS:mystery"
          observation "13" None None (Some "mystery"), "WTX-UNKNOWN-KIND:mystery"
          observation "14" (Some "Incident") None None, "WTX-UNSUPPORTED-NATIVE:incident"
          observation "15" None (Some "defect") (Some "anchor"), "WTX-AMBIGUOUS"
          { valid with HierarchyPresent = true; HierarchyPreservable = false }, "WTX-LOSSY-HIERARCHY"
          { valid with RepositoryScopePreservable = false }, "WTX-LOSSY-REPOSITORY-SCOPE" ]
    for item, expected in cases do Assert.Contains(expected, codes item)

[<Fact>]
let ``planning is total ordered all or nothing and byte stable`` () =
    let a = observation "row-b" None (Some "defect") None
    let b = observation "row-a" None (Some "hardening") (Some "work")
    let first = WorkTaxonomy.plan [ a; b ]
    let second = WorkTaxonomy.plan [ b; a ]
    match first, second with
    | Ok firstPlan, Ok secondPlan ->
        Assert.Equal<string list>([ "row-a"; "row-b" ], firstPlan |> List.map _.StableRowId)
        Assert.Equal<WorkTaxonomyDisposition list>(firstPlan, secondPlan)
        Assert.Equal<byte>(WorkTaxonomy.canonicalPlanBytes firstPlan, WorkTaxonomy.canonicalPlanBytes secondPlan)
        Assert.Equal(WorkTaxonomy.canonicalPlanSha256 firstPlan, WorkTaxonomy.canonicalPlanSha256 secondPlan)
        Assert.All(firstPlan, fun item -> Assert.Equal(64, item.PrestateFingerprint.Length))
    | other -> failwithf "unexpected plans %A" other

    match WorkTaxonomy.plan [ a; a ] with
    | Error refusals ->
        Assert.Equal(2, refusals.Length)
        Assert.All(refusals, fun refusal -> Assert.Contains(WorkTaxonomyDiagnostic.DuplicateStableRowId, refusal.Diagnostics))
    | Ok _ -> failwith "duplicate identity produced a partial plan"

[<Fact>]
let ``invalid member refuses the complete migration plan`` () =
    let valid = observation "row-a" None (Some "defect") None
    let invalid = observation "row-b" None None None
    match WorkTaxonomy.plan [ valid; invalid ] with
    | Error [ refusal ] ->
        Assert.Equal(Some "row-b", refusal.StableRowId)
        Assert.Contains(WorkTaxonomyDiagnostic.MissingClassification, refusal.Diagnostics)
    | other -> failwithf "partial or unexpected plan %A" other
