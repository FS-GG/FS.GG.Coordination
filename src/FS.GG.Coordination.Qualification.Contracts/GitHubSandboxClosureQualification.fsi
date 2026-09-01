namespace FS.GG.Coordination.Qualification.Contracts

type GitHubSandboxClosureControl =
    | ProductionIdentity
    | ProductionTarget
    | ProductionCredential
    | Quota
    | StaleFence
    | ResponseUnknown
    | PartialCleanup
    | ReceiptSubstitution
    | WarmReuse
    | OmittedAdapter

type GitHubSandboxClosureControlResult =
    { Control: GitHubSandboxClosureControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubSandboxClosureFinding =
    { Code: string
      ControlId: string
      Message: string }

[<RequireQualifiedAccess>]
module GitHubSandboxClosureQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-sandbox-closure-qualification/1"
    val requiredControls: GitHubSandboxClosureControl list
    val controlId: GitHubSandboxClosureControl -> string
    val validate: generated: GitHubSandboxClosureControlResult list -> independent: GitHubSandboxClosureControlResult list -> Result<unit, GitHubSandboxClosureFinding list>
