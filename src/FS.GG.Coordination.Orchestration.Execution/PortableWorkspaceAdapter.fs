namespace FS.GG.Coordination.Orchestration.Execution

open System

type PortableWorkspaceAuthority =
    {
        WorkspaceScope: string
        WorkflowRevision: uint64
        FenceGeneration: uint64
        ObservedAt: DateTimeOffset
    }

type PreparedWorkspaceOperation =
    {
        CommandId: Guid
        IdempotencyId: string
        WorkingDirectory: string
        EntryPoint: string
        Deadline: DateTimeOffset
        MaximumRuntimeSeconds: uint64
        MaximumOutputBytes: uint64
    }

[<RequireQualifiedAccess>]
module PortableWorkspaceAdapter =
    let prepare
        now
        (authority: PortableWorkspaceAuthority)
        (profile: PortableWorkspaceProfile)
        (command: PortableWorkspaceCommand)
        =
        PortableWorkspaceContract.validateProfile profile
        |> Result.bind (fun _ -> PortableWorkspaceContract.validateCommand command)
        |> Result.bind (fun _ ->
            if
                command.WorkspaceScope <> authority.WorkspaceScope
                || command.WorkspaceScope <> profile.WorkspaceScope
            then
                Error "portable-workspace-scope-refused"
            elif
                command.ProfileId <> profile.ProfileId
                || command.ProfileRevision <> profile.Revision
            then
                Error "portable-profile-revision-refused"
            elif command.SourceRevision <> profile.SourceRevision then
                Error "portable-source-revision-refused"
            elif command.ExpectedWorkflowRevision <> authority.WorkflowRevision then
                Error "portable-workflow-revision-refused"
            elif command.FenceGeneration <> authority.FenceGeneration then
                Error "portable-fence-generation-refused"
            elif command.Deadline <= now || authority.ObservedAt > now then
                Error "portable-deadline-refused"
            else
                let selected =
                    match command.ComponentId with
                    | Some componentId ->
                        profile.Components
                        |> List.tryFind (fun part -> part.Id = componentId)
                        |> Option.bind (fun part ->
                            let entry =
                                match command.Operation with
                                | "build" -> Some part.EntryPoints.Build
                                | "test" -> Some part.EntryPoints.Test
                                | "lint" -> part.EntryPoints.Lint
                                | "artifact" -> part.EntryPoints.Artifact
                                | _ -> None

                            entry |> Option.map (fun value -> part.WorkingDirectory, value))
                    | None ->
                        match command.Operation with
                        | "build" -> Some("product", profile.ProductBuild)
                        | "test" -> Some("product", profile.ProductTest)
                        | "journey" -> Some("product", profile.ProductJourney)
                        | _ -> None

                match selected with
                | Some(directory, entryPoint) ->
                    Ok
                        {
                            CommandId = command.CommandId
                            IdempotencyId = command.IdempotencyId
                            WorkingDirectory = directory
                            EntryPoint = entryPoint
                            Deadline = command.Deadline
                            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                            MaximumOutputBytes = profile.MaximumOutputBytes
                        }
                | None -> Error "portable-operation-refused")
