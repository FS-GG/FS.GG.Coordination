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

[<CLIMutable>]
type OciInput =
    { image: string
      manifestDigest: string
      configDigest: string
      os: string
      architecture: string }

[<CLIMutable>]
type SelectionInput =
    { runtime: OciInput
      runtimeRoot: string
      runtimeCanonicalRoot: string
      sdk: OciInput
      sdkRoot: string
      sdkExecutable: string }

let schema = "fsgg.coordination.telemetry-host-manager-bundle/1"
let preparedSchema = "fsgg.coordination.telemetry-host-manager-bundle-prepared/1"
let schemaV2 = "fsgg.coordination.telemetry-host-manager-bundle/2"
let preparedSchemaV2 = "fsgg.coordination.telemetry-host-manager-bundle-prepared/2"
let repository = "FS-GG/FS.GG.Coordination"
let archiveRoot = "telemetry-host-manager-net10.0"
let manifestPath = archiveRoot + "/manifest.json"
let entrypoint = archiveRoot + "/TelemetryHostManager.dll"
let installationRoot = "/opt/fsgg/telemetry-host-manager"
let supportedSdk = "10.0.400"
let supportedRuntime = "10.0.12"
let selectedRuntimeImage = "mcr.microsoft.com/dotnet/aspnet"
let selectedRuntimeManifestDigest = "sha256:ed6a2d26633ddcd3d42a1d9f9866214ecbbc11ba6ac5e0e843da02c13da24072"
let selectedRuntimeConfigDigest = "sha256:d84f2a8aca8b8dbf142dd6bb1ffa7a1c051085c55bf36fc2f7fa1b1f17820932"
let selectedRuntimeTreeSha256 = "ead4ece42719198be9607d18415e428e3a6fcaadf50b88dc6e93894c47bec4c2"
let selectedSdkImage = "mcr.microsoft.com/dotnet/sdk"
let selectedSdkManifestDigest = "sha256:1aabdb4843de1c426d3676bf1220bc040e540f82a765320b3eb2c693e8d0a7dd"
let selectedSdkConfigDigest = "sha256:690de8d26a94a08b03190ccabf1906ac4025172267a1584255f062869caf8242"
let selectedSdkTreeSha256 = "c51a26bcd972e5f1b2944a912ca57cab9878fa88a2d8110fa0300c54ba0afcb0"
let selectedOs = "linux"
let selectedArchitecture = "amd64"
let canonicalRuntimeRoot = "/usr/share/dotnet"
let maxFiles = 4096
let maxSdkFiles = 32768
let maxFileBytes = 256L * 1024L * 1024L
let maxTotalBytes = 1024L * 1024L * 1024L
let maxSdkTotalBytes = 4L * 1024L * 1024L * 1024L
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

let modeString (path: string) = Convert.ToString(int(File.GetUnixFileMode path),8).PadLeft(4,'0')

let validateHardenedRuntimeTree runtimeRoot =
    let runtimeRoot = fullDirectory runtimeRoot "THMB-RUNTIME"
    let directories =
        seq {
            yield runtimeRoot
            yield! Directory.EnumerateDirectories(runtimeRoot,"*",SearchOption.AllDirectories)
        }
    for directory in directories do
        require ((File.GetAttributes directory &&& FileAttributes.ReparsePoint) = enum 0) "THMB-LINK" "runtime directory is a link"
        require (modeString directory = "0555") "THMB-RUNTIME-MODE" "runtime directory is not deterministic read-only 0555"
    for path in regularFiles runtimeRoot "THMB-RUNTIME" do
        let relative = Path.GetRelativePath(runtimeRoot,path).Replace(Path.DirectorySeparatorChar,'/')
        let expected = if relative = "dotnet" then "0555" else "0444"
        require (modeString path = expected) "THMB-RUNTIME-MODE" (relative+" mode differs from "+expected)

let validateNonWritableTree root =
    let root = fullDirectory root "THMB-SDK"
    let paths =
        seq {
            yield root
            yield! Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories)
            yield! regularFiles root "THMB-SDK"
        }
    for path in paths do
        let mode = int(File.GetUnixFileMode path)
        require ((mode &&& 0o022) = 0) "THMB-SDK" "SDK tree is group/other writable"

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

let validateOci expectedImage expectedManifest expectedConfig (value: OciInput) =
    require (value.image = expectedImage) "THMB-OCI" "OCI image repository differs"
    require (value.manifestDigest = expectedManifest) "THMB-OCI" "OCI manifest digest differs"
    require (value.configDigest = expectedConfig) "THMB-OCI" "OCI config digest differs"
    require (value.os = selectedOs && value.architecture = selectedArchitecture) "THMB-OCI" "OCI platform differs"

let sdkFacts sourceRoot (selection: SelectionInput) =
    validateOci selectedSdkImage selectedSdkManifestDigest selectedSdkConfigDigest selection.sdk
    let sdkRoot = fullDirectory selection.sdkRoot "THMB-SDK"
    let expectedExecutable = Path.Combine(sdkRoot,"dotnet")
    require (Path.GetFullPath selection.sdkExecutable = expectedExecutable && File.Exists expectedExecutable) "THMB-SDK" "SDK executable is not the selected tree host"
    require ((File.GetAttributes expectedExecutable &&& FileAttributes.ReparsePoint) = enum 0) "THMB-SDK" "SDK executable is a link"
    validateNonWritableTree sdkRoot
    let rows = ResizeArray<FileBinding>()
    let mutable total = 0L
    for path in regularFiles sdkRoot "THMB-SDK" do
        require (rows.Count < maxSdkFiles) "THMB-LIMIT" "SDK file count exceeds 32768"
        let row = runtimeBinding sdkRoot path
        total <- total + row.bytes
        require (total <= maxSdkTotalBytes) "THMB-LIMIT" "SDK tree exceeds 4 GiB"
        rows.Add row
    let rows = rows.ToArray() |> Array.sortBy _.path
    require (rows.Length > 0) "THMB-SDK" "SDK tree is empty"
    let treeSha256 = canonical rows |> shaBytes
    require (treeSha256 = selectedSdkTreeSha256) "THMB-SDK" "SDK tree differs from the selected immutable input"
    let executable = runtimeBinding sdkRoot expectedExecutable
    require (runtimeBinding sdkRoot expectedExecutable = executable) "THMB-SDK" "SDK executable changed before version readback"
    let sdk = run expectedExecutable ["--version"] sourceRoot
    require (runtimeBinding sdkRoot expectedExecutable = executable) "THMB-SDK" "SDK executable changed during version readback"
    require (sdk = supportedSdk) "THMB-SDK" "SDK version is unsupported"
    {| source=selection.sdk;canonicalRoot=canonicalRuntimeRoot;canonicalExecutable=canonicalRuntimeRoot+"/dotnet"
       version=sdk;executable=executable;fileCount=rows.Length;bytes=total
       treeSha256=treeSha256 |}

let runtimeFactsV2 (selection: SelectionInput) runtimeVersion =
    require (runtimeVersion = supportedRuntime) "THMB-RUNTIME" "runtime version is unsupported"
    validateOci selectedRuntimeImage selectedRuntimeManifestDigest selectedRuntimeConfigDigest selection.runtime
    require (selection.runtimeCanonicalRoot = canonicalRuntimeRoot) "THMB-RUNTIME" "canonical runtime root differs"
    let runtimeRoot = fullDirectory selection.runtimeRoot "THMB-RUNTIME"
    validateHardenedRuntimeTree runtimeRoot
    let host = Path.Combine(runtimeRoot,"dotnet")
    let fxrRoot = Path.Combine(runtimeRoot,"host/fxr",runtimeVersion)
    let frameworkRoot = Path.Combine(runtimeRoot,"shared/Microsoft.NETCore.App",runtimeVersion)
    let aspnetRoot = Path.Combine(runtimeRoot,"shared/Microsoft.AspNetCore.App",runtimeVersion)
    require (File.Exists host && Directory.Exists fxrRoot && Directory.Exists frameworkRoot && Directory.Exists aspnetRoot) "THMB-RUNTIME" "runtime closure is incomplete"
    let runtimeRows = runtimeFiles runtimeRoot runtimeRoot |> Array.sortBy _.path
    require (runtimeRows.Length <= maxFiles && (runtimeRows |> Array.sumBy _.bytes) <= maxTotalBytes) "THMB-LIMIT" "runtime closure exceeds bounds"
    let treeSha256 = canonical runtimeRows |> shaBytes
    require (treeSha256 = selectedRuntimeTreeSha256) "THMB-RUNTIME" "runtime tree differs from the selected immutable input"
    require (runtimeRows |> Array.exists(fun row -> row.path = "host/fxr/"+runtimeVersion+"/libhostfxr.so")) "THMB-RUNTIME" "hostfxr is absent"
    for name in ["libhostpolicy.so";"libcoreclr.so";"libclrjit.so";"System.Private.CoreLib.dll"] do
        require (runtimeRows |> Array.exists(fun row -> row.path = "shared/Microsoft.NETCore.App/"+runtimeVersion+"/"+name)) "THMB-RUNTIME" (name+" is absent")
    require (runtimeRows |> Array.exists(fun row -> row.path = "shared/Microsoft.AspNetCore.App/"+runtimeVersion+"/Microsoft.AspNetCore.dll")) "THMB-RUNTIME" "ASP.NET runtime is absent"
    let runtimes = run host ["--list-runtimes"] runtimeRoot
    require (runtimes.Contains("Microsoft.NETCore.App "+runtimeVersion,StringComparison.Ordinal) && runtimes.Contains("Microsoft.AspNetCore.App "+runtimeVersion,StringComparison.Ordinal)) "THMB-RUNTIME" "selected host does not resolve the required runtimes"
    {| source=selection.runtime;dotnetRoot=canonicalRuntimeRoot;framework="Microsoft.NETCore.App";frameworkVersion=runtimeVersion
       aspnetFramework="Microsoft.AspNetCore.App";aspnetFrameworkVersion=runtimeVersion;rollForward="Disable";treeSha256=treeSha256;files=runtimeRows |}

let validatePayloadRequirements (rows: FileBinding array) =
    let names = rows |> Array.map _.path |> Set.ofArray
    require (names.Count = rows.Length) "THMB-DUPLICATE" "payload path is duplicated"
    for name in ["TelemetryHostManager";"TelemetryHostManager.dll";"TelemetryHostManager.deps.json";"TelemetryHostManager.runtimeconfig.json";"FSharp.Core.dll"] do
        require (names.Contains(archiveRoot+"/"+name)) "THMB-PAYLOAD" (name+" is absent")
    require (rows |> Array.exists(fun row -> row.path = entrypoint && row.mode = "0444")) "THMB-PAYLOAD" "entrypoint DLL differs"

// The bundle/1 functions and CLI route intentionally retain the original wire shape and
// reconstruction behavior for historical artifacts. Hardened production uses bundle/2.
let runtimeFactsV1 sourceRoot runtimeRoot runtimeVersion =
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

let runtimeFacts sourceRoot runtimeRoot runtimeVersion = runtimeFactsV1 sourceRoot runtimeRoot runtimeVersion

let makeManifestV1 sourceRoot revision tree runtimeRoot runtimeVersion payloads =
    validatePayloadRequirements payloads
    let source = sourceFacts sourceRoot revision tree
    let runtime = runtimeFactsV1 sourceRoot runtimeRoot runtimeVersion
    let fixedArgv = [|runtimeRoot+"/dotnet";"exec";"--fx-version";runtimeVersion;installationRoot+"/TelemetryHostManager.dll"|]
    {| schema=schema; source=source; runtime=runtime; archiveRoot=archiveRoot; installationRoot=installationRoot
       entrypoint=entrypoint; fixedArgv=fixedArgv; payloads=payloads |}

let makeManifest sourceRoot revision tree runtimeRoot runtimeVersion payloads =
    makeManifestV1 sourceRoot revision tree runtimeRoot runtimeVersion payloads

let makeManifestV2 sourceRoot revision tree (selection: SelectionInput) runtimeVersion payloads =
    validatePayloadRequirements payloads
    let source = sourceFacts sourceRoot revision tree
    let sdk = sdkFacts sourceRoot selection
    let runtime = runtimeFactsV2 selection runtimeVersion
    let fixedArgv = [|canonicalRuntimeRoot+"/dotnet";"exec";"--fx-version";runtimeVersion;installationRoot+"/TelemetryHostManager.dll"|]
    {| schema=schemaV2; source=source;buildSdk=sdk;runtime=runtime; archiveRoot=archiveRoot; installationRoot=installationRoot
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

let prepareV1 sourceRoot publishRoot runtimeRoot revision tree runtimeVersion output =
    let sourceRoot = fullDirectory sourceRoot "THMB-SOURCE"
    let publishRoot = fullDirectory publishRoot "THMB-PAYLOAD"
    let runtimeRoot = fullDirectory runtimeRoot "THMB-RUNTIME"
    validateRuntimeConfig publishRoot
    let payloads = enumerateFiles publishRoot archiveRoot
    let manifestBytes = makeManifestV1 sourceRoot revision tree runtimeRoot runtimeVersion payloads |> canonical
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

let prepare sourceRoot publishRoot runtimeRoot revision tree runtimeVersion output =
    prepareV1 sourceRoot publishRoot runtimeRoot revision tree runtimeVersion output

let prepareV2 sourceRoot publishRoot (selection: SelectionInput) revision tree runtimeVersion output =
    let sourceRoot = fullDirectory sourceRoot "THMB-SOURCE"
    let publishRoot = fullDirectory publishRoot "THMB-PAYLOAD"
    validateRuntimeConfig publishRoot
    let payloads = enumerateFiles publishRoot archiveRoot
    let manifest = makeManifestV2 sourceRoot revision tree selection runtimeVersion payloads
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
    let receipt = {|schema=preparedSchemaV2;archive={|file=archiveName;bytes=FileInfo(archivePath).Length;sha256=shaFile archivePath|}
                    manifestSha256=shaBytes manifestBytes;runtimeManifestDigest=selection.runtime.manifestDigest
                    sdkManifestDigest=selection.sdk.manifestDigest;sourceRevision=revision;sourceTree=tree|} |> canonical
    File.WriteAllBytes(Path.Combine(output,"prepared.json"),receipt)
    archivePath

let verifyV1 sourceRoot runtimeRoot expectedRevision expectedTree preparedPath archiveOverride =
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
    let reconstructed = makeManifestV1 sourceRoot expectedRevision expectedTree runtimeRoot supportedRuntime archivedPayloads |> canonical
    require (reconstructed = manifestBytes) "THMB-MANIFEST" "source, runtime, or payload binding differs"

let verify sourceRoot runtimeRoot expectedRevision expectedTree preparedPath archiveOverride =
    verifyV1 sourceRoot runtimeRoot expectedRevision expectedTree preparedPath archiveOverride

let verifyV2 sourceRoot (selection: SelectionInput) expectedRevision expectedTree preparedPath archiveOverride =
    let sourceRoot = fullDirectory sourceRoot "THMB-SOURCE"
    let preparedBytes = File.ReadAllBytes preparedPath
    use prepared = parseCanonical maxManifestBytes preparedBytes
    let root = prepared.RootElement
    requireShape ["archive";"manifestSha256";"runtimeManifestDigest";"schema";"sdkManifestDigest";"sourceRevision";"sourceTree"] root "THMB-PREPARED"
    require (stringProperty "schema" root = preparedSchemaV2 && stringProperty "sourceRevision" root = expectedRevision && stringProperty "sourceTree" root = expectedTree) "THMB-PREPARED" "prepared identity differs"
    require (stringProperty "runtimeManifestDigest" root = selection.runtime.manifestDigest && stringProperty "sdkManifestDigest" root = selection.sdk.manifestDigest) "THMB-PREPARED" "selected OCI input differs"
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
    requireShape ["archiveRoot";"buildSdk";"entrypoint";"fixedArgv";"installationRoot";"payloads";"runtime";"schema";"source"] manifestDocument.RootElement "THMB-MANIFEST"
    let archivedPayloads = validateArchive archivePath manifestBytes
    require (stringProperty "schema" manifestDocument.RootElement = schemaV2) "THMB-MANIFEST" "manifest schema differs"
    let reconstructed = makeManifestV2 sourceRoot expectedRevision expectedTree selection supportedRuntime archivedPayloads |> canonical
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

let selectionOptionNames =
    Set ["--runtime-image";"--runtime-manifest-digest";"--runtime-config-digest";"--runtime-os";"--runtime-architecture"
         "--runtime-root";"--runtime-canonical-root";"--sdk-image";"--sdk-manifest-digest";"--sdk-config-digest"
         "--sdk-os";"--sdk-architecture";"--sdk-root";"--sdk-executable"]

let selectionFrom (values: Dictionary<string,string>) =
    { runtime=
        { image=required "--runtime-image" values
          manifestDigest=required "--runtime-manifest-digest" values
          configDigest=required "--runtime-config-digest" values
          os=required "--runtime-os" values
          architecture=required "--runtime-architecture" values }
      runtimeRoot=required "--runtime-root" values
      runtimeCanonicalRoot=required "--runtime-canonical-root" values
      sdk=
        { image=required "--sdk-image" values
          manifestDigest=required "--sdk-manifest-digest" values
          configDigest=required "--sdk-config-digest" values
          os=required "--sdk-os" values
          architecture=required "--sdk-architecture" values }
      sdkRoot=required "--sdk-root" values
      sdkExecutable=required "--sdk-executable" values }

[<EntryPoint>]
let main argv =
    try
        let command,values = parseArguments argv
        match command with
        | "assemble" ->
            require (Set.ofSeq values.Keys = Set ["--source-root";"--publish";"--runtime-root";"--source-revision";"--source-tree";"--runtime-version";"--output"]) "THMB-ARGUMENT" "assemble options differ"
            let archive = prepareV1 (required "--source-root" values) (required "--publish" values) (required "--runtime-root" values) (required "--source-revision" values) (required "--source-tree" values) (required "--runtime-version" values) (required "--output" values)
            printfn "TELEMETRY_HOST_MANAGER_BUNDLE_PREPARED archive=%s sha256=%s" (Path.GetFileName archive) (shaFile archive)
        | "verify" ->
            let allowed = Set ["--source-root";"--runtime-root";"--expected-revision";"--expected-tree";"--prepared";"--archive"]
            require (Set.isSubset (Set.ofSeq values.Keys) allowed) "THMB-ARGUMENT" "verify options differ"
            let archive = match values.TryGetValue "--archive" with | true,value -> Some value | _ -> None
            verifyV1 (required "--source-root" values) (required "--runtime-root" values) (required "--expected-revision" values) (required "--expected-tree" values) (required "--prepared" values) archive
            printfn "TELEMETRY_HOST_MANAGER_BUNDLE_VERIFIED"
        | "assemble-v2" ->
            let expected = Set.union selectionOptionNames (Set ["--source-root";"--publish";"--source-revision";"--source-tree";"--runtime-version";"--output"])
            require (Set.ofSeq values.Keys = expected) "THMB-ARGUMENT" "assemble options differ"
            let archive = prepareV2 (required "--source-root" values) (required "--publish" values) (selectionFrom values) (required "--source-revision" values) (required "--source-tree" values) (required "--runtime-version" values) (required "--output" values)
            printfn "TELEMETRY_HOST_MANAGER_BUNDLE_PREPARED archive=%s sha256=%s" (Path.GetFileName archive) (shaFile archive)
        | "verify-v2" ->
            let allowed = Set.union selectionOptionNames (Set ["--source-root";"--expected-revision";"--expected-tree";"--prepared";"--archive"])
            require (Set.isSubset (Set.ofSeq values.Keys) allowed) "THMB-ARGUMENT" "verify options differ"
            require (Set.isSubset (Set.remove "--archive" allowed) (Set.ofSeq values.Keys)) "THMB-ARGUMENT" "verify options are incomplete"
            let archive = match values.TryGetValue "--archive" with | true,value -> Some value | _ -> None
            verifyV2 (required "--source-root" values) (selectionFrom values) (required "--expected-revision" values) (required "--expected-tree" values) (required "--prepared" values) archive
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
