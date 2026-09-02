namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text

type GitHubPermissionLevel = PermissionRead | PermissionWrite
type GitHubPrincipalClass = NormalCoordination | AdminCutover | Release
type GitHubInterpreterOperation = InspectCoordination | CoordinateIssue | ApplyRepositoryCutover | PublishRelease
type GitHubPermission = { Name: string; Level: GitHubPermissionLevel }
type GitHubInterpreterRegistration =
    { Id: string; Operation: GitHubInterpreterOperation; PrincipalClass: GitHubPrincipalClass
      AppPrincipal: string; Environment: string
      DeclaredAppPermissions: GitHubPermission list; DeclaredWorkflowPermissions: GitHubPermission list }
type GitHubPermissionCompilationSnapshot =
    { SchemaVersion: int; Repository: string; SourceRevision: string; RoadmapRevision: string
      RoadmapSha256: string; PrerequisiteReceiptDigest: string; Complete: bool
      Registrations: GitHubInterpreterRegistration list }
type GitHubCompiledInterpreterPermission =
    { Id: string; Operation: GitHubInterpreterOperation; PrincipalClass: GitHubPrincipalClass
      AppPrincipal: string; Environment: string
      AppPermissions: GitHubPermission list; WorkflowPermissions: GitHubPermission list }
type GitHubPermissionCompilationReport =
    { Repository: string; SourceRevision: string; InterpreterCount: int; NormalCount: int
      AdminCutoverCount: int; ReleaseCount: int; Interpreters: GitHubCompiledInterpreterPermission list; Seal: string }
type GitHubPermissionCompilationFinding =
    | InvalidPermissionCompilationField of string
    | IncompleteInterpreterInventory
    | DuplicateInterpreterId of string
    | DuplicateInterpreterOperation of GitHubInterpreterOperation
    | MissingInterpreterOperation of GitHubInterpreterOperation
    | InvalidPrincipalBinding of string
    | InvalidEnvironmentBinding of string
    | WildcardPermission of string
    | DuplicatePermission of string * string
    | UndeclaredOrOverprivilegedPermission of string * string
    | MissingLeastPrivilegePermission of string * string
    | AlteredPermissionCompilationSeal
type GitHubPermissionCompilationControl =
    | PermissionPrerequisite | PermissionCompleteness | PermissionSourceBinding | InterpreterInventory
    | InterpreterUniqueness | LeastPrivilegeApp | LeastPrivilegeWorkflow | NoWildcardPermission
    | NoPermissionEscalation | NormalPrincipalSeparation | AdminPrincipalSeparation | ReleasePrincipalSeparation
    | EnvironmentSeparation | StablePermissionOrdering | ExactPermissionSeal | ExactPermissionReplay
    | QuintPermissionUnchanged | NoPermissionMutationSurface
type GitHubPermissionCompilationControlResult =
    { Control: GitHubPermissionCompilationControl; ControlPassed: bool; BaselineGreen: bool }
type GitHubPermissionCompilationQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubPermissionCompilationQualification =
    let requiredControls =
        [ PermissionPrerequisite; PermissionCompleteness; PermissionSourceBinding; InterpreterInventory
          InterpreterUniqueness; LeastPrivilegeApp; LeastPrivilegeWorkflow; NoWildcardPermission
          NoPermissionEscalation; NormalPrincipalSeparation; AdminPrincipalSeparation; ReleasePrincipalSeparation
          EnvironmentSeparation; StablePermissionOrdering; ExactPermissionSeal; ExactPermissionReplay
          QuintPermissionUnchanged; NoPermissionMutationSurface ]

    let controlId = function
        | PermissionPrerequisite -> "prerequisite-receipt"
        | PermissionCompleteness -> "corpus-completeness"
        | PermissionSourceBinding -> "source-binding"
        | InterpreterInventory -> "interpreter-inventory"
        | InterpreterUniqueness -> "interpreter-uniqueness"
        | LeastPrivilegeApp -> "least-privilege-app"
        | LeastPrivilegeWorkflow -> "least-privilege-workflow"
        | NoWildcardPermission -> "no-wildcard-permission"
        | NoPermissionEscalation -> "no-permission-escalation"
        | NormalPrincipalSeparation -> "normal-principal-separation"
        | AdminPrincipalSeparation -> "admin-principal-separation"
        | ReleasePrincipalSeparation -> "release-principal-separation"
        | EnvironmentSeparation -> "environment-separation"
        | StablePermissionOrdering -> "stable-ordering"
        | ExactPermissionSeal -> "exact-seal"
        | ExactPermissionReplay -> "exact-replay"
        | QuintPermissionUnchanged -> "quint-unchanged"
        | NoPermissionMutationSurface -> "no-mutation-surface"

    let requiredOperations = [ InspectCoordination; CoordinateIssue; ApplyRepositoryCutover; PublishRelease ]
    let private permission name level = { Name = name; Level = level }
    let private read name = permission name PermissionRead
    let private write name = permission name PermissionWrite
    let private operationName = function
        | InspectCoordination -> "inspect-coordination"
        | CoordinateIssue -> "coordinate-issue"
        | ApplyRepositoryCutover -> "apply-repository-cutover"
        | PublishRelease -> "publish-release"
    let private className = function NormalCoordination -> "normal" | AdminCutover -> "admin-cutover" | Release -> "release"
    let private levelName = function PermissionRead -> "read" | PermissionWrite -> "write"
    let private requirement = function
        | InspectCoordination ->
            NormalCoordination, "coordination-app", "coordination",
            [ read "contents"; read "metadata" ], [ read "contents" ]
        | CoordinateIssue ->
            NormalCoordination, "coordination-app", "coordination",
            [ write "issues"; read "metadata"; write "pull_requests" ],
            [ read "contents"; write "issues"; write "pull-requests" ]
        | ApplyRepositoryCutover ->
            AdminCutover, "coordination-admin-app", "coordination-admin",
            [ write "administration"; write "actions"; read "contents" ],
            [ write "actions"; read "contents" ]
        | PublishRelease ->
            Release, "release-app", "release",
            [ write "contents"; read "metadata"; write "packages" ],
            [ write "attestations"; write "contents"; write "id-token"; write "packages" ]
    let private permissionKey value = value.Name, levelName value.Level
    let private normalize values = values |> List.sortBy permissionKey
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private hash values =
        values |> String.concat "|" |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> _.ToLowerInvariant()
    let private validHex count (value: string) = value.Length = count && value |> Seq.forall Uri.IsHexDigit
    let private permissionFrames values =
        values |> normalize |> List.collect (fun value -> [ frame value.Name; frame (levelName value.Level) ])
    let private compileSeal snapshot registrations =
        [ frame (string snapshot.SchemaVersion); frame snapshot.Repository; frame snapshot.SourceRevision
          frame snapshot.RoadmapRevision; frame snapshot.RoadmapSha256; frame snapshot.PrerequisiteReceiptDigest
          frame (string snapshot.Complete)
          for value in registrations |> List.sortBy _.Id do
              frame value.Id; frame (operationName value.Operation); frame (className value.PrincipalClass)
              frame value.AppPrincipal; frame value.Environment
              yield! permissionFrames value.AppPermissions
              yield! permissionFrames value.WorkflowPermissions ] |> hash

    let compile snapshot =
        let findings = ResizeArray<GitHubPermissionCompilationFinding>()
        if snapshot.SchemaVersion <> 1 then findings.Add(InvalidPermissionCompilationField "schemaVersion")
        if String.IsNullOrWhiteSpace snapshot.Repository then findings.Add(InvalidPermissionCompilationField "repository")
        if not (validHex 40 snapshot.SourceRevision) then findings.Add(InvalidPermissionCompilationField "sourceRevision")
        if not (validHex 40 snapshot.RoadmapRevision) then findings.Add(InvalidPermissionCompilationField "roadmapRevision")
        if not (validHex 64 snapshot.RoadmapSha256) then findings.Add(InvalidPermissionCompilationField "roadmapSha256")
        if not (validHex 64 snapshot.PrerequisiteReceiptDigest) then findings.Add(InvalidPermissionCompilationField "prerequisiteReceiptDigest")
        if not snapshot.Complete then findings.Add IncompleteInterpreterInventory
        snapshot.Registrations |> List.groupBy _.Id |> List.iter (fun (id, values) ->
            if String.IsNullOrWhiteSpace id then findings.Add(InvalidPermissionCompilationField "interpreter.id")
            if values.Length <> 1 then findings.Add(DuplicateInterpreterId id))
        snapshot.Registrations |> List.groupBy _.Operation |> List.iter (fun (operation, values) ->
            if values.Length <> 1 then findings.Add(DuplicateInterpreterOperation operation))
        let present = snapshot.Registrations |> List.map _.Operation |> Set.ofList
        for operation in requiredOperations do if not (present.Contains operation) then findings.Add(MissingInterpreterOperation operation)
        let compiled =
            [ for registration in snapshot.Registrations do
                  let expectedClass, expectedPrincipal, expectedEnvironment, expectedApp, expectedWorkflow = requirement registration.Operation
                  if registration.PrincipalClass <> expectedClass || registration.AppPrincipal <> expectedPrincipal then
                      findings.Add(InvalidPrincipalBinding registration.Id)
                  if registration.Environment <> expectedEnvironment then findings.Add(InvalidEnvironmentBinding registration.Id)
                  let checkPermissions surface expected actual =
                      actual |> List.groupBy _.Name |> List.iter (fun (name, values) ->
                          if name = "*" || name.Contains("all", StringComparison.OrdinalIgnoreCase) then findings.Add(WildcardPermission $"{registration.Id}:{surface}:{name}")
                          if values.Length <> 1 then findings.Add(DuplicatePermission(registration.Id, $"{surface}:{name}")))
                      let expectedSet, actualSet = expected |> List.map permissionKey |> Set.ofList, actual |> List.map permissionKey |> Set.ofList
                      for name, level in Set.difference actualSet expectedSet do findings.Add(UndeclaredOrOverprivilegedPermission(registration.Id, $"{surface}:{name}:{level}"))
                      for name, level in Set.difference expectedSet actualSet do findings.Add(MissingLeastPrivilegePermission(registration.Id, $"{surface}:{name}:{level}"))
                  checkPermissions "app" expectedApp registration.DeclaredAppPermissions
                  checkPermissions "workflow" expectedWorkflow registration.DeclaredWorkflowPermissions
                  yield { Id = registration.Id; Operation = registration.Operation; PrincipalClass = registration.PrincipalClass
                          AppPrincipal = registration.AppPrincipal; Environment = registration.Environment
                          AppPermissions = normalize expectedApp; WorkflowPermissions = normalize expectedWorkflow } ]
        if findings.Count > 0 then Error(List.ofSeq findings)
        else
            let ordered = compiled |> List.sortBy _.Id
            Ok { Repository = snapshot.Repository; SourceRevision = snapshot.SourceRevision; InterpreterCount = ordered.Length
                 NormalCount = ordered |> List.filter (fun value -> value.PrincipalClass = NormalCoordination) |> List.length
                 AdminCutoverCount = ordered |> List.filter (fun value -> value.PrincipalClass = AdminCutover) |> List.length
                 ReleaseCount = ordered |> List.filter (fun value -> value.PrincipalClass = Release) |> List.length
                 Interpreters = ordered; Seal = compileSeal snapshot ordered }

    let verify expectedSeal snapshot =
        match compile snapshot with
        | Ok report when report.Seal = expectedSeal -> Ok report
        | Ok _ -> Error [ AlteredPermissionCompilationSeal ]
        | Error findings -> Error findings

    let validate generated independent =
        let expected = requiredControls |> List.map controlId |> Set.ofList
        let findingsFor source values =
            let grouped = values |> List.groupBy (fun value -> controlId value.Control)
            [ for missing in Set.difference expected (grouped |> List.map fst |> Set.ofList) do
                  { Code = "PC-CONTROL-MISSING"; ControlId = missing; Message = $"{source} omitted the required control" }
              for control, results in grouped do
                  if results.Length <> 1 then { Code = "PC-CONTROL-DUPLICATE"; ControlId = control; Message = $"{source} supplied the control more than once" }
                  else
                      if not results.Head.BaselineGreen then { Code = "PC-BASELINE-RED"; ControlId = control; Message = $"{source} baseline is not green" }
                      if not results.Head.ControlPassed then { Code = "PC-CONTROL-FAILED"; ControlId = control; Message = $"{source} control did not pass" } ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings
