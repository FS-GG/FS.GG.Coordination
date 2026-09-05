#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubAuditRepairQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-audit-repair.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let readJson relative = JsonDocument.Parse(File.ReadAllText(path relative))
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let strings (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let contract = readJson "evidence/github-substrate-v2/gs2-07-3/contract.json"
let c = contract.RootElement
if text "schema" c <> "fsgg.github-audit-repair-evidence/v1" || text "unit" c <> "GS2-07.3" then failwith "evidence contract identity differs"
if shaFile "evidence/github-substrate-v2/accepted/GS2-07.2.json" <> text "prerequisiteFileSha256" c then failwith "accepted prerequisite bytes differ"
if shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" <> text "protocolSha256" c then failwith "canonical Quint protocol changed"
if strings "classifications" c <> requiredClassifications || strings "writerBoundary" c <> writerBoundary then failwith "retained inventory differs"
let repository = "FS-GG/FS.GG.Coordination"
let externalRepository = "FS-GG/External"
let revision = text "sourceRevision" c
let scope = strings "auditScope" c
let cursor = text "cursor" c
let history repository kind id subjectRevision delivery =
    { Repository = repository; SourceRevision = revision; SubjectKind = kind; SubjectId = id; SubjectRevision = subjectRevision; DeliveryId = delivery }
let observation repo page pageCount kind id subjectRevision classification evidence =
    { Repository = repo; SourceRevision = revision; AuditScope = scope; Cursor = cursor
      Page = page; PageCount = pageCount; SubjectKind = kind; SubjectId = id; SubjectRevision = subjectRevision
      Classification = classification; EvidenceId = evidence; Route = $"reconcile/{kind}"
      Origin = "audit"; AttemptsDerivedWrite = false }
let histories =
    [ history repository "issue" "304" 1L "delivery-issue-1"
      history repository "project" "coordination" 4L "delivery-project-4" ]
let observations =
    [ observation repository 1 3 "issue" "304" 2L "dropped-delivery" "audit-drop-1"
      observation repository 2 3 "project" "coordination" 4L "preview-gap" "audit-preview-1"
      observation repository 3 3 "repository" "FS.GG.Coordination" 7L "schema-drift" "audit-schema-1"
      observation externalRepository 1 1 "repository" "External" 3L "external-repository" "audit-external-1" ]
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = compile repository revision scope cursor histories observations |> get
let bytes = serialize baseline
let sourceText = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubAuditRepairQualification.fs")
let issue = observations.Head

let executeGenerated control =
    match control with
    | AuditPrerequisites -> text "prerequisiteReceiptDigest" c = "6ae56a7c9dce52f3ac25e39145b275ed5e8127a1020ee8c65a392b976661c298"
    | AuditRoadmap -> text "roadmapSha256" c = "6e0de6a1f12de38c248c607c60064c8b81e1683460410acaa2f69aea47829844"
    | AuditCompleteness -> compile repository revision scope cursor histories [] |> has GitHubAuditRepairFinding.IncompleteAuditScope
    | AuditScope -> compile repository revision (List.rev scope) cursor histories observations |> has GitHubAuditRepairFinding.IncompleteAuditScope
    | AuditCursor -> compile repository revision scope "stale" histories observations |> has (GitHubAuditRepairFinding.StaleCursor "stale")
    | AuditEventHistory -> compile repository revision scope cursor (history repository "release" "missing" 1L "delivery-missing" :: histories) observations |> has (GitHubAuditRepairFinding.AlteredObservation $"{repository}|release:missing")
    | AuditObservation -> compile repository revision scope cursor histories ({ issue with Origin = "event" } :: observations.Tail) |> has (GitHubAuditRepairFinding.AlteredObservation "audit-drop-1")
    | AuditDeliveryGap -> (compile repository revision scope cursor [] observations |> get).Entries |> List.exists (fun entry -> entry.Classifications = [ "dropped-delivery" ] && entry.DeduplicationDisposition = "audit-repair")
    | AuditPreviewGap -> baseline.Entries |> List.exists (fun entry -> entry.Classifications = [ "preview-gap" ])
    | AuditExternalRepository -> baseline.Entries |> List.exists (fun entry -> entry.Repository = externalRepository)
    | AuditSchemaDrift -> baseline.Entries |> List.exists (fun entry -> entry.Classifications = [ "schema-drift" ])
    | AuditRepairRouting -> compile repository revision scope cursor histories ({ issue with Route = "reconcile/release" } :: observations.Tail) |> has (GitHubAuditRepairFinding.AlteredRouting "reconcile/release")
    | AuditSchedulingKey ->
        let otherScope = [ "FS-GG/External"; "FS-GG/Other" ]
        let other = observations |> List.map (fun row -> { row with Repository = (if row.Repository = repository then "FS-GG/Other" else row.Repository); AuditScope = otherScope })
        (compile repository revision scope cursor histories observations |> get).Entries.Head.SchedulingKey <>
        (compile "FS-GG/Other" revision otherScope cursor [] other |> get).Entries.Head.SchedulingKey
    | AuditDeduplication -> baseline.Entries.Length = 4
    | AuditConvergence -> baseline.Entries |> List.exists (fun entry -> entry.SubjectRevision = 2L && entry.DeduplicationDisposition = "event-audit-converged")
    | AuditOmission -> requiredClassifications |> List.forall (fun classification -> compile repository revision scope cursor [] (observations |> List.filter (fun row -> row.Classification <> classification)) |> has (GitHubAuditRepairFinding.OmittedClassification classification))
    | AuditExclusiveWriter -> baseline.WriterBoundary = writerBoundary
    | AuditDirectWrite -> compile repository revision scope cursor histories ({ issue with AttemptsDerivedWrite = true } :: observations.Tail) |> has (GitHubAuditRepairFinding.DirectWrite "audit-drop-1")
    | AuditSealedPlan -> verify baseline.Seal { baseline with WriterBoundary = writerBoundary.Tail } = Error [ GitHubAuditRepairFinding.UnsealedPlan ]
    | AuditOrdering -> verify baseline.Seal { baseline with Entries = List.rev baseline.Entries } = Error [ GitHubAuditRepairFinding.InvalidSerialization "entry ordering" ]
    | AuditSeal -> verify (String.replicate 64 "0") baseline = Error [ GitHubAuditRepairFinding.AlteredSeal ]
    | AuditReplay -> replay baseline histories observations = Ok baseline && serialize (replay baseline histories observations |> get) = bytes
    | AuditQuintPreservation -> shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" = text "protocolSha256" c
    | AuditNoNetwork -> not(Regex.IsMatch(sourceText, "HttpClient|WebRequest|webhook", RegexOptions.IgnoreCase))
    | AuditNoProductionQueue -> not(Regex.IsMatch(sourceText, "QueueClient|enqueue|dequeue", RegexOptions.IgnoreCase))
    | AuditNoMutation -> not(Regex.IsMatch(sourceText, "Octokit|GitHubClient|\\b(PATCH|POST|PUT|DELETE)\\b", RegexOptions.IgnoreCase))

let executeIndependent control =
    match control with
    | AuditPrerequisites -> shaFile "evidence/github-substrate-v2/accepted/GS2-07.2.json" = text "prerequisiteFileSha256" c
    | AuditRoadmap -> text "roadmapRevision" c = "9d88c7b7967e8d69c1b8873d718ee8f0f435afd9"
    | AuditCompleteness -> compile repository revision scope cursor histories (observations |> List.filter (fun row -> row.Page <> 2)) |> has (GitHubAuditRepairFinding.PartialPage repository)
    | AuditScope -> compile repository revision scope cursor histories ({ issue with Repository = "FS-GG/Outside" } :: observations.Tail) |> has (GitHubAuditRepairFinding.AlteredScope "FS-GG/Outside")
    | AuditCursor -> compile repository revision scope cursor histories ({ issue with Cursor = "audit:old" } :: observations.Tail) |> has (GitHubAuditRepairFinding.StaleCursor "audit:old")
    | AuditEventHistory -> let newer = { histories.Head with SubjectRevision = 9L } in (compile repository revision scope cursor (newer :: histories.Tail) observations |> get).Entries |> List.exists (fun entry -> entry.SubjectRevision = 9L)
    | AuditObservation -> compile repository revision scope cursor histories ({ issue with SourceRevision = String.replicate 40 "a" } :: observations.Tail) |> has (GitHubAuditRepairFinding.StaleRevision(String.replicate 40 "a"))
    | AuditDeliveryGap -> (compile repository revision scope cursor [] observations |> get).Entries.Length = 4
    | AuditPreviewGap -> compile repository revision scope cursor [] (observations |> List.filter (fun row -> row.Classification <> "preview-gap")) |> has (GitHubAuditRepairFinding.OmittedClassification "preview-gap")
    | AuditExternalRepository -> compile repository revision [ repository ] cursor [] observations |> has (GitHubAuditRepairFinding.AlteredScope externalRepository)
    | AuditSchemaDrift -> compile repository revision scope cursor histories ({ issue with Classification = "unexpected-field" } :: observations.Tail) |> has (GitHubAuditRepairFinding.AlteredClassification "unexpected-field")
    | AuditRepairRouting -> compile repository revision scope cursor histories ({ issue with Route = "reconcile/project" } :: observations.Tail) |> has (GitHubAuditRepairFinding.AlteredRouting "reconcile/project")
    | AuditSchedulingKey -> baseline.Entries |> List.map _.SchedulingKey |> Set.ofList |> Set.count = baseline.Entries.Length
    | AuditDeduplication -> let repeated = observations @ [ observations.Head ] in (compile repository revision scope cursor histories repeated |> get).Entries.Length = baseline.Entries.Length
    | AuditConvergence -> compile repository revision scope cursor (List.rev histories) (List.rev observations) = Ok baseline
    | AuditOmission -> compile repository revision scope cursor [] (observations |> List.filter (fun row -> row.Classification <> "external-repository")) |> has (GitHubAuditRepairFinding.OmittedClassification "external-repository")
    | AuditExclusiveWriter -> verify baseline.Seal { baseline with WriterBoundary = [ "fresh-observe"; "reduce"; "draft-plan"; "apply"; "verify" ] } = Error [ GitHubAuditRepairFinding.UnsealedPlan ]
    | AuditDirectWrite -> compile repository revision scope cursor histories ({ issue with Origin = "writer"; AttemptsDerivedWrite = true } :: observations.Tail) |> has (GitHubAuditRepairFinding.DirectWrite "audit-drop-1")
    | AuditSealedPlan -> verify baseline.Seal { baseline with WriterBoundary = writerBoundary |> List.take 4 } = Error [ GitHubAuditRepairFinding.UnsealedPlan ]
    | AuditOrdering -> verify baseline.Seal { baseline with Entries = baseline.Entries.Tail @ [ baseline.Entries.Head ] } = Error [ GitHubAuditRepairFinding.InvalidSerialization "entry ordering" ]
    | AuditSeal -> let changed = (if baseline.Seal[0] = '0' then "1" else "0") + baseline.Seal.Substring 1 in parse (bytes.Replace(baseline.Seal, changed)) = Error [ GitHubAuditRepairFinding.AlteredSeal ]
    | AuditReplay -> replay baseline histories ({ issue with SubjectRevision = 9L } :: observations.Tail) |> has (GitHubAuditRepairFinding.ReplayConflict "audit replay differs from the sealed plan")
    | AuditQuintPreservation -> text "protocolSha256" c = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
    | AuditNoNetwork -> let detector (value: string) = Regex.IsMatch(value, "httpclient|webrequest|webhook", RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText + "\nHttpCLIENT")
    | AuditNoProductionQueue -> let detector (value: string) = Regex.IsMatch(value, "queueclient|enqueue|dequeue", RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText + "\nEnQueue")
    | AuditNoMutation -> let detector (value: string) = Regex.IsMatch(value, "octokit|githubclient|\\b(patch|post|put|delete)\\b", RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText + "\nGitHubClient")

let baselineGreen = parse bytes = Ok baseline && verify baseline.Seal baseline = Ok baseline
let generated: GitHubAuditRepairControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeGenerated control; BaselineGreen = baselineGreen })
let independent: GitHubAuditRepairControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeIndependent control; BaselineGreen = baselineGreen })
let retained relative =
    use document = readJson relative
    strings "controls" document.RootElement, strings "cases" document.RootElement, text "caseContract" document.RootElement
let expectedIds = requiredControls |> List.map controlId
let generatedIds, generatedCases, generatedContract = retained "evidence/github-substrate-v2/gs2-07-3/generated-controls.json"
let independentIds, independentCases, independentContract = retained "evidence/github-substrate-v2/gs2-07-3/independent-controls.json"
if generatedIds <> expectedIds || generatedCases.Length <> expectedIds.Length || generatedCases |> List.exists String.IsNullOrWhiteSpace then failwith "generated retained inventory differs"
if independentIds <> expectedIds || independentCases.Length <> expectedIds.Length || independentCases |> List.exists String.IsNullOrWhiteSpace then failwith "independent retained inventory differs"
if generatedCases = independentCases || generatedContract = independentContract then failwith "control authorship is not independent"
match validateControls generated independent with
| Ok () -> ()
| Error errors -> failwithf "Q3 controls failed: %A; generated=%A; independent=%A" errors generated independent
printfn "GITHUB_AUDIT_REPAIR_OK observations=%d entries=%d controls=%d seal=%s" observations.Length baseline.Entries.Length expectedIds.Length baseline.Seal
