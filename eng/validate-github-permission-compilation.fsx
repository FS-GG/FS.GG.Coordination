#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubPermissionCompilationQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let defaultRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let root = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.tryFind Directory.Exists |> Option.map Path.GetFullPath |> Option.defaultValue defaultRoot
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-5")
let corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "corpus.json"))).AsObject()
let expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "independent-expectations.json"))).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let sha256File path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let operation = function
    | "inspect-coordination" -> InspectCoordination | "coordinate-issue" -> CoordinateIssue
    | "apply-repository-cutover" -> ApplyRepositoryCutover | "publish-release" -> PublishRelease
    | value -> failwith $"unknown interpreter operation: {value}"
let principalClass = function
    | "normal" -> NormalCoordination | "admin-cutover" -> AdminCutover | "release" -> Release
    | value -> failwith $"unknown principal class: {value}"
let permission (value: string) =
    match value.Split(':') with
    | [| name; "read" |] -> { Name = name; Level = PermissionRead }
    | [| name; "write" |] -> { Name = name; Level = PermissionWrite }
    | _ -> failwith $"unknown permission: {value}"
let registration (node: JsonNode) =
    let value = node.AsObject()
    { Id = text value "id"; Operation = text value "operation" |> operation
      PrincipalClass = text value "principalClass" |> principalClass
      AppPrincipal = text value "appPrincipal"; Environment = text value "environment"
      DeclaredAppPermissions = texts value "appPermissions" |> List.map permission
      DeclaredWorkflowPermissions = texts value "workflowPermissions" |> List.map permission }
let permissionCensusPath = text corpus "permissionCensusPath"
let permissionCensusFullPath = Path.Combine(root, permissionCensusPath)
let permissionCensus = JsonNode.Parse(File.ReadAllText(permissionCensusFullPath)).AsObject()
let permissionCensusContent = permissionCensus["content"].AsObject()
let permissionFamilies = texts permissionCensusContent "requiredPermissions"
let snapshot =
    { SchemaVersion = corpus["schemaVersion"].GetValue<int>(); Repository = text corpus "repository"
      SourceRevision = text corpus "sourceRevision"; RoadmapRevision = text corpus "roadmapRevision"
      RoadmapSha256 = text corpus "roadmapSha256"; PrerequisiteReceiptDigest = text corpus "prerequisiteReceiptDigest"
      PermissionCensusPath = permissionCensusPath
      PermissionCensusSha256 = text corpus "permissionCensusSha256"
      RequiredPermissionFamilies = permissionFamilies
      Complete = corpus["complete"].GetValue<bool>()
      Registrations = corpus["registrations"].AsArray() |> Seq.map registration |> List.ofSeq }
let compile value = GitHubPermissionCompilationQualification.compile value
let refused value = compile value |> Result.isError
let report = compile snapshot |> Result.defaultWith (failwithf "permission compilation baseline refused: %A")

if fsi.CommandLineArgs |> Array.contains "--mint" then printfn "%s" report.Seal else
    let receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-06.4.json"))).AsObject()
    if text receipt "digest" <> snapshot.PrerequisiteReceiptDigest then failwith "accepted GS2-06.4 receipt differs"
    if snapshot.SourceRevision <> "34fdebc438c04c81039c767a0d2bbbc13f060c47" then failwith "candidate source binding differs"
    if sha256File permissionCensusFullPath <> snapshot.PermissionCensusSha256 then failwith "canonical permission census bytes differ"
    if text permissionCensus "schema" <> "fsgg.quint.compiled-output/1" || text permissionCensus "family" <> "COUT-PermissionCensus" then failwith "canonical permission census identity differs"
    if snapshot.RoadmapRevision <> "96ed5fc67fa6f4a7d7251ea9c6540fa9fb60f412" || snapshot.RoadmapSha256 <> "889b5cde4bcd8f184d1982bfe75294eb511a72246dcba7ad6d6eab97cebd4df3" then failwith "accepted roadmap binding differs"
    let expectedIds = texts expectations "interpreterIds"
    let observedIds = report.Interpreters |> List.map _.Id
    if observedIds <> expectedIds then failwith "independent interpreter identity inventory differs"
    let expectedOperations = texts expectations "operationIds" |> Set.ofList
    let operationId = function InspectCoordination -> "inspect-coordination" | CoordinateIssue -> "coordinate-issue" | ApplyRepositoryCutover -> "apply-repository-cutover" | PublishRelease -> "publish-release"
    if (report.Interpreters |> List.map (fun value -> operationId value.Operation) |> Set.ofList) <> expectedOperations then failwith "independent operation inventory differs"
    let expectedEnvironments = texts expectations "environments" |> Set.ofList
    if (report.Interpreters |> List.map _.Environment |> Set.ofList) <> expectedEnvironments then failwith "independent environment inventory differs"
    let classCounts = expectations["principalClasses"].AsObject()
    if report.NormalCount <> classCounts["normal"].GetValue<int>() || report.AdminCutoverCount <> classCounts["admin-cutover"].GetValue<int>() || report.ReleaseCount <> classCounts["release"].GetValue<int>() then failwith "principal class counts differ"
    if report.Seal <> text expectations "expectedSeal" then failwith "baseline seal differs"
    if GitHubPermissionCompilationQualification.verify report.Seal snapshot <> Ok report then failwith "exact replay failed"
    let expectedControls = GitHubPermissionCompilationQualification.requiredControls |> List.map GitHubPermissionCompilationQualification.controlId
    if texts expectations "controls" <> expectedControls then failwith "independent control inventory differs"

    let replaceRegistration id map = { snapshot with Registrations = snapshot.Registrations |> List.map (fun value -> if value.Id = id then map value else value) }
    let generatedMutation = function
        | PermissionPrerequisite -> refused { snapshot with PrerequisiteReceiptDigest = "invalid" }
        | PermissionCompleteness -> refused { snapshot with Complete = false }
        | PermissionSourceBinding -> refused { snapshot with SourceRevision = "main" }
        | PermissionProducerAgreement -> refused { snapshot with RequiredPermissionFamilies = [ "attacker-invented-permission" ] }
        | InterpreterInventory -> refused { snapshot with Registrations = snapshot.Registrations.Tail }
        | InterpreterUniqueness -> refused { snapshot with Registrations = snapshot.Registrations.Head :: snapshot.Registrations }
        | LeastPrivilegeApp -> refused (replaceRegistration "coordination-inspect" (fun value -> { value with DeclaredAppPermissions = value.DeclaredAppPermissions.Tail }))
        | LeastPrivilegeWorkflow -> refused (replaceRegistration "coordination-issue" (fun value -> { value with DeclaredWorkflowPermissions = { Name = "packages"; Level = PermissionWrite } :: value.DeclaredWorkflowPermissions }))
        | NoWildcardPermission -> refused (replaceRegistration "admin-cutover" (fun value -> { value with DeclaredAppPermissions = { Name = "*"; Level = PermissionWrite } :: value.DeclaredAppPermissions }))
        | NoPermissionEscalation -> refused (replaceRegistration "coordination-inspect" (fun value -> { value with DeclaredAppPermissions = { Name = "contents"; Level = PermissionWrite } :: value.DeclaredAppPermissions.Tail }))
        | NormalPrincipalSeparation -> refused (replaceRegistration "coordination-issue" (fun value -> { value with AppPrincipal = "release-app" }))
        | AdminPrincipalSeparation -> refused (replaceRegistration "admin-cutover" (fun value -> { value with PrincipalClass = NormalCoordination }))
        | ReleasePrincipalSeparation -> refused (replaceRegistration "release-publish" (fun value -> { value with AppPrincipal = "coordination-admin-app" }))
        | EnvironmentSeparation -> refused (replaceRegistration "release-publish" (fun value -> { value with Environment = "coordination" }))
        | StablePermissionOrdering -> compile { snapshot with Registrations = List.rev snapshot.Registrations } = Ok report
        | ExactPermissionSeal -> GitHubPermissionCompilationQualification.verify (String.replicate 64 "0") snapshot |> Result.isError
        | ExactPermissionReplay -> GitHubPermissionCompilationQualification.verify report.Seal snapshot = Ok report
        | QuintPermissionUnchanged -> sha256File (Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")) = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoPermissionMutationSurface ->
            let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubPermissionCompilationQualification.fsi"))
            [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "PATCH"; "POST"; "DELETE" ] |> List.forall (surface.Contains >> not)
    let independentMutation = function
        | PermissionPrerequisite -> snapshot.PrerequisiteReceiptDigest = "9f2476ebea520372f836b69fc8b1d11300d5299ed1796fc34cc70afead9e2a76"
        | PermissionCompleteness -> snapshot.Complete && report.InterpreterCount = expectedIds.Length
        | PermissionSourceBinding -> snapshot.SourceRevision = "34fdebc438c04c81039c767a0d2bbbc13f060c47"
        | PermissionProducerAgreement ->
            sha256File permissionCensusFullPath = snapshot.PermissionCensusSha256
            && text permissionCensus "family" = "COUT-PermissionCensus"
            && snapshot.RequiredPermissionFamilies = texts corpus "requiredPermissionFamilies"
        | InterpreterInventory -> observedIds = expectedIds
        | InterpreterUniqueness -> observedIds.Length = (observedIds |> Set.ofList |> Set.count)
        | LeastPrivilegeApp -> report.Interpreters |> List.forall (fun value -> not value.AppPermissions.IsEmpty)
        | LeastPrivilegeWorkflow -> report.Interpreters |> List.forall (fun value -> not value.WorkflowPermissions.IsEmpty)
        | NoWildcardPermission -> report.Interpreters |> List.collect (fun value -> value.AppPermissions @ value.WorkflowPermissions) |> List.forall (fun value -> value.Name <> "*" && not (value.Name.Contains("all", StringComparison.OrdinalIgnoreCase)))
        | NoPermissionEscalation -> generatedMutation NoPermissionEscalation
        | NormalPrincipalSeparation -> report.Interpreters |> List.filter (fun value -> value.PrincipalClass = NormalCoordination) |> List.forall (fun value -> value.AppPrincipal = "coordination-app" && value.Environment = "coordination")
        | AdminPrincipalSeparation -> report.Interpreters |> List.filter (fun value -> value.PrincipalClass = AdminCutover) |> List.forall (fun value -> value.AppPrincipal = "coordination-admin-app" && value.Environment = "coordination-admin")
        | ReleasePrincipalSeparation -> report.Interpreters |> List.filter (fun value -> value.PrincipalClass = Release) |> List.forall (fun value -> value.AppPrincipal = "release-app" && value.Environment = "release")
        | EnvironmentSeparation -> expectedEnvironments.Count = 3 && report.Interpreters |> List.map (fun value -> value.PrincipalClass, value.Environment) |> List.distinct |> List.length = 3
        | StablePermissionOrdering | ExactPermissionSeal | ExactPermissionReplay | QuintPermissionUnchanged | NoPermissionMutationSurface as control -> generatedMutation control
    let result control passed = { Control = control; ControlPassed = passed; BaselineGreen = true }
    let generated = GitHubPermissionCompilationQualification.requiredControls |> List.map (fun control -> result control (generatedMutation control))
    let independent = GitHubPermissionCompilationQualification.requiredControls |> List.map (fun control -> result control (independentMutation control))
    match GitHubPermissionCompilationQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_PERMISSION_COMPILATION_OK interpreters=%d normal=%d admin=%d release=%d controls=%d seal=%s" report.InterpreterCount report.NormalCount report.AdminCutoverCount report.ReleaseCount expectedControls.Length report.Seal
    | Error findings -> failwithf "permission compilation qualification failed: %A" findings
