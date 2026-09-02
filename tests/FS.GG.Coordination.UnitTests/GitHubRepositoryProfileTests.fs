module FS.GG.Coordination.GitHubRepositoryProfileTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private reviewedAt = DateTimeOffset.Parse("2026-09-02T00:00:00Z")
let private rows =
    [ { Id = ".github"; FullName = "FS-GG/.github"; Role = Authority; Capabilities = [ "labels" ]; KitDelivery = None; AbsenceCover = None; Reason = None }
      { Id = "sdd"; FullName = "FS-GG/FS.GG.SDD"; Role = Framework; Capabilities = [ "labels"; "coordination-kit" ]; KitDelivery = Some "package"; AbsenceCover = Some "required"; Reason = None }
      { Id = "sir"; FullName = "EHotwagner/S.I.R."; Role = NonParticipant; Capabilities = []; KitDelivery = None; AbsenceCover = None; Reason = Some "external owner" } ]

let private snapshot rows =
    { SchemaVersion = 1
      SourceRevision = "1da112a0d80fd38e5b2a9780db77a4fb98c873ca"
      SourceArtifactSha256 = String.replicate 64 "a"
      CanonicalRosterSha256 = RepositoryProfileAdapter.canonicalRosterDigest rows
      ReviewedAt = reviewedAt
      Complete = true
      Rows = rows }

let private findings = function Ok _ -> [] | Error values -> values

[<Fact>]
let ``repository profiles retain rich authority and never plan writes for external rows`` () =
    let first = RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) (snapshot rows) |> Result.defaultWith (failwithf "%A")
    let second = RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) (snapshot (List.rev rows)) |> Result.defaultWith (failwithf "%A")
    Assert.Equal(first, second)
    Assert.Equal(3, first.Profiles.Length)
    let framework = first.Profiles |> List.find (fun profile -> profile.Id = "sdd")
    Assert.Equal<string list>([ "coordination-kit"; "labels" ], framework.Capabilities)
    Assert.Equal(3, framework.NativeProperties.Length)
    Assert.True(framework.PropertyMutationPermitted)
    let external = first.Profiles |> List.find (fun profile -> profile.Id = "sir")
    Assert.Equal(AdministrationBoundary.ExternalObserveOnly, external.Administration)
    Assert.Empty(external.NativeProperties)
    Assert.False(external.PropertyMutationPermitted)
    Assert.Equal(Some "external owner", external.Reason)
    Assert.Equal(Ok first, RepositoryProfileAdapter.verify first.Seal reviewedAt (TimeSpan.FromHours 1) (snapshot rows))

[<Fact>]
let ``repository profile compiler fails closed on identity source freshness and vocabulary defects`` () =
    let duplicate = rows @ [ rows.Head ]
    let duplicateFindings = RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) (snapshot duplicate) |> findings
    Assert.Contains(DuplicateRepositoryId ".github", duplicateFindings)
    Assert.Contains(DuplicateRepositoryName "fs-gg/.github", duplicateFindings)
    let unsupported = rows |> List.map (fun row -> if row.Id = "sdd" then { row with Capabilities = "unknown" :: row.Capabilities } else row)
    Assert.Contains(UnsupportedRepositoryCapability("FS-GG/FS.GG.SDD", "unknown"), RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) (snapshot unsupported) |> findings)
    let stale = snapshot rows
    Assert.Equal(Error [ StaleRoster ], RepositoryProfileAdapter.compile (reviewedAt.AddHours 2) (TimeSpan.FromHours 1) stale)
    Assert.Contains(InvalidSourceBinding "sourceRevision", RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) { stale with SourceRevision = "main" } |> findings)
    Assert.Contains(InvalidSourceBinding "canonicalRosterSha256", RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) { stale with CanonicalRosterSha256 = String.replicate 64 "0" } |> findings)
    let baseline = RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) stale |> Result.defaultWith (failwithf "%A")
    Assert.Equal(Error [ AlteredRepositoryProfileSeal ], RepositoryProfileAdapter.verify baseline.Seal reviewedAt (TimeSpan.FromHours 1) { stale with SourceArtifactSha256 = String.replicate 64 "f" })
    let crossed = rows |> List.map (fun row -> if row.Id = "sdd" then { row with FullName = "someone/FS.GG.SDD" } else row)
    Assert.Contains(UnsupportedRepositoryRole "someone/FS.GG.SDD", RepositoryProfileAdapter.compile reviewedAt (TimeSpan.FromHours 1) (snapshot crossed) |> findings)

[<Fact>]
let ``repository profile qualification requires both complete mutation inventories`` () =
    let passing: GitHubRepositoryProfileControlResult list =
        GitHubRepositoryProfileQualification.requiredControls
        |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok(), GitHubRepositoryProfileQualification.validate passing passing)
    let broken = passing |> List.map (fun result -> if result.Control = GitHubRepositoryProfileControl.ExternalObserveOnly then { result with MutationRed = false } else result)
    match GitHubRepositoryProfileQualification.validate passing broken with
    | Ok _ -> Assert.Fail("external-owner mutation survived independent qualification")
    | Error values -> Assert.Contains("RP-MUTATION-SURVIVED", values |> List.map _.Code)
