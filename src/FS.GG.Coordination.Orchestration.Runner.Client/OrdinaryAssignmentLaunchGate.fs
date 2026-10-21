namespace FS.GG.Coordination.Orchestration.Runner.Client

open System
open System.IO
open FS.GG.Coordination.Orchestration.Runner.Protocol

module OrdinaryAssignmentLaunchGate =
    let sameReference left right =
        let identity bytes = OrdinaryAssignmentWire.reference bytes |> Result.map(Option.map(fun r -> r.AssignmentSha256,r.ArtifactSha256))
        identity left=identity right && Result.isOk(identity left)
    let validate inputRoot (controlBytes: byte array) (command: ExecutorCommandV2) (manifest: ExecutorWorkspaceManifest) =
        OrdinaryAssignmentWire.reference controlBytes |> Result.bind(function
        | None -> Ok()
        | Some reference ->
          try
            let path=Path.Combine(inputRoot,reference.ArtifactSha256+".input")
            let info=FileInfo path
            if not info.Exists || not(isNull info.LinkTarget) || info.Length<=0L || info.Length>int64 OrdinaryAssignmentWire.maximumArtifactBytes then
                Error "ordinary-artifact-missing-or-size-refused"
            else
                let bytes=File.ReadAllBytes path
                if RunnerWire.sha256 bytes<>reference.ArtifactSha256 then Error "ordinary-artifact-object-digest-refused"
                else OrdinaryAssignmentWire.parseArtifact bytes |> Result.bind(fun a ->
                    let same (left: string array) (right: string array) = Set.ofArray left=Set.ofArray right && left.Length=right.Length
                    if a.AssignmentSha256<>reference.AssignmentSha256 || a.AssignmentId<>command.AssignmentId
                       || a.AttemptId<>command.AttemptId || a.Generation<>command.Generation
                       || a.WorkItemPersistenceId<>command.WorkItemPersistenceId || a.RecordedAt<>command.RecordedAt
                       || a.Deadline<>command.Deadline || a.MaximumRuntimeSeconds<>command.MaximumRuntimeSeconds
                       || a.MaximumAttempts<>command.MaximumAttempts || a.InputSha256<>command.InputDigest
                       || a.Workspace<>command.Workspace || a.RepositoryBinding<>manifest.RepositoryBinding
                       || a.BaselineObjectId<>manifest.BaselineObjectId || a.Model<>command.RequestedModel || a.Effort<>command.RequestedEffort
                       || not(same a.AllowedPaths manifest.AllowedPaths && same a.Validations manifest.Validations)
                       || a.Validations.Length=0 || (a.Validations |> Array.exists(fun c -> c<>"git-diff-check" && c<>"prose-citations"))
                       || (a.Tools |> Array.exists(fun t -> t<>"git"))
                       || (a.Effects |> Array.exists(fun e -> e<>"workspace-edit"))
                       || a.Outputs.Length=0 || a.Reviews.Length=0 then Error "ordinary-launch-obligations-refused"
                    else
                        let input=Path.Combine(inputRoot,command.InputDigest+".input")
                        if not(File.Exists input) then Error "ordinary-context-input-missing"
                        else
                            let text=File.ReadAllText input
                            if a.Context |> Array.exists(fun c -> not(text.Contains(c,StringComparison.Ordinal))) then Error "ordinary-mandatory-context-refused"
                            else Ok())
          with :? IOException -> Error "ordinary-artifact-read-refused")
