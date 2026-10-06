#!/usr/bin/env dotnet fsi
open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Security.Cryptography

let refuse reason = failwith ("P4_MANIFEST_REFUSED " + reason)
let require condition reason = if not condition then refuse reason
let shaRx = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
let commitRx = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
let maxJson = 4 * 1024 * 1024
let requiredRoles = set ["provider-input-join";"candidate-receipt";"profile";"receiver-archive";"coordination-package";"sdd-package";"templates-package";"descriptor";"runtime-archive";"image-archive";"image-receipt"]
let manifestSchema = "fsgg.portable-p4-private-inputs/2"
let acquiredSchema = "fsgg.portable-p4-acquired-inputs/2"
let transportSchema = "fsgg.portable-p4-manifest-transport/2"
let repository = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
let coordinationRepository = "FS-GG/FS.GG.Coordination"
// One prospective input generation for the accepted dc934 receiver. Historical tags stay closed.
let releaseTag = "portable-p4-python-private-inputs-20261006-diag-source3"

let sha256Bytes (bytes: byte[]) = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
let sha256File path =
    use stream = File.OpenRead(path)
    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
let exactKeys (element: JsonElement) expected reason =
    require (element.ValueKind = JsonValueKind.Object) reason
    let actual = element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
    require (actual = Set.ofList expected) reason
let property (name: string) (element: JsonElement) =
    match element.TryGetProperty(name) with
    | true, value -> value
    | _ -> refuse ("missing-" + name)
let text (name: string) element =
    let value = property name element
    require (value.ValueKind = JsonValueKind.String) (name + "-type")
    value.GetString()
let positiveInt (name: string) element =
    let value = property name element
    let mutable result = 0L
    require (value.ValueKind = JsonValueKind.Number && value.TryGetInt64(&result) && result > 0L) (name + "-integer")
    result
let ensureHash (pattern: Regex) value reason =
    require (not (isNull value) && pattern.IsMatch(value)) reason
    value
let safeRelative value =
    require (not(String.IsNullOrEmpty value) && not(value.Contains('\\')) && not(Path.IsPathRooted value)) "path-refused"
    let parts = value.Split('/')
    require (parts |> Array.forall(fun part -> part <> "" && part <> "." && part <> "..")) "path-refused"
    value
let rec inspect (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        let names = HashSet<string>(StringComparer.Ordinal)
        for item in element.EnumerateObject() do
            require (names.Add item.Name) "duplicate-key"
            inspect item.Value
    | JsonValueKind.Array -> for item in element.EnumerateArray() do inspect item
    | JsonValueKind.Number ->
        let mutable value = 0L
        require (element.TryGetInt64(&value)) "non-integer-number"
    | JsonValueKind.String | JsonValueKind.True | JsonValueKind.False | JsonValueKind.Null -> ()
    | _ -> refuse "json-kind"
let parseBytes (raw: byte[]) =
    require (raw.Length > 0 && raw.Length <= maxJson) "json-size"
    require (not(raw.Length >= 3 && raw[0]=0xEFuy && raw[1]=0xBBuy && raw[2]=0xBFuy)) "utf8-bom"
    try
        let options = JsonDocumentOptions(CommentHandling=JsonCommentHandling.Disallow, AllowTrailingCommas=false)
        let document = JsonDocument.Parse(raw, options)
        inspect document.RootElement
        document
    with :? JsonException -> refuse "json-invalid"
let rec writeCanonical (writer: Utf8JsonWriter) (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        writer.WriteStartObject()
        element.EnumerateObject()
        |> Seq.sortWith(fun a b -> StringComparer.Ordinal.Compare(a.Name,b.Name))
        |> Seq.iter(fun item -> writer.WritePropertyName(item.Name); writeCanonical writer item.Value)
        writer.WriteEndObject()
    | JsonValueKind.Array ->
        writer.WriteStartArray()
        element.EnumerateArray() |> Seq.iter(writeCanonical writer)
        writer.WriteEndArray()
    | JsonValueKind.String -> writer.WriteStringValue(element.GetString())
    | JsonValueKind.Number -> writer.WriteNumberValue(element.GetInt64())
    | JsonValueKind.True -> writer.WriteBooleanValue(true)
    | JsonValueKind.False -> writer.WriteBooleanValue(false)
    | JsonValueKind.Null -> writer.WriteNullValue()
    | _ -> refuse "canonical-kind"
let canonical element =
    use memory = new MemoryStream()
    use writer = new Utf8JsonWriter(memory, JsonWriterOptions(Indented=false,SkipValidation=false))
    writeCanonical writer element
    writer.Flush()
    Array.append (memory.ToArray()) [|10uy|]

let validateManifest (root: JsonElement) expectedRelease expectedTarget expectedHelper expectedTree =
    exactKeys root ["schema";"classification";"release";"coordinationSource";"producers";"files";"privateProviderFactsRef"] "manifest-shape"
    require (text "schema" root = manifestSchema && text "classification" root = "public-candidate-files-only") "manifest-header"
    let release = property "release" root
    exactKeys release ["repository";"releaseId";"tag";"targetCommit"] "release-shape"
    require (text "repository" release = repository && positiveInt "releaseId" release = expectedRelease && text "tag" release = releaseTag && text "targetCommit" release = expectedTarget) "release-identity"
    let source = property "coordinationSource" root
    exactKeys source ["repository";"commit";"tree"] "source-shape"
    require (text "repository" source = coordinationRepository && text "commit" source = expectedHelper && text "tree" source = expectedTree) "source-identity"
    ensureHash commitRx (text "commit" source) "source-commit" |> ignore
    ensureHash commitRx (text "tree" source) "source-tree" |> ignore
    let producers = property "producers" root
    require (producers.ValueKind=JsonValueKind.Array && producers.GetArrayLength()>0 && producers.GetArrayLength()<=16) "producers"
    for producer in producers.EnumerateArray() do
        exactKeys producer ["repository";"runId";"runAttempt";"workflowPath";"headSha";"artifactId";"archiveSha256"] "producer-shape"
        require ((text "repository" producer).Contains('/')) "producer-repository"
        safeRelative (text "workflowPath" producer) |> ignore
        positiveInt "runId" producer |> ignore
        positiveInt "runAttempt" producer |> ignore
        positiveInt "artifactId" producer |> ignore
        ensureHash commitRx (text "headSha" producer) "producer-head" |> ignore
        ensureHash shaRx (text "archiveSha256" producer) "producer-hash" |> ignore
    let files = property "files" root
    require (files.ValueKind=JsonValueKind.Array && files.GetArrayLength()=11) "file-count"
    let roles = HashSet<string>()
    let names = HashSet<string>()
    let ids = HashSet<int64>()
    let mutable total = 0L
    for item in files.EnumerateArray() do
        exactKeys item ["path";"role";"assetId";"name";"bytes";"sha256";"producer"] "file-shape"
        let path = safeRelative(text "path" item)
        let role = text "role" item
        require (requiredRoles.Contains role && roles.Add role) "file-role"
        let id = positiveInt "assetId" item
        require (ids.Add id) "file-id"
        let size = positiveInt "bytes" item
        require (size < 2_000_000_000L) "file-size"
        total <- total + size
        let name = text "name" item
        require (name=Path.GetFileName(path) && names.Add name) "file-name"
        ensureHash shaRx (text "sha256" item) "file-hash" |> ignore
        let mutable index = 0L
        let producerValue = property "producer" item
        require (producerValue.ValueKind=JsonValueKind.Number && producerValue.TryGetInt64(&index) && index>=0L && index<int64(producers.GetArrayLength())) "file-producer"
    require (Set.ofSeq roles = requiredRoles && total <= 7L*1024L*1024L*1024L) "file-census"
    let reference = property "privateProviderFactsRef" root
    if reference.ValueKind <> JsonValueKind.Null then
        exactKeys reference ["sha256";"repository";"runId";"runAttempt";"artifactId";"capsuleSha256"] "facts-ref-shape"
        ensureHash shaRx (text "sha256" reference) "facts-ref-hash" |> ignore
        ensureHash shaRx (text "capsuleSha256" reference) "facts-ref-capsule" |> ignore
        require (text "repository" reference=repository) "facts-ref-repository"
        positiveInt "runId" reference |> ignore
        positiveInt "runAttempt" reference |> ignore
        positiveInt "artifactId" reference |> ignore
    files

let validateRoleFiles (files: JsonElement) (roles: JsonElement) assetsRoot =
    let expected = files.EnumerateArray() |> Seq.map(fun item -> text "role" item,item) |> Map.ofSeq
    require (roles.ValueKind=JsonValueKind.Object) "acquired-roles"
    require (roles.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq = requiredRoles) "acquired-role-set"
    let root = Path.GetFullPath(assetsRoot)
    for role in roles.EnumerateObject() do
        exactKeys role.Value ["path";"bytes";"sha256"] "acquired-role-shape"
        let item = expected[role.Name]
        require (positiveInt "bytes" role.Value=positiveInt "bytes" item && text "sha256" role.Value=text "sha256" item) "acquired-role-manifest"
        let path = Path.GetFullPath(text "path" role.Value)
        let expectedPath = Path.GetFullPath(Path.Combine(root,(safeRelative(text "path" item)).Replace('/',Path.DirectorySeparatorChar)))
        require (path=expectedPath && File.Exists(path)) "acquired-role-path"
        require (FileInfo(path).Length=positiveInt "bytes" role.Value && sha256File path=text "sha256" role.Value) "acquired-role-bytes"

let args = Environment.GetCommandLineArgs() |> Array.skip 3 |> Array.toList
let command, rest = match args with | command::rest -> command,rest | _ -> refuse "usage"
let rec parseOptions (values: string list) (result: Map<string,string>) =
    match values with
    | key::value::tail when key.StartsWith("--",StringComparison.Ordinal) -> parseOptions tail (Map.add key value result)
    | [] -> result
    | _ -> refuse "arguments"
let opts = parseOptions rest Map.empty
let required name = Map.tryFind name opts |> Option.defaultWith(fun () -> refuse("missing-"+name))
let output (bytes: byte[]) =
    let path = Path.GetFullPath(required "--output")
    require (not(File.Exists path)) "output-exists"
    File.WriteAllBytes(path,bytes)
let expectedRelease = Int64.Parse(required "--expected-release-id")
let target = ensureHash commitRx (required "--expected-target") "target"
let helper = ensureHash commitRx (required "--expected-helper") "helper"
let tree = ensureHash commitRx (required "--expected-helper-tree") "helper-tree"
let readInput () = File.ReadAllBytes(Path.GetFullPath(required "--input"))

match command with
| "construct" | "validate-manifest" ->
    use document = parseBytes(readInput())
    validateManifest document.RootElement expectedRelease target helper tree |> ignore
    output(canonical document.RootElement)
| "transport" ->
    use document = parseBytes(readInput())
    let root = document.RootElement
    exactKeys root ["schema";"releaseId";"manifestAssetId";"manifestSha256";"manifest";"assets";"roles"] "transport-shape"
    require (text "schema" root=transportSchema && positiveInt "releaseId" root=expectedRelease) "transport-header"
    let manifestId = positiveInt "manifestAssetId" root
    let manifestHash = ensureHash shaRx (text "manifestSha256" root) "manifest-hash"
    require (manifestId=Int64.Parse(required "--expected-manifest-asset-id")) "transport-manifest-id"
    require (manifestHash=ensureHash shaRx (required "--expected-manifest-sha256") "expected-manifest-hash") "transport-manifest-sha"
    let manifest = property "manifest" root
    let manifestBytes = canonical manifest
    require (sha256Bytes manifestBytes=manifestHash) "transport-manifest-hash"
    let files = validateManifest manifest expectedRelease target helper tree
    let assets = property "assets" root
    require (assets.ValueKind=JsonValueKind.Array && assets.GetArrayLength()=12) "transport-asset-count"
    let byId = Dictionary<int64,JsonElement>()
    let assetNames = HashSet<string>(StringComparer.Ordinal)
    for asset in assets.EnumerateArray() do
        exactKeys asset ["id";"name";"bytes";"sha256";"state"] "transport-asset-shape"
        let id = positiveInt "id" asset
        require (byId.TryAdd(id,asset)) "transport-asset-id"
        require (assetNames.Add(text "name" asset)) "transport-asset-name"
        require (text "state" asset = "uploaded") "transport-asset-state"
        positiveInt "bytes" asset |> ignore
        ensureHash shaRx (text "sha256" asset) "transport-asset-hash" |> ignore
    require (byId.ContainsKey manifestId) "transport-manifest-asset"
    let manifestAsset = byId[manifestId]
    require (text "name" manifestAsset="public-inputs-manifest.json" && positiveInt "bytes" manifestAsset=int64(manifestBytes.Length) && text "sha256" manifestAsset=manifestHash) "transport-manifest-asset-bytes"
    for item in files.EnumerateArray() do
        let id = positiveInt "assetId" item
        require (id<>manifestId && byId.ContainsKey id) "transport-role-asset"
        let asset = byId[id]
        require (text "name" asset=text "name" item && positiveInt "bytes" asset=positiveInt "bytes" item && text "sha256" asset=text "sha256" item) "transport-role-asset-bytes"
    let roles = property "roles" root
    validateRoleFiles files roles (required "--assets-root")
    use memory = new MemoryStream()
    use writer = new Utf8JsonWriter(memory)
    writer.WriteStartObject()
    writer.WriteString("schema",acquiredSchema)
    writer.WriteNumber("releaseId",expectedRelease)
    writer.WriteNumber("manifestAssetId",manifestId)
    writer.WriteString("manifestSha256",manifestHash)
    writer.WritePropertyName("manifest")
    manifest.WriteTo(writer)
    writer.WritePropertyName("roles")
    roles.WriteTo(writer)
    writer.WriteEndObject()
    writer.Flush()
    use acquired = parseBytes(memory.ToArray())
    output(canonical acquired.RootElement)
| "validate-acquired" ->
    let manifestId = Int64.Parse(required "--expected-manifest-asset-id")
    let manifestHash = ensureHash shaRx (required "--expected-manifest-sha256") "manifest-hash"
    use document = parseBytes(readInput())
    let root = document.RootElement
    exactKeys root ["schema";"releaseId";"manifestAssetId";"manifestSha256";"manifest";"roles"] "acquired-shape"
    require (text "schema" root=acquiredSchema && positiveInt "releaseId" root=expectedRelease && positiveInt "manifestAssetId" root=manifestId && text "manifestSha256" root=manifestHash) "acquired-tuple"
    let manifest = property "manifest" root
    require (sha256Bytes(canonical manifest)=manifestHash) "acquired-manifest-hash"
    let files = validateManifest manifest expectedRelease target helper tree
    validateRoleFiles files (property "roles" root) (required "--assets-root")
    output(canonical root)
| _ -> refuse "command"
