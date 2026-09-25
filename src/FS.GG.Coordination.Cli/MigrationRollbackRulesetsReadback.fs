namespace FS.GG.Coordination.Cli

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialRepositoryRulesetsRollbackReadback =
    { PlanSeal: string
      Core: PartialRepositorySettingsRollbackReadback
      RepositoryRulesetsSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryRulesets
      Second: MigrationRepositoryRulesets
      FinalCore: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackRulesetsReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"

    let private rawSha256 (captured: MigrationRepositoryRulesets) =
        [ yield "fsgg.gs2-09.7.repository-rulesets-raw/v1"
          yield string captured.RepositoryId
          yield string captured.PageCount
          for page in captured.ListPages do
              yield page.ListRequestedUri
              yield page.ListPayloadSha256
              yield page.ListNextUri |> Option.defaultValue ""
          for rule in captured.Rulesets do
              yield string rule.RulesetId
              yield rule.DetailUri
              yield rule.PayloadSha256 ]
        |> List.map frame |> String.concat "" |> Encoding.UTF8.GetBytes
        |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let capturePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 expectedRepositoryRulesetsSha256
        (options: MigrationGitHubReadOptions) (transport: IMigrationGitHubReadTransport) =
        if not (exactSha expectedRepositoryRulesetsSha256) then
            Error "invalid:repository-rulesets-pin"
        else
            MigrationRollbackSettingsReadback.captureCorePartial expectedPlanSeal plan expectedNodeId
                expectedCorePayloadSha256 options transport
            |> Result.bind (fun core ->
                match MigrationGitHubRead.readRepositoryBranchTagRulesets options transport with
                | Error failure -> Error $"repository-rulesets-first:{failure}"
                | Ok first ->
                    match MigrationGitHubRead.readRepositoryBranchTagRulesets options transport with
                    | Error failure -> Error $"repository-rulesets-second:{failure}"
                    | Ok second when first.RepositoryId <> core.RepositoryId
                                     || second.RepositoryId <> core.RepositoryId
                                     || not first.Terminal || not second.Terminal ->
                        Error "invalid:repository-rulesets-identity"
                    | Ok second when first <> second -> Error "changed:repository-rulesets-raw"
                    | Ok second ->
                        let actual = rawSha256 first
                        if actual <> expectedRepositoryRulesetsSha256 then
                            Error "changed:repository-rulesets-state"
                        else
                            match MigrationGitHubRead.readRepositoryCoreSettings options transport with
                            | Error failure -> Error $"settings-core-final:{failure}"
                            | Ok finalCore when finalCore.RepositoryId <> core.RepositoryId
                                                || finalCore.NodeId <> core.RepositoryNodeId ->
                                Error "invalid:settings-cross-surface-identity"
                            | Ok finalCore when finalCore <> core.First ->
                                Error "changed:settings-cross-surface"
                            | Ok finalCore ->
                                Ok { PlanSeal=plan.Seal; Core=core; RepositoryRulesetsSha256=actual
                                     SettingsAuthorityComplete=false; First=first; Second=second
                                     FinalCore=finalCore })
