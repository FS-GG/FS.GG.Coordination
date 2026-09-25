namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialPrivateForkWorkflowRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      PrivateForkWorkflowSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationPrivateForkWorkflowSettings
      Second: MigrationPrivateForkWorkflowSettings
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackPrivateForkWorkflowReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private rawSha256 (captured: MigrationPrivateForkWorkflowSettings) =
        [ "fsgg.gs2-09.7.private-fork-workflow-raw/v1"
          string captured.RepositoryId
          captured.RepositoryFullName
          captured.PolicyUri
          captured.IdentityPayloadSha256
          captured.PolicyPayloadSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let capturePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 expectedPrivateForkWorkflowSha256
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (exactSha expectedPrivateForkWorkflowSha256) then
            Error "invalid:private-fork-workflow-pin"
        else
            MigrationRollbackSettingsReadback.captureCorePartial expectedPlanSeal plan expectedNodeId
                expectedCorePayloadSha256 options transport
            |> Result.bind (fun core ->
                if core.First.Visibility <> "private" then
                    Error "invalid:private-fork-workflow-visibility"
                else
                    match MigrationGitHubRead.readPrivateForkWorkflowSettings options transport with
                    | Error failure -> Error $"private-fork-workflow-first:{failure}"
                    | Ok first ->
                        match MigrationGitHubRead.readPrivateForkWorkflowSettings options transport with
                        | Error failure -> Error $"private-fork-workflow-second:{failure}"
                        | Ok second when first.RepositoryId <> core.RepositoryId
                                         || second.RepositoryId <> core.RepositoryId
                                         || first.RepositoryFullName <> core.First.FullName
                                         || second.RepositoryFullName <> core.First.FullName ->
                            Error "invalid:private-fork-workflow-identity"
                        | Ok second when first <> second -> Error "changed:private-fork-workflow-raw"
                        | Ok second ->
                            let actual = rawSha256 first
                            if actual <> expectedPrivateForkWorkflowSha256 then
                                Error "changed:private-fork-workflow-state"
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
                                         PrivateForkWorkflowSha256=actual
                                         SettingsAuthorityComplete=false; First=first; Second=second
                                         FinalCore=finalCore })
