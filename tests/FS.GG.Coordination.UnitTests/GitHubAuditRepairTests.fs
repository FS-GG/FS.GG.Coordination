module FS.GG.Coordination.GitHubAuditRepairTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubAuditRepairQualification

let private repository = "FS-GG/FS.GG.Coordination"
let private externalRepository = "FS-GG/External"
let private revision = "84829429eee52717db1ce1c19e066e2a4be3203b"
let private scope = [ externalRepository; repository ]
let private cursor = "audit:2026-09-05T17:00:00Z"
let private get = function Ok value -> value | Error findings -> failwithf "unexpected findings: %A" findings
let private findings = function Error values -> values | Ok value -> failwithf "expected refusal, received %A" value
let private history repository kind id subjectRevision delivery =
    { Repository = repository; SourceRevision = revision; SubjectKind = kind; SubjectId = id
      SubjectRevision = subjectRevision; DeliveryId = delivery }
let private observation repo page pageCount kind id subjectRevision classification evidence =
    { Repository = repo; SourceRevision = revision; AuditScope = scope
      Cursor = cursor; Page = page; PageCount = pageCount; SubjectKind = kind; SubjectId = id
      SubjectRevision = subjectRevision; Classification = classification; EvidenceId = evidence
      Route = $"reconcile/{kind}"; Origin = "audit"; AttemptsDerivedWrite = false }
let private histories =
    [ history repository "issue" "304" 1L "delivery-issue-1"
      history repository "project" "coordination" 4L "delivery-project-4" ]
let private observations =
    [ observation repository 1 3 "issue" "304" 2L "dropped-delivery" "audit-drop-1"
      observation repository 2 3 "project" "coordination" 4L "preview-gap" "audit-preview-1"
      observation repository 3 3 "repository" "FS.GG.Coordination" 7L "schema-drift" "audit-schema-1"
      observation externalRepository 1 1 "repository" "External" 3L "external-repository" "audit-external-1" ]

[<Fact>]
let ``complete audit remains authoritative and converges with event history`` () =
    let plan = compile repository revision scope cursor histories observations |> get
    Assert.Equal(4, plan.Entries.Length)
    let dropped = plan.Entries |> List.find (fun entry -> entry.Subject.EndsWith("issue:304"))
    Assert.Equal(2L, dropped.SubjectRevision)
    Assert.Equal("event-audit-converged", dropped.DeduplicationDisposition)
    let schema = plan.Entries |> List.find (fun entry -> entry.Classifications = [ "schema-drift" ])
    Assert.Equal("audit-repair", schema.DeduplicationDisposition)
    Assert.Equal(serialize plan, serialize (compile repository revision scope cursor (List.rev histories) (List.rev observations) |> get))

[<Fact>]
let ``complete audit works without delivery history`` () =
    let plan = compile repository revision scope cursor [] observations |> get
    Assert.All(plan.Entries, fun entry -> Assert.Equal("audit-repair", entry.DeduplicationDisposition))

[<Fact>]
let ``scope cursor and pages fail closed`` () =
    Assert.Contains(GitHubAuditRepairFinding.IncompleteAuditScope, compile repository revision [ repository; externalRepository ] cursor histories observations |> findings)
    let missingRepository = observations |> List.filter (fun row -> row.Repository <> externalRepository)
    Assert.Contains(GitHubAuditRepairFinding.IncompleteAuditScope, compile repository revision scope cursor histories missingRepository |> findings)
    let alteredScope = { observations.Head with AuditScope = [ repository ] }
    Assert.Contains(GitHubAuditRepairFinding.AlteredScope repository, compile repository revision scope cursor histories (alteredScope :: observations.Tail) |> findings)
    Assert.Contains(GitHubAuditRepairFinding.StaleCursor "stale", compile repository revision scope "stale" histories observations |> findings)
    let partial = observations |> List.filter (fun row -> row.Page <> 2)
    Assert.Contains(GitHubAuditRepairFinding.PartialPage repository, compile repository revision scope cursor histories partial |> findings)

[<Fact>]
let ``observation identity revision and classification fail closed`` () =
    let row = observations.Head
    Assert.Contains(GitHubAuditRepairFinding.StaleRevision "0", compile repository revision scope cursor histories ({ row with SubjectRevision = 0L } :: observations.Tail) |> findings)
    Assert.Contains(GitHubAuditRepairFinding.AlteredClassification "unknown", compile repository revision scope cursor histories ({ row with Classification = "unknown" } :: observations.Tail) |> findings)
    Assert.Contains(GitHubAuditRepairFinding.AlteredObservation "audit-drop-1", compile repository revision scope cursor histories ({ row with Origin = "event" } :: observations.Tail) |> findings)
    let staleHistory = { histories.Head with SourceRevision = String.replicate 40 "a" }
    Assert.Contains(GitHubAuditRepairFinding.StaleRevision(String.replicate 40 "a"), compile repository revision scope cursor (staleHistory :: histories.Tail) observations |> findings)

[<Fact>]
let ``every repair classification is mandatory`` () =
    for classification in requiredClassifications do
        let reduced = observations |> List.filter (fun row -> row.Classification <> classification)
        Assert.Contains(GitHubAuditRepairFinding.OmittedClassification classification, compile repository revision scope cursor [] reduced |> findings)

[<Fact>]
let ``audit discovery schedules reconciler and cannot write`` () =
    let row = observations.Head
    Assert.Contains(GitHubAuditRepairFinding.AlteredRouting "reconcile/release", compile repository revision scope cursor histories ({ row with Route = "reconcile/release" } :: observations.Tail) |> findings)
    Assert.Contains(GitHubAuditRepairFinding.DirectWrite "audit-drop-1", compile repository revision scope cursor histories ({ row with AttemptsDerivedWrite = true } :: observations.Tail) |> findings)

[<Fact>]
let ``seal ordering and canonical bytes fail closed`` () =
    let plan = compile repository revision scope cursor histories observations |> get
    Assert.Equal(Error [ GitHubAuditRepairFinding.UnsealedPlan ], verify plan.Seal { plan with WriterBoundary = writerBoundary.Tail })
    Assert.Equal(Error [ GitHubAuditRepairFinding.AlteredSeal ], verify (String.replicate 64 "0") plan)
    Assert.Equal(Error [ GitHubAuditRepairFinding.InvalidSerialization "entry ordering" ], verify plan.Seal { plan with Entries = List.rev plan.Entries })
    Assert.Equal(Error [ GitHubAuditRepairFinding.InvalidSerialization "non-canonical bytes" ], parse (serialize plan + " "))

[<Fact>]
let ``exact replay is byte identical and changed replay refuses`` () =
    let plan = compile repository revision scope cursor histories observations |> get
    Assert.Equal(serialize plan, serialize (replay plan histories observations |> get))
    let changed = { observations.Head with SubjectRevision = 9L }
    Assert.Contains(GitHubAuditRepairFinding.ReplayConflict "audit replay differs from the sealed plan", replay plan histories (changed :: observations.Tail) |> findings)

[<Fact>]
let ``generated and independent audit control inventories are exact`` () =
    let green: GitHubAuditRepairControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), validateControls green green)
    Assert.Contains("generated control inventory differs", validateControls green.Tail green |> findings)
    Assert.Contains("independent control failed", validateControls green ({ green.Head with ControlPassed = false } :: green.Tail) |> findings)
