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
let revisionPattern = Text.RegularExpressions.Regex("^[0-9a-f]{40}$")
let unitPattern = Text.RegularExpressions.Regex("^GS2-[0-9]{2}\.[0-9]+$")
let canonicalTimePattern = Text.RegularExpressions.Regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")
let blobPattern = Text.RegularExpressions.Regex("^[0-9a-f]{40}$")
let q0Revision = "15aba28c76551d31b00bac9ff990703f9e61f57d"
let q0SourceCommit = "95de1c77674b9dd8d7a9ce568d1ee175a7797e5e"
let q0ManifestRelative = "corpus/provenance/q0-corpus-originals.source"
let q0EvidenceRelative = "corpus/provenance/q0-evidence.source"
let q0ManifestSha256 = "5c94fa3ee60e02b7fbee80918b45e5e2046a152a2342f6b88044ac169c1dc67b"
let q0EvidenceSha256 = "3a0a73d81823c1667f61f9493c1611aa89b85e24d3e1580cd922d309e2f12f87"
let frozenAggregateSha256 = "bf38fc3d426e74237561798d9f3b9fa5dd1b94b487e69f1565cc9cc6ab58c753"
let frozenCorpusSchemaSha256 = "75a4925efdd72b6ff1d7a2b0a64030bea3704d31c5d7bf8dd7cb22aae74399dd"
let explicitIndeterminateRationale = "Q0 deliberately classifies this expected decision as Indeterminate; the import preserves that ambiguity without selecting a fallback outcome."
let noneRecordedRationale = "Q0 records no case-level ambiguity for this artifact; the import preserves that absence without inferring additional certainty."
let unobservedDetail = "Q0 froze these multi-case source bytes and their expected behavior but did not bind an atomic runtime result to this individual artifact; no green result is inferred."

let fail code detail = failwith $"{code}: {detail}"

let sha256 (bytes: byte array) =
    bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let gitBlobSha1 (bytes: byte array) =
    let header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\000")
    Array.append header bytes |> SHA1.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

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

let boolProperty name value =
    let child = property name value
    if child.ValueKind <> JsonValueKind.True && child.ValueKind <> JsonValueKind.False then fail "ES-JSON-TYPE" $"{name} must be a boolean"
    child.GetBoolean()

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
    let styles = Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal
    if isNull value
       || not (canonicalTimePattern.IsMatch value)
       || not (DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", Globalization.CultureInfo.InvariantCulture, styles, &parsed)) then
        fail "ES-RECORD-TIME" path

let validateRevision path (value: string) =
    if isNull value || not (revisionPattern.IsMatch value) then fail "ES-RECORD-CANDIDATE" path

let tryProperty (name: string) (value: JsonElement) =
    let mutable child = Unchecked.defaultof<JsonElement>
    if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &child) then Some child else None

let rec validateSchemaVocabulary (path: string) (schema: JsonElement) =
    match schema.ValueKind with
    | JsonValueKind.True | JsonValueKind.False -> ()
    | JsonValueKind.Object ->
        let supported =
            set [ "$schema"; "$id"; "$defs"; "$ref"; "category"; "type"; "const"; "enum"; "minLength"; "pattern"; "format"
                  "minimum"; "maximum"; "minItems"; "maxItems"; "uniqueItems"; "items"; "required"; "properties"; "additionalProperties" ]
        for keyword in schema.EnumerateObject() do
            if not (supported.Contains keyword.Name) then fail "ES-SCHEMA-KEYWORD" $"{path}: unsupported keyword {keyword.Name}"
        match tryProperty "properties" schema with
        | Some properties ->
            for declared in properties.EnumerateObject() do validateSchemaVocabulary $"{path}.properties.{declared.Name}" declared.Value
        | None -> ()
        match tryProperty "$defs" schema with
        | Some definitions ->
            for declared in definitions.EnumerateObject() do validateSchemaVocabulary $"{path}.$defs.{declared.Name}" declared.Value
        | None -> ()
        match tryProperty "items" schema with
        | Some items -> validateSchemaVocabulary $"{path}.items" items
        | None -> ()
        match tryProperty "additionalProperties" schema with
        | Some additional -> validateSchemaVocabulary $"{path}.additionalProperties" additional
        | None -> ()
    | _ -> fail "ES-SCHEMA-KEYWORD" $"{path}: schema must be an object or boolean"

let rec validateJsonSchema (rootSchema: JsonElement) (path: string) (schema: JsonElement) (instance: JsonElement) =
    let schemaFail detail = fail "ES-SCHEMA-VALIDATION" $"{path}: {detail}"
    if schema.ValueKind = JsonValueKind.False then schemaFail "boolean schema is false"
    elif schema.ValueKind = JsonValueKind.True then ()
    elif schema.ValueKind <> JsonValueKind.Object then schemaFail "schema must be an object or boolean"
    match tryProperty "$ref" schema with
    | Some reference ->
        let value = reference.GetString()
        let prefix = "#/$defs/"
        if isNull value || not (value.StartsWith(prefix, StringComparison.Ordinal)) then schemaFail "unsupported reference"
        let name = value.Substring(prefix.Length)
        let resolved = property name (property "$defs" rootSchema)
        validateJsonSchema rootSchema path resolved instance
    | None -> ()

    match tryProperty "type" schema with
    | Some declared ->
        let matches value =
            match value, instance.ValueKind with
            | "object", JsonValueKind.Object
            | "array", JsonValueKind.Array
            | "string", JsonValueKind.String
            | "integer", JsonValueKind.Number
            | "number", JsonValueKind.Number
            | "boolean", JsonValueKind.True
            | "boolean", JsonValueKind.False
            | "null", JsonValueKind.Null -> true
            | _ -> false
        let declaredTypes =
            match declared.ValueKind with
            | JsonValueKind.String -> [ declared.GetString() ]
            | JsonValueKind.Array -> declared.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            | _ -> schemaFail "type must be a string or array"
        if not (declaredTypes |> List.exists matches) then schemaFail $"type is {instance.ValueKind}"
        if declaredTypes |> List.contains "integer" && not (declaredTypes |> List.contains "number") && instance.ValueKind = JsonValueKind.Number then
            let mutable integer = 0L
            if not (instance.TryGetInt64(&integer)) then schemaFail "number is not an integer"
    | None -> ()

    match tryProperty "const" schema with
    | Some expected when expected.GetRawText() <> instance.GetRawText() -> schemaFail "const differs"
    | _ -> ()
    match tryProperty "enum" schema with
    | Some values when values.EnumerateArray() |> Seq.exists (fun value -> value.GetRawText() = instance.GetRawText()) |> not -> schemaFail "value is outside enum"
    | _ -> ()

    if instance.ValueKind = JsonValueKind.String then
        let value = instance.GetString()
        match tryProperty "minLength" schema with
        | Some minimum when value.Length < minimum.GetInt32() -> schemaFail "string is too short"
        | _ -> ()
        match tryProperty "pattern" schema with
        | Some pattern when not (Text.RegularExpressions.Regex(pattern.GetString()).IsMatch value) -> schemaFail "string does not match pattern"
        | _ -> ()
        match tryProperty "format" schema with
        | Some format when format.GetString() = "date-time" ->
            try validateTime path value with _ -> schemaFail "invalid date-time"
        | Some format -> fail "ES-SCHEMA-KEYWORD" $"{path}: unsupported format {format.GetString()}"
        | _ -> ()

    if instance.ValueKind = JsonValueKind.Number then
        let value = instance.GetDecimal()
        match tryProperty "minimum" schema with
        | Some minimum when value < minimum.GetDecimal() -> schemaFail "number is below minimum"
        | _ -> ()
        match tryProperty "maximum" schema with
        | Some maximum when value > maximum.GetDecimal() -> schemaFail "number exceeds maximum"
        | _ -> ()

    if instance.ValueKind = JsonValueKind.Array then
        let items = instance.EnumerateArray() |> Seq.toList
        match tryProperty "minItems" schema with
        | Some minimum when items.Length < minimum.GetInt32() -> schemaFail "array is too short"
        | _ -> ()
        match tryProperty "maxItems" schema with
        | Some maximum when items.Length > maximum.GetInt32() -> schemaFail "array is too long"
        | _ -> ()
        match tryProperty "uniqueItems" schema with
        | Some unique when unique.GetBoolean() && (items |> List.map _.GetRawText() |> List.distinct |> List.length) <> items.Length -> schemaFail "array items are not unique"
        | _ -> ()
        match tryProperty "items" schema with
        | Some itemSchema -> items |> List.iteri (fun index item -> validateJsonSchema rootSchema $"{path}[{index}]" itemSchema item)
        | None -> ()

    if instance.ValueKind = JsonValueKind.Object then
        let observed = instance.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        match tryProperty "required" schema with
        | Some required ->
            for name in required.EnumerateArray() |> Seq.map _.GetString() do
                if not (observed.Contains name) then schemaFail $"missing required property {name}"
        | None -> ()
        match tryProperty "properties" schema with
        | Some properties ->
            let declared = properties.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
            for field in instance.EnumerateObject() do
                match tryProperty field.Name properties with
                | Some fieldSchema -> validateJsonSchema rootSchema $"{path}.{field.Name}" fieldSchema field.Value
                | None ->
                    match tryProperty "additionalProperties" schema with
                    | Some additional when additional.ValueKind = JsonValueKind.False -> schemaFail $"additional property {field.Name}"
                    | _ -> ()
            match tryProperty "additionalProperties" schema with
            | Some additional when additional.ValueKind <> JsonValueKind.True && additional.ValueKind <> JsonValueKind.False ->
                for field in instance.EnumerateObject() do
                    if not (declared.Contains field.Name) then validateJsonSchema rootSchema $"{path}.{field.Name}" additional field.Value
            | _ -> ()
        | None ->
            match tryProperty "additionalProperties" schema with
            | Some additional when additional.ValueKind = JsonValueKind.False && not observed.IsEmpty -> schemaFail "object properties are forbidden"
            | Some additional when additional.ValueKind <> JsonValueKind.True ->
                for field in instance.EnumerateObject() do validateJsonSchema rootSchema $"{path}.{field.Name}" additional field.Value
            | _ -> ()

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
        validateRevision relative (stringProperty "candidate" record)
        let outcome = stringProperty "outcome" record
        if outcome <> "passed" && outcome <> "failed" then fail "ES-RECORD-OUTCOME" relative
    | "reviews" ->
        exactProperties relative [ "accountableOwner"; "candidate"; "createdAt"; "digest"; "evidence"; "evidenceSetSha256"; "findings"; "rollup"; "schema" ] record
        if stringProperty "schema" record <> "fsgg.coordination.critique-evidence/1" then fail "ES-RECORD-SCHEMA" relative
        nonEmpty relative (stringProperty "accountableOwner" record)
        validateTime relative (stringProperty "createdAt" record)
        validateSha relative (stringProperty "digest" record)
        validateSha relative (stringProperty "evidenceSetSha256" record)
    | _ -> ()

type Category = { Name: string; Path: string; Schema: string }

let validateFrozenCorpusSchema (root: string) =
    use schemaDocument = readJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json"))
    let schema = schemaDocument.RootElement
    validateSchemaVocabulary "schema.corpus-inputs" schema
    if stringProperty "$schema" schema <> "https://json-schema.org/draft/2020-12/schema" then
        fail "ES-SCHEMA-DIALECT" "corpus-inputs must bind Draft 2020-12"
    let schemaProperties = property "properties" schema
    let inputSchema = property "input" schemaProperties
    let inputProperties = property "properties" inputSchema
    let expectedBehaviorSchema = property "expectedBehavior" inputProperties
    let expectedBehaviorProperties = property "properties" expectedBehaviorSchema
    let expectedBehaviorRequired = arrayProperty "required" expectedBehaviorSchema |> List.map _.GetString()
    if expectedBehaviorRequired <> [ "decisionClass"; "predicateId"; "authority"; "detail" ] then fail "FC-SCHEMA-CONTRACT" "expectedBehavior required fields differ"
    if boolProperty "additionalProperties" expectedBehaviorSchema then fail "FC-SCHEMA-CONTRACT" "expectedBehavior must be closed"
    exactProperties "schema.expectedBehavior.properties" [ "decisionClass"; "predicateId"; "authority"; "detail"; "metrics" ] expectedBehaviorProperties
    let metricsSchema = property "metrics" expectedBehaviorProperties
    let metricsRequired = arrayProperty "required" metricsSchema |> List.map _.GetString()
    let expectedMetrics = [ "windowHours"; "opened"; "closed"; "net"; "commits" ]
    if metricsRequired <> expectedMetrics || boolProperty "additionalProperties" metricsSchema then fail "FC-SCHEMA-CONTRACT" "metrics shape differs"
    let metricsProperties = property "properties" metricsSchema
    exactProperties "schema.expectedBehavior.metrics.properties" expectedMetrics metricsProperties
    for name in expectedMetrics do
        let metric = property name metricsProperties
        exactProperties $"schema.expectedBehavior.metrics.{name}" [ "type" ] metric
        if stringProperty "type" metric <> "integer" then fail "FC-SCHEMA-CONTRACT" $"metrics.{name} must be integer"
    let resultSchema = property "currentV1Result" inputProperties
    let resultProperties = property "properties" resultSchema
    let observedAtSchema = property "observedAt" resultProperties
    exactProperties "schema.currentV1Result.observedAt" [ "type"; "format" ] observedAtSchema
    let observedAtType = property "type" observedAtSchema
    if observedAtType.ValueKind <> JsonValueKind.Array then fail "FC-SCHEMA-CONTRACT" "observedAt type must be a union"
    let observedAtTypes = observedAtType.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    if observedAtTypes <> [ "string"; "null" ] || stringProperty "format" observedAtSchema <> "date-time" then
        fail "FC-SCHEMA-CONTRACT" "observedAt must admit canonical timestamps and explicit absence"

let validateFrozenCorpus (root: string) (entries: JsonElement list) =
    validateFrozenCorpusSchema root
    use corpusSchemaDocument = readJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json"))
    let corpusSchema = corpusSchemaDocument.RootElement
    let provenanceFiles = [ q0ManifestRelative, q0ManifestSha256; q0EvidenceRelative, q0EvidenceSha256 ]
    for relative, expectedDigest in provenanceFiles do
        if not (safeRelativePath relative) then fail "FC-PROVENANCE-PATH" relative
        ensureNoSymlink root relative
        let path = Path.Combine(root, relative)
        if not (File.Exists path) then fail "FC-PROVENANCE-MISSING" relative
        if sha256 (File.ReadAllBytes path) <> expectedDigest then fail "FC-PROVENANCE-DIGEST" relative

    let provenanceDirectory = Path.Combine(root, "corpus/provenance")
    let observedProvenance =
        Directory.EnumerateFiles(provenanceDirectory, "*", SearchOption.TopDirectoryOnly)
        |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
        |> Set.ofSeq
    let expectedProvenance = provenanceFiles |> List.map fst |> Set.ofList
    if observedProvenance <> expectedProvenance then fail "FC-PROVENANCE-INVENTORY" "provenance inventory differs"

    use manifestDocument = readJson (Path.Combine(root, q0ManifestRelative))
    use evidenceDocument = readJson (Path.Combine(root, q0EvidenceRelative))
    let manifest = manifestDocument.RootElement
    let evidence = evidenceDocument.RootElement
    exactProperties "q0-manifest" [ "schema"; "sourceCommit"; "entries" ] manifest
    if stringProperty "schema" manifest <> "fsgg.github-substrate.q0-corpus-originals/v2" then fail "FC-PROVENANCE-SCHEMA" "manifest"
    if stringProperty "sourceCommit" manifest <> q0SourceCommit then fail "FC-SOURCE-COMMIT" "manifest"
    if stringProperty "schema" evidence <> "fsgg.github-substrate.q0-evidence/v1" then fail "FC-PROVENANCE-SCHEMA" "evidence"
    if stringProperty "sourceBase" evidence <> q0SourceCommit then fail "FC-SOURCE-COMMIT" "evidence"
    let sources = arrayProperty "entries" manifest
    let expectations = arrayProperty "corpus" evidence
    if sources.Length <> 21 || expectations.Length <> 21 then fail "FC-COUNT" "Q0 provenance must contain exactly 21 cases"
    let expectedById =
        expectations
        |> List.map (fun item -> stringProperty "id" item, item)
        |> Map.ofList
    if expectedById.Count <> 21 then fail "FC-Q0-DUPLICATE" "duplicate Q0 evidence identity"

    let aggregateText =
        sources
        |> List.map (fun item ->
            String.concat "\t" [ stringProperty "id" item; stringProperty "sha256" item; stringProperty "gitBlobSha1" item; string (int64Property "byteLength" item) ])
        |> String.concat "\n"
        |> fun value -> value + "\n"
    if sha256 (Encoding.UTF8.GetBytes aggregateText) <> frozenAggregateSha256 then fail "FC-AGGREGATE" "Q0 ordered identity aggregate differs"

    let corpusIndexEntries =
        entries
        |> List.filter (fun entry -> stringProperty "category" entry = "corpus-inputs")
    if corpusIndexEntries.Length <> 21 then fail "FC-INDEX-COUNT" $"expected 21 corpus index rows; observed {corpusIndexEntries.Length}"

    let expectedIds = sources |> List.map (stringProperty "id")
    let indexedIds = corpusIndexEntries |> List.map (stringProperty "id")
    let expectedIndexedIds = expectedIds |> List.map (fun id -> "corpus-" + id) |> List.sort
    if indexedIds <> expectedIndexedIds then fail "FC-INDEX-INVENTORY" "corpus index identities differ"

    let originalsDirectory = Path.Combine(root, "corpus/originals")
    ensureNoSymlink root "corpus/originals"
    let observedOriginals =
        Directory.EnumerateFiles(originalsDirectory, "*", SearchOption.TopDirectoryOnly)
        |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
        |> Set.ofSeq
    let expectedOriginals = expectedIds |> List.map (fun id -> $"corpus/originals/{id}.source") |> Set.ofList
    if observedOriginals <> expectedOriginals then fail "FC-PAYLOAD-INVENTORY" "raw payload inventory differs"

    let metadataIds = HashSet<string>(StringComparer.Ordinal)
    let mutable observedCount = 0
    let mutable unobservedCount = 0
    for ordinal, source in sources |> List.indexed do
        exactProperties "q0-source" [ "id"; "mediaType"; "path"; "sourceRef"; "gitBlobSha1"; "sha256"; "byteLength" ] source
        let id = stringProperty "id" source
        if not (metadataIds.Add id) then fail "FC-Q0-DUPLICATE" id
        let expected = expectedById |> Map.tryFind id |> Option.defaultWith (fun () -> fail "FC-Q0-JOIN" id)
        exactProperties "q0-evidence-case" [ "id"; "kind"; "source"; "historicalContext"; "expected"; "artifact"; "originalBytesSha256" ] expected
        if stringProperty "sourceRef" source <> stringProperty "source" expected then fail "FC-Q0-SOURCE-REF" id
        if stringProperty "sha256" source <> stringProperty "originalBytesSha256" expected then fail "FC-Q0-SHA" id
        if stringProperty "artifact" expected <> $"q0-corpus-originals.json#{id}" then fail "FC-Q0-ARTIFACT" id

        let metadataRelative = $"corpus/{id}.json"
        let metadataPath = Path.Combine(root, metadataRelative)
        if not (isCanonicalJson metadataPath) then fail "ES-JSON-CANONICAL" metadataRelative
        use metadataDocument = readJson metadataPath
        let record = metadataDocument.RootElement
        exactProperties metadataRelative [ "schema"; "id"; "input"; "sha256" ] record
        if stringProperty "schema" record <> "fsgg.coordination.corpus-input/1" then fail "FC-METADATA-SCHEMA" id
        if stringProperty "id" record <> id then fail "FC-METADATA-ID" id
        let input = property "input" record
        exactProperties metadataRelative [ "schema"; "ordinal"; "kind"; "source"; "historicalContext"; "expectedBehavior"; "ambiguity"; "currentV1Result"; "provenance" ] input
        if stringProperty "schema" input <> "fsgg.coordination.frozen-corpus-case/1" then fail "FC-INPUT-SCHEMA" id
        if int64Property "ordinal" input <> int64 (ordinal + 1) then fail "FC-ORDER" id
        if stringProperty "kind" input <> stringProperty "kind" expected then fail "FC-KIND" id
        if stringProperty "historicalContext" input <> stringProperty "historicalContext" expected then fail "FC-CONTEXT" id
        let expectedBehavior = property "expectedBehavior" input
        if expectedBehavior.GetRawText() <> (property "expected" expected |> _.GetRawText()) then fail "FC-EXPECTED-BEHAVIOR" id
        let expectedBehaviorShape =
            if id = "C-churn" then [ "decisionClass"; "predicateId"; "authority"; "detail"; "metrics" ]
            else [ "decisionClass"; "predicateId"; "authority"; "detail" ]
        exactProperties $"{metadataRelative}.expectedBehavior" expectedBehaviorShape expectedBehavior
        if id = "C-churn" then
            let metrics = property "metrics" expectedBehavior
            let expectedMetrics = [ "windowHours"; "opened"; "closed"; "net"; "commits" ]
            exactProperties $"{metadataRelative}.expectedBehavior.metrics" expectedMetrics metrics
            for name in expectedMetrics do int64Property name metrics |> ignore

        let sourceBinding = property "source" input
        exactProperties metadataRelative [ "repository"; "commit"; "path"; "ref"; "mediaType"; "bytes"; "sha256"; "gitBlobSha1"; "payloadPath" ] sourceBinding
        let sourcePath = stringProperty "path" source
        let sourceRef = stringProperty "sourceRef" source
        let payloadRelative = $"corpus/originals/{id}.source"
        if stringProperty "repository" sourceBinding <> "FS-GG/.github" then fail "FC-SOURCE-REPOSITORY" id
        if stringProperty "commit" sourceBinding <> q0SourceCommit then fail "FC-SOURCE-COMMIT" id
        if stringProperty "path" sourceBinding <> sourcePath || not (safeRelativePath sourcePath) then fail "FC-SOURCE-PATH" id
        if stringProperty "ref" sourceBinding <> sourceRef || sourceRef <> $"git:{q0SourceCommit}:{sourcePath}" then fail "FC-SOURCE-REF" id
        if stringProperty "mediaType" sourceBinding <> stringProperty "mediaType" source then fail "FC-SOURCE-MEDIA" id
        if stringProperty "payloadPath" sourceBinding <> payloadRelative || not (safeRelativePath payloadRelative) then fail "FC-PAYLOAD-PATH" id
        ensureNoSymlink root payloadRelative
        let payloadPath = Path.Combine(root, payloadRelative)
        if not (File.Exists payloadPath) then fail "FC-PAYLOAD-MISSING" id
        let payload = File.ReadAllBytes payloadPath
        let expectedLength = int64Property "byteLength" source
        let expectedSha = stringProperty "sha256" source
        let expectedBlob = stringProperty "gitBlobSha1" source
        if expectedLength < 1L || int64 payload.Length <> expectedLength || int64Property "bytes" sourceBinding <> expectedLength then fail "FC-PAYLOAD-LENGTH" id
        if not (shaPattern.IsMatch expectedSha) || sha256 payload <> expectedSha || stringProperty "sha256" sourceBinding <> expectedSha || stringProperty "sha256" record <> expectedSha then fail "FC-PAYLOAD-SHA256" id
        if not (blobPattern.IsMatch expectedBlob) || gitBlobSha1 payload <> expectedBlob || stringProperty "gitBlobSha1" sourceBinding <> expectedBlob then fail "FC-PAYLOAD-BLOB" id

        let ambiguity = property "ambiguity" input
        exactProperties metadataRelative [ "state"; "rationale" ] ambiguity
        let expectedAmbiguity = if id = "C-rate" || id = "C-partial" then "explicit-indeterminate" else "none-recorded"
        let expectedRationale = if expectedAmbiguity = "explicit-indeterminate" then explicitIndeterminateRationale else noneRecordedRationale
        if stringProperty "state" ambiguity <> expectedAmbiguity || stringProperty "rationale" ambiguity <> expectedRationale then fail "FC-AMBIGUITY" id
        let decisionClass = property "expected" expected |> stringProperty "decisionClass"
        if (expectedAmbiguity = "explicit-indeterminate") <> (decisionClass = "Indeterminate") then fail "FC-AMBIGUITY-CONTRADICTION" id

        let result = property "currentV1Result" input
        exactProperties metadataRelative [ "state"; "outcome"; "evidence"; "headSha"; "observedAt"; "detail" ] result
        if stringProperty "headSha" result <> q0SourceCommit then fail "FC-RESULT-HEAD" id
        let observedAt = property "observedAt" result
        let outcome = property "outcome" result
        match id with
        | "C-pagination" | "C-stale" ->
            observedCount <- observedCount + 1
            let expectedUrl, expectedTime, expectedDetail =
                if id = "C-pagination" then
                    "https://github.com/FS-GG/.github/actions/runs/32908004312", "2026-08-25T22:50:19Z", "The exact-source-head recipe-pagination workflow directly executed the frozen pagination artifact and completed successfully."
                else
                    "https://github.com/FS-GG/.github/actions/runs/32908004500", "2026-08-25T22:50:35Z", "The exact-source-head engine-freshness workflow directly executed the frozen stale-read artifact and completed successfully."
            if stringProperty "state" result <> "observed" || outcome.ValueKind <> JsonValueKind.String || outcome.GetString() <> "passed" then fail "FC-RESULT-STATE" id
            if observedAt.ValueKind <> JsonValueKind.String || observedAt.GetString() <> expectedTime then fail "FC-RESULT-TIME" id
            validateTime id (observedAt.GetString())
            if stringProperty "evidence" result <> expectedUrl then fail "FC-RESULT-EVIDENCE" id
            if stringProperty "detail" result <> expectedDetail then fail "FC-RESULT-DETAIL" id
        | _ ->
            unobservedCount <- unobservedCount + 1
            if stringProperty "state" result <> "not-atomically-observed" || outcome.ValueKind <> JsonValueKind.Null then fail "FC-RESULT-STATE" id
            if observedAt.ValueKind <> JsonValueKind.Null then fail "FC-RESULT-TIME" id
            if stringProperty "evidence" result <> $"git:{q0Revision}:work/2953-gh-modernization-m0-invariants/q0-evidence.json#corpus/{id}" then fail "FC-RESULT-EVIDENCE" id
            if stringProperty "detail" result <> unobservedDetail then fail "FC-RESULT-DETAIL" id

        let provenance = property "provenance" input
        exactProperties metadataRelative [ "q0Revision"; "q0ManifestPath"; "q0ManifestSha256"; "q0EvidencePath"; "q0EvidenceSha256"; "importedByUnit" ] provenance
        let provenanceValues =
            [ "q0Revision", q0Revision
              "q0ManifestPath", "work/2953-gh-modernization-m0-invariants/q0-corpus-originals.json"
              "q0ManifestSha256", q0ManifestSha256
              "q0EvidencePath", "work/2953-gh-modernization-m0-invariants/q0-evidence.json"
              "q0EvidenceSha256", q0EvidenceSha256
              "importedByUnit", "GS2-03.2" ]
        if provenanceValues |> List.exists (fun (name, value) -> stringProperty name provenance <> value) then fail "FC-PROVENANCE-BINDING" id
        validateJsonSchema corpusSchema metadataRelative corpusSchema record

    if metadataIds.Count <> 21 then fail "FC-METADATA-COUNT" "metadata identity count differs"
    let schemaBytes = File.ReadAllBytes(Path.Combine(root, "schemas/v1/corpus-inputs.schema.json"))
    if sha256 schemaBytes <> frozenCorpusSchemaSha256 then
        fail "FC-SCHEMA-DIGEST" "the frozen corpus schema differs from its accepted content identity"
    $"frozenCorpusCases=21 observed={observedCount} unobserved={unobservedCount} aggregate={frozenAggregateSha256}"

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
          "test-results"; "artifact-manifests"; "reviews"; "qualification-inventories"; "qualification-manifests"; "mutation-proofs"; "accepted-receipts"; "repair-receipts" ]
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
        validateSchemaVocabulary $"schema.%s{category.Name}" schema
        if stringProperty "$schema" schema <> "https://json-schema.org/draft/2020-12/schema" then fail "ES-SCHEMA-DIALECT" category.Name
        if stringProperty "category" schema <> category.Name then fail "ES-SCHEMA-CATEGORY" category.Name
        let schemaSegments = category.Schema.Split('/')
        if schemaSegments.Length <> 3 || schemaSegments[0] <> "schemas" || not (Text.RegularExpressions.Regex("^v[1-9][0-9]*$").IsMatch schemaSegments[1]) then fail "ES-SCHEMA-VERSION" category.Schema
        if stringProperty "$id" schema <> $"https://fs-gg.github.io/schemas/evidence/%s{category.Name}/%s{schemaSegments[1]}" then fail "ES-SCHEMA-ID" category.Name

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
            let unitId = stringProperty "unitId" receipt.RootElement
            if isNull unitId || not (unitPattern.IsMatch unitId) then fail "ES-RECEIPT-UNIT" relative
            if stringProperty "state" receipt.RootElement <> "accepted" then fail "ES-RECEIPT-STATE" relative
            validateSha relative (stringProperty "unitContractSha256" receipt.RootElement)
            let sourceRevision = stringProperty "sourceRevision" receipt.RootElement
            if isNull sourceRevision || not (revisionPattern.IsMatch sourceRevision) then fail "ES-RECEIPT-REVISION" relative
            let artifacts = arrayProperty "artifacts" receipt.RootElement
            if artifacts.IsEmpty then fail "ES-RECEIPT-ARTIFACTS" relative
            for artifact in artifacts do
                exactProperties relative [ "name"; "sha256" ] artifact
                nonEmpty relative (stringProperty "name" artifact)
                validateSha relative (stringProperty "sha256" artifact)
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
            nonEmpty relative (stringProperty "id" manifest)
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
            if categoryName <> "corpus-inputs" then
                use recordSchemaDocument = readJson (Path.Combine(root, category.Schema))
                validateJsonSchema recordSchemaDocument.RootElement relative recordSchemaDocument.RootElement recordDocument.RootElement

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

    let frozenSummary = validateFrozenCorpus root entries
    $"EVIDENCE_STORAGE_OK categories={categories.Length} entries={entries.Length} maxTrackedBytes={maxBytes} {frozenSummary}"

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
        let entries = node["entries"].AsArray()
        entries.Add entry
        let ordered =
            entries
            |> Seq.map (fun item -> item.DeepClone())
            |> Seq.sortBy (fun item -> item["id"].GetValue<string>())
            |> Seq.toArray
        entries.Clear()
        for item in ordered do entries.Add item)

let refreshIndexedJson (root: string) (relative: string) =
    let bytes = File.ReadAllBytes(Path.Combine(root, relative))
    mutateJson (Path.Combine(root, "index.json")) (fun node ->
        let entry =
            node["entries"].AsArray()
            |> Seq.find (fun item -> item["path"].GetValue<string>() = relative)
        entry["bytes"] <- bytes.Length
        entry["sha256"] <- sha256 bytes)

let mutateCorpusJson (root: string) (id: string) mutation =
    let relative = $"corpus/{id}.json"
    mutateJson (Path.Combine(root, relative)) mutation
    refreshIndexedJson root relative

let nestedObject (node: JsonNode) (path: string list) =
    path |> List.fold (fun (current: JsonObject) name -> current[name].AsObject()) (node.AsObject())

let setNestedString (node: JsonNode) (path: string list) (name: string) (value: string) =
    let target = nestedObject node path
    target[name] <- JsonValue.Create value

let setNestedInt (node: JsonNode) (path: string list) (name: string) (value: int) =
    let target = nestedObject node path
    target[name] <- JsonValue.Create value

let selfTest evidenceRoot =
    let validManifest =
        "{\"schema\":\"fsgg.coordination.artifact-manifest/1\",\"id\":\"artifact-1\",\"store\":\"github-actions-artifact\",\"repositoryId\":131313,\"producerId\":33038126581,\"artifactId\":98405834712,\"artifactName\":\"qualification-evidence\",\"bytes\":70000,\"mediaType\":\"application/zip\",\"sha256\":\"" + String('a', 64) + "\"}"
    let reviewDigest = String('b', 64)
    let reviewFinding perspective id phase =
        "{\"author\":\"accountable-owner\",\"candidateFingerprintSha256\":\"" + reviewDigest
        + "\",\"completedAt\":\"2026-08-31T00:00:00Z\",\"contentSha256\":\"" + reviewDigest
        + "\",\"decision\":\"passed\",\"digest\":\"" + reviewDigest + "\",\"evidenceSetSha256\":\""
        + reviewDigest + "\",\"id\":\"" + id + "\",\"perspective\":\"" + perspective + "\",\"phaseId\":\"" + phase + "\"}"
    let reviewFindings =
        [ reviewFinding "adapter" "finding-adapter" "phase-adapter"
          reviewFinding "architecture" "finding-architecture" "phase-architecture"
          reviewFinding "cutover" "finding-cutover" "phase-cutover"
          reviewFinding "migration" "finding-migration" "phase-migration"
          reviewFinding "security" "finding-security" "phase-security" ]
        |> String.concat ","
    let reviewPerspectives = "[\"adapter\",\"architecture\",\"cutover\",\"migration\",\"security\"]"
    let validReview =
        "{\"accountableOwner\":\"accountable-owner\",\"candidate\":{\"commitSha\":\"" + String('a', 40)
        + "\",\"fingerprintSha256\":\"" + reviewDigest + "\",\"treeSha256\":\"" + reviewDigest
        + "\",\"unitContractSha256\":\"" + reviewDigest + "\"},\"createdAt\":\"2026-08-31T00:00:01Z\",\"digest\":\""
        + reviewDigest + "\",\"evidence\":[{\"id\":\"architecture-tests\",\"sha256\":\"" + reviewDigest
        + "\"}],\"evidenceSetSha256\":\"" + reviewDigest + "\",\"findings\":[" + reviewFindings
        + "],\"rollup\":{\"acceptanceAuthority\":\"accountable-owner-only\",\"accountableOwner\":\"accountable-owner\",\"derivation\":\"all-required-bound-green/1\",\"digest\":\""
        + reviewDigest + "\",\"findingSetSha256\":\"" + reviewDigest + "\",\"outcome\":\"passed\",\"passingPerspectives\":"
        + reviewPerspectives + ",\"requiredPerspectives\":" + reviewPerspectives + "},\"schema\":\"fsgg.coordination.critique-evidence/1\"}"
    let proofGates = [ "compiler"; "dependencies"; "externalFixtures"; "generatedCases"; "independentCases"; "model"; "packages"; "results"; "reviewers"; "sources" ]
    let proofMutations = [ "vacuous"; "absent"; "stale"; "truncated"; "forged"; "generated-only" ]
    let proofControls =
        proofGates
        |> List.map (fun gate -> $"{{\"diagnostics\":[],\"gateClass\":\"%s{gate}\",\"outcome\":\"passed\"}}")
        |> String.concat ","
    let proofObservations =
        [ for gate in proofGates do
              for mutation in proofMutations do
                  yield $"{{\"diagnostics\":[\"TEST-REJECTION\"],\"gateClass\":\"%s{gate}\",\"mutationKind\":\"%s{mutation}\",\"outcome\":\"rejected\"}}" ]
        |> String.concat ","
    let quoted values = values |> List.map (sprintf "\"%s\"") |> String.concat ","
    let validMutationProof =
        "{\"baselineSha256\":\"" + reviewDigest + "\",\"candidateCommit\":\"" + String('a', 40)
        + "\",\"candidateTreeSha256\":\"" + reviewDigest + "\",\"controls\":[" + proofControls
        + "],\"digest\":\"" + reviewDigest + "\",\"gateClasses\":[" + quoted proofGates
        + "],\"inventorySha256\":\"" + reviewDigest + "\",\"mutationKinds\":[" + quoted proofMutations
        + "],\"observations\":[" + proofObservations + "],\"schema\":\"fsgg.coordination.harness-mutation-proof/1\",\"unitContractSha256\":\""
        + reviewDigest + "\",\"validatorSha256\":\"" + reviewDigest + "\"}"
    let positiveRoot = Path.Combine(Path.GetTempPath(), $"fsgg-evidence-{Guid.NewGuid():N}")
    try
        copyDirectory evidenceRoot positiveRoot
        addTrackedJson positiveRoot "manifest-valid" "artifact-manifests" "artifact-manifests/manifest-valid.json" validManifest
        addTrackedJson positiveRoot "review-valid" "reviews" "reviews/review-valid.json" validReview
        addTrackedJson positiveRoot "mutation-proof-valid" "mutation-proofs" "mutation-proofs/proof-valid.json" validMutationProof
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
          "missing-schema", (fun root -> File.Delete(Path.Combine(root, "schemas/v2/reviews.schema.json"))), "ES-SCHEMA-MISSING"
          "missing-category", (fun root -> Directory.Delete(Path.Combine(root, "reviews"), true)), "ES-CATEGORY-MISSING"
          "malformed-category-record", (fun root ->
              addTrackedJson root "review-malformed" "reviews" "reviews/malformed.json" "{}"), "ES-JSON-SHAPE"
          "review-six-findings", (fun root ->
              let relative = "reviews/review-six-findings.json"
              addTrackedJson root "review-six-findings" "reviews" relative validReview
              mutateJson (Path.Combine(root, relative)) (fun node ->
                  let findings = node["findings"].AsArray()
                  findings.Add(findings[0].DeepClone()))
              refreshIndexedJson root relative), "ES-SCHEMA-VALIDATION"
          "review-external-authority", (fun root ->
              let relative = "reviews/review-external-authority.json"
              addTrackedJson root "review-external-authority" "reviews" relative validReview
              mutateJson (Path.Combine(root, relative)) (fun node ->
                  node["rollup"].AsObject()["acceptanceAuthority"] <- "external-quorum")
              refreshIndexedJson root relative), "ES-SCHEMA-VALIDATION"
          "review-extra-property", (fun root ->
              let relative = "reviews/review-extra-property.json"
              addTrackedJson root "review-extra-property" "reviews" relative validReview
              mutateJson (Path.Combine(root, relative)) (fun node -> node["externalApproval"] <- true)
              refreshIndexedJson root relative), "ES-JSON-SHAPE"
          "mutation-proof-truncated-matrix", (fun root ->
              let relative = "mutation-proofs/truncated.json"
              addTrackedJson root "mutation-proof-truncated" "mutation-proofs" relative validMutationProof
              mutateJson (Path.Combine(root, relative)) (fun node -> node["observations"].AsArray().RemoveAt(0))
              refreshIndexedJson root relative), "ES-SCHEMA-VALIDATION"
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
                  let entry =
                      node["entries"].AsArray()
                      |> Seq.find (fun item -> item["path"].GetValue<string>() = "accepted/GS2-01.2.json")
                  entry["bytes"] <- bytes.Length
                  entry["sha256"] <- sha256 bytes)), "ES-PAYLOAD-OVERSIZE"
          "corpus-empty-id", (fun root ->
              let content = "{\"schema\":\"fsgg.coordination.corpus-input/1\",\"id\":\"\",\"input\":{},\"sha256\":\"" + String('a', 64) + "\"}"
              addTrackedJson root "corpus-invalid" "corpus-inputs" "corpus/invalid.json" content), "ES-RECORD-VALUE"
          "observation-date-only", (fun root ->
              let content = "{\"schema\":\"fsgg.coordination.external-observation/1\",\"id\":\"observation-1\",\"source\":\"https://example.invalid\",\"observedAt\":\"2026-08-27\",\"sha256\":\"" + String('a', 64) + "\"}"
              addTrackedJson root "observation-invalid" "external-observations" "observations/invalid.json" content), "ES-RECORD-TIME"
          "oracle-invalid-sha", (fun root ->
              let content = "{\"schema\":\"fsgg.coordination.independent-oracle/1\",\"id\":\"oracle-1\",\"oracle\":\"reference\",\"expected\":{},\"sha256\":\"nope\"}"
              addTrackedJson root "oracle-invalid" "independent-oracles" "oracles/invalid.json" content), "ES-SHA256"
          "generated-extra-property", (fun root ->
              let content = "{\"schema\":\"fsgg.coordination.generated-case/1\",\"id\":\"generated-1\",\"generator\":\"generator\",\"seed\":\"seed\",\"sha256\":\"" + String('a', 64) + "\",\"extra\":true}"
              addTrackedJson root "generated-invalid" "generated-cases" "generated/invalid.json" content), "ES-JSON-SHAPE"
          "test-uppercase-candidate", (fun root ->
              let content = "{\"schema\":\"fsgg.coordination.test-result/1\",\"id\":\"test-1\",\"candidate\":\"" + String('A', 40) + "\",\"outcome\":\"passed\",\"sha256\":\"" + String('a', 64) + "\"}"
              addTrackedJson root "test-invalid" "test-results" "test-results/invalid.json" content), "ES-RECORD-CANDIDATE"
          "artifact-empty-id", (fun root ->
              let content = validManifest.Replace("\"id\":\"artifact-1\"", "\"id\":\"\"")
              addTrackedJson root "manifest-empty-id" "artifact-manifests" "artifact-manifests/empty-id.json" content), "ES-RECORD-VALUE"
          "receipt-invalid-unit-and-artifacts", (fun root ->
              let content = "{\"schema\":\"fsgg.coordination.unit-acceptance/1\",\"unitId\":\"x\",\"state\":\"accepted\",\"unitContractSha256\":\"" + String('a', 64) + "\",\"sourceRevision\":\"" + String('a', 40) + "\",\"artifacts\":[],\"acceptedAt\":\"2026-08-27T00:00:00Z\",\"digest\":\"" + String('a', 64) + "\"}"
              addTrackedJson root "accepted-invalid" "accepted-receipts" "accepted/invalid.json" content), "ES-RECEIPT-UNIT"
          "frozen-payload-byte", (fun root ->
              let path = Path.Combine(root, "corpus/originals/C-claim.source")
              let bytes = File.ReadAllBytes path
              bytes[0] <- bytes[0] ^^^ 0x01uy
              File.WriteAllBytes(path, bytes)), "FC-PAYLOAD-SHA256"
          "frozen-stale-length", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedInt node [ "input"; "source" ] "bytes" 1)), "FC-PAYLOAD-LENGTH"
          "frozen-stale-sha", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "sha256" (String('0', 64)))), "FC-PAYLOAD-SHA256"
          "frozen-stale-blob", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "gitBlobSha1" (String('0', 40)))), "FC-PAYLOAD-BLOB"
          "frozen-source-repository", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "repository" "FS-GG/elsewhere")), "FC-SOURCE-REPOSITORY"
          "frozen-source-commit", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "commit" (String('0', 40)))), "FC-SOURCE-COMMIT"
          "frozen-source-path", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "path" "tests/other.py")), "FC-SOURCE-PATH"
          "frozen-source-ref", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "ref" "git:main:mutable")), "FC-SOURCE-REF"
          "frozen-payload-path-traversal", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "source" ] "payloadPath" "../escape")), "FC-PAYLOAD-PATH"
          "frozen-expected-behavior", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "expectedBehavior" ] "decisionClass" "Accept")), "FC-EXPECTED-BEHAVIOR"
          "frozen-ambiguity", (fun root ->
              mutateCorpusJson root "C-rate" (fun node -> setNestedString node [ "input"; "ambiguity" ] "state" "none-recorded")), "FC-AMBIGUITY"
          "frozen-ambiguity-rationale", (fun root ->
              mutateCorpusJson root "C-rate" (fun node -> setNestedString node [ "input"; "ambiguity" ] "rationale" "tampered but nonempty")), "FC-AMBIGUITY"
          "frozen-schema-observed-at", (fun root ->
              mutateJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json")) (fun node ->
                  setNestedString node [ "properties"; "input"; "properties"; "currentV1Result"; "properties"; "observedAt" ] "type" "string")), "FC-SCHEMA-CONTRACT"
          "frozen-schema-required-source", (fun root ->
              mutateJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json")) (fun node ->
                  let required = ((nestedObject node [ "properties"; "input" ])["required"]).AsArray()
                  let sourceIndex = required |> Seq.findIndex (fun item -> item.GetValue<string>() = "source")
                  required.RemoveAt(sourceIndex))), "FC-SCHEMA-DIGEST"
          "frozen-schema-id-pattern", (fun root ->
              mutateJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json")) (fun node ->
                  setNestedString node [ "properties"; "id" ] "pattern" "^Z-[a-z0-9-]+$")), "ES-SCHEMA-VALIDATION"
          "frozen-schema-unsupported-keyword", (fun root ->
              mutateJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json")) (fun node ->
                  setNestedInt node [ "properties"; "id" ] "maxLength" 1)), "ES-SCHEMA-KEYWORD"
          "frozen-schema-additional-properties-without-properties", (fun root ->
              mutateJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json")) (fun node ->
                  (nestedObject node [ "properties"; "input"; "properties"; "source" ]).Remove("properties") |> ignore)), "ES-SCHEMA-VALIDATION"
          "frozen-schema-dialect", (fun root ->
              mutateJson (Path.Combine(root, "schemas/v1/corpus-inputs.schema.json")) (fun node ->
                  node["$schema"] <- "https://example.invalid/unknown-dialect")), "ES-SCHEMA-DIALECT"
          "frozen-metadata-noncanonical", (fun root ->
              let relative = "corpus/C-claim.json"
              let path = Path.Combine(root, relative)
              let content = File.ReadAllText path
              File.WriteAllText(path, content.Insert(1, " "), UTF8Encoding(false))
              refreshIndexedJson root relative), "ES-JSON-CANONICAL"
          "frozen-unobserved-green", (fun root ->
              mutateCorpusJson root "C-claim" (fun node ->
                  setNestedString node [ "input"; "currentV1Result" ] "state" "observed"
                  setNestedString node [ "input"; "currentV1Result" ] "outcome" "passed")), "FC-RESULT-STATE"
          "frozen-unobserved-time", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "currentV1Result" ] "observedAt" "2026-08-25T22:00:00Z")), "FC-RESULT-TIME"
          "frozen-unobserved-detail", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "currentV1Result" ] "detail" "tampered but nonempty")), "FC-RESULT-DETAIL"
          "frozen-observed-evidence", (fun root ->
              mutateCorpusJson root "C-pagination" (fun node -> setNestedString node [ "input"; "currentV1Result" ] "evidence" "https://example.invalid/run")), "FC-RESULT-EVIDENCE"
          "frozen-observed-detail", (fun root ->
              mutateCorpusJson root "C-pagination" (fun node -> setNestedString node [ "input"; "currentV1Result" ] "detail" "tampered but nonempty")), "FC-RESULT-DETAIL"
          "frozen-order", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedInt node [ "input" ] "ordinal" 2)), "FC-ORDER"
          "frozen-input-schema", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input" ] "schema" "fsgg.coordination.frozen-corpus-case/2")), "FC-INPUT-SCHEMA"
          "frozen-unknown-field", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> (nestedObject node [ "input" ]).Add("unknown", true))), "ES-JSON-SHAPE"
          "frozen-extra-payload", (fun root ->
              File.WriteAllText(Path.Combine(root, "corpus/originals/extra.source"), "extra")), "FC-PAYLOAD-INVENTORY"
          "frozen-provenance-byte", (fun root ->
              File.AppendAllText(Path.Combine(root, q0ManifestRelative), " ")), "FC-PROVENANCE-DIGEST"
          "frozen-payload-symlink", (fun root ->
              let payload = Path.Combine(root, "corpus/originals/C-claim.source")
              let target = Path.Combine(root, "corpus/originals/C-touch-set.source")
              File.Delete payload
              File.CreateSymbolicLink(payload, target) |> ignore), "ES-PATH-SYMLINK"
          "frozen-provenance-binding", (fun root ->
              mutateCorpusJson root "C-claim" (fun node -> setNestedString node [ "input"; "provenance" ] "importedByUnit" "GS2-03.3")), "FC-PROVENANCE-BINDING" ]
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
    $"EVIDENCE_STORAGE_SELF_TEST_OK negativeCases={cases.Length} positiveArtifactManifests=1 positiveCritiqueBundles=1 positiveMutationProofs=1"

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
