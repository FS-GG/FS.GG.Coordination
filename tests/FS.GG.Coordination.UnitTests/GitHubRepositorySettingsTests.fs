module FS.GG.Coordination.GitHubRepositorySettingsTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private identity = { NodeId = "R_fixture"; DatabaseId = 42L; Owner = "FS-GG"; Name = "fixture"; DefaultBranch = "main"; SourceRepositoryNodeId = None }
let private revision = "revision-1"
let private setting (surface: SettingsSurface) subject name (value: SettingValue): RepositorySetting = { Surface = surface; Subject = subject; Name = name; Value = value }
let private settings = [ setting SettingsSurface.Repository "repository" "visibility" (SettingValue.Text "public"); setting SettingsSurface.MergePolicy "repository" "allow-squash" (SettingValue.Boolean true); setting SettingsSurface.ActionsPolicy "repository" "allowed-actions" (SettingValue.Text "selected") ]
let private surfaceMap (values: RepositorySetting list) =
    let grouped = values |> List.groupBy _.Surface |> Map.ofList
    RepositorySettingsAdapter.surfaces |> List.map (fun surface -> surface, SurfaceObservation.Supported(revision, true, grouped |> Map.tryFind surface |> Option.defaultValue [])) |> Map.ofList
let private observed (values: RepositorySetting list) =
    let surfaces = surfaceMap values
    { Identity = identity; CapturedRevision = revision; Surfaces = surfaces; Digest = RepositorySettingsAdapter.observationDigest identity revision surfaces }
let private desired (values: RepositorySetting list): DesiredRepositorySettings = { Identity = identity; Settings = values; Digest = RepositorySettingsAdapter.desiredDigest identity values }

[<Fact>]
let ``observation binds exact identity complete pagination and digest`` () =
    let baseline = observed settings
    Assert.Equal(Ok baseline, RepositorySettingsAdapter.validate baseline)
    Assert.Equal(Error InvalidObservationDigest, RepositorySettingsAdapter.validate { baseline with Digest = "bad" })
    let incompleteSurfaces = baseline.Surfaces |> Map.add SettingsSurface.BranchRulesets (SurfaceObservation.Supported(revision, false, []))
    let incomplete = { baseline with Surfaces = incompleteSurfaces; Digest = RepositorySettingsAdapter.observationDigest identity revision incompleteSurfaces }
    Assert.Equal(Error(PartialSurface(SettingsSurface.BranchRulesets, "pagination incomplete")), RepositorySettingsAdapter.validate incomplete)

[<Fact>]
let ``planning is minimal stable digest bound and least privilege`` () =
    let baseline = observed settings
    let wanted = desired (setting SettingsSurface.CustomProperties "repository" "tier" (SettingValue.Text "critical") :: (settings |> List.map (fun value -> if value.Surface = SettingsSurface.Repository then { value with Value = SettingValue.Text "private" } else value)))
    let plan = RepositorySettingsAdapter.plan revision baseline wanted |> Result.defaultWith (failwithf "%A")
    Assert.Equal<string list>([ "visibility"; "tier" ], plan.Operations |> List.map _.Name)
    Assert.All(plan.Operations, fun operation -> Assert.Equal("administration:write", operation.RequiredPermission); Assert.Equal(baseline.Digest, operation.ObservationDigest); Assert.Equal(wanted.Digest, operation.DesiredDigest))
    Assert.Equal(Ok [], RepositorySettingsAdapter.plan revision baseline (desired settings) |> Result.map _.Operations)

[<Fact>]
let ``partial observations stale revisions unsupported desired and secret values fail closed`` () =
    let baseline = observed settings
    let target = desired settings
    Assert.Equal(Error(SettingsFailure.StaleObservation("other", revision)), RepositorySettingsAdapter.plan "other" baseline target)
    let unsupportedSurfaces = baseline.Surfaces |> Map.add SettingsSurface.ImmutableReleases (SurfaceObservation.Unsupported "feature absent")
    let unsupported = { baseline with Surfaces = unsupportedSurfaces; Digest = RepositorySettingsAdapter.observationDigest identity revision unsupportedSurfaces }
    let immutable = desired (setting SettingsSurface.ImmutableReleases "repository" "enabled" (SettingValue.Boolean true) :: settings)
    Assert.Equal(Error(UnsupportedDesiredSurface SettingsSurface.ImmutableReleases), RepositorySettingsAdapter.plan revision unsupported immutable)
    let secret = desired (setting SettingsSurface.Environments "production" "deploy-token" (SettingValue.Text "must-not-serialize") :: settings)
    Assert.Equal(Error(SecretValueForbidden "deploy-token"), RepositorySettingsAdapter.plan revision baseline secret)

[<Fact>]
let ``reconciliation verifies only exact poststate and classifies partial repair`` () =
    let baseline = observed settings
    let wantedSettings = setting SettingsSurface.CustomProperties "repository" "tier" (SettingValue.Text "critical") :: (settings |> List.map (fun value -> if value.Surface = SettingsSurface.Repository then { value with Value = SettingValue.Text "private" } else value))
    let plan = RepositorySettingsAdapter.plan revision baseline (desired wantedSettings) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(SettingsRereadAndReplan, RepositorySettingsAdapter.reconcile plan SettingsResponseUnknown baseline)
    Assert.Equal(SettingsVerified, RepositorySettingsAdapter.reconcile plan SettingsResponseUnknown (observed wantedSettings))
    match RepositorySettingsAdapter.reconcile plan (SettingsPartiallyApplied [ plan.Operations.Head.OperationId ]) baseline with
    | SettingsRollback operations -> Assert.Single operations |> ignore
    | outcome -> failwithf "unexpected %A" outcome

[<Fact>]
let ``qualification inventory is closed and mutations must turn red`` () =
    let passing: GitHubRepositorySettingsControlResult list = GitHubRepositorySettingsQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubRepositorySettingsQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index value -> if index = 16 then { value with MutationRed = false } else value)
    match GitHubRepositorySettingsQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.ControlId = "ambiguous-response" && finding.Code = "GRSQ-INDEPENDENT-NOT-RED")
    | Ok () -> failwith "accepted a load-bearing mutation"
