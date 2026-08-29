module FS.GG.Coordination.Qualification.Contracts.GeneratedStructuralTests

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<Literal>]
let private Schema = "fsgg.quint.generated-structural-tests/1"

[<Literal>]
let private EvidenceClass = "generated-structural"

let private categoryOrder =
    [ "vocabulary"; "transition"; "command"; "mutation"; "permission"; "schema"; "projection" ]

type ValidationSummary =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      ManifestSha256: string
      CategoryCounts: (string * int) list
      TotalCount: int
      SelfSha256: string }

type private StructuralCase =
    { Category: string
      Key: string
      SourceArtifact: string
      SourceKey: string
      Derivation: string
      EvidenceClass: string
      SourceSha256: string }

type private QualifiedInputs =
    { SourceSha256: string
      BehavioralSha256: string
      ContractSha256: string
      ManifestSha256: string
      Contract: JsonElement
      CommandMetadata: JsonElement
      MutationCensus: JsonElement
      PermissionCensus: JsonElement
      Schemas: JsonElement
      ProjectionView: JsonElement
      Manifest: JsonElement }

let private sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private utf8 (value: string) = Encoding.UTF8.GetBytes value

let private rawSha256 (value: JsonElement) = value.GetRawText() |> utf8 |> sha256

let private canonicalJson (value: JsonElement) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
    let rec write (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            writer.WriteStartObject()
            element.EnumerateObject()
            |> Seq.sortBy _.Name
            |> Seq.iter (fun property -> writer.WritePropertyName(property.Name); write property.Value)
            writer.WriteEndObject()
        | JsonValueKind.Array ->
            writer.WriteStartArray()
            element.EnumerateArray() |> Seq.iter write
            writer.WriteEndArray()
        | JsonValueKind.String -> writer.WriteStringValue(element.GetString())
        | JsonValueKind.Number -> writer.WriteRawValue(element.GetRawText())
        | JsonValueKind.True -> writer.WriteBooleanValue true
        | JsonValueKind.False -> writer.WriteBooleanValue false
        | JsonValueKind.Null -> writer.WriteNullValue()
        | kind -> invalidOp $"unsupported JSON value kind %A{kind}"
    write value
    writer.Flush()
    stream.ToArray()

let private combine (root: string) (relative: string) = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))

let private readBytes root relative =
    let path = combine root relative
    if File.Exists path then Ok(File.ReadAllBytes path) else Error $"GST-INPUT-MISSING: %s{relative}"

let private parseJson code relative (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        Ok(document.RootElement.Clone())
    with error ->
        Error $"%s{code}: %s{relative}: %s{error.Message}"

let private tryProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

let private stringProperty code name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
    | _ -> Error $"%s{code}: missing string %s{name}"

let private boolProperty code name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.True -> Ok true
    | Some value when value.ValueKind = JsonValueKind.False -> Ok false
    | _ -> Error $"%s{code}: missing boolean %s{name}"

let private intProperty code name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt32() with
        | true, number -> Ok number
        | _ -> Error $"%s{code}: invalid integer %s{name}"
    | _ -> Error $"%s{code}: missing integer %s{name}"

let private arrayProperty code name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.Array -> Ok(value.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList)
    | _ -> Error $"%s{code}: missing array %s{name}"

let private requireEqual code label expected actual =
    if String.Equals(expected, actual, StringComparison.Ordinal) then Ok()
    else Error $"%s{code}: %s{label} expected %s{expected}, observed %s{actual}"

let private uniqueBy code label keyOf values =
    let seen = HashSet<string>(StringComparer.Ordinal)
    match values |> List.tryFind (fun value -> not (seen.Add(keyOf value))) with
    | Some duplicate -> Error $"%s{code}: duplicate %s{label} %s{keyOf duplicate}"
    | None -> Ok values

let private jsonRelative family =
    match family with
    | "COUT-CommandMetadata" -> Some "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/command-metadata.json"
    | "COUT-MutationCensus" -> Some "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/mutation-census.json"
    | "COUT-PermissionCensus" -> Some "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/permission-census.json"
    | "COUT-Schemas" -> Some "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/schemas.json"
    | "COUT-ProjectionViews" -> Some "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/projection-view.json"
    | _ -> None

let private loadQualified root =
    let contractRelative = "src/FS.GG.Coordination.Protocol/Generated/contract.json"
    let outputRoot = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs"
    let manifestRelative = outputRoot + "/manifest.json"

    match readBytes root contractRelative, readBytes root manifestRelative with
    | Error error, _ | _, Error error -> Error error
    | Ok contractBytes, Ok manifestBytes ->
        match parseJson "GST-INPUT-MALFORMED" contractRelative contractBytes, parseJson "GST-INPUT-MALFORMED" manifestRelative manifestBytes with
        | Error error, _ | _, Error error -> Error error
        | Ok contract, Ok manifest ->
            match stringProperty "GST-INPUT-SCHEMA" "schema" contract,
                  stringProperty "GST-INPUT-SCHEMA" "schema" manifest,
                  stringProperty "GST-INPUT-IDENTITY" "sourceSha256" manifest,
                  stringProperty "GST-INPUT-IDENTITY" "behavioralSha256" manifest,
                  stringProperty "GST-INPUT-IDENTITY" "contractSha256" manifest,
                  arrayProperty "GST-INPUT-MANIFEST" "outputs" manifest with
            | Ok contractSchema, Ok manifestSchema, Ok sourceSha, Ok behavioralSha, Ok contractSha, Ok outputs ->
                match requireEqual "GST-INPUT-SCHEMA" "contract schema" "fsgg.quint.compiled-contract/v2" contractSchema,
                      requireEqual "GST-INPUT-SCHEMA" "manifest schema" "fsgg.quint.compiled-output-manifest/1" manifestSchema,
                      requireEqual "GST-INPUT-DIGEST" "compiled contract digest" contractSha (sha256 contractBytes),
                      uniqueBy "GST-SOURCE-DUPLICATE" "compiled-output family" (fun output -> stringProperty "GST-INPUT-MANIFEST" "family" output |> Result.defaultValue "") outputs with
                | Error error, _, _, _ | _, Error error, _, _ | _, _, Error error, _ | _, _, _, Error error -> Error error
                | Ok (), Ok (), Ok (), Ok uniqueOutputs ->
                    let sortedOutputs =
                        uniqueOutputs
                        |> List.sortBy (fun output -> intProperty "GST-INPUT-MANIFEST" "ordinal" output |> Result.defaultValue Int32.MaxValue)

                    let expectedOrdinals = [ 1 .. sortedOutputs.Length ]
                    let observedOrdinals = sortedOutputs |> List.map (intProperty "GST-INPUT-MANIFEST" "ordinal" >> Result.defaultValue -1)

                    if observedOrdinals <> expectedOrdinals then
                        Error "GST-INPUT-MANIFEST: compiled-output ordinals are incomplete or non-canonical"
                    else
                        let validateOutput output =
                            match stringProperty "GST-INPUT-MANIFEST" "family" output,
                                  stringProperty "GST-INPUT-IDENTITY" "sourceSha256" output,
                                  stringProperty "GST-INPUT-IDENTITY" "behavioralSha256" output,
                                  stringProperty "GST-INPUT-IDENTITY" "contractSha256" output,
                                  boolProperty "GST-INPUT-STALE" "supported" output,
                                  boolProperty "GST-INPUT-STALE" "complete" output,
                                  boolProperty "GST-INPUT-STALE" "fresh" output,
                                  arrayProperty "GST-INPUT-MANIFEST" "files" output with
                            | Ok family, Ok outputSource, Ok outputBehavioral, Ok outputContract, Ok supported, Ok complete, Ok fresh, Ok files ->
                                match requireEqual "GST-INPUT-IDENTITY" $"%s{family} source" sourceSha outputSource,
                                      requireEqual "GST-INPUT-IDENTITY" $"%s{family} behavior" behavioralSha outputBehavioral,
                                      requireEqual "GST-INPUT-IDENTITY" $"%s{family} contract" contractSha outputContract with
                                | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error
                                | Ok (), Ok (), Ok () when not supported || not complete || not fresh -> Error $"GST-INPUT-STALE: %s{family} is unsupported, incomplete, or stale"
                                | Ok (), Ok (), Ok () ->
                                    match uniqueBy "GST-SOURCE-DUPLICATE" $"%s{family} file" (fun file -> stringProperty "GST-INPUT-MANIFEST" "path" file |> Result.defaultValue "") files with
                                    | Error error -> Error error
                                    | Ok uniqueFiles ->
                                        uniqueFiles
                                        |> List.fold (fun state file ->
                                            match state with
                                            | Error _ -> state
                                            | Ok () ->
                                                match stringProperty "GST-INPUT-MANIFEST" "path" file, stringProperty "GST-INPUT-MANIFEST" "contentSha256" file with
                                                | Ok path, Ok expectedSha ->
                                                    match readBytes root (outputRoot + "/" + path) with
                                                    | Error error -> Error error
                                                    | Ok bytes -> requireEqual "GST-INPUT-DIGEST" path expectedSha (sha256 bytes)
                                                | Error error, _ | _, Error error -> Error error) (Ok())
                            | Error error, _, _, _, _, _, _, _
                            | _, Error error, _, _, _, _, _, _
                            | _, _, Error error, _, _, _, _, _
                            | _, _, _, Error error, _, _, _, _
                            | _, _, _, _, Error error, _, _, _
                            | _, _, _, _, _, Error error, _, _
                            | _, _, _, _, _, _, Error error, _
                            | _, _, _, _, _, _, _, Error error -> Error error

                        match sortedOutputs |> List.fold (fun state output -> match state with Error _ -> state | Ok () -> validateOutput output) (Ok()) with
                        | Error error -> Error error
                        | Ok () ->
                            let loadOutput family =
                                match jsonRelative family with
                                | None -> Error $"GST-INPUT-MISSING: no typed output for %s{family}"
                                | Some relative ->
                                    match readBytes root relative with
                                    | Error error -> Error error
                                    | Ok bytes ->
                                        match parseJson "GST-INPUT-MALFORMED" relative bytes with
                                        | Error error -> Error error
                                        | Ok output ->
                                            match stringProperty "GST-INPUT-SCHEMA" "schema" output,
                                                  stringProperty "GST-INPUT-IDENTITY" "family" output,
                                                  stringProperty "GST-INPUT-IDENTITY" "sourceSha256" output,
                                                  stringProperty "GST-INPUT-IDENTITY" "behavioralSha256" output,
                                                  stringProperty "GST-INPUT-IDENTITY" "contractSha256" output,
                                                  boolProperty "GST-INPUT-STALE" "supported" output,
                                                  boolProperty "GST-INPUT-STALE" "complete" output,
                                                  boolProperty "GST-INPUT-STALE" "fresh" output with
                                            | Ok schema, Ok actualFamily, Ok actualSource, Ok actualBehavior, Ok actualContract, Ok supported, Ok complete, Ok fresh ->
                                                match requireEqual "GST-INPUT-SCHEMA" $"%s{family} schema" "fsgg.quint.compiled-output/1" schema,
                                                      requireEqual "GST-INPUT-IDENTITY" "typed output family" family actualFamily,
                                                      requireEqual "GST-INPUT-IDENTITY" $"%s{family} source" sourceSha actualSource,
                                                      requireEqual "GST-INPUT-IDENTITY" $"%s{family} behavior" behavioralSha actualBehavior,
                                                      requireEqual "GST-INPUT-IDENTITY" $"%s{family} contract" contractSha actualContract with
                                                | Error error, _, _, _, _
                                                | _, Error error, _, _, _
                                                | _, _, Error error, _, _
                                                | _, _, _, Error error, _
                                                | _, _, _, _, Error error -> Error error
                                                | Ok (), Ok (), Ok (), Ok (), Ok () when not supported || not complete || not fresh -> Error $"GST-INPUT-STALE: %s{family} typed output is unsupported, incomplete, or stale"
                                                | Ok (), Ok (), Ok (), Ok (), Ok () -> Ok output
                                            | Error error, _, _, _, _, _, _, _
                                            | _, Error error, _, _, _, _, _, _
                                            | _, _, Error error, _, _, _, _, _
                                            | _, _, _, Error error, _, _, _, _
                                            | _, _, _, _, Error error, _, _, _
                                            | _, _, _, _, _, Error error, _, _
                                            | _, _, _, _, _, _, Error error, _
                                            | _, _, _, _, _, _, _, Error error -> Error error

                            match loadOutput "COUT-CommandMetadata", loadOutput "COUT-MutationCensus", loadOutput "COUT-PermissionCensus", loadOutput "COUT-Schemas", loadOutput "COUT-ProjectionViews" with
                            | Ok commands, Ok mutations, Ok permissions, Ok schemas, Ok projection ->
                                let sameRaw code label (left: JsonElement) (right: JsonElement) =
                                    if ReadOnlySpan<byte>(canonicalJson left).SequenceEqual(ReadOnlySpan<byte>(canonicalJson right)) then Ok()
                                    else Error $"%s{code}: %s{label} does not agree with the qualified contract"

                                let registeredMutations =
                                    match tryProperty "content" mutations |> Option.bind (tryProperty "entries") with
                                    | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList
                                    | _ -> []
                                let contractMutations =
                                    match tryProperty "catalogue" contract with
                                    | Some value when value.ValueKind = JsonValueKind.Array ->
                                        value.EnumerateArray()
                                        |> Seq.filter (fun item ->
                                            match stringProperty "GST-INPUT-SCHEMA" "kind" item with
                                            | Ok "mutationKind" | Ok "mutationOutcome" -> true
                                            | _ -> false)
                                        |> Seq.map _.Clone()
                                        |> Seq.toList
                                    | _ -> []

                                let projectionPair contractName projectionName =
                                    match tryProperty contractName contract, tryProperty "content" projection |> Option.bind (tryProperty projectionName) with
                                    | Some left, Some right -> sameRaw "GST-PROJECTION-REGISTRATION" projectionName left right
                                    | _ -> Error $"GST-PROJECTION-REGISTRATION: missing %s{projectionName}"

                                match tryProperty "actionEffects" contract,
                                      tryProperty "content" commands |> Option.bind (tryProperty "actions"),
                                      uniqueBy "GST-SOURCE-DUPLICATE" "registered mutation" (fun item -> stringProperty "GST-INPUT-SCHEMA" "id" item |> Result.defaultValue "") registeredMutations,
                                      uniqueBy "GST-SOURCE-DUPLICATE" "contract mutation" (fun item -> stringProperty "GST-INPUT-SCHEMA" "id" item |> Result.defaultValue "") contractMutations with
                                | Some contractActions, Some commandActions, Ok registered, Ok expectedMutations ->
                                    let expectedMutationIds = expectedMutations |> List.map (fun item -> stringProperty "GST-INPUT-SCHEMA" "id" item |> Result.defaultValue "") |> List.sort
                                    let registeredMutationIds = registered |> List.map (fun item -> stringProperty "GST-INPUT-SCHEMA" "id" item |> Result.defaultValue "") |> List.sort
                                    match sameRaw "GST-COMMAND-REGISTRATION" "command actions" contractActions commandActions,
                                          (if expectedMutationIds = registeredMutationIds then Ok() else Error "GST-MUTATION-REGISTRATION: mutation census differs from the qualified catalogue"),
                                          projectionPair "catalogue" "catalogue",
                                          projectionPair "relationships" "relationships",
                                          projectionPair "actionEffects" "actions",
                                          projectionPair "verificationProfiles" "verificationProfiles",
                                          projectionPair "bounds" "bounds",
                                          projectionPair "compatibility" "compatibility" with
                                    | Ok (), Ok (), Ok (), Ok (), Ok (), Ok (), Ok (), Ok () ->
                                        Ok
                                            { SourceSha256 = sourceSha
                                              BehavioralSha256 = behavioralSha
                                              ContractSha256 = contractSha
                                              ManifestSha256 = sha256 manifestBytes
                                              Contract = contract
                                              CommandMetadata = commands
                                              MutationCensus = mutations
                                              PermissionCensus = permissions
                                              Schemas = schemas
                                              ProjectionView = projection
                                              Manifest = manifest }
                                    | Error error, _, _, _, _, _, _, _
                                    | _, Error error, _, _, _, _, _, _
                                    | _, _, Error error, _, _, _, _, _
                                    | _, _, _, Error error, _, _, _, _
                                    | _, _, _, _, Error error, _, _, _
                                    | _, _, _, _, _, Error error, _, _
                                    | _, _, _, _, _, _, Error error, _
                                    | _, _, _, _, _, _, _, Error error -> Error error
                                | None, _, _, _ | _, None, _, _ -> Error "GST-COMMAND-REGISTRATION: missing action registration"
                                | _, _, Error error, _ | _, _, _, Error error -> Error error
                            | Error error, _, _, _, _
                            | _, Error error, _, _, _
                            | _, _, Error error, _, _
                            | _, _, _, Error error, _
                            | _, _, _, _, Error error -> Error error
            | Error error, _, _, _, _, _
            | _, Error error, _, _, _, _
            | _, _, Error error, _, _, _
            | _, _, _, Error error, _, _
            | _, _, _, _, Error error, _
            | _, _, _, _, _, Error error -> Error error

let private makeCase category key artifact sourceKey derivation source =
    { Category = category
      Key = $"%s{category}:%s{key}"
      SourceArtifact = artifact
      SourceKey = sourceKey
      Derivation = derivation
      EvidenceClass = EvidenceClass
      SourceSha256 = rawSha256 source }

let private contentArray name output =
    match tryProperty "content" output |> Option.bind (tryProperty name) with
    | Some value when value.ValueKind = JsonValueKind.Array -> Ok(value.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList)
    | _ -> Error $"GST-INPUT-SCHEMA: missing content.%s{name}"

let private deriveForGeneration inputs =
    let contractPath = "src/FS.GG.Coordination.Protocol/Generated/contract.json"
    let outputPath name = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/" + name
    let getKey property value = stringProperty "GST-INPUT-SCHEMA" property value

    match arrayProperty "GST-INPUT-SCHEMA" "catalogue" inputs.Contract,
          arrayProperty "GST-INPUT-SCHEMA" "actionEffects" inputs.Contract,
          contentArray "actions" inputs.CommandMetadata,
          contentArray "entries" inputs.MutationCensus,
          contentArray "requiredPermissions" inputs.PermissionCensus,
          contentArray "recordShapes" inputs.Schemas,
          arrayProperty "GST-INPUT-MANIFEST" "outputs" inputs.Manifest with
    | Ok catalogue, Ok transitions, Ok commands, Ok mutations, Ok permissions, Ok schemas, Ok projections ->
        let build property category artifact derivation values =
            values
            |> List.map (fun value -> getKey property value |> Result.map (fun key -> makeCase category key artifact key derivation value))

        let permissionCases =
            permissions
            |> List.map (fun value ->
                if value.ValueKind = JsonValueKind.String then
                    let key = value.GetString()
                    Ok(makeCase "permission" key (outputPath "permission-census.json") key "required-permission-entry" value)
                else Error "GST-INPUT-SCHEMA: permission entry is not a string")

        let all =
            [ build "id" "vocabulary" contractPath "catalogue-entry" catalogue
              build "actionId" "transition" contractPath "action-effect-registration" transitions
              build "actionId" "command" (outputPath "command-metadata.json") "command-action-registration" commands
              build "id" "mutation" (outputPath "mutation-census.json") "mutation-entry-registration" mutations
              permissionCases
              build "kind" "schema" (outputPath "schemas.json") "record-shape-round-trip" schemas
              build "family" "projection" (outputPath "manifest.json") "compiled-output-freshness" projections ]
            |> List.concat

        match all |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
        | Some error -> Error error
        | None ->
            all
            |> List.choose (function Ok value -> Some value | Error _ -> None)
            |> List.sortBy (fun value -> List.findIndex ((=) value.Category) categoryOrder, value.Key)
            |> uniqueBy "GST-SOURCE-DUPLICATE" "structural case" _.Key
    | Error error, _, _, _, _, _, _
    | _, Error error, _, _, _, _, _
    | _, _, Error error, _, _, _, _
    | _, _, _, Error error, _, _, _
    | _, _, _, _, Error error, _, _
    | _, _, _, _, _, Error error, _
    | _, _, _, _, _, _, Error error -> Error error

let private deriveForValidation inputs =
    // Deliberately reconstructed independently of deriveForGeneration: validation walks each live
    // source category and appends an exact expected case rather than parsing producer totals.
    let cases = ResizeArray<StructuralCase>()
    let appendObjects category artifact derivation arrayName keyName container =
        match arrayName container with
        | Error error -> Error error
        | Ok values ->
            values
            |> List.fold (fun state value ->
                match state with
                | Error _ -> state
                | Ok () ->
                    match stringProperty "GST-INPUT-SCHEMA" keyName value with
                    | Error error -> Error error
                    | Ok key -> cases.Add(makeCase category key artifact key derivation value); Ok()) (Ok())

    let contractArray name element = arrayProperty "GST-INPUT-SCHEMA" name element
    let outputArray name element = contentArray name element
    let outputPath name = "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/" + name
    let steps =
        [ fun () -> appendObjects "vocabulary" "src/FS.GG.Coordination.Protocol/Generated/contract.json" "catalogue-entry" (contractArray "catalogue") "id" inputs.Contract
          fun () -> appendObjects "transition" "src/FS.GG.Coordination.Protocol/Generated/contract.json" "action-effect-registration" (contractArray "actionEffects") "actionId" inputs.Contract
          fun () -> appendObjects "command" (outputPath "command-metadata.json") "command-action-registration" (outputArray "actions") "actionId" inputs.CommandMetadata
          fun () -> appendObjects "mutation" (outputPath "mutation-census.json") "mutation-entry-registration" (outputArray "entries") "id" inputs.MutationCensus
          fun () ->
              match outputArray "requiredPermissions" inputs.PermissionCensus with
              | Error error -> Error error
              | Ok values ->
                  values
                  |> List.fold (fun state value ->
                      match state with
                      | Error _ -> state
                      | Ok () when value.ValueKind <> JsonValueKind.String -> Error "GST-INPUT-SCHEMA: permission entry is not a string"
                      | Ok () ->
                          let key = value.GetString()
                          cases.Add(makeCase "permission" key (outputPath "permission-census.json") key "required-permission-entry" value)
                          Ok()) (Ok())
          fun () -> appendObjects "schema" (outputPath "schemas.json") "record-shape-round-trip" (outputArray "recordShapes") "kind" inputs.Schemas
          fun () -> appendObjects "projection" (outputPath "manifest.json") "compiled-output-freshness" (contractArray "outputs") "family" inputs.Manifest ]

    match steps |> List.fold (fun state step -> match state with Error _ -> state | Ok () -> step()) (Ok()) with
    | Error error -> Error error
    | Ok () ->
        cases
        |> Seq.toList
        |> List.sortBy (fun value -> List.findIndex ((=) value.Category) categoryOrder, value.Key)
        |> uniqueBy "GST-SOURCE-DUPLICATE" "expected structural case" _.Key

let private categoryCounts cases =
    categoryOrder |> List.map (fun category -> category, cases |> List.filter (_.Category >> (=) category) |> List.length)

let private serialize inputs cases (selfDigest: string) =
    use stream = new MemoryStream()
    let options = JsonWriterOptions(Indented = false)
    use writer = new Utf8JsonWriter(stream, options)
    writer.WriteStartObject()
    writer.WriteString("schema", Schema)
    writer.WriteString("sourceSha256", inputs.SourceSha256)
    writer.WriteString("behavioralSha256", inputs.BehavioralSha256)
    writer.WriteString("contractSha256", inputs.ContractSha256)
    writer.WriteString("manifestSha256", inputs.ManifestSha256)
    writer.WriteStartArray("categories")
    for category, count in categoryCounts cases do
        writer.WriteStartObject()
        writer.WriteString("id", category)
        writer.WriteNumber("count", count)
        writer.WriteEndObject()
    writer.WriteEndArray()
    writer.WriteStartArray("cases")
    for item in cases do
        writer.WriteStartObject()
        writer.WriteString("category", item.Category)
        writer.WriteString("key", item.Key)
        writer.WriteString("sourceArtifact", item.SourceArtifact)
        writer.WriteString("sourceKey", item.SourceKey)
        writer.WriteString("derivation", item.Derivation)
        writer.WriteString("evidenceClass", item.EvidenceClass)
        writer.WriteString("sourceSha256", item.SourceSha256)
        writer.WriteEndObject()
    writer.WriteEndArray()
    writer.WriteNumber("totalCount", cases.Length)
    writer.WriteString("selfSha256", selfDigest)
    writer.WriteEndObject()
    writer.Flush()
    Array.append (stream.ToArray()) [| byte '\n' |]

let generate root =
    match loadQualified root with
    | Error error -> Error error
    | Ok inputs ->
        match deriveForGeneration inputs with
        | Error error -> Error error
        | Ok cases ->
            let digest = serialize inputs cases "" |> sha256
            Ok(serialize inputs cases digest)

let private parseArtifact bytes =
    match parseJson "GST-ARTIFACT-MALFORMED" "generated structural artifact" bytes with
    | Error error -> Error error
    | Ok root ->
        match stringProperty "GST-ARTIFACT-SCHEMA" "schema" root,
              stringProperty "GST-ARTIFACT-IDENTITY" "sourceSha256" root,
              stringProperty "GST-ARTIFACT-IDENTITY" "behavioralSha256" root,
              stringProperty "GST-ARTIFACT-IDENTITY" "contractSha256" root,
              stringProperty "GST-ARTIFACT-IDENTITY" "manifestSha256" root,
              arrayProperty "GST-CATEGORY" "categories" root,
              arrayProperty "GST-CASE-COUNT" "cases" root,
              intProperty "GST-CASE-COUNT" "totalCount" root,
              stringProperty "GST-SELF-DIGEST" "selfSha256" root with
        | Ok schema, Ok source, Ok behavior, Ok contract, Ok manifest, Ok categories, Ok cases, Ok total, Ok self ->
            Ok(schema, source, behavior, contract, manifest, categories, cases, total, self)
        | Error error, _, _, _, _, _, _, _, _
        | _, Error error, _, _, _, _, _, _, _
        | _, _, Error error, _, _, _, _, _, _
        | _, _, _, Error error, _, _, _, _, _
        | _, _, _, _, Error error, _, _, _, _
        | _, _, _, _, _, Error error, _, _, _
        | _, _, _, _, _, _, Error error, _, _
        | _, _, _, _, _, _, _, Error error, _
        | _, _, _, _, _, _, _, _, Error error -> Error error

let private parseCase element =
    match stringProperty "GST-CASE-SOURCE" "category" element,
          stringProperty "GST-CASE-SOURCE" "key" element,
          stringProperty "GST-CASE-SOURCE" "sourceArtifact" element,
          stringProperty "GST-CASE-SOURCE" "sourceKey" element,
          stringProperty "GST-CASE-SOURCE" "derivation" element,
          stringProperty "GST-EVIDENCE-CLASS" "evidenceClass" element,
          stringProperty "GST-CASE-SOURCE" "sourceSha256" element with
    | Ok category, Ok key, Ok artifact, Ok sourceKey, Ok derivation, Ok evidenceClass, Ok sourceSha ->
        Ok { Category = category; Key = key; SourceArtifact = artifact; SourceKey = sourceKey; Derivation = derivation; EvidenceClass = evidenceClass; SourceSha256 = sourceSha }
    | Error error, _, _, _, _, _, _
    | _, Error error, _, _, _, _, _
    | _, _, Error error, _, _, _, _
    | _, _, _, Error error, _, _, _
    | _, _, _, _, Error error, _, _
    | _, _, _, _, _, Error error, _
    | _, _, _, _, _, _, Error error -> Error error

let validate root artifactBytes =
    match loadQualified root, parseArtifact artifactBytes with
    | Error error, _ | _, Error error -> Error error
    | Ok inputs, Ok(schema, source, behavior, contract, manifest, categoryElements, caseElements, total, selfDigest) ->
        match requireEqual "GST-ARTIFACT-SCHEMA" "artifact schema" Schema schema,
              requireEqual "GST-ARTIFACT-IDENTITY" "source" inputs.SourceSha256 source,
              requireEqual "GST-ARTIFACT-IDENTITY" "behavior" inputs.BehavioralSha256 behavior,
              requireEqual "GST-ARTIFACT-IDENTITY" "contract" inputs.ContractSha256 contract,
              requireEqual "GST-ARTIFACT-IDENTITY" "manifest" inputs.ManifestSha256 manifest,
              deriveForValidation inputs with
        | Error error, _, _, _, _, _
        | _, Error error, _, _, _, _
        | _, _, Error error, _, _, _
        | _, _, _, Error error, _, _
        | _, _, _, _, Error error, _
        | _, _, _, _, _, Error error -> Error error
        | Ok (), Ok (), Ok (), Ok (), Ok (), Ok expected ->
            let parsedCases = caseElements |> List.map parseCase
            match parsedCases |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
            | Some error -> Error error
            | None ->
                let actual = parsedCases |> List.choose (function Ok value -> Some value | Error _ -> None)
                match uniqueBy "GST-CASE-DUPLICATE" "case key" _.Key actual with
                | Error error -> Error error
                | Ok actual when actual |> List.exists (_.EvidenceClass >> (<>) EvidenceClass) -> Error "GST-EVIDENCE-CLASS: every case must be generated-structural"
                | Ok actual when total <> actual.Length || actual.Length <> expected.Length -> Error $"GST-CASE-COUNT: expected %d{expected.Length}, observed %d{actual.Length}, total %d{total}"
                | Ok actual ->
                    let expectedKeys = expected |> List.map _.Key
                    let actualKeys = actual |> List.map _.Key
                    if Set.ofList expectedKeys <> Set.ofList actualKeys then Error "GST-CASE-SET: live and generated case keys differ"
                    elif expectedKeys <> actualKeys then Error "GST-CASE-ORDER: case keys are not in canonical order"
                    else
                        let mismatch = List.zip expected actual |> List.tryFind (fun (left, right) -> left <> right)
                        match mismatch with
                        | Some(left, _) -> Error $"GST-CASE-SOURCE: generated case differs from live source at %s{left.Key}"
                        | None ->
                            let parsedCategories =
                                categoryElements
                                |> List.map (fun value ->
                                    match stringProperty "GST-CATEGORY" "id" value, intProperty "GST-CATEGORY" "count" value with
                                    | Ok id, Ok count -> Ok(id, count)
                                    | Error error, _ | _, Error error -> Error error)
                            match parsedCategories |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
                            | Some error -> Error error
                            | None ->
                                let actualCategories = parsedCategories |> List.choose (function Ok value -> Some value | Error _ -> None)
                                let expectedCategories = categoryCounts expected
                                if actualCategories <> expectedCategories then Error "GST-CATEGORY: category order or counts differ from live sources"
                                else
                                    let canonicalWithoutDigest = serialize inputs expected ""
                                    let expectedSelf = sha256 canonicalWithoutDigest
                                    if not (String.Equals(expectedSelf, selfDigest, StringComparison.Ordinal)) then Error "GST-SELF-DIGEST: self digest does not match canonical source-bound bytes"
                                    else
                                        let canonical = serialize inputs expected expectedSelf
                                        if not (ReadOnlySpan<byte>(canonical).SequenceEqual(ReadOnlySpan<byte>(artifactBytes))) then Error "GST-CANONICAL: artifact bytes are not canonical"
                                        else
                                            Ok
                                                { SourceSha256 = source
                                                  BehavioralSha256 = behavior
                                                  ContractSha256 = contract
                                                  ManifestSha256 = manifest
                                                  CategoryCounts = expectedCategories
                                                  TotalCount = expected.Length
                                                  SelfSha256 = expectedSelf }

let write (root: string) (outputPath: string) =
    match generate root with
    | Error error -> Error error
    | Ok bytes ->
        let fullPath = if Path.IsPathRooted outputPath then outputPath else combine root outputPath
        let directory = Path.GetDirectoryName fullPath
        if not (String.IsNullOrEmpty directory) then Directory.CreateDirectory directory |> ignore
        File.WriteAllBytes(fullPath, bytes)
        validate root bytes

let check (root: string) (artifactPath: string) =
    let fullPath = if Path.IsPathRooted artifactPath then artifactPath else combine root artifactPath
    if not (File.Exists fullPath) then Error $"GST-INPUT-MISSING: %s{artifactPath}"
    else validate root (File.ReadAllBytes fullPath)
