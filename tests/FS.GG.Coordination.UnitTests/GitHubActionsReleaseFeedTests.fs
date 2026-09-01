module FS.GG.Coordination.GitHubActionsReleaseFeedTests

open System.Text
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private repository = "FS-GG/fixture"
let private revision = "abc123"
let private digest = String.replicate 64 "a"
let private packageAttrs = Map [ "owner", "FS-GG"; "name", "pkg"; "version", "1.0.0"; "feed", "github" ]
let private item surface identity attempt lifecycle state attributes =
    { Surface = surface; Identity = identity; Repository = repository; Subject = revision; Attempt = attempt; Lifecycle = lifecycle; State = state; Attributes = attributes; Digest = Some digest }
let private items =
    [ item ActionsRuns "run:1/attempt:1" (Some 1) (Some Completed) Present (Map [ "workflow-id", "1" ])
      item Checks "suite:1/run:2" None (Some Neutral) Present (Map [ "suite-id", "1" ])
      item MergeGroups "group:1" None None Present (Map [ "head-sha", revision ])
      item Releases "release:1/asset:1" None None Immutable (Map [ "tag", "v1" ])
      item Attestations "attestation:1" None None Present (Map [ "predicate", "slsa"; "subject-digest", digest ])
      item Packages "package:1" None None Immutable packageAttrs
      item Feeds "feed:1" None None Present packageAttrs
      item ServedDownloads "download:1" None None Present (Map [ "final-uri", "https://example.test/pkg" ]) ]
let private surfaceMap =
    let grouped = items |> List.groupBy _.Surface |> Map.ofList
    ActionsReleaseFeedAdapter.surfaces |> List.map (fun surface -> surface, ArtifactSurfaceObservation.Supported(revision, true, 2, grouped.[surface])) |> Map.ofList
let private observed surfaces =
    { Repository = repository; RepositoryNodeId = "R_fixture"; CapturedRevision = revision; Surfaces = surfaces; Fingerprint = ActionsReleaseFeedAdapter.fingerprint repository "R_fixture" revision surfaces }

[<Fact>]
let completeExactObservationsValidate () =
    let baseline = observed surfaceMap
    Assert.Equal(Ok baseline, ActionsReleaseFeedAdapter.validate baseline)
    Assert.Equal(Error ArtifactFailure.InvalidFingerprint, ActionsReleaseFeedAdapter.validate { baseline with Fingerprint = "bad" })

[<Fact>]
let attemptPaginationAndAvailabilityFailClosed () =
    let run = items |> List.find (fun value -> value.Surface = ActionsRuns)
    let invalid = surfaceMap |> Map.add ActionsRuns (ArtifactSurfaceObservation.Supported(revision, true, 2, [ { run with Attempt = Some 0 } ])) |> observed
    Assert.Equal(Error(ArtifactFailure.InvalidLifecycle(ActionsRuns, run.Identity)), ActionsReleaseFeedAdapter.validate invalid)
    let partial = surfaceMap |> Map.add Checks (ArtifactSurfaceObservation.Supported(revision, false, 1, [ items.[1] ])) |> observed
    Assert.Equal(Error(ArtifactFailure.PartialSurface(Checks, "pagination incomplete")), ActionsReleaseFeedAdapter.validate partial)
    let denied = surfaceMap |> Map.add Feeds (ArtifactSurfaceObservation.Unauthorized "denied") |> observed
    Assert.Equal(Error(ArtifactFailure.PartialSurface(Feeds, "denied")), ActionsReleaseFeedAdapter.validate denied)

[<Fact>]
let attestationPackageAndSecretCoordinatesAreExact () =
    let attestation = items |> List.find (fun value -> value.Surface = Attestations)
    let invalid = surfaceMap |> Map.add Attestations (ArtifactSurfaceObservation.Supported(revision, true, 1, [ { attestation with Attributes = Map.empty } ])) |> observed
    Assert.Equal(Error(ArtifactFailure.InvalidAttestation attestation.Identity), ActionsReleaseFeedAdapter.validate invalid)
    let package = items |> List.find (fun value -> value.Surface = Packages)
    let secret = surfaceMap |> Map.add Packages (ArtifactSurfaceObservation.Supported(revision, true, 1, [ { package with Attributes = package.Attributes |> Map.add "token" "forbidden" } ])) |> observed
    Assert.Equal(Error ArtifactFailure.SecretMaterialForbidden, ActionsReleaseFeedAdapter.validate secret)

[<Fact>]
let servedBytesBindRedirectsAndRetrievalClass () =
    let bytes = Encoding.UTF8.GetBytes "artifact"
    let content = ActionsReleaseFeedAdapter.observeServedContent "https://example.test/start" [ "https://cdn.example.test/one" ] "https://cdn.example.test/final" 200 "application/octet-stream" AnonymousPublic bytes |> Result.defaultWith (failwithf "%A")
    Assert.Equal(ActionsReleaseFeedAdapter.sha256 bytes, content.Sha256)
    Assert.Equal(int64 bytes.Length, content.Length)
    Assert.True(ActionsReleaseFeedAdapter.validateStages [ PublicServedBytes content ] |> Result.isOk)
    Assert.True(ActionsReleaseFeedAdapter.observeServedContent "http://unsafe.test" [] "http://unsafe.test" 200 "text/plain" AnonymousPublic bytes |> Result.isError)

[<Fact>]
let evidenceLadderDoesNotPromoteUploadAcceptance () =
    Assert.Equal(Ok [ UploadAccepted "request" ], ActionsReleaseFeedAdapter.validateStages [ UploadAccepted "request" ])

[<Fact>]
let qualificationInventoryIsMutationSensitive () =
    let passing: GitHubActionsReleaseFeedControlResult list = GitHubActionsReleaseFeedQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubActionsReleaseFeedQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index value -> if index = 12 then { value with MutationRed = false } else value)
    match GitHubActionsReleaseFeedQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.ControlId = "byte-digest" && finding.Code = "GARFQ-INDEPENDENT-NOT-RED")
    | Ok () -> failwith "accepted a load-bearing mutation"
