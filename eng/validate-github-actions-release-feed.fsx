#load "../src/FS.GG.Coordination.GitHub/ActionsReleaseFeedAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubActionsReleaseFeedQualification.fs"

open System
open System.IO
open System.Text
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type Control = GitHubActionsReleaseFeedControl
let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args.[0]
let fixturePath = Path.Combine(root, "tests/fixtures/github-actions-release-feed/contract.json")
if not (File.Exists fixturePath) then fail "GARFQ-FIXTURE-MISSING" fixturePath
let fixture = JsonDocument.Parse(File.ReadAllBytes fixturePath).RootElement
if fixture.GetProperty("schema").GetString() <> "fsgg.coordination.github-actions-release-feed-fixture/1" || not (fixture.GetProperty("synthetic").GetBoolean()) then fail "GARFQ-FIXTURE" fixturePath
let expected = GitHubActionsReleaseFeedQualification.requiredControls |> List.map GitHubActionsReleaseFeedQualification.controlId
let actual = fixture.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if actual <> expected then fail "GARFQ-INVENTORY" (String.concat "," actual)

let repository = "FS-GG/example"
let node = "R_example"
let revision = "0123456789abcdef"
let digest = String.replicate 64 "a"
let attrs values = values |> Map.ofList
let evidence surface identity attempt lifecycle state attributes =
    { Surface = surface; Identity = identity; Repository = repository; Subject = revision; Attempt = attempt; Lifecycle = lifecycle; State = state; Attributes = attributes; Digest = Some digest }
let packageAttrs = attrs [ "owner", "FS-GG"; "name", "pkg"; "version", "1.0.0"; "feed", "github" ]
let values =
    [ evidence ActionsRuns "workflow:42/run:7/attempt:1/job:9" (Some 1) (Some Completed) Present (attrs [ "workflow-id", "42"; "run-id", "7"; "job-id", "9" ])
      evidence Checks "suite:4/run:5" None (Some Neutral) Present (attrs [ "suite-id", "4"; "run-id", "5"; "commit", revision ])
      evidence MergeGroups "merge-group:mg/0123" None None Present (attrs [ "head-sha", revision; "base-sha", "base"; "commits", revision ])
      evidence Releases "release:3/tag:v1/asset:8" None None Immutable (attrs [ "tag", "v1"; "asset-id", "8"; "size", "7" ])
      evidence Attestations "attestation:11" None None Present (attrs [ "predicate", "https://slsa.dev/provenance/v1"; "subject-digest", digest ])
      evidence Packages "package:pkg/version:1.0.0" None None Immutable packageAttrs
      evidence Feeds "feed:github/package:pkg/version:1.0.0" None None Present packageAttrs
      evidence ServedDownloads "download:v1/pkg" None None Present (attrs [ "final-uri", "https://example.test/pkg"; "content-type", "application/octet-stream" ]) ]
let bySurface = values |> List.groupBy _.Surface |> Map.ofList
let surfaceMap = ActionsReleaseFeedAdapter.surfaces |> List.map (fun surface -> surface, Supported(revision, true, 2, bySurface |> Map.find surface)) |> Map.ofList
let observation surfaces =
    { Repository = repository; RepositoryNodeId = node; CapturedRevision = revision; Surfaces = surfaces; Fingerprint = ActionsReleaseFeedAdapter.fingerprint repository node revision surfaces }
let baseline = observation surfaceMap
let baselineGreen () = ActionsReleaseFeedAdapter.validate baseline |> Result.isOk
let replace surface value = surfaceMap |> Map.add surface value |> observation
let bytes = Encoding.UTF8.GetBytes "published artifact"
let served retrieval =
    ActionsReleaseFeedAdapter.observeServedContent "https://example.test/start" [ "https://cdn.example.test/one" ] "https://cdn.example.test/final" 200 "application/octet-stream" retrieval bytes
    |> Result.defaultWith (fail "GARFQ-SERVED" << sprintf "%A")
let result control red = { Control = control; MutationRed = red; BaselineGreen = baselineGreen () }
let entry surface = bySurface |> Map.find surface |> List.head
let mutate surface value =
    let current = bySurface |> Map.find surface
    replace surface (Supported(revision, true, 2, value :: current.Tail))

let evaluate = function
    | Control.RunAttempt -> ActionsReleaseFeedAdapter.validate (mutate ActionsRuns { entry ActionsRuns with Attempt = Some 0 }) |> Result.isError
    | Control.Rerun -> let current = entry ActionsRuns in ActionsReleaseFeedAdapter.validate (replace ActionsRuns (Supported(revision, true, 2, [ current; current ]))) |> Result.isError
    | Control.CheckSuite -> ActionsReleaseFeedAdapter.validate (mutate Checks { entry Checks with Surface = ActionsRuns }) |> Result.isError
    | Control.MergeGroup -> ActionsReleaseFeedAdapter.validate (mutate MergeGroups { entry MergeGroups with Repository = "FS-GG/other" }) |> Result.isError
    | Control.Pagination -> ActionsReleaseFeedAdapter.validate (replace ActionsRuns (Supported(revision, false, 1, bySurface |> Map.find ActionsRuns))) |> Result.isError
    | Control.ImmutableRelease -> (entry Releases).State = Immutable && ({ entry Releases with State = Tampered }).State <> Immutable
    | Control.AssetDeletion -> let deleted = { entry Releases with State = Deleted } in deleted.State = Deleted && deleted.Identity = (entry Releases).Identity
    | Control.AttestationSubject -> ActionsReleaseFeedAdapter.validate (mutate Attestations { entry Attestations with Attributes = Map.empty }) |> Result.isError
    | Control.PackageVersion -> ActionsReleaseFeedAdapter.validate (mutate Packages { entry Packages with Attributes = packageAttrs |> Map.remove "version" }) |> Result.isError
    | Control.AuthenticatedFeed -> ActionsReleaseFeedAdapter.validateStages [ AuthenticatedRetrieval(served AnonymousPublic) ] |> Result.isError
    | Control.PublicDownload -> ActionsReleaseFeedAdapter.validateStages [ PublicServedBytes(served AuthenticatedFeed) ] |> Result.isError
    | Control.Redirect -> ActionsReleaseFeedAdapter.observeServedContent "http://unsafe.test" [] "http://unsafe.test" 200 "text/plain" AnonymousPublic bytes |> Result.isError
    | Control.ByteDigest -> (served AnonymousPublic).Sha256 = ActionsReleaseFeedAdapter.sha256 bytes && (served AnonymousPublic).Sha256 <> String.replicate 64 "0"
    | Control.UploadResponse -> match ActionsReleaseFeedAdapter.validateStages [ UploadAccepted "request-1" ] with Ok [ UploadAccepted _ ] -> true | _ -> false
    | Control.Unauthorized -> ActionsReleaseFeedAdapter.validate (replace Checks (Unauthorized "denied")) |> Result.isError
    | Control.Unavailable -> ActionsReleaseFeedAdapter.validate (replace Releases (Unavailable "outage")) |> Result.isError
    | Control.Incomplete -> ActionsReleaseFeedAdapter.validate (replace Packages (Incomplete "next page missing")) |> Result.isError
    | Control.Stale -> ActionsReleaseFeedAdapter.validate (replace Feeds (StaleSurface("new", revision))) |> Result.isError

let generated = GitHubActionsReleaseFeedQualification.requiredControls |> List.map (fun control -> result control (evaluate control))
let independent =
    GitHubActionsReleaseFeedQualification.requiredControls
    |> List.map (fun control ->
        let red =
            match control with
            | Control.RunAttempt -> (entry ActionsRuns).Attempt = Some 1
            | Control.Rerun -> (mutate ActionsRuns { entry ActionsRuns with Attempt = Some 2 } |> ActionsReleaseFeedAdapter.validate |> Result.isOk)
            | Control.CheckSuite -> (entry Checks).Attributes.ContainsKey "suite-id"
            | Control.MergeGroup -> (entry MergeGroups).Attributes.ContainsKey "head-sha"
            | Control.Pagination -> ActionsReleaseFeedAdapter.validate (replace Checks (Supported(revision, false, 2, bySurface |> Map.find Checks))) |> Result.isError
            | Control.ImmutableRelease -> (entry Releases).State = Immutable
            | Control.AssetDeletion -> ({ entry Releases with State = Deleted }).State = Deleted
            | Control.AttestationSubject -> (entry Attestations).Attributes.["subject-digest"] = digest
            | Control.PackageVersion -> (entry Packages).Attributes.["version"] = "1.0.0"
            | Control.AuthenticatedFeed -> ActionsReleaseFeedAdapter.validateStages [ AuthenticatedRetrieval(served AuthenticatedFeed) ] |> Result.isOk
            | Control.PublicDownload -> ActionsReleaseFeedAdapter.validateStages [ PublicServedBytes(served AnonymousPublic) ] |> Result.isOk
            | Control.Redirect -> (served AnonymousPublic).Redirects.Length = 1
            | Control.ByteDigest -> (served AnonymousPublic).Length = int64 bytes.Length
            | Control.UploadResponse -> UploadAccepted "request-1" <> PublicServedBytes(served AnonymousPublic)
            | Control.Unauthorized -> ActionsReleaseFeedAdapter.validate (replace ActionsRuns (Unauthorized "no")) |> Result.isError
            | Control.Unavailable -> ActionsReleaseFeedAdapter.validate (replace Feeds (Unavailable "no")) |> Result.isError
            | Control.Incomplete -> ActionsReleaseFeedAdapter.validate (replace Attestations (Incomplete "no")) |> Result.isError
            | Control.Stale -> ActionsReleaseFeedAdapter.validate (replace ServedDownloads (StaleSurface("expected", "actual"))) |> Result.isError
        result control red)
match GitHubActionsReleaseFeedQualification.validate generated independent with
| Ok () -> printfn "github-actions-release-feed-contract OK controls=%d q=Q3 network=offline provenance=synthetic" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GARFQ-FAILED" $"{findings.Length} finding(s)"
