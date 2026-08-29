open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

let fail code detail =
    eprintfn "COMPILED_OUTPUT_RED code=%s detail=%s" code detail
    exit 1

let sha256Bytes (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let sha256File path = File.ReadAllBytes path |> sha256Bytes

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec parse root output remaining =
    match remaining with
    | [] -> root, output
    | "--root" :: value :: tail -> parse (Path.GetFullPath value) output tail
    | "--output" :: value :: tail -> parse root (Some value) tail
    | value :: _ -> fail "ARGUMENT" value

let root, outputOption = parse (Path.GetFullPath ".") None arguments
let sourcePath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
let contractPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/contract.json")
let authorityPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/typed-authority.json")
let receiptPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/receipt.json")

let outputRoot =
    outputOption
    |> Option.map (fun path ->
        if Path.IsPathRooted path then Path.GetFullPath path
        else Path.GetFullPath(Path.Combine(root, path)))
    |> Option.defaultValue (Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs"))

if not (File.Exists sourcePath) then fail "SOURCE-MISSING" sourcePath
if not (File.Exists contractPath) then fail "CONTRACT-MISSING" contractPath
if not (File.Exists authorityPath) then fail "AUTHORITY-MISSING" authorityPath
if not (File.Exists receiptPath) then fail "RECEIPT-MISSING" receiptPath

let sourceText = File.ReadAllText(sourcePath, Encoding.UTF8)
let sourceSha256 = sha256File sourcePath
let contractSha256 = sha256File contractPath
let contractDocument = JsonDocument.Parse(File.ReadAllBytes contractPath)
let contract = contractDocument.RootElement
let authorityDocument = JsonDocument.Parse(File.ReadAllBytes authorityPath)
let authority = authorityDocument.RootElement
let receiptDocument = JsonDocument.Parse(File.ReadAllBytes receiptPath)
let receipt = receiptDocument.RootElement

if contract.GetProperty("schema").GetString() <> "fsgg.quint.compiled-contract/v2" then
    fail "CONTRACT-SCHEMA" "wrong"

let profile = contract.GetProperty("profile").GetString()
if profile <> "fsgg-quint-profile/2" then fail "PROFILE" profile

let catalogue = contract.GetProperty("catalogue").EnumerateArray() |> Seq.toList

let recordField (entry: JsonElement) name =
    entry.GetProperty("value").GetProperty("fields").EnumerateArray()
    |> Seq.tryFind (fun field -> field.GetProperty("name").GetString() = name)
    |> Option.map (fun field -> field.GetProperty("value").GetProperty("value"))

let compiledSpecification =
    catalogue
    |> List.filter (fun entry -> entry.GetProperty("id").GetString() = "COUT-Specification")
    |> function
        | [ value ] -> value
        | values -> fail "SPECIFICATION" $"expected-one; actual={values.Length}"

let requiredString name =
    match recordField compiledSpecification name with
    | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | _ -> fail "SPECIFICATION-FIELD" name

let familyContract = requiredString "familyContract"
let identityContract = requiredString "identityContract"
let qualificationContract = requiredString "qualificationContract"
let projectionViewFormats = requiredString "projectionViewFormats"
let refusalContract = requiredString "refusalContract"
let normalizationAuthority = requiredString "normalizationAuthority"
let versionContract = requiredString "versionContract"
let semanticDiffContract = requiredString "semanticDiffContract"

if identityContract <> "family|ordinal|source|behavior|source-version|extractor-version|quint-version|profile-version|schema-version|contract|content" then fail "IDENTITY-CONTRACT" identityContract
if qualificationContract <> "supported|complete|fresh" then fail "QUALIFICATION-CONTRACT" qualificationContract
if projectionViewFormats <> "markdown|json" then fail "PROJECTION-FORMATS" projectionViewFormats
if refusalContract <> "missing|duplicate|substituted|unsupported|incomplete|reordered|stale" then
    fail "REFUSAL-CONTRACT" refusalContract
if normalizationAuthority <> "typed-effect-json" then fail "NORMALIZATION-AUTHORITY" normalizationAuthority
if semanticDiffContract <> "ordinal|json-pointer|value-sha256" then fail "SEMANTIC-DIFF-CONTRACT" semanticDiffContract

let versionParts = versionContract.Split('|')

if versionParts.Length <> 5 then fail "VERSION-CONTRACT" versionContract

let sourceVersion, extractorVersion, quintVersion, profileVersion, schemaVersion =
    versionParts[0], versionParts[1], versionParts[2], versionParts[3], versionParts[4]

if sourceVersion <> "fsgg.quint.literate-source/1" then fail "SOURCE-VERSION" sourceVersion
if extractorVersion <> "quint-specification-v1@FS.GG.SDD.Artifacts/1.5.0" then fail "EXTRACTOR-VERSION" extractorVersion
if quintVersion <> "sha256:939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f" then fail "QUINT-VERSION" quintVersion
if profileVersion <> profile then fail "PROFILE-VERSION" profileVersion
if schemaVersion <> contract.GetProperty("schema").GetString() then fail "SCHEMA-VERSION" schemaVersion

if authority.GetProperty("backend").GetString() <> "quint-specification-v1" then fail "AUTHORITY-BACKEND" "wrong"
if authority.GetProperty("packageIdentity").GetString() <> "FS.GG.SDD.Artifacts/1.5.0" then fail "AUTHORITY-PACKAGE" "wrong"
if authority.GetProperty("profileIdentity").GetString() <> profileVersion then fail "AUTHORITY-PROFILE" "wrong"
if receipt.GetProperty("schema").GetString() <> "fsgg.quint.observed-compilation-receipt/v2" then fail "RECEIPT-SCHEMA" "wrong"
if receipt.GetProperty("sourceSha256").GetString() <> sourceSha256 then fail "RECEIPT-SOURCE" "mismatch"
if receipt.GetProperty("contractSha256").GetString() <> contractSha256 then fail "RECEIPT-CONTRACT" "mismatch"

let behavioralSha256 = receipt.GetProperty("typedEffectSha256").GetString()

if String.IsNullOrWhiteSpace behavioralSha256 || behavioralSha256.Length <> 64 then
    fail "BEHAVIORAL-IDENTITY" behavioralSha256

let familyId name =
    match name with
    | "schemas" -> "COUT-Schemas"
    | "command-metadata" -> "COUT-CommandMetadata"
    | "permission-census" -> "COUT-PermissionCensus"
    | "mutation-census" -> "COUT-MutationCensus"
    | "settings-plans" -> "COUT-SettingsPlans"
    | "projection-views" -> "COUT-ProjectionViews"
    | "semantic-diff" -> "COUT-SemanticDiff"
    | "diagrams" -> "COUT-Diagrams"
    | "model-test-inventory" -> "COUT-ModelTestInventory"
    | value -> fail "UNKNOWN-FAMILY" value

let families =
    familyContract.Split('|', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun value ->
        match value.Split(':', 2) with
        | [| ordinal; name |] -> Int32.Parse ordinal, name, familyId name
        | _ -> fail "FAMILY-CONTRACT" value)
    |> Array.toList

if families |> List.map (fun (ordinal, _, _) -> ordinal) <> [ 1 .. 9 ] then fail "FAMILY-ORDER" familyContract
if families |> List.map (fun (_, _, id) -> id) |> Set.ofList |> Set.count <> 9 then fail "FAMILY-DUPLICATE" familyContract

let jsonOptions =
    JsonSerializerOptions(WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let clone (value: JsonElement) = JsonNode.Parse(value.GetRawText())
let array (values: seq<JsonNode>) = JsonArray(values |> Seq.toArray)
let stringNode (value: string) = JsonValue.Create(value) :> JsonNode

let versionsNode () =
    let versions = JsonObject()
    versions.Add("source", sourceVersion)
    versions.Add("extractor", extractorVersion)
    versions.Add("quint", quintVersion)
    versions.Add("profile", profileVersion)
    versions.Add("schema", schemaVersion)
    versions

let identityNode () =
    let identity = JsonObject()
    identity.Add("schema", "fsgg.quint.deterministic-identity/1")
    identity.Add("sourceSha256", sourceSha256)
    identity.Add("behavioralSha256", behavioralSha256)
    identity.Add("contractSha256", contractSha256)
    identity.Add("normalizationAuthority", normalizationAuthority)
    identity.Add("versions", versionsNode ())
    identity.Add("supported", true)
    identity.Add("complete", true)
    identity.Add("fresh", true)
    identity

let common (family: string) (ordinal: int) (content: JsonNode) =
    let root = JsonObject()
    root.Add("schema", "fsgg.quint.compiled-output/1")
    root.Add("family", family)
    root.Add("ordinal", ordinal)
    root.Add("sourceSha256", sourceSha256)
    root.Add("behavioralSha256", behavioralSha256)
    root.Add("profile", profile)
    root.Add("contractSha256", contractSha256)
    root.Add("identity", identityNode ())
    root.Add("supported", true)
    root.Add("complete", true)
    root.Add("fresh", true)
    root.Add("content", content)
    root

let writeText (relative: string) (text: string) =
    let path = Path.Combine(outputRoot, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text, UTF8Encoding(false))
    path

let writeJson (relative: string) (value: JsonNode) =
    writeText relative (value.ToJsonString jsonOptions + "\n")

let schemaRows =
    catalogue
    |> Seq.groupBy (fun entry -> entry.GetProperty("kind").GetString())
    |> Seq.sortBy fst
    |> Seq.map (fun (kind, entries) ->
        let fields =
            entries
            |> Seq.collect (fun entry ->
                entry.GetProperty("value").GetProperty("fields").EnumerateArray()
                |> Seq.map (fun field -> field.GetProperty("name").GetString()))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.map stringNode
            |> array

        let row = JsonObject()
        row.Add("kind", kind)
        row.Add("fields", fields)
        row :> JsonNode)
    |> array

let schemasContent = JsonObject()
schemasContent.Add("recordShapes", schemaRows)

let commandContent = JsonObject()
commandContent.Add("actions", contract.GetProperty("actionEffects") |> clone)

let permissionPattern = Regex("requiredPermission:\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)
let permissions =
    permissionPattern.Matches sourceText
    |> Seq.cast<Match>
    |> Seq.map (fun item -> item.Groups[1].Value)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.map stringNode
    |> array

if permissions.Count <> 6 then fail "PERMISSION-CENSUS" $"expected-six; actual={permissions.Count}"
let permissionContent = JsonObject()
permissionContent.Add("requiredPermissions", permissions)

let mutationRows =
    catalogue
    |> Seq.filter (fun entry ->
        let id = entry.GetProperty("id").GetString()
        id.StartsWith("MUT-", StringComparison.Ordinal) || id.StartsWith("MOUT-", StringComparison.Ordinal))
    |> Seq.sortBy (fun entry -> entry.GetProperty("id").GetString())
    |> Seq.map clone
    |> array

if mutationRows.Count <> 16 then fail "MUTATION-CENSUS" $"expected-sixteen; actual={mutationRows.Count}"
let mutationContent = JsonObject()
mutationContent.Add("entries", mutationRows)

let desiredState =
    catalogue
    |> List.filter (fun entry -> entry.GetProperty("id").GetString() = "DSTATE-Specification")
    |> function
        | [ value ] -> clone value
        | values -> fail "SETTINGS-PLAN" $"expected-one; actual={values.Length}"

let settingsContent = JsonObject()
settingsContent.Add("specification", desiredState)
settingsContent.Add("requiredPermissions", permissions.DeepClone())

let projectionContent = JsonObject()
projectionContent.Add("catalogue", contract.GetProperty("catalogue") |> clone)
projectionContent.Add("relationships", contract.GetProperty("relationships") |> clone)
projectionContent.Add("actions", contract.GetProperty("actionEffects") |> clone)
projectionContent.Add("verificationProfiles", contract.GetProperty("verificationProfiles") |> clone)
projectionContent.Add("bounds", contract.GetProperty("bounds") |> clone)
projectionContent.Add("compatibility", contract.GetProperty("compatibility") |> clone)

let semanticDiffContent = JsonObject()
semanticDiffContent.Add("identityFields", identityContract.Split('|') |> Seq.map stringNode |> array)
semanticDiffContent.Add("qualificationFields", qualificationContract.Split('|') |> Seq.map stringNode |> array)
semanticDiffContent.Add("refusalKinds", refusalContract.Split('|') |> Seq.map stringNode |> array)
semanticDiffContent.Add("familyOrder", families |> Seq.map (fun (_, _, id) -> stringNode id) |> array)

let pointerEscape (value: string) = value.Replace("~", "~0").Replace("/", "~1")

let rec semanticLeaves path (value: JsonElement) =
    seq {
        match value.ValueKind with
        | JsonValueKind.Object ->
            let properties = value.EnumerateObject() |> Seq.sortBy (fun property -> property.Name) |> Seq.toList
            if List.isEmpty properties then yield path, value.GetRawText()
            else
                for property in properties do
                    yield! semanticLeaves ($"{path}/{pointerEscape property.Name}") property.Value
        | JsonValueKind.Array ->
            let items = value.EnumerateArray() |> Seq.toList
            if List.isEmpty items then yield path, value.GetRawText()
            else
                for index, item in items |> List.indexed do
                    yield! semanticLeaves ($"{path}/{index}") item
        | _ -> yield path, value.GetRawText()
    }

let semanticRows =
    seq {
        yield "/behavioralSha256", JsonSerializer.Serialize(behavioralSha256)
        yield! semanticLeaves "" contract
    }
    |> Seq.sortBy fst
    |> Seq.mapi (fun index (path, rawValue) ->
        let row = JsonObject()
        row.Add("ordinal", JsonValue.Create(index + 1))
        row.Add("path", JsonValue.Create(if String.IsNullOrEmpty path then "/" else path))
        row.Add("valueSha256", JsonValue.Create(Encoding.UTF8.GetBytes(rawValue) |> sha256Bytes))
        row :> JsonNode)
    |> array

semanticDiffContent.Add("rows", semanticRows)

let relationshipLines =
    contract.GetProperty("relationships").EnumerateArray()
    |> Seq.map (fun edge ->
        let clean (value: string) = Regex.Replace(value, "[^A-Za-z0-9_]", "_")
        let fromId = edge.GetProperty("from").GetString()
        let toId = edge.GetProperty("to").GetString()
        let kind = edge.GetProperty("kind").GetString()
        $"  {clean fromId}[\"{fromId}\"] -->|{kind}| {clean toId}[\"{toId}\"]")
    |> String.concat "\n"

let diagrams =
    $"# Compiled contract diagrams\n\nSource: `{sourceSha256}`\n\nBehavior: `{behavioralSha256}`\n\nContract: `{contractSha256}`\n\n```mermaid\ngraph LR\n{relationshipLines}\n```\n\n```mermaid\nflowchart LR\n  Inspect --> Plan\n  Plan --> Apply\n  Plan --> Verify\n  Apply --> Verify\n```\n"

let testNames =
    Regex.Matches(sourceText, "\\brun\\s+(test[A-Za-z0-9_]+)\\s*=", RegexOptions.CultureInvariant)
    |> Seq.cast<Match>
    |> Seq.map (fun item -> item.Groups[1].Value)
    |> Seq.distinct
    |> Seq.sort
    |> Seq.map stringNode
    |> array

let modelInventoryContent = JsonObject()
modelInventoryContent.Add("tests", testNames)
modelInventoryContent.Add("verificationProfiles", contract.GetProperty("verificationProfiles") |> clone)

Directory.CreateDirectory(outputRoot) |> ignore

let actionCount = contract.GetProperty("actionEffects").GetArrayLength()
let relationshipCount = contract.GetProperty("relationships").GetArrayLength()
let verificationCount = contract.GetProperty("verificationProfiles").GetArrayLength()
let boundCount = contract.GetProperty("bounds").GetArrayLength()
let projectionMarkdown =
    $"# Compiled contract projection\n\nSource: `{sourceSha256}`\n\nBehavior: `{behavioralSha256}`\n\nProfile: `{profile}`\n\nContract: `{contractSha256}`\n\n- Catalogue entries: {catalogue.Length}\n- Actions: {actionCount}\n- Relationships: {relationshipCount}\n- Verification profiles: {verificationCount}\n- Bounds: {boundCount}\n"

let outputs =
    [ ("COUT-Schemas", 1, [ ("schemas.json", writeJson "schemas.json" (common "COUT-Schemas" 1 schemasContent)) ])
      ("COUT-CommandMetadata", 2, [ ("command-metadata.json", writeJson "command-metadata.json" (common "COUT-CommandMetadata" 2 commandContent)) ])
      ("COUT-PermissionCensus", 3, [ ("permission-census.json", writeJson "permission-census.json" (common "COUT-PermissionCensus" 3 permissionContent)) ])
      ("COUT-MutationCensus", 4, [ ("mutation-census.json", writeJson "mutation-census.json" (common "COUT-MutationCensus" 4 mutationContent)) ])
      ("COUT-SettingsPlans", 5, [ ("settings-plans.json", writeJson "settings-plans.json" (common "COUT-SettingsPlans" 5 settingsContent)) ])
      ("COUT-ProjectionViews", 6,
        [ ("projection-view.json", writeJson "projection-view.json" (common "COUT-ProjectionViews" 6 projectionContent))
          ("projection-view.md", writeText "projection-view.md" projectionMarkdown) ])
      ("COUT-SemanticDiff", 7, [ ("semantic-diff.json", writeJson "semantic-diff.json" (common "COUT-SemanticDiff" 7 semanticDiffContent)) ])
      ("COUT-Diagrams", 8, [ ("diagrams.md", writeText "diagrams.md" diagrams) ])
      ("COUT-ModelTestInventory", 9, [ ("model-test-inventory.json", writeJson "model-test-inventory.json" (common "COUT-ModelTestInventory" 9 modelInventoryContent)) ]) ]

if (outputs |> List.map (fun (family, _, _) -> family)) <> (families |> List.map (fun (_, _, family) -> family)) then
    fail "OUTPUT-FAMILY-ORDER" "generator differs from authority"

let manifestEntries =
    outputs
    |> Seq.map (fun (family, ordinal, files) ->
        let row = JsonObject()
        row.Add("family", family)
        row.Add("ordinal", ordinal)
        row.Add("sourceSha256", sourceSha256)
        row.Add("behavioralSha256", behavioralSha256)
        row.Add("profile", profile)
        row.Add("contractSha256", contractSha256)
        row.Add("identity", identityNode ())
        row.Add("supported", true)
        row.Add("complete", true)
        row.Add("fresh", true)
        row.Add(
            "files",
            files
            |> Seq.map (fun (relative, path) ->
                let file = JsonObject()
                file.Add("path", relative)
                file.Add("contentSha256", sha256File path)
                file :> JsonNode)
            |> array
        )
        row :> JsonNode)
    |> array

let manifest = JsonObject()
manifest.Add("schema", "fsgg.quint.compiled-output-manifest/1")
manifest.Add("sourceSha256", sourceSha256)
manifest.Add("behavioralSha256", behavioralSha256)
manifest.Add("profile", profile)
manifest.Add("contractSha256", contractSha256)
manifest.Add("identity", identityNode ())
manifest.Add("identityContract", identityContract)
manifest.Add("qualificationContract", qualificationContract)
manifest.Add("refusalContract", refusalContract)
manifest.Add("outputs", manifestEntries)
let manifestPath = writeJson "manifest.json" manifest

printfn "COMPILED_OUTPUT_OK manifest=%s source=%s contract=%s families=%d" (sha256File manifestPath) sourceSha256 contractSha256 outputs.Length
