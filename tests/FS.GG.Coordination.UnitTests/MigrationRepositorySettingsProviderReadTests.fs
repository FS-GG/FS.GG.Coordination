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
            if surface = ReleasesAndTags then
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
let ``release provider pages join the eleven-surface two-pass composer`` () =
    let transport = FakeTransport(successPass() @ successPass())
    let concrete =
        MigrationRepositorySettingsGitHubProvider(options, transport)
        :> IMigrationRepositorySettingsSurfaceProvider
    let provider = HybridProvider(concrete)
    match MigrationRepositorySettingsRead.captureTwoPass identity revision provider with
    | Error failure -> failwithf "two-pass provider capture refused: %A" failure
    | Ok captured ->
        Assert.Equal(10, transport.Requests.Length)
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
