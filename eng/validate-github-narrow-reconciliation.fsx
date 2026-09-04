#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubNarrowReconciliationQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-narrow-reconciliation.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let readJson relative = JsonDocument.Parse(File.ReadAllText(path relative))
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let strings (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let contract = readJson "evidence/github-substrate-v2/gs2-07-2/contract.json"
let c = contract.RootElement
if text "schema" c <> "fsgg.github-narrow-reconciliation-evidence/v1" || text "unit" c <> "GS2-07.2" then failwith "evidence contract identity differs"
if shaFile "evidence/github-substrate-v2/accepted/GS2-07.1.json" <> text "prerequisiteFileSha256" c then failwith "accepted prerequisite bytes differ"
if shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" <> text "protocolSha256" c then failwith "canonical Quint protocol changed"
if strings "eventKinds" c <> supportedEventKinds || strings "writerBoundary" c <> writerBoundary then failwith "retained inventory differs"
let repository = "FS-GG/FS.GG.Coordination"
let revision = text "sourceRevision" c
let event kind id subjectRevision delivery =
    { EventKind = kind; Repository = repository; SourceRevision = revision; SubjectKind = kind
      SubjectId = id; SubjectRevision = subjectRevision; DeliveryId = delivery
      Route = $"reconcile/{kind}"; Origin = "event"; AttemptsDerivedWrite = false }
let allEvents = supportedEventKinds |> List.mapi (fun index kind -> event kind (string(index + 1)) 1L $"delivery-{index + 1}")
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = compile repository revision allEvents |> get
let bytes = serialize baseline
let tamperedSeal = (if baseline.Seal[0] = '0' then "1" else "0") + baseline.Seal.Substring 1
let issue = event "issue" "299" 2L "delivery-2"
let sourceText = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubNarrowReconciliationQualification.fs")

let executeGenerated control =
    match control with
    | ReconciliationPrerequisites -> text "prerequisiteReceiptDigest" c = "825781cedeebbd56aad3a3d41499d6f9bbc647da372f8a91df7c7e2a5ed336e1"
    | ReconciliationRoadmap -> text "roadmapSha256" c = "0a9a10c017b184a50c3348e882b264e90a4f2c5736206de8ab9e52330304f7fd"
    | ReconciliationCompleteness -> compile repository revision [] = Error [ GitHubNarrowReconciliationFinding.IncompleteEventInventory ]
    | ReconciliationEventKind -> baseline.SupportedEventKinds = supportedEventKinds && baseline.Entries.Length = supportedEventKinds.Length
    | ReconciliationSubject -> compile repository revision [ { issue with SubjectKind = "release" } ] |> has (GitHubNarrowReconciliationFinding.ConflictingSubject "release:299")
    | ReconciliationRevision -> compile repository revision [ { issue with SubjectRevision = 0L } ] |> has (GitHubNarrowReconciliationFinding.StaleRevision "0")
    | ReconciliationRouting -> compile repository revision [ { issue with Route = "reconcile/release" } ] |> has (GitHubNarrowReconciliationFinding.AlteredRouting "reconcile/release")
    | ReconciliationSchedulingKey ->
        (compile repository revision [ event "issue" "a:b" 1L "a" ] |> get).Entries.Head.SchedulingKey <>
        (compile repository revision [ event "issue" "a" 1L "a" ] |> get).Entries.Head.SchedulingKey
    | ReconciliationDeduplication ->
        let plan = compile repository revision [ { issue with SubjectRevision = 1L; DeliveryId = "delivery-1" }; issue ] |> get
        plan.Entries.Length = 1 && plan.Entries.Head.SubjectRevision = 2L
    | ReconciliationDuplicate -> (compile repository revision [ issue; issue ] |> get).Entries.Length = 1
    | ReconciliationReorder -> compile repository revision (List.rev allEvents) = Ok baseline
    | ReconciliationUnsupported -> compile repository revision [ event "workflow" "1" 1L "delivery" ] |> has (GitHubNarrowReconciliationFinding.UnknownEventKind "workflow")
    | ReconciliationScope -> compile repository revision [ { issue with Repository = "FS-GG/Other" } ] |> has (GitHubNarrowReconciliationFinding.CrossScope "FS-GG/Other")
    | ReconciliationExclusiveWriter -> baseline.WriterBoundary = [ "fresh-observe"; "reduce"; "sealed-plan"; "apply"; "verify" ]
    | ReconciliationDirectWrite -> compile repository revision [ { issue with AttemptsDerivedWrite = true } ] |> has (GitHubNarrowReconciliationFinding.DirectWrite "delivery-2")
    | ReconciliationSealedPlan -> verify baseline.Seal { baseline with WriterBoundary = writerBoundary.Tail } = Error [ GitHubNarrowReconciliationFinding.UnsealedPlan ]
    | ReconciliationOrdering -> verify baseline.Seal { baseline with Entries = List.rev baseline.Entries } = Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "entry ordering" ]
    | ReconciliationSeal -> verify (String.replicate 64 "0") baseline = Error [ GitHubNarrowReconciliationFinding.AlteredSeal ]
    | ReconciliationReplay -> replay baseline allEvents = Ok baseline && serialize (replay baseline allEvents |> get) = bytes
    | ReconciliationQuintPreservation -> shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" = text "protocolSha256" c
    | ReconciliationNoNetwork -> not(Regex.IsMatch(sourceText, "HttpClient|WebRequest|webhook", RegexOptions.IgnoreCase))
    | ReconciliationNoProductionQueue -> not(Regex.IsMatch(sourceText, "QueueClient|enqueue|dequeue", RegexOptions.IgnoreCase))
    | ReconciliationNoMutation -> not(Regex.IsMatch(sourceText, "Octokit|GitHubClient|\\b(PATCH|POST|PUT|DELETE)\\b", RegexOptions.IgnoreCase))

let executeIndependent control =
    match control with
    | ReconciliationPrerequisites -> shaFile "evidence/github-substrate-v2/accepted/GS2-07.1.json" = text "prerequisiteFileSha256" c
    | ReconciliationRoadmap -> text "roadmapRevision" c = "6849585bc46b542e1d5ca93410a92a0f7ee15cdc"
    | ReconciliationCompleteness -> compile repository revision [ { issue with DeliveryId = " " } ] |> has (GitHubNarrowReconciliationFinding.MissingField "deliveryId")
    | ReconciliationEventKind -> strings "eventKinds" c = supportedEventKinds
    | ReconciliationSubject -> compile repository revision [ { issue with EventKind = "release"; Route = "reconcile/release" } ] |> has (GitHubNarrowReconciliationFinding.ConflictingSubject "issue:299")
    | ReconciliationRevision -> compile repository revision [ { issue with SubjectRevision = -1L } ] |> has (GitHubNarrowReconciliationFinding.StaleRevision "-1")
    | ReconciliationRouting -> compile repository revision [ { issue with Route = "reconcile/installation" } ] |> has (GitHubNarrowReconciliationFinding.AlteredRouting "reconcile/installation")
    | ReconciliationSchedulingKey ->
        let scoped = { event "issue" "a:b" 1L "a" with Repository = "FS-GG/Other" }
        (compile repository revision [ event "issue" "a:b" 1L "a" ] |> get).Entries.Head.SchedulingKey <>
        (compile "FS-GG/Other" revision [ scoped ] |> get).Entries.Head.SchedulingKey
    | ReconciliationDeduplication ->
        let rows = [ event "issue" "299" 1L "d1"; event "issue" "299" 3L "d3"; event "issue" "299" 2L "d2" ]
        let entry = (compile repository revision rows |> get).Entries.Head
        entry.SubjectRevision = 3L && entry.DeduplicationDisposition = "deduplicated"
    | ReconciliationDuplicate -> replay (compile repository revision [ issue ] |> get) [ issue; issue ] |> Result.isOk
    | ReconciliationReorder ->
        let rows = [ event "issue" "1" 1L "one"; event "release" "2" 1L "two"; event "ruleset" "3" 1L "three" ]
        compile repository revision rows = compile repository revision [ rows[2]; rows[0]; rows[1] ]
    | ReconciliationUnsupported -> compile repository revision [ event "installation_target" "1" 1L "delivery" ] |> has (GitHubNarrowReconciliationFinding.UnknownEventKind "installation_target")
    | ReconciliationScope -> compile repository revision [ { issue with SourceRevision = String.replicate 40 "a" } ] |> has (GitHubNarrowReconciliationFinding.StaleRevision(String.replicate 40 "a"))
    | ReconciliationExclusiveWriter -> verify baseline.Seal { baseline with WriterBoundary = writerBoundary |> List.take 4 } = Error [ GitHubNarrowReconciliationFinding.UnsealedPlan ]
    | ReconciliationDirectWrite -> compile repository revision [ { issue with Origin = "command"; AttemptsDerivedWrite = true } ] |> has (GitHubNarrowReconciliationFinding.DirectWrite "delivery-2")
    | ReconciliationSealedPlan -> verify baseline.Seal { baseline with WriterBoundary = [ "fresh-observe"; "reduce"; "draft-plan"; "apply"; "verify" ] } = Error [ GitHubNarrowReconciliationFinding.UnsealedPlan ]
    | ReconciliationOrdering -> verify baseline.Seal { baseline with Entries = List.rev baseline.Entries } = Error [ GitHubNarrowReconciliationFinding.InvalidSerialization "entry ordering" ]
    | ReconciliationSeal -> parse (bytes.Replace(baseline.Seal, tamperedSeal)) = Error [ GitHubNarrowReconciliationFinding.AlteredSeal ]
    | ReconciliationReplay -> replay baseline [ event "issue" "new" 1L "new-delivery" ] |> has (GitHubNarrowReconciliationFinding.ReplayConflict "new or newer subject requires fresh reconciliation")
    | ReconciliationQuintPreservation -> text "protocolSha256" c = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
    | ReconciliationNoNetwork -> let detector (value: string) = Regex.IsMatch(value, "httpclient|webrequest|webhook", RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText + "\nHttpCLIENT")
    | ReconciliationNoProductionQueue -> let detector (value: string) = Regex.IsMatch(value, "queueclient|enqueue|dequeue", RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText + "\nEnQueue")
    | ReconciliationNoMutation -> let detector (value: string) = Regex.IsMatch(value, "octokit|githubclient|\\b(patch|post|put|delete)\\b", RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText + "\nGitHubClient")

let baselineGreen = parse bytes = Ok baseline && verify baseline.Seal baseline = Ok baseline
let generated: GitHubNarrowReconciliationControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeGenerated control; BaselineGreen = baselineGreen })
let independent: GitHubNarrowReconciliationControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeIndependent control; BaselineGreen = baselineGreen })
let retained relative =
    use document = readJson relative
    strings "controls" document.RootElement, strings "cases" document.RootElement, text "caseContract" document.RootElement
let expectedIds = requiredControls |> List.map controlId
let generatedIds, generatedCases, generatedContract = retained "evidence/github-substrate-v2/gs2-07-2/generated-controls.json"
let independentIds, independentCases, independentContract = retained "evidence/github-substrate-v2/gs2-07-2/independent-controls.json"
if generatedIds <> expectedIds || generatedCases.Length <> expectedIds.Length || generatedCases |> List.exists String.IsNullOrWhiteSpace then failwith "generated retained inventory differs"
if independentIds <> expectedIds || independentCases.Length <> expectedIds.Length || independentCases |> List.exists String.IsNullOrWhiteSpace then failwith "independent retained inventory differs"
if generatedCases = independentCases || generatedContract = independentContract then failwith "control authorship is not independent"
match validateControls generated independent with
| Ok () -> ()
| Error errors -> failwithf "Q3 controls failed: %A; generated=%A; independent=%A" errors generated independent
printfn "GITHUB_NARROW_RECONCILIATION_OK events=%d entries=%d controls=%d seal=%s" supportedEventKinds.Length baseline.Entries.Length expectedIds.Length baseline.Seal
