namespace FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
type RoadmapIssueType = Epic | Feature | Task | Bug | Decision
type RoadmapField = { Name: string; Value: string }
type RoadmapNode = { Key: string; Repository: string; IssueType: RoadmapIssueType; Title: string; Body: string; Parent: string option; Dependencies: string list; Start: string option; Target: string option; Fields: RoadmapField list }
type RoadmapDefinition = { Schema: string; Identity: string; Revision: string; Nodes: RoadmapNode list }
type RoadmapTarget = { Key: string; OwnerIdentity: string; RoadmapRevision: string; Repository: string; Number: int; IssueType: RoadmapIssueType; Title: string; Body: string; Parent: string option; Dependencies: string list; Start: string option; Target: string option; Fields: RoadmapField list; Projected: bool }
type RoadmapObservation = { Complete: bool; Revision: string; Targets: RoadmapTarget list; UnrelatedProjectItems: int; UnrelatedBacklogItems: int }
[<RequireQualifiedAccess>]
type RoadmapEffectKind = UpsertIssue | SetParent | SetDependency | SetStart | SetTarget | SetField | EnsureProjectProjection
type RoadmapEffect = { Ordinal: int; Kind: RoadmapEffectKind; Key: string; Argument: string; ExpectedRevision: string }
type RoadmapCost = { AuthorityReads: int; MaximumEffects: int }
type RoadmapPlan = { Schema: string; Identity: string; ExpectedRevision: string; Effects: RoadmapEffect list; Cost: RoadmapCost; Digest: string }
type RoadmapDiagnostic = { Code: string; Path: string; Message: string }
type RoadmapDrift = { Code: string; Key: string; Surface: string; Expected: string; Actual: string }
[<RequireQualifiedAccess>]
type RoadmapApplyFailure = InvalidPlan | Stale | Unauthorized | Unsupported | Indeterminate | Partial of accepted: int
type RoadmapApplyReceipt = { PlanDigest: string; Applied: int; Replay: bool }

[<RequireQualifiedAccess>]
module RoadmapIntakeAdapter =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-roadmap-intake/1"
    val validate: RoadmapDefinition -> Result<RoadmapDefinition, RoadmapDiagnostic list>
    val plan: RoadmapDefinition -> RoadmapObservation -> Result<RoadmapPlan, RoadmapDiagnostic list>
    val validatePlan: RoadmapPlan -> bool
    val inspect: RoadmapDefinition -> RoadmapObservation -> Result<RoadmapDrift list, RoadmapDiagnostic list>
    val applyControlled: RoadmapPlan -> RoadmapObservation -> authorized: bool -> supported: bool -> indeterminate: bool -> failAfter: int option -> Result<RoadmapApplyReceipt, RoadmapApplyFailure>
