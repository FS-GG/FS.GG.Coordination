module FS.GG.Coordination.GitHubRequiredCheckCensusTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private revision = String.replicate 40 "a"
let private digest = String.replicate 64 "b"
let private observedAt = DateTimeOffset.Parse("2026-09-02T00:00:00Z")
let private event = { Declared = true; BranchFilters = []; PathFilters = []; ActivityTypes = [] }
let private requirement source context integrationId =
    { Repository = "FS-GG/example"; Context = context; IntegrationId = integrationId; Source = source }
let private producer context integrationId =
    { Repository = "FS-GG/example"
      Context = context
      IntegrationId = integrationId
      Workflow = ".github/workflows/ci.yml"
      Job = "aggregate"
      WorkflowRevision = revision
      WorkflowSha256 = digest
      PullRequest = event
      MergeGroup = event
      DependenciesComplete = true
      Conditional = false
      ContinueOnError = false }
let private snapshot =
    { SchemaVersion = 1
      Repository = "FS-GG/example"
      ProfileSeal = digest
      PrerequisiteReceiptDigest = digest
      AuthorityEvidenceSha256 = digest
      SourceRevision = revision
      ObservedAt = observedAt
      Complete = true
      ClassicComplete = true
      RulesetsComplete = true
      ProducersComplete = true
      Requirements =
        [ requirement ClassicProtection "build" (Some 15368L)
          requirement (Ruleset 42L) "build" (Some 15368L)
          requirement (Ruleset 42L) "policy" None ]
      Producers = [ producer "build" (Some 15368L); producer "policy" None ] }
let private compile value = RequiredCheckCensusAdapter.compile observedAt (TimeSpan.FromHours 1) value

[<Fact>]
let ``census unions classic and ruleset authorities while retaining provenance`` () =
    let report = compile snapshot |> Result.defaultWith (failwithf "baseline refused: %A")
    Assert.Equal(2, report.Aggregate.RequiredCount)
    Assert.Equal(1, report.Aggregate.DualSourceCount)
    Assert.Equal(1, report.Aggregate.RulesetOnlyCount)
    Assert.Equal(1, report.Aggregate.IntegrationBoundCount)
    Assert.True(report.Aggregate.PullRequestReady)
    Assert.True(report.Aggregate.MergeGroupReady)
    Assert.Equal<RequiredCheckSource list>([ ClassicProtection; Ruleset 42L ], report.Entries.Head.Sources)
    Assert.DoesNotContain("build", string report.Aggregate)

[<Fact>]
let ``census is stable under authority and producer ordering`` () =
    let expected = compile snapshot
    let reordered = { snapshot with Requirements = List.rev snapshot.Requirements; Producers = List.rev snapshot.Producers }
    Assert.Equal(expected, compile reordered)

[<Fact>]
let ``census exact seal rejects changed source bindings`` () =
    let report = compile snapshot |> Result.defaultWith (failwithf "baseline refused: %A")
    Assert.Equal(Ok report, RequiredCheckCensusAdapter.verify report.Seal observedAt (TimeSpan.FromHours 1) snapshot)
    let changed = { snapshot with SourceRevision = String.replicate 40 "c" }
    Assert.Equal(Error [ AlteredRequiredCheckCensusSeal ], RequiredCheckCensusAdapter.verify report.Seal observedAt (TimeSpan.FromHours 1) changed)

[<Fact>]
let ``mixed integration identities for one context refuse`` () =
    let changed = { snapshot with Requirements = requirement ClassicProtection "build" None :: snapshot.Requirements }
    match compile changed with
    | Error findings -> Assert.Contains(AmbiguousRequiredCheckContext "build", findings)
    | Ok _ -> failwith "ambiguous identity was accepted"

[<Theory>]
[<InlineData("pull-request")>]
[<InlineData("merge-group")>]
[<InlineData("path-filter")>]
[<InlineData("branch-filter")>]
[<InlineData("activity-type")>]
[<InlineData("condition")>]
[<InlineData("continue-on-error")>]
[<InlineData("dependency")>]
let ``complete noncompliant producer routes are classified not refused`` mutation =
    let first = snapshot.Producers.Head
    let changedProducer =
        match mutation with
        | "pull-request" -> { first with PullRequest = { event with Declared = false } }
        | "merge-group" -> { first with MergeGroup = { event with Declared = false } }
        | "path-filter" -> { first with PullRequest = { event with PathFilters = [ "src/**" ] } }
        | "branch-filter" -> { first with PullRequest = { event with BranchFilters = [ "main" ] } }
        | "activity-type" -> { first with PullRequest = { event with ActivityTypes = [ "synchronize" ] } }
        | "condition" -> { first with Conditional = true }
        | "continue-on-error" -> { first with ContinueOnError = true }
        | "dependency" -> { first with DependenciesComplete = false }
        | value -> failwith value
    let changed = { snapshot with Producers = changedProducer :: snapshot.Producers.Tail }
    let report = compile changed |> Result.defaultWith (failwithf "complete observation refused: %A")
    if mutation = "merge-group" then Assert.False(report.Aggregate.MergeGroupReady)
    else Assert.False(report.Aggregate.PullRequestReady)

[<Fact>]
let ``complete conditional census retains exact production evidence`` () =
    let first = snapshot.Producers.Head
    let changedProducer =
        { first with
            MergeGroup = { event with Declared = false }
            Conditional = true }
    let changed = { snapshot with Producers = changedProducer :: snapshot.Producers.Tail }
    let report = compile changed |> Result.defaultWith (failwithf "complete observation refused: %A")
    let entry = report.Entries |> List.find (fun value -> value.Context = "build")
    Assert.False(report.Aggregate.PullRequestReady)
    Assert.False(report.Aggregate.MergeGroupReady)
    Assert.True(entry.PullRequest.Declared)
    Assert.False(entry.MergeGroup.Declared)
    Assert.True(entry.Conditional)
    Assert.Equal(digest, entry.ProducerWorkflowSha256)

[<Fact>]
let ``missing duplicate orphan and cross-repository producers refuse`` () =
    Assert.True(compile { snapshot with Producers = snapshot.Producers.Tail } |> Result.isError)
    Assert.True(compile { snapshot with Producers = snapshot.Producers @ [ snapshot.Producers.Head ] } |> Result.isError)
    Assert.True(compile { snapshot with Producers = producer "orphan" None :: snapshot.Producers } |> Result.isError)
    let cross = { snapshot.Producers.Head with Repository = "FS-GG/other" }
    Assert.True(compile { snapshot with Producers = cross :: snapshot.Producers.Tail } |> Result.isError)

[<Fact>]
let ``partial stale and invalid binding observations refuse`` () =
    Assert.True(compile { snapshot with RulesetsComplete = false } |> Result.isError)
    Assert.True(RequiredCheckCensusAdapter.compile (observedAt.AddHours 2) (TimeSpan.FromHours 1) snapshot |> Result.isError)
    Assert.True(compile { snapshot with ProfileSeal = "not-a-digest" } |> Result.isError)
    Assert.True(compile { snapshot with AuthorityEvidenceSha256 = "not-a-digest" } |> Result.isError)
    let invalidWorkflowDigest = { snapshot.Producers.Head with WorkflowSha256 = "not-a-digest" }
    Assert.True(compile { snapshot with Producers = invalidWorkflowDigest :: snapshot.Producers.Tail } |> Result.isError)

[<Fact>]
let ``exact verification rejects a producer rename at the same source revision`` () =
    let report = compile snapshot |> Result.defaultWith (failwithf "baseline refused: %A")
    let renamed = { snapshot.Producers.Head with Workflow = ".github/workflows/renamed.yml" }
    let changed = { snapshot with Producers = renamed :: snapshot.Producers.Tail }
    Assert.Equal(Error [ AlteredRequiredCheckCensusSeal ], RequiredCheckCensusAdapter.verify report.Seal observedAt (TimeSpan.FromHours 1) changed)

[<Fact>]
let ``qualification requires complete generated and independent mutation inventories`` () =
    let passing: GitHubRequiredCheckCensusControlResult list =
        GitHubRequiredCheckCensusQualification.requiredControls
        |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok(), GitHubRequiredCheckCensusQualification.validate passing passing)
    let broken = passing |> List.map (fun result -> if result.Control = MergeGroupProduction then { result with MutationRed = false } else result)
    match GitHubRequiredCheckCensusQualification.validate passing broken with
    | Ok _ -> Assert.Fail("merge-group mutation survived independent qualification")
    | Error values -> Assert.Contains("RC-MUTATION-SURVIVED", values |> List.map _.Code)
