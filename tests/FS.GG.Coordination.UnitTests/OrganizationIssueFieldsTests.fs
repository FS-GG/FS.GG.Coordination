module FS.GG.Coordination.OrganizationIssueFieldsTests

open Xunit
open FS.GG.Coordination.Core

let private valid stableId =
    { StableRowId = stableId
      Revision = "node-1@2026-09-01T00:00:00Z"
      RepositoryScope = "FS-GG/repository"
      NativeIssueType = "Task"
      SchedulingIntent = Some "Backlog"
      LifecycleStatus = Some "Backlog"
      HoldReason = Some "not-yet-actionable"
      Priority = Some "Normal"
      Effort = Some "M"
      StartDate = Some "2026-09-01"
      TargetDate = Some "2026-09-02"
      Severity = Some "Unset"
      Phase = Some "Execution"
      Workstream = Some "Coordination"
      ContractReference = None
      ContractAuthorityDigest = None
      TouchSet = []
      TouchSetAuthorityDigest = None
      HierarchyPresent = false
      HierarchyPreservable = true
      Dependencies = []
      DependenciesPreservable = true
      RepositoryScopePreservable = true
      LifecycleExempt = false
      Complete = true
      Current = true
      Readable = true }

let private codes observation =
    match OrganizationIssueFields.validate observation with
    | Ok _ -> failwith "expected refusal"
    | Error diagnostics -> diagnostics |> List.map OrganizationIssueFields.diagnosticCode

[<Fact>]
let ``intent is authoritative and status is its derived projection`` () =
    let cases =
        [ "Backlog", "Backlog", Some "dependency", SchedulingIntent.Backlog, LifecycleStatus.Backlog
          "Ready", "Ready", None, SchedulingIntent.Ready, LifecycleStatus.Ready
          "Paused", "Blocked", Some "operator", SchedulingIntent.Paused, LifecycleStatus.Blocked
          "Cancelled", "Done", None, SchedulingIntent.Cancelled, LifecycleStatus.Done ]
    for intent, status, hold, expectedIntent, expectedStatus in cases do
        match OrganizationIssueFields.validate { valid intent with SchedulingIntent = Some intent; LifecycleStatus = Some status; HoldReason = hold } with
        | Ok fields -> Assert.Equal(expectedIntent, fields.SchedulingIntent); Assert.Equal(expectedStatus, fields.LifecycleStatus)
        | Error errors -> failwithf "unexpected refusal %A" errors

    Assert.Contains("OIF-INTENT-STATUS-AUTHORITY", codes { valid "conflict" with LifecycleStatus = Some "Done" })

[<Fact>]
let ``contract and touch set projections require exact authoritative bindings`` () =
    let digest = String.replicate 64 "a"
    let paths = [ "eng/**"; "src/**" ]
    let touchDigest = OrganizationIssueFields.touchSetDigest paths
    let bound = { valid "bound" with ContractReference = Some digest; ContractAuthorityDigest = Some digest; TouchSet = paths; TouchSetAuthorityDigest = Some touchDigest }
    Assert.True(OrganizationIssueFields.validate bound |> Result.isOk)
    Assert.Contains("OIF-UNBOUND-CONTRACT", codes { bound with ContractAuthorityDigest = None })
    Assert.Contains("OIF-NONCANONICAL-TOUCH-SET", codes { bound with TouchSet = List.rev paths })
    Assert.Contains("OIF-UNBOUND-TOUCH-SET", codes { bound with TouchSetAuthorityDigest = Some(String.replicate 64 "b") })

[<Fact>]
let ``all registered refusal families are stable and corpus plans all or nothing`` () =
    let item = valid "row"
    let cases =
        [ { item with Readable = false }, "OIF-UNREADABLE"
          { item with Complete = false }, "OIF-INCOMPLETE"
          { item with Current = false }, "OIF-STALE"
          { item with StableRowId = "" }, "OIF-MISSING-STABLE-ID"
          { item with Revision = "" }, "OIF-MISSING-REVISION"
          { item with RepositoryScope = "" }, "OIF-MISSING-REPOSITORY-SCOPE"
          { item with NativeIssueType = "" }, "OIF-MISSING-NATIVE-TYPE"
          { item with SchedulingIntent = None }, "OIF-MISSING-INTENT"
          { item with SchedulingIntent = Some "Later" }, "OIF-UNKNOWN-INTENT:later"
          { item with LifecycleStatus = None }, "OIF-MISSING-STATUS"
          { item with LifecycleStatus = Some "Running" }, "OIF-UNKNOWN-STATUS:running"
          { item with HoldReason = None }, "OIF-MISSING-HOLD"
          { item with SchedulingIntent = Some "Ready"; LifecycleStatus = Some "Ready" }, "OIF-UNEXPECTED-HOLD"
          { item with HoldReason = Some "mystery" }, "OIF-UNKNOWN-HOLD:mystery"
          { item with Priority = None }, "OIF-MISSING-PRIORITY"
          { item with Priority = Some "Urgent" }, "OIF-UNKNOWN-PRIORITY:urgent"
          { item with Effort = None }, "OIF-MISSING-EFFORT"
          { item with Effort = Some "XXL" }, "OIF-UNKNOWN-EFFORT:xxl"
          { item with StartDate = Some "09/01/2026" }, "OIF-INVALID-START-DATE"
          { item with TargetDate = Some "tomorrow" }, "OIF-INVALID-TARGET-DATE"
          { item with TargetDate = Some "2026-08-31" }, "OIF-REVERSED-DATE-RANGE"
          { item with Severity = None }, "OIF-MISSING-SEVERITY"
          { item with Severity = Some "Urgent" }, "OIF-UNKNOWN-SEVERITY:urgent"
          { item with Phase = None }, "OIF-MISSING-PHASE"
          { item with Phase = Some "Build" }, "OIF-UNKNOWN-PHASE:build"
          { item with Workstream = None }, "OIF-MISSING-WORKSTREAM"
          { item with Workstream = Some "Other" }, "OIF-UNKNOWN-WORKSTREAM:other"
          { item with ContractReference = Some "bad"; ContractAuthorityDigest = Some "bad" }, "OIF-INVALID-CONTRACT"
          { item with HierarchyPresent = true; HierarchyPreservable = false }, "OIF-LOSSY-HIERARCHY"
          { item with DependenciesPreservable = false }, "OIF-LOSSY-DEPENDENCIES"
          { item with RepositoryScopePreservable = false }, "OIF-LOSSY-REPOSITORY-SCOPE" ]
    for observation, expected in cases do Assert.Contains(expected, codes observation)

    match OrganizationIssueFields.plan [ valid "b"; valid "a" ] with
    | Ok dispositions ->
        Assert.Equal<string list>([ "a"; "b" ], dispositions |> List.map _.StableRowId)
        Assert.All(dispositions, fun disposition -> Assert.True(disposition.NoOp))
        let reversed = OrganizationIssueFields.plan [ valid "a"; valid "b" ] |> Result.defaultWith (failwithf "%A")
        Assert.Equal<byte>(OrganizationIssueFields.canonicalPlanBytes dispositions, OrganizationIssueFields.canonicalPlanBytes reversed)
    | Error errors -> failwithf "unexpected refusal %A" errors

    match OrganizationIssueFields.plan [ valid "same"; valid "same" ] with
    | Error refusals -> Assert.All(refusals, fun refusal -> Assert.Contains(OrganizationIssueFieldDiagnostic.DuplicateStableRowId, refusal.Diagnostics))
    | Ok _ -> failwith "duplicate escaped"

    match OrganizationIssueFields.plan [ { valid "normalize" with SchedulingIntent = Some "backlog" } ] with
    | Ok [ disposition ] -> Assert.False(disposition.NoOp)
    | actual -> failwithf "unexpected normalization result %A" actual
