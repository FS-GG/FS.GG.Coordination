open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let policySchema = "fsgg.coordination.evidence-storage-policy/1"
let indexSchema = "fsgg.coordination.evidence-index/1"
let receiptSchema = "fsgg.coordination.unit-acceptance/1"
let shaPattern = Text.RegularExpressions.Regex("^[0-9a-f]{64}$")

let fail code detail = failwith $"{code}: {detail}"

let sha256 (bytes: byte array) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let property (name: string) (value: JsonElement) =
    let mutable child = Unchecked.defaultof<JsonElement>
    if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &child) then child
    else fail "ES-JSON-PROPERTY" $"missing {name}"

let stringProperty name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.String then fail "ES-JSON-TYPE" $"{name} must be a string"
    child.GetString()

let int64Property name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.Number then fail "ES-JSON-TYPE" $"{name} must be an integer"
    match child.TryGetInt64() with
    | true, number -> number
    | _ -> fail "ES-JSON-TYPE" $"{name} must be an integer"

let arrayProperty name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.Array then fail "ES-JSON-TYPE" $"{name} must be an array"
    child.EnumerateArray() |> Seq.toList

let exactProperties path expected (value: JsonElement) =
    let observed = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
    if observed <> expected then
        let expectedText = String.concat "," expected
        let observedText = String.concat "," observed
        fail "ES-JSON-SHAPE" $"{path} properties/order expected {expectedText}; observed {observedText}"

let readJson path =
    try JsonDocument.Parse(File.ReadAllBytes path)
    with :? JsonException as error -> fail "ES-JSON-PARSE" $"{path}: {error.Message}"

let isCanonicalJson path =
    let bytes = File.ReadAllBytes path
    let node = JsonNode.Parse bytes
    let rendered = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n")
    bytes = rendered

let safeRelativePath (value: string) =
    not (String.IsNullOrWhiteSpace value)
    && not (Path.IsPathRooted value)
    && not (value.Contains('\\'))
    && value.Split('/') |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> "..")

let ensureNoSymlink (root: string) (relative: string) =
    let mutable current = root
    for segment in relative.Split('/') do
        current <- Path.Combine(current, segment)
        if File.Exists current || Directory.Exists current then
            let attributes = File.GetAttributes current
            if attributes.HasFlag FileAttributes.ReparsePoint then fail "ES-PATH-SYMLINK" relative

let validateSha (path: string) (value: string) =
    if isNull value || not (shaPattern.IsMatch value) then fail "ES-SHA256" $"{path} must be lowercase SHA-256"

let nonEmpty path value =
    if String.IsNullOrWhiteSpace value then fail "ES-RECORD-VALUE" $"{path} must not be empty"

let validateTime path (value: string) =
    let mutable parsed = DateTimeOffset.MinValue
    if not (DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind, &parsed)) then
        fail "ES-RECORD-TIME" path

let validateRecord categoryName relative (record: JsonElement) =
    let require schema properties =
        exactProperties relative properties record
        if stringProperty "schema" record <> schema then fail "ES-RECORD-SCHEMA" relative
        nonEmpty relative (stringProperty "id" record)
        validateSha relative (stringProperty "sha256" record)
    match categoryName with
    | "corpus-inputs" ->
        require "fsgg.coordination.corpus-input/1" [ "schema"; "id"; "input"; "sha256" ]
        property "input" record |> ignore
    | "external-observations" ->
        require "fsgg.coordination.external-observation/1" [ "schema"; "id"; "source"; "observedAt"; "sha256" ]
        nonEmpty relative (stringProperty "source" record)
        validateTime relative (stringProperty "observedAt" record)
    | "independent-oracles" ->
        require "fsgg.coordination.independent-oracle/1" [ "schema"; "id"; "oracle"; "expected"; "sha256" ]
        nonEmpty relative (stringProperty "oracle" record)
        property "expected" record |> ignore
    | "generated-cases" ->
        require "fsgg.coordination.generated-case/1" [ "schema"; "id"; "generator"; "seed"; "sha256" ]
        nonEmpty relative (stringProperty "generator" record)
        nonEmpty relative (stringProperty "seed" record)
    | "test-results" ->
        require "fsgg.coordination.test-result/1" [ "schema"; "id"; "candidate"; "outcome"; "sha256" ]
        let candidate = stringProperty "candidate" record
        if candidate.Length <> 40 || candidate |> Seq.exists (Uri.IsHexDigit >> not) then fail "ES-RECORD-CANDIDATE" relative
        let outcome = stringProperty "outcome" record
        if outcome <> "passed" && outcome <> "failed" then fail "ES-RECORD-OUTCOME" relative
    | "reviews" ->
        exactProperties relative [ "schema"; "id"; "candidate"; "reviewer"; "decision"; "evidenceSha256" ] record
        if stringProperty "schema" record <> "fsgg.coordination.review/1" then fail "ES-RECORD-SCHEMA" relative
        nonEmpty relative (stringProperty "id" record)
        nonEmpty relative (stringProperty "reviewer" record)
        let candidate = stringProperty "candidate" record
        if candidate.Length <> 40 || candidate |> Seq.exists (Uri.IsHexDigit >> not) then fail "ES-RECORD-CANDIDATE" relative
        let decision = stringProperty "decision" record
        if decision <> "accepted" && decision <> "changes-requested" then fail "ES-RECORD-DECISION" relative
        validateSha relative (stringProperty "evidenceSha256" record)
    | _ -> ()

type Category = { Name: string; Path: string; Schema: string }

let validate evidenceRoot =
    let root = Path.GetFullPath evidenceRoot
    if not (Directory.Exists root) then fail "ES-ROOT-MISSING" root
    let policyPath = Path.Combine(root, "storage-policy.json")
    let indexPath = Path.Combine(root, "index.json")
    for path in [ policyPath; indexPath ] do
        if not (File.Exists path) then fail "ES-CONTRACT-MISSING" path
        ensureNoSymlink root (Path.GetRelativePath(root, path).Replace('\\', '/'))
        if not (isCanonicalJson path) then fail "ES-JSON-CANONICAL" path

    use policyDocument = readJson policyPath
    let policy = policyDocument.RootElement
    exactProperties "policy" [ "schema"; "version"; "trackedMaxBytes"; "externalStores"; "categories" ] policy
    if stringProperty "schema" policy <> policySchema then fail "ES-POLICY-VERSION" "unsupported storage policy"
    if int64Property "version" policy <> 1L then fail "ES-POLICY-VERSION" "version must be 1"
    let maxBytes = int64Property "trackedMaxBytes" policy
    if maxBytes <> 65536L then fail "ES-POLICY-LIMIT" "trackedMaxBytes must be 65536"
    let stores = arrayProperty "externalStores" policy |> List.map _.GetString()
    if stores <> [ "github-actions-artifact"; "github-release-asset" ] then fail "ES-POLICY-STORES" "external stores differ"
    let categories =
        arrayProperty "categories" policy
        |> List.map (fun item ->
            exactProperties "category" [ "name"; "path"; "schema" ] item
            { Name = stringProperty "name" item; Path = stringProperty "path" item; Schema = stringProperty "schema" item })
    let expectedNames =
        [ "corpus-inputs"; "external-observations"; "independent-oracles"; "generated-cases"
          "test-results"; "artifact-manifests"; "reviews"; "accepted-receipts" ]
    if categories |> List.map _.Name <> expectedNames then fail "ES-CATEGORY-INVENTORY" "category names or order differ"
    if categories |> List.map _.Path |> List.distinct |> List.length <> categories.Length then fail "ES-CATEGORY-DUPLICATE" "duplicate category path"

    for category in categories do
        if not (safeRelativePath category.Path) || not (safeRelativePath category.Schema) then fail "ES-PATH-UNSAFE" category.Name
        let directory = Path.Combine(root, category.Path)
        let schemaPath = Path.Combine(root, category.Schema)
        if not (Directory.Exists directory) then fail "ES-CATEGORY-MISSING" category.Path
        if not (File.Exists schemaPath) then fail "ES-SCHEMA-MISSING" category.Schema
        ensureNoSymlink root category.Path
        ensureNoSymlink root category.Schema
        use schemaDocument = readJson schemaPath
        let schema = schemaDocument.RootElement
        if stringProperty "category" schema <> category.Name then fail "ES-SCHEMA-CATEGORY" category.Name
        if stringProperty "$id" schema <> $"https://fs-gg.github.io/schemas/evidence/{category.Name}/v1" then fail "ES-SCHEMA-ID" category.Name

    use indexDocument = readJson indexPath
    let index = indexDocument.RootElement
    exactProperties "index" [ "schema"; "version"; "entries" ] index
    if stringProperty "schema" index <> indexSchema then fail "ES-INDEX-VERSION" "unsupported evidence index"
    if int64Property "version" index <> 1L then fail "ES-INDEX-VERSION" "version must be 1"
    let entries = arrayProperty "entries" index
    let ids = HashSet<string>(StringComparer.Ordinal)
    let paths = HashSet<string>(StringComparer.Ordinal)
    let mutable previousId = ""
    for entry in entries do
        exactProperties "entry" [ "id"; "category"; "storage"; "path"; "mediaType"; "bytes"; "sha256" ] entry
        let id = stringProperty "id" entry
        let categoryName = stringProperty "category" entry
        let storage = stringProperty "storage" entry
        let relative = stringProperty "path" entry
        let mediaType = stringProperty "mediaType" entry
        let length = int64Property "bytes" entry
        let digest = stringProperty "sha256" entry
        if String.CompareOrdinal(previousId, id) >= 0 then fail "ES-INDEX-ORDER" id
        previousId <- id
        if not (ids.Add id) then fail "ES-ID-DUPLICATE" id
        if not (paths.Add relative) then fail "ES-PATH-DUPLICATE" relative
        if storage <> "git" then fail "ES-STORAGE-KIND" $"tracked index entry {id} must use git"
        if mediaType <> "application/json" then fail "ES-MEDIA-TYPE" id
        if not (safeRelativePath relative) then fail "ES-PATH-UNSAFE" relative
        let category = categories |> List.tryFind (fun item -> item.Name = categoryName) |> Option.defaultWith (fun () -> fail "ES-CATEGORY-UNKNOWN" categoryName)
        if not (relative.StartsWith(category.Path + "/", StringComparison.Ordinal)) then fail "ES-CATEGORY-PATH" relative
        ensureNoSymlink root relative
        let path = Path.Combine(root, relative)
        if not (File.Exists path) then fail "ES-PAYLOAD-MISSING" relative
        let bytes = File.ReadAllBytes path
        if int64 bytes.Length <> length then fail "ES-LENGTH-STALE" relative
        if length > maxBytes then fail "ES-PAYLOAD-OVERSIZE" relative
        validateSha relative digest
        if sha256 bytes <> digest then fail "ES-DIGEST-STALE" relative
        if categoryName = "accepted-receipts" then
            use receipt = readJson path
            exactProperties
                relative
                [ "schema"; "unitId"; "state"; "unitContractSha256"; "sourceRevision"; "artifacts"; "acceptedAt"; "digest" ]
                receipt.RootElement
            if stringProperty "schema" receipt.RootElement <> receiptSchema then fail "ES-RECEIPT-SCHEMA" relative
            if stringProperty "state" receipt.RootElement <> "accepted" then fail "ES-RECEIPT-STATE" relative
            validateSha relative (stringProperty "unitContractSha256" receipt.RootElement)
            let sourceRevision = stringProperty "sourceRevision" receipt.RootElement
            if sourceRevision.Length <> 40 || sourceRevision |> Seq.exists (Uri.IsHexDigit >> not) then fail "ES-RECEIPT-REVISION" relative
            validateTime relative (stringProperty "acceptedAt" receipt.RootElement)
            validateSha relative (stringProperty "digest" receipt.RootElement)

        if categoryName = "artifact-manifests" then
            use manifestDocument = readJson path
            let manifest = manifestDocument.RootElement
            exactProperties
                "artifact-manifest"
                [ "schema"; "id"; "store"; "repositoryId"; "producerId"; "artifactId"; "artifactName"; "bytes"; "mediaType"; "sha256" ]
                manifest
            if stringProperty "schema" manifest <> "fsgg.coordination.artifact-manifest/1" then fail "ES-MANIFEST-SCHEMA" relative
            let store = stringProperty "store" manifest
            if store <> "github-actions-artifact" && store <> "github-release-asset" then
                fail "ES-MANIFEST-STORE" relative
            for name in [ "repositoryId"; "producerId"; "artifactId" ] do
                if int64Property name manifest < 1L then fail "ES-MANIFEST-ID" $"{relative}/{name}"
            if String.IsNullOrWhiteSpace(stringProperty "artifactName" manifest) then fail "ES-MANIFEST-LOCATOR" relative
            if int64Property "bytes" manifest < 0L then fail "ES-MANIFEST-LENGTH" relative
            nonEmpty relative (stringProperty "mediaType" manifest)
            validateSha relative (stringProperty "sha256" manifest)

        if categoryName <> "accepted-receipts" && categoryName <> "artifact-manifests" then
            use recordDocument = readJson path
            validateRecord categoryName relative recordDocument.RootElement

    let indexed = paths |> Seq.toList |> Set.ofList
    let discovered =
        categories
        |> List.collect (fun category ->
            Directory.EnumerateFiles(Path.Combine(root, category.Path), "*.json", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
            |> Seq.toList)
        |> Set.ofList
    if discovered <> indexed then
        let missing = Set.difference discovered indexed |> String.concat ","
        let stale = Set.difference indexed discovered |> String.concat ","
        fail "ES-INDEX-COVERAGE" $"unindexed={missing}; missing={stale}"

    $"EVIDENCE_STORAGE_OK categories={categories.Length} entries={entries.Length} maxTrackedBytes={maxBytes}"

let copyDirectory source destination =
    Directory.CreateDirectory destination |> ignore
    for directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories) do
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory))) |> ignore
    for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
        let target = Path.Combine(destination, Path.GetRelativePath(source, file))
        File.Copy(file, target)

let mutateJson path mutation =
    let node = JsonNode.Parse(File.ReadAllText path)
    mutation node
    File.WriteAllText(path, node.ToJsonString() + "\n", UTF8Encoding(false))

let addTrackedJson (root: string) (id: string) (category: string) (path: string) (content: string) =
    let payload = Path.Combine(root, path)
    Directory.CreateDirectory(Path.GetDirectoryName payload) |> ignore
    File.WriteAllText(payload, content + "\n", UTF8Encoding(false))
    let bytes = File.ReadAllBytes payload
    mutateJson (Path.Combine(root, "index.json")) (fun node ->
        let entry = JsonObject()
        entry.Add("id", id)
        entry.Add("category", category)
        entry.Add("storage", "git")
        entry.Add("path", path)
        entry.Add("mediaType", "application/json")
        entry.Add("bytes", bytes.Length)
        entry.Add("sha256", sha256 bytes)
        node["entries"].AsArray().Add entry)

let selfTest evidenceRoot =
    let validManifest =
        "{\"schema\":\"fsgg.coordination.artifact-manifest/1\",\"id\":\"artifact-1\",\"store\":\"github-actions-artifact\",\"repositoryId\":131313,\"producerId\":33038126581,\"artifactId\":98405834712,\"artifactName\":\"qualification-evidence\",\"bytes\":70000,\"mediaType\":\"application/zip\",\"sha256\":\"" + String('a', 64) + "\"}"
    let positiveRoot = Path.Combine(Path.GetTempPath(), $"fsgg-evidence-{Guid.NewGuid():N}")
    try
        copyDirectory evidenceRoot positiveRoot
        addTrackedJson positiveRoot "manifest-valid" "artifact-manifests" "artifact-manifests/manifest-valid.json" validManifest
        validate positiveRoot |> ignore
    finally
        if Directory.Exists positiveRoot then Directory.Delete(positiveRoot, true)

    let cases: (string * (string -> unit) * string) list =
        [ "unsupported-version", (fun root -> mutateJson (Path.Combine(root, "index.json")) (fun node -> node["version"] <- 2)), "ES-INDEX-VERSION"
          "stale-digest", (fun root -> mutateJson (Path.Combine(root, "index.json")) (fun node ->
              let entry = node["entries"].AsArray()[0]
              entry["sha256"] <- String('0', 64))), "ES-DIGEST-STALE"
          "unsafe-path", (fun root -> mutateJson (Path.Combine(root, "index.json")) (fun node ->
              let entry = node["entries"].AsArray()[0]
              entry["path"] <- "../escape.json")), "ES-PATH-UNSAFE"
          "duplicate-id", (fun root ->
              mutateJson (Path.Combine(root, "index.json")) (fun node ->
                  let entries = node["entries"].AsArray()
                  let first = entries[0]
                  let duplicate = first["id"].GetValue<string>()
                  entries[1]["id"] <- duplicate)), "ES-INDEX-ORDER"
          "duplicate-path", (fun root -> mutateJson (Path.Combine(root, "index.json")) (fun node ->
              let entries = node["entries"].AsArray()
              let first = entries[0]
              let duplicate = first["path"].GetValue<string>()
              entries[1]["path"] <- duplicate)), "ES-PATH-DUPLICATE"
          "wrong-category", (fun root -> mutateJson (Path.Combine(root, "index.json")) (fun node ->
              let entry = node["entries"].AsArray()[0]
              entry["category"] <- "reviews")), "ES-CATEGORY-PATH"
          "noncanonical", (fun root -> File.WriteAllText(Path.Combine(root, "index.json"), File.ReadAllText(Path.Combine(root, "index.json")).Replace("{\"schema\"", "{ \"schema\""))), "ES-JSON-CANONICAL"
          "missing-schema", (fun root -> File.Delete(Path.Combine(root, "schemas/v1/reviews.schema.json"))), "ES-SCHEMA-MISSING"
          "missing-category", (fun root -> Directory.Delete(Path.Combine(root, "reviews"), true)), "ES-CATEGORY-MISSING"
          "malformed-category-record", (fun root ->
              addTrackedJson root "review-malformed" "reviews" "reviews/malformed.json" "{}"), "ES-JSON-SHAPE"
          "mutable-artifact-locator", (fun root ->
              let mutableManifest = validManifest.Replace("\"producerId\":33038126581", "\"producerId\":\"latest\"")
              addTrackedJson root "manifest-mutable" "artifact-manifests" "artifact-manifests/mutable.json" mutableManifest), "ES-JSON-TYPE"
          "stale-length", (fun root -> mutateJson (Path.Combine(root, "index.json")) (fun node ->
              let entry = node["entries"].AsArray()[0]
              entry["bytes"] <- 1)), "ES-LENGTH-STALE"
          "symlink", (fun root ->
              let payload = Path.Combine(root, "accepted/GS2-01.2.json")
              let target = Path.Combine(root, "accepted/GS2-01.3.json")
              File.Delete payload
              File.CreateSymbolicLink(payload, target) |> ignore), "ES-PATH-SYMLINK"
          "oversized", (fun root ->
              let payload = Path.Combine(root, "accepted/GS2-01.2.json")
              File.AppendAllText(payload, String('x', 65537))
              mutateJson (Path.Combine(root, "index.json")) (fun node ->
                  let bytes = File.ReadAllBytes payload
                  let entry = node["entries"].AsArray()[0]
                  entry["bytes"] <- bytes.Length
                  entry["sha256"] <- sha256 bytes)), "ES-PAYLOAD-OVERSIZE" ]
    for name, mutate, expected in cases do
        let temp = Path.Combine(Path.GetTempPath(), $"fsgg-evidence-{Guid.NewGuid():N}")
        try
            copyDirectory evidenceRoot temp
            mutate temp
            try
                validate temp |> ignore
                fail "ES-SELF-TEST" $"{name} unexpectedly passed"
            with error when error.Message.StartsWith(expected + ":", StringComparison.Ordinal) -> ()
        finally
            if Directory.Exists temp then Directory.Delete(temp, true)
    $"EVIDENCE_STORAGE_SELF_TEST_OK negativeCases={cases.Length} positiveArtifactManifests=1"

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
try
    match arguments with
    | [ root ] -> printfn "%s" (validate root)
    | [ "--self-test"; root ] ->
        printfn "%s" (validate root)
        printfn "%s" (selfTest root)
    | _ -> fail "ES-USAGE" "validate-evidence-storage.fsx [--self-test] <evidence-root>"
with error ->
    eprintfn "%s" error.Message
    exit 2
