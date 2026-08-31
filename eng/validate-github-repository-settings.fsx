#load "../src/FS.GG.Coordination.GitHub/RepositorySettingsAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubRepositorySettingsQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type Control = GitHubRepositorySettingsControl

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-repository-settings/contract.json")
if not (File.Exists fixturePath) then fail "GRSQ-FIXTURE-MISSING" fixturePath
let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath)
let json = fixture.RootElement
if json.EnumerateObject() |> Seq.map _.Name |> Seq.toList <> [ "controls"; "generated"; "schema"; "synthetic" ] then fail "GRSQ-FIXTURE-SHAPE" fixturePath
if json.GetProperty("schema").GetString() <> "fsgg.coordination.github-repository-settings-fixture/1" then fail "GRSQ-FIXTURE-SCHEMA" fixturePath
if not (json.GetProperty("synthetic").GetBoolean()) then fail "GRSQ-FIXTURE-PROVENANCE" fixturePath
let expected = GitHubRepositorySettingsQualification.requiredControls |> List.map GitHubRepositorySettingsQualification.controlId
let fixtureControls = json.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if fixtureControls <> expected then fail "GRSQ-FIXTURE-INVENTORY" (String.concat "," fixtureControls)

let generated = json.GetProperty("generated")
let identity =
    { NodeId = generated.GetProperty("nodeId").GetString()
      DatabaseId = generated.GetProperty("databaseId").GetInt64()
      Owner = generated.GetProperty("owner").GetString()
      Name = generated.GetProperty("name").GetString()
      DefaultBranch = generated.GetProperty("defaultBranch").GetString()
      SourceRepositoryNodeId = None }
let revision = generated.GetProperty("revision").GetString()
let setting surface subject name value: RepositorySetting = { Surface = surface; Subject = subject; Name = name; Value = value }
let baselineSettings =
    [ setting Repository "repository" "visibility" (Text "public")
      setting MergePolicy "repository" "allow-squash" (Boolean true)
      setting ActionsPolicy "repository" "allowed-actions" (Text "selected")
      setting Environments "production" "reviewers" (TextList [ "release-team" ])
      setting CodeSecurity "repository" "secret-scanning-enabled" (Boolean true) ]
let bySurface = baselineSettings |> List.groupBy _.Surface |> Map.ofList
let surfaceMap = RepositorySettingsAdapter.surfaces |> List.map (fun surface -> surface, Supported(revision, true, bySurface |> Map.tryFind surface |> Option.defaultValue [])) |> Map.ofList
let observation surfaces =
    { Identity = identity; CapturedRevision = revision; Surfaces = surfaces; Digest = RepositorySettingsAdapter.observationDigest identity revision surfaces }
let baseline = observation surfaceMap
let desired settings = { Identity = identity; Settings = settings; Digest = RepositorySettingsAdapter.desiredDigest identity settings }
let baselineDesired = desired baselineSettings
let targetSettings = setting CustomProperties "repository" "tier" (Text "critical") :: (baselineSettings |> List.map (fun value -> if value.Surface = Repository then { value with Value = Text "private" } else value))
let target = desired targetSettings
let plan = RepositorySettingsAdapter.plan revision baseline target |> Result.defaultWith (fail "GRSQ-PLAN" << sprintf "%A")
let baselineGreen () = RepositorySettingsAdapter.validate baseline |> Result.isOk && RepositorySettingsAdapter.plan revision baseline baselineDesired |> Result.map (fun value -> value.Operations.IsEmpty) = Ok true
let result control red: GitHubRepositorySettingsControlResult = { Control = control; MutationRed = red; BaselineGreen = baselineGreen () }
let replace (surface: SettingsSurface) (value: SurfaceObservation) = surfaceMap |> Map.add surface value |> observation
let withDigest (value: RepositorySettingsObservation) = { value with Digest = RepositorySettingsAdapter.observationDigest value.Identity value.CapturedRevision value.Surfaces }
let postSettings = targetSettings
let postBySurface = postSettings |> List.groupBy _.Surface |> Map.ofList
let postMap = RepositorySettingsAdapter.surfaces |> List.map (fun surface -> surface, Supported(revision, true, postBySurface |> Map.tryFind surface |> Option.defaultValue [])) |> Map.ofList
let poststate = observation postMap
let thirdTarget = desired (setting TagRulesets "v*" "signed-tags" (Boolean true) :: targetSettings)
let plan3 = RepositorySettingsAdapter.plan revision baseline thirdTarget |> Result.defaultWith (fail "GRSQ-PLAN3" << sprintf "%A")

let evaluate control =
    match control with
    | Control.IdentityDrift -> RepositorySettingsAdapter.plan revision baseline { target with Identity = { identity with Name = "other" } } = Error SettingsFailure.IdentityDrift
    | Control.MissingSurface -> let partial = { baseline with Surfaces = baseline.Surfaces |> Map.remove ActionsPolicy } |> withDigest in RepositorySettingsAdapter.validate partial = Error(SettingsFailure.MissingSurface ActionsPolicy)
    | Control.PaginationIncomplete -> RepositorySettingsAdapter.validate (replace BranchRulesets (Supported(revision, false, []))) = Error(PartialSurface(BranchRulesets, "pagination incomplete"))
    | Control.UnauthorizedSurface -> RepositorySettingsAdapter.validate (replace Environments (Unauthorized "forbidden")) = Error(PartialSurface(Environments, "forbidden"))
    | Control.UnavailableSurface -> RepositorySettingsAdapter.validate (replace ReleasesAndTags (Unavailable "outage")) = Error(PartialSurface(ReleasesAndTags, "outage"))
    | Control.UnreadableSurface -> RepositorySettingsAdapter.validate (replace CodeSecurity (Unreadable "schema")) = Error(PartialSurface(CodeSecurity, "schema"))
    | Control.ContradictorySetting -> let duplicate = baselineSettings.Head :: baselineSettings in RepositorySettingsAdapter.plan revision baseline (desired duplicate) |> Result.isError
    | Control.SecretValue -> RepositorySettingsAdapter.plan revision baseline (desired (setting Environments "production" "deploy-token" (Text "forbidden") :: baselineSettings)) = Error(SecretValueForbidden "deploy-token")
    | Control.ObservationDigest -> RepositorySettingsAdapter.validate { baseline with Digest = String.replicate 64 "0" } = Error InvalidObservationDigest
    | Control.DesiredDigest -> RepositorySettingsAdapter.plan revision baseline { target with Digest = String.replicate 64 "0" } = Error InvalidDesiredDigest
    | Control.StaleObservation -> RepositorySettingsAdapter.plan "newer" baseline target = Error(SettingsFailure.StaleObservation("newer", revision))
    | Control.UnsupportedDesired -> let unsupported = replace ImmutableReleases (Unsupported "not enabled") in RepositorySettingsAdapter.plan revision unsupported (desired (setting ImmutableReleases "repository" "enabled" (Boolean true) :: baselineSettings)) = Error(UnsupportedDesiredSurface ImmutableReleases)
    | Control.MinimalPlan -> plan.Operations.Length = 2
    | Control.StableOrder -> plan.Operations = (plan.Operations |> List.sortBy (fun value -> RepositorySettingsAdapter.surfaces |> List.findIndex ((=) value.Surface), value.Subject, value.Name, value.OperationId))
    | Control.LeastPermission -> plan.Operations |> List.forall (fun value -> value.RequiredPermission = "administration:write")
    | Control.NoOp -> RepositorySettingsAdapter.plan revision baseline baselineDesired |> Result.map _.Operations = Ok []
    | Control.AmbiguousResponse -> RepositorySettingsAdapter.reconcile plan SettingsResponseUnknown baseline = SettingsRereadAndReplan
    | Control.PartialRollback -> match RepositorySettingsAdapter.reconcile plan (SettingsPartiallyApplied [ plan.Operations.Head.OperationId ]) baseline with SettingsRollback values -> values.Length = 1 | _ -> false
    | Control.PartialRepair -> match RepositorySettingsAdapter.reconcile plan3 (SettingsPartiallyApplied (plan3.Operations |> List.take 2 |> List.map _.OperationId)) baseline with SettingsForwardRepair values -> values.Length = 1 | _ -> false
    | Control.UnrelatedPreserved -> RepositorySettingsAdapter.reconcile plan SettingsAccepted poststate = SettingsVerified

let generatedResults () = GitHubRepositorySettingsQualification.requiredControls |> List.map (fun control -> result control (evaluate control))
let independentResults () =
    [ result Control.IdentityDrift (RepositorySettingsAdapter.plan revision baseline { baselineDesired with Identity = { identity with NodeId = "other" } } = Error SettingsFailure.IdentityDrift)
      result Control.MissingSurface (RepositorySettingsAdapter.validate ({ baseline with Surfaces = baseline.Surfaces |> Map.remove Repository } |> withDigest) = Error(SettingsFailure.MissingSurface Repository))
      result Control.PaginationIncomplete (RepositorySettingsAdapter.validate (replace TagRulesets (Supported(revision, false, []))) |> Result.isError)
      result Control.UnauthorizedSurface (RepositorySettingsAdapter.validate (replace ActionsPolicy (Unauthorized "denied")) |> Result.isError)
      result Control.UnavailableSurface (RepositorySettingsAdapter.validate (replace Repository (Unavailable "offline")) |> Result.isError)
      result Control.UnreadableSurface (RepositorySettingsAdapter.validate (replace DependencyControls (Unreadable "invalid")) |> Result.isError)
      result Control.ContradictorySetting (RepositorySettingsAdapter.plan revision baseline (desired (baselineSettings @ [ baselineSettings.Head ])) |> Result.isError)
      result Control.SecretValue (RepositorySettingsAdapter.plan revision baseline (desired (setting Environments "prod" "password" (Text "x") :: baselineSettings)) |> Result.isError)
      result Control.ObservationDigest (RepositorySettingsAdapter.validate { baseline with Digest = "bad" } = Error InvalidObservationDigest)
      result Control.DesiredDigest (RepositorySettingsAdapter.plan revision baseline { target with Digest = "bad" } = Error InvalidDesiredDigest)
      result Control.StaleObservation (RepositorySettingsAdapter.plan "stale" baseline target |> Result.isError)
      result Control.UnsupportedDesired (let value = replace CustomProperties (Unsupported "feature absent") in RepositorySettingsAdapter.plan revision value target |> Result.isError)
      result Control.MinimalPlan (plan.Operations |> List.map _.Name = [ "visibility"; "tier" ])
      result Control.StableOrder ((plan.Operations |> List.map _.OperationId |> Set.ofList).Count = plan.Operations.Length)
      result Control.LeastPermission (plan.Operations |> List.forall (fun operation -> operation.RequiredPermission.EndsWith(":write", StringComparison.Ordinal)))
      result Control.NoOp ((RepositorySettingsAdapter.plan revision baseline baselineDesired |> Result.defaultWith (fail "GRSQ-NOOP" << sprintf "%A")).Operations.IsEmpty)
      result Control.AmbiguousResponse (RepositorySettingsAdapter.reconcile plan SettingsAccepted baseline = SettingsRereadAndReplan)
      result Control.PartialRollback (match RepositorySettingsAdapter.reconcile plan (SettingsPartiallyApplied [ plan.Operations.Head.OperationId ]) baseline with SettingsRollback _ -> true | _ -> false)
      result Control.PartialRepair (match RepositorySettingsAdapter.reconcile plan3 (SettingsPartiallyApplied (plan3.Operations |> List.take 2 |> List.map _.OperationId)) baseline with SettingsForwardRepair _ -> true | _ -> false)
      result Control.UnrelatedPreserved (RepositorySettingsAdapter.reconcile plan SettingsResponseUnknown poststate = SettingsVerified) ]

let generatedResultsValue = generatedResults ()
let independentResultsValue = independentResults ()
match GitHubRepositorySettingsQualification.validate generatedResultsValue independentResultsValue with
| Ok () -> printfn "github-repository-settings-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generatedResultsValue.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GRSQ-FAILED" $"{findings.Length} finding(s)"
