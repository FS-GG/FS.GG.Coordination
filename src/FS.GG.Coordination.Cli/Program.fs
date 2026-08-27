open FS.GG.Coordination.Cli

[<EntryPoint>]
let main arguments =
    match arguments |> Array.toList with
    | "roadmap-work" :: rest -> RoadmapCommand.run (List.toArray rest)
    | [] ->
        printfn "FS.GG.Coordination CLI boundary is installed; no production commands are enabled."
        0
    | _ ->
        eprintfn "unknown command; available local-only command: roadmap-work"
        2
