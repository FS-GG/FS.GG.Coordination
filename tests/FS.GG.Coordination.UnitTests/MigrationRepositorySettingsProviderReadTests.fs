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
    $"{{\"id\":41,\"node_id\":\"R_settings\",\"full_name\":\"FS-GG/sandbox\",\"default_branch\":\"main\",\"updated_at\":\"{revision}\",\"fork\":false,\"permissions\":{{\"push\":{push}}}}}"

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
            if surface = ReleasesAndTags || surface = CodeSecurity || surface = DependencyControls then
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
    let pass () = successPass() @ securityPass() @ dependencyPass()
    let transport = FakeTransport(pass() @ pass())
    let concrete =
        MigrationRepositorySettingsGitHubProvider(options, transport)
        :> IMigrationRepositorySettingsSurfaceProvider
    let provider = HybridProvider(concrete)
    match MigrationRepositorySettingsRead.captureTwoPass identity revision provider with
    | Error failure -> failwithf "two-pass provider capture refused: %A" failure
    | Ok captured ->
        Assert.Equal(22, transport.Requests.Length)
        Assert.Equal(
            Ok captured,
            MigrationRepositorySettingsRead.validateCapture captured)
        match MigrationRepositorySettingsRead.composeComplete captured with
        | Error failure -> failwithf "complete composition refused: %A" failure
        | Ok observation ->
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
    let transport = FakeTransport([])
    let provider = MigrationRepositorySettingsGitHubProvider(options, transport)
    let source = provider :> IMigrationRepositorySettingsSurfaceProvider
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Unsupported "surface-reader-not-installed:repository"),
        source.Read(identity, revision, SettingsSurface.Repository))
    Assert.Equal(
        Error(MigrationRepositorySettingsSurfaceRefusal.Partial
            "environment-secrets-variables-and-plan-conditions-remain-unbound"),
        source.Read(identity, revision, Environments))
    Assert.Equal(
        Error(MigrationRepositorySettingsReadFailure.ProviderRefused(
            SettingsSurface.Repository,
            MigrationRepositorySettingsSurfaceRefusal.Unsupported "surface-reader-not-installed:repository")),
        MigrationRepositorySettingsRead.captureTwoPass identity revision source)
    Assert.Empty transport.Requests

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
