namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialActionsWorkflowRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      ActionsPolicySha256: string
      WorkflowPermissionsSha256: string
      SettingsAuthorityComplete: bool
      FirstActions: MigrationRepositoryActionsPolicy
      FirstWorkflow: MigrationRepositoryWorkflowPermissions
      SecondWorkflow: MigrationRepositoryWorkflowPermissions
      SecondActions: MigrationRepositoryActionsPolicy
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackActionsWorkflowReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private hash (values: string list) =
        values |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private actionsHash (captured: MigrationRepositoryActionsPolicy) =
        [ "fsgg.gs2-09.7.actions-policy-raw/v1"
          captured.IdentityPayloadSha256
          captured.PolicyPayloadSha256
          if captured.SelectedActionsUri.IsSome then "selected" else "unselected"
          captured.SelectedActionsUri |> Option.defaultValue ""
          captured.SelectedActionsPayloadSha256 |> Option.defaultValue "" ] |> hash

    let private workflowHash (captured: MigrationRepositoryWorkflowPermissions) =
        [ "fsgg.gs2-09.7.workflow-permissions-raw/v1"
          string captured.RepositoryId
          captured.RepositoryFullName
          captured.PermissionsUri
          captured.IdentityPayloadSha256
          captured.PermissionsPayloadSha256 ] |> hash

    let capturePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 expectedActionsPolicySha256 expectedWorkflowPermissionsSha256
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (exactSha expectedActionsPolicySha256) then Error "invalid:actions-policy-pin"
        elif not (exactSha expectedWorkflowPermissionsSha256) then Error "invalid:workflow-permissions-pin"
        else
            MigrationRollbackSettingsReadback.captureCorePartial expectedPlanSeal plan expectedNodeId
                expectedCorePayloadSha256 options transport
            |> Result.bind (fun core ->
                let identity =
                    fun repositoryId fullName ->
                        repositoryId = core.RepositoryId && fullName = core.First.FullName
                match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
                | Error failure -> Error $"actions-policy-first:{failure}"
                | Ok firstActions when not (identity firstActions.RepositoryId firstActions.RepositoryFullName) ->
                    Error "invalid:actions-policy-identity"
                | Ok firstActions ->
                    match MigrationGitHubRead.readRepositoryWorkflowPermissions options transport with
                    | Error failure -> Error $"workflow-permissions-first:{failure}"
                    | Ok firstWorkflow when not (identity firstWorkflow.RepositoryId firstWorkflow.RepositoryFullName) ->
                        Error "invalid:workflow-permissions-identity"
                    | Ok firstWorkflow ->
                        match MigrationGitHubRead.readRepositoryWorkflowPermissions options transport with
                        | Error failure -> Error $"workflow-permissions-second:{failure}"
                        | Ok secondWorkflow when not (identity secondWorkflow.RepositoryId secondWorkflow.RepositoryFullName) ->
                            Error "invalid:workflow-permissions-identity"
                        | Ok secondWorkflow when secondWorkflow <> firstWorkflow ->
                            Error "changed:workflow-permissions-raw"
                        | Ok secondWorkflow ->
                            match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
                            | Error failure -> Error $"actions-policy-second:{failure}"
                            | Ok secondActions when not (identity secondActions.RepositoryId secondActions.RepositoryFullName) ->
                                Error "invalid:actions-policy-identity"
                            | Ok secondActions when secondActions <> firstActions ->
                                Error "changed:actions-policy-cross-surface"
                            | Ok secondActions ->
                                let actualActions = actionsHash firstActions
                                let actualWorkflow = workflowHash firstWorkflow
                                if actualActions <> expectedActionsPolicySha256 then
                                    Error "changed:actions-policy-state"
                                elif actualWorkflow <> expectedWorkflowPermissionsSha256 then
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
                                        Ok { PlanSeal=plan.Seal; Core=core
                                             ActionsPolicySha256=actualActions
                                             WorkflowPermissionsSha256=actualWorkflow
                                             SettingsAuthorityComplete=false
                                             FirstActions=firstActions; FirstWorkflow=firstWorkflow
                                             SecondWorkflow=secondWorkflow; SecondActions=secondActions
                                             FinalCore=finalCore })
