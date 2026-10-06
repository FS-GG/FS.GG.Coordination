module TelemetryHostManager

#nowarn "9"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

let private fail reason = raise (InvalidOperationException reason)
let private sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
let private hex64 (value: string) = Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
let private sha40 (value: string) = Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

type private UnitReadback = { unit: string; active: string; fileState: string }

let private options (arguments: string list) =
    let rec collect (current: Map<string, string>) (rest: string list) =
        match rest with
        | [] -> current
        | key :: value :: tail when key.StartsWith("--", StringComparison.Ordinal) && not (current |> Map.containsKey key) ->
            collect (current |> Map.add key value) tail
        | _ -> fail "expected distinct --name value options"
    collect Map.empty arguments

let private required name values =
    match values |> Map.tryFind name with
    | Some value when not (String.IsNullOrWhiteSpace value) -> value
    | _ -> fail ("missing " + name)

let private only allowed values =
    values |> Map.iter (fun key _ -> if not (Set.contains key allowed) then fail ("unknown option " + key))

let private json value = JsonSerializer.Serialize value

let private command (timeout: int) (executable: string) (arguments: string list) =
    let start = ProcessStartInfo(executable)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in arguments do start.ArgumentList.Add argument
    use child = Process.Start start
    if isNull child then fail "command did not start"
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit(timeout)) then
        child.Kill(true)
        fail "command timed out"
    child.ExitCode, output.Result.Trim(), error.Result.Trim()

let private checkedCommand timeout executable arguments =
    let code, output, _ = command timeout executable arguments
    if code <> 0 then fail ("command refused: " + Path.GetFileName executable)
    output

let private verifyHash path expected =
    if not (hex64 expected) then fail "expected SHA-256 must be lowercase hex"
    let actual = File.ReadAllBytes path |> sha256
    if actual <> expected then fail "release archive SHA-256 differs"
    actual

let private safeDirectory (path: string) =
    if not (Path.IsPathFullyQualified path) then fail "absolute directory required"
    let full = Path.GetFullPath path
    if not (Regex.IsMatch(full, "^/[A-Za-z0-9_./-]+$")) then fail "installation root contains unsafe characters"
    let mutable current = DirectoryInfo full
    while not (isNull current) do
        if current.Exists && not (isNull current.LinkTarget) then fail "symlink directory refused"
        current <- current.Parent
    full

let private installEngine values =
    only (Set.ofList [ "--package"; "--sha256"; "--version"; "--root" ]) values
    let package = Path.GetFullPath(required "--package" values)
    let expected = required "--sha256" values
    let version = required "--version" values
    let root = required "--root" values |> safeDirectory
    if not (Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid engine version"
    let digest = verifyHash package expected
    Directory.CreateDirectory root |> ignore
    let destination = Path.Combine(root, version)
    if Directory.Exists destination || File.Exists destination then fail "engine version already exists; inspect it before replacement"
    let staging = Path.Combine(root, ".engine-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory staging |> ignore
    try
        use archive = ZipFile.OpenRead package
        let prefix = "tools/net10.0/any/"
        let mutable count = 0
        let mutable total = 0L
        for entry in archive.Entries do
            if entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && not (entry.FullName.EndsWith("/", StringComparison.Ordinal)) then
                let relative = entry.FullName.Substring(prefix.Length)
                let parts = relative.Split('/')
                if parts |> Array.exists (fun part -> part = "" || part = "." || part = "..") then fail "unsafe package member path"
                if (entry.ExternalAttributes >>> 16) &&& 0xF000 = 0xA000 then fail "package symlink refused"
                count <- count + 1
                total <- total + entry.Length
                if count > 512 || total > 200L * 1024L * 1024L then fail "package extraction bound exceeded"
                let target = Path.Combine(Array.append [| staging |] parts)
                let parent = Path.GetDirectoryName target
                Directory.CreateDirectory parent |> ignore
                entry.ExtractToFile(target, false)
        if count = 0 || not (File.Exists(Path.Combine(staging, "fsgg-coord-engine.dll"))) then fail "engine payload missing"
        let wrapper = Path.Combine(staging, "fsgg-coord-engine")
        File.WriteAllText(wrapper, "#!/bin/sh\nexec /usr/bin/dotnet " + Path.Combine(destination, "fsgg-coord-engine.dll") + " \"$@\"\n")
        for file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories) do
            File.SetUnixFileMode(file, if file = wrapper then UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute else UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
        for directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories) do
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        File.SetUnixFileMode(staging, UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        let observed = checkedCommand 30000 "/usr/bin/dotnet" [ Path.Combine(staging, "fsgg-coord-engine.dll"); "--version" ]
        if observed <> version + ".0" then fail "engine package version readback differs"
        Directory.Move(staging, destination)
        let installed = checkedCommand 30000 (Path.Combine(destination, "fsgg-coord-engine")) [ "--version" ]
        if installed <> observed then fail "installed engine version readback differs"
        printfn "%s" (json {| status = "installed"; version = version; packageSha256 = digest; path = destination |})
    finally
        if Directory.Exists staging then
            for file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories) do File.SetUnixFileMode(file, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            for directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories) do File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            Directory.Delete(staging, true)

let private installManager values =
    only (Set.ofList [ "--root"; "--version" ]) values
    if Environment.UserName <> "root" then fail "root required to install manager"
    let root = required "--root" values |> safeDirectory
    let version = required "--version" values
    if not (Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid manager version"
    let source = AppContext.BaseDirectory
    let names = [ "TelemetryHostManager"; "TelemetryHostManager.dll"; "TelemetryHostManager.deps.json"; "TelemetryHostManager.runtimeconfig.json"; "FSharp.Core.dll" ]
    let destination = Path.Combine(root, version)
    Directory.CreateDirectory root |> ignore
    if Directory.Exists destination || File.Exists destination then fail "manager version already exists; inspect it before replacement"
    let staging = Path.Combine(root, ".manager-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory staging |> ignore
    try
        let receipts =
            names |> List.map (fun name ->
                let original = Path.Combine(source, name)
                let info = FileInfo original
                if not info.Exists || not (isNull info.LinkTarget) then fail "manager source file unsafe"
                let bytes = File.ReadAllBytes original
                let target = Path.Combine(staging, name)
                File.WriteAllBytes(target, bytes)
                File.SetUnixFileMode(target, if name = "TelemetryHostManager" then UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute else UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
                name, sha256 bytes)
        File.SetUnixFileMode(staging, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        Directory.Move(staging, destination)
        printfn "%s" (json {| status = "manager-installed"; version = version; path = destination; files = receipts |})
    finally
        if Directory.Exists staging then Directory.Delete(staging, true)

let private verifyHostRelease values =
    only (Set.ofList [ "--package"; "--manifest"; "--sha256"; "--verifier" ]) values
    let package = required "--package" values |> Path.GetFullPath
    let manifest = required "--manifest" values |> Path.GetFullPath
    let verifier = required "--verifier" values |> Path.GetFullPath
    let digest = verifyHash package (required "--sha256" values)
    let result = checkedCommand 60000 "/usr/bin/python3" [ verifier; "verify"; "--package"; package; "--manifest"; manifest ]
    use document = JsonDocument.Parse result
    if document.RootElement.GetProperty("verified").GetBoolean() <> true || document.RootElement.GetProperty("archiveSha256").GetString() <> digest then
        fail "Host verifier result differs"
    printfn "%s" result

let private verifyEngineManifest manifest expected version packageDigest =
    verifyHash manifest expected |> ignore
    use document = JsonDocument.Parse(File.ReadAllBytes manifest)
    let root = document.RootElement
    let descriptor = root.GetProperty("descriptor")
    if root.GetProperty("schema").GetString() <> "fsgg.release-saga/1"
       || root.GetProperty("state").GetProperty("phase").GetString() <> "promoted"
       || descriptor.GetProperty("channel").GetString() <> "stable"
       || descriptor.GetProperty("version").GetString() <> version then fail "CLI release manifest differs"
    let packages = descriptor.GetProperty("packages").EnumerateArray() |> Seq.toList
    let matches = packages |> List.filter (fun package -> package.GetProperty("id").GetString() = "FS.GG.Coord.Cli")
    if matches.Length <> 1 || matches[0].GetProperty("version").GetString() <> version
       || matches[0].GetProperty("artifact").GetProperty("sha256").GetString() <> packageDigest then fail "CLI package manifest differs"

let private verifyEngineRelease values =
    only (Set.ofList [ "--package"; "--sha256"; "--manifest"; "--manifest-sha256"; "--version" ]) values
    let version = required "--version" values
    let digest = verifyHash (required "--package" values) (required "--sha256" values)
    verifyEngineManifest (required "--manifest" values) (required "--manifest-sha256" values) version digest
    printfn "%s" (json {| status = "verified"; version = version; packageSha256 = digest |})

let private currentUid () =
    let value = checkedCommand 10000 "/usr/bin/id" [ "-u" ]

    match Int32.TryParse value with
    | true, uid when uid >= 0 -> uid
    | _ -> fail "current user identity is invalid"

let private ownedBy uid path =
    checkedCommand 10000 "/usr/bin/stat" [ "-c"; "%u"; path ] = string uid

let private noLinkedAncestor path =
    let mutable current = Path.GetFullPath path
    let mutable safe = true

    while safe && not (String.IsNullOrEmpty current) do
        if File.Exists current then
            safe <- isNull (FileInfo(current).LinkTarget)
        elif Directory.Exists current then
            safe <- isNull (DirectoryInfo(current).LinkTarget)

        let parent = Path.GetDirectoryName current
        current <- if parent = current then null else parent

    safe

let private privateFile uid path =
    let info = FileInfo path

    info.Exists
    && Path.IsPathFullyQualified path
    && noLinkedAncestor path
    && ownedBy uid path
    && File.GetUnixFileMode(path) = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

let private privateDirectory uid path =
    let info = DirectoryInfo path

    info.Exists
    && Path.IsPathFullyQualified path
    && noLinkedAncestor path
    && ownedBy uid path
    && File.GetUnixFileMode(path) = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let private executableFile path =
    let info = FileInfo path
    let mode = if info.Exists then File.GetUnixFileMode path else enum 0

    info.Exists
    && Path.IsPathFullyQualified path
    && noLinkedAncestor path
    && (mode
        &&& (UnixFileMode.UserExecute
             ||| UnixFileMode.GroupExecute
             ||| UnixFileMode.OtherExecute))
       <> enum 0
    && (mode &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)) = enum 0

let private ownedByHostOrRoot uid path =
    let owner = checkedCommand 10000 "/usr/bin/stat" [ "-c"; "%u"; path ]
    owner = "0" || owner = string uid

let private privateDescendantDirectory uid anchor path =
    let anchor = Path.GetFullPath anchor |> Path.TrimEndingDirectorySeparator
    let path = Path.GetFullPath path |> Path.TrimEndingDirectorySeparator
    let relative = Path.GetRelativePath(anchor, path)

    if
        relative = "."
        || Path.IsPathFullyQualified relative
        || relative = ".."
        || relative.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
    then
        false
    else
        let mutable current = path
        let mutable safe = true

        while safe && current <> anchor do
            safe <- privateDirectory uid current
            current <- Path.GetDirectoryName current |> Path.TrimEndingDirectorySeparator

        safe && current = anchor

let private atomicPrivateWrite (path: string) (bytes: byte array) =
    let directory = Path.GetDirectoryName path

    let temporary =
        Path.Combine(directory, ".native-collector-" + Guid.NewGuid().ToString("N"))

    try
        use stream =
            new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            )

        stream.Write bytes
        stream.Flush true
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        File.Move(temporary, path, false)
    finally
        if File.Exists temporary then
            File.Delete temporary

let private exactJsonProperties (expected: Set<string>) (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then false
    else
        let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toArray
        names.Length = expected.Count && Set.ofArray names = expected

let private nonzeroSha256 value = hex64 value && value |> Seq.exists ((<>) '0')

let private canonicalAbsolutePath (path: string) =
    Path.IsPathFullyQualified path && Path.GetFullPath path = path

let private safeOwnedArtifactFile uid executable path =
    let info = FileInfo path
    let mode = if info.Exists then File.GetUnixFileMode path else enum 0
    let executableMode = mode &&& (UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute)
    info.Exists
    && canonicalAbsolutePath path
    && isNull info.LinkTarget
    && noLinkedAncestor path
    && ownedByHostOrRoot uid path
    && (mode &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)) = enum 0
    && (not executable || executableMode <> enum 0)

let private boundedFileBytes maximum label path =
    let info = FileInfo path
    if not info.Exists || info.Length < 0L || info.Length > int64 maximum then fail (label + " exceeds its byte bound")
    let bytes = File.ReadAllBytes path
    if int64 bytes.Length <> info.Length then fail (label + " changed while read")
    bytes

let private fileSha256 path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private stringJsonProperty (label: string) (name: string) (element: JsonElement) =
    let mutable property = Unchecked.defaultof<JsonElement>
    if not (element.TryGetProperty(name, &property))
       || property.ValueKind <> JsonValueKind.String
       || String.IsNullOrWhiteSpace(property.GetString()) then fail (label + " " + name + " is invalid")
    property.GetString()

[<StructLayout(LayoutKind.Explicit, Size = 256)>]
type private NativeDependencyStatx =
    struct
        [<FieldOffset(0)>] val mutable Mask: uint32
        [<FieldOffset(16)>] val mutable Links: uint32
        [<FieldOffset(20)>] val mutable Uid: uint32
        [<FieldOffset(24)>] val mutable Gid: uint32
        [<FieldOffset(28)>] val mutable Mode: uint16
        [<FieldOffset(32)>] val mutable Inode: uint64
        [<FieldOffset(40)>] val mutable Size: uint64
        [<FieldOffset(96)>] val mutable ChangedSeconds: int64
        [<FieldOffset(104)>] val mutable ChangedNanoseconds: uint32
        [<FieldOffset(112)>] val mutable ModifiedSeconds: int64
        [<FieldOffset(120)>] val mutable ModifiedNanoseconds: uint32
        [<FieldOffset(136)>] val mutable DeviceMajor: uint32
        [<FieldOffset(140)>] val mutable DeviceMinor: uint32
    end

[<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
extern int private openNativeDependency(string path, int flags)

[<DllImport("libc", EntryPoint = "statx", SetLastError = true)>]
extern int private statNativeDependency(int descriptor, string path, int flags, uint32 mask, NativeDependencyStatx& value)

let private nativeDependencyIdentity descriptor path flags =
    let mutable value = NativeDependencyStatx()
    if statNativeDependency(descriptor, path, flags, 0x7ffu, &value) <> 0
       || value.Mask &&& 0x3dfu <> 0x3dfu then
        fail "native verifier runtime file stat refused"
    value

let private sameNativeDependency (left: NativeDependencyStatx) (right: NativeDependencyStatx) =
    left.Links = right.Links && left.Uid = right.Uid && left.Gid = right.Gid
    && left.Mode = right.Mode && left.Inode = right.Inode && left.Size = right.Size
    && left.DeviceMajor = right.DeviceMajor && left.DeviceMinor = right.DeviceMinor
    && left.ChangedSeconds = right.ChangedSeconds && left.ChangedNanoseconds = right.ChangedNanoseconds
    && left.ModifiedSeconds = right.ModifiedSeconds && left.ModifiedNanoseconds = right.ModifiedNanoseconds

let private verifyNativeManifestFile (uid: int) executable allowEmpty declaredBytes digest path =
    if not (safeOwnedArtifactFile uid executable path) then fail "native verifier runtime manifest file is unsafe"
    // Bind declared size and digest to one regular, single-link, no-follow fd and its final name.
    let descriptor = openNativeDependency(path, 0x80000 ||| 0x20000 ||| 0x800)
    if descriptor < 0 then fail "native verifier runtime manifest file open refused"
    use handle = new SafeFileHandle(nativeint descriptor, true)
    let before = nativeDependencyIdentity descriptor "" 0x1000
    let mode = before.Mode &&& 0xfffus
    if before.Mode &&& 0xf000us <> 0x8000us || before.Links <> 1u
       || (before.Uid <> 0u && before.Uid <> uint32 uid)
       || mode &&& 0o022us <> 0us || (executable && mode &&& 0o111us = 0us)
       || before.Size <> uint64 declaredBytes then
        fail "native verifier runtime manifest file is unsafe"
    if declaredBytes = 0L && (not allowEmpty || mode <> 0o444us) then
        fail "native verifier runtime empty dependency custody differs"
    use stream = new FileStream(handle, FileAccess.Read, 65536, false)
    use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
    let buffer = Array.zeroCreate<byte> 65536
    let mutable remaining = declaredBytes
    while remaining > 0L do
        let count = stream.Read(buffer, 0, int (min remaining (int64 buffer.Length)))
        if count = 0 then fail "native verifier runtime manifest file is truncated"
        hash.AppendData(buffer, 0, count)
        remaining <- remaining - int64 count
    if stream.ReadByte() <> -1 then fail "native verifier runtime manifest file extent changed"
    let after = nativeDependencyIdentity descriptor "" 0x1000
    let named = nativeDependencyIdentity -100 path 0x100
    if not (sameNativeDependency before after && sameNativeDependency after named)
       || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() <> digest then
        fail "native verifier runtime manifest file differs"

let private verifyNativeVerifierManifestFor (canonicalModuleSha256: string) uid runtime runtimeDigest modulePath moduleDigest manifestPath manifestDigest =
    if not (safeOwnedArtifactFile uid true runtime) || FileInfo(runtime).Length <= 0L then fail "native verifier runtime executable is unsafe"
    if not (safeOwnedArtifactFile uid false modulePath) || FileInfo(modulePath).Length <= 0L then fail "native verifier module is unsafe"
    if not (safeOwnedArtifactFile uid false manifestPath) || FileInfo(manifestPath).Length <= 0L then fail "native verifier runtime manifest is unsafe"
    if not (nonzeroSha256 runtimeDigest && nonzeroSha256 moduleDigest && nonzeroSha256 manifestDigest) then
        fail "native verifier SHA-256 must be lowercase nonzero hex"
    if moduleDigest <> canonicalModuleSha256 then fail "native verifier module is not the canonical qualified module"
    if fileSha256 runtime <> runtimeDigest then fail "native verifier runtime SHA-256 differs"
    if fileSha256 modulePath <> moduleDigest then fail "native verifier module SHA-256 differs"
    let manifestBytes = boundedFileBytes (1024 * 1024) "native verifier runtime manifest" manifestPath
    if sha256 manifestBytes <> manifestDigest then fail "native verifier runtime manifest SHA-256 differs"
    use document = JsonDocument.Parse manifestBytes
    let root = document.RootElement
    if not (exactJsonProperties (Set.ofList [ "schema"; "sourceRevision"; "runtimeImageDigest"; "runtimeExecutablePath"; "modulePath"; "files" ]) root)
       || stringJsonProperty "native verifier runtime manifest" "schema" root <> "fsgg.telemetry.native-verifier-runtime/1" then
        fail "native verifier runtime manifest is not closed schema v1"
    let sourceRevision = stringJsonProperty "native verifier runtime manifest" "sourceRevision" root
    let imageDigest = stringJsonProperty "native verifier runtime manifest" "runtimeImageDigest" root
    let declaredRuntime = stringJsonProperty "native verifier runtime manifest" "runtimeExecutablePath" root
    let declaredModule = stringJsonProperty "native verifier runtime manifest" "modulePath" root
    if not (sha40 sourceRevision) then fail "native verifier source revision is invalid"
    if not (imageDigest.StartsWith("sha256:", StringComparison.Ordinal))
       || not (nonzeroSha256 (imageDigest.Substring("sha256:".Length))) then fail "native verifier runtime image digest is invalid"
    if declaredRuntime <> runtime || declaredModule <> modulePath then fail "native verifier runtime manifest paths differ"
    let mutable files = Unchecked.defaultof<JsonElement>
    if not (root.TryGetProperty("files", &files)) || files.ValueKind <> JsonValueKind.Array then
        fail "native verifier runtime manifest files are invalid"
    let entries = files.EnumerateArray() |> Seq.toArray
    if entries.Length = 0 || entries.Length > 4096 then fail "native verifier runtime manifest file count exceeds its bound"
    let mutable previous = null
    let mutable total = 0L
    let mutable runtimeSeen = false
    let mutable moduleSeen = false
    for entry in entries do
        if not (exactJsonProperties (Set.ofList [ "path"; "bytes"; "sha256" ]) entry) then
            fail "native verifier runtime manifest file entry is not closed"
        let path = stringJsonProperty "native verifier runtime manifest file" "path" entry
        let digest = stringJsonProperty "native verifier runtime manifest file" "sha256" entry
        let mutable declaredBytes = 0L
        let mutable bytesProperty = Unchecked.defaultof<JsonElement>
        if not (entry.TryGetProperty("bytes", &bytesProperty))
           || bytesProperty.ValueKind <> JsonValueKind.Number
           || not (bytesProperty.TryGetInt64(&declaredBytes))
           || declaredBytes < 0L then fail "native verifier runtime manifest file bytes are invalid"
        if not (canonicalAbsolutePath path) || not (nonzeroSha256 digest) then
            fail "native verifier runtime manifest file identity is invalid"
        let allowEmpty = path <> runtime && path <> modulePath && path <> manifestPath
        if declaredBytes = 0L
           && (not allowEmpty || digest <> "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855") then
            fail "native verifier runtime manifest empty dependency is invalid"
        if not (isNull previous) && StringComparer.Ordinal.Compare(previous, path) >= 0 then
            fail "native verifier runtime manifest files must be sorted and unique"
        if total > 512L * 1024L * 1024L - declaredBytes then fail "native verifier runtime closure exceeds its byte bound"
        total <- total + declaredBytes
        previous <- path
        verifyNativeManifestFile uid (path = runtime) allowEmpty declaredBytes digest path
        if path = runtime then
            runtimeSeen <- true
            if digest <> runtimeDigest then fail "native verifier runtime inventory binding differs"
        if path = modulePath then
            moduleSeen <- true
            if digest <> moduleDigest then fail "native verifier module inventory binding differs"
    if total <= 0L then fail "native verifier runtime closure must remain positive"
    if not runtimeSeen || not moduleSeen then fail "native verifier runtime inventory omits required code"
    manifestBytes

let private verifyNativeVerifierManifest uid runtime runtimeDigest modulePath moduleDigest manifestPath manifestDigest =
    verifyNativeVerifierManifestFor "8d6a33beae9a4de84fa7a703809e9b1a1656359a085f92091cf56de3b77fd3ba" uid runtime runtimeDigest modulePath moduleDigest manifestPath manifestDigest

let private verifyNativeSourceReference uid custodyAnchor codexHome evidenceRoot manifestDigest =
    let sourceReferencePath = Path.Combine(custodyAnchor, "source-reference.json")
    if not (privateFile uid sourceReferencePath) then fail "native source reference must be an owner-private regular file"
    let sourceReferenceBytes = boundedFileBytes 65536 "native source reference" sourceReferencePath
    use document = JsonDocument.Parse sourceReferenceBytes
    let root = document.RootElement
    if not (exactJsonProperties (Set.ofList [ "schema"; "profileSha256"; "nativeSourceVolume"; "developmentTarget"; "collectorReadOnlyTarget"; "readerProfileSha256"; "captureQualified"; "verifierRuntimeManifestSha256" ]) root)
       || stringJsonProperty "native source reference" "schema" root <> "fsgg.telemetry.persistent-source-references/3" then
        fail "native source reference is not closed schema v3"
    let profileSha = stringJsonProperty "native source reference" "profileSha256" root
    let readerProfileSha = stringJsonProperty "native source reference" "readerProfileSha256" root
    let sourceVolume = stringJsonProperty "native source reference" "nativeSourceVolume" root
    let developmentTarget = stringJsonProperty "native source reference" "developmentTarget" root
    let collectorTarget = stringJsonProperty "native source reference" "collectorReadOnlyTarget" root
    let declaredManifest = stringJsonProperty "native source reference" "verifierRuntimeManifestSha256" root
    let mutable captureQualified = Unchecked.defaultof<JsonElement>
    if not (nonzeroSha256 profileSha && nonzeroSha256 readerProfileSha)
       || declaredManifest <> manifestDigest
       || not (Regex.IsMatch(sourceVolume, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
       || not (canonicalAbsolutePath developmentTarget)
       || collectorTarget <> codexHome
       || not (root.TryGetProperty("captureQualified", &captureQualified))
       || captureQualified.ValueKind <> JsonValueKind.False then fail "native source reference binding differs"
    let readerProfilePath = Path.Combine(evidenceRoot, "fixed-native-capability-profile.json")
    if not (privateFile uid readerProfilePath) then fail "native reader profile must be an owner-private regular file"
    let readerProfileBytes = boundedFileBytes (1024 * 1024) "native reader profile" readerProfilePath
    if sha256 readerProfileBytes <> readerProfileSha then fail "native source reference reader profile SHA-256 differs"
    sourceReferenceBytes

let private installNativeCollector values =
    only
        (Set.ofList
            [
                "--host-config"
                "--credential-reference"
                "--executable"
                "--codex-home"
                "--evidence-root"
                "--provider"
                "--model"
                "--effort"
                "--installation-version"
                "--executable-sha256"
                "--verifier-runtime"
                "--verifier-runtime-sha256"
                "--verifier-module"
                "--verifier-module-sha256"
                "--verifier-runtime-manifest"
                "--verifier-runtime-manifest-sha256"
            ])
        values

    if not (OperatingSystem.IsLinux()) then
        fail "native collector installation supports Linux only"

    let uid = currentUid ()
    let hostConfig = required "--host-config" values |> Path.GetFullPath
    let credentialReference = required "--credential-reference" values
    let executable = required "--executable" values |> Path.GetFullPath
    let codexHome = required "--codex-home" values |> Path.GetFullPath
    let evidenceRoot = required "--evidence-root" values |> Path.GetFullPath
    let installationVersion = values |> Map.tryFind "--installation-version" |> Option.defaultValue "1"

    if installationVersion <> "1" && installationVersion <> "2" && installationVersion <> "3" then
        fail "installation version must be 1, 2, or 3"

    let verifierOptionNames =
        [ "--verifier-runtime"; "--verifier-runtime-sha256"; "--verifier-module"; "--verifier-module-sha256"; "--verifier-runtime-manifest"; "--verifier-runtime-manifest-sha256" ]
    let suppliedVerifierOptions = verifierOptionNames |> List.filter (fun name -> values |> Map.containsKey name)
    if installationVersion = "3" && suppliedVerifierOptions.Length <> verifierOptionNames.Length then
        fail "installation version must be 1 or 2 unless all version 3 native verifier options are supplied"
    if installationVersion <> "3" && not suppliedVerifierOptions.IsEmpty then
        fail "native verifier options are valid only for installation version 3"
    let verifierOptions =
        if installationVersion = "3" then
            Some(required "--verifier-runtime" values |> Path.GetFullPath,
                 required "--verifier-runtime-sha256" values,
                 required "--verifier-module" values |> Path.GetFullPath,
                 required "--verifier-module-sha256" values,
                 required "--verifier-runtime-manifest" values |> Path.GetFullPath,
                 required "--verifier-runtime-manifest-sha256" values)
        else None

    let executablePin =
        match installationVersion, Map.tryFind "--executable-sha256" values with
        | "1", None -> None
        | "1", Some _ -> fail "executable SHA-256 is valid only for installation version 2 or 3"
        | ("2" | "3"), None -> fail "missing --executable-sha256"
        | "2", Some value when hex64 value -> Some value
        | "2", Some _ -> fail "executable SHA-256 must be lowercase hex"
        | "3", Some value when nonzeroSha256 value -> Some value
        | "3", Some _ -> fail "executable SHA-256 must be lowercase nonzero hex"
        | _ -> None

    let bounded name value =
        if
            String.IsNullOrWhiteSpace value
            || value.Length > 128
            || value
               |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c || ".:_-/@+".Contains c))
        then
            fail ("invalid " + name)

        value

    let provider = required "--provider" values |> bounded "provider"
    let model = required "--model" values |> bounded "model"
    let effort = required "--effort" values |> bounded "effort"

    if not (Regex.IsMatch(credentialReference, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) then
        fail "invalid credential reference"

    if not (privateFile uid hostConfig) then
        fail "Host configuration must be an owner-private regular file"

    if not (privateDirectory uid (Path.GetDirectoryName hostConfig)) then
        fail "Host configuration parent must be an owner-private directory"

    if not (privateDirectory uid codexHome) then
        fail "Codex home must be an owner-private directory"

    if not (executableFile executable) then
        fail "Codex executable is unsafe"

    if (installationVersion = "2" || installationVersion = "3") && not (ownedByHostOrRoot uid executable) then
        fail "Codex executable must be owned by the Host account or root"

    let executableDigest =
        use source = File.OpenRead executable
        Convert.ToHexString(SHA256.HashData source).ToLowerInvariant()

    match executablePin with
    | Some expected when executableDigest <> expected -> fail "Codex executable SHA-256 differs"
    | _ -> ()

    let custodyAnchor = Path.GetDirectoryName hostConfig

    if (installationVersion = "2" || installationVersion = "3") && not (privateDescendantDirectory uid custodyAnchor codexHome) then
        fail "Codex home must be a private descendant of the Host configuration parent"

    let evidenceParent = Path.GetDirectoryName evidenceRoot

    if String.IsNullOrEmpty evidenceParent || not (privateDirectory uid evidenceParent) then
        fail "evidence parent must be an owner-private directory"

    if
        (installationVersion = "2" || installationVersion = "3")
        && Path.TrimEndingDirectorySeparator evidenceParent <> Path.TrimEndingDirectorySeparator custodyAnchor
        && not (privateDescendantDirectory uid custodyAnchor evidenceParent)
    then
        fail "evidence root must be beneath private Host configuration custody"

    if Directory.Exists evidenceRoot then
        if not (privateDirectory uid evidenceRoot) then
            fail "evidence root must be an owner-private directory"
    elif File.Exists evidenceRoot then
        fail "evidence root is not a directory"
    else
        Directory.CreateDirectory evidenceRoot |> ignore

        File.SetUnixFileMode(
            evidenceRoot,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

    if (installationVersion = "2" || installationVersion = "3") && not (privateDescendantDirectory uid custodyAnchor evidenceRoot) then
        fail "evidence root must be a private descendant of the Host configuration parent"

    let verifierEvidence =
        match verifierOptions with
        | Some(runtime, runtimeDigest, modulePath, moduleDigest, manifestPath, manifestDigest) ->
            verifyNativeVerifierManifest uid runtime runtimeDigest modulePath moduleDigest manifestPath manifestDigest |> ignore
            let sourceReferenceBytes = verifyNativeSourceReference uid custodyAnchor codexHome evidenceRoot manifestDigest
            Some(runtime, runtimeDigest, modulePath, moduleDigest, manifestPath, manifestDigest, sourceReferenceBytes)
        | None -> None

    let configBytes = File.ReadAllBytes hostConfig

    if configBytes.Length > 1024 * 1024 then
        fail "Host configuration is oversized"

    use document = JsonDocument.Parse configBytes
    let root = document.RootElement
    let mutable schema = Unchecked.defaultof<JsonElement>

    if
        root.ValueKind <> JsonValueKind.Object
        || not (root.TryGetProperty("Schema", &schema))
        || schema.ValueKind <> JsonValueKind.String
        || schema.GetString() <> "fsgg.telemetry.host-config/2"
    then
        fail "native collector requires Host configuration v2"

    let mutable credentials = Unchecked.defaultof<JsonElement>

    if
        not (root.TryGetProperty("Credentials", &credentials))
        || credentials.ValueKind <> JsonValueKind.Array
    then
        fail "Host credential inventory is unavailable"

    let matches =
        credentials.EnumerateArray()
        |> Seq.filter (fun entry ->
            let mutable reference = Unchecked.defaultof<JsonElement>

            entry.ValueKind = JsonValueKind.Object
            && entry.TryGetProperty("Reference", &reference)
            && reference.ValueKind = JsonValueKind.String
            && reference.GetString() = credentialReference)
        |> Seq.toArray

    if matches.Length <> 1 then
        fail "native collector credential reference must resolve exactly once"

    let credential = matches[0]

    let stringProperty (name: string) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if
            not (credential.TryGetProperty(name, &property))
            || property.ValueKind <> JsonValueKind.String
            || String.IsNullOrWhiteSpace(property.GetString())
        then
            fail ("native collector credential " + name + " is invalid")

        property.GetString()

    let int64Property (name: string) =
        let mutable property = Unchecked.defaultof<JsonElement>
        let mutable value = 0L

        if
            not (credential.TryGetProperty(name, &property))
            || not (property.TryGetInt64(&value))
            || value <= 0L
        then
            fail ("native collector credential " + name + " is invalid")

        value

    let mutable revokedProperty = Unchecked.defaultof<JsonElement>

    if
        not (credential.TryGetProperty("Revoked", &revokedProperty))
        || (revokedProperty.ValueKind <> JsonValueKind.True
            && revokedProperty.ValueKind <> JsonValueKind.False)
    then
        fail "native collector credential Revoked is invalid"

    let revoked = revokedProperty.GetBoolean()

    if stringProperty "Role" <> "native-collector" || revoked then
        fail "native collector credential is not active"

    let secretFile = stringProperty "SecretFile" |> Path.GetFullPath

    if not (privateFile uid secretFile) then
        fail "native collector credential secret must be owner-private"

    let workspace = stringProperty "WorkspaceId"
    let producer = stringProperty "ProducerId"
    let stream = stringProperty "StreamId"
    let grantId = stringProperty "GrantId"
    let generation = int64Property "GrantGeneration"

    let validIdentity (value: string) =
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

    if [ workspace; producer; stream; grantId ] |> List.exists (validIdentity >> not) then
        fail "native collector credential scope or grant is invalid"

    let sidecar = hostConfig + ".native-collector.json"
    let receiptPath = hostConfig + ".native-collector.receipt.json"

    let sidecarBytes =
        if installationVersion = "3" then
            let runtime, runtimeDigest, modulePath, moduleDigest, manifestPath, manifestDigest, _ = verifierEvidence.Value
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    Schema = "fsgg.telemetry.native-collector-installation/3"
                    CredentialReference = credentialReference
                    ExecutablePath = executable
                    CodexHome = codexHome
                    EvidenceRoot = evidenceRoot
                    Provider = provider
                    Model = model
                    Effort = effort
                    ExecutableSha256 = executableDigest
                    NativeVerifier =
                        {|
                            RuntimeExecutablePath = runtime
                            RuntimeExecutableSha256 = runtimeDigest
                            ModulePath = modulePath
                            ModuleSha256 = moduleDigest
                            RuntimeManifestPath = manifestPath
                            RuntimeManifestSha256 = manifestDigest
                        |}
                |}
        elif installationVersion = "2" then
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    Schema = "fsgg.telemetry.native-collector-installation/2"
                    CredentialReference = credentialReference
                    ExecutablePath = executable
                    CodexHome = codexHome
                    EvidenceRoot = evidenceRoot
                    Provider = provider
                    Model = model
                    Effort = effort
                    ExecutableSha256 = executableDigest
                |}
        else
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    Schema = "fsgg.telemetry.native-collector-installation/1"
                    CredentialReference = credentialReference
                    ExecutablePath = executable
                    CodexHome = codexHome
                    EvidenceRoot = evidenceRoot
                    Provider = provider
                    Model = model
                    Effort = effort
                |}

    let receiptBytes =
        if installationVersion = "3" then
            let _, _, _, _, _, manifestDigest, sourceReferenceBytes = verifierEvidence.Value
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    schema = "fsgg.telemetry.native-collector-installation-receipt/3"
                    status = "installed"
                    ownerUid = uid
                    hostConfigSha256 = sha256 configBytes
                    sidecarSha256 = sha256 sidecarBytes
                    executableSha256 = executableDigest
                    credentialReference = credentialReference
                    workspaceId = workspace
                    producerId = producer
                    streamId = stream
                    grantId = grantId
                    grantGeneration = generation
                    sourceVerification = "unknown"
                    snapshotOrigin = "unknown"
                    sharedCostCompleteness = "unknown"
                    activationAuthorized = false
                    sourceReferenceSha256 = sha256 sourceReferenceBytes
                    verifierRuntimeManifestSha256 = manifestDigest
                |}
        else
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    schema = "fsgg.telemetry.native-collector-installation-receipt/" + installationVersion
                    status = "installed"
                    ownerUid = uid
                    hostConfigSha256 = sha256 configBytes
                    sidecarSha256 = sha256 sidecarBytes
                    executableSha256 = executableDigest
                    credentialReference = credentialReference
                    workspaceId = workspace
                    producerId = producer
                    streamId = stream
                    grantId = grantId
                    grantGeneration = generation
                    sourceVerification = "unknown"
                    snapshotOrigin = "unknown"
                    sharedCostCompleteness = "unknown"
                    activationAuthorized = false
                |}

    let installExact path bytes =
        if File.Exists path then
            if not (privateFile uid path) || File.ReadAllBytes path <> bytes then
                fail "installed native collector custody differs"
        else
            atomicPrivateWrite path bytes

    if (installationVersion = "2" || installationVersion = "3") && File.Exists sidecar && privateFile uid sidecar then
        use installed = JsonDocument.Parse(File.ReadAllBytes sidecar)
        let mutable installedSchema = Unchecked.defaultof<JsonElement>

        if
            installed.RootElement.ValueKind = JsonValueKind.Object
            && installed.RootElement.TryGetProperty("Schema", &installedSchema)
            && installedSchema.ValueKind = JsonValueKind.String
            && ((installationVersion = "2" && installedSchema.GetString() = "fsgg.telemetry.native-collector-installation/1")
                || (installationVersion = "3" && installedSchema.GetString() <> "fsgg.telemetry.native-collector-installation/3"))
        then
            fail "native collector installation cannot be promoted in place; choose prospective custody paths"

    installExact sidecar sidecarBytes
    installExact receiptPath receiptBytes
    printfn "%s" (System.Text.Encoding.UTF8.GetString receiptBytes)

let private installResponsesCollector values =
    let allowed = Set.ofList [ "--host-config"; "--credential-reference"; "--provider-credential-reference"; "--provider-credential-file"; "--evidence-root"; "--capability-profile"; "--capability-profile-sha256"; "--capability-result"; "--capability-result-sha256"; "--verifier-runtime"; "--verifier-runtime-sha256"; "--verifier-module"; "--verifier-module-sha256"; "--verifier-runtime-manifest"; "--verifier-runtime-manifest-sha256" ]
    only allowed values
    if not (OperatingSystem.IsLinux()) then fail "Responses installation supports Linux only"
    let uid = currentUid ()
    let absolute (name: string) =
        let path = required name values
        if not (canonicalAbsolutePath path) then fail (name + " requires a normalized absolute path")
        path
    let hostConfig = absolute "--host-config"
    let anchor = Path.GetDirectoryName hostConfig
    if not (privateFile uid hostConfig && privateDirectory uid anchor) then fail "Responses requires private Host custody"
    let privateInput (name: string) bound =
        let path = absolute name
        if not (privateFile uid path) || not (path.StartsWith(anchor + string Path.DirectorySeparatorChar, StringComparison.Ordinal)) then fail "Responses private input is outside Host custody"
        path, boundedFileBytes bound name path
    let expectedSha (name: string) =
        let value = required name values
        if not (nonzeroSha256 value) then fail "Responses digest must be nonzero lowercase SHA256"
        value
    let profilePath, profileBytes = privateInput "--capability-profile" 65536
    let profileDigest = expectedSha "--capability-profile-sha256"
    let resultPath, resultBytes = privateInput "--capability-result" 65536
    let resultDigest = expectedSha "--capability-result-sha256"
    if sha256 profileBytes <> profileDigest || sha256 resultBytes <> resultDigest then fail "Responses capability input digest differs"
    let runtime = absolute "--verifier-runtime"
    let runtimeDigest = expectedSha "--verifier-runtime-sha256"
    let modulePath = absolute "--verifier-module"
    let moduleDigest = expectedSha "--verifier-module-sha256"
    let manifestPath = absolute "--verifier-runtime-manifest"
    let manifestDigest = expectedSha "--verifier-runtime-manifest-sha256"
    let responsesModuleDigest = "599034490c93333875b169c7fc4d3e873e3d911fe5571ad90127ea86b1e8b373"
    verifyNativeVerifierManifestFor responsesModuleDigest uid runtime runtimeDigest modulePath moduleDigest manifestPath manifestDigest |> ignore
    let number (label: string) (name: string) (node: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        let mutable value = 0L
        if not (node.TryGetProperty(name, &property)) || property.ValueKind <> JsonValueKind.Number || not (property.TryGetInt64(&value)) || value < 0L then fail (label + " number invalid")
        value
    let boolean (label: string) (name: string) (expected: bool) (node: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if not (node.TryGetProperty(name, &property)) || (property.ValueKind <> JsonValueKind.True && property.ValueKind <> JsonValueKind.False) || property.GetBoolean() <> expected then fail (label + " Boolean differs")
    let array (label: string) (name: string) (node: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if not (node.TryGetProperty(name, &property)) || property.ValueKind <> JsonValueKind.Array then fail (label + " array invalid")
        property.EnumerateArray() |> Seq.toArray
    use profileDoc = JsonDocument.Parse profileBytes
    let profile = profileDoc.RootElement
    let profileFields = Set.ofList [ "schema"; "sourceVariant"; "provider"; "model"; "effort"; "countEndpoint"; "generationEndpoint"; "inputTokenLimit"; "outputTokenLimit"; "wholeMilliseconds"; "networkMilliseconds"; "requestPolicySha256"; "instructionsSha256"; "responseSchemaSha256"; "responseSchemaName"; "verifierModuleSha256"; "verifierRuntimeManifestSha256"; "installedRoots"; "installedFiles" ]
    if not (exactJsonProperties profileFields profile) then fail "Responses profile is not closed"
    let ps name = stringJsonProperty "Responses profile" name profile
    for name, expected in [ "schema", "fsgg.telemetry.responses-capability-profile/1"; "sourceVariant", "openai-responses/1"; "provider", "openai"; "model", "gpt-6.1-sol"; "effort", "medium"; "countEndpoint", "https://api.openai.com/v1/responses/input_tokens"; "generationEndpoint", "https://api.openai.com/v1/responses"; "requestPolicySha256", "7a0e6970101b8cc9343c23ebe5d273e183cf4535b4dc09fca4d48a0bee7ae071"; "verifierModuleSha256", moduleDigest; "verifierRuntimeManifestSha256", manifestDigest ] do
        if ps name <> expected then fail "Responses profile fixed policy differs"
    for name, expected in [ "inputTokenLimit", 8000L; "outputTokenLimit", 1500L; "wholeMilliseconds", 60000L; "networkMilliseconds", 55000L ] do
        if number "Responses profile" name profile <> expected then fail "Responses profile limit differs"
    if not (nonzeroSha256 (ps "instructionsSha256") && nonzeroSha256 (ps "responseSchemaSha256")) || not (Regex.IsMatch(ps "responseSchemaName", "^[A-Za-z0-9_-]{1,64}$")) then fail "Responses instruction/schema policy is invalid"
    let roots = array "Responses profile" "installedRoots" profile |> Array.map (fun node ->
        if node.ValueKind <> JsonValueKind.String then fail "Responses installed root invalid"
        let path = node.GetString()
        if not (canonicalAbsolutePath path) || not (Directory.Exists path) then fail "Responses installed root missing"
        safeDirectory path)
    if roots.Length = 0 || roots.Length > 4 || (Set.ofArray roots |> Set.count) <> roots.Length then fail "Responses installed roots invalid"
    for a in roots do
        for b in roots do
            if a <> b && a.StartsWith(Path.TrimEndingDirectorySeparator(b) + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then fail "Responses installed roots overlap"
    let files = array "Responses profile" "installedFiles" profile
    if files.Length = 0 || files.Length > 512 then fail "Responses installed file count exceeded"
    let mutable previous: string = null
    let mutable total = 0L
    let mutable declared = Set.empty<string>
    let mutable components = Set.empty<string>
    let expectedAssemblies = Map.ofList [ "host", "FS.GG.Telemetry.Host"; "client", "FS.GG.Telemetry.Client"; "core", "FS.GG.Coord.Core"; "store", "FS.GG.Telemetry.Store" ]
    for entry in files do
        if not (exactJsonProperties (Set.ofList [ "path"; "bytes"; "sha256"; "components" ]) entry) then fail "Responses installed entry is not closed"
        let path = stringJsonProperty "Responses installed file" "path" entry
        let digest = stringJsonProperty "Responses installed file" "sha256" entry
        let bytes = number "Responses installed file" "bytes" entry
        if not (canonicalAbsolutePath path) || not (nonzeroSha256 digest) || not (roots |> Array.exists (fun root -> path.StartsWith(Path.TrimEndingDirectorySeparator(root) + string Path.DirectorySeparatorChar, StringComparison.Ordinal))) then fail "Responses installed file identity differs"
        if not (isNull previous) && StringComparer.Ordinal.Compare(previous,path) >= 0 then fail "Responses installed files must be sorted unique"
        if total > 200L * 1024L * 1024L - bytes then fail "Responses installed files exceed byte bound"
        if bytes <= 0L then fail "Responses installed product files must be positive"
        verifyNativeManifestFile uid false false bytes digest path
        previous <- path
        total <- total + bytes
        declared <- declared.Add path
        let roles = array "Responses installed file" "components" entry |> Array.map (fun node ->
            if node.ValueKind <> JsonValueKind.String then fail "Responses component invalid"
            node.GetString())
        if roles.Length > 4 || (Set.ofArray roles |> Set.count) <> roles.Length then fail "Responses component list invalid"
        for role in roles do
            if components.Contains role || not (expectedAssemblies.ContainsKey role) then fail "Responses component repeated or invalid"
            let expectedName = expectedAssemblies[role]
            if Path.GetFileName path <> expectedName + ".dll" || Reflection.AssemblyName.GetAssemblyName(path).Name <> expectedName then fail "Responses component assembly identity differs"
            components <- components.Add role
    if components <> (expectedAssemblies |> Map.keys |> Set.ofSeq) then fail "Responses installed inventory omits assembly components"
    let mutable actual = Set.empty<string>
    let mutable directories = 0
    let pending = Collections.Generic.Stack<string>(roots)
    while pending.Count > 0 do
        let directory = pending.Pop()
        directories <- directories + 1
        if directories > 1024 || not (isNull (DirectoryInfo(directory).LinkTarget)) then fail "Responses installed directory inventory refused"
        for entry in Directory.EnumerateFileSystemEntries directory do
            if not (isNull (FileInfo(entry).LinkTarget)) then fail "Responses installed linked entry refused"
            if Directory.Exists entry then pending.Push entry
            elif File.Exists entry then
                actual <- actual.Add entry
                if actual.Count > 512 then fail "Responses installed inventory file bound exceeded"
            else fail "Responses installed special entry refused"
    if actual <> declared then fail "Responses installed inventory has extra or missing files"
    let installedFilesDigest = sha256 (JsonSerializer.SerializeToUtf8Bytes(profile.GetProperty("installedFiles")))
    use resultDoc = JsonDocument.Parse resultBytes
    let result = resultDoc.RootElement
    let resultFields = Set.ofList [ "schema"; "sourceVariant"; "profileSha256"; "verifierRuntimeManifestSha256"; "verifierModuleSha256"; "installedFilesSha256"; "scenarioResults"; "ownedCustodyClean"; "resourceFailed"; "elapsedMilliseconds"; "originalWholeMilliseconds"; "operationSpecificCaptureProduced" ]
    if not (exactJsonProperties resultFields result) then fail "Responses static qualification is not closed"
    for name, expected in [ "schema", "fsgg.telemetry.responses-static-qualification/1"; "sourceVariant", "openai-responses/1"; "profileSha256", profileDigest; "verifierRuntimeManifestSha256", manifestDigest; "verifierModuleSha256", moduleDigest; "installedFilesSha256", installedFilesDigest ] do
        if stringJsonProperty "Responses static qualification" name result <> expected then fail "Responses static qualification binding differs"
    boolean "Responses static qualification" "ownedCustodyClean" true result
    boolean "Responses static qualification" "resourceFailed" false result
    boolean "Responses static qualification" "operationSpecificCaptureProduced" false result
    let whole = number "Responses static qualification" "originalWholeMilliseconds" result
    if whole <= 0L || whole > 60000L || number "Responses static qualification" "elapsedMilliseconds" result >= whole then fail "Responses static qualification original deadline failed"
    let scenarios = array "Responses static qualification" "scenarioResults" result
    let expectedCases = Set.ofList [ "bounded-client-wire-cardinality"; "denied-count-no-generation"; "cancellation-retirement"; "request-policy-caps"; "credential-role-separation"; "verifier-valid-capture"; "verifier-mutated-capture-refused"; "verifier-stale-snapshot-refused"; "current-installed-closure" ]
    let names = scenarios |> Array.map (stringJsonProperty "Responses scenario" "name")
    if names <> (expectedCases |> Set.toArray) then fail "Responses scenario qualification roster must be exact and sorted"
    let mutable seen = Set.empty<string>
    for scenario in scenarios do
        if not (exactJsonProperties (Set.ofList [ "name"; "outcome"; "evidenceSha256" ]) scenario) then fail "Responses scenario result is not closed"
        let name = stringJsonProperty "Responses scenario" "name" scenario
        if seen.Contains name || not (expectedCases.Contains name) || stringJsonProperty "Responses scenario" "outcome" scenario <> "pass" || not (nonzeroSha256 (stringJsonProperty "Responses scenario" "evidenceSha256" scenario)) then fail "Responses scenario qualification differs"
        seen <- seen.Add name
    if seen <> expectedCases then fail "Responses static qualification scenario roster is incomplete"
    let configBytes = boundedFileBytes (1024 * 1024) "Host config" hostConfig
    use configDoc = JsonDocument.Parse configBytes
    let config = configDoc.RootElement
    let rec uniqueJson depth (node: JsonElement) =
        if depth > 64 then fail "Responses Host config nesting exceeds bound"
        if node.ValueKind = JsonValueKind.Object then
            let properties = node.EnumerateObject() |> Seq.toArray
            if (properties |> Array.map _.Name |> Set.ofArray |> Set.count) <> properties.Length then fail "Responses Host config duplicate property"
            for property in properties do uniqueJson (depth + 1) property.Value
        elif node.ValueKind = JsonValueKind.Array then
            for entry in node.EnumerateArray() do uniqueJson (depth + 1) entry
    uniqueJson 0 config
    if stringJsonProperty "Host config" "Schema" config <> "fsgg.telemetry.host-config/2" then fail "Responses requires Host config v2"
    let credentials = array "Host config" "Credentials" config
    let reference = required "--credential-reference" values
    let providerReference = required "--provider-credential-reference" values
    let validIdentity (value: string) = Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
    if not (validIdentity reference && validIdentity providerReference) || reference = providerReference then fail "Responses credential roles must be distinct"
    let matches = credentials |> Array.filter (fun c -> stringJsonProperty "Host credential" "Reference" c = reference)
    if matches.Length <> 1 then fail "Responses native principal must resolve exactly once"
    let credential = matches[0]
    boolean "Host credential" "Revoked" false credential
    if stringJsonProperty "Host credential" "Role" credential <> "native-collector" then fail "Responses principal is not native-collector"
    let cs name = stringJsonProperty "Host credential" name credential
    let workspace, producer, stream, grant = cs "WorkspaceId", cs "ProducerId", cs "StreamId", cs "GrantId"
    if [ workspace; producer; stream; grant ] |> List.exists (validIdentity >> not) then fail "Responses native principal scope invalid"
    let generation = number "Host credential" "GrantGeneration" credential
    if generation <= 0L || not (privateFile uid (cs "SecretFile")) then fail "Responses native grant invalid"
    let providerFile = absolute "--provider-credential-file"
    if not (privateFile uid providerFile) || not (providerFile.StartsWith(anchor + string Path.DirectorySeparatorChar,StringComparison.Ordinal)) || (credentials |> Array.exists (fun c -> stringJsonProperty "Host credential" "Reference" c = providerReference || stringJsonProperty "Host credential" "SecretFile" c = providerFile)) then fail "Responses provider credential is not separately held"
    let providerIdentity = nativeDependencyIdentity -100 providerFile 0x100
    let nativeIdentity = nativeDependencyIdentity -100 (cs "SecretFile") 0x100
    if providerIdentity.Links <> 1u || nativeIdentity.Links <> 1u
       || providerIdentity.Mode &&& 0xf000us <> 0x8000us || nativeIdentity.Mode &&& 0xf000us <> 0x8000us
       || (providerIdentity.DeviceMajor = nativeIdentity.DeviceMajor && providerIdentity.DeviceMinor = nativeIdentity.DeviceMinor && providerIdentity.Inode = nativeIdentity.Inode) then fail "Responses secret file roles are not physically distinct"
    // Inspect custody only. Never read or hash either credential's secret bytes.
    let evidence = absolute "--evidence-root"
    let evidenceParent = Path.GetDirectoryName evidence
    if not (privateDirectory uid evidenceParent) || (evidenceParent <> anchor && not (privateDescendantDirectory uid anchor evidenceParent)) then fail "Responses evidence custody differs"
    if File.Exists evidence || (Directory.Exists evidence && not (privateDescendantDirectory uid anchor evidence)) then fail "Responses evidence root unsafe"
    let sidecar = hostConfig + ".native-collector.json"
    let receiptPath = hostConfig + ".native-collector.receipt.json"
    let sidecarBytes = JsonSerializer.SerializeToUtf8Bytes
                        {| Schema = "fsgg.telemetry.native-collector-installation/4"; SourceVariant = "openai-responses/1"; CredentialReference = reference; ProviderCredentialReference = providerReference; ProviderCredentialFile = providerFile; EvidenceRoot = evidence; Provider = "openai"; Model = "gpt-6.1-sol"; Effort = "medium"; CountEndpoint = "https://api.openai.com/v1/responses/input_tokens"; GenerationEndpoint = "https://api.openai.com/v1/responses"; InputTokenLimit = 8000L; OutputTokenLimit = 1500L; WholeMilliseconds = 60000L; CapabilityProfilePath = profilePath; CapabilityProfileSha256 = profileDigest; CapabilityResultPath = resultPath; CapabilityResultSha256 = resultDigest; NativeVerifier = {| RuntimeExecutablePath = runtime; RuntimeExecutableSha256 = runtimeDigest; ModulePath = modulePath; ModuleSha256 = moduleDigest; RuntimeManifestPath = manifestPath; RuntimeManifestSha256 = manifestDigest |} |}
    let receiptBytes = JsonSerializer.SerializeToUtf8Bytes
                        {| schema = "fsgg.telemetry.native-collector-installation-receipt/4"; status = "installed"; ownerUid = uid; hostConfigSha256 = sha256 configBytes; sidecarSha256 = sha256 sidecarBytes; sourceVariant = "openai-responses/1"; credentialReference = reference; workspaceId = workspace; producerId = producer; streamId = stream; grantId = grant; grantGeneration = generation; providerCredentialReference = providerReference; providerCredentialFile = providerFile; capabilityProfileSha256 = profileDigest; capabilityResultSha256 = resultDigest; verifierRuntimeManifestSha256 = manifestDigest; verifierModuleSha256 = moduleDigest; installedFilesSha256 = installedFilesDigest; sourceVerification = "unknown"; snapshotOrigin = "unknown"; activationAuthorized = false |}
    for path, bytes in [ sidecar, sidecarBytes; receiptPath, receiptBytes ] do
        if File.Exists path && (not (privateFile uid path) || File.ReadAllBytes path <> bytes) then fail "Responses installation cannot promote or replace existing custody"
    // All positive static qualification and current physical joins precede writes.
    if not (Directory.Exists evidence) then
        Directory.CreateDirectory evidence |> ignore
        File.SetUnixFileMode(evidence, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    for path, bytes in [ sidecar, sidecarBytes; receiptPath, receiptBytes ] do
        if not (File.Exists path) then atomicPrivateWrite path bytes
    printfn "%s" (System.Text.Encoding.UTF8.GetString receiptBytes)

let private serviceAccount = "fsgg-telemetry-podman"
let private serviceHome = "/var/lib/fs-gg/telemetry-podman"

let private requireServiceAccount () =
    if Environment.UserName <> serviceAccount then fail "run as the telemetry service account"
    if Environment.GetEnvironmentVariable("HOME") <> serviceHome then fail "telemetry service HOME differs"
    Directory.SetCurrentDirectory serviceHome

let private createAccount () =
    if Environment.UserName <> "root" then fail "root required to create service account"
    let code, old, _ = command 10000 "/usr/bin/getent" [ "passwd"; serviceAccount ]
    if code = 0 then
        let parts = old.Split(':')
        if parts.Length < 7 || parts[5] <> serviceHome || parts[6] <> "/usr/bin/nologin" then fail "existing telemetry account differs"
    elif code = 2 then
        checkedCommand 30000 "/usr/sbin/useradd" [ "--system"; "--create-home"; "--add-subids-for-system"; "--home-dir"; serviceHome; "--shell"; "/usr/bin/nologin"; serviceAccount ] |> ignore
    else fail "service account lookup refused"
    for path in [ "/etc/fs-gg/telemetry-host-container"; "/var/lib/fs-gg/telemetry-host-container-schema10"; "/var/lib/fs-gg/telemetry-host-container-restores"; "/var/backups/fs-gg/telemetry-host-container"; serviceHome + "/releases" ] do
        if Directory.Exists path then
            let info = DirectoryInfo path
            if not (isNull info.LinkTarget) then fail "service directory symlink refused"
        checkedCommand 10000 "/usr/bin/install" [ "-d"; "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0700"; path ] |> ignore
    printfn "%s" (json {| status = "account-prepared"; account = serviceAccount; home = serviceHome; unitsEnabled = false |})

let private prepareRootlessRuntime () =
    if Environment.UserName <> "root" then fail "root required to prepare rootless runtime"
    checkedCommand 30000 "/usr/bin/loginctl" [ "enable-linger"; serviceAccount ] |> ignore
    let linger = checkedCommand 10000 "/usr/bin/loginctl" [ "show-user"; serviceAccount; "--property=Linger"; "--value" ]
    if linger <> "yes" then fail "service account lingering was not enabled"
    printfn "%s" (json {| status = "rootless-runtime-prepared"; account = serviceAccount; telemetryUnitsEnabled = false |})

let private stageHostAssets values =
    only (Set.ofList [ "--package"; "--manifest"; "--journal"; "--package-sha256"; "--manifest-sha256"; "--journal-sha256"; "--version"; "--verifier" ]) values
    if Environment.UserName <> "root" then fail "root required to stage Host assets"
    let package = required "--package" values |> Path.GetFullPath
    let manifest = required "--manifest" values |> Path.GetFullPath
    let journal = required "--journal" values |> Path.GetFullPath
    let verifier = required "--verifier" values |> Path.GetFullPath
    let version = required "--version" values
    if not (Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid Host version"
    let digest = verifyHash package (required "--package-sha256" values)
    verifyHash manifest (required "--manifest-sha256" values) |> ignore
    verifyHash journal (required "--journal-sha256" values) |> ignore
    let report = checkedCommand 60000 "/usr/bin/python3" [ verifier; "verify"; "--package"; package; "--manifest"; manifest ]
    use verified = JsonDocument.Parse report
    if verified.RootElement.GetProperty("verified").GetBoolean() <> true || verified.RootElement.GetProperty("archiveSha256").GetString() <> digest || verified.RootElement.GetProperty("version").GetString() <> version then fail "verified Host release differs"
    let release = Path.Combine(serviceHome, "releases", version)
    if Directory.Exists release || File.Exists release then fail "existing Host release directory requires inspection"
    let staging = Path.Combine(serviceHome, "releases", ".host-" + Guid.NewGuid().ToString("N"))
    checkedCommand 10000 "/usr/bin/install" [ "-d"; "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0700"; staging ] |> ignore
    try
        for source, name, expected in [ package, "FS.GG.Telemetry.Host." + version + ".nupkg", digest; manifest, "manifest.json", required "--manifest-sha256" values; journal, "publication-journal.json", required "--journal-sha256" values ] do
            checkedCommand 10000 "/usr/bin/install" [ "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0600"; source; Path.Combine(staging, name) ] |> ignore
            verifyHash (Path.Combine(staging, name)) expected |> ignore
        Directory.Move(staging, release)
    finally
        if Directory.Exists staging then Directory.Delete(staging, true)
    printfn "%s" (json {| status = "host-assets-staged"; version = version; packageSha256 = digest; path = release; serviceStarted = false |})

let private copyExact (source: string) (destination: string) (mode: UnixFileMode) =
    let sourceInfo = FileInfo source
    if not sourceInfo.Exists || not (isNull sourceInfo.LinkTarget) then fail "installation source is unsafe"
    let parent = Path.GetDirectoryName destination |> safeDirectory
    let relative = Path.GetRelativePath(serviceHome, parent)
    if relative = "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified relative then fail "installation target escaped service home"
    let mutable current = serviceHome
    for segment in relative.Split(Path.DirectorySeparatorChar) do
        current <- Path.Combine(current, segment)
        let directory = DirectoryInfo current
        if directory.Exists && not (isNull directory.LinkTarget) then fail "service directory symlink refused"
        checkedCommand 10000 "/usr/bin/install" [ "-d"; "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0700"; current ] |> ignore
    let original = File.ReadAllBytes source
    if File.Exists destination then
        let existing = FileInfo destination
        if not (isNull existing.LinkTarget) || File.ReadAllBytes destination <> original then fail "installed source differs"
    else
        let temporary = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"))
        try
            File.WriteAllBytes(temporary, original)
            File.SetUnixFileMode(temporary, mode)
            checkedCommand 10000 "/usr/bin/chown" [ serviceAccount + ":" + serviceAccount; temporary ] |> ignore
            File.Move(temporary, destination)
        finally
            if File.Exists temporary then File.Delete temporary
    File.SetUnixFileMode(destination, mode)
    let installed = FileInfo destination
    let expectedOwner = checkedCommand 10000 "/usr/bin/id" [ "-u"; serviceAccount ] |> int
    if installed.UnixFileMode <> mode || File.GetUnixFileMode(destination) <> mode then fail "installed file mode differs"
    let owner = checkedCommand 10000 "/usr/bin/stat" [ "-c"; "%u"; destination ] |> int
    if owner <> expectedOwner then fail "installed file owner differs"

let private updateExact (source: string) (destination: string) (mode: UnixFileMode) =
    let sourceInfo = FileInfo source
    let existing = FileInfo destination
    let parent = Path.GetDirectoryName destination |> safeDirectory
    let expectedOwner = checkedCommand 10000 "/usr/bin/id" [ "-u"; serviceAccount ] |> int
    if not sourceInfo.Exists || not (isNull sourceInfo.LinkTarget) then fail "update source is unsafe"
    if not existing.Exists || not (isNull existing.LinkTarget) then fail "installed update target is absent or unsafe"
    if (checkedCommand 10000 "/usr/bin/stat" [ "-c"; "%u"; destination ] |> int) <> expectedOwner then fail "installed update target owner differs"
    let temporary = Path.Combine(parent, ".update-" + Guid.NewGuid().ToString("N"))
    try
        File.WriteAllBytes(temporary, File.ReadAllBytes source)
        File.SetUnixFileMode(temporary, mode)
        checkedCommand 10000 "/usr/bin/chown" [ serviceAccount + ":" + serviceAccount; temporary ] |> ignore
        File.Move(temporary, destination, true)
    finally
        if File.Exists temporary then File.Delete temporary
    if File.GetUnixFileMode(destination) <> mode then fail "updated file mode differs"
    if (checkedCommand 10000 "/usr/bin/stat" [ "-c"; "%u"; destination ] |> int) <> expectedOwner then fail "updated file owner differs"

let private installHostFiles values =
    only (Set.ofList [ "--systemadmin-root"; "--commit" ]) values
    if Environment.UserName <> "root" then fail "root required to install Host files"
    let root = required "--systemadmin-root" values |> safeDirectory
    let expectedCommit = required "--commit" values
    if not (sha40 expectedCommit) then fail "SystemAdmin commit must be a full SHA"
    let gitPrefix = [ "-c"; "safe.directory=" + root; "-C"; root ]
    let observedCommit = checkedCommand 30000 "/usr/bin/git" (gitPrefix @ [ "rev-parse"; "HEAD" ])
    if observedCommit <> expectedCommit then fail "SystemAdmin source commit differs"
    let sourcePaths = [ "Services/telemetry-host-podman"; "Services/telemetry-host" ]
    let clean, _, _ = command 30000 "/usr/bin/git" (gitPrefix @ [ "diff"; "--quiet"; "HEAD"; "--" ] @ sourcePaths)
    if clean <> 0 then fail "reviewed SystemAdmin source paths are dirty"
    let executable = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let podman = Path.Combine(root, "Services/telemetry-host-podman")
    let host = Path.Combine(root, "Services/telemetry-host")
    let scripts = [ "build-image.sh"; "telemetry-host-podman.sh"; "telemetry_host_podman_backup.py"; "telemetry_host_update.py"; "Containerfile" ]
    let target = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host-podman")
    for name in scripts do copyExact (Path.Combine(podman, name)) (Path.Combine(target, name)) (if name = "Containerfile" then privateFile else executable)
    let hostTarget = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host")
    for name in [ "telemetry_host_release.py"; "telemetry_host_config_backup.py" ] do
        copyExact (Path.Combine(host, name)) (Path.Combine(hostTarget, name)) executable
    copyExact (Path.Combine(podman, "fsgg-telemetry-host-podman.service.example"))
        (Path.Combine(serviceHome, ".config/systemd/user/fsgg-telemetry-host-podman.service")) privateFile
    copyExact (Path.Combine(podman, "fsgg-telemetry-host-update.service.example"))
        (Path.Combine(serviceHome, ".config/systemd/user/fsgg-telemetry-host-update.service")) privateFile
    copyExact (Path.Combine(podman, "fsgg-telemetry-host-update.timer.example"))
        (Path.Combine(serviceHome, ".config/systemd/user/fsgg-telemetry-host-update.timer")) privateFile
    printfn "%s" (json {| status = "host-files-installed"; account = serviceAccount; unitsEnabled = false |})

let private updateHostFiles values =
    only (Set.ofList [ "--systemadmin-root"; "--commit" ]) values
    if Environment.UserName <> "root" then fail "root required to update Host files"
    let root = required "--systemadmin-root" values |> safeDirectory
    let expectedCommit = required "--commit" values
    if not (sha40 expectedCommit) then fail "SystemAdmin commit must be a full SHA"
    let gitPrefix = [ "-c"; "safe.directory=" + root; "-C"; root ]
    if checkedCommand 30000 "/usr/bin/git" (gitPrefix @ [ "rev-parse"; "HEAD" ]) <> expectedCommit then fail "SystemAdmin source commit differs"
    let sourcePaths = [ "Services/telemetry-host-podman"; "Services/telemetry-host" ]
    let clean, _, _ = command 30000 "/usr/bin/git" (gitPrefix @ [ "diff"; "--quiet"; "HEAD"; "--" ] @ sourcePaths)
    if clean <> 0 then fail "reviewed SystemAdmin source paths are dirty"
    let executable = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let podman = Path.Combine(root, "Services/telemetry-host-podman")
    let host = Path.Combine(root, "Services/telemetry-host")
    let target = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host-podman")
    for name in [ "build-image.sh"; "telemetry-host-podman.sh"; "telemetry_host_podman_backup.py"; "telemetry_host_update.py"; "Containerfile" ] do
        updateExact (Path.Combine(podman, name)) (Path.Combine(target, name)) (if name = "Containerfile" then privateFile else executable)
    let hostTarget = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host")
    for name in [ "telemetry_host_release.py"; "telemetry_host_config_backup.py" ] do
        updateExact (Path.Combine(host, name)) (Path.Combine(hostTarget, name)) executable
    for sourceName, targetName in [ "fsgg-telemetry-host-podman.service.example", "fsgg-telemetry-host-podman.service"; "fsgg-telemetry-host-update.service.example", "fsgg-telemetry-host-update.service"; "fsgg-telemetry-host-update.timer.example", "fsgg-telemetry-host-update.timer" ] do
        updateExact (Path.Combine(podman, sourceName)) (Path.Combine(serviceHome, ".config/systemd/user", targetName)) privateFile
    printfn "%s" (json {| status = "host-files-updated"; account = serviceAccount; unitsEnabled = false; sourceCommit = expectedCommit |})

let private buildHostImage values =
    only (Set.ofList [ "--package"; "--manifest"; "--sha256"; "--runtime-image"; "--image" ]) values
    requireServiceAccount ()
    let package = required "--package" values |> Path.GetFullPath
    let manifest = required "--manifest" values |> Path.GetFullPath
    let digest = required "--sha256" values
    let runtime = required "--runtime-image" values
    let image = required "--image" values
    if not (Regex.IsMatch(runtime, "^[^ @]+@sha256:[0-9a-f]{64}$")) || not (Regex.IsMatch(image, "^[a-z0-9./-]+:[0-9][a-z0-9.-]+$")) then fail "image identity is invalid"
    verifyHash package digest |> ignore
    checkedCommand 900000 "/usr/bin/podman" [ "pull"; runtime ] |> ignore
    let builder = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host-podman/build-image.sh")
    let output = checkedCommand 900000 builder [ "--package"; package; "--manifest"; manifest; "--archive-sha256"; digest; "--runtime-image"; runtime; "--image"; image ]
    use receipt = JsonDocument.Parse output
    if receipt.RootElement.GetProperty("schema").GetString() <> "fsgg.telemetry.host-container-image/1" then fail "image receipt schema differs"
    printfn "%s" output

let private unitState unit property =
    let code, output, _ = command 10000 "/usr/bin/systemctl" [ "show"; unit; "--property=" + property; "--value" ]
    if code <> 0 then fail ("systemd readback failed: " + unit)
    output

let private enabled unit =
    let code, output, _ = command 10000 "/usr/bin/systemctl" [ "is-enabled"; unit ]
    if code <> 0 && code <> 1 && code <> 4 then fail ("systemd enablement unreadable: " + unit)
    output

let private status () =
    let publisher = "fsgg-telemetry-dashboard-publisher-member-v3"
    let registry = "fsgg-telemetry-member-registry-update"
    let stage = "fsgg-telemetry-dashboard-stage-member-v3"
    let publicCommit = checkedCommand 30000 "/usr/bin/git" [ "ls-remote"; "https://github.com/FS-GG/.github.git"; "refs/heads/telemetry-data" ]
    let fields = publicCommit.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    if fields.Length <> 2 || not (sha40 fields[0]) || fields[1] <> "refs/heads/telemetry-data" then fail "public ref readback malformed"
    printfn "%s" (json {| schema = "fsgg.telemetry.host-manager-status/1"; observedAt = DateTimeOffset.UtcNow.ToString("O"); publicCommit = fields[0]; publisherTimer = unitState (publisher + ".timer") "ActiveState"; publisherEnabled = enabled (publisher + ".timer"); publisherService = unitState (publisher + ".service") "ActiveState"; registryTimer = unitState (registry + ".timer") "ActiveState"; stageTimer = unitState (stage + ".timer") "ActiveState" |})

let private telemetryUnitNames executable prefix =
    let lines command =
        let output = checkedCommand 10000 executable (prefix @ [ command; "--all"; "--no-legend"; "--plain"; "fsgg-telemetry-*" ])
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line ->
            let fields = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            if fields.Length < 2 || not (Regex.IsMatch(fields[0], "^fsgg-telemetry-[A-Za-z0-9@_.-]+[.][a-z]+$")) then fail "telemetry unit inventory malformed"
            fields[0])
    Array.append (lines "list-unit-files") (lines "list-units")
    |> Array.distinct
    |> Array.sort

let private sourceFenceStatus () =
    if Environment.UserName <> "root" then fail "root required to read source fence status"
    let publicRef = checkedCommand 30000 "/usr/bin/git" [ "ls-remote"; "https://github.com/FS-GG/.github.git"; "refs/heads/telemetry-data" ]
    let fields = publicRef.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    if fields.Length <> 2 || not (sha40 fields[0]) || fields[1] <> "refs/heads/telemetry-data" then fail "public ref readback malformed"
    let accountUid = checkedCommand 10000 "/usr/bin/id" [ "-u"; serviceAccount ]
    if not (Regex.IsMatch(accountUid, "^[0-9]+$")) then fail "telemetry account UID is invalid"
    let userPrefix = [ "-u"; serviceAccount; "--"; "/usr/bin/env"; "-i"; "HOME=" + serviceHome; "USER=" + serviceAccount; "LOGNAME=" + serviceAccount; "XDG_RUNTIME_DIR=/run/user/" + accountUid; "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/" + accountUid + "/bus"; "/usr/bin/systemctl"; "--user" ]
    let systemUnits = telemetryUnitNames "/usr/bin/systemctl" []
    let userUnits = telemetryUnitNames "/usr/bin/runuser" userPrefix
    let systemRequired = [ "fsgg-telemetry-dashboard-publisher-member-v3.service"; "fsgg-telemetry-dashboard-publisher-member-v3.timer"; "fsgg-telemetry-member-registry-update.service"; "fsgg-telemetry-member-registry-update.timer" ]
    let userRequired = [ "fsgg-telemetry-host-podman.service" ]
    if systemUnits.Length = 0 || userUnits.Length = 0 || systemRequired |> List.exists (fun unit -> not (Array.contains unit systemUnits)) || userRequired |> List.exists (fun unit -> not (Array.contains unit userUnits)) then fail "source telemetry unit inventory is incomplete"
    let readUnit executable prefix unit : UnitReadback =
        let active = checkedCommand 10000 executable (prefix @ [ "show"; unit; "--property=ActiveState"; "--value" ])
        let fileState = checkedCommand 10000 executable (prefix @ [ "show"; unit; "--property=UnitFileState"; "--value" ])
        if active = "" || fileState = "" then fail "source telemetry unit state is incomplete"
        { unit = unit; active = active; fileState = fileState }
    let system = systemUnits |> Array.map (readUnit "/usr/bin/systemctl" [])
    let user = userUnits |> Array.map (readUnit "/usr/bin/runuser" userPrefix)
    let quiescent (item: UnitReadback) =
        let service = item.unit.EndsWith(".service", StringComparison.Ordinal)
        let trigger = [ ".timer"; ".path"; ".socket" ] |> List.exists (fun ending -> item.unit.EndsWith(ending, StringComparison.Ordinal))
        if not (service || trigger) then fail "unreviewed telemetry unit type"
        item.active = "inactive" &&
        (item.fileState = "disabled" || item.fileState = "masked" ||
         (service && item.fileState = "static"))
    let quiesced = Array.forall quiescent system && Array.forall quiescent user
    let publicRefAfter = checkedCommand 30000 "/usr/bin/git" [ "ls-remote"; "https://github.com/FS-GG/.github.git"; "refs/heads/telemetry-data" ]
    if publicRefAfter <> publicRef then fail "public ref moved during source fence readback"
    printfn "%s" (json {| schema = "fsgg.telemetry.source-fence-status/1"; observedAt = DateTimeOffset.UtcNow.ToString("O"); publicCommit = fields[0]; observedUnitsQuiesced = quiesced; activationAuthorized = false; systemUnits = system; userUnits = user |})
    if not quiesced then fail "source telemetry units remain active or recurrent"

let private guard values =
    only (Set.ofList [ "--host-id" ]) values
    let hostId = required "--host-id" values
    if not (Regex.IsMatch(hostId, "^[a-z][a-z0-9-]{0,39}$")) then fail "invalid host id"
    let main = checkedCommand 30000 "/usr/bin/git" [ "ls-remote"; "https://github.com/FS-GG/.github.git"; "refs/heads/main" ]
    let fields = main.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    if fields.Length <> 2 || not (sha40 fields[0]) || fields[1] <> "refs/heads/main" then fail "protected main readback malformed"
    let uri = "https://raw.githubusercontent.com/FS-GG/.github/" + fields[0] + "/telemetry-dashboard/active-publisher.json"
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
    let raw = client.GetByteArrayAsync(uri).GetAwaiter().GetResult()
    if raw.Length > 4096 then fail "publisher selector too large"
    use document = JsonDocument.Parse raw
    let root = document.RootElement
    let keys = root.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq
    if keys <> Set.ofList [ "schema"; "generation"; "activeHost" ] || root.GetProperty("schema").GetString() <> "fsgg.telemetry.active-publisher/1" then fail "publisher selector schema differs"
    let generation = root.GetProperty("generation").GetInt32()
    let selected = root.GetProperty("activeHost").GetString()
    if generation < 1 || isNull selected || not (Regex.IsMatch(selected, "^[a-z][a-z0-9-]{0,39}$")) then fail "publisher selector invalid"
    if selected = hostId then
        printfn "%s" (json {| status = "selected"; host = hostId; generation = generation; mainCommit = fields[0] |})
        0
    else
        eprintfn "publisher not selected for this host"
        1

let private installGuardDropins values =
    only (Set.ofList [ "--host-id"; "--manager" ]) values
    if Environment.UserName <> "root" then fail "root required to install publisher guards"
    let hostId = required "--host-id" values
    let manager = required "--manager" values
    if not (Regex.IsMatch(hostId, "^[a-z][a-z0-9-]{0,39}$")) then fail "invalid host id"
    if not (Regex.IsMatch(manager, "^/[A-Za-z0-9_./-]+$")) || not (File.Exists manager) || not (isNull (FileInfo(manager).LinkTarget)) then fail "unsafe manager executable"
    // A standby must receive the guard before it can be selected. Both a
    // selected and an unselected host can install it; malformed control fails.
    guard (Map.ofList [ "--host-id", hostId ]) |> ignore
    let units = [ "fsgg-telemetry-dashboard-publisher-member-v3.service"; "fsgg-telemetry-member-registry-update.service" ]
    let content = "[Service]\nExecCondition=" + manager + " guard --host-id " + hostId + "\n"
    // Refuse conflicting drop-ins before writing either unit.
    for unit in units do
        let target = Path.Combine("/etc/systemd/system", unit + ".d", "active-publisher.conf")
        if File.Exists target && File.ReadAllText target <> content then fail "existing publisher guard differs"
    for unit in units do
        let directory = Path.Combine("/etc/systemd/system", unit + ".d")
        Directory.CreateDirectory directory |> ignore
        File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        let target = Path.Combine(directory, "active-publisher.conf")
        if not (File.Exists target) then
            let temporary = Path.Combine(directory, ".guard-" + Guid.NewGuid().ToString("N"))
            try
                File.WriteAllText(temporary, content)
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
                File.Move(temporary, target)
            finally
                if File.Exists temporary then File.Delete temporary
    checkedCommand 10000 "/usr/bin/systemctl" [ "daemon-reload" ] |> ignore
    for unit in units do
        let loaded = unitState unit "ExecCondition"
        if not (loaded.Contains(manager, StringComparison.Ordinal) && loaded.Contains(hostId, StringComparison.Ordinal)) then fail "installed publisher guard was not loaded"
    printfn "%s" (json {| status = "guards-installed"; host = hostId; units = units; servicesStarted = false |})

let private installLegacyWriterGuard values =
    only (Set.ofList [ "--host-id"; "--root"; "--version" ]) values
    if Environment.UserName <> "root" then fail "root required to install legacy writer guard"
    let hostId = required "--host-id" values
    // The currently selected writer must remain selected throughout this
    // initial installation. A missing or changed selector refuses the batch.
    if guard (Map.ofList [ "--host-id", hostId ]) <> 0 then fail "legacy writer is not selected"
    let root = required "--root" values |> safeDirectory
    let version = required "--version" values
    installManager (Map.ofList [ "--root", root; "--version", version ])
    let manager = Path.Combine(root, version, "TelemetryHostManager")
    installGuardDropins (Map.ofList [ "--host-id", hostId; "--manager", manager ])
    if guard (Map.ofList [ "--host-id", hostId ]) <> 0 then fail "legacy writer selector changed during installation"
    printfn "%s" (json {| status = "legacy-writer-guard-installed"; host = hostId; manager = manager; servicesStarted = false |})

let private runHostUpdater values =
    only (Set.ofList [ "--updater"; "--config" ]) values
    requireServiceAccount ()
    let updater = required "--updater" values |> Path.GetFullPath
    let config = required "--config" values |> Path.GetFullPath
    let output = checkedCommand 900000 "/usr/bin/python3" [ updater; "--config"; config ]
    printfn "%s" output

let private migrateHost values =
    only (Set.ofList [ "--updater"; "--config"; "--command-id"; "--expected-current-image"; "--target-qualified-release" ]) values
    requireServiceAccount ()
    let updater = required "--updater" values |> Path.GetFullPath
    let config = required "--config" values |> Path.GetFullPath
    let commandId = required "--command-id" values
    let expectedImage = required "--expected-current-image" values
    let target = required "--target-qualified-release" values
    if not (Regex.IsMatch(commandId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) then fail "invalid migration command ID"
    if not (Regex.IsMatch(expectedImage, "^sha256:[0-9a-f]{64}$")) then fail "invalid expected current image ID"
    if not (Regex.IsMatch(target, "^telemetry-host/v[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid qualified target release"
    let output = checkedCommand 900000 "/usr/bin/python3"
                    [ updater; "--config"; config; "--command-id"; commandId;
                      "--expected-current-image"; expectedImage;
                      "--target-qualified-release"; target; "--schema-migration" ]
    printfn "%s" output

let private retryHost values =
    only (Set.ofList [ "--updater"; "--config"; "--command-id"; "--expected-current-image"; "--target-qualified-release"; "--retry-failed-command" ]) values
    requireServiceAccount ()
    let updater = required "--updater" values |> Path.GetFullPath
    let config = required "--config" values |> Path.GetFullPath
    let commandId = required "--command-id" values
    let expectedImage = required "--expected-current-image" values
    let target = required "--target-qualified-release" values
    let failedCommand = required "--retry-failed-command" values
    let validId (value: string) = Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
    if not (validId commandId && validId failedCommand && commandId <> failedCommand) then fail "invalid retry command IDs"
    if not (Regex.IsMatch(expectedImage, "^sha256:[0-9a-f]{64}$")) then fail "invalid expected current image ID"
    if not (Regex.IsMatch(target, "^telemetry-host/v[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid qualified target release"
    let output = checkedCommand 900000 "/usr/bin/python3"
                    [ updater; "--config"; config; "--command-id"; commandId;
                      "--expected-current-image"; expectedImage;
                      "--target-qualified-release"; target;
                      "--retry-failed-command"; failedCommand ]
    printfn "%s" output

let private backup values =
    only (Set.ofList [ "--operator"; "--deployment"; "--backup-id"; "--host-unit" ]) values
    let name = required "--backup-id" values
    if not (Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,79}$")) then fail "invalid backup id"
    let unit = required "--host-unit" values
    requireServiceAccount ()
    let state = checkedCommand 10000 "/usr/bin/systemctl" [ "--user"; "show"; unit; "--property=ActiveState"; "--value" ]
    if state <> "inactive" then fail "Host service must be stopped before backup"
    let operatorPath = required "--operator" values |> Path.GetFullPath
    let deployment = required "--deployment" values |> Path.GetFullPath
    let output = checkedCommand 300000 operatorPath [ deployment; "backup"; name ]
    printfn "%s" output

let private prepareInert values =
    if Environment.UserName <> "root" then fail "root required to prepare inert Host"
    let allowed = Set.ofList [ "--systemadmin-root"; "--systemadmin-commit"; "--package"; "--manifest"; "--journal"; "--package-sha256"; "--manifest-sha256"; "--journal-sha256"; "--version"; "--engine-package"; "--engine-sha256"; "--engine-manifest"; "--engine-manifest-sha256"; "--engine-version"; "--engine-root" ]
    only allowed values
    let root = required "--systemadmin-root" values |> safeDirectory
    let verifier = Path.Combine(root, "Services/telemetry-host/telemetry_host_release.py")
    let assetKeys = Set.ofList [ "--package"; "--manifest"; "--journal"; "--package-sha256"; "--manifest-sha256"; "--journal-sha256"; "--version" ]
    let assetValues = values |> Map.filter (fun key _ -> Set.contains key assetKeys) |> Map.add "--verifier" verifier
    let engineValues = Map.ofList [ "--package", required "--engine-package" values; "--sha256", required "--engine-sha256" values; "--version", required "--engine-version" values; "--root", required "--engine-root" values ]
    verifyEngineManifest (required "--engine-manifest" values) (required "--engine-manifest-sha256" values) (required "--engine-version" values) (required "--engine-sha256" values)
    installEngine engineValues
    createAccount ()
    installHostFiles (Map.ofList [ "--systemadmin-root", root; "--commit", required "--systemadmin-commit" values ])
    stageHostAssets assetValues
    printfn "%s" (json {| status = "inert-host-prepared"; account = serviceAccount; servicesStarted = false; unitsEnabled = false |})

[<EntryPoint>]
let main arguments =
    try
        match Array.toList arguments with
        | "status" :: [] -> status (); 0
        | "source-fence-status" :: [] -> sourceFenceStatus (); 0
        | "guard" :: rest -> guard (options rest)
        | "install-guard-dropins" :: rest -> installGuardDropins (options rest); 0
        | "install-legacy-writer-guard" :: rest -> installLegacyWriterGuard (options rest); 0
        | "install-engine" :: rest -> installEngine (options rest); 0
        | "install-manager" :: rest -> installManager (options rest); 0
        | "create-host-account" :: [] -> createAccount (); 0
        | "prepare-rootless-runtime" :: [] -> prepareRootlessRuntime (); 0
        | "stage-host-assets" :: rest -> stageHostAssets (options rest); 0
        | "install-host-files" :: rest -> installHostFiles (options rest); 0
        | "update-host-files" :: rest -> updateHostFiles (options rest); 0
        | "build-host-image" :: rest -> buildHostImage (options rest); 0
        | "verify-host-release" :: rest -> verifyHostRelease (options rest); 0
        | "verify-engine-release" :: rest -> verifyEngineRelease (options rest); 0
        | "install-native-collector" :: rest -> installNativeCollector (options rest); 0
        | "install-responses-collector" :: rest -> installResponsesCollector (options rest); 0
        | "update-host" :: rest -> runHostUpdater (options rest); 0
        | "migrate-host" :: rest -> migrateHost (options rest); 0
        | "retry-host" :: rest -> retryHost (options rest); 0
        | "backup-stopped-host" :: rest -> backup (options rest); 0
        | "prepare-inert" :: rest -> prepareInert (options rest); 0
        | _ ->
            eprintfn "usage: telemetry-host-manager <status|source-fence-status|guard|install-guard-dropins|install-legacy-writer-guard|install-engine|install-manager|create-host-account|prepare-rootless-runtime|stage-host-assets|install-host-files|update-host-files|migrate-host|retry-host|build-host-image|verify-host-release|verify-engine-release|install-native-collector|install-responses-collector|backup-stopped-host|prepare-inert> [--name value ...]"
            2
    with error ->
        eprintfn "telemetry host manager refused: %s" error.Message
        3
