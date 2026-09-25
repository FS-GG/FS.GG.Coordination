namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialActionsPolicyRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      ActionsPolicySha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryActionsPolicy
      Second: MigrationRepositoryActionsPolicy
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackActionsReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private rawSha256 (captured: MigrationRepositoryActionsPolicy) =
        [ "fsgg.gs2-09.7.actions-policy-raw/v1"
          captured.IdentityPayloadSha256
          captured.PolicyPayloadSha256
          if captured.SelectedActionsUri.IsSome then "selected" else "unselected"
          captured.SelectedActionsUri |> Option.defaultValue ""
          captured.SelectedActionsPayloadSha256 |> Option.defaultValue "" ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let capturePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 expectedActionsPolicySha256
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (exactSha expectedActionsPolicySha256) then
            Error "invalid:actions-policy-pin"
        else
            MigrationRollbackSettingsReadback.captureCorePartial expectedPlanSeal plan expectedNodeId
                expectedCorePayloadSha256 options transport
            |> Result.bind (fun core ->
                match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
                | Error failure -> Error $"actions-policy-first:{failure}"
                | Ok first ->
                    match MigrationGitHubRead.readRepositoryActionsPolicy options transport with
                    | Error failure -> Error $"actions-policy-second:{failure}"
                    | Ok second when first.RepositoryId <> core.RepositoryId
                                     || second.RepositoryId <> core.RepositoryId
                                     || first.RepositoryFullName <> core.First.FullName
                                     || second.RepositoryFullName <> core.First.FullName ->
                        Error "invalid:actions-policy-identity"
                    | Ok second when first <> second -> Error "changed:actions-policy-raw"
                    | Ok second ->
                        let actual = rawSha256 first
                        if actual <> expectedActionsPolicySha256 then
                            Error "changed:actions-policy-state"
                        else
                            match MigrationGitHubRead.readRepositoryCoreSettings options transport with
                            | Error failure -> Error $"settings-core-final:{failure}"
                            | Ok finalCore when finalCore.RepositoryId <> core.RepositoryId
                                                || finalCore.NodeId <> core.RepositoryNodeId ->
                                Error "invalid:settings-cross-surface-identity"
                            | Ok finalCore when finalCore <> core.First ->
                                Error "changed:settings-cross-surface"
                            | Ok finalCore ->
                                Ok { PlanSeal=plan.Seal; Core=core; ActionsPolicySha256=actual
                                     SettingsAuthorityComplete=false; First=first; Second=second
                                     FinalCore=finalCore })
