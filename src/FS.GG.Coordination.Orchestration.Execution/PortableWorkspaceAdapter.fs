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
        WorkspaceScope: string
        ProfileId: string
        ProfileRevision: uint64
        SourceRevision: string
        QualifiedImage: string
        WorkflowRevision: uint64
        FenceGeneration: uint64
        ComponentId: string option
        OperationIdentity: string
        WorkingDirectory: string
        EntryPoint: string
        Deadline: DateTimeOffset
        MaximumRuntimeSeconds: uint64
        MaximumOutputBytes: uint64
    }

[<RequireQualifiedAccess>]
module PortableWorkspaceAdapter =
    let private isLowerHex (value: string) =
        value
        |> Seq.forall (fun character ->
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

    let private isPinnedSourceRevision (value: string) =
        (value.Length = 40 || value.Length = 64)
        && isLowerHex value
        && (value |> Seq.distinct |> Seq.length) > 1

    let private isPinnedImage (value: string) =
        let marker = "@sha256:"
        let index = value.LastIndexOf(marker, StringComparison.Ordinal)

        if index <= 0 || index + marker.Length + 64 <> value.Length then
            false
        else
            let digest = value[(index + marker.Length) ..]
            isLowerHex digest && (digest |> Seq.distinct |> Seq.length) > 1

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
            elif not (isPinnedSourceRevision profile.SourceRevision) then
                Error "portable-source-placeholder-refused"
            elif not (isPinnedImage profile.QualifiedImage) then
                Error "portable-image-placeholder-refused"
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
                            WorkspaceScope = command.WorkspaceScope
                            ProfileId = command.ProfileId
                            ProfileRevision = command.ProfileRevision
                            SourceRevision = command.SourceRevision
                            QualifiedImage = profile.QualifiedImage
                            WorkflowRevision = command.ExpectedWorkflowRevision
                            FenceGeneration = command.FenceGeneration
                            ComponentId = command.ComponentId
                            OperationIdentity = command.Operation
                            WorkingDirectory = directory
                            EntryPoint = entryPoint
                            Deadline = command.Deadline
                            MaximumRuntimeSeconds = profile.MaximumRuntimeSeconds
                            MaximumOutputBytes = profile.MaximumOutputBytes
                        }
                | None -> Error "portable-operation-refused")
