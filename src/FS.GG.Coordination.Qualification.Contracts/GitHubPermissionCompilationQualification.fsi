namespace FS.GG.Coordination.Qualification.Contracts

type GitHubPermissionLevel = PermissionRead | PermissionWrite
type GitHubPrincipalClass = NormalCoordination | AdminCutover | Release
type GitHubInterpreterOperation = InspectCoordination | CoordinateIssue | ApplyRepositoryCutover | PublishRelease
type GitHubPermission = { Name: string; Level: GitHubPermissionLevel }
type GitHubInterpreterRegistration =
    { Id: string
      Operation: GitHubInterpreterOperation
      PrincipalClass: GitHubPrincipalClass
      AppPrincipal: string
      Environment: string
      DeclaredAppPermissions: GitHubPermission list
      DeclaredWorkflowPermissions: GitHubPermission list }
type GitHubPermissionCompilationSnapshot =
    { SchemaVersion: int
      Repository: string
      SourceRevision: string
      RoadmapRevision: string
      RoadmapSha256: string
      PrerequisiteReceiptDigest: string
      PermissionCensusPath: string
      PermissionCensusSha256: string
      RequiredPermissionFamilies: string list
      Complete: bool
      Registrations: GitHubInterpreterRegistration list }
type GitHubCompiledInterpreterPermission =
    { Id: string
      Operation: GitHubInterpreterOperation
      PrincipalClass: GitHubPrincipalClass
      AppPrincipal: string
      Environment: string
      AppPermissions: GitHubPermission list
      WorkflowPermissions: GitHubPermission list }
type GitHubPermissionCompilationReport =
    { Repository: string
      SourceRevision: string
      InterpreterCount: int
      NormalCount: int
      AdminCutoverCount: int
      ReleaseCount: int
      Interpreters: GitHubCompiledInterpreterPermission list
      Seal: string }
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
    | CanonicalPermissionCensusMismatch
    | AlteredPermissionCompilationSeal

type GitHubPermissionCompilationControl =
    | PermissionPrerequisite | PermissionCompleteness | PermissionSourceBinding | PermissionProducerAgreement | InterpreterInventory
    | InterpreterUniqueness | LeastPrivilegeApp | LeastPrivilegeWorkflow | NoWildcardPermission
    | NoPermissionEscalation | NormalPrincipalSeparation | AdminPrincipalSeparation | ReleasePrincipalSeparation
    | EnvironmentSeparation | StablePermissionOrdering | ExactPermissionSeal | ExactPermissionReplay
    | QuintPermissionUnchanged | NoPermissionMutationSurface
type GitHubPermissionCompilationControlResult =
    { Control: GitHubPermissionCompilationControl; ControlPassed: bool; BaselineGreen: bool }
type GitHubPermissionCompilationQualificationFinding = { Code: string; ControlId: string; Message: string }

module GitHubPermissionCompilationQualification =
    val requiredControls: GitHubPermissionCompilationControl list
    val controlId: GitHubPermissionCompilationControl -> string
    val requiredOperations: GitHubInterpreterOperation list
    val compile: GitHubPermissionCompilationSnapshot -> Result<GitHubPermissionCompilationReport, GitHubPermissionCompilationFinding list>
    val verify: string -> GitHubPermissionCompilationSnapshot -> Result<GitHubPermissionCompilationReport, GitHubPermissionCompilationFinding list>
    val validate: GitHubPermissionCompilationControlResult list -> GitHubPermissionCompilationControlResult list -> Result<unit, GitHubPermissionCompilationQualificationFinding list>
