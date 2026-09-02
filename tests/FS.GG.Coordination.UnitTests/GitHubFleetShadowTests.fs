module FS.GG.Coordination.GitHubFleetShadowTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let start = DateTimeOffset.Parse("2026-09-02T00:12:00Z")
let finish = DateTimeOffset.Parse("2026-09-02T00:13:00Z")
let asOf = DateTimeOffset.Parse("2026-09-02T00:14:00Z")
let decision raw normalized revision = { Raw = raw; Normalized = normalized; SourceRevision = revision }
let equalItem repo item value =
    { Repository = repo; Item = item; V1 = decision value value "v1-r1"; V2 = decision value value "v2-r1"; Divergence = None }
let divergentItem repo item classification =
    { Repository = repo; Item = item; V1 = decision "legacy-ready" "ready" "v1-r1"
      V2 = decision "native-ready" "native-ready" "v2-r1"
      Divergence = Some { Classification = classification; AccountableAgent = "critic-1"; Evidence = "bound finding" } }
let refusals = function Ok report -> failwithf "expected refusal, got %A" report | Error values -> values
let observation =
    { Complete = true; RosterRevision = "roster-r1"; Roster = [ "fs-gg/.github"; "fs-gg/fs.gg.coordination" ]
      WindowStartedAt = start; WindowEndedAt = finish; Capabilities = FleetShadowAdapter.requiredCapabilities
      MutationAttempts = []
      Repositories =
        [ { Repository = "FS-GG/.github"; ExpectedItemCount = 1; TerminalPageObserved = true
            Items = [ equalItem "FS-GG/.github" "FS-GG/.github#1" "ready" ] }
          { Repository = "FS-GG/FS.GG.Coordination"; ExpectedItemCount = 1; TerminalPageObserved = true
            Items = [ divergentItem "FS-GG/FS.GG.Coordination" "FS-GG/FS.GG.Coordination#2" IntentionalVersionedChange ] } ] }

[<Fact>]
let ``complete read-only fleet reports zero unexplained divergence and stable replay`` () =
    let first = FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) observation |> Result.defaultWith (failwithf "%A")
    let second = FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) observation |> Result.defaultWith (failwithf "%A")
    Assert.Equal(2, first.RepositoryCount)
    Assert.Equal(2, first.ItemCount)
    Assert.Equal(1, first.EqualDecisionCount)
    Assert.Equal(1, first.ClassifiedDivergenceCount)
    Assert.Equal(0, first.UnexplainedDivergenceCount)
    Assert.Equal(first, second)
    Assert.Equal(Ok first, FleetShadowAdapter.verify first.Seal asOf (TimeSpan.FromHours 1) observation)

[<Fact>]
let ``coverage divergence and subject defects fail closed`` () =
    let missingRepo = { observation with Repositories = observation.Repositories |> List.tail }
    Assert.Contains(MissingFleetRepository "fs-gg/.github", FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) missingRepo |> refusals)
    let incomplete = { observation with Repositories = [ { observation.Repositories.Head with TerminalPageObserved = false }; observation.Repositories[1] ] }
    Assert.Contains(IncompleteFleetRepository "fs-gg/.github", FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) incomplete |> refusals)
    let unclassifiedItem = { divergentItem "FS-GG/.github" "FS-GG/.github#1" V1Defect with Divergence = None }
    let unclassified = { observation with Repositories = [ { observation.Repositories.Head with Items = [ unclassifiedItem ] }; observation.Repositories[1] ] }
    Assert.Contains(UnclassifiedFleetDivergence "FS-GG/.github#1", FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) unclassified |> refusals)
    let crossedItem = { observation.Repositories.Head.Items.Head with Repository = "FS-GG/other" }
    let crossed = { observation with Repositories = [ { observation.Repositories.Head with Items = [ crossedItem ] }; observation.Repositories[1] ] }
    Assert.Contains(CrossRepositoryFleetItem "FS-GG/.github#1", FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) crossed |> refusals)
    let reordered = { observation with Repositories = List.rev observation.Repositories }
    Assert.Contains(InvalidFleetRoster, FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) reordered |> refusals)
    let duplicateAcrossRepositories =
        { observation with
            Repositories =
                [ observation.Repositories.Head
                  { observation.Repositories[1] with Items = [ { observation.Repositories[1].Items.Head with Item = "FS-GG/.github#1" } ] } ] }
    Assert.Contains(DuplicateFleetItem "FS-GG/.github#1", FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) duplicateAcrossRepositories |> refusals)

[<Fact>]
let ``permissions attempts freshness and seals are independent fences`` () =
    let permission = { observation with Capabilities = observation.Capabilities @ [ MutationCapability "issues:write" ] }
    let permissionErrors = FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) permission |> refusals
    Assert.Contains(InvalidFleetCapabilityManifest, permissionErrors)
    Assert.Contains(FleetMutationCapabilityPresent "issues:write", permissionErrors)
    Assert.Equal(Error [ FleetMutationAttempted "PATCH /issues/1" ], FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with MutationAttempts = [ "PATCH /issues/1" ] })
    Assert.Equal(Error [ StaleFleetObservation ], FleetShadowAdapter.compare (finish.AddHours 2) (TimeSpan.FromHours 1) observation)
    let report = FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) observation |> Result.defaultWith (failwithf "%A")
    let changed = { observation with RosterRevision = "roster-r2" }
    Assert.Equal(Error [ AlteredFleetShadowSeal ], FleetShadowAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) changed)

[<Fact>]
let ``generated and independent Q4 control inventories are exact`` () =
    let passing: GitHubFleetShadowControlResult list =
        GitHubFleetShadowQualification.requiredControls
        |> List.map (fun control -> ({ Control = control; MutationRed = true; BaselineGreen = true }: GitHubFleetShadowControlResult))
    Assert.Equal(Ok(), GitHubFleetShadowQualification.validate passing passing)
    let broken = passing |> List.tail
    match GitHubFleetShadowQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "independent-INVENTORY")
    | Ok _ -> failwith "missing control survived"
