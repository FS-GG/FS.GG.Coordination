module FS.GG.Coordination.MigrationRepositorySettingsReadTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.GitHub

let private identity =
    { NodeId="R_kgDOSettings"; DatabaseId=41L; Owner="FS-GG"; Name="sandbox"
      DefaultBranch="main"; SourceRepositoryNodeId=None }

let private revision = "2026-09-28T00:00:00Z"

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData
    |> Convert.ToHexString |> _.ToLowerInvariant()

let private read surface suffix =
    let body = $"{{\"surface\":\"{RepositorySettingsAdapter.surfaceId surface}\",\"pass\":\"{suffix}\"}}"
    { RepositoryIdentity=identity
      RepositoryRevision=revision
      Surface=surface
      Complete=true
      Pages=
        [ { SettingsRequestedUri=$"https://api.github.test/repos/FS-GG/sandbox/settings/{RepositorySettingsAdapter.surfaceId surface}"
            SettingsPayloadJson=body; SettingsPayloadSha256=sha body; SettingsNextUri=None } ]
      Settings=
        [ { Surface=surface; Subject="FS-GG/sandbox"; Name="enabled"; Value=Boolean true } ] }

type private FakeProvider(results: Result<MigrationRepositorySettingsSurfaceRead, MigrationRepositorySettingsSurfaceRefusal> list) =
    let mutable remaining = results
    let requests = ResizeArray<SettingsSurface>()
    member _.Requests = requests |> Seq.toList
    interface IMigrationRepositorySettingsSurfaceProvider with
        member _.Read(actualIdentity, actualRevision, surface) =
            Assert.Equal(identity, actualIdentity)
            Assert.Equal(revision, actualRevision)
            requests.Add surface
            match remaining with
            | head :: tail -> remaining <- tail; head
            | [] -> failwith "unexpected provider request"

let private stableResults () =
    [ for _ in 1..2 do
          for surface in RepositorySettingsAdapter.surfaces do
              Ok(read surface "stable") ]

[<Fact>]
let ``complete composer requires two equal provider reads of the exact eleven surfaces`` () =
    let provider = FakeProvider(stableResults())
    match MigrationRepositorySettingsRead.captureTwoPass identity revision provider with
    | Error failure -> failwithf "complete settings capture refused: %A" failure
    | Ok captured ->
        Assert.Equal(22, provider.Requests.Length)
        Assert.Equal<SettingsSurface list>(
            RepositorySettingsAdapter.surfaces @ RepositorySettingsAdapter.surfaces,
            provider.Requests)
        Assert.Equal(11, captured.First.Count)
        Assert.Equal(64, captured.CaptureFingerprint.Length)
        Assert.Equal(Ok captured, MigrationRepositorySettingsRead.validateCapture captured)
        match MigrationRepositorySettingsRead.composeComplete captured with
        | Error failure -> failwithf "complete settings composition refused: %A" failure
        | Ok observation ->
            Assert.Equal(identity, observation.Identity)
            Assert.Equal(revision, observation.CapturedRevision)
            Assert.Equal<SettingsSurface list>(
                RepositorySettingsAdapter.surfaces,
                observation.Surfaces |> Map.keys |> Seq.toList)
            Assert.Equal(Ok observation, RepositorySettingsAdapter.validate observation)

[<Theory>]
[<InlineData("unsupported")>]
[<InlineData("conditional")>]
[<InlineData("partial")>]
let ``unsupported conditional and partial provider surfaces refuse canonical capture`` kind =
    let refusal =
        match kind with
        | "unsupported" -> MigrationRepositorySettingsSurfaceRefusal.Unsupported "api-not-supported"
        | "conditional" -> MigrationRepositorySettingsSurfaceRefusal.Conditional "private-only"
        | _ -> MigrationRepositorySettingsSurfaceRefusal.Partial "inherited-provenance-missing"
    let provider =
        FakeProvider(
            [ Ok(read SettingsSurface.Repository "stable")
              Error refusal ])
    Assert.Equal(
        Error(MigrationRepositorySettingsReadFailure.ProviderRefused(CustomProperties, refusal)),
        MigrationRepositorySettingsRead.captureTwoPass identity revision provider)
    Assert.Equal<SettingsSurface list>([ SettingsSurface.Repository; CustomProperties ], provider.Requests)

[<Fact>]
let ``capture refuses typed or raw drift between provider passes`` () =
    let first = RepositorySettingsAdapter.surfaces |> List.map (fun surface -> Ok(read surface "stable"))
    let second =
        RepositorySettingsAdapter.surfaces
        |> List.map (fun surface ->
            if surface = CodeSecurity then
                let changed = read surface "stable"
                Ok { changed with Settings=[ { changed.Settings.Head with Value=Boolean false } ] }
            else Ok(read surface "stable"))
    Assert.Equal(
        Error(MigrationRepositorySettingsReadFailure.SnapshotDrift CodeSecurity),
        MigrationRepositorySettingsRead.captureTwoPass identity revision (FakeProvider(first @ second)))

    let broken = read SettingsSurface.Repository "stable"
    let invalid =
        { broken with
            Pages=
                [ { broken.Pages.Head with
                      SettingsPayloadJson=broken.Pages.Head.SettingsPayloadJson + " " } ] }
    Assert.Equal(
        Error(MigrationRepositorySettingsReadFailure.EvidenceInvalid(SettingsSurface.Repository, "payload-hash-drift")),
        MigrationRepositorySettingsRead.captureTwoPass identity revision
            (FakeProvider([ Ok invalid ])))

[<Fact>]
let ``retained capture refuses incomplete pagination and fingerprint substitution`` () =
    let provider = FakeProvider(stableResults())
    let captured =
        MigrationRepositorySettingsRead.captureTwoPass identity revision provider
        |> Result.defaultWith (failwithf "capture refused: %A")

    let firstRepository = captured.First[SettingsSurface.Repository]
    let openPage =
        { firstRepository.Pages.Head with SettingsNextUri=Some "https://api.github.test/next" }
    let incompleteRead = { firstRepository with Pages=[ openPage ] }
    let incomplete =
        { captured with First=Map.add SettingsSurface.Repository incompleteRead captured.First
                        Second=Map.add SettingsSurface.Repository incompleteRead captured.Second }
    Assert.Equal(
        Error(MigrationRepositorySettingsReadFailure.PartialSurface(SettingsSurface.Repository, "unterminated-pagination")),
        MigrationRepositorySettingsRead.validateCapture incomplete)

    let substituted = { captured with CaptureFingerprint=String.replicate 64 "0" }
    Assert.Equal(
        Error MigrationRepositorySettingsReadFailure.CaptureFingerprintDrift,
        MigrationRepositorySettingsRead.composeComplete substituted)
