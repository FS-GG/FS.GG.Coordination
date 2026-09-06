#r "../src/FS.GG.Coordination.Qualification.Contracts/bin/Release/net10.0/FS.GG.Coordination.Qualification.Contracts.dll"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventSecurityQualification

let root =
    match fsi.CommandLineArgs |> Array.tryLast with
    | Some value when value <> fsi.CommandLineArgs[0] -> Path.GetFullPath value
    | _ -> failwith "usage: dotnet fsi eng/validate-github-event-security.fsx -- <root>"
let path relative = Path.Combine(root, relative)
let shaFile relative = File.ReadAllBytes(path relative) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let readJson relative = JsonDocument.Parse(File.ReadAllText(path relative))
let text (name: string) (node: JsonElement) = node.GetProperty(name).GetString()
let strings (name: string) (node: JsonElement) = node.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let contract = readJson "evidence/github-substrate-v2/gs2-07-4/contract.json"
let c = contract.RootElement
if text "schema" c <> "fsgg.github-event-security-evidence/v1" || text "unit" c <> "GS2-07.4" then failwith "evidence contract identity differs"
if shaFile "evidence/github-substrate-v2/accepted/GS2-07.3.json" <> text "prerequisiteFileSha256" c then failwith "accepted prerequisite bytes differ"
if shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" <> text "protocolSha256" c then failwith "canonical Quint protocol changed"
if text "disposition" c <> disposition then failwith "event disposition differs"

let payload = Encoding.UTF8.GetBytes "{\"action\":\"edited\",\"issue\":{\"id\":310}}"
let secret = Encoding.UTF8.GetBytes "0123456789abcdef0123456789abcdef"
let received = c.GetProperty("receivedAtUnixSeconds").GetInt64()
let window = c.GetProperty("replayWindowSeconds").GetInt64()
let baselineFacts =
    let unsigned =
        { RawPayload = payload; Signature = ""; Secret = secret; DeliveryId = "delivery-gs2-07-4"
          InstallationId = c.GetProperty("installationId").GetInt64(); ExpectedInstallationId = c.GetProperty("installationId").GetInt64()
          Repository = text "repository" c; ExpectedRepository = text "repository" c
          ReceivedAtUnixSeconds = received; EventTimestampUnixSeconds = received; ReplayWindowSeconds = window
          SeenDeliveryIds = []; PayloadSubject = "issue:310"; PayloadRevision = 7L
          ApiSubject = "issue:310"; ApiRevision = 7L
          RequiredPermissions = strings "requiredPermissions" c; GrantedPermissions = strings "requiredPermissions" c
          AttemptsDerivedWrite = false }
    { unsigned with Signature = sign secret payload }
let get = function Ok value -> value | Error errors -> failwithf "baseline refused: %A" errors
let has expected = function Error errors -> List.contains expected errors | Ok _ -> false
let baseline = compile baselineFacts |> get
let bytes = serialize baseline
let sourceText = File.ReadAllText(path "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventSecurityQualification.fs")

let executeGenerated control =
    match control with
    | EventSecurityPrerequisite -> text "prerequisiteReceiptDigest" c = "4c6a18a3c8cca8ebd59ce040f63f0192c07f9468ad155e7941f676b0b611719c"
    | EventSecurityRoadmap -> text "roadmapSha256" c = "66b69d9a0c5df8f4786a5c7954a5d0b3fb87e6ce2f4ed0563b3b43e45179952a"
    | SignaturePositive ->
        let independent =
            use hmac = new HMACSHA256(secret)
            "sha256=" + (hmac.ComputeHash(payload) |> Convert.ToHexString |> _.ToLowerInvariant())
        baselineFacts.Signature = independent && compile baselineFacts = Ok baseline
    | SignatureNegative -> compile { baselineFacts with RawPayload = Encoding.UTF8.GetBytes "{}" } |> has GitHubEventSecurityFinding.InvalidSignature
    | EventInstallationScope -> compile { baselineFacts with InstallationId = 7L } |> has (GitHubEventSecurityFinding.InstallationScopeMismatch 7L)
    | EventRepositoryScope -> compile { baselineFacts with Repository = "FS-GG/Outside" } |> has (GitHubEventSecurityFinding.RepositoryScopeMismatch "FS-GG/Outside")
    | ReplayLowerBound -> compile { baselineFacts with EventTimestampUnixSeconds = received - window } |> Result.isOk
    | ReplayUpperBound -> compile { baselineFacts with EventTimestampUnixSeconds = received + window } |> Result.isOk
    | DuplicateDelivery -> compile { baselineFacts with SeenDeliveryIds = [ baselineFacts.DeliveryId ] } |> has (GitHubEventSecurityFinding.DuplicateDelivery baselineFacts.DeliveryId)
    | PayloadApiAgreement -> baseline.Subject = baselineFacts.ApiSubject && baseline.SubjectRevision = baselineFacts.ApiRevision
    | PayloadApiDisagreement -> compile { baselineFacts with ApiRevision = 8L } |> has (GitHubEventSecurityFinding.PayloadApiDisagreement "revision")
    | LeastPrivilege -> baseline.RequiredPermissions = baselineFacts.RequiredPermissions
    | ExcessivePermission -> compile { baselineFacts with GrantedPermissions = [ "contents:read"; "issues:write"; "metadata:read" ] } |> has (GitHubEventSecurityFinding.ExcessivePermission "issues:write")
    | MissingPermission -> compile { baselineFacts with GrantedPermissions = [ "metadata:read" ] } |> has (GitHubEventSecurityFinding.MissingPermission "contents:read")
    | SchedulingOnly -> baseline.Disposition = disposition && not baseline.AttemptsDerivedWrite
    | ExclusiveWriter -> verify baseline.Seal { baseline with Disposition = "apply-derived-state" } |> has (GitHubEventSecurityFinding.DirectWriteAttempt baseline.DeliveryId)
    | DirectWrite -> compile { baselineFacts with AttemptsDerivedWrite = true } |> has (GitHubEventSecurityFinding.DirectWriteAttempt baselineFacts.DeliveryId)
    | EventSecurityOrdering -> verify baseline.Seal { baseline with RequiredPermissions = List.rev baseline.RequiredPermissions } |> has (GitHubEventSecurityFinding.NonCanonicalPermissions "required")
    | EventSecuritySeal -> verify (String.replicate 64 "0") baseline = Error [ GitHubEventSecurityFinding.AlteredSeal ]
    | EventSecurityReplay -> replay baseline baselineFacts = Ok baseline && serialize (replay baseline baselineFacts |> get) = bytes
    | EventSecurityQuintPreservation -> shaFile "src/FS.GG.Coordination.Protocol/Protocol.md" = text "protocolSha256" c
    | EventSecurityNoNetwork -> not(Regex.IsMatch(sourceText, "HttpClient|WebRequest", RegexOptions.IgnoreCase))
    | EventSecurityNoProductionQueue -> not(Regex.IsMatch(sourceText, "QueueClient|enqueue|dequeue", RegexOptions.IgnoreCase))
    | EventSecurityNoMutation -> not(Regex.IsMatch(sourceText, "Octokit|GitHubClient|\\b(PATCH|POST|PUT|DELETE)\\b", RegexOptions.IgnoreCase))

let executeIndependent control =
    match control with
    | EventSecurityPrerequisite -> shaFile "evidence/github-substrate-v2/accepted/GS2-07.3.json" = text "prerequisiteFileSha256" c
    | EventSecurityRoadmap -> text "roadmapRevision" c = "cac998e81bbbdd1f4b1259e3b9a7173e161a9da6"
    | SignaturePositive -> sign secret payload = baselineFacts.Signature
    | SignatureNegative -> compile { baselineFacts with Signature = baselineFacts.Signature.ToUpperInvariant() } |> has GitHubEventSecurityFinding.InvalidSignature
    | EventInstallationScope -> compile { baselineFacts with ExpectedInstallationId = 0L } |> has (GitHubEventSecurityFinding.MalformedField "installationId")
    | EventRepositoryScope -> compile { baselineFacts with ExpectedRepository = "FS-GG/Other" } |> has (GitHubEventSecurityFinding.RepositoryScopeMismatch baselineFacts.Repository)
    | ReplayLowerBound -> compile { baselineFacts with EventTimestampUnixSeconds = received - window - 1L } |> has (GitHubEventSecurityFinding.ReplayExpired(received - window - 1L))
    | ReplayUpperBound -> compile { baselineFacts with EventTimestampUnixSeconds = received + window + 1L } |> has (GitHubEventSecurityFinding.ReplayFromFuture(received + window + 1L))
    | DuplicateDelivery -> compile { baselineFacts with SeenDeliveryIds = [ "other"; baselineFacts.DeliveryId ] } |> has (GitHubEventSecurityFinding.DuplicateDelivery baselineFacts.DeliveryId)
    | PayloadApiAgreement -> compile baselineFacts |> Result.isOk
    | PayloadApiDisagreement -> compile { baselineFacts with ApiSubject = "issue:311" } |> has (GitHubEventSecurityFinding.PayloadApiDisagreement "subject")
    | LeastPrivilege -> compile { baselineFacts with GrantedPermissions = List.rev baselineFacts.GrantedPermissions } |> has (GitHubEventSecurityFinding.NonCanonicalPermissions "granted")
    | ExcessivePermission -> compile { baselineFacts with GrantedPermissions = baselineFacts.GrantedPermissions @ [ "projects:write" ] } |> has (GitHubEventSecurityFinding.ExcessivePermission "projects:write")
    | MissingPermission -> compile { baselineFacts with GrantedPermissions = [ "contents:read" ] } |> has (GitHubEventSecurityFinding.MissingPermission "metadata:read")
    | SchedulingOnly -> baseline.Disposition = "schedule-reconciliation" && baseline.SchedulingKey.Length = 64
    | ExclusiveWriter -> verify baseline.Seal { baseline with AttemptsDerivedWrite = true } |> has (GitHubEventSecurityFinding.DirectWriteAttempt baseline.DeliveryId)
    | DirectWrite -> compile { baselineFacts with AttemptsDerivedWrite = true } |> Result.isError
    | EventSecurityOrdering -> parse (bytes + " ") = Error [ GitHubEventSecurityFinding.InvalidSerialization "non-canonical bytes" ]
    | EventSecuritySeal ->
        let changed = (if baseline.Seal[0] = '0' then "1" else "0") + baseline.Seal.Substring 1
        parse (bytes.Replace(baseline.Seal, changed)) = Error [ GitHubEventSecurityFinding.AlteredSeal ]
    | EventSecurityReplay -> replay baseline { baselineFacts with DeliveryId = "delivery-changed" } |> has (GitHubEventSecurityFinding.ReplayConflict "event replay differs from sealed plan")
    | EventSecurityQuintPreservation -> text "protocolSha256" c = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
    | EventSecurityNoNetwork ->
        let detector (value: string) = Regex.IsMatch(value, "httpclient|webrequest", RegexOptions.IgnoreCase)
        not(detector sourceText) && detector(sourceText + "\nHttpCLIENT")
    | EventSecurityNoProductionQueue ->
        let detector (value: string) = Regex.IsMatch(value, "queueclient|enqueue|dequeue", RegexOptions.IgnoreCase)
        not(detector sourceText) && detector(sourceText + "\nEnQueue")
    | EventSecurityNoMutation ->
        let detector (value: string) = Regex.IsMatch(value, "octokit|githubclient|\\b(patch|post|put|delete)\\b", RegexOptions.IgnoreCase)
        not(detector sourceText) && detector(sourceText + "\nGitHubClient")

let baselineGreen = parse bytes = Ok baseline && verify baseline.Seal baseline = Ok baseline
let generated: GitHubEventSecurityControlResult list =
    requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeGenerated control; BaselineGreen = baselineGreen })
let independent: GitHubEventSecurityControlResult list =
    requiredControls |> List.map (fun control -> { Control = control; ControlPassed = executeIndependent control; BaselineGreen = baselineGreen })
let retained relative =
    use document = readJson relative
    strings "controls" document.RootElement, strings "cases" document.RootElement, text "caseContract" document.RootElement
let expectedIds = requiredControls |> List.map controlId
let generatedIds, generatedCases, generatedContract = retained "evidence/github-substrate-v2/gs2-07-4/generated-controls.json"
let independentIds, independentCases, independentContract = retained "evidence/github-substrate-v2/gs2-07-4/independent-controls.json"
if generatedIds <> expectedIds || generatedCases.Length <> expectedIds.Length || generatedCases |> List.exists String.IsNullOrWhiteSpace then failwith "generated retained inventory differs"
if independentIds <> expectedIds || independentCases.Length <> expectedIds.Length || independentCases |> List.exists String.IsNullOrWhiteSpace then failwith "independent retained inventory differs"
if generatedCases = independentCases || generatedContract = independentContract then failwith "control authorship is not independent"
match validateControls generated independent with
| Ok () -> ()
| Error errors -> failwithf "Q3 controls failed: %A; generated=%A; independent=%A" errors generated independent
printfn "GITHUB_EVENT_SECURITY_OK disposition=%s controls=%d seal=%s" baseline.Disposition expectedIds.Length baseline.Seal
