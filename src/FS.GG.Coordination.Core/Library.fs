namespace FS.GG.Coordination.Core

type DependencyBoundary =
    { AssemblyName: string
      AllowedDependencies: Set<string> }

[<RequireQualifiedAccess>]
module SolutionBoundary =
    let expected =
        [ { AssemblyName = "FS.GG.Coordination.Protocol"
            AllowedDependencies = Set.empty }
          { AssemblyName = "FS.GG.Coordination.Core"
            AllowedDependencies = Set.singleton "FS.GG.Coordination.Protocol" }
          { AssemblyName = "FS.GG.Coordination.GitHub"
            AllowedDependencies =
              Set.ofList
                  [ "FS.GG.Coordination.Protocol"
                    "FS.GG.Coordination.Core" ] }
          { AssemblyName = "FS.GG.Coordination.Cli"
            AllowedDependencies =
              Set.ofList
                  [ "FS.GG.Coordination.Protocol"
                    "FS.GG.Coordination.Core"
                    "FS.GG.Coordination.GitHub" ] }
          { AssemblyName = "FS.GG.Coordination.App"
            AllowedDependencies =
              Set.ofList
                  [ "FS.GG.Coordination.Protocol"
                    "FS.GG.Coordination.Core"
                    "FS.GG.Coordination.GitHub" ] }
          { AssemblyName = "FS.GG.Coordination.Qualification.Contracts"
            AllowedDependencies = Set.singleton "FS.GG.Coordination.Protocol" } ]

    let isAllowed dependent dependency =
        expected
        |> List.tryFind (fun boundary -> boundary.AssemblyName = dependent)
        |> Option.exists (fun boundary -> Set.contains dependency boundary.AllowedDependencies)
