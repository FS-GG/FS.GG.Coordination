module FS.GG.Coordination.GitHubPermissionCompilationTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private read name = { Name = name; Level = PermissionRead }
let private write name = { Name = name; Level = PermissionWrite }
let private registration id operation principal app environment appPermissions workflowPermissions =
    { Id = id; Operation = operation; PrincipalClass = principal; AppPrincipal = app; Environment = environment
      DeclaredAppPermissions = appPermissions; DeclaredWorkflowPermissions = workflowPermissions }
let private baseline =
    { SchemaVersion = 1; Repository = "FS-GG/FS.GG.Coordination"; SourceRevision = String.replicate 40 "a"
      RoadmapRevision = String.replicate 40 "b"; RoadmapSha256 = String.replicate 64 "c"
      PrerequisiteReceiptDigest = String.replicate 64 "d"
      PermissionCensusPath = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/permission-census.json"
      PermissionCensusSha256 = String.replicate 64 "e"
      RequiredPermissionFamilies =
        [ "actions-administration"; "organization-administration"; "project-administration"
          "release-administration"; "repository-administration"; "security-administration" ]
      Complete = true
      Registrations =
        [ registration "admin-cutover" ApplyRepositoryCutover AdminCutover "coordination-admin-app" "coordination-admin"
            [ write "administration"; write "actions"; read "contents" ] [ write "actions"; read "contents" ]
          registration "coordination-inspect" InspectCoordination NormalCoordination "coordination-app" "coordination"
            [ read "contents"; read "metadata" ] [ read "contents" ]
          registration "coordination-issue" CoordinateIssue NormalCoordination "coordination-app" "coordination"
            [ write "issues"; read "metadata"; write "pull_requests" ] [ read "contents"; write "issues"; write "pull-requests" ]
          registration "release-publish" PublishRelease Release "release-app" "release"
            [ write "contents"; read "metadata"; write "packages" ] [ write "attestations"; write "contents"; write "id-token"; write "packages" ] ] }
let private compile value = GitHubPermissionCompilationQualification.compile value
let private findings = function Error values -> values | Ok _ -> []

[<Fact>]
let ``complete interpreter inventory compiles least privilege into isolated classes`` () =
    let report = compile baseline |> Result.defaultWith (failwithf "%A")
    Assert.Equal(4, report.InterpreterCount)
    Assert.Equal(2, report.NormalCount)
    Assert.Equal(1, report.AdminCutoverCount)
    Assert.Equal(1, report.ReleaseCount)
    Assert.Equal<string list>([ "coordination"; "coordination"; "coordination-admin"; "release" ], report.Interpreters |> List.map _.Environment |> List.sort)

[<Fact>]
let ``ordering is stable and exact replay is seal bound`` () =
    let report = compile baseline |> Result.defaultWith (failwithf "%A")
    Assert.Equal(compile baseline, compile { baseline with Registrations = List.rev baseline.Registrations })
    Assert.Equal(Ok report, GitHubPermissionCompilationQualification.verify report.Seal baseline)
    Assert.Equal(Error [ AlteredPermissionCompilationSeal ], GitHubPermissionCompilationQualification.verify (String.replicate 64 "0") baseline)

[<Fact>]
let ``missing duplicate and incomplete interpreter inventories fail closed`` () =
    Assert.Contains(MissingInterpreterOperation ApplyRepositoryCutover, compile { baseline with Registrations = baseline.Registrations.Tail } |> findings)
    let duplicate = baseline.Registrations.Head
    Assert.Contains(DuplicateInterpreterId duplicate.Id, compile { baseline with Registrations = duplicate :: baseline.Registrations } |> findings)
    Assert.Contains(IncompleteInterpreterInventory, compile { baseline with Complete = false } |> findings)

[<Fact>]
let ``wildcard undeclared elevated and missing permissions fail closed`` () =
    let head = baseline.Registrations.Head
    let wildcard = { head with DeclaredAppPermissions = write "*" :: head.DeclaredAppPermissions }
    Assert.True(compile { baseline with Registrations = wildcard :: baseline.Registrations.Tail } |> Result.isError)
    let extra = { head with DeclaredWorkflowPermissions = write "packages" :: head.DeclaredWorkflowPermissions }
    Assert.Contains(UndeclaredOrOverprivilegedPermission(head.Id, "workflow:packages:write"), compile { baseline with Registrations = extra :: baseline.Registrations.Tail } |> findings)
    let missing = { head with DeclaredAppPermissions = head.DeclaredAppPermissions.Tail }
    Assert.True(compile { baseline with Registrations = missing :: baseline.Registrations.Tail } |> Result.isError)

[<Fact>]
let ``normal admin and release principal or environment crossover fails closed`` () =
    let normal = baseline.Registrations |> List.find (fun value -> value.PrincipalClass = NormalCoordination)
    let crossedPrincipal = { normal with AppPrincipal = "release-app" }
    Assert.Contains(InvalidPrincipalBinding normal.Id, compile { baseline with Registrations = crossedPrincipal :: (baseline.Registrations |> List.filter (fun value -> value.Id <> normal.Id)) } |> findings)
    let crossedEnvironment = { normal with Environment = "coordination-admin" }
    Assert.Contains(InvalidEnvironmentBinding normal.Id, compile { baseline with Registrations = crossedEnvironment :: (baseline.Registrations |> List.filter (fun value -> value.Id <> normal.Id)) } |> findings)

[<Fact>]
let ``qualification requires independent and generated controls`` () =
    let passing =
        GitHubPermissionCompilationQualification.requiredControls
        |> List.map (fun control ->
            { GitHubPermissionCompilationControlResult.Control = control
              ControlPassed = true
              BaselineGreen = true })
    Assert.Equal(Ok(), GitHubPermissionCompilationQualification.validate passing passing)
    let broken = passing |> List.map (fun value -> if value.Control = LeastPrivilegeWorkflow then { value with ControlPassed = false } else value)
    Assert.True(GitHubPermissionCompilationQualification.validate passing broken |> Result.isError)
