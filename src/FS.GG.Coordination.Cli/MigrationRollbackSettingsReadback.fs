namespace FS.GG.Coordination.Cli

open System
open System.Text.RegularExpressions
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type PartialRepositorySettingsRollbackReadback =
    { PlanSeal: string
      RepositoryId: int64
      RepositoryNodeId: string
      CorePayloadSha256: string
      SettingsAuthorityComplete: bool
      First: MigrationRepositoryCoreSettings
      Second: MigrationRepositoryCoreSettings }

[<RequireQualifiedAccess>]
module MigrationRollbackSettingsReadback =
    let private exactSha (value: string) =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private exactAtom (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value = value.Trim()
        && (value |> Seq.forall (fun ch -> not (Char.IsControl ch)))

    let captureCorePartial expectedPlanSeal (plan: GitHubRollbackPlan) expectedNodeId
        expectedCorePayloadSha256 (options: MigrationGitHubReadOptions)
        (transport: IMigrationGitHubReadTransport) =
        match GitHubRollbackPlanQualification.verify expectedPlanSeal plan with
        | Error _ -> Error "invalid:rollback-plan"
        | Ok _ when not (exactAtom expectedNodeId && exactSha expectedCorePayloadSha256) ->
            Error "invalid:settings-core-pin"
        | Ok _ ->
            let target = $"repository-settings:{options.ExpectedRepositoryId}:{expectedNodeId}"
            match plan.Steps |> List.tryFind (fun step -> step.Domain = Settings) with
            | None -> Error "invalid:settings-rollback-target"
            | Some step when step.TargetIdentity <> target -> Error "invalid:settings-rollback-target"
            | Some _ ->
                match MigrationGitHubRead.readRepositoryCoreSettings options transport with
                | Error failure -> Error $"settings-core-first:{failure}"
                | Ok first when first.NodeId <> expectedNodeId -> Error "invalid:settings-core-identity"
                | Ok first ->
                    match MigrationGitHubRead.readRepositoryCoreSettings options transport with
                    | Error failure -> Error $"settings-core-second:{failure}"
                    | Ok second when second.NodeId <> expectedNodeId -> Error "invalid:settings-core-identity"
                    | Ok second when first.PayloadSha256 <> second.PayloadSha256 || first <> second ->
                        Error "changed:settings-core-raw"
                    | Ok second when first.PayloadSha256 <> expectedCorePayloadSha256 ->
                        Error "changed:settings-core-state"
                    | Ok second ->
                        Ok { PlanSeal=plan.Seal; RepositoryId=first.RepositoryId
                             RepositoryNodeId=first.NodeId
                             CorePayloadSha256=first.PayloadSha256
                             SettingsAuthorityComplete=false; First=first; Second=second }
