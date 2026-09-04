#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventEnvelopeQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-event-envelope.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let readJson relative = JsonDocument.Parse(File.ReadAllText(path relative))
let text (name:string) (node:JsonElement) = node.GetProperty(name).GetString()
let contract = readJson "evidence/github-substrate-v2/gs2-07-1/contract.json"
let c = contract.RootElement
if text "schema" c <> "fsgg.github-event-envelope-evidence/v1" || text "unit" c <> "GS2-07.1" then failwith "evidence contract identity differs"
if shaFile "evidence/github-substrate-v2/accepted/GS2-06.8.json" <> text "prerequisiteFileSha256" c then failwith "accepted prerequisite bytes differ"
if shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" <> text "protocolSha256" c then failwith "canonical Quint protocol changed"
let source = { Kind="issues"; InstallationId="installation-42"; Repository="FS-GG/FS.GG.Coordination"; SourceRevision=text "sourceRevision" c }
let delivery pos did eid subject revision cause correlation receipt =
    { CursorPosition=pos; DeliveryId=did; EventId=eid; Subject=subject; SubjectRevision=string revision
      CausationId=cause; CorrelationId=correlation; ReceiptId=receipt; ReceiptDisposition="accepted" }
let d1 = delivery 1L "delivery-1" "event-1" "issue-294" 1 "cause-root" "corr-294" "receipt-1"
let d2 = delivery 2L "delivery-2" "event-2" "issue-294" 2 "event-1" "corr-294" "receipt-2"
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let baseline = compile source [d1;d2] |> get
let bytes = serialize baseline
let isError = Result.isError
let sourceText = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventEnvelopeQualification.fs")
let executeGenerated control =
    match control with
    | EventPrerequisites -> File.Exists(path "evidence/github-substrate-v2/accepted/GS2-06.8.json")
    | EventRoadmap -> text "roadmapSha256" c = "152956bff4f264d7a6e034c0d8553d3df2cd44ac6773b03e83f85ff52dfb4655"
    | EventCompleteness -> compile source [] |> isError
    | EventSource -> compile {source with Kind=" "} [d1] |> isError
    | EventDeliveryIdentity -> compile source [d1;{d1 with SubjectRevision="2"}] |> isError
    | EventIdentity -> compile source [d1;{d1 with DeliveryId="other-delivery"}] |> isError
    | EventSubject -> compile source [d1;{d2 with Subject="issue-elsewhere"}] |> isError
    | EventRevision -> compile source [{d1 with SubjectRevision="0"}] |> isError
    | EventCausation -> compile source [{d1 with CausationId=d1.EventId}] |> isError
    | EventCorrelation -> compile source [{d1 with CorrelationId=d1.DeliveryId}] |> isError
    | EventReceipt -> compile source [{d1 with ReceiptDisposition="unknown"}] |> isError
    | EventDuplicate -> compile source [d1;d2;d1] = Ok baseline
    | EventReorder -> compile source [d2;d1] = Ok baseline
    | EventConflict -> compile source [d1;{d1 with ReceiptId="receipt-conflict"}] |> isError
    | EventCursor -> compile source [d1;{d2 with CursorPosition=3L}] |> isError
    | EventOrdering -> (compile source [d2;d1] |> get).Deliveries = [d1;d2]
    | EventSeal -> verify (String.replicate 64 "0") baseline |> isError
    | EventReplay -> replay baseline source [d2;d1;d1] = Ok baseline
    | EventQuintPreservation -> shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" = text "protocolSha256" c
    | EventNoNetwork -> let detector (v:string)=v.Contains("HttpClient")||v.Contains("WebRequest") in not(detector sourceText)
    | EventNoQueue -> let detector (v:string)=v.Contains("enqueue")||v.Contains("dequeue") in not(detector sourceText)
    | EventNoMutation -> let detector (v:string)=v.Contains("Octokit")||v.Contains("PATCH ")||v.Contains("POST ") in not(detector sourceText)

// Independently authored adversarial cases intentionally do not delegate to the
// generated mutation function: each control has a second, distinct observation.
let executeIndependent control =
    match control with
    | EventPrerequisites -> text "prerequisiteReceiptDigest" c = "c8831d8e3b06f77ae26d23579b738347794a8d08e460c84c5856cbbff50abd0e"
    | EventRoadmap -> text "roadmapRevision" c = "d0267c02c59de75571f6ee9086f924e8c924da08"
    | EventCompleteness -> compile source [{d1 with ReceiptId=""}] |> isError
    | EventSource -> compile {source with Kind="unknown-kind"} [d1] |> isError
    | EventDeliveryIdentity -> compile source [d1;{d1 with ReceiptId="receipt-conflict"}] |> isError
    | EventIdentity -> compile source [d1;{d1 with EventId="other-event"}] |> isError
    | EventSubject -> compile source [d1;{d2 with Subject="issue-elsewhere"}] |> isError
    | EventRevision -> compile source [{d1 with SubjectRevision="-1"}] |> isError
    | EventCausation -> compile source [d1;{d2 with CausationId="event-999"}] |> isError
    | EventCorrelation -> compile source [d1;{d2 with CorrelationId="corr-other"}] |> isError
    | EventReceipt -> compile source [d1;{d2 with ReceiptId=d1.ReceiptId}] |> isError
    | EventDuplicate -> replay baseline source [d1;d2;d1] = Ok baseline
    | EventReorder -> replay baseline source [d2;d1] = Ok baseline
    | EventConflict -> compile source [d1;{d1 with SubjectRevision="2"}] |> isError
    | EventCursor ->
        let left = delivery 1L "a:b" "c" "issue-294" 1 "cause-root" "corr-294" "r"
        let right = delivery 1L "a" "b:c" "issue-294" 1 "cause-root" "corr-294" "r"
        (compile source [left] |> get).Cursor <> (compile source [right] |> get).Cursor
    | EventOrdering -> verify baseline.Seal {baseline with Deliveries=[d2;d1]} |> isError
    | EventSeal -> parse (bytes.Replace(baseline.Seal,String.replicate 64 "0")) |> isError
    | EventReplay -> replay {baseline with SchemaVersion=2} source [] |> isError
    | EventQuintPreservation -> shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" = text "protocolSha256" c
    | EventNoNetwork -> let detector (v:string)=Regex.IsMatch(v,"httpclient|webrequest|webhook",RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText+"\nhttpCLIENT")
    | EventNoQueue -> let detector (v:string)=Regex.IsMatch(v,"enqueue|dequeue|queueclient",RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText+"\nEnQueue")
    | EventNoMutation -> let detector (v:string)=Regex.IsMatch(v,"octokit|githubclient|\\b(patch|post|put|delete)\\b",RegexOptions.IgnoreCase) in not(detector sourceText) && detector(sourceText+"\nGitHubClient")
let baselineGreen = parse bytes = Ok baseline && verify baseline.Seal baseline = Ok baseline
let generated: GitHubEventEnvelopeControlResult list = requiredControls |> List.map(fun control->{Control=control;ControlPassed=executeGenerated control;BaselineGreen=baselineGreen})
let independent: GitHubEventEnvelopeControlResult list = requiredControls |> List.map(fun control->{Control=control;ControlPassed=executeIndependent control;BaselineGreen=baselineGreen})
let retained relative =
    use document = readJson relative
    let root = document.RootElement
    let controls = root.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    let cases = root.GetProperty("cases").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    controls, cases, text "caseContract" root
let ids = requiredControls |> List.map controlId
let generatedIds, generatedCases, generatedContract = retained "evidence/github-substrate-v2/gs2-07-1/generated-controls.json"
let independentIds, independentCases, independentContract = retained "evidence/github-substrate-v2/gs2-07-1/independent-controls.json"
if generatedIds <> ids || generatedCases.Length <> ids.Length || generatedCases |> List.exists String.IsNullOrWhiteSpace then failwith "generated retained inventory differs"
if independentIds <> ids || independentCases.Length <> ids.Length || independentCases |> List.exists String.IsNullOrWhiteSpace then failwith "independent retained inventory differs"
if generatedCases = independentCases || generatedContract = independentContract then failwith "control authorship is not independent"
match validateControls generated independent with Ok () -> () | Error errors -> failwithf "Q3 controls failed: %A; generated=%A; independent=%A" errors generated independent
printfn "GITHUB_EVENT_ENVELOPE_OK deliveries=%d cursor=%d controls=%d seal=%s" baseline.Deliveries.Length baseline.Cursor.Length ids.Length baseline.Seal
