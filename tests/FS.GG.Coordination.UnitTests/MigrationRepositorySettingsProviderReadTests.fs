module FS.GG.Coordination.MigrationRepositorySettingsProviderReadTests

open System
open Xunit
open FS.GG.Coordination.GitHub

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Token="token"; UserAgent="settings-test"
      Owner="FS-GG"; Repository="sandbox"; ExpectedRepositoryId=41L }

let private identity =
    { NodeId="R_settings"; DatabaseId=41L; Owner="FS-GG"; Name="sandbox"
      DefaultBranch="main"; SourceRepositoryNodeId=None }

let private revision = "2026-09-28T01:02:03Z"

let private repository canPush =
    let push = if canPush then "true" else "false"
    $"{{\"id\":41,\"node_id\":\"R_settings\",\"name\":\"sandbox\",\"full_name\":\"FS-GG/sandbox\",\"owner\":{{\"login\":\"FS-GG\",\"type\":\"Organization\"}},\"default_branch\":\"main\",\"updated_at\":\"{revision}\",\"fork\":false,\"private\":false,\"visibility\":\"public\",\"description\":\"Sandbox repository\",\"homepage\":null,\"topics\":[\"migration\",\"test\"],\"archived\":false,\"disabled\":false,\"has_issues\":true,\"has_projects\":true,\"has_wiki\":false,\"has_pages\":false,\"has_discussions\":true,\"has_downloads\":true,\"has_pull_requests\":true,\"pull_request_creation_policy\":\"all\",\"is_template\":false,\"web_commit_signoff_required\":false,\"allow_squash_merge\":true,\"allow_merge_commit\":true,\"allow_rebase_merge\":true,\"allow_auto_merge\":false,\"allow_update_branch\":true,\"delete_branch_on_merge\":true,\"squash_merge_commit_title\":\"PR_TITLE\",\"squash_merge_commit_message\":\"PR_BODY\",\"merge_commit_title\":\"PR_TITLE\",\"merge_commit_message\":\"PR_BODY\",\"use_squash_pr_title_as_default\":true,\"permissions\":{{\"push\":{push}}}}}"

let private organizationPolicy =
    "{\"login\":\"FS-GG\",\"updated_at\":\"2026-09-28T00:00:00Z\",\"has_repository_projects\":true,\"members_can_fork_private_repositories\":true,\"web_commit_signoff_required\":false}"

let private tagsFirst =
    "[{\"name\":\"v2\",\"node_id\":\"REF_v2\",\"commit\":{\"sha\":\"2222222222222222222222222222222222222222\"}}]"

let private tagsSecond =
    "[{\"name\":\"v1\",\"node_id\":\"REF_v1\",\"commit\":{\"sha\":\"1111111111111111111111111111111111111111\"}}]"

let private releaseOne =
    "{\"id\":9,\"node_id\":\"REL_9\",\"tag_name\":\"v2\",\"target_commitish\":\"main\",\"name\":\"Version 2\",\"draft\":true,\"prerelease\":false,\"immutable\":false}"

let private releaseTwo =
    "{\"id\":8,\"node_id\":\"REL_8\",\"tag_name\":\"v1\",\"target_commitish\":\"1111111111111111111111111111111111111111\",\"name\":null,\"draft\":false,\"prerelease\":false,\"immutable\":true}"

let private rate = { Limit=None; Remaining=None; ResetAt=None; Cost=None }
let private response status headers body =
    Response { StatusCode=status; Headers=headers; Body=body; ETag=None; RateBudget=rate }

let private repositoryPass () =
    [ response 200 Map.empty (repository true)
      response 200 Map.empty organizationPolicy ]

let private mergePass () = [ response 200 Map.empty (repository true) ]

let private organizationActions =
    "{\"enabled_repositories\":\"all\",\"allowed_actions\":\"all\",\"selected_actions_url\":null,\"sha_pinning_required\":true}"

let private actionsOrganizationIdentity =
    "{\"id\":7,\"node_id\":\"ORG_7\",\"login\":\"FS-GG\"}"

let private repositoryActions =
    "{\"enabled\":true,\"allowed_actions\":\"selected\",\"selected_actions_url\":\"https://api.github.test/repositories/41/actions/permissions/selected-actions\",\"sha_pinning_required\":true}"

let private selectedActions =
    "{\"github_owned_allowed\":true,\"verified_allowed\":false,\"patterns_allowed\":[\"FS-GG/*@*\"]}"

let private organizationWorkflow =
    "{\"default_workflow_permissions\":\"read\",\"can_approve_pull_request_reviews\":false}"

let private repositoryWorkflow =
    "{\"default_workflow_permissions\":\"write\",\"can_approve_pull_request_reviews\":true}"

let private organizationRetention = "{\"days\":90,\"maximum_allowed_days\":400}"
let private repositoryRetention = "{\"days\":30,\"maximum_allowed_days\":90}"
let private organizationForkApproval = "{\"approval_policy\":\"first_time_contributors\"}"
let private repositoryForkApproval = "{\"approval_policy\":\"all_external_contributors\"}"
let private noApplicableActionsPolicies = "{\"total_count\":0,\"policies\":[]}"

let private actionsTail () =
    [ response 200 Map.empty organizationWorkflow
      response 200 Map.empty repositoryWorkflow
      response 200 Map.empty organizationRetention
      response 200 Map.empty repositoryRetention
      response 200 Map.empty organizationForkApproval
      response 200 Map.empty repositoryForkApproval
      response 200 Map.empty noApplicableActionsPolicies ]

let private actionsPass () =
    [ response 200 Map.empty (repository true)
      response 200 Map.empty actionsOrganizationIdentity
      response 200 Map.empty organizationActions
      response 200 Map.empty repositoryActions
      response 200 Map.empty selectedActions ] @ actionsTail()

let private privateActionsAccess = "{\"access_level\":\"organization\"}"
let private organizationPrivateForkWorkflows =
    "{\"run_workflows_from_fork_pull_requests\":true,\"send_write_tokens_to_workflows\":false,\"send_secrets_and_variables\":false,\"require_approval_for_fork_pr_workflows\":true}"
let private repositoryPrivateForkWorkflows =
    "{\"run_workflows_from_fork_pull_requests\":true,\"send_write_tokens_to_workflows\":true,\"send_secrets_and_variables\":false,\"require_approval_for_fork_pr_workflows\":true}"

let private environmentReviewers =
    "[{\"type\":\"User\",\"reviewer\":{\"id\":7,\"node_id\":\"USER_7\",\"login\":\"alice\"}},{\"type\":\"Team\",\"reviewer\":{\"id\":8,\"node_id\":\"TEAM_8\",\"slug\":\"operators\"}}]"

let private environmentRules =
    $"[{{\"id\":11,\"node_id\":\"RULE_11\",\"type\":\"wait_timer\",\"wait_timer\":5}},{{\"id\":12,\"node_id\":\"RULE_12\",\"type\":\"required_reviewers\",\"prevent_self_review\":true,\"reviewers\":{environmentReviewers}}},{{\"id\":13,\"node_id\":\"RULE_13\",\"type\":\"branch_policy\"}}]"

let private environment =
    $"{{\"id\":100,\"node_id\":\"ENV_100\",\"name\":\"fleet-cutover\",\"url\":\"https://api.github.test/repos/FS-GG/sandbox/environments/fleet-cutover\",\"updated_at\":\"{revision}\",\"protection_rules\":{environmentRules},\"deployment_branch_policy\":{{\"protected_branches\":false,\"custom_branch_policies\":true}}}}"

let private environmentList = $"{{\"total_count\":1,\"environments\":[{environment}]}}"
let private environmentBranches =
    "{\"total_count\":1,\"branch_policies\":[{\"id\":21,\"node_id\":\"BRANCH_21\",\"name\":\"release/*\",\"type\":\"branch\"}]}"
let private environmentCustomRules = "{\"total_count\":0,\"custom_deployment_protection_rules\":[]}"
let private environmentSecrets = "{\"total_count\":0,\"secrets\":[]}"
let private environmentVariables =
    "{\"total_count\":1,\"variables\":[{\"name\":\"REGION\",\"value\":\"eu-central\",\"created_at\":\"2026-09-28T00:00:00Z\",\"updated_at\":\"2026-09-28T01:00:00Z\"}]}"

let private privateRepository =
    (repository true).Replace("\"private\":false", "\"private\":true")
                     .Replace("\"visibility\":\"public\"", "\"visibility\":\"private\"")

let private privateActionsPass () =
    [ response 200 Map.empty privateRepository
      response 200 Map.empty actionsOrganizationIdentity
      response 200 Map.empty organizationActions
      response 200 Map.empty repositoryActions
      response 200 Map.empty selectedActions
      response 200 Map.empty privateActionsAccess
      response 200 Map.empty organizationPrivateForkWorkflows
      response 200 Map.empty repositoryPrivateForkWorkflows ] @ actionsTail()

let private environmentPassFor repositoryBody =
    [ response 200 Map.empty repositoryBody
      response 200 Map.empty repositoryBody
      response 200 Map.empty environmentList
      response 200 Map.empty environment
      response 200 Map.empty environmentBranches
      response 200 Map.empty environmentCustomRules
      response 200 Map.empty environmentSecrets
      response 200 Map.empty environmentVariables
      response 200 Map.empty repositoryBody ]

let private environmentPass () = environmentPassFor (repository true)
let private privateEnvironmentPass () = environmentPassFor privateRepository

let private immutableRepository enabled enforced =
    $"{{\"enabled\":{enabled.ToString().ToLowerInvariant()},\"enforced_by_owner\":{enforced.ToString().ToLowerInvariant()}}}"

let private immutableOrganization mode = $"{{\"enforced_repositories\":\"{mode}\"}}"

let private immutableSelected repositories =
    let joined = String.concat "," repositories
    $"{{\"total_count\":{List.length repositories},\"repositories\":[{joined}]}}"

let private immutableSelectedRepository id nodeId fullName =
    $"{{\"id\":{id},\"node_id\":\"{nodeId}\",\"full_name\":\"{fullName}\"}}"

let private immutablePass enabled enforced mode selected =
    [ response 200 Map.empty privateRepository
      response 200 Map.empty (immutableRepository enabled enforced)
      response 200 Map.empty (immutableOrganization mode)
      if mode = "selected" then response 200 Map.empty (immutableSelected selected) ]

let private next suffix =
    Map.ofList
        [ "link",
          $"<https://api.github.test/repos/FS-GG/sandbox/{suffix}?per_page=100&page=2>; rel=\"next\"" ]

let private successPass () =
    [ response 200 Map.empty (repository true)
      response 200 (next "tags") tagsFirst
      response 200 Map.empty tagsSecond
      response 200 (next "releases") $"[{releaseOne}]"
      response 200 Map.empty $"[{releaseTwo}]" ]

let private securityConfiguration =
    "{\"status\":\"attached\",\"configuration\":{\"id\":1325,\"target_type\":\"organization\",\"name\":\"sandbox security\",\"advanced_security\":\"enabled\",\"code_security\":\"enabled\",\"dependency_graph\":\"enabled\",\"dependency_graph_autosubmit_action\":\"enabled\",\"dependency_graph_autosubmit_action_options\":{\"labeled_runners\":false},\"dependabot_alerts\":\"enabled\",\"dependabot_security_updates\":\"enabled\",\"dependabot_delegated_alert_dismissal\":\"disabled\",\"code_scanning_default_setup\":\"enabled\",\"code_scanning_default_setup_options\":{\"runner_type\":\"standard\",\"runner_label\":null},\"code_scanning_options\":{\"allow_advanced\":false},\"code_scanning_delegated_alert_dismissal\":\"disabled\",\"secret_protection\":\"enabled\",\"secret_scanning\":\"enabled\",\"secret_scanning_push_protection\":\"enabled\",\"secret_scanning_delegated_bypass\":\"disabled\",\"secret_scanning_delegated_bypass_options\":null,\"secret_scanning_validity_checks\":\"enabled\",\"secret_scanning_non_provider_patterns\":\"disabled\",\"secret_scanning_generic_secrets\":\"disabled\",\"secret_scanning_delegated_alert_dismissal\":\"disabled\",\"secret_scanning_extended_metadata\":\"disabled\",\"private_vulnerability_reporting\":\"enabled\",\"enforcement\":\"enforced\",\"url\":\"https://api.github.test/orgs/FS-GG/code-security/configurations/1325\",\"updated_at\":\"2026-09-28T01:00:00Z\"}}"

let private securityPass () =
    [ response 200 Map.empty (repository true)
      response 200 Map.empty securityConfiguration ]

let private dependencyPass () =
    [ response 200 Map.empty (repository true)
      response 200 Map.empty securityConfiguration
      response 204 Map.empty null
      response 200 Map.empty "{\"enabled\":true,\"paused\":false}" ]

type private FakeTransport(outcomes: TransportOutcome list) =
    let mutable remaining = outcomes
    let requests = ResizeArray<GitHubRequest>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationGitHubReadTransport with
        member _.Send request =
            requests.Add request
            match remaining with
            | head::tail -> remaining <- tail; head
            | [] -> failwith "unexpected provider request"

[<Fact>]
let ``repository reader binds explicit properties and organization provenance`` () =
    let transport = FakeTransport(repositoryPass())
    match MigrationRepositorySettingsProviderRead.readRepository options identity revision transport with
    | Error refusal -> failwithf "repository refused: %A" refusal
    | Ok captured ->
        Assert.Equal(identity, captured.SurfaceRead.RepositoryIdentity)
        Assert.Equal(revision, captured.SurfaceRead.RepositoryRevision)
        Assert.Equal("public", captured.Visibility)
        Assert.True(captured.HasProjects)
        Assert.True(captured.HasDiscussions)
        Assert.Equal(Some "Sandbox repository", captured.Description)
        Assert.Equal(None, captured.Homepage)
        Assert.Equal<string list>([ "migration"; "test" ], captured.Topics)
        Assert.Equal<string list>(
            [ "repository-identity"; "organization-repository-policy" ],
            captured.SurfaceRead.Pages |> List.map _.SettingsStream)
        Assert.Equal(18, captured.SurfaceRead.Settings.Length)
        Assert.All(captured.SurfaceRead.Pages, fun page -> Assert.Equal(64, page.SettingsPayloadSha256.Length))

[<Fact>]
let ``repository reader refuses inherited policy missing fields and revision drift`` () =
    let inheritedPolicy = organizationPolicy.Replace(
        "\"has_repository_projects\":true", "\"has_repository_projects\":false")
    let inherited =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty inheritedPolicy ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
            "inherited:organization-repository-projects-policy"),
        MigrationRepositorySettingsProviderRead.readRepository options identity revision inherited)

    let missing = (repository true).Replace(",\"has_discussions\":true", "")
    let missingField = FakeTransport([ response 200 Map.empty missing ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "missing:has_discussions"),
        MigrationRepositorySettingsProviderRead.readRepository options identity revision missingField)

    let drifted = (repository true).Replace(revision, "2026-09-28T01:02:04Z")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift"),
        MigrationRepositorySettingsProviderRead.readRepository
            options identity revision (FakeTransport([ response 200 Map.empty drifted ])))

    let privateRepository =
        (repository true)
            .Replace("\"private\":false", "\"private\":true")
            .Replace("\"visibility\":\"public\"", "\"visibility\":\"private\",\"allow_forking\":false")
    let privateForkingDenied = organizationPolicy.Replace(
        "\"members_can_fork_private_repositories\":true",
        "\"members_can_fork_private_repositories\":false")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
            "inherited:organization-private-forking-policy"),
        MigrationRepositorySettingsProviderRead.readRepository
            options identity revision
            (FakeTransport(
                [ response 200 Map.empty privateRepository
                  response 200 Map.empty privateForkingDenied ])))

    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readRepository
            options identity revision (FakeTransport([ response 403 Map.empty "{}" ])))
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "http:404"),
        MigrationRepositorySettingsProviderRead.readRepository
            options identity revision (FakeTransport([ response 404 Map.empty "{}" ])))

    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "repository-unexpected-continuation"),
        MigrationRepositorySettingsProviderRead.readRepository
            options identity revision
            (FakeTransport(
                [ response 200
                    (Map.ofList [ "link", "<https://api.github.test/repos/FS-GG/sandbox?page=2>; rel=\"next\"" ])
                    (repository true) ])))

[<Fact>]
let ``merge policy reader binds explicit modes and commit message choices`` () =
    let transport = FakeTransport(mergePass())
    match MigrationRepositorySettingsProviderRead.readMergePolicy options identity revision transport with
    | Error refusal -> failwithf "merge policy refused: %A" refusal
    | Ok captured ->
        Assert.True(captured.AllowSquashMerge)
        Assert.True(captured.AllowMergeCommit)
        Assert.True(captured.AllowRebaseMerge)
        Assert.False(captured.AllowAutoMerge)
        Assert.True(captured.AllowUpdateBranch)
        Assert.Equal("PR_TITLE", captured.SquashMergeCommitTitle)
        Assert.Equal("PR_BODY", captured.MergeCommitMessage)
        Assert.Equal(10, captured.SurfaceRead.Settings.Length)
        Assert.Single(captured.SurfaceRead.Pages) |> ignore

[<Fact>]
let ``merge policy reader refuses hidden options permission gaps and unknown choices`` () =
    let disabledSquash = (repository true).Replace("\"allow_squash_merge\":true", "\"allow_squash_merge\":false")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "disabled-squash-merge-latent-options-unavailable"),
        MigrationRepositorySettingsProviderRead.readMergePolicy
            options identity revision (FakeTransport([ response 200 Map.empty disabledSquash ])))

    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized
            "merge-settings-contents-write-unproven"),
        MigrationRepositorySettingsProviderRead.readMergePolicy
            options identity revision (FakeTransport([ response 200 Map.empty (repository false) ])))

    let unknown = (repository true).Replace(
        "\"merge_commit_message\":\"PR_BODY\"", "\"merge_commit_message\":\"FUTURE\"")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "unsupported:merge_commit_message:FUTURE"),
        MigrationRepositorySettingsProviderRead.readMergePolicy
            options identity revision (FakeTransport([ response 200 Map.empty unknown ])))

[<Fact>]
let ``actions policy reader binds explicit organization repository and allowlist state`` () =
    let transport = FakeTransport(actionsPass())
    match MigrationRepositorySettingsProviderRead.readActionsPolicy options identity revision transport with
    | Error refusal -> failwithf "actions policy refused: %A" refusal
    | Ok captured ->
        Assert.Equal(identity, captured.SurfaceRead.RepositoryIdentity)
        Assert.Equal(revision, captured.SurfaceRead.RepositoryRevision)
        Assert.Equal("all", captured.OrganizationEnabledRepositories)
        Assert.Equal("all", captured.OrganizationAllowedActions)
        Assert.True(captured.OrganizationShaPinningRequired)
        Assert.Equal("selected", captured.RepositoryAllowedActions)
        Assert.True(captured.ShaPinningRequired)
        Assert.Equal(Some [ "FS-GG/*@*" ], captured.RepositorySelectedActions |> Option.map _.PatternsAllowed)
        Assert.Equal("read", captured.OrganizationDefaultWorkflowPermissions)
        Assert.Equal("write", captured.RepositoryDefaultWorkflowPermissions)
        Assert.Equal(90L, captured.OrganizationArtifactAndLogRetentionDays)
        Assert.Equal(30L, captured.RepositoryArtifactAndLogRetentionDays)
        Assert.Equal(0L, captured.ApplicableActionsPolicyCount)
        Assert.Equal(7L, captured.OrganizationDatabaseId)
        Assert.Equal("ORG_7", captured.OrganizationNodeId)
        Assert.Equal(12, transport.Requests.Length)
        Assert.Equal(22, captured.SurfaceRead.Settings.Length)
        Assert.Equal(
            "https://api.github.test/repos/FS-GG/sandbox/actions/policies?per_page=100&has_parents=true",
            captured.SurfaceRead.Pages[11].SettingsRequestedUri)
        Assert.Equal<string list>(
            [ "repository-identity"; "actions-organization-identity"; "organization-actions-permissions"
              "repository-actions-permissions"; "repository-selected-actions"
              "organization-workflow-permissions"; "repository-workflow-permissions"
              "organization-artifact-and-log-retention"; "repository-artifact-and-log-retention"
              "organization-fork-pr-contributor-approval"; "repository-fork-pr-contributor-approval"
              "applicable-actions-policies" ],
            captured.SurfaceRead.Pages |> List.map _.SettingsStream)
        Assert.All(
            captured.SurfaceRead.Pages,
            fun page ->
                Assert.Equal(64, page.SettingsPayloadSha256.Length)
                Assert.Null(page.SettingsNextUri |> Option.toObj))

[<Fact>]
let ``actions policy reader binds private access fork policy and stable passes`` () =
    let transport = FakeTransport(privateActionsPass() @ privateActionsPass())
    let first = MigrationRepositorySettingsProviderRead.readActionsPolicy options identity revision transport
    match first with
    | Error refusal -> failwithf "private actions refused: %A" refusal
    | Ok captured ->
        Assert.Equal(Some "organization", captured.PrivateRepositoryAccessLevel)
        Assert.True(captured.OrganizationPrivateForkWorkflowPolicy.IsSome)
        Assert.True(captured.RepositoryPrivateForkWorkflowPolicy.IsSome)
        Assert.True(captured.RepositoryPrivateForkWorkflowPolicy.Value.SendWriteTokensToWorkflows)
        Assert.Equal(31, captured.SurfaceRead.Settings.Length)
        Assert.Equal<string list>(
            [ "repository-actions-access"; "organization-private-fork-workflows"
              "repository-private-fork-workflows" ],
            captured.SurfaceRead.Pages |> List.map _.SettingsStream |> List.skip 5 |> List.take 3)
        let second = MigrationRepositorySettingsProviderRead.readActionsPolicy options identity revision transport
        Assert.Equal(first, second)
        Assert.Equal(30, transport.Requests.Length)

[<Fact>]
let ``actions policy reader binds selected organization actions and repository population`` () =
    let selectedOrganization =
        organizationActions
            .Replace("\"enabled_repositories\":\"all\"", "\"enabled_repositories\":\"selected\"")
            .Replace("\"allowed_actions\":\"all\"", "\"allowed_actions\":\"selected\"")
            .Replace("\"selected_actions_url\":null",
                     "\"selected_actions_url\":\"https://api.github.test/organizations/7/actions/permissions/selected-actions\"")
    let selectedRepositories =
        "{\"total_count\":1,\"repositories\":[{\"id\":41,\"node_id\":\"R_settings\",\"full_name\":\"FS-GG/sandbox\"}]}"
    let responses =
        [ response 200 Map.empty (repository true)
          response 200 Map.empty actionsOrganizationIdentity
          response 200 Map.empty selectedOrganization
          response 200 Map.empty selectedActions
          response 200 Map.empty selectedRepositories
          response 200 Map.empty repositoryActions
          response 200 Map.empty selectedActions ] @ actionsTail()
    match MigrationRepositorySettingsProviderRead.readActionsPolicy
            options identity revision (FakeTransport(responses)) with
    | Error refusal -> failwithf "selected Actions policy refused: %A" refusal
    | Ok captured ->
        Assert.True(captured.OrganizationSelectedActions.IsSome)
        Assert.Single(captured.OrganizationSelectedRepositories) |> ignore
        Assert.Equal(27, captured.SurfaceRead.Settings.Length)

[<Fact>]
let ``actions policy reader refuses inaccessible partial conditional unknown and drifted evidence`` () =
    let forbidden = FakeTransport([ response 200 Map.empty (repository true); response 403 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy options identity revision forbidden)
    let missing = FakeTransport([ response 200 Map.empty (repository true); response 404 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "http:404"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy options identity revision missing)

    let missingPrivateAccess =
        privateActionsPass()
        |> List.mapi (fun index outcome -> if index = 5 then response 200 Map.empty "{}" else outcome)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "unsupported:repository-actions-access-shape"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy
            options identity revision (FakeTransport(missingPrivateAccess)))

    let deniedPrivateFork =
        privateActionsPass()
        |> List.mapi (fun index outcome -> if index = 6 then response 403 Map.empty "{}" else outcome)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy
            options identity revision (FakeTransport(deniedPrivateFork)))

    let unknown = organizationActions.Replace("\"allowed_actions\":\"all\"", "\"allowed_actions\":\"future\"")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "unsupported:allowed_actions:future"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy options identity revision
            (FakeTransport(
                [ response 200 Map.empty (repository true)
                  response 200 Map.empty actionsOrganizationIdentity
                  response 200 Map.empty unknown ])))

    let nonemptyPolicies =
        actionsPass()
        |> List.mapi (fun index outcome ->
            if index = 11 then response 200 Map.empty "{\"total_count\":1,\"policies\":[{}]}" else outcome)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "applicable-actions-policies-detail-unmodeled"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy
            options identity revision (FakeTransport(nonemptyPolicies)))

    let missingWorkflow =
        actionsPass()
        |> List.mapi (fun index outcome ->
            if index = 5 then response 200 Map.empty "{\"default_workflow_permissions\":\"read\"}" else outcome)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "unsupported:organization-workflow-permissions-shape"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy
            options identity revision (FakeTransport(missingWorkflow)))

    let drifted = (repository true).Replace(revision, "2026-09-28T01:02:04Z")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift"),
        MigrationRepositorySettingsProviderRead.readActionsPolicy
            options identity revision (FakeTransport([ response 200 Map.empty drifted ])))

[<Fact>]
let ``environment reader binds terminal roster rules reviewers branches and variables`` () =
    let transport = FakeTransport(environmentPass())
    match MigrationRepositorySettingsProviderRead.readEnvironments options identity revision transport with
    | Error refusal -> failwithf "environments refused: %A" refusal
    | Ok captured ->
        Assert.Equal(identity, captured.SurfaceRead.RepositoryIdentity)
        Assert.Equal(revision, captured.SurfaceRead.RepositoryRevision)
        let observed = Assert.Single(captured.Environments)
        Assert.Equal("fleet-cutover", observed.Name)
        Assert.Equal(3, observed.ProtectionRules.Length)
        Assert.Equal(Some 5, observed.ProtectionRules[0].WaitMinutes)
        Assert.Equal(2, observed.ProtectionRules[1].Reviewers.Length)
        Assert.Single(observed.BranchPolicies) |> ignore
        Assert.Empty(observed.CustomRules)
        Assert.Empty(observed.Secrets)
        Assert.Single(observed.Variables) |> ignore
        Assert.Equal(9, transport.Requests.Length)
        Assert.Equal(7, captured.SurfaceRead.Pages.Length)
        Assert.Equal(24, captured.SurfaceRead.Settings.Length)
        Assert.Contains(
            captured.SurfaceRead.Settings,
            fun setting ->
                setting.Subject = "environment:100:variable:REGION"
                && setting.Name = "value"
                && setting.Value = SettingValue.Text "eu-central")
        Assert.All(
            captured.SurfaceRead.Pages,
            fun page -> Assert.Equal(64, page.SettingsPayloadSha256.Length))

[<Fact>]
let ``environment reader accepts two stable exact private endpoint passes`` () =
    let transport = FakeTransport(privateEnvironmentPass() @ privateEnvironmentPass())
    let first = MigrationRepositorySettingsProviderRead.readEnvironments options identity revision transport
    match first with
    | Error refusal -> failwithf "private environments refused: %A" refusal
    | Ok captured ->
        Assert.Single(captured.Environments) |> ignore
        Assert.Equal(24, captured.SurfaceRead.Settings.Length)
        Assert.Contains(
            captured.SurfaceRead.Settings,
            fun setting ->
                setting.Subject = "environment:100:variable:REGION"
                && setting.Value = SettingValue.Text "eu-central")
        Assert.Equal(privateRepository, captured.SurfaceRead.Pages.Head.SettingsPayloadJson)
        let second = MigrationRepositorySettingsProviderRead.readEnvironments options identity revision transport
        Assert.Equal(first, second)
        Assert.Equal(18, transport.Requests.Length)

[<Fact>]
let ``environment reader refuses secrets plan ambiguity access gaps malformed rules and drift`` () =
    let secretInventory =
        "{\"total_count\":1,\"secrets\":[{\"name\":\"TOKEN\",\"created_at\":\"2026-09-28T00:00:00Z\",\"updated_at\":\"2026-09-28T01:00:00Z\"}]}"
    let withSecret =
        privateEnvironmentPass()
        |> List.mapi (fun index outcome ->
            if index = 6 then response 200 Map.empty secretInventory else outcome)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "environment-secret-values-provider-inaccessible"),
        MigrationRepositorySettingsProviderRead.readEnvironments
            options identity revision (FakeTransport(withSecret)))

    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readEnvironments options identity revision
            (FakeTransport([ response 200 Map.empty privateRepository; response 403 Map.empty "{}" ])))
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:401"),
        MigrationRepositorySettingsProviderRead.readEnvironments options identity revision
            (FakeTransport([ response 200 Map.empty privateRepository; response 401 Map.empty "{}" ])))
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
            "environment-plan-permission-or-resource-http:404"),
        MigrationRepositorySettingsProviderRead.readEnvironments options identity revision
            (FakeTransport([ response 200 Map.empty privateRepository; response 404 Map.empty "{}" ])))

    let malformedRules = environment.Replace(",\"wait_timer\":5", "")
    let malformedList = environmentList.Replace(environment, malformedRules)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "environment:missing:wait_timer"),
        MigrationRepositorySettingsProviderRead.readEnvironments options identity revision
            (FakeTransport(
                [ response 200 Map.empty privateRepository
                  response 200 Map.empty privateRepository
                  response 200 Map.empty malformedList ])))

    let escapedRoster =
        FakeTransport(
            [ response 200 Map.empty privateRepository
              response 200 Map.empty privateRepository
              response 200
                  (Map.ofList
                      [ "link", "<https://evil.test/repos/FS-GG/sandbox/environments?per_page=100&page=2>; rel=\"next\"" ])
                  environmentList ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "environment-pagination:escaped-next-uri"),
        MigrationRepositorySettingsProviderRead.readEnvironments
            options identity revision escapedRoster)

    let terminalRawDrift =
        privateEnvironmentPass()
        |> List.mapi (fun index outcome ->
            if index = 8 then
                response 200 Map.empty (privateRepository.Replace("}", ",\"extra\":true}"))
            else outcome)
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "environment-repository-raw-identity-drift"),
        MigrationRepositorySettingsProviderRead.readEnvironments
            options identity revision (FakeTransport(terminalRawDrift)))

    let drifted = (repository true).Replace(revision, "2026-09-28T01:02:04Z")
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift"),
        MigrationRepositorySettingsProviderRead.readEnvironments
            options identity revision (FakeTransport([ response 200 Map.empty drifted ])))

[<Fact>]
let ``releases and tags reader retains terminal raw streams and draft visibility proof`` () =
    let transport = FakeTransport(successPass())
    match MigrationRepositorySettingsProviderRead.readReleasesAndTags options identity revision transport with
    | Error refusal -> failwithf "releases and tags refused: %A" refusal
    | Ok captured ->
        Assert.Equal(5, transport.Requests.Length)
        Assert.Equal<string list>(
            [ "repository-identity"; "tags"; "tags"; "releases"; "releases" ],
            captured.SurfaceRead.Pages |> List.map _.SettingsStream)
        Assert.Equal<string list>([ "v2"; "v1" ], captured.Tags |> List.map _.Name)
        Assert.Equal<int64 list>([ 9L; 8L ], captured.Releases |> List.map _.DatabaseId)
        Assert.True(captured.Releases.Head.Draft)
        Assert.True(captured.Releases.Tail.Head.Immutable)
        Assert.True(captured.SurfaceRead.Complete)
        Assert.Equal(ReleasesAndTags, captured.SurfaceRead.Surface)
        Assert.Equal(17, captured.SurfaceRead.Settings.Length)
        Assert.All(
            captured.SurfaceRead.Pages,
            fun page -> Assert.Equal(64, page.SettingsPayloadSha256.Length))

type private HybridProvider(concrete: IMigrationRepositorySettingsSurfaceProvider) =
    interface IMigrationRepositorySettingsSurfaceProvider with
        member _.Read(actualIdentity, actualRevision, surface) =
            if surface = SettingsSurface.Repository || surface = MergePolicy || surface = ActionsPolicy
               || surface = Environments
               || surface = ReleasesAndTags || surface = CodeSecurity || surface = DependencyControls then
                concrete.Read(actualIdentity, actualRevision, surface)
            else
                let body = $"{{\"surface\":\"{RepositorySettingsAdapter.surfaceId surface}\"}}"
                let hash =
                    body |> Text.Encoding.UTF8.GetBytes
                    |> Security.Cryptography.SHA256.HashData
                    |> Convert.ToHexString |> _.ToLowerInvariant()
                Ok
                    { RepositoryIdentity=actualIdentity; RepositoryRevision=actualRevision
                      Surface=surface; Complete=true
                      Pages=
                        [ { SettingsStream=RepositorySettingsAdapter.surfaceId surface
                            SettingsRequestedUri=$"https://api.github.test/fabricated/{RepositorySettingsAdapter.surfaceId surface}"
                            SettingsPayloadJson=body; SettingsPayloadSha256=hash; SettingsNextUri=None } ]
                      Settings=
                        [ { Surface=surface; Subject="FS-GG/sandbox"; Name="present"
                            Value=SettingValue.Boolean true } ] }

[<Fact>]
let ``concrete provider pages join the eleven-surface two-pass composer`` () =
    let pass () =
        repositoryPass() @ mergePass() @ actionsPass() @ environmentPass()
        @ successPass() @ securityPass() @ dependencyPass()
    let transport = FakeTransport(pass() @ pass())
    let concrete =
        MigrationRepositorySettingsGitHubProvider(options, transport)
        :> IMigrationRepositorySettingsSurfaceProvider
    let provider = HybridProvider(concrete)
    match MigrationRepositorySettingsRead.captureTwoPass identity revision provider with
    | Error failure -> failwithf "two-pass provider capture refused: %A" failure
    | Ok captured ->
        Assert.Equal(70, transport.Requests.Length)
        Assert.Equal(
            Ok captured,
            MigrationRepositorySettingsRead.validateCapture captured)
        match MigrationRepositorySettingsRead.composeComplete captured with
        | Error failure -> failwithf "complete composition refused: %A" failure
        | Ok observation ->
            match observation.Surfaces[SettingsSurface.Repository] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(18, settings.Length)
            | state -> failwithf "unexpected repository surface: %A" state
            match observation.Surfaces[MergePolicy] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(10, settings.Length)
            | state -> failwithf "unexpected merge policy surface: %A" state
            match observation.Surfaces[ActionsPolicy] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(22, settings.Length)
            | state -> failwithf "unexpected actions policy surface: %A" state
            match observation.Surfaces[Environments] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(24, settings.Length)
            | state -> failwithf "unexpected environments surface: %A" state
            match observation.Surfaces[ReleasesAndTags] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(17, settings.Length)
            | state -> failwithf "unexpected releases surface: %A" state
            match observation.Surfaces[CodeSecurity] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(22, settings.Length)
            | state -> failwithf "unexpected code security surface: %A" state
            match observation.Surfaces[DependencyControls] with
            | Supported(actualRevision, true, settings) ->
                Assert.Equal(revision, actualRevision)
                Assert.Equal(12, settings.Length)
            | state -> failwithf "unexpected dependency controls surface: %A" state

[<Fact>]
let ``code security reader binds attached configuration provenance and explicit values`` () =
    let transport = FakeTransport(securityPass())
    match MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision transport with
    | Error refusal -> failwithf "code security refused: %A" refusal
    | Ok captured ->
        Assert.Equal(2, transport.Requests.Length)
        Assert.Equal(1325L, captured.ConfigurationId)
        Assert.Equal("organization", captured.ConfigurationTargetType)
        Assert.Equal("enforced", captured.Enforcement)
        Assert.Equal(identity, captured.SurfaceRead.RepositoryIdentity)
        Assert.Equal(revision, captured.SurfaceRead.RepositoryRevision)
        Assert.Equal<string list>(
            [ "repository-identity"; "code-security-configuration" ],
            captured.SurfaceRead.Pages |> List.map _.SettingsStream)
        Assert.Equal(securityConfiguration, captured.SurfaceRead.Pages[1].SettingsPayloadJson)
        Assert.Equal(22, captured.SurfaceRead.Settings.Length)
        Assert.All(captured.SurfaceRead.Settings, fun setting -> Assert.Equal(CodeSecurity, setting.Surface))
        Assert.All(
            captured.SurfaceRead.Pages,
            fun page -> Assert.Equal(64, page.SettingsPayloadSha256.Length))

[<Fact>]
let ``code security reader refuses absent inherited forbidden and unknown configuration`` () =
    let absent =
        FakeTransport([ response 200 Map.empty (repository true); response 204 Map.empty null ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
            "no-attached-configuration-effective-settings-unproven"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision absent)

    let forbidden =
        FakeTransport([ response 200 Map.empty (repository true); response 403 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision forbidden)

    let inherited = securityConfiguration.Replace(
        "\"secret_scanning\":\"enabled\"", "\"secret_scanning\":\"not_set\"")
    let partial =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty inherited ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial "inherited-or-unset:secret_scanning"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision partial)

    let enabledBypass = securityConfiguration.Replace(
        "\"secret_scanning_delegated_bypass\":\"disabled\"",
        "\"secret_scanning_delegated_bypass\":\"enabled\"")
    let missingBypassReviewers =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty enabledBypass ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "enabled-delegated-bypass-reviewers-unproven"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision missingBypassReviewers)

    let unknown = securityConfiguration.Replace("\"status\":\"attached\"", "\"status\":\"pending\"")
    let unreadable =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty unknown ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "unsupported:attachment-status:pending"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision unreadable)

    let unknownOptionShape = securityConfiguration.Replace(
        "\"code_scanning_options\":{\"allow_advanced\":false}",
        "\"code_scanning_options\":{\"allow_advanced\":false,\"future_option\":true}")
    let unsupportedShape =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty unknownOptionShape ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "unsupported:code_scanning_options-shape"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision unsupportedShape)

    let revisionDrift =
        FakeTransport(
            [ response 200 Map.empty ((repository true).Replace(revision, "2026-09-28T01:02:04Z")) ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift"),
        MigrationRepositorySettingsProviderRead.readCodeSecurity options identity revision revisionDrift)

[<Fact>]
let ``dependency controls reader reconciles explicit configuration with effective endpoints`` () =
    let transport = FakeTransport(dependencyPass())
    match MigrationRepositorySettingsProviderRead.readDependencyControls options identity revision transport with
    | Error refusal -> failwithf "dependency controls refused: %A" refusal
    | Ok captured ->
        Assert.Equal(identity, captured.SurfaceRead.RepositoryIdentity)
        Assert.Equal(revision, captured.SurfaceRead.RepositoryRevision)
        Assert.True(captured.DependencyGraph)
        Assert.True(captured.DependencyGraphAutosubmitAction)
        Assert.False(captured.DependencyGraphAutosubmitUsesLabeledRunners)
        Assert.True(captured.DependabotAlerts)
        Assert.True(captured.DependabotSecurityUpdates)
        Assert.False(captured.DependabotSecurityUpdatesPaused)
        Assert.False(captured.DependabotDelegatedAlertDismissal)
        Assert.Equal<string list>(
            [ "repository-identity"
              "dependency-security-configuration"
              "vulnerability-alerts"
              "automated-security-fixes" ],
            captured.SurfaceRead.Pages |> List.map _.SettingsStream)
        Assert.Equal("", captured.SurfaceRead.Pages[2].SettingsPayloadJson)
        Assert.Equal(12, captured.SurfaceRead.Settings.Length)
        Assert.All(
            captured.SurfaceRead.Pages,
            fun page ->
                Assert.Equal(64, page.SettingsPayloadSha256.Length)
                Assert.Null(page.SettingsNextUri |> Option.toObj))

[<Fact>]
let ``dependency controls reader refuses forbidden missing unknown and drifted evidence`` () =
    let forbidden = FakeTransport([ response 200 Map.empty (repository true); response 403 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readDependencyControls options identity revision forbidden)

    let absentConfiguration =
        FakeTransport([ response 200 Map.empty (repository true); response 404 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable "http:404"),
        MigrationRepositorySettingsProviderRead.readDependencyControls
            options identity revision absentConfiguration)

    let missing = securityConfiguration.Replace(
        ",\"dependency_graph_autosubmit_action_options\":{\"labeled_runners\":false}", "")
    let missingSurface =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty missing ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "missing:dependency_graph_autosubmit_action_options"),
        MigrationRepositorySettingsProviderRead.readDependencyControls options identity revision missingSurface)

    let unknown = securityConfiguration.Replace(
        "\"dependabot_alerts\":\"enabled\"", "\"dependabot_alerts\":\"scheduled\"")
    let unknownStatus =
        FakeTransport([ response 200 Map.empty (repository true); response 200 Map.empty unknown ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "unsupported:dependabot_alerts:scheduled"),
        MigrationRepositorySettingsProviderRead.readDependencyControls options identity revision unknownStatus)

    let disabledOrInaccessible =
        FakeTransport(
            [ response 200 Map.empty (repository true)
              response 200 Map.empty securityConfiguration
              response 404 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional
            "disabled-or-inaccessible:vulnerability-alerts"),
        MigrationRepositorySettingsProviderRead.readDependencyControls
            options identity revision disabledOrInaccessible)

    let revisionDrift =
        FakeTransport(
            [ response 200 Map.empty ((repository true).Replace(revision, "2026-09-28T01:02:04Z")) ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "repository-identity-drift"),
        MigrationRepositorySettingsProviderRead.readDependencyControls options identity revision revisionDrift)

[<Fact>]
let ``concrete provider leaves every unimplemented or partial surface unavailable`` () =
    let transport = FakeTransport(repositoryPass())
    let provider = MigrationRepositorySettingsGitHubProvider(options, transport)
    let source = provider :> IMigrationRepositorySettingsSurfaceProvider
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unsupported
            "surface-reader-not-installed:custom-properties"),
        source.Read(identity, revision, CustomProperties))
    Assert.Equal(
        Error(MigrationRepositorySettingsReadFailure.ProviderRefused(
            CustomProperties,
            MigrationRepositorySettingsSurfaceRefusal.Unsupported
                "surface-reader-not-installed:custom-properties")),
        MigrationRepositorySettingsRead.captureTwoPass identity revision source)
    Assert.Equal(2, transport.Requests.Length)

[<Fact>]
let ``release reader refuses missing push proof and forbidden access`` () =
    let conditional = FakeTransport([ response 200 Map.empty (repository false) ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Conditional "draft-release-visibility-unproven"),
        MigrationRepositorySettingsProviderRead.readReleasesAndTags options identity revision conditional)

    let forbidden = FakeTransport([ response 403 Map.empty "{}" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized "http:403"),
        MigrationRepositorySettingsProviderRead.readReleasesAndTags options identity revision forbidden)

[<Fact>]
let ``release reader refuses escaped pagination and unknown release shapes`` () =
    let escaped =
        FakeTransport(
            [ response 200 Map.empty (repository true)
              response 200
                  (Map.ofList [ "link", "<https://evil.test/repos/FS-GG/sandbox/tags?page=2>; rel=\"next\"" ])
                  tagsFirst ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial "pagination-escaped-scope"),
        MigrationRepositorySettingsProviderRead.readReleasesAndTags options identity revision escaped)

    let missingImmutable = releaseOne.Replace(",\"immutable\":false", "")
    let malformed =
        FakeTransport(
            [ response 200 Map.empty (repository true)
              response 200 Map.empty "[]"
              response 200 Map.empty $"[{missingImmutable}]" ])
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "missing:immutable"),
        MigrationRepositorySettingsProviderRead.readReleasesAndTags options identity revision malformed)

[<Fact>]
let ``immutable releases reader binds private none all and selected policies`` () =
    let cases =
        [ true, false, "none", [], ImmutableReleasesOrganizationPolicy.NoRepositories, true, 4
          true, true, "all", [], ImmutableReleasesOrganizationPolicy.AllRepositories, true, 4
          true, true, "selected",
              [ immutableSelectedRepository 41L "R_settings" "FS-GG/sandbox"
                immutableSelectedRepository 42L "R_other" "FS-GG/other" ],
              ImmutableReleasesOrganizationPolicy.SelectedRepositories, true, 11 ]
    for enabled, enforced, mode, selected, expectedPolicy, effective, expectedSettings in cases do
        let pass = immutablePass enabled enforced mode selected
        let transport = FakeTransport(pass @ pass)
        match MigrationRepositorySettingsProviderRead.readImmutableReleases
                  options identity revision transport with
        | Error refusal -> failwithf "immutable releases refused for %s: %A" mode refusal
        | Ok captured ->
            Assert.Equal(expectedPolicy, captured.OrganizationPolicy)
            Assert.Equal(enabled, captured.RepositoryEnabled)
            Assert.Equal(enforced, captured.EnforcedByOwner)
            Assert.Equal(effective, captured.EffectiveEnabled)
            Assert.Equal(expectedSettings, captured.SurfaceRead.Settings.Length)
            Assert.Equal(pass.Length * 2, captured.SurfaceRead.Pages.Length)
            Assert.Equal(64, captured.CaptureFingerprint.Length)
            Assert.All(captured.SurfaceRead.Pages, fun page ->
                Assert.Equal(64, page.SettingsPayloadSha256.Length))
            Assert.All(transport.Requests, fun request ->
                match request with
                | Rest value -> Assert.Equal(Get, value.Method)
                | _ -> failwith "immutable reader emitted non-REST request")

[<Fact>]
let ``immutable releases reader refuses missing and mismatched selected membership`` () =
    let missingPass =
        immutablePass true true "selected"
            [ immutableSelectedRepository 42L "R_other" "FS-GG/other" ]
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "inheritance-contradiction:unselected"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport missingPass))

    let nonterminalPass =
        [ response 200 Map.empty privateRepository
          response 200 Map.empty (immutableRepository true true)
          response 200 Map.empty (immutableOrganization "selected")
          response 200
              (Map.ofList
                  [ "link",
                    "<https://evil.test/orgs/FS-GG/settings/immutable-releases/repositories?per_page=100&page=2>; rel=\"next\"" ])
              (immutableSelected
                  [ immutableSelectedRepository 41L "R_settings" "FS-GG/sandbox" ]) ]
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "selected-pagination-continuation"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport nonterminalPass))

    let mismatchedPass =
        immutablePass true true "selected"
            [ immutableSelectedRepository 41L "R_wrong" "FS-GG/sandbox" ]
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "selected-membership-identity-drift"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport(mismatchedPass @ mismatchedPass)))

[<Fact>]
let ``immutable releases reader refuses access ambiguous absence unknown mode and drift`` () =
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized
            "immutable-releases-read-unknown:http-401"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport([ response 401 Map.empty "{}" ])))
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unauthorized
            "immutable-releases-read-unknown:http-403"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport([ response 403 Map.empty "{}" ])))
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unavailable
            "immutable-releases-read-unknown:http-404"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport([ response 404 Map.empty "{}" ])))

    let unknownMode =
        [ response 200 Map.empty privateRepository
          response 200 Map.empty (immutableRepository false false)
          response 200 Map.empty (immutableOrganization "future") ]
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable "organization-policy-shape"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport unknownMode))

    let first = immutablePass false false "none" []
    let second = immutablePass true false "none" []
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "immutable-releases-pass-drift"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport(first @ second)))

    let revisionDrift =
        privateRepository.Replace(revision, "2026-09-28T01:02:04Z")
    let driftedPass =
        [ response 200 Map.empty revisionDrift
          response 200 Map.empty (immutableRepository false false)
          response 200 Map.empty (immutableOrganization "none") ]
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unreadable
            "immutable-releases-repository-identity-drift"),
        MigrationRepositorySettingsProviderRead.readImmutableReleases
            options identity revision (FakeTransport(driftedPass @ driftedPass)))
