open FS.GG.Coordination.Cli
open System
open System.Threading

let private runPortableWorkspace arguments =
    use cancellation = new CancellationTokenSource()
    let handler = ConsoleCancelEventHandler(fun _ event -> event.Cancel <- true; cancellation.Cancel())
    Console.CancelKeyPress.AddHandler handler
    try
        PortableWorkspaceRuntimeCommand.runWithCancellation
            (PortableWorkspaceTrustedEnrollment.productionDependencies ())
            arguments
            cancellation.Token
    finally
        Console.CancelKeyPress.RemoveHandler handler

[<EntryPoint>]
let main arguments =
    match arguments |> Array.toList with
    | "roadmap-work" :: rest -> RoadmapCommand.run (List.toArray rest)
    | "qualification-manifest" :: rest -> QualificationManifestCommand.run (List.toArray rest)
    | "workflow-select" :: rest -> WorkflowSelectionCommand.run (List.toArray rest)
    | "ledger-protection" :: rest -> LedgerProtectionCommand.run (List.toArray rest)
    | "observer-view" :: rest -> ObserverViewCommand.run (List.toArray rest)
    | "delivery" :: rest -> DeliveryCommand.run (List.toArray rest)
    | "ordinary-settlement" :: rest -> OrdinarySettlementCommand.run (List.toArray rest)
    | "workspace-contract" :: rest -> PortableWorkspaceCommand.run (List.toArray rest)
    | "portable-workspace" :: rest -> runPortableWorkspace (List.toArray rest)
    | [] ->
        printfn "FS.GG.Coordination CLI boundary is installed; no production commands are enabled."
        0
    | _ ->
        eprintfn
            "unknown command; available commands: workflow-select, roadmap-work, qualification-manifest, ledger-protection, observer-view, delivery, ordinary-settlement, workspace-contract, portable-workspace"

        2
