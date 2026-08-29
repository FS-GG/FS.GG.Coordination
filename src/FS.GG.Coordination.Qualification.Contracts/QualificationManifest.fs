namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

type QualificationManifestCandidate =
    { CommitSha: string
      TreeSha256: string
      ContractSha256: string
      Producer: string }

type QualificationManifestContent =
    { Id: string
      Sha256: string
      Bytes: int64
      MediaType: string
      Producer: string
      ObservedAt: DateTimeOffset }

type QualificationManifestEnvironment =
    { Os: string
      Architecture: string
      Runtime: string
      Locale: string
      Timezone: string
      NetworkMode: string
      Producer: string
      ObservedAt: DateTimeOffset }

type QualificationManifestResult =
    { Id: string
      QGate: string
      Sha256: string
      Producer: string
      CompletedAt: DateTimeOffset }

type QualificationManifestReview =
    { Id: string
      Role: string
      Sha256: string
      Principal: string
      CompletedAt: DateTimeOffset }

type QualificationManifestExpectedInventory =
    { Sources: string list
      Model: string list
      Compiler: string list
      Dependencies: string list
      GeneratedCases: string list
      IndependentCases: string list
      ExternalFixtures: string list
      Packages: string list
      Results: string list
      Reviewers: string list }

type QualificationManifestInput =
    { Candidate: QualificationManifestCandidate
      Expected: QualificationManifestExpectedInventory
      CreatedAt: DateTimeOffset
      Sources: QualificationManifestContent list
      Model: QualificationManifestContent list
      Compiler: QualificationManifestContent list
      Dependencies: QualificationManifestContent list
      GeneratedCases: QualificationManifestContent list
      IndependentCases: QualificationManifestContent list
      ExternalFixtures: QualificationManifestContent list
      Packages: QualificationManifestContent list
      Environment: QualificationManifestEnvironment
      Results: QualificationManifestResult list
      Reviewers: QualificationManifestReview list }

type QualificationManifestFinding =
    { Code: string
      Path: string
      Expected: string
      Actual: string }

[<RequireQualifiedAccess>]
module QualificationManifest =
    [<Literal>]
    let Schema = "fsgg.coordination.qualification-manifest/1"

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private finding code path expected actual =
        { Code = code; Path = path; Expected = expected; Actual = actual }

    let private canonicalTime (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

    let private writeCanonical (writer: Utf8JsonWriter) (node: JsonNode) =
        let rec write (value: JsonNode) =
            match value with
            | null -> writer.WriteNullValue()
            | :? JsonObject as objectValue ->
                writer.WriteStartObject()
                objectValue
                |> Seq.sortWith (fun left right -> String.CompareOrdinal(left.Key, right.Key))
                |> Seq.iter (fun property ->
                    writer.WritePropertyName property.Key
                    write property.Value)
                writer.WriteEndObject()
            | :? JsonArray as arrayValue ->
                writer.WriteStartArray()
                arrayValue |> Seq.iter write
                writer.WriteEndArray()
            | _ -> value.WriteTo writer
        write node

    let private canonicalBytes (node: JsonNode) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        writeCanonical writer node
        writer.Flush()
        stream.ToArray()

    let private cloneWithoutDigest (root: JsonObject) =
        let clone = JsonNode.Parse(root.ToJsonString()).AsObject()
        clone.Remove "digest" |> ignore
        clone

    let private addString (target: JsonObject) (name: string) (value: string) = target.Add(name, JsonValue.Create value)
    let private addInt64 (target: JsonObject) (name: string) (value: int64) = target.Add(name, JsonValue.Create value)

    let private idArray (values: string list) =
        let result = JsonArray()
        values |> List.sort |> List.iter (fun value -> result.Add(JsonValue.Create value))
        result

    let private expectedPairs (expected: QualificationManifestExpectedInventory) =
        [ "compiler", expected.Compiler; "dependencies", expected.Dependencies
          "externalFixtures", expected.ExternalFixtures; "generatedCases", expected.GeneratedCases
          "independentCases", expected.IndependentCases; "model", expected.Model
          "packages", expected.Packages; "results", expected.Results
          "reviewers", expected.Reviewers; "sources", expected.Sources ]

    let private expectedIdsNode expected =
        let node = JsonObject()
        for name, ids in expectedPairs expected do node.Add(name, idArray ids)
        node

    let private inventoryNode expected =
        let root = expectedIdsNode expected
        root.Add("schema", JsonValue.Create "fsgg.coordination.qualification-inventory/1")
        root

    let private inventoryCanonicalBytes expected =
        Array.append (inventoryNode expected |> canonicalBytes) [| byte '\n' |]

    let private contentNode kind candidateSha (entry: QualificationManifestContent) =
        let node = JsonObject()
        addInt64 node "bytes" entry.Bytes
        addString node "candidateSha" candidateSha
        addString node "id" entry.Id
        addString node "kind" kind
        addString node "mediaType" entry.MediaType
        addString node "observedAt" (canonicalTime entry.ObservedAt)
        addString node "producer" entry.Producer
        addString node "sha256" entry.Sha256
        node

    let private contentArray (kind: string) (candidateSha: string) (entries: QualificationManifestContent list) =
        let result = JsonArray()
        entries
        |> List.sortBy _.Id
        |> List.iter (fun entry -> result.Add(contentNode kind candidateSha entry))
        result

    let private environmentNode candidateSha (environment: QualificationManifestEnvironment) =
        let node = JsonObject()
        addString node "architecture" environment.Architecture
        addString node "candidateSha" candidateSha
        addString node "locale" environment.Locale
        addString node "networkMode" environment.NetworkMode
        addString node "observedAt" (canonicalTime environment.ObservedAt)
        addString node "os" environment.Os
        addString node "producer" environment.Producer
        addString node "runtime" environment.Runtime
        addString node "timezone" environment.Timezone
        node

    let private inputSetNode (root: JsonObject) =
        let node = JsonObject()
        for name in
            [ "compiler"; "dependencies"; "environment"; "externalFixtures"; "generatedCases"
              "independentCases"; "model"; "packages"; "sources" ] do
            node.Add(name, JsonNode.Parse(root[name].ToJsonString()))
        node

    let private inputSetDigest (root: JsonObject) = inputSetNode root |> canonicalBytes |> sha256

    let private resultNode candidateSha inputDigest (entry: QualificationManifestResult) =
        let node = JsonObject()
        addString node "candidateSha" candidateSha
        addString node "completedAt" (canonicalTime entry.CompletedAt)
        addString node "id" entry.Id
        addString node "inputSetSha256" inputDigest
        addString node "outcome" "passed"
        addString node "producer" entry.Producer
        addString node "qGate" entry.QGate
        addString node "sha256" entry.Sha256
        node

    let private reviewNode candidateSha inputDigest (entry: QualificationManifestReview) =
        let node = JsonObject()
        addString node "candidateSha" candidateSha
        addString node "completedAt" (canonicalTime entry.CompletedAt)
        addString node "id" entry.Id
        addString node "inputSetSha256" inputDigest
        addString node "outcome" "accepted"
        addString node "principal" entry.Principal
        addString node "role" entry.Role
        addString node "sha256" entry.Sha256
        node

    let private manifestNode (input: QualificationManifestInput) =
        let root = JsonObject()
        let candidate = JsonObject()
        addString candidate "commitSha" input.Candidate.CommitSha
        addString candidate "contractSha256" input.Candidate.ContractSha256
        addString candidate "producer" input.Candidate.Producer
        addString candidate "treeSha256" input.Candidate.TreeSha256
        let expectedIds = expectedIdsNode input.Expected
        candidate.Add("expectedIds", expectedIds)
        addString candidate "inventorySha256" (inventoryCanonicalBytes input.Expected |> sha256)
        root.Add("candidate", candidate)
        root.Add("compiler", contentArray "compiler" input.Candidate.CommitSha input.Compiler)
        root.Add("dependencies", contentArray "dependency" input.Candidate.CommitSha input.Dependencies)
        root.Add("environment", environmentNode input.Candidate.CommitSha input.Environment)
        root.Add("externalFixtures", contentArray "external-fixture" input.Candidate.CommitSha input.ExternalFixtures)
        root.Add("generatedCases", contentArray "generated-case" input.Candidate.CommitSha input.GeneratedCases)
        root.Add("independentCases", contentArray "independent-case" input.Candidate.CommitSha input.IndependentCases)
        root.Add("model", contentArray "quint-model" input.Candidate.CommitSha input.Model)
        root.Add("packages", contentArray "package" input.Candidate.CommitSha input.Packages)
        root.Add("sources", contentArray "source" input.Candidate.CommitSha input.Sources)
        let inputsDigest = inputSetDigest root
        addString candidate "inputSetSha256" inputsDigest
        let results = JsonArray()
        input.Results |> List.sortBy _.Id |> List.iter (fun entry -> results.Add(resultNode input.Candidate.CommitSha inputsDigest entry))
        root.Add("results", results)
        let reviewers = JsonArray()
        input.Reviewers |> List.sortBy _.Id |> List.iter (fun entry -> reviewers.Add(reviewNode input.Candidate.CommitSha inputsDigest entry))
        root.Add("reviewers", reviewers)
        addString root "createdAt" (canonicalTime input.CreatedAt)
        addString root "schema" Schema
        root

    let private stringPattern pattern value =
        not (isNull value) && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)

    let private isSha value = stringPattern "^[0-9a-f]{64}$" value
    let private isRevision value = stringPattern "^[0-9a-f]{40}$" value
    let private isId value = stringPattern "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$" value

    let private parseTime value =
        match DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, parsed -> Some parsed
        | _ -> None

    let private properties (value: JsonElement) =
        value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    let private exactProperties code path expected (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            [ finding code path (String.concat "," expected) (value.ValueKind.ToString()) ]
        else
            let actual = properties value
            let expectedSet = Set.ofList expected
            if actual = expectedSet then []
            else [ finding code path (String.concat "," expected) (actual |> Set.toList |> String.concat ",") ]

    let private tryProperty (name: string) (value: JsonElement) =
        let mutable child = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &child) then Some child else None

    let private stringProperty code path name value findings =
        match tryProperty name value with
        | Some child when child.ValueKind = JsonValueKind.String -> child.GetString(), findings
        | Some child -> null, finding code (path + "/" + name) "string" (child.ValueKind.ToString()) :: findings
        | None -> null, finding code (path + "/" + name) "present string" "missing" :: findings

    let private arrayProperty code path name value findings =
        match tryProperty name value with
        | Some child when child.ValueKind = JsonValueKind.Array -> child.EnumerateArray() |> Seq.toList, findings
        | Some child -> [], finding code (path + "/" + name) "array" (child.ValueKind.ToString()) :: findings
        | None -> [], finding code (path + "/" + name) "present array" "missing" :: findings

    let private parseInventory (inventory: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse inventory
            let root = document.RootElement
            let names =
                [ "compiler"; "dependencies"; "externalFixtures"; "generatedCases"; "independentCases"
                  "model"; "packages"; "results"; "reviewers"; "schema"; "sources" ]
            let mutable findings = exactProperties "QM-INVENTORY-SHAPE" "/inventory" names root
            let schema, next = stringProperty "QM-INVENTORY-SCHEMA" "/inventory" "schema" root findings
            findings <- next
            if schema <> "fsgg.coordination.qualification-inventory/1" then
                findings <- finding "QM-INVENTORY-SCHEMA" "/inventory/schema" "fsgg.coordination.qualification-inventory/1" (if isNull schema then "<missing>" else schema) :: findings
            let mutable values = Map.empty
            for name in names |> List.filter ((<>) "schema") do
                let elements, next = arrayProperty "QM-INVENTORY-IDS" "/inventory" name root findings
                findings <- next
                let ids =
                    elements
                    |> List.choose (fun value -> if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)
                if ids.Length <> elements.Length || List.isEmpty ids || ids |> List.exists (isId >> not) || ids <> List.sort ids || ids.Length <> (ids |> List.distinct).Length then
                    findings <- finding "QM-INVENTORY-IDS" ("/inventory/" + name) "nonempty sorted unique stable identifiers" (String.concat "," ids) :: findings
                values <- values.Add(name, ids)
            let node = JsonNode.Parse(inventory.Span)
            let canonical = Array.append (canonicalBytes node) [| byte '\n' |]
            if not (inventory.Span.SequenceEqual(ReadOnlySpan<byte>(canonical))) then
                findings <- finding "QM-INVENTORY-CANONICAL" "/inventory" "canonical JSON bytes" "non-canonical bytes" :: findings
            match findings |> List.rev |> List.distinct with
            | [] ->
                Ok(
                    { Sources = values["sources"]; Model = values["model"]; Compiler = values["compiler"]
                      Dependencies = values["dependencies"]; GeneratedCases = values["generatedCases"]
                      IndependentCases = values["independentCases"]; ExternalFixtures = values["externalFixtures"]
                      Packages = values["packages"]; Results = values["results"]; Reviewers = values["reviewers"] },
                    canonical)
            | errors -> Error errors
        with
        | :? JsonException as error -> Error [ finding "QM-INVENTORY-JSON" "/inventory" "valid JSON" error.Message ]
        | :? InvalidOperationException as error -> Error [ finding "QM-INVENTORY-TYPE" "/inventory" "valid inventory types" error.Message ]
        | :? NullReferenceException as error -> Error [ finding "QM-INVENTORY-TYPE" "/inventory" "complete inventory" error.Message ]

    type private ObservedEntry =
        { Id: string; Producer: string; ObservedAt: DateTimeOffset option }

    let private validateContent category kind candidate root findings =
        let entries, findings = arrayProperty "QM-CATEGORY" "" category root findings
        let mutable current = findings
        if List.isEmpty entries then current <- finding "QM-CATEGORY-EMPTY" ("/" + category) "at least one entry" "empty" :: current
        let observed = ResizeArray<ObservedEntry>()
        let seen = HashSet<string>(StringComparer.Ordinal)
        let mutable previous = ""
        entries |> List.iteri (fun index entry ->
            let path = $"/%s{category}/%d{index}"
            current <- exactProperties "QM-ENTRY-SHAPE" path [ "bytes"; "candidateSha"; "id"; "kind"; "mediaType"; "observedAt"; "producer"; "sha256" ] entry @ current
            let id, next = stringProperty "QM-ENTRY-ID" path "id" entry current
            current <- next
            let actualKind, next = stringProperty "QM-ENTRY-KIND" path "kind" entry current
            current <- next
            let digest, next = stringProperty "QM-ENTRY-DIGEST" path "sha256" entry current
            current <- next
            let mediaType, next = stringProperty "QM-ENTRY-MEDIA" path "mediaType" entry current
            current <- next
            let producer, next = stringProperty "QM-ENTRY-PRODUCER" path "producer" entry current
            current <- next
            let candidateSha, next = stringProperty "QM-CANDIDATE-BINDING" path "candidateSha" entry current
            current <- next
            let observedAt, next = stringProperty "QM-TIME" path "observedAt" entry current
            current <- next
            match tryProperty "bytes" entry with
            | Some value when value.ValueKind = JsonValueKind.Number ->
                match value.TryGetInt64() with
                | true, length when length > 0L -> ()
                | _ -> current <- finding "QM-ENTRY-BYTES" (path + "/bytes") "positive integer" (value.GetRawText()) :: current
            | _ -> current <- finding "QM-ENTRY-BYTES" (path + "/bytes") "positive integer" "missing or non-integer" :: current
            if not (isId id) then current <- finding "QM-ENTRY-ID" (path + "/id") "stable identifier" (if isNull id then "<missing>" else id) :: current
            elif not (seen.Add id) then current <- finding "QM-ENTRY-DUPLICATE" (path + "/id") "unique identifier" id :: current
            if index > 0 && String.CompareOrdinal(previous, id) >= 0 then current <- finding "QM-ENTRY-ORDER" (path + "/id") "ordinal id order" id :: current
            previous <- if isNull id then previous else id
            if actualKind <> kind then current <- finding "QM-ENTRY-KIND" (path + "/kind") kind (if isNull actualKind then "<missing>" else actualKind) :: current
            if not (isSha digest) then current <- finding "QM-ENTRY-DIGEST" (path + "/sha256") "lowercase SHA-256" (if isNull digest then "<missing>" else digest) :: current
            if String.IsNullOrWhiteSpace mediaType then current <- finding "QM-ENTRY-MEDIA" (path + "/mediaType") "non-empty media type" "empty" :: current
            if not (isId producer) then current <- finding "QM-ENTRY-PRODUCER" (path + "/producer") "stable principal id" (if isNull producer then "<missing>" else producer) :: current
            if candidateSha <> candidate then current <- finding "QM-CANDIDATE-BINDING" (path + "/candidateSha") candidate (if isNull candidateSha then "<missing>" else candidateSha) :: current
            let parsed = parseTime observedAt
            if parsed.IsNone then current <- finding "QM-TIME" (path + "/observedAt") "canonical UTC second" (if isNull observedAt then "<missing>" else observedAt) :: current
            observed.Add { Id = id; Producer = producer; ObservedAt = parsed })
        List.ofSeq observed, current

    let private validateManifest (expected: QualificationManifestExpectedInventory) expectedInventoryDigest (manifest: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse manifest
            let root = document.RootElement
            let rootFields =
                [ "candidate"; "compiler"; "createdAt"; "dependencies"; "digest"; "environment"
                  "externalFixtures"; "generatedCases"; "independentCases"; "model"; "packages"
                  "results"; "reviewers"; "schema"; "sources" ]
            let mutable findings = exactProperties "QM-ROOT-SHAPE" "/" rootFields root
            let schema, next = stringProperty "QM-SCHEMA" "" "schema" root findings
            findings <- next
            if schema <> Schema then findings <- finding "QM-SCHEMA" "/schema" Schema (if isNull schema then "<missing>" else schema) :: findings
            let createdAtRaw, next = stringProperty "QM-TIME" "" "createdAt" root findings
            findings <- next
            let createdAt = parseTime createdAtRaw
            if createdAt.IsNone then findings <- finding "QM-TIME" "/createdAt" "canonical UTC second" (if isNull createdAtRaw then "<missing>" else createdAtRaw) :: findings
            let candidateElement = tryProperty "candidate" root |> Option.defaultValue Unchecked.defaultof<JsonElement>
            findings <- exactProperties "QM-CANDIDATE-SHAPE" "/candidate" [ "commitSha"; "contractSha256"; "expectedIds"; "inputSetSha256"; "inventorySha256"; "producer"; "treeSha256" ] candidateElement @ findings
            let commit, next = stringProperty "QM-CANDIDATE" "/candidate" "commitSha" candidateElement findings
            findings <- next
            let tree, next = stringProperty "QM-CANDIDATE" "/candidate" "treeSha256" candidateElement findings
            findings <- next
            let contract, next = stringProperty "QM-CANDIDATE" "/candidate" "contractSha256" candidateElement findings
            findings <- next
            let statedInputs, next = stringProperty "QM-INPUT-SET" "/candidate" "inputSetSha256" candidateElement findings
            findings <- next
            let statedInventory, next = stringProperty "QM-INVENTORY-BINDING" "/candidate" "inventorySha256" candidateElement findings
            findings <- next
            let candidateProducer, next = stringProperty "QM-CANDIDATE" "/candidate" "producer" candidateElement findings
            findings <- next
            if not (isRevision commit) then findings <- finding "QM-CANDIDATE" "/candidate/commitSha" "lowercase 40-hex revision" (if isNull commit then "<missing>" else commit) :: findings
            if not (isSha tree) then findings <- finding "QM-CANDIDATE" "/candidate/treeSha256" "lowercase SHA-256" (if isNull tree then "<missing>" else tree) :: findings
            if not (isSha contract) then findings <- finding "QM-CANDIDATE" "/candidate/contractSha256" "lowercase SHA-256" (if isNull contract then "<missing>" else contract) :: findings
            if not (isSha statedInputs) then findings <- finding "QM-INPUT-SET" "/candidate/inputSetSha256" "lowercase SHA-256" (if isNull statedInputs then "<missing>" else statedInputs) :: findings
            if statedInventory <> expectedInventoryDigest then findings <- finding "QM-INVENTORY-BINDING" "/candidate/inventorySha256" expectedInventoryDigest (if isNull statedInventory then "<missing>" else statedInventory) :: findings
            if not (isId candidateProducer) then findings <- finding "QM-CANDIDATE" "/candidate/producer" "stable principal id" (if isNull candidateProducer then "<missing>" else candidateProducer) :: findings
            let expectedNames =
                [ "compiler"; "dependencies"; "externalFixtures"; "generatedCases"; "independentCases"
                  "model"; "packages"; "results"; "reviewers"; "sources" ]
            let expectedElement = tryProperty "expectedIds" candidateElement |> Option.defaultValue Unchecked.defaultof<JsonElement>
            findings <- exactProperties "QM-EXPECTED-SHAPE" "/candidate/expectedIds" expectedNames expectedElement @ findings
            let mutable embeddedByCategory = Map.empty
            for name in expectedNames do
                let values, next = arrayProperty "QM-EXPECTED-IDS" "/candidate/expectedIds" name expectedElement findings
                findings <- next
                let mutable ids = []
                for index, value in values |> List.indexed do
                    if value.ValueKind <> JsonValueKind.String then
                        findings <- finding "QM-EXPECTED-IDS" $"/candidate/expectedIds/%s{name}/%d{index}" "stable identifier" (value.ValueKind.ToString()) :: findings
                    else
                        ids <- value.GetString() :: ids
                let ids = List.rev ids
                if List.isEmpty ids || ids |> List.exists (isId >> not) || ids <> List.sort ids || ids.Length <> (ids |> List.distinct).Length then
                    findings <- finding "QM-EXPECTED-IDS" ("/candidate/expectedIds/" + name) "nonempty sorted unique stable identifiers" (String.concat "," ids) :: findings
                embeddedByCategory <- embeddedByCategory.Add(name, ids)
            let expectedByCategory = expectedPairs expected |> List.map (fun (name, ids) -> name, List.sort ids) |> Map.ofList
            for name in expectedNames do
                let embeddedIds = embeddedByCategory.TryFind name |> Option.defaultValue []
                let authoritativeIds = expectedByCategory.TryFind name |> Option.defaultValue [] |> List.sort
                if embeddedIds <> authoritativeIds then
                    findings <- finding "QM-INVENTORY-BINDING" ("/candidate/expectedIds/" + name) (String.concat "," authoritativeIds) (String.concat "," embeddedIds) :: findings
            let categories =
                [ "sources", "source"; "model", "quint-model"; "compiler", "compiler"
                  "dependencies", "dependency"; "generatedCases", "generated-case"
                  "independentCases", "independent-case"; "externalFixtures", "external-fixture"
                  "packages", "package" ]
            let mutable observedByCategory = Map.empty
            for category, kind in categories do
                let observed, next = validateContent category kind commit root findings
                findings <- next
                observedByCategory <- observedByCategory.Add(category, observed)
                let observedIds = observed |> List.map _.Id
                let expectedIds = expectedByCategory.TryFind category |> Option.defaultValue []
                if observedIds <> expectedIds then
                    findings <- finding "QM-CATEGORY-CLOSED" ("/" + category) (String.concat "," expectedIds) (String.concat "," observedIds) :: findings
            let environment = tryProperty "environment" root |> Option.defaultValue Unchecked.defaultof<JsonElement>
            findings <- exactProperties "QM-ENVIRONMENT-SHAPE" "/environment" [ "architecture"; "candidateSha"; "locale"; "networkMode"; "observedAt"; "os"; "producer"; "runtime"; "timezone" ] environment @ findings
            let environmentNames = [ "architecture"; "locale"; "networkMode"; "os"; "producer"; "runtime"; "timezone" ]
            let mutable environmentValues = Map.empty
            for name in environmentNames do
                let value, next = stringProperty "QM-ENVIRONMENT" "/environment" name environment findings
                findings <- next
                environmentValues <- environmentValues.Add(name, value)
                if String.IsNullOrWhiteSpace value then findings <- finding "QM-ENVIRONMENT" ("/environment/" + name) "non-empty string" "empty" :: findings
            let environmentCandidate, next = stringProperty "QM-CANDIDATE-BINDING" "/environment" "candidateSha" environment findings
            findings <- next
            if environmentCandidate <> commit then findings <- finding "QM-CANDIDATE-BINDING" "/environment/candidateSha" commit (if isNull environmentCandidate then "<missing>" else environmentCandidate) :: findings
            let environmentTimeRaw, next = stringProperty "QM-TIME" "/environment" "observedAt" environment findings
            findings <- next
            let environmentTime = parseTime environmentTimeRaw
            if environmentTime.IsNone then findings <- finding "QM-TIME" "/environment/observedAt" "canonical UTC second" (if isNull environmentTimeRaw then "<missing>" else environmentTimeRaw) :: findings
            if environmentValues.TryFind "networkMode" <> Some "isolated" then findings <- finding "QM-ENVIRONMENT" "/environment/networkMode" "isolated" (environmentValues.TryFind "networkMode" |> Option.defaultValue "<missing>") :: findings
            let mutable node = JsonNode.Parse(manifest.Span).AsObject()
            let inputNames =
                [ "compiler"; "dependencies"; "environment"; "externalFixtures"; "generatedCases"
                  "independentCases"; "model"; "packages"; "sources" ]
            let computedInputs =
                if inputNames |> List.forall (fun name -> not (isNull node[name])) then inputSetDigest node
                else "<incomplete-input-set>"
            if statedInputs <> computedInputs then findings <- finding "QM-INPUT-SET" "/candidate/inputSetSha256" computedInputs (if isNull statedInputs then "<missing>" else statedInputs) :: findings
            let resultElements, next = arrayProperty "QM-RESULTS" "" "results" root findings
            findings <- next
            if List.isEmpty resultElements then findings <- finding "QM-RESULTS-EMPTY" "/results" "at least one result" "empty" :: findings
            let resultProducers = HashSet<string>(StringComparer.Ordinal)
            let resultTimes = ResizeArray<DateTimeOffset>()
            let resultIds = HashSet<string>(StringComparer.Ordinal)
            let observedResultIds = ResizeArray<string>()
            let mutable previousResult = ""
            resultElements |> List.iteri (fun index entry ->
                let path = $"/results/%d{index}"
                findings <- exactProperties "QM-RESULT-SHAPE" path [ "candidateSha"; "completedAt"; "id"; "inputSetSha256"; "outcome"; "producer"; "qGate"; "sha256" ] entry @ findings
                let id, next = stringProperty "QM-RESULT-ID" path "id" entry findings
                findings <- next
                let gate, next = stringProperty "QM-RESULT-GATE" path "qGate" entry findings
                findings <- next
                let outcome, next = stringProperty "QM-RESULT-OUTCOME" path "outcome" entry findings
                findings <- next
                let digest, next = stringProperty "QM-RESULT-DIGEST" path "sha256" entry findings
                findings <- next
                let producer, next = stringProperty "QM-RESULT-PRODUCER" path "producer" entry findings
                findings <- next
                let candidateSha, next = stringProperty "QM-CANDIDATE-BINDING" path "candidateSha" entry findings
                findings <- next
                let inputs, next = stringProperty "QM-INPUT-SET-BINDING" path "inputSetSha256" entry findings
                findings <- next
                let completedRaw, next = stringProperty "QM-TIME" path "completedAt" entry findings
                findings <- next
                if not (isId id) || not (resultIds.Add id) then findings <- finding "QM-RESULT-ID" (path + "/id") "unique stable identifier" (if isNull id then "<missing>" else id) :: findings
                if index > 0 && String.CompareOrdinal(previousResult, id) >= 0 then findings <- finding "QM-RESULT-ORDER" (path + "/id") "ordinal id order" id :: findings
                previousResult <- if isNull id then previousResult else id
                if not (isNull id) then observedResultIds.Add id
                if not (stringPattern "^Q(?:[0-9]|10)$" gate) then findings <- finding "QM-RESULT-GATE" (path + "/qGate") "Q0..Q10" (if isNull gate then "<missing>" else gate) :: findings
                if outcome <> "passed" then findings <- finding "QM-RESULT-OUTCOME" (path + "/outcome") "passed" (if isNull outcome then "<missing>" else outcome) :: findings
                if not (isSha digest) then findings <- finding "QM-RESULT-DIGEST" (path + "/sha256") "lowercase SHA-256" (if isNull digest then "<missing>" else digest) :: findings
                if not (isId producer) then findings <- finding "QM-RESULT-PRODUCER" (path + "/producer") "stable principal id" (if isNull producer then "<missing>" else producer) :: findings else resultProducers.Add producer |> ignore
                if candidateSha <> commit then findings <- finding "QM-CANDIDATE-BINDING" (path + "/candidateSha") commit (if isNull candidateSha then "<missing>" else candidateSha) :: findings
                if inputs <> computedInputs then findings <- finding "QM-INPUT-SET-BINDING" (path + "/inputSetSha256") computedInputs (if isNull inputs then "<missing>" else inputs) :: findings
                match parseTime completedRaw with
                | Some value -> resultTimes.Add value
                | None -> findings <- finding "QM-TIME" (path + "/completedAt") "canonical UTC second" (if isNull completedRaw then "<missing>" else completedRaw) :: findings)
            let expectedResultIds = expectedByCategory.TryFind "results" |> Option.defaultValue []
            if List.ofSeq observedResultIds <> expectedResultIds then
                findings <- finding "QM-RESULTS-CLOSED" "/results" (String.concat "," expectedResultIds) (String.concat "," observedResultIds) :: findings
            let reviewElements, next = arrayProperty "QM-REVIEWS" "" "reviewers" root findings
            findings <- next
            if List.isEmpty reviewElements then findings <- finding "QM-REVIEWS-EMPTY" "/reviewers" "at least one independent reviewer" "empty" :: findings
            let reviewTimes = ResizeArray<DateTimeOffset>()
            let reviewIds = HashSet<string>(StringComparer.Ordinal)
            let observedReviewIds = ResizeArray<string>()
            let mutable previousReview = ""
            reviewElements |> List.iteri (fun index entry ->
                let path = $"/reviewers/%d{index}"
                findings <- exactProperties "QM-REVIEW-SHAPE" path [ "candidateSha"; "completedAt"; "id"; "inputSetSha256"; "outcome"; "principal"; "role"; "sha256" ] entry @ findings
                let id, next = stringProperty "QM-REVIEW-ID" path "id" entry findings
                findings <- next
                let role, next = stringProperty "QM-REVIEW-ROLE" path "role" entry findings
                findings <- next
                let outcome, next = stringProperty "QM-REVIEW-OUTCOME" path "outcome" entry findings
                findings <- next
                let digest, next = stringProperty "QM-REVIEW-DIGEST" path "sha256" entry findings
                findings <- next
                let principal, next = stringProperty "QM-REVIEW-PRINCIPAL" path "principal" entry findings
                findings <- next
                let candidateSha, next = stringProperty "QM-CANDIDATE-BINDING" path "candidateSha" entry findings
                findings <- next
                let inputs, next = stringProperty "QM-INPUT-SET-BINDING" path "inputSetSha256" entry findings
                findings <- next
                let completedRaw, next = stringProperty "QM-TIME" path "completedAt" entry findings
                findings <- next
                if not (isId id) || not (reviewIds.Add id) then findings <- finding "QM-REVIEW-ID" (path + "/id") "unique stable identifier" (if isNull id then "<missing>" else id) :: findings
                if index > 0 && String.CompareOrdinal(previousReview, id) >= 0 then findings <- finding "QM-REVIEW-ORDER" (path + "/id") "ordinal id order" id :: findings
                previousReview <- if isNull id then previousReview else id
                if not (isNull id) then observedReviewIds.Add id
                if not ([ "adapter"; "architecture"; "cutover"; "migration"; "security" ] |> List.contains role) then findings <- finding "QM-REVIEW-ROLE" (path + "/role") "closed review role" (if isNull role then "<missing>" else role) :: findings
                if outcome <> "accepted" then findings <- finding "QM-REVIEW-OUTCOME" (path + "/outcome") "accepted" (if isNull outcome then "<missing>" else outcome) :: findings
                if not (isSha digest) then findings <- finding "QM-REVIEW-DIGEST" (path + "/sha256") "lowercase SHA-256" (if isNull digest then "<missing>" else digest) :: findings
                if not (isId principal) then findings <- finding "QM-REVIEW-PRINCIPAL" (path + "/principal") "stable principal id" (if isNull principal then "<missing>" else principal) :: findings
                elif principal = candidateProducer || resultProducers.Contains principal then findings <- finding "QM-SELF-REVIEW" (path + "/principal") "principal distinct from candidate and result producers" principal :: findings
                if candidateSha <> commit then findings <- finding "QM-CANDIDATE-BINDING" (path + "/candidateSha") commit (if isNull candidateSha then "<missing>" else candidateSha) :: findings
                if inputs <> computedInputs then findings <- finding "QM-INPUT-SET-BINDING" (path + "/inputSetSha256") computedInputs (if isNull inputs then "<missing>" else inputs) :: findings
                match parseTime completedRaw with
                | Some value -> reviewTimes.Add value
                | None -> findings <- finding "QM-TIME" (path + "/completedAt") "canonical UTC second" (if isNull completedRaw then "<missing>" else completedRaw) :: findings)
            let expectedReviewIds = expectedByCategory.TryFind "reviewers" |> Option.defaultValue []
            if List.ofSeq observedReviewIds <> expectedReviewIds then
                findings <- finding "QM-REVIEWS-CLOSED" "/reviewers" (String.concat "," expectedReviewIds) (String.concat "," observedReviewIds) :: findings
            let generatedProducers = observedByCategory.TryFind "generatedCases" |> Option.defaultValue [] |> List.map _.Producer |> Set.ofList
            let independent = observedByCategory.TryFind "independentCases" |> Option.defaultValue []
            if independent |> List.exists (fun entry -> entry.Producer = candidateProducer || generatedProducers.Contains entry.Producer) then
                findings <- finding "QM-INDEPENDENCE" "/independentCases" "producer distinct from candidate and generated-case producers" "overlap" :: findings
            let inputTimes =
                [ for KeyValue(_, entries) in observedByCategory do
                      for entry in entries do yield! entry.ObservedAt |> Option.toList
                  yield! environmentTime |> Option.toList ]
            if not (List.isEmpty inputTimes) && not (Seq.isEmpty resultTimes) && (List.max inputTimes > Seq.min resultTimes) then
                findings <- finding "QM-TIME-ORDER" "/results" "results not earlier than inputs" "inverted" :: findings
            if not (Seq.isEmpty resultTimes) && not (Seq.isEmpty reviewTimes) && (Seq.max resultTimes > Seq.min reviewTimes) then
                findings <- finding "QM-TIME-ORDER" "/reviewers" "reviews not earlier than results" "inverted" :: findings
            match createdAt with
            | Some created when (inputTimes |> List.exists (fun value -> value > created)) || (resultTimes |> Seq.exists (fun value -> value > created)) || (reviewTimes |> Seq.exists (fun value -> value > created)) ->
                findings <- finding "QM-TIME-ORDER" "/createdAt" "not earlier than any entry" createdAtRaw :: findings
            | _ -> ()
            let statedDigest, next = stringProperty "QM-SELF-DIGEST" "" "digest" root findings
            findings <- next
            let computedDigest = cloneWithoutDigest node |> canonicalBytes |> sha256
            if statedDigest <> computedDigest then findings <- finding "QM-SELF-DIGEST" "/digest" computedDigest (if isNull statedDigest then "<missing>" else statedDigest) :: findings
            let canonical = Array.append (canonicalBytes node) [| byte '\n' |]
            if not (manifest.Span.SequenceEqual(ReadOnlySpan<byte>(canonical))) then
                findings <- finding "QM-CANONICAL" "/" "canonical JSON bytes" "non-canonical bytes" :: findings
            match findings |> List.rev |> List.distinct with
            | [] -> Ok canonical
            | errors -> Error errors
        with
        | :? JsonException as error -> Error [ finding "QM-JSON" "/" "valid JSON" error.Message ]
        | :? InvalidOperationException as error -> Error [ finding "QM-TYPE" "/" "valid manifest types" error.Message ]
        | :? NullReferenceException as error -> Error [ finding "QM-TYPE" "/" "complete manifest" error.Message ]

    let generateInventory (expected: QualificationManifestExpectedInventory) =
        let bytes = inventoryCanonicalBytes expected
        match parseInventory (ReadOnlyMemory<byte>(bytes)) with
        | Ok _ -> Ok bytes
        | Error findings -> Error findings

    let validate inventory manifest =
        match parseInventory inventory with
        | Ok(expected, canonical) -> validateManifest expected (sha256 canonical) manifest
        | Error findings -> Error findings

    let generate (input: QualificationManifestInput) =
        let inventory = inventoryCanonicalBytes input.Expected
        let root = manifestNode input
        let digest = cloneWithoutDigest root |> canonicalBytes |> sha256
        addString root "digest" digest
        let bytes = Array.append (canonicalBytes root) [| byte '\n' |]
        match validate (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(bytes)) with
        | Ok _ -> Ok bytes
        | Error findings -> Error findings
