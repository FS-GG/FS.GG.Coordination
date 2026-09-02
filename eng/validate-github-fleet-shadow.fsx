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
if text corpus.RootElement "registeredContractSha256" <> "b738d44b1bfafd6a5bbd2e00e4e4817c3dd607cefff5e0833c23112e82440c59" then failwith "fleet-shadow registered contract mismatch"
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
if source.GetProperty("credentialsRetained").GetBoolean() then failwith "live source retained credentials"
let v1Source = source.GetProperty "v1"
let v2Source = source.GetProperty "v2"
let v1SourcePath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-8/live-ready.json")
let v2SourcePath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-8/live-project-pages.json")
if text v1Source "sha256" <> sha256 v1SourcePath || text v2Source "sha256" <> sha256 v2SourcePath then failwith "retained live source changed"
let v1Document = JsonDocument.Parse(File.ReadAllText v1SourcePath)
let v2Document = JsonDocument.Parse(File.ReadAllText v2SourcePath)
let v1Rows =
    v1Document.RootElement.EnumerateArray()
    |> Seq.map (fun row ->
        let repositoryName = text row "repo"
        let issueNumber = row.GetProperty("number").GetInt32()
        repositoryName, $"{repositoryName}#{issueNumber}", text row "status")
    |> Seq.sort
    |> Seq.toList
let v2Pages = v2Document.RootElement.EnumerateArray() |> Seq.toList
let v2PageCounts = v2Pages |> List.map (fun page -> page.GetProperty("data").GetProperty("organization").GetProperty("projectV2").GetProperty("items").GetProperty("nodes").GetArrayLength())
if v2PageCounts <> [ 100; 80 ]
   || v2Pages.Head.GetProperty("data").GetProperty("organization").GetProperty("projectV2").GetProperty("items").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean() <> true
   || v2Pages[1].GetProperty("data").GetProperty("organization").GetProperty("projectV2").GetProperty("items").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean() <> false then
    failwith "retained v2 pagination proof is incomplete"
let v2Rows =
    v2Pages
    |> List.collect (fun page -> page.GetProperty("data").GetProperty("organization").GetProperty("projectV2").GetProperty("items").GetProperty("nodes").EnumerateArray() |> Seq.toList)
    |> List.map (fun row ->
        let content = row.GetProperty "content"
        let repositoryName = content.GetProperty("repository").GetProperty("nameWithOwner").GetString()
        let issueNumber = content.GetProperty("number").GetInt32()
        let statuses =
            row.GetProperty("fieldValues").GetProperty("nodes").EnumerateArray()
            |> Seq.choose (fun value ->
                let mutable field = Unchecked.defaultof<JsonElement>
                let mutable name = Unchecked.defaultof<JsonElement>
                if value.TryGetProperty("field", &field) && field.TryGetProperty("name", &name) && name.GetString() = "Status" then Some(text value "name") else None)
            |> Seq.toList
        if statuses.Length <> 1 then failwithf "v2 subject %s#%d has no exact status" repositoryName issueNumber
        repositoryName, $"{repositoryName}#{issueNumber}", statuses.Head)
    |> List.sort
let evidenceV1Rows =
    observation.Repositories
    |> List.collect (fun repository -> repository.Items |> List.map (fun item -> item.Repository, item.Item, item.V1.Raw))
    |> List.sort
let evidenceV2Rows =
    observation.Repositories
    |> List.collect (fun repository -> repository.Items |> List.map (fun item -> item.Repository, item.Item, item.V2.Raw))
    |> List.sort
if v1Rows.Length <> report.ItemCount || v2Rows.Length <> report.ItemCount || v1Rows <> evidenceV1Rows || v2Rows <> evidenceV2Rows || v1Rows <> v2Rows then
    failwith "independent retained sources do not exactly match the sealed decisions"
let independence = source.GetProperty "independence"
if text independence "highestReached" <> "value-independent" || independence.GetProperty("residual").GetArrayLength() <> 2 then failwith "independence residual is not disclosed"
if text v1Source "command" <> "fsgg-coord ready --all --json" || text v2Source "command" <> "gh api graphql --paginate --slurp" then failwith "live commands are not independently bound"
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
    | LiveEvidence -> text v1Source "command" = "fsgg-coord ready --all --json" && text v2Source "command" = "gh api graphql --paginate --slurp" && v1Rows = evidenceV1Rows && v2Rows = evidenceV2Rows
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
