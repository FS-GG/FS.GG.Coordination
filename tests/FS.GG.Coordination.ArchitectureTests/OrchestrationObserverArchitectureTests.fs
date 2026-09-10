module FS.GG.Coordination.OrchestrationObserverArchitectureTests

open System
open System.IO
open System.Xml.Linq
open Xunit
open FS.GG.Coordination.Orchestration.Observer

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read relative = File.ReadAllText(Path.Combine(root, relative))

[<Fact>]
let ``observer assembly is inert and has no writer runner or App edge`` () =
    let project = XDocument.Load(Path.Combine(root, "src/FS.GG.Coordination.Orchestration.Observer/FS.GG.Coordination.Orchestration.Observer.fsproj"))
    let references = project.Descendants(XName.Get "ProjectReference") |> Seq.map (fun value -> value.Attribute(XName.Get "Include").Value.Replace('\\', '/')) |> Seq.toList
    Assert.Equal<string list>([ "../FS.GG.Coordination.Core/FS.GG.Coordination.Core.fsproj"; "../FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj" ], references)
    Assert.Equal("false", project.Descendants(XName.Get "IsPackable") |> Seq.exactlyOne |> _.Value)
    let source = read "src/FS.GG.Coordination.Orchestration.Observer/Observer.fs"
    for forbidden in [ "DispatchRunner"; "EffectIntent"; "IJournalStore"; "MembershipPlan"; "StatusPlan"; "HttpClient"; "BackgroundService" ] do Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal)
    let app = read "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj"
    Assert.DoesNotContain("Orchestration.Observer", app, StringComparison.Ordinal)

[<Fact>]
let ``observer composition public dependencies are read plan readback and typed journal only`` () =
    let names =
        typeof<ObserverComposition>.Assembly.GetExportedTypes()
        |> Array.filter (fun value -> value.IsInterface)
        |> Array.map _.Name
        |> Set.ofArray
    for required in [ "IProjectReadCapability"; "IBoundedPlanningCapability"; "ICommandReadbackCapability"; "IObserverJournalStore" ] do Assert.Contains(required, names)
    for forbidden in [ "IRunnerDispatcher"; "IProviderMutation"; "IWriterCredential" ] do Assert.DoesNotContain(forbidden, names)

[<Fact>]
let ``CLI export view is explicit about unverified authority and closed stages`` () =
    let source = read "src/FS.GG.Coordination.Cli/ObserverViewCommand.fs"
    Assert.Contains("unverified-event-export", source)
    for stage in [ "conversation"; "proposed"; "approved"; "durable-acceptance"; "effect-complete" ] do Assert.Contains(stage, source)
    Assert.Contains("RecoverObserver", read "docs/architecture/orchestration-observer.md")
