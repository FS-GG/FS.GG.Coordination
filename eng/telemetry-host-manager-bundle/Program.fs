module FS.GG.Telemetry.Host.Manager.Bundle

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<CLIMutable>]
type FileBinding = { path: string; bytes: int64; sha256: string; mode: string }

let schema = "fsgg.coordination.telemetry-host-manager-bundle/1"
let preparedSchema = "fsgg.coordination.telemetry-host-manager-bundle-prepared/1"
let repository = "FS-GG/FS.GG.Coordination"
let archiveRoot = "telemetry-host-manager-net10.0"
let manifestPath = archiveRoot + "/manifest.json"
let entrypoint = archiveRoot + "/TelemetryHostManager.dll"
let installationRoot = "/opt/fsgg/telemetry-host-manager"
let supportedSdk = "10.0.400"
let supportedRuntime = "10.0.12"
let maxFiles = 4096
let maxFileBytes = 256L * 1024L * 1024L
let maxTotalBytes = 1024L * 1024L * 1024L
let maxManifestBytes = 1024 * 1024
let maxArchiveBytes = 1024L * 1024L * 1024L
let fixedTime = DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero)
let utf8 = UTF8Encoding(false)
let jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let refuse code detail = raise (InvalidOperationException(code + " " + detail))
let require condition code detail = if not condition then refuse code detail

let shaBytes (value: byte array) = Convert.ToHexString(SHA256.HashData value).ToLowerInvariant()
let shaFile path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let run command arguments workingDirectory =
    let start = ProcessStartInfo(command)
    start.WorkingDirectory <- workingDirectory
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in arguments do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    require (child.ExitCode = 0) "THMB-PROCESS" (command + " failed: " + error.Trim())
    output.Trim()

let rec writeCanonical (writer: Utf8JsonWriter) (value: JsonElement) =
    match value.ValueKind with
    | JsonValueKind.Object ->
        writer.WriteStartObject()
        value.EnumerateObject()
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Name,right.Name))
        |> Seq.iter(fun property -> writer.WritePropertyName property.Name; writeCanonical writer property.Value)
        writer.WriteEndObject()
    | JsonValueKind.Array ->
        writer.WriteStartArray()
        value.EnumerateArray() |> Seq.iter(writeCanonical writer)
        writer.WriteEndArray()
    | JsonValueKind.String -> writer.WriteStringValue(value.GetString())
    | JsonValueKind.Number -> writer.WriteRawValue(value.GetRawText())
    | JsonValueKind.True -> writer.WriteBooleanValue true
    | JsonValueKind.False -> writer.WriteBooleanValue false
    | JsonValueKind.Null -> writer.WriteNullValue()
    | _ -> refuse "THMB-JSON" "unsupported JSON token"

let canonicalElement (value: JsonElement) =
    use memory = new MemoryStream()
    use writer = new Utf8JsonWriter(memory, JsonWriterOptions(Indented=false))
    writeCanonical writer value
    writer.Flush()
    Array.append (memory.ToArray()) [|byte '\n'|]

let canonical value =
    let raw = JsonSerializer.SerializeToUtf8Bytes(value,jsonOptions)
    use document = JsonDocument.Parse(raw)
    canonicalElement document.RootElement

let rejectDuplicates (bytes: byte array) =
    let mutable reader = Utf8JsonReader(ReadOnlySpan bytes, JsonReaderOptions(MaxDepth=32))
    let scopes = Stack<HashSet<string>>()
    while reader.Read() do
        match reader.TokenType with
        | JsonTokenType.StartObject -> scopes.Push(HashSet<string>(StringComparer.Ordinal))
        | JsonTokenType.EndObject -> scopes.Pop() |> ignore
        | JsonTokenType.PropertyName ->
            require (scopes.Count > 0 && scopes.Peek().Add(reader.GetString())) "THMB-JSON" "duplicate property"
        | _ -> ()

let parseCanonical limit (bytes: byte array) =
    require (bytes.Length > 0 && bytes.Length <= limit) "THMB-JSON" "JSON size is outside the bound"
    rejectDuplicates bytes
    let document = JsonDocument.Parse(bytes,JsonDocumentOptions(MaxDepth=32))
    require (canonicalElement document.RootElement = bytes) "THMB-JSON" "JSON is not canonical"
    document

let propertyNames (value: JsonElement) =
    value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
let requireShape (expected: string list) (value: JsonElement) (code: string) =
    require (propertyNames value = Set.ofList expected) code "object shape differs"
let stringProperty (name: string) (value: JsonElement) =
    let property = value.GetProperty name
    require (property.ValueKind = JsonValueKind.String) "THMB-JSON" (name + " is not a string")
    property.GetString()
let int64Property (name: string) (value: JsonElement) =
    let property = value.GetProperty name
    require (property.ValueKind = JsonValueKind.Number) "THMB-JSON" (name + " is not a number")
    property.GetInt64()

let fullDirectory (path: string) (code: string) =
    let full = Path.GetFullPath path
    require (Path.IsPathFullyQualified full && Directory.Exists full) code "directory is absent"
    let rec verifyAncestors (current: string) =
        require ((File.GetAttributes current &&& FileAttributes.ReparsePoint) = enum 0) code "directory or ancestor is a link"
        let parent = Directory.GetParent current
        if not(isNull parent) then verifyAncestors parent.FullName
    verifyAncestors full
    full

let regularFiles (root: string) code =
    let root = fullDirectory root code
    let rec visit (directory: string) = seq {
        require ((File.GetAttributes directory &&& FileAttributes.ReparsePoint) = enum 0) code "directory is a link"
        for path in Directory.EnumerateFileSystemEntries directory do
            let attributes = File.GetAttributes path
            require ((attributes &&& FileAttributes.ReparsePoint) = enum 0) "THMB-LINK" "source tree contains a link"
            if (attributes &&& FileAttributes.Directory) <> enum 0 then
                yield! visit path
            else
                yield path
    }
    visit root

let safeRelative (value: string) =
    require (not(String.IsNullOrWhiteSpace value) && not(Path.IsPathRooted value) && not(value.Contains '\\') && not(value.Contains ':')) "THMB-PATH" "path is not portable relative form"
    let parts = value.Split('/',StringSplitOptions.None)
    require (parts |> Array.forall(fun part -> part <> "" && part <> "." && part <> "..")) "THMB-PATH" "path traverses"
    value

let enumerateFiles root prefix =
    let values = ResizeArray<FileBinding>()
    let mutable total = 0L
    for path in regularFiles root "THMB-PAYLOAD" do
        require (values.Count < maxFiles) "THMB-LIMIT" "file count exceeds 4096"
        let attributes = File.GetAttributes path
        require ((attributes &&& FileAttributes.ReparsePoint) = enum 0) "THMB-LINK" "source file is a link"
        let info = FileInfo path
        require (info.Length >= 0L && info.Length <= maxFileBytes) "THMB-LIMIT" "file exceeds 256 MiB"
        total <- total + info.Length
        require (total <= maxTotalBytes) "THMB-LIMIT" "files exceed 1 GiB"
        let relative = Path.GetRelativePath(root,path).Replace(Path.DirectorySeparatorChar,'/')
        safeRelative relative |> ignore
        let archivePath = if String.IsNullOrEmpty prefix then relative else prefix + "/" + relative
        let mode = if relative = "TelemetryHostManager" then "0555" else "0444"
        values.Add({path=archivePath;bytes=info.Length;sha256=shaFile path;mode=mode})
    values |> Seq.sortBy _.path |> Seq.toArray

let runtimeBinding (runtimeRoot: string) (path: string) =
    let attributes = File.GetAttributes path
    require ((attributes &&& FileAttributes.ReparsePoint) = enum 0) "THMB-LINK" "runtime file is a link"
    let info = FileInfo path
    require (info.Length >= 0L && info.Length <= maxFileBytes) "THMB-LIMIT" "runtime file exceeds 256 MiB"
    let relative = Path.GetRelativePath(runtimeRoot,path).Replace(Path.DirectorySeparatorChar,'/') |> safeRelative
    let mode = Convert.ToString(int(File.GetUnixFileMode path),8).PadLeft(4,'0')
    {path=relative;bytes=info.Length;sha256=shaFile path;mode=mode}

let runtimeFiles (runtimeRoot: string) (directory: string) =
    let values = ResizeArray<FileBinding>()
    let mutable total = 0L
    for path in regularFiles directory "THMB-RUNTIME" do
        require (values.Count < maxFiles) "THMB-LIMIT" "runtime file count exceeds 4096"
        let value = runtimeBinding runtimeRoot path
        total <- total + value.bytes
        require (total <= maxTotalBytes) "THMB-LIMIT" "runtime files exceed 1 GiB"
        values.Add value
    values.ToArray()

let validateRuntimeConfig publishRoot =
    let path = Path.Combine(publishRoot,"TelemetryHostManager.runtimeconfig.json")
    require (File.Exists path) "THMB-PAYLOAD" "runtimeconfig is absent"
    let bytes = File.ReadAllBytes path
    rejectDuplicates bytes
    use document = JsonDocument.Parse(bytes,JsonDocumentOptions(MaxDepth=8))
    let runtime = document.RootElement.GetProperty "runtimeOptions"
    let framework = runtime.GetProperty "framework"
    require (stringProperty "tfm" runtime = "net10.0" && stringProperty "name" framework = "Microsoft.NETCore.App" && stringProperty "version" framework = "10.0.0") "THMB-RUNTIME" "runtimeconfig profile differs"

let sourceFacts sourceRoot revision tree =
    let actualRevision = run "git" ["rev-parse";"HEAD"] sourceRoot
    let actualTree = run "git" ["rev-parse";"HEAD^{tree}"] sourceRoot
    require (actualRevision = revision && actualTree = tree) "THMB-SOURCE" "source revision or tree differs"
    let managerRelative = "eng/telemetry-host-manager"
    let buildInputs =
        [| "global.json"; "Directory.Build.props"; "Directory.Build.targets"; "Directory.Build.local.props"; "Directory.Packages.props"
           "eng/Directory.Build.props"; "eng/Directory.Build.targets"; "eng/Directory.Packages.props"
           managerRelative+"/Directory.Build.props"; managerRelative+"/Directory.Build.targets"; managerRelative+"/Directory.Packages.props"
           managerRelative+"/Program.fs"; managerRelative+"/TelemetryHostManager.fsproj"; managerRelative+"/packages.lock.json" |]
    let existingInputs = buildInputs |> Array.filter(fun relative -> File.Exists(Path.Combine(sourceRoot,relative)))
    let trackedInputs = run "git" (["ls-files";"--"] @ Array.toList buildInputs) sourceRoot
    let tracked = trackedInputs.Split('\n',StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
    require (existingInputs |> Array.forall tracked.Contains) "THMB-SOURCE" "untracked manager build input is present"
    let sourceStatus = run "git" (["status";"--porcelain=v1";"--untracked-files=all";"--"] @ Array.toList buildInputs) sourceRoot
    require (String.IsNullOrEmpty sourceStatus) "THMB-SOURCE" "tracked manager build input differs from the declared tree"
    let projectRelative = "eng/telemetry-host-manager/TelemetryHostManager.fsproj"
    let lockRelative = "eng/telemetry-host-manager/packages.lock.json"
    let project = Path.Combine(sourceRoot,projectRelative)
    let lockFile = Path.Combine(sourceRoot,lockRelative)
    require (File.Exists project && File.Exists lockFile) "THMB-SOURCE" "manager project or lockfile is absent"
    {| repository=repository; revision=revision; tree=tree
       project={|path=projectRelative;sha256=shaFile project|}
       lockFile={|path=lockRelative;sha256=shaFile lockFile|} |}

let runtimeFacts sourceRoot runtimeRoot runtimeVersion =
    require (runtimeVersion = supportedRuntime) "THMB-RUNTIME" "runtime version is unsupported"
    let sdk = run "dotnet" ["--version"] sourceRoot
    require (sdk = supportedSdk) "THMB-RUNTIME" "SDK version is unsupported"
    let host = Path.Combine(runtimeRoot,"dotnet")
    let fxrRoot = Path.Combine(runtimeRoot,"host/fxr",runtimeVersion)
    let frameworkRoot = Path.Combine(runtimeRoot,"shared/Microsoft.NETCore.App",runtimeVersion)
    require (File.Exists host && Directory.Exists fxrRoot && Directory.Exists frameworkRoot) "THMB-RUNTIME" "runtime closure is incomplete"
    let runtimeRows =
        Array.concat [|
            [|runtimeBinding runtimeRoot host|]
            runtimeFiles runtimeRoot fxrRoot
            runtimeFiles runtimeRoot frameworkRoot
        |]
        |> Array.sortBy _.path
    require (runtimeRows.Length <= maxFiles && (runtimeRows |> Array.sumBy _.bytes) <= maxTotalBytes) "THMB-LIMIT" "runtime closure exceeds bounds"
    require (runtimeRows |> Array.exists(fun row -> row.path = "host/fxr/"+runtimeVersion+"/libhostfxr.so")) "THMB-RUNTIME" "hostfxr is absent"
    for name in ["libhostpolicy.so";"libcoreclr.so";"libclrjit.so";"System.Private.CoreLib.dll"] do
        require (runtimeRows |> Array.exists(fun row -> row.path = "shared/Microsoft.NETCore.App/"+runtimeVersion+"/"+name)) "THMB-RUNTIME" (name+" is absent")
    {| dotnetRoot=runtimeRoot; sdkVersion=sdk; framework="Microsoft.NETCore.App"; frameworkVersion=runtimeVersion; rollForward="Disable"; files=runtimeRows |}

let validatePayloadRequirements (rows: FileBinding array) =
    let names = rows |> Array.map _.path |> Set.ofArray
    require (names.Count = rows.Length) "THMB-DUPLICATE" "payload path is duplicated"
    for name in ["TelemetryHostManager";"TelemetryHostManager.dll";"TelemetryHostManager.deps.json";"TelemetryHostManager.runtimeconfig.json";"FSharp.Core.dll"] do
        require (names.Contains(archiveRoot+"/"+name)) "THMB-PAYLOAD" (name+" is absent")
    require (rows |> Array.exists(fun row -> row.path = entrypoint && row.mode = "0444")) "THMB-PAYLOAD" "entrypoint DLL differs"

let makeManifest sourceRoot revision tree runtimeRoot runtimeVersion payloads =
    validatePayloadRequirements payloads
    let source = sourceFacts sourceRoot revision tree
    let runtime = runtimeFacts sourceRoot runtimeRoot runtimeVersion
    let fixedArgv = [|runtimeRoot+"/dotnet";"exec";"--fx-version";runtimeVersion;installationRoot+"/TelemetryHostManager.dll"|]
    {| schema=schema; source=source; runtime=runtime; archiveRoot=archiveRoot; installationRoot=installationRoot
       entrypoint=entrypoint; fixedArgv=fixedArgv; payloads=payloads |}

let zipMode mode =
    let permission = if mode = "0555" then 0o555 else 0o444
    (0o100000 ||| permission) <<< 16

let writeArchive path publishRoot (payloads: FileBinding array) manifestBytes =
    use stream = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)
    use archive = new ZipArchive(stream,ZipArchiveMode.Create,false,utf8)
    for row in payloads do
        let relative = row.path.Substring(archiveRoot.Length+1)
        let source = Path.Combine(publishRoot,relative.Replace('/',Path.DirectorySeparatorChar))
        let entry = archive.CreateEntry(row.path,CompressionLevel.SmallestSize)
        entry.LastWriteTime <- fixedTime
        entry.ExternalAttributes <- zipMode row.mode
        use target = entry.Open()
        use input = File.OpenRead source
        input.CopyTo target
    let manifestEntry = archive.CreateEntry(manifestPath,CompressionLevel.SmallestSize)
    manifestEntry.LastWriteTime <- fixedTime
    manifestEntry.ExternalAttributes <- zipMode "0444"
    use target = manifestEntry.Open()
    target.Write(manifestBytes,0,manifestBytes.Length)

let validateArchive archivePath expectedManifest =
    let info = FileInfo archivePath
    require (info.Exists && info.Length > 0L && info.Length <= maxArchiveBytes) "THMB-ARCHIVE" "archive is absent or too large"
    use stream = File.OpenRead archivePath
    use archive = new ZipArchive(stream,ZipArchiveMode.Read,false,utf8)
    let entries = archive.Entries |> Seq.toArray
    require (entries.Length > 1 && entries.Length <= maxFiles+1) "THMB-LIMIT" "archive entry count differs"
    let names = HashSet<string>(StringComparer.Ordinal)
    let mutable total = 0L
    for entry in entries do
        safeRelative entry.FullName |> ignore
        require (names.Add entry.FullName) "THMB-DUPLICATE" "archive path is duplicated"
        require (entry.Length >= 0L && entry.Length <= maxFileBytes) "THMB-LIMIT" "archive entry is too large"
        total <- total + entry.Length
        require (total <= maxTotalBytes) "THMB-LIMIT" "archive expands beyond 1 GiB"
        let mode = (entry.ExternalAttributes >>> 16) &&& 0xFFFF
        let expectedMode = if entry.FullName = archiveRoot+"/TelemetryHostManager" then 0o100555 else 0o100444
        require (mode = expectedMode) "THMB-LINK" "archive entry is not the expected regular-file mode"
        require (entry.LastWriteTime.DateTime = fixedTime.DateTime) "THMB-TIMESTAMP" "archive timestamp differs"
    let ordered = entries |> Array.map _.FullName
    let expectedOrder = Array.append (ordered |> Array.filter((<>) manifestPath) |> Array.sort) [|manifestPath|]
    require (ordered = expectedOrder) "THMB-LAYOUT" "archive entries are not ordered"
    let manifestEntry = entries |> Array.tryFind(fun entry -> entry.FullName = manifestPath)
    require manifestEntry.IsSome "THMB-MANIFEST" "embedded manifest is absent"
    let readEntry (entry: ZipArchiveEntry) =
        use input = entry.Open()
        use memory = new MemoryStream()
        input.CopyTo memory
        memory.ToArray()
    let embedded = readEntry manifestEntry.Value
    require (embedded = expectedManifest) "THMB-MANIFEST" "embedded manifest differs"
    let payloads =
        entries
        |> Array.filter(fun entry -> entry.FullName <> manifestPath)
        |> Array.map(fun entry ->
            let bytes = readEntry entry
            let mode = if entry.FullName = archiveRoot+"/TelemetryHostManager" then "0555" else "0444"
            {path=entry.FullName;bytes=int64 bytes.Length;sha256=shaBytes bytes;mode=mode})
    validatePayloadRequirements payloads
    let payloads = payloads |> Array.sortBy _.path
    use manifestDocument = parseCanonical maxManifestBytes expectedManifest
    let declared =
        manifestDocument.RootElement.GetProperty("payloads").EnumerateArray()
        |> Seq.map(fun value ->
            requireShape ["bytes";"mode";"path";"sha256"] value "THMB-PAYLOAD"
            {path=stringProperty "path" value;bytes=int64Property "bytes" value;sha256=stringProperty "sha256" value;mode=stringProperty "mode" value})
        |> Seq.sortBy _.path
        |> Seq.toArray
    require (payloads = declared) "THMB-PAYLOAD" "archive content differs from manifest"
    payloads

let prepare sourceRoot publishRoot runtimeRoot revision tree runtimeVersion output =
    let sourceRoot = fullDirectory sourceRoot "THMB-SOURCE"
    let publishRoot = fullDirectory publishRoot "THMB-PAYLOAD"
    let runtimeRoot = fullDirectory runtimeRoot "THMB-RUNTIME"
    validateRuntimeConfig publishRoot
    let payloads = enumerateFiles publishRoot archiveRoot
    let manifest = makeManifest sourceRoot revision tree runtimeRoot runtimeVersion payloads
    let manifestBytes = canonical manifest
    require (manifestBytes.Length <= maxManifestBytes) "THMB-MANIFEST" "manifest exceeds 1 MiB"
    Directory.CreateDirectory output |> ignore
    let archiveName = "telemetry-host-manager-net10.0-"+revision+".zip"
    let archivePath = Path.Combine(output,archiveName)
    require (not(File.Exists archivePath)) "THMB-OUTPUT" "archive already exists"
    writeArchive archivePath publishRoot payloads manifestBytes
    let observed = validateArchive archivePath manifestBytes
    require (observed = payloads) "THMB-PAYLOAD" "archive payload differs"
    File.WriteAllBytes(Path.Combine(output,"manifest.json"),manifestBytes)
    let receipt = {|schema=preparedSchema;archive={|file=archiveName;bytes=FileInfo(archivePath).Length;sha256=shaFile archivePath|}
                    manifestSha256=shaBytes manifestBytes;sourceRevision=revision;sourceTree=tree|} |> canonical
    File.WriteAllBytes(Path.Combine(output,"prepared.json"),receipt)
    archivePath

let verify sourceRoot runtimeRoot expectedRevision expectedTree preparedPath archiveOverride =
    let sourceRoot = fullDirectory sourceRoot "THMB-SOURCE"
    let runtimeRoot = fullDirectory runtimeRoot "THMB-RUNTIME"
    let preparedBytes = File.ReadAllBytes preparedPath
    use prepared = parseCanonical maxManifestBytes preparedBytes
    let root = prepared.RootElement
    requireShape ["archive";"manifestSha256";"schema";"sourceRevision";"sourceTree"] root "THMB-PREPARED"
    require (stringProperty "schema" root = preparedSchema && stringProperty "sourceRevision" root = expectedRevision && stringProperty "sourceTree" root = expectedTree) "THMB-PREPARED" "prepared identity differs"
    let archiveBinding = root.GetProperty "archive"
    requireShape ["bytes";"file";"sha256"] archiveBinding "THMB-PREPARED"
    let archiveName = stringProperty "file" archiveBinding
    require (archiveName = "telemetry-host-manager-net10.0-"+expectedRevision+".zip") "THMB-PREPARED" "archive name differs"
    let archivePath = defaultArg archiveOverride (Path.Combine(Path.GetDirectoryName preparedPath,archiveName))
    let archiveInfo = FileInfo archivePath
    require (archiveInfo.Exists && archiveInfo.Length = int64Property "bytes" archiveBinding && shaFile archivePath = stringProperty "sha256" archiveBinding) "THMB-PREPARED" "archive bytes differ"
    let manifestFile = Path.Combine(Path.GetDirectoryName preparedPath,"manifest.json")
    let manifestBytes = File.ReadAllBytes manifestFile
    require (shaBytes manifestBytes = stringProperty "manifestSha256" root) "THMB-PREPARED" "manifest digest differs"
    use manifestDocument = parseCanonical maxManifestBytes manifestBytes
    requireShape ["archiveRoot";"entrypoint";"fixedArgv";"installationRoot";"payloads";"runtime";"schema";"source"] manifestDocument.RootElement "THMB-MANIFEST"
    let archivedPayloads = validateArchive archivePath manifestBytes
    let reconstructed = makeManifest sourceRoot expectedRevision expectedTree runtimeRoot supportedRuntime archivedPayloads |> canonical
    require (reconstructed = manifestBytes) "THMB-MANIFEST" "source, runtime, or payload binding differs"

let parseArguments (argv: string array) =
    require (argv.Length > 0) "THMB-ARGUMENT" "command is required"
    let values = Dictionary<string,string>(StringComparer.Ordinal)
    let mutable index = 1
    while index < argv.Length do
        require (index+1 < argv.Length && argv[index].StartsWith("--",StringComparison.Ordinal)) "THMB-ARGUMENT" "option differs"
        require (values.TryAdd(argv[index],argv[index+1])) "THMB-ARGUMENT" "option is duplicated"
        index <- index + 2
    argv[0],values
let required name (values: Dictionary<string,string>) =
    match values.TryGetValue name with
    | true,value when not(String.IsNullOrWhiteSpace value) -> value
    | _ -> refuse "THMB-ARGUMENT" (name+" is required")

[<EntryPoint>]
let main argv =
    try
        let command,values = parseArguments argv
        match command with
        | "assemble" ->
            require (Set.ofSeq values.Keys = Set ["--source-root";"--publish";"--runtime-root";"--source-revision";"--source-tree";"--runtime-version";"--output"]) "THMB-ARGUMENT" "assemble options differ"
            let archive = prepare (required "--source-root" values) (required "--publish" values) (required "--runtime-root" values) (required "--source-revision" values) (required "--source-tree" values) (required "--runtime-version" values) (required "--output" values)
            printfn "TELEMETRY_HOST_MANAGER_BUNDLE_PREPARED archive=%s sha256=%s" (Path.GetFileName archive) (shaFile archive)
        | "verify" ->
            let allowed = Set ["--source-root";"--runtime-root";"--expected-revision";"--expected-tree";"--prepared";"--archive"]
            require (Set.isSubset (Set.ofSeq values.Keys) allowed) "THMB-ARGUMENT" "verify options differ"
            let archive = match values.TryGetValue "--archive" with | true,value -> Some value | _ -> None
            verify (required "--source-root" values) (required "--runtime-root" values) (required "--expected-revision" values) (required "--expected-tree" values) (required "--prepared" values) archive
            printfn "TELEMETRY_HOST_MANAGER_BUNDLE_VERIFIED"
        | _ -> refuse "THMB-ARGUMENT" "command differs"
        0
    with
    | :? InvalidOperationException as error ->
        eprintfn "%s" error.Message
        2
    | error ->
        eprintfn "THMB-INTERNAL %s" error.Message
        2
