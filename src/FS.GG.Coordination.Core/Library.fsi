namespace FS.GG.Coordination.Core

type DependencyBoundary =
    { AssemblyName: string
      AllowedDependencies: Set<string> }

[<RequireQualifiedAccess>]
module SolutionBoundary =
    val expected: DependencyBoundary list
    val isAllowed: dependent: string -> dependency: string -> bool
