module FS.GG.Coordination.GitHubRuntimeOperationsTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts
module Q = FS.GG.Coordination.Qualification.Contracts.GitHubRuntimeOperationsQualification

let private authority = String.replicate 40 "a"
let private exercise failure subjects pages path confirmed settlement diagnostic =
    { ExerciseId = $"exercise-{failure}"; Failure = failure; ExpectedSubjects = subjects; RecoveredSubjects = subjects
      ExpectedPages = pages; RecoveredPages = pages; AuthorityBefore = authority; AuthorityAfter = authority
      RecoveryPath = path; ProviderConfirmed = confirmed; Settlement = settlement
      DiagnosticInput = diagnostic |> Option.map (fun secret -> $"recovery interrupted synthetic-secret={secret}")
      DiagnosticOutput = diagnostic |> Option.map (fun _ -> "recovery interrupted synthetic-secret=<redacted>") }
let private baseline () =
    { Unit = "GS2-07.8"; PrerequisiteReceiptSha256 = Q.prerequisiteReceiptSha256
      RoadmapRevision = Q.roadmapRevision; RoadmapSha256 = Q.roadmapSha256; CandidateHead = authority
      BuildInputs =
        { OutputType = "Library"; IsPackable = false; PublishProfile = None; RuntimeIdentifier = None
          SelfContained = false; Listening = false; DeploymentConfigured = false; ProductionAuthority = false
          EvaluatedProjects = [ "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj" ] }
      Clauses = Q.requiredClauses
      Exercises =
        [ exercise "event-absence" [ "issue:17" ] [ 1 ] "scheduled-complete-audit" true "converged" None
          exercise "provider-unavailability" [ "project:3"; "repository:coordination" ] [ 1; 2 ] "retry-after-provider-read" true "converged" None
          exercise "incomplete-audit" [ "issue:21"; "issue:22" ] [ 1; 2; 3 ] "discard-partial-and-repeat-complete-audit" true "converged" None
          exercise "backlog-replay" [ "issue:31"; "relation:31-32" ] [ 1; 2 ] "replay-through-shared-reconciler" true "converged" None
          exercise "interruption" [ "ruleset:main" ] [ 1 ] "reobserve-before-resume" false "unsettled" (Some "gs2-07-8-test-secret") ]
      AcceptedChildren = Q.acceptedChildren; ModelIdentity = Q.modelIdentity
      ProductionV2 = false; InstalledAuditExecution = false; PollingReduced = false; Gs208Claimed = false }
let private get = function Ok value -> value | Error errors -> failwithf "baseline failed: %A" errors
let private has expected = function Error errors -> List.contains expected errors | Ok _ -> false

[<Fact>]
let ``no-host qualification exercises applicable recovery and preserves limitations`` () =
    let report = baseline () |> Q.compile |> get
    Assert.Equal("no-host-scheduled-audit-authoritative", report.RuntimeDisposition)
    Assert.Equal("scheduled-complete-audit", report.AuditAuthority)
    Assert.Equal(Q.requiredFailures, report.Exercises |> List.map _.Failure)
    Assert.False(report.BuildInputs.Listening)
    Assert.False(report.ProductionV2)
    Assert.False(report.InstalledAuditExecution)
    Assert.False(report.PollingReduced)
    Assert.False(report.Gs208Claimed)
    Assert.Contains("no hosted log or alert pipeline exists", report.Limits)

[<Fact>]
let ``canonical report round trips and replay is exact`` () =
    let facts = baseline ()
    let report = facts |> Q.compile |> get
    let bytes = Q.serialize report
    Assert.Equal(Ok report, Q.parse bytes)
    Assert.Equal(Ok report, Q.verify report.Seal report)
    Assert.Equal(Ok report, Q.replay report facts)
    Assert.True(Q.replay report { facts with CandidateHead = String.replicate 40 "b" } |> has RuntimeOperationsFinding.ReplayConflict)

[<Fact>]
let ``evaluated build inputs detect every host activation`` () =
    let facts = baseline ()
    let build = facts.BuildInputs
    Assert.True(Q.compile { facts with BuildInputs = { build with OutputType = "Exe" } } |> has (RuntimeOperationsFinding.HostActivated "OutputType"))
    Assert.True(Q.compile { facts with BuildInputs = { build with IsPackable = true } } |> has (RuntimeOperationsFinding.HostActivated "IsPackable"))
    Assert.True(Q.compile { facts with BuildInputs = { build with PublishProfile = Some "host.pubxml" } } |> has (RuntimeOperationsFinding.HostActivated "PublishProfile"))
    Assert.True(Q.compile { facts with BuildInputs = { build with Listening = true } } |> has (RuntimeOperationsFinding.HostActivated "Listening"))
    Assert.True(Q.compile { facts with BuildInputs = { build with ProductionAuthority = true } } |> has (RuntimeOperationsFinding.HostActivated "ProductionAuthority"))

[<Fact>]
let ``missing recovery subjects pages and current authority fail closed`` () =
    let facts = baseline ()
    Assert.True(Q.compile { facts with Exercises = facts.Exercises.Tail } |> has (RuntimeOperationsFinding.MissingRecovery "event-absence"))
    let first = facts.Exercises.Head
    Assert.True(Q.compile { facts with Exercises = { first with RecoveredSubjects = [] } :: facts.Exercises.Tail } |> has (RuntimeOperationsFinding.OmittedSubject first.Failure))
    Assert.True(Q.compile { facts with Exercises = { first with RecoveredPages = [] } :: facts.Exercises.Tail } |> has (RuntimeOperationsFinding.OmittedPage first.Failure))
    Assert.True(Q.compile { facts with Exercises = { first with AuthorityAfter = String.replicate 40 "b" } :: facts.Exercises.Tail } |> has (RuntimeOperationsFinding.StaleReplayAuthority first.Failure))

[<Fact>]
let ``interruption cannot invent settlement or leak its synthetic secret`` () =
    let facts = baseline ()
    let interruption = facts.Exercises |> List.last
    let prefix = facts.Exercises |> List.take 4
    Assert.True(Q.compile { facts with Exercises = prefix @ [ { interruption with ProviderConfirmed = true; Settlement = "converged" } ] } |> has (RuntimeOperationsFinding.InventedSettlement "interruption"))
    Assert.True(Q.compile { facts with Exercises = prefix @ [ { interruption with DiagnosticOutput = interruption.DiagnosticInput } ] } |> has (RuntimeOperationsFinding.SecretLeak "interruption"))

[<Fact>]
let ``changed predecessor roadmap clauses children model and unsupported claims refuse`` () =
    let facts = baseline ()
    Assert.True(Q.compile { facts with PrerequisiteReceiptSha256 = String.replicate 64 "0" } |> has RuntimeOperationsFinding.ChangedPrerequisite)
    Assert.True(Q.compile { facts with RoadmapRevision = String.replicate 40 "0" } |> has RuntimeOperationsFinding.ChangedRoadmap)
    Assert.True(Q.compile { facts with Clauses = facts.Clauses.Tail } |> has RuntimeOperationsFinding.ClauseInventoryChanged)
    Assert.True(Q.compile { facts with AcceptedChildren = facts.AcceptedChildren.Tail } |> has (RuntimeOperationsFinding.ChildReceiptChanged "GS2-07.1"))
    Assert.True(Q.compile { facts with ModelIdentity = String.replicate 64 "0" } |> has RuntimeOperationsFinding.ModelIdentityChanged)
    Assert.True(Q.compile { facts with ProductionV2 = true } |> has (RuntimeOperationsFinding.UnsupportedClaim "production-v2"))
    Assert.True(Q.compile { facts with InstalledAuditExecution = true } |> has (RuntimeOperationsFinding.UnsupportedClaim "installed-audit-execution"))
    Assert.True(Q.compile { facts with PollingReduced = true } |> has (RuntimeOperationsFinding.UnsupportedClaim "polling-reduced"))
    Assert.True(Q.compile { facts with Gs208Claimed = true } |> has (RuntimeOperationsFinding.UnsupportedClaim "GS2-08"))

[<Fact>]
let ``control inventory must be independently evidenced`` () =
    let generated: RuntimeOperationsControlResult list = Q.requiredControls |> List.map (fun id -> { ControlId = id; ControlPassed = true; BaselineGreen = true; Evidence = $"generated:{id}" })
    let independent: RuntimeOperationsControlResult list = Q.requiredControls |> List.map (fun id -> { ControlId = id; ControlPassed = true; BaselineGreen = true; Evidence = $"independent:{id}" })
    Assert.Equal(Ok (), Q.validateControls generated independent)
    Assert.True(Q.validateControls generated generated |> Result.isError)
    Assert.True(Q.validateControls generated.Tail independent |> Result.isError)
