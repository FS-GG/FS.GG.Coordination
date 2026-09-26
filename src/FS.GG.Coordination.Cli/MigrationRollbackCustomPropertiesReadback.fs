namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialCustomPropertiesRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      CustomPropertiesSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationCustomProperties
      Second: MigrationCustomProperties
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackCustomPropertiesReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private rawSha256 (captured: MigrationCustomProperties) =
        [ "fsgg.gs2-09.7.custom-properties-raw/v1"
          captured.IdentityPayloadSha256
          captured.SchemaPayloadSha256
          captured.ValuesPayloadSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let capturePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 expectedCustomPropertiesSha256
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (exactSha expectedCustomPropertiesSha256) then
            Error "invalid:custom-properties-pin"
        else
            MigrationRollbackSettingsReadback.captureCorePartial expectedPlanSeal plan expectedNodeId
                expectedCorePayloadSha256 options transport
            |> Result.bind (fun core ->
                match MigrationGitHubRead.readCustomProperties options transport with
                | Error failure -> Error $"custom-properties-first:{failure}"
                | Ok first ->
                    match MigrationGitHubRead.readCustomProperties options transport with
                    | Error failure -> Error $"custom-properties-second:{failure}"
                    | Ok second when first.RepositoryId <> core.RepositoryId
                                     || second.RepositoryId <> core.RepositoryId
                                     || first.RepositoryFullName <> core.First.FullName
                                     || second.RepositoryFullName <> core.First.FullName ->
                        Error "invalid:custom-properties-identity"
                    | Ok second when first <> second -> Error "changed:custom-properties-raw"
                    | Ok second ->
                        let actual = rawSha256 first
                        if actual <> expectedCustomPropertiesSha256 then
                            Error "changed:custom-properties-state"
                        else
                            match MigrationGitHubRead.readRepositoryCoreSettings options transport with
                            | Error failure -> Error $"settings-core-final:{failure}"
                            | Ok finalCore when finalCore.RepositoryId <> core.RepositoryId
                                                || finalCore.NodeId <> core.RepositoryNodeId ->
                                Error "invalid:settings-cross-surface-identity"
                            | Ok finalCore when finalCore <> core.First ->
                                Error "changed:settings-cross-surface"
                            | Ok finalCore ->
                                Ok { PlanSeal=plan.Seal; Core=core; CustomPropertiesSha256=actual
                                     SettingsAuthorityComplete=false; First=first; Second=second
                                     FinalCore=finalCore })
