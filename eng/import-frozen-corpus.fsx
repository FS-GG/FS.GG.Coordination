open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let q0Revision = "15aba28c76551d31b00bac9ff990703f9e61f57d"
let sourceCommit = "95de1c77674b9dd8d7a9ce568d1ee175a7797e5e"
let q0ManifestRelative = "work/2953-gh-modernization-m0-invariants/q0-corpus-originals.json"
let q0EvidenceRelative = "work/2953-gh-modernization-m0-invariants/q0-evidence.json"
let q0ManifestSha256 = "5c94fa3ee60e02b7fbee80918b45e5e2046a152a2342f6b88044ac169c1dc67b"
let q0EvidenceSha256 = "3a0a73d81823c1667f61f9493c1611aa89b85e24d3e1580cd922d309e2f12f87"
let explicitIndeterminateRationale = "Q0 deliberately classifies this expected decision as Indeterminate; the import preserves that ambiguity without selecting a fallback outcome."
let noneRecordedRationale = "Q0 records no case-level ambiguity for this artifact; the import preserves that absence without inferring additional certainty."
let unobservedDetail = "Q0 froze these multi-case source bytes and their expected behavior but did not bind an atomic runtime result to this individual artifact; no green result is inferred."

let fail code detail = failwith $"{code}: {detail}"

let sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let gitBlobSha1 (bytes: byte array) =
    let header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\000")
    let input = Array.append header bytes
    SHA1.HashData input |> Convert.ToHexString |> _.ToLowerInvariant()

let canonicalBytes (node: JsonNode) =
    Encoding.UTF8.GetBytes(node.ToJsonString() + "\n")

let stringProperty (name: string) (value: JsonElement) = value.GetProperty(name).GetString()
let intProperty (name: string) (value: JsonElement) = value.GetProperty(name).GetInt32()

let runGitBytes sourceRoot arguments =
    let startInfo = ProcessStartInfo("git")
    startInfo.ArgumentList.Add "-C"
    startInfo.ArgumentList.Add sourceRoot
    for argument in arguments do startInfo.ArgumentList.Add argument
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    use output = new MemoryStream()
    child.StandardOutput.BaseStream.CopyTo output
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then fail "FC-GIT" error
    output.ToArray()

let runGitText sourceRoot arguments =
    runGitBytes sourceRoot arguments |> Encoding.UTF8.GetString |> _.Trim()

let addString (target: JsonObject) (name: string) (value: string) = target.Add(name, JsonValue.Create value)
let addInt (target: JsonObject) (name: string) (value: int) = target.Add(name, JsonValue.Create value)

let parseArguments () =
    let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
    let rec loop sourceRoot evidenceRoot remaining =
        match remaining with
        | [] -> sourceRoot, evidenceRoot
        | "--source-root" :: value :: tail -> loop (Some value) evidenceRoot tail
        | "--evidence-root" :: value :: tail -> loop sourceRoot (Some value) tail
        | value :: _ -> fail "FC-USAGE" $"unknown argument {value}"
    match loop None None arguments with
    | Some sourceRoot, Some evidenceRoot -> Path.GetFullPath sourceRoot, Path.GetFullPath evidenceRoot
    | _ -> fail "FC-USAGE" "import-frozen-corpus.fsx --source-root DIR --evidence-root DIR"

let sourceRoot, evidenceRoot = parseArguments ()
let sourceHead = runGitText sourceRoot [ "rev-parse"; "HEAD" ]
if sourceHead <> q0Revision then fail "FC-Q0-REVISION" $"expected {q0Revision}; observed {sourceHead}"

let q0ManifestPath = Path.Combine(sourceRoot, q0ManifestRelative)
let q0EvidencePath = Path.Combine(sourceRoot, q0EvidenceRelative)
let q0ManifestBytes = File.ReadAllBytes q0ManifestPath
let q0EvidenceBytes = File.ReadAllBytes q0EvidencePath
if sha256 q0ManifestBytes <> q0ManifestSha256 then fail "FC-Q0-MANIFEST" q0ManifestRelative
if sha256 q0EvidenceBytes <> q0EvidenceSha256 then fail "FC-Q0-EVIDENCE" q0EvidenceRelative

let manifestDocument = JsonDocument.Parse q0ManifestBytes
let evidenceDocument = JsonDocument.Parse q0EvidenceBytes
let manifestRoot = manifestDocument.RootElement
let evidenceRootNode = evidenceDocument.RootElement
if stringProperty "sourceCommit" manifestRoot <> sourceCommit then fail "FC-SOURCE-COMMIT" "Q0 source commit differs"

let sourceEntries = manifestRoot.GetProperty("entries").EnumerateArray() |> Seq.toArray
let evidenceEntries = evidenceRootNode.GetProperty("corpus").EnumerateArray() |> Seq.toArray
if sourceEntries.Length <> 21 || evidenceEntries.Length <> 21 then fail "FC-COUNT" "Q0 corpus must contain exactly 21 rows"

let evidenceById =
    evidenceEntries
    |> Seq.map (fun entry -> stringProperty "id" entry, entry.Clone())
    |> Map.ofSeq

let corpusDirectory = Path.Combine(evidenceRoot, "corpus")
let originalsDirectory = Path.Combine(corpusDirectory, "originals")
let provenanceDirectory = Path.Combine(corpusDirectory, "provenance")
Directory.CreateDirectory originalsDirectory |> ignore
Directory.CreateDirectory provenanceDirectory |> ignore
File.WriteAllBytes(Path.Combine(provenanceDirectory, "q0-corpus-originals.source"), q0ManifestBytes)
File.WriteAllBytes(Path.Combine(provenanceDirectory, "q0-evidence.source"), q0EvidenceBytes)

let metadataEntries = ResizeArray<JsonObject>()

for ordinal, sourceEntry in sourceEntries |> Array.indexed do
    let id = stringProperty "id" sourceEntry
    let evidenceEntry =
        evidenceById
        |> Map.tryFind id
        |> Option.defaultWith (fun () -> fail "FC-EVIDENCE-MISSING" id)
    if stringProperty "sourceRef" sourceEntry <> stringProperty "source" evidenceEntry then fail "FC-SOURCE-REF" id
    if stringProperty "sha256" sourceEntry <> stringProperty "originalBytesSha256" evidenceEntry then fail "FC-SOURCE-SHA" id

    let sourcePath = stringProperty "path" sourceEntry
    let expectedBlob = stringProperty "gitBlobSha1" sourceEntry
    let resolvedBlob = runGitText sourceRoot [ "rev-parse"; $"{sourceCommit}:{sourcePath}" ]
    if resolvedBlob <> expectedBlob then fail "FC-SOURCE-BLOB" id
    let payload = runGitBytes sourceRoot [ "cat-file"; "blob"; expectedBlob ]
    if payload.Length <> intProperty "byteLength" sourceEntry then fail "FC-SOURCE-LENGTH" id
    if sha256 payload <> stringProperty "sha256" sourceEntry then fail "FC-SOURCE-SHA" id
    if gitBlobSha1 payload <> expectedBlob then fail "FC-SOURCE-BLOB" id

    let payloadRelative = $"corpus/originals/{id}.source"
    File.WriteAllBytes(Path.Combine(evidenceRoot, payloadRelative), payload)

    let source = JsonObject()
    addString source "repository" "FS-GG/.github"
    addString source "commit" sourceCommit
    addString source "path" sourcePath
    addString source "ref" (stringProperty "sourceRef" sourceEntry)
    addString source "mediaType" (stringProperty "mediaType" sourceEntry)
    addInt source "bytes" payload.Length
    addString source "sha256" (sha256 payload)
    addString source "gitBlobSha1" expectedBlob
    addString source "payloadPath" payloadRelative

    let expectedBehavior = JsonNode.Parse(evidenceEntry.GetProperty("expected").GetRawText())
    let expectedDecision = evidenceEntry.GetProperty("expected").GetProperty("decisionClass").GetString()
    let ambiguity = JsonObject()
    if expectedDecision = "Indeterminate" then
        addString ambiguity "state" "explicit-indeterminate"
        addString ambiguity "rationale" explicitIndeterminateRationale
    else
        addString ambiguity "state" "none-recorded"
        addString ambiguity "rationale" noneRecordedRationale

    let currentResult = JsonObject()
    let observed =
        match id with
        | "C-pagination" ->
            Some("https://github.com/FS-GG/.github/actions/runs/32908004312", "2026-08-25T22:50:19Z", "The exact-source-head recipe-pagination workflow directly executed the frozen pagination artifact and completed successfully.")
        | "C-stale" ->
            Some("https://github.com/FS-GG/.github/actions/runs/32908004500", "2026-08-25T22:50:35Z", "The exact-source-head engine-freshness workflow directly executed the frozen stale-read artifact and completed successfully.")
        | _ -> None
    match observed with
    | Some(evidence, observedAt, detail) ->
        addString currentResult "state" "observed"
        addString currentResult "outcome" "passed"
        addString currentResult "evidence" evidence
        addString currentResult "headSha" sourceCommit
        addString currentResult "observedAt" observedAt
        addString currentResult "detail" detail
    | None ->
        addString currentResult "state" "not-atomically-observed"
        currentResult.Add("outcome", null)
        addString currentResult "evidence" $"git:{q0Revision}:{q0EvidenceRelative}#corpus/{id}"
        addString currentResult "headSha" sourceCommit
        currentResult.Add("observedAt", null)
        addString currentResult "detail" unobservedDetail

    let provenance = JsonObject()
    addString provenance "q0Revision" q0Revision
    addString provenance "q0ManifestPath" q0ManifestRelative
    addString provenance "q0ManifestSha256" q0ManifestSha256
    addString provenance "q0EvidencePath" q0EvidenceRelative
    addString provenance "q0EvidenceSha256" q0EvidenceSha256
    addString provenance "importedByUnit" "GS2-03.2"

    let input = JsonObject()
    addString input "schema" "fsgg.coordination.frozen-corpus-case/1"
    addInt input "ordinal" (ordinal + 1)
    addString input "kind" (stringProperty "kind" evidenceEntry)
    input.Add("source", source)
    addString input "historicalContext" (stringProperty "historicalContext" evidenceEntry)
    input.Add("expectedBehavior", expectedBehavior)
    input.Add("ambiguity", ambiguity)
    input.Add("currentV1Result", currentResult)
    input.Add("provenance", provenance)

    let record = JsonObject()
    addString record "schema" "fsgg.coordination.corpus-input/1"
    addString record "id" id
    record.Add("input", input)
    addString record "sha256" (sha256 payload)
    let metadataBytes = canonicalBytes record
    let metadataRelative = $"corpus/{id}.json"
    File.WriteAllBytes(Path.Combine(evidenceRoot, metadataRelative), metadataBytes)

    let indexEntry = JsonObject()
    addString indexEntry "id" $"corpus-{id}"
    addString indexEntry "category" "corpus-inputs"
    addString indexEntry "storage" "git"
    addString indexEntry "path" metadataRelative
    addString indexEntry "mediaType" "application/json"
    addInt indexEntry "bytes" metadataBytes.Length
    addString indexEntry "sha256" (sha256 metadataBytes)
    metadataEntries.Add indexEntry

let indexPath = Path.Combine(evidenceRoot, "index.json")
let index = JsonNode.Parse(File.ReadAllBytes indexPath).AsObject()
let entries = index["entries"].AsArray()
let retained =
    entries
    |> Seq.filter (fun entry -> entry["category"].GetValue<string>() <> "corpus-inputs")
    |> Seq.map _.DeepClone()
    |> Seq.toList
let ordered =
    Seq.append retained (metadataEntries |> Seq.map _.DeepClone())
    |> Seq.sortBy (fun entry -> entry["id"].GetValue<string>())
    |> Seq.toArray
entries.Clear()
for entry in ordered do entries.Add entry
File.WriteAllBytes(indexPath, canonicalBytes index)

printfn "FROZEN_CORPUS_IMPORTED cases=%d observed=2 unobserved=19 q0=%s source=%s" sourceEntries.Length q0Revision sourceCommit
