module FS.GG.Coordination.OrchestrationHostArchitectureTests

open System
open System.IO
open System.Xml.Linq
open Xunit

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))

let private read relative = File.ReadAllText(Path.Combine(root, relative))

[<Fact>]
let ``administration host is separate and serve path is migration free`` () =
    let project = XDocument.Load(Path.Combine(root, "src/FS.GG.Coordination.Orchestration.Host/FS.GG.Coordination.Orchestration.Host.fsproj"))
    Assert.Equal("Exe", project.Descendants(XName.Get "OutputType") |> Seq.exactlyOne |> _.Value)
    Assert.Equal("fsgg-coord-orchestration-host", project.Descendants(XName.Get "AssemblyName") |> Seq.exactlyOne |> _.Value)
    Assert.Equal("false", project.Descendants(XName.Get "IsPackable") |> Seq.exactlyOne |> _.Value)
    let references = project.Descendants(XName.Get "ProjectReference") |> Seq.map (_.Attribute(XName.Get "Include") >> _.Value.Replace('\\', '/')) |> Seq.toList
    Assert.Equal<string list>(
        [ "../FS.GG.Coordination.Core/FS.GG.Coordination.Core.fsproj"
          "../FS.GG.Coordination.Orchestration.Pilot/FS.GG.Coordination.Orchestration.Pilot.fsproj"
          "../FS.GG.Coordination.Orchestration.PostgreSql/FS.GG.Coordination.Orchestration.PostgreSql.fsproj"
          "../FS.GG.Coordination.Orchestration.Runner.Protocol/FS.GG.Coordination.Orchestration.Runner.Protocol.fsproj" ], references)
    let runtime = read "src/FS.GG.Coordination.Orchestration.Host/HostRuntime.fs"
    Assert.DoesNotContain(".migrate", runtime, StringComparison.Ordinal)
    Assert.DoesNotContain("FS.GG.Coordination.GitHub", runtime, StringComparison.Ordinal)
    let runner = read "src/FS.GG.Coordination.Orchestration.Host/RunnerWireRuntime.fs"
    Assert.Contains("runner-dispatch-intent-not-current", runner, StringComparison.Ordinal)
    Assert.Contains("runner-candidate-intent-not-current", runner, StringComparison.Ordinal)
    Assert.DoesNotContain("FS.GG.Coordination.GitHub", runner, StringComparison.Ordinal)
    Assert.DoesNotContain("CreatePullRequest", runner, StringComparison.Ordinal)
    Assert.DoesNotContain("MergePullRequest", runner, StringComparison.Ordinal)
    let initialization = read "src/FS.GG.Coordination.Orchestration.Host/HostInitialization.fs"
    Assert.Contains("PostgreSqlSchema.migrate", initialization, StringComparison.Ordinal)
    Assert.Contains("PostgreSqlPilotSchema.migrate", initialization, StringComparison.Ordinal)
    let program = read "src/FS.GG.Coordination.Orchestration.Host/Program.fs"
    Assert.Contains("PosixSignal.SIGTERM", program, StringComparison.Ordinal)
    let app = read "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj"
    Assert.DoesNotContain("Orchestration.Host", app, StringComparison.Ordinal)

[<Fact>]
let ``host contract records paused startup and runner authority limit`` () =
    let contract = read "docs/architecture/orchestration-administration-host.md"
    for required in [ "migration-free"; "default-paused"; "loopback"; "runner token"; "Telemetry is neither referenced" ] do
        Assert.Contains(required, contract, StringComparison.Ordinal)
