namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

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
    let Schema = "fsgg.coordination.github-roadmap-intake/1"

    let private allowedFields = set [ "contract"; "effort"; "phase"; "priority"; "severity"; "touch-set"; "workstream" ]
    let private clean (value: string) = if isNull value then "" else value.Trim()
    let private valid value = let value = clean value in value <> "" && value = value.Trim()
    let private issueTypeText = function RoadmapIssueType.Epic -> "Epic" | RoadmapIssueType.Feature -> "Feature" | RoadmapIssueType.Task -> "Task" | RoadmapIssueType.Bug -> "Bug" | RoadmapIssueType.Decision -> "Decision"
    let private kindText = function RoadmapEffectKind.UpsertIssue -> "upsert" | RoadmapEffectKind.SetParent -> "parent" | RoadmapEffectKind.SetDependency -> "dependency" | RoadmapEffectKind.SetStart -> "start" | RoadmapEffectKind.SetTarget -> "target" | RoadmapEffectKind.SetField -> "field" | RoadmapEffectKind.EnsureProjectProjection -> "project"
    let private frame (value: string) = string (Encoding.UTF8.GetByteCount value) + ":" + value
    let private hash values = values |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let private diagnostic code path message = { Code = code; Path = path; Message = message }
    let private optionText = Option.defaultValue ""
    let private canonicalFields (fields: RoadmapField list) : RoadmapField list = fields |> List.map (fun value -> { Name = (clean value.Name).ToLowerInvariant(); Value = clean value.Value }) |> List.sortBy (fun value -> value.Name, value.Value)
    let private canonicalNode (node: RoadmapNode) : RoadmapNode = { node with Key = clean node.Key; Repository = clean node.Repository; Title = clean node.Title; Body = clean node.Body; Dependencies = node.Dependencies |> List.map clean |> List.sort; Fields = canonicalFields node.Fields; Start = node.Start |> Option.map clean; Target = node.Target |> Option.map clean }
    let private canonical (definition: RoadmapDefinition) : RoadmapDefinition = { definition with Schema = clean definition.Schema; Identity = clean definition.Identity; Revision = clean definition.Revision; Nodes = definition.Nodes |> List.map canonicalNode |> List.sortBy _.Key }
    let private dateValid value = match DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with true, parsed -> Some parsed | _ -> None
    let private hasCycle edges =
        let graph = edges |> List.groupBy fst |> Map.ofList |> Map.map (fun _ values -> values |> List.map snd)
        let rec visit path node =
            if Set.contains node path then true
            else graph |> Map.tryFind node |> Option.defaultValue [] |> List.exists (visit (Set.add node path))
        graph |> Map.toList |> List.exists (fst >> visit Set.empty)
    let private nodeText (node: RoadmapNode) =
        [ node.Key; node.Repository; issueTypeText node.IssueType; node.Title; node.Body; optionText node.Parent
          String.concat "," node.Dependencies; optionText node.Start; optionText node.Target
          node.Fields |> List.map (fun field -> field.Name + "=" + field.Value) |> String.concat "," ] |> String.concat "|"
    let private effectText (effect: RoadmapEffect) = String.concat "|" [ string effect.Ordinal; kindText effect.Kind; effect.Key; effect.Argument; effect.ExpectedRevision ]
    let private seal identity revision cost effects = hash ([ Schema; identity; revision; string cost.AuthorityReads; string cost.MaximumEffects ] @ (effects |> List.map effectText))

    let validate definition =
        if obj.ReferenceEquals(definition, null) then Error [ diagnostic "ROADMAP-NULL" "$" "roadmap is required" ]
        elif obj.ReferenceEquals(definition.Nodes, null) then Error [ diagnostic "ROADMAP-NODES" "$.nodes" "nodes are required" ]
        else
            let value = canonical definition
            let keys = value.Nodes |> List.map _.Key
            let keySet = Set.ofList keys
            let parentEdges = value.Nodes |> List.choose (fun node -> node.Parent |> Option.map (fun parent -> node.Key, clean parent))
            let dependencyEdges = value.Nodes |> List.collect (fun node -> node.Dependencies |> List.map (fun dependency -> node.Key, dependency))
            let roots = value.Nodes |> List.filter (fun node -> node.Parent.IsNone)
            let findings =
                [ if value.Schema <> Schema then yield diagnostic "ROADMAP-SCHEMA" "$.schema" "schema is unsupported"
                  if not (valid value.Identity) then yield diagnostic "ROADMAP-IDENTITY" "$.identity" "identity is required"
                  if not (valid value.Revision) then yield diagnostic "ROADMAP-REVISION" "$.revision" "revision is required"
                  if List.isEmpty value.Nodes then yield diagnostic "ROADMAP-EMPTY" "$.nodes" "at least one node is required"
                  for key, values in keys |> List.groupBy id do if values.Length > 1 then yield diagnostic "ROADMAP-DUPLICATE-KEY" ("$.nodes[" + key + "]") "stable key is duplicated"
                  if roots.Length <> 1 || (roots.Length = 1 && roots.Head.IssueType <> RoadmapIssueType.Epic) then yield diagnostic "ROADMAP-ROOT" "$.nodes" "exactly one root Epic is required"
                  for node in value.Nodes do
                      let path = "$.nodes[" + node.Key + "]"
                      if not (valid node.Key && valid node.Repository && valid node.Title && valid node.Body) then yield diagnostic "ROADMAP-NODE-TEXT" path "key, repository, title, and body are required"
                      if node.IssueType = RoadmapIssueType.Epic && node.Parent.IsSome then yield diagnostic "ROADMAP-EPIC-PARENT" (path + ".parent") "an Epic cannot have a parent"
                      if node.IssueType <> RoadmapIssueType.Epic && node.Parent.IsNone then yield diagnostic "ROADMAP-PARENT-MISSING" (path + ".parent") "non-Epic work requires a parent"
                      for dependency in node.Dependencies do if not (Set.contains dependency keySet) || dependency = node.Key then yield diagnostic "ROADMAP-DEPENDENCY" (path + ".dependencies") "dependency must name another owned node"
                      if (node.Dependencies |> List.distinct).Length <> node.Dependencies.Length then yield diagnostic "ROADMAP-DEPENDENCY-DUPLICATE" (path + ".dependencies") "dependency occurs more than once"
                      match node.Parent with Some parent when not (Set.contains (clean parent) keySet) || clean parent = node.Key -> yield diagnostic "ROADMAP-PARENT" (path + ".parent") "parent must name another owned node" | _ -> ()
                      for field in node.Fields do
                          if not (Set.contains field.Name allowedFields) || not (valid field.Value) then yield diagnostic "ROADMAP-FIELD" (path + ".fields") "field is unknown or empty"
                      for name, values in node.Fields |> List.groupBy _.Name do if values.Length > 1 then yield diagnostic "ROADMAP-FIELD-DUPLICATE" (path + ".fields." + name) "field occurs more than once"
                      match node.Start, node.Target with
                      | Some start, Some target -> match dateValid start, dateValid target with Some left, Some right when left <= right -> () | _ -> yield diagnostic "ROADMAP-DATE" (path + ".dates") "dates must be ISO and start must not follow target"
                      | Some date, None | None, Some date -> if dateValid date |> Option.isNone then yield diagnostic "ROADMAP-DATE" (path + ".dates") "date must be ISO"
                      | None, None -> ()
                  if hasCycle parentEdges then yield diagnostic "ROADMAP-HIERARCHY-CYCLE" "$.nodes" "parent graph contains a cycle"
                  if hasCycle dependencyEdges then yield diagnostic "ROADMAP-DEPENDENCY-CYCLE" "$.nodes" "dependency graph contains a cycle" ]
            if List.isEmpty findings then Ok value else Error findings

    let plan definition observation =
        match validate definition with
        | Error findings -> Error findings
        | Ok roadmap when obj.ReferenceEquals(observation, null) || not observation.Complete -> Error [ diagnostic "ROADMAP-OBSERVATION-INCOMPLETE" "$.observation" "complete bounded observation is required" ]
        | Ok roadmap when not (valid observation.Revision) || obj.ReferenceEquals(observation.Targets, null) -> Error [ diagnostic "ROADMAP-OBSERVATION" "$.observation" "revision and targets are required" ]
        | Ok roadmap ->
            let groups = observation.Targets |> List.groupBy _.Key |> Map.ofList
            let extraOwned = observation.Targets |> List.filter (fun target -> target.OwnerIdentity = roadmap.Identity && not (roadmap.Nodes |> List.exists (fun node -> node.Key = target.Key)))
            let collisions =
                [ for target in extraOwned do yield diagnostic "ROADMAP-OWNED-EXTRA" ("$.targets[" + target.Key + "]") "owned target is absent from the roadmap authority"
                  yield! roadmap.Nodes |> List.collect (fun node ->
                    match groups |> Map.tryFind node.Key with
                    | Some targets when targets.Length > 1 -> [ diagnostic "ROADMAP-TARGET-AMBIGUOUS" ("$.targets[" + node.Key + "]") "stable key resolved to multiple targets" ]
                    | Some [ target ] when target.OwnerIdentity <> roadmap.Identity -> [ diagnostic "ROADMAP-IDENTITY-COLLISION" ("$.targets[" + node.Key + "]") "stable key is owned by another roadmap" ]
                    | Some [ target ] when target.RoadmapRevision <> roadmap.Revision -> [ diagnostic "ROADMAP-STALE-TARGET" ("$.targets[" + node.Key + "]") "owned target revision is stale" ]
                    | _ -> []) ]
            if not (List.isEmpty collisions) then Error collisions else
            let mutable effects = []
            let add kind key argument = effects <- (kind, key, argument) :: effects
            for node in roadmap.Nodes do
                let target = groups |> Map.tryFind node.Key |> Option.bind List.tryExactlyOne
                let core = nodeText node
                let coreMatches = target |> Option.exists (fun value -> value.Repository = node.Repository && value.IssueType = node.IssueType && value.Title = node.Title && value.Body = node.Body)
                if not coreMatches then add RoadmapEffectKind.UpsertIssue node.Key core
                if target |> Option.exists (fun value -> value.Parent = node.Parent) |> not then node.Parent |> Option.iter (add RoadmapEffectKind.SetParent node.Key)
                let currentDependencies = target |> Option.map (fun value -> value.Dependencies |> List.sort) |> Option.defaultValue []
                if currentDependencies <> node.Dependencies then add RoadmapEffectKind.SetDependency node.Key (String.concat "," node.Dependencies)
                if target |> Option.exists (fun value -> value.Start = node.Start) |> not then add RoadmapEffectKind.SetStart node.Key (optionText node.Start)
                if target |> Option.exists (fun value -> value.Target = node.Target) |> not then add RoadmapEffectKind.SetTarget node.Key (optionText node.Target)
                let currentFields = target |> Option.map (fun value -> canonicalFields value.Fields) |> Option.defaultValue []
                if currentFields <> node.Fields then add RoadmapEffectKind.SetField node.Key (node.Fields |> List.map (fun field -> field.Name + "=" + field.Value) |> String.concat ",")
                if target.IsNone then add RoadmapEffectKind.EnsureProjectProjection node.Key roadmap.Identity
            let raw = List.rev effects
            let ordered = raw |> List.sortBy (fun (kind, key, argument) -> key, kindText kind, argument)
            let sealedEffects = ordered |> List.mapi (fun index (kind, key, argument) -> { Ordinal = index + 1; Kind = kind; Key = key; Argument = argument; ExpectedRevision = observation.Revision })
            let hierarchy = roadmap.Nodes |> List.sumBy (fun node -> if node.Parent.IsSome then 1 else 0)
            let cost = { AuthorityReads = 1 + roadmap.Nodes.Length + hierarchy + roadmap.Nodes.Length; MaximumEffects = roadmap.Nodes.Length * 6 + hierarchy }
            let digest = seal roadmap.Identity observation.Revision cost sealedEffects
            Ok { Schema = Schema; Identity = roadmap.Identity; ExpectedRevision = observation.Revision; Effects = sealedEffects; Cost = cost; Digest = digest }

    let validatePlan plan =
        not (obj.ReferenceEquals(plan, null)) && plan.Schema = Schema && plan.Effects = (plan.Effects |> List.mapi (fun index effect -> { effect with Ordinal = index + 1 })) && plan.Digest = seal plan.Identity plan.ExpectedRevision plan.Cost plan.Effects

    let inspect definition observation =
        match validate definition with
        | Error findings -> Error findings
        | Ok _ when obj.ReferenceEquals(observation, null) -> Error [ diagnostic "ROADMAP-OBSERVATION-INCOMPLETE" "$.observation" "complete bounded observation is required" ]
        | Ok roadmap ->
            let ownedExtras = observation.Targets |> List.filter (fun target -> target.OwnerIdentity = roadmap.Identity && not (roadmap.Nodes |> List.exists (fun node -> node.Key = target.Key)))
            let scoped = { observation with Targets = observation.Targets |> List.filter (fun target -> ownedExtras |> List.contains target |> not) }
            match plan roadmap scoped with
            | Error findings -> Error findings
            | Ok plan ->
                [ for target in ownedExtras do yield { Code = "ROADMAP-OWNED-EXTRA"; Key = target.Key; Surface = "issue"; Expected = "absent"; Actual = string target.Number }
                  for effect in plan.Effects do
                      if effect.Kind <> RoadmapEffectKind.EnsureProjectProjection then yield { Code = "ROADMAP-OWNED-DRIFT"; Key = effect.Key; Surface = kindText effect.Kind; Expected = effect.Argument; Actual = "different-or-absent" } ] |> Ok

    let applyControlled plan observation authorized supported indeterminate failAfter =
        if not (validatePlan plan) then Error RoadmapApplyFailure.InvalidPlan
        elif obj.ReferenceEquals(observation, null) || not observation.Complete || observation.Revision <> plan.ExpectedRevision then Error RoadmapApplyFailure.Stale
        elif not authorized then Error RoadmapApplyFailure.Unauthorized
        elif not supported then Error RoadmapApplyFailure.Unsupported
        elif indeterminate then Error RoadmapApplyFailure.Indeterminate
        else
            match failAfter with
            | Some accepted when accepted >= 0 && accepted < plan.Effects.Length -> Error(RoadmapApplyFailure.Partial accepted)
            | _ -> Ok { PlanDigest = plan.Digest; Applied = plan.Effects.Length; Replay = List.isEmpty plan.Effects }
