namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialWorkflowPermissionsRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      WorkflowPermissionsSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryWorkflowPermissions
      Second: MigrationRepositoryWorkflowPermissions
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackWorkflowPermissionsReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private rawSha256 (captured: MigrationRepositoryWorkflowPermissions) =
        [ "fsgg.gs2-09.7.workflow-permissions-raw/v1"
          string captured.RepositoryId
          captured.RepositoryFullName
          captured.PermissionsUri
          captured.IdentityPayloadSha256
          captured.PermissionsPayloadSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let capturePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 expectedWorkflowPermissionsSha256
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (exactSha expectedWorkflowPermissionsSha256) then
            Error "invalid:workflow-permissions-pin"
        else
            MigrationRollbackSettingsReadback.captureCorePartial expectedPlanSeal plan expectedNodeId
                expectedCorePayloadSha256 options transport
            |> Result.bind (fun core ->
                match MigrationGitHubRead.readRepositoryWorkflowPermissions options transport with
                | Error failure -> Error $"workflow-permissions-first:{failure}"
                | Ok first ->
                    match MigrationGitHubRead.readRepositoryWorkflowPermissions options transport with
                    | Error failure -> Error $"workflow-permissions-second:{failure}"
                    | Ok second when first.RepositoryId <> core.RepositoryId
                                     || second.RepositoryId <> core.RepositoryId
                                     || first.RepositoryFullName <> core.First.FullName
                                     || second.RepositoryFullName <> core.First.FullName ->
                        Error "invalid:workflow-permissions-identity"
                    | Ok second when first <> second -> Error "changed:workflow-permissions-raw"
                    | Ok second ->
                        let actual = rawSha256 first
                        if actual <> expectedWorkflowPermissionsSha256 then
                            Error "changed:workflow-permissions-state"
                        else
                            match MigrationGitHubRead.readRepositoryCoreSettings options transport with
                            | Error failure -> Error $"settings-core-final:{failure}"
                            | Ok finalCore when finalCore.RepositoryId <> core.RepositoryId
                                                || finalCore.NodeId <> core.RepositoryNodeId ->
                                Error "invalid:settings-cross-surface-identity"
                            | Ok finalCore when finalCore <> core.First ->
                                Error "changed:settings-cross-surface"
                            | Ok finalCore ->
                                Ok { PlanSeal=plan.Seal; Core=core; WorkflowPermissionsSha256=actual
                                     SettingsAuthorityComplete=false; First=first; Second=second
                                     FinalCore=finalCore })
