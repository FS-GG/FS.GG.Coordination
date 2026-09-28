namespace FS.GG.Coordination.Cli

open System

[<RequireQualifiedAccess>]
module MigrationSandboxSeedBootstrapRuntimeCommand =
    let private parse arguments =
        let rec loop values =
            function
            | [] -> Some values
            | (name: string) :: value :: rest when name.StartsWith("--", StringComparison.Ordinal) ->
                if Map.containsKey name values then
                    None
                else
                    loop (Map.add name value values) rest
            | _ -> None

        loop Map.empty arguments

    let run arguments =
        try
            match Array.toList arguments with
            | "establish-and-write" :: rest ->
                let values = parse rest |> Option.defaultWith (fun () -> invalidOp "arguments")

                let required name =
                    Map.tryFind name values |> Option.defaultWith (fun () -> invalidOp name)

                if values.Count <> 17 then
                    invalidOp "argument-count"

                let input =
                    {
                        BootstrapDirectory = required "--bootstrap-dir"
                        SourceManifestPath = required "--source-manifest"
                        MintProofPath = required "--mint-proof"
                        FinalAdmissionPath = required "--final-admission"
                        InitialPrestatePath = required "--initial-prestate"
                        InitialPrestateEvidencePath = required "--initial-prestate-evidence"
                        FinalPrestatePath = required "--final-prestate"
                        FinalPrestateEvidencePath = required "--final-prestate-evidence"
                        RunResponsePath = required "--run-response"
                        WorkflowBlobPath = required "--workflow-blob"
                        BindingBuilderBlobPath = required "--binding-builder-blob"
                        RepositoryResponsePath = required "--repository-response"
                        ProjectResponsePath = required "--project-response"
                        MintResponsePath = required "--mint-response"
                        ViewerResponsePath = required "--viewer-response"
                        TokenPath = required "--token-file"
                        OutputPath = required "--output"
                    }

                match MigrationSandboxSeedBootstrapRuntime.execute input with
                | Ok() -> 0
                | Error reason ->
                    eprintfn "seed-bootstrap-runtime refused: %s" reason
                    3
            | _ -> 2
        with _ ->
            eprintfn "seed-bootstrap-runtime refused"
            3
