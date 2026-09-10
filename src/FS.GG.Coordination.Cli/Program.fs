open FS.GG.Coordination.Cli

[<EntryPoint>]
let main arguments =
    match arguments |> Array.toList with
    | "roadmap-work" :: rest -> RoadmapCommand.run (List.toArray rest)
    | "qualification-manifest" :: rest -> QualificationManifestCommand.run (List.toArray rest)
    | "workflow-select" :: rest -> WorkflowSelectionCommand.run (List.toArray rest)
    | "ledger-protection" :: rest -> LedgerProtectionCommand.run (List.toArray rest)
    | "observer-view" :: rest -> ObserverViewCommand.run (List.toArray rest)
    | [] ->
        printfn "FS.GG.Coordination CLI boundary is installed; no production commands are enabled."
        0
    | _ ->
        eprintfn "unknown command; available commands: workflow-select, roadmap-work, qualification-manifest, ledger-protection, observer-view"
        2
