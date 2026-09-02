#load "../src/FS.GG.Coordination.GitHub/FleetShadowAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubFleetShadowQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let root = if fsi.CommandLineArgs.Length > 1 then Path.GetFullPath fsi.CommandLineArgs[1] else Path.GetFullPath "."
let readDocument relative = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)))
let text (node: JsonElement) (name: string) = node.GetProperty(name).GetString()
let sha256 path = File.ReadAllBytes(path) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let capability = function
    | "roster-read" -> RosterRead | "metadata-read" -> MetadataRead | "issue-read" -> IssueRead
    | "project-read" -> ProjectRead | "journal-read" -> JournalRead | "check-read" -> CheckRead
    | value -> MutationCapability value
let classification = function
    | "v1-defect" -> V1Defect | "v2-defect" -> V2Defect | "intentional-versioned-change" -> IntentionalVersionedChange
    | value -> failwithf "unsupported divergence classification %s" value
let decision (node: JsonElement) = { Raw = text node "raw"; Normalized = text node "normalized"; SourceRevision = text node "sourceRevision" }
let divergence (node: JsonElement) =
    if node.ValueKind = JsonValueKind.Null then None
    else Some { Classification = classification (text node "classification"); AccountableAgent = text node "accountableAgent"; Evidence = text node "evidence" }
let item (node: JsonElement) =
    { Repository = text node "repository"; Item = text node "item"; V1 = decision (node.GetProperty "v1")
      V2 = decision (node.GetProperty "v2"); Divergence = divergence (node.GetProperty "divergence") }
let repository (node: JsonElement) =
    { Repository = text node "repository"; ExpectedItemCount = node.GetProperty("expectedItemCount").GetInt32()
      TerminalPageObserved = node.GetProperty("terminalPageObserved").GetBoolean()
      Items = node.GetProperty("items").EnumerateArray() |> Seq.map item |> Seq.toList }

let evidence = readDocument "evidence/github-substrate-v2/gs2-05-8-fleet-shadow.json"
let corpus = readDocument "evidence/github-substrate-v2/gs2-05-8/corpus.json"
let independentDocument = readDocument "evidence/github-substrate-v2/gs2-05-8/independent-expectations.json"
let e = evidence.RootElement
let requiredIds = GitHubFleetShadowQualification.requiredControls |> List.map GitHubFleetShadowQualification.controlId
let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let independentIds = independentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
if generatedIds <> requiredIds || independentIds <> requiredIds then failwith "fleet-shadow control inventory is not exact"
if text corpus.RootElement "registeredContractSha256" <> "da7733bdd597a976a5132d14f9c7c0136fb7e4e90cbb05864d465a7200730ce2" then failwith "fleet-shadow registered contract mismatch"
if text corpus.RootElement "liveEvidenceSha256" <> sha256 (Path.Combine(root, "evidence/github-substrate-v2/gs2-05-8-fleet-shadow.json")) then failwith "live fleet evidence changed"
if text corpus.RootElement "acceptedPredecessorReceiptSha256" <> sha256 (Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.7.json")) then failwith "accepted GS2-05.7 receipt file changed"
let o = e.GetProperty "observation"
let observation =
    { Complete = o.GetProperty("complete").GetBoolean(); RosterRevision = text o "rosterRevision"
      Roster = o.GetProperty("roster").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
      WindowStartedAt = DateTimeOffset.Parse(text o "windowStartedAt"); WindowEndedAt = DateTimeOffset.Parse(text o "windowEndedAt")
      Capabilities = o.GetProperty("capabilities").EnumerateArray() |> Seq.map (_.GetString() >> capability) |> Seq.toList
      MutationAttempts = o.GetProperty("mutationAttempts").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
      Repositories = o.GetProperty("repositories").EnumerateArray() |> Seq.map repository |> Seq.toList }
let expected = e.GetProperty "report"
let asOf = observation.WindowEndedAt.AddMinutes 1
let report = FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) observation |> Result.defaultWith (failwithf "fleet shadow refused: %A")
if report.RepositoryCount <> expected.GetProperty("repositoryCount").GetInt32()
   || report.ItemCount <> expected.GetProperty("itemCount").GetInt32()
   || report.EqualDecisionCount <> expected.GetProperty("equalDecisionCount").GetInt32()
   || report.ClassifiedDivergenceCount <> expected.GetProperty("classifiedDivergenceCount").GetInt32()
   || report.UnexplainedDivergenceCount <> 0
   || report.Seal <> text expected "seal" then failwith "fleet-shadow report does not match its sealed observation"
if FleetShadowAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) observation <> Ok report then failwith "exact fleet-shadow replay failed"
if text e "unitId" <> "GS2-05.8" then failwith "wrong fleet-shadow unit"
let prerequisite = e.GetProperty "prerequisite"
if text prerequisite "unitId" <> "GS2-05.7" || text prerequisite "receiptDigest" <> "77ba4ae9ddf350ec93afe7021b320474c5f04ed5f7a255fa2136a3f15af5af12" then failwith "wrong GS2-05.7 prerequisite"
let source = o.GetProperty "source"
if source.GetProperty("credentialsRetained").GetBoolean() || source.GetProperty("itemCount").GetInt32() <> report.ItemCount then failwith "live source proof is incomplete"
let liveSourcePath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-8/live-ready.json")
if text source "sha256" <> sha256 liveSourcePath then failwith "retained live source changed"
let liveSource = JsonDocument.Parse(File.ReadAllText liveSourcePath)
let liveRows =
    liveSource.RootElement.EnumerateArray()
    |> Seq.map (fun row ->
        let repositoryName = text row "repo"
        let issueNumber = row.GetProperty("number").GetInt32()
        repositoryName, $"{repositoryName}#{issueNumber}", text row "status")
    |> Seq.sort
    |> Seq.toList
let evidenceRows =
    observation.Repositories
    |> List.collect (fun repository -> repository.Items |> List.map (fun item -> item.Repository, item.Item, item.V1.Raw))
    |> List.sort
if liveRows.Length <> report.ItemCount || liveRows <> evidenceRows then failwith "retained live source does not exactly match v1 decisions"
if observation.Roster.Length <> report.RepositoryCount || observation.Repositories |> List.exists (fun repository -> not repository.TerminalPageObserved) then failwith "fleet completeness proof failed"
let quint = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
if sha256 quint <> "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218" then failwith "canonical Quint source changed"

let baselineGreen = report.UnexplainedDivergenceCount = 0 && report.ItemCount = 180 && report.RepositoryCount = 10
let classify classificationValue =
    let firstRepository = observation.Repositories.Head
    let firstItem = firstRepository.Items.Head
    let changed =
        { firstItem with
            V2 = { firstItem.V2 with Normalized = firstItem.V2.Normalized + "-changed" }
            Divergence = Some { Classification = classificationValue; AccountableAgent = "independent-critic"; Evidence = "bound divergence evidence" } }
    { observation with Repositories = { firstRepository with Items = changed :: firstRepository.Items.Tail } :: observation.Repositories.Tail }
let unexplained () =
    let classified = classify V1Defect
    let firstRepository = classified.Repositories.Head
    let firstItem = firstRepository.Items.Head
    { classified with Repositories = { firstRepository with Items = { firstItem with Divergence = None } :: firstRepository.Items.Tail } :: classified.Repositories.Tail }
let generatedMutation control =
    match control with
    | RosterCompleteness -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Roster = observation.Roster.Tail } |> Result.isError
    | PaginationCompleteness -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Repositories = { observation.Repositories.Head with TerminalPageObserved = false } :: observation.Repositories.Tail } |> Result.isError
    | StableOrdering -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Repositories = List.rev observation.Repositories } |> Result.isError
    | DecisionPreservation | ExactSeal -> FleetShadowAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) { observation with RosterRevision = "altered" } |> Result.isError
    | EqualDecision ->
        let firstRepository = observation.Repositories.Head
        let firstItem = firstRepository.Items.Head
        let altered = { observation with Repositories = { firstRepository with Items = { firstItem with Divergence = Some { Classification = V1Defect; AccountableAgent = "critic"; Evidence = "invalid on equal decision" } } :: firstRepository.Items.Tail } :: observation.Repositories.Tail }
        FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) altered |> Result.isError
    | V1DefectClassification -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) (classify V1Defect) |> Result.isOk
    | V2DefectClassification -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) (classify V2Defect) |> Result.isOk
    | VersionChangeClassification -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) (classify IntentionalVersionedChange) |> Result.isOk
    | ZeroUnexplained -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) (unexplained ()) |> Result.isError
    | ReadOnlyManifest -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Capabilities = observation.Capabilities @ [ MutationCapability "write" ] } |> Result.isError
    | NoMutationAttempt -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with MutationAttempts = [ "write" ] } |> Result.isError
    | FreshObservation -> FleetShadowAdapter.compare (asOf.AddHours 2) (TimeSpan.FromHours 1) observation |> Result.isError
    | ExactReplay -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) observation = Ok report
    | CrossSubject -> let first = observation.Repositories.Head in FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Repositories = { first with Items = [ { first.Items.Head with Repository = "other/repo" } ] } :: observation.Repositories.Tail } |> Result.isError
    | PartialUnreadable -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Complete = false } |> Result.isError
    | QuintAndPrerequisite -> sha256 quint = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
    | LiveEvidence -> text source "command" = "fsgg-coord ready --all --json" && liveRows = evidenceRows
let independentMutation control =
    match control with
    | StableOrdering -> FleetShadowAdapter.compare asOf (TimeSpan.FromHours 1) { observation with Roster = List.rev observation.Roster } |> Result.isError
    | DecisionPreservation ->
        let firstRepository = observation.Repositories.Head
        let firstItem = firstRepository.Items.Head
        let altered = { observation with Repositories = { firstRepository with Items = { firstItem with V1 = { firstItem.V1 with Raw = firstItem.V1.Raw + "!" } } :: firstRepository.Items.Tail } :: observation.Repositories.Tail }
        FleetShadowAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) altered |> Result.isError
    | ExactSeal -> FleetShadowAdapter.verify (String.replicate 64 "0") asOf (TimeSpan.FromHours 1) observation |> Result.isError
    | _ -> generatedMutation control
let generated: GitHubFleetShadowControlResult list = GitHubFleetShadowQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = baselineGreen })
let independent: GitHubFleetShadowControlResult list = GitHubFleetShadowQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = baselineGreen })
match GitHubFleetShadowQualification.validate generated independent with
| Error findings -> failwithf "fleet-shadow qualification failed: %A" findings
| Ok () -> printfn "GITHUB_FLEET_SHADOW_OK repositories=%d items=%d equal=%d classified=%d unexplained=%d seal=%s" report.RepositoryCount report.ItemCount report.EqualDecisionCount report.ClassifiedDivergenceCount report.UnexplainedDivergenceCount report.Seal
