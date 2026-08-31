namespace FS.GG.Coordination.Qualification.Contracts

type GitHubTransportControl =
    | Truncation
    | UnsafeReplay
    | StaleRevision
    | RateExhaustion
    | IncompletePagination
    | RedactionLeakage
    | AmbiguousMapping

type GitHubTransportControlResult =
    { Control: GitHubTransportControl
      MutationRed: bool
      BaselineGreen: bool }

type GitHubTransportFinding = { Code: string; ControlId: string; Message: string }

[<RequireQualifiedAccess>]
module GitHubTransportQualification =
    [<Literal>]
    val Schema: string = "fsgg.coordination.github-transport-qualification/1"
    val requiredControls: GitHubTransportControl list
    val controlId: GitHubTransportControl -> string
    val validate: generated: GitHubTransportControlResult list -> independent: GitHubTransportControlResult list -> Result<unit, GitHubTransportFinding list>
