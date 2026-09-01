module FS.GG.Coordination.GitHubRoadmapIntakeTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private node key issueType parent dependencies start target fields =
    { Key = key; Repository = "FS-GG/Product"; IssueType = issueType; Title = "Title " + key; Body = "Body " + key
      Parent = parent; Dependencies = dependencies; Start = start; Target = target
      Fields = fields |> List.map (fun (name, value) -> { Name = name; Value = value }) }
let private epic = node "roadmap" RoadmapIssueType.Epic None [] (Some "2026-09-01") (Some "2026-12-31") [ "phase", "delivery" ]
let private feature = node "feature-a" RoadmapIssueType.Feature (Some "roadmap") [] None None [ "priority", "High" ]
let private task = node "task-a" RoadmapIssueType.Task (Some "feature-a") [ "feature-a" ] None (Some "2026-10-01") [ "effort", "3" ]
let private roadmap nodes = { Schema = RoadmapIntakeAdapter.Schema; Identity = "roadmap:product:v2"; Revision = "source-42"; Nodes = nodes }
let private empty project backlog = { Complete = true; Revision = "observed-7"; Targets = []; UnrelatedProjectItems = project; UnrelatedBacklogItems = backlog }
let private target (node: RoadmapNode) projected =
    { Key = node.Key; OwnerIdentity = "roadmap:product:v2"; RoadmapRevision = "source-42"; Repository = node.Repository; Number = 1
      IssueType = node.IssueType; Title = node.Title; Body = node.Body; Parent = node.Parent; Dependencies = node.Dependencies
      Start = node.Start; Target = node.Target; Fields = node.Fields; Projected = projected }

[<Fact>]
let ``typed roadmap compiles deterministically to bounded native effects`` () =
    let definition = roadmap [ task; epic; feature ]
    let first = RoadmapIntakeAdapter.plan definition (empty 0 0) |> Result.defaultWith (failwithf "%A")
    let second = RoadmapIntakeAdapter.plan definition (empty 1000000 2000000) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(first, second)
    Assert.Matches("^[0-9a-f]{64}$", first.Digest)
    Assert.Equal(9, first.Cost.AuthorityReads)
    Assert.Equal(20, first.Cost.MaximumEffects)
    Assert.True(first.Effects |> List.exists (fun effect -> effect.Kind = RoadmapEffectKind.SetParent))
    Assert.True(first.Effects |> List.exists (fun effect -> effect.Kind = RoadmapEffectKind.SetDependency))
    Assert.True(first.Effects |> List.exists (fun effect -> effect.Kind = RoadmapEffectKind.SetStart))
    Assert.True(first.Effects |> List.exists (fun effect -> effect.Kind = RoadmapEffectKind.SetTarget))
    Assert.True(first.Effects |> List.exists (fun effect -> effect.Kind = RoadmapEffectKind.SetField))
    Assert.True(first.Effects |> List.exists (fun effect -> effect.Kind = RoadmapEffectKind.EnsureProjectProjection))
    Assert.True(RoadmapIntakeAdapter.validatePlan first)

[<Fact>]
let ``identity graph date field observation and sealed plan failures close`` () =
    let invalidDefinitions =
        [ roadmap [ { epic with IssueType = RoadmapIssueType.Task } ]
          roadmap [ epic; { feature with Parent = Some "missing" } ]
          roadmap [ epic; feature; { task with Dependencies = [ "task-a" ] } ]
          roadmap [ epic; { feature with Start = Some "2026-10-02"; Target = Some "2026-10-01" } ]
          roadmap [ epic; { feature with Fields = [ { Name = "status"; Value = "Done" } ] } ] ]
    Assert.All(invalidDefinitions, fun value -> Assert.True(RoadmapIntakeAdapter.validate value |> Result.isError))
    let definition = roadmap [ epic; feature ]
    let duplicate = target feature true
    Assert.True(RoadmapIntakeAdapter.plan definition { empty 0 0 with Targets = [ duplicate; { duplicate with Number = 2 } ] } |> Result.isError)
    Assert.True(RoadmapIntakeAdapter.plan definition { empty 0 0 with Targets = [ { duplicate with OwnerIdentity = "roadmap:foreign" } ] } |> Result.isError)
    Assert.True(RoadmapIntakeAdapter.plan definition { empty 0 0 with Targets = [ { duplicate with RoadmapRevision = "old" } ] } |> Result.isError)
    let plan = RoadmapIntakeAdapter.plan definition (empty 0 0) |> Result.defaultWith (failwithf "%A")
    Assert.False(RoadmapIntakeAdapter.validatePlan { plan with Effects = List.rev plan.Effects })
    Assert.Equal(Error RoadmapApplyFailure.InvalidPlan, RoadmapIntakeAdapter.applyControlled { plan with Digest = "altered" } (empty 0 0) true true false None)

[<Fact>]
let ``Project state is projection only while owned drift and controlled failures remain explicit`` () =
    let definition = roadmap [ epic; feature ]
    let satisfied = { empty 9 11 with Targets = [ target epic true; target feature true ] }
    let plan = RoadmapIntakeAdapter.plan definition satisfied |> Result.defaultWith (failwithf "%A")
    Assert.Empty(plan.Effects)
    Assert.Equal(Ok [], RoadmapIntakeAdapter.inspect definition { satisfied with UnrelatedProjectItems = 900000 })
    let projectionOnly = { satisfied with Targets = [ target epic false; target feature false ] }
    Assert.Equal(Ok [], RoadmapIntakeAdapter.inspect definition projectionOnly)
    Assert.Equal(plan, RoadmapIntakeAdapter.plan definition projectionOnly |> Result.defaultWith (failwithf "%A"))
    let drifted = { satisfied with Targets = [ target epic true; { (target feature true) with Title = "drift" } ] }
    let drift = RoadmapIntakeAdapter.inspect definition drifted |> Result.defaultWith (failwithf "%A")
    Assert.Contains(drift, fun value -> value.Key = "feature-a" && value.Surface = "upsert")
    let extra = { (target feature true) with Key = "retired"; Number = 99 }
    let extraDrift = RoadmapIntakeAdapter.inspect definition { satisfied with Targets = extra :: satisfied.Targets } |> Result.defaultWith (failwithf "%A")
    Assert.Contains(extraDrift, fun value -> value.Code = "ROADMAP-OWNED-EXTRA" && value.Key = "retired")
    Assert.True(RoadmapIntakeAdapter.plan definition { satisfied with Targets = extra :: satisfied.Targets } |> Result.isError)
    Assert.Equal(Ok { PlanDigest = plan.Digest; Applied = 0; Replay = true }, RoadmapIntakeAdapter.applyControlled plan satisfied true true false None)
    Assert.Equal(Error RoadmapApplyFailure.Unauthorized, RoadmapIntakeAdapter.applyControlled plan satisfied false true false None)
    Assert.Equal(Error RoadmapApplyFailure.Unsupported, RoadmapIntakeAdapter.applyControlled plan satisfied true false false None)
    Assert.Equal(Error RoadmapApplyFailure.Indeterminate, RoadmapIntakeAdapter.applyControlled plan satisfied true true true None)
    let creation = RoadmapIntakeAdapter.plan definition (empty 0 0) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(Error(RoadmapApplyFailure.Partial 1), RoadmapIntakeAdapter.applyControlled creation (empty 0 0) true true false (Some 1))

[<Fact>]
let ``roadmap qualification inventory requires independent mutation sensitivity`` () =
    let passing: GitHubRoadmapIntakeControlResult list = GitHubRoadmapIntakeQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(18, passing.Length)
    Assert.Equal(Ok (), GitHubRoadmapIntakeQualification.validate passing passing)
    Assert.True(GitHubRoadmapIntakeQualification.validate passing (List.tail passing) |> Result.isError)
