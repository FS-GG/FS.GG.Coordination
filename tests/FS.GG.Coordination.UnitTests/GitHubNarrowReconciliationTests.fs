module FS.GG.Coordination.GitHubNarrowReconciliationTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubNarrowReconciliationQualification

let private repository = "FS-GG/FS.GG.Coordination"
let private revision = "84829429eee52717db1ce1c19e066e2a4be3203b"
let private get = function Ok value -> value | Error findings -> failwithf "unexpected findings: %A" findings
let private findings = function Error values -> values | Ok value -> failwithf "expected refusal, received %A" value
let private event kind id subjectRevision delivery =
    { EventKind = kind; Repository = repository; SourceRevision = revision; SubjectKind = kind
      SubjectId = id; SubjectRevision = subjectRevision; DeliveryId = delivery
      Route = $"reconcile/{kind}"; Origin = "event"; AttemptsDerivedWrite = false }
let private baselineEvents =
    supportedEventKinds |> List.mapi (fun index kind -> event kind (string(index + 1)) 1L $"delivery-{index + 1}")

[<Fact>]
let ``complete supported inventory compiles to a canonical sealed plan`` () =
    let plan = compile repository revision baselineEvents |> get
    let bytes = serialize plan
    Assert.Equal<string list>(supportedEventKinds, plan.SupportedEventKinds)
    Assert.Equal<string list>(writerBoundary, plan.WriterBoundary)
    Assert.Equal(8, plan.Entries.Length)
    Assert.Equal(plan, parse bytes |> get)
    Assert.Equal(plan, verify plan.Seal plan |> get)
    Assert.Equal(64, plan.Seal.Length)

[<Fact>]
let ``duplicate and reordered events schedule one newest subject`` () =
    let old = event "issue" "299" 1L "delivery-old"
    let newest = { old with SubjectRevision = 2L; DeliveryId = "delivery-new" }
    let forward = compile repository revision [ old; newest; old ] |> get
    let reversed = compile repository revision ([ old; newest; old ] |> List.rev) |> get
    Assert.Equal(forward, reversed)
    let entry = Assert.Single forward.Entries
    Assert.Equal(2L, entry.SubjectRevision)
    Assert.Equal("deduplicated", entry.DeduplicationDisposition)

[<Fact>]
let ``scheduling key uses length framing and repository scope`` () =
    let left = compile repository revision [ event "issue" "a:b" 1L "delivery-1" ] |> get
    let right = compile repository revision [ event "issue" "a" 1L "delivery-1" ] |> get
    let other = compile "FS-GG/Other" revision [ { event "issue" "a:b" 1L "delivery-1" with Repository = "FS-GG/Other" } ] |> get
    Assert.NotEqual(left.Entries.Head.SchedulingKey, right.Entries.Head.SchedulingKey)
    Assert.NotEqual(left.Entries.Head.SchedulingKey, other.Entries.Head.SchedulingKey)

[<Fact>]
let ``unknown malformed incomplete and stale inputs refuse explicitly`` () =
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.IncompleteEventInventory ], compile repository revision [])
    Assert.Contains(GitHubNarrowReconciliationFinding.UnknownEventKind "unsupported", compile repository revision [ event "unsupported" "1" 1L "delivery" ] |> findings)
    Assert.Contains(GitHubNarrowReconciliationFinding.MissingField "subjectId", compile repository revision [ { event "issue" "1" 1L "delivery" with SubjectId = " " } ] |> findings)
    Assert.Contains(GitHubNarrowReconciliationFinding.MalformedField "origin", compile repository revision [ { event "issue" "1" 1L "delivery" with Origin = "mutation" } ] |> findings)
    Assert.Contains(GitHubNarrowReconciliationFinding.StaleRevision "0", compile repository revision [ event "issue" "1" 0L "delivery" ] |> findings)

[<Fact>]
let ``cross scope conflicting subject and altered routing refuse`` () =
    let good = event "issue" "299" 1L "delivery"
    Assert.Contains(GitHubNarrowReconciliationFinding.CrossScope "FS-GG/Other", compile repository revision [ { good with Repository = "FS-GG/Other" } ] |> findings)
    Assert.Contains(GitHubNarrowReconciliationFinding.StaleRevision(String.replicate 40 "a"), compile repository revision [ { good with SourceRevision = String.replicate 40 "a" } ] |> findings)
    Assert.Contains(GitHubNarrowReconciliationFinding.ConflictingSubject "release:299", compile repository revision [ { good with SubjectKind = "release" } ] |> findings)
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.AlteredRouting "reconcile/release" ], compile repository revision [ { good with Route = "reconcile/release" } ])

[<Fact>]
let ``commands and events schedule only and direct writes refuse`` () =
    let command = { event "repository" "FS.GG.Coordination" 1L "command-1" with Origin = "command" }
    Assert.Single((compile repository revision [ command ] |> get).Entries) |> ignore
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.DirectWrite "command-1" ], compile repository revision [ { command with AttemptsDerivedWrite = true } ])

[<Fact>]
let ``writer boundary and seals fail closed when altered`` () =
    let plan = compile repository revision baselineEvents |> get
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.UnsealedPlan ], verify plan.Seal { plan with WriterBoundary = writerBoundary.Tail })
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.AlteredSeal ], verify (String.replicate 64 "0") plan)
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.AlteredSeal ], parse ((serialize plan).Replace(plan.Seal, String.replicate 64 "0")))

[<Fact>]
let ``exact replay is byte identical and new work refuses replay`` () =
    let one = event "issue" "299" 2L "delivery-2"
    let plan = compile repository revision [ one ] |> get
    let replayed = replay plan [ one; { one with SubjectRevision = 1L; DeliveryId = "delivery-1" } ] |> get
    Assert.Equal(serialize plan, serialize replayed)
    Assert.Contains(GitHubNarrowReconciliationFinding.ReplayConflict "new or newer subject requires fresh reconciliation", replay plan [ { one with SubjectRevision = 3L; DeliveryId = "delivery-3" } ] |> findings)

[<Fact>]
let ``non canonical serialization and ordering refuse`` () =
    let plan = compile repository revision baselineEvents |> get
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "non-canonical bytes" ], parse (serialize plan + " "))
    Assert.Equal(Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "entry ordering" ], verify plan.Seal { plan with Entries = List.rev plan.Entries })

[<Fact>]
let ``generated and independent control inventories are exact`` () =
    let green: GitHubNarrowReconciliationControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), validateControls green green)
    Assert.Contains("generated control inventory differs", validateControls green.Tail green |> findings)
    Assert.Contains("independent control failed", validateControls green ({ green.Head with ControlPassed = false } :: green.Tail) |> findings)
