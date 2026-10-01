#r "System.Formats.Tar"

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

let fail message = eprintfn "PORTABLE_WORKSPACE_RELEASE_REFUSED %s" message; exit 2
let require condition message = if not condition then fail message
let utf8 = UTF8Encoding(false)
let sha256Bytes (bytes: byte array) = SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
let sha256 (path: string) = File.ReadAllBytes(path) |> sha256Bytes
let exactSha (value: string) = value.Length = 64 && value |> Seq.forall Uri.IsHexDigit

let capture (workingDirectory: string) (command: string) (arguments: string list) =
    let start = Diagnostics.ProcessStartInfo(command, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true)
    for argument in arguments do start.ArgumentList.Add argument
    use proc = Diagnostics.Process.Start start
    let output = proc.StandardOutput.ReadToEnd().Trim()
    proc.WaitForExit()
    let invocation = String.concat " " arguments
    require (proc.ExitCode = 0) $"command failed: {command} {invocation}"
    output

let parse arguments =
    let rec loop values remaining =
        match remaining with
        | (name: string) :: value :: rest when name.StartsWith("--", StringComparison.Ordinal) && not (Map.containsKey name values) ->
            loop (Map.add name value values) rest
        | [] -> values
        | _ -> fail "options must be unique --name value pairs"
    loop Map.empty arguments

let required name values =
    values |> Map.tryFind name |> Option.defaultWith (fun () -> fail $"missing {name}")

let property (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    require (element.TryGetProperty(name, &value)) $"missing JSON property {name}"
    value

let stringProperty name element =
    let value = property name element
    require (value.ValueKind = JsonValueKind.String) $"JSON property {name} must be a string"
    value.GetString()

let boolProperty name element =
    let value = property name element
    require (value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False) $"JSON property {name} must be a boolean"
    value.GetBoolean()

let intProperty name element =
    let value = property name element
    let mutable parsed = 0
    require (value.TryGetInt32(&parsed)) $"JSON property {name} must be an integer"
    parsed

let readJson path =
    require (File.Exists path) $"input is missing: {path}"
    JsonDocument.Parse(File.ReadAllBytes path)

let canonicalJson (node: JsonNode) =
    utf8.GetBytes(node.ToJsonString(JsonSerializerOptions(WriteIndented = false)) + "\n")

let readTarFiles path =
    let files = Collections.Generic.Dictionary<string, byte array>(StringComparer.Ordinal)
    use stream = File.OpenRead path
    use reader = new TarReader(stream, false)
    let mutable keepReading = true
    while keepReading do
        let entry = reader.GetNextEntry()
        if isNull entry then keepReading <- false
        elif not (isNull entry.DataStream) then
            let name = entry.Name.Replace('\\', '/')
            require (not (name.StartsWith("/", StringComparison.Ordinal)) && not (name.Split('/') |> Array.contains "..")) "OCI archive contains an unsafe path"
            require (not (files.ContainsKey name)) $"OCI archive repeats {name}"
            use memory = new MemoryStream()
            entry.DataStream.CopyTo memory
            require (memory.Length <= 268435456L) $"OCI entry is too large: {name}"
            files.Add(name, memory.ToArray())
    files

let validateOciArchive path expectedDigest expectedImageId =
    let files = readTarFiles path
    let requiredFile name =
        require (files.ContainsKey name) $"OCI archive is missing {name}"
        files[name]
    use layout = JsonDocument.Parse(requiredFile "oci-layout")
    require (stringProperty "imageLayoutVersion" layout.RootElement = "1.0.0") "OCI layout version changed"
    use index = JsonDocument.Parse(requiredFile "index.json")
    require (intProperty "schemaVersion" index.RootElement = 2) "OCI index schema changed"
    let manifests = property "manifests" index.RootElement
    require (manifests.ValueKind = JsonValueKind.Array && manifests.GetArrayLength() = 1) "OCI archive must contain exactly one image manifest"
    let descriptor = manifests[0]
    let digest = stringProperty "digest" descriptor
    require (digest = expectedDigest) "OCI index image digest differs from the qualified image"
    let mutable platform = Unchecked.defaultof<JsonElement>
    if descriptor.TryGetProperty("platform", &platform) then
        require (platform.ValueKind = JsonValueKind.Object) "OCI image descriptor platform must be an object"
        require (stringProperty "os" platform = "linux" && stringProperty "architecture" platform = "amd64") "OCI image descriptor platform must be linux/amd64 when present"
    let digestHex = digest.Replace("sha256:", "")
    require (exactSha digestHex) "OCI image digest is not SHA-256"
    let manifestBytes = requiredFile $"blobs/sha256/{digestHex}"
    require (sha256Bytes manifestBytes = digestHex) "OCI image manifest digest changed"
    use manifest = JsonDocument.Parse(manifestBytes)
    require (intProperty "schemaVersion" manifest.RootElement = 2) "OCI image manifest schema changed"
    let config = property "config" manifest.RootElement
    let configDigest = stringProperty "digest" config
    require (configDigest = expectedImageId) "OCI image config does not equal the qualified image ID"
    let descriptors = Seq.append [ config ] ((property "layers" manifest.RootElement).EnumerateArray())
    for item in descriptors do
        let value = stringProperty "digest" item
        let hex = value.Replace("sha256:", "")
        require (exactSha hex) "OCI blob digest is not SHA-256"
        let bytes = requiredFile $"blobs/sha256/{hex}"
        require (sha256Bytes bytes = hex) $"OCI blob digest changed: {value}"
    let configHex = configDigest.Replace("sha256:", "")
    use configJson = JsonDocument.Parse(requiredFile $"blobs/sha256/{configHex}")
    require (stringProperty "os" configJson.RootElement = "linux" && stringProperty "architecture" configJson.RootElement = "amd64") "OCI config platform changed"
    let imageConfig = property "config" configJson.RootElement
    let user = stringProperty "User" imageConfig
    require (user = "32768" || user = "32768:32768") "OCI image must select the fixed non-root user"

let addZipEntry (archive: ZipArchive) name (bytes: byte array) =
    let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
    entry.LastWriteTime <- DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
    entry.ExternalAttributes <- 0o100644 <<< 16
    use target = entry.Open()
    target.Write bytes

let createBundle target entries =
    use file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    use archive = new ZipArchive(file, ZipArchiveMode.Create, false, utf8)
    for name, bytes in entries |> List.sortBy fst do addZipEntry archive name bytes

let readZip target =
    use archive = ZipFile.OpenRead target
    archive.Entries
    |> Seq.map (fun entry ->
        use source = entry.Open()
        use memory = new MemoryStream()
        source.CopyTo memory
        entry.FullName, memory.ToArray())
    |> Map.ofSeq

let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let argv = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let command, options =
    match argv with
    | command :: rest -> command, parse rest
    | [] -> fail "usage: portable-workspace-release.fsx <prepare|verify> --source SHA --version <0.2.0|0.2.1> --package FILE --package-manifest FILE --image-archive FILE --image-manifest FILE --image-qualification FILE --executor-evidence FILE --output DIRECTORY"

let source = required "--source" options
let version = required "--version" options
let packagePath = required "--package" options |> Path.GetFullPath
let packageManifestPath = required "--package-manifest" options |> Path.GetFullPath
let imageArchivePath = required "--image-archive" options |> Path.GetFullPath
let imageManifestPath = required "--image-manifest" options |> Path.GetFullPath
let imageQualificationPath = required "--image-qualification" options |> Path.GetFullPath
let executorEvidencePath = required "--executor-evidence" options |> Path.GetFullPath
let output = required "--output" options |> Path.GetFullPath
let packageName = $"FS.GG.Coordination.Cli.{version}.nupkg"
let bundleName = $"portable-workspace-v1-{version}.zip"
let imageName = $"portable-workspace-linux-amd64-{version}.oci.tar"
let manifestName = "portable-workspace-release-manifest.json"
let bundlePath = Path.Combine(output, bundleName)
let retainedImagePath = Path.Combine(output, imageName)
let releaseManifestPath = Path.Combine(output, manifestName)

let validateInputs () =
    require (version = "0.2.0" || version = "0.2.1") "only reviewed portable release versions 0.2.0 and 0.2.1 may be prepared"
    require (source.Length = 40 && source |> Seq.forall Uri.IsHexDigit) "source must be an exact 40-character Git SHA"
    require (Path.GetFileName packagePath = packageName) "package filename changed"
    for path in [ packagePath; packageManifestPath; imageArchivePath; imageManifestPath; imageQualificationPath; executorEvidencePath ] do
        require (File.Exists path) $"input is missing: {path}"
    let tree = capture repo "git" [ "rev-parse"; source + "^{tree}" ]
    use packageManifest = readJson packageManifestPath
    let packageRoot = packageManifest.RootElement
    require (stringProperty "schema" packageRoot = "fsgg.coordination.callable-cli-release-preparation/1") "package manifest schema changed"
    require (stringProperty "packageId" packageRoot = "FS.GG.Coordination.Cli") "package identity changed"
    require (stringProperty "version" packageRoot = version && stringProperty "tag" packageRoot = $"v{version}") "package version or tag changed"
    require (stringProperty "sourceCommit" packageRoot = source && stringProperty "sourceTree" packageRoot = tree) "package source identity changed"
    require (stringProperty "packageSha256" packageRoot = sha256 packagePath) "package digest changed"
    require (not (boolProperty "publicationAuthorized" packageRoot) && not (boolProperty "tagAuthorized" packageRoot)) "preparation input cannot authorize publication or tagging"
    use package = ZipFile.OpenRead packagePath
    let executionAssemblies = package.Entries |> Seq.filter (fun entry -> entry.FullName.EndsWith("/FS.GG.Coordination.Orchestration.Execution.dll", StringComparison.Ordinal)) |> Seq.length
    require (executionAssemblies = 1) "package must contain exactly one Execution assembly"
    use qualification = readJson imageQualificationPath
    let qualificationRoot = qualification.RootElement
    require (stringProperty "candidateSha256" qualificationRoot = sha256 imageArchivePath) "qualified OCI archive digest changed"
    require (stringProperty "manifestSha256" qualificationRoot = sha256 imageManifestPath) "qualified image manifest digest changed"
    let imageReference = stringProperty "imageReference" qualificationRoot
    let imageId = stringProperty "imageId" qualificationRoot
    require (imageId.StartsWith("sha256:", StringComparison.Ordinal) && exactSha (imageId.Substring(7))) "qualified image ID is invalid"
    use imageManifest = readJson imageManifestPath
    let imageRoot = imageManifest.RootElement
    require (stringProperty "schema" imageRoot = "fsgg.portable-workspace-local-image/1") "image manifest schema changed"
    let imageSource = property "source" imageRoot
    require (stringProperty "revision" imageSource = source && stringProperty "tree" imageSource = tree) "image source identity changed"
    let image = property "image" imageRoot
    require (stringProperty "reference" image = imageReference && stringProperty "id" image = imageId) "image qualification identity changed"
    require (stringProperty "os" image = "linux" && stringProperty "architecture" image = "amd64") "image platform changed"
    let imageDigest = stringProperty "digest" image
    validateOciArchive imageArchivePath imageDigest imageId
    use evidence = readJson executorEvidencePath
    let evidenceRoot = evidence.RootElement
    require (stringProperty "schema" evidenceRoot = "fsgg.portable-workspace-executor-qualification/1") "executor evidence schema changed"
    require (stringProperty "imageReference" evidenceRoot = imageReference && stringProperty "imageId" evidenceRoot = imageId) "executor image identity changed"
    require (stringProperty "manifestSha256" evidenceRoot = sha256 imageManifestPath) "executor image manifest changed"
    require (stringProperty "outcome" evidenceRoot = "passed" && boolProperty "strictAcceptance" evidenceRoot) "executor qualification did not pass strictly"
    require (intProperty "passed" evidenceRoot = 6 && intProperty "failed" evidenceRoot = 0 && intProperty "unknown" evidenceRoot = 0) "executor qualification result changed"
    for name in [ "duplicateOrPendingWithoutRelaunch"; "sourceFenceBeforeWrite"; "preCancelledBeforeLaunch"; "hostEnvironmentInjectionCleared"; "missingImageRefusedBeforeStart"; "overflowArithmeticAccepted"; "interruptedRecoveryNoRelaunch" ] do
        require (boolProperty name evidenceRoot) $"executor qualification fence is false: {name}"
    require (intProperty "remainingExecutionRoots" evidenceRoot = 0) "executor cleanup is incomplete"
    tree, imageReference, imageId, imageDigest

let bundleEntries (source: string) (tree: string) (imageReference: string) (imageId: string) (imageDigest: string) =
    let tracked =
        [
            "contracts/portable-workspace/v1/README.md", "contracts/portable-workspace/v1/README.md"
            "contracts/portable-workspace/v1/toolchain-profile.schema.json", "contracts/portable-workspace/v1/toolchain-profile.schema.json"
            "contracts/portable-workspace/v1/command.schema.json", "contracts/portable-workspace/v1/command.schema.json"
            "contracts/portable-workspace/v1/result.schema.json", "contracts/portable-workspace/v1/result.schema.json"
            "contracts/portable-workspace/v1/examples/python.json", "contracts/portable-workspace/v1/examples/python.json"
            "contracts/portable-workspace/v1/examples/typescript-python.json", "contracts/portable-workspace/v1/examples/typescript-python.json"
            "contracts/portable-workspace/v1/examples/result-unknown.json", "contracts/portable-workspace/v1/examples/result-unknown.json"
            "tests/portable-workspace/image/Containerfile", "image/Containerfile"
            "tests/portable-workspace/image/inputs.json", "image/inputs.json"
            "tests/portable-workspace/image/README.md", "image/README.md"
        ]
        |> List.map (fun (sourcePath, targetPath) -> targetPath, File.ReadAllBytes(Path.Combine(repo, sourcePath)))
    let summary = JsonObject()
    summary.Add("schema", "fsgg.portable-workspace-release-qualification/1")
    summary.Add("sourceCommit", source)
    summary.Add("sourceTree", tree)
    summary.Add("imageReference", imageReference)
    summary.Add("imageId", imageId)
    summary.Add("imageDigest", imageDigest)
    summary.Add("imageManifestSha256", sha256 imageManifestPath)
    summary.Add("imageQualificationSha256", sha256 imageQualificationPath)
    summary.Add("imageArchiveSha256", sha256 imageArchivePath)
    summary.Add("executorEvidenceSha256", sha256 executorEvidencePath)
    summary.Add("strictAcceptance", true)
    summary.Add("passed", 6)
    summary.Add("failed", 0)
    summary.Add("unknown", 0)
    summary.Add("publicationAuthorized", false)
    summary.Add("activationAuthorized", false)
    let readme = utf8.GetBytes("# Portable workspace v1 release bundle\n\nThis finite bundle contains the frozen v1 contracts and the reviewed Linux amd64 recipe inputs bound by the release manifest. The OCI archive is a separate release asset. Preparation grants no publication, execution or receiver activation authority.\n")
    let entries = ("README.md", readme) :: ("qualification/summary.json", canonicalJson summary) :: tracked
    let checksums =
        entries
        |> List.sortBy fst
        |> List.map (fun (name, bytes) -> $"{sha256Bytes bytes}  {name}")
        |> String.concat "\n"
        |> fun value -> utf8.GetBytes(value + "\n")
    ("checksums.sha256", checksums) :: entries

let verifyOutputs (source: string) (tree: string) (imageReference: string) (imageId: string) (imageDigest: string) =
    for path in [ bundlePath; retainedImagePath; releaseManifestPath ] do require (File.Exists path) $"release output is missing: {path}"
    require (sha256 retainedImagePath = sha256 imageArchivePath) "retained OCI archive differs from the qualified bytes"
    validateOciArchive retainedImagePath imageDigest imageId
    let expected = bundleEntries source tree imageReference imageId imageDigest |> Map.ofList
    let actual = readZip bundlePath
    require (actual.Count = expected.Count) "portable bundle entry count changed"
    for KeyValue(name, bytes) in expected do
        require (Map.tryFind name actual |> Option.exists (fun value -> value.AsSpan().SequenceEqual(bytes))) $"portable bundle entry changed: {name}"
    use release = readJson releaseManifestPath
    let root = release.RootElement
    require (stringProperty "schema" root = "fsgg.portable-workspace-release/1") "release manifest schema changed"
    require (stringProperty "sourceCommit" root = source && stringProperty "sourceTree" root = tree) "release source changed"
    require (stringProperty "version" root = version && stringProperty "tag" root = $"v{version}") "release version changed"
    require (stringProperty "packageSha256" root = sha256 packagePath) "release package digest changed"
    require (stringProperty "packageManifestSha256" root = sha256 packageManifestPath) "release package provenance changed"
    require (stringProperty "bundleSha256" root = sha256 bundlePath) "release bundle digest changed"
    require (stringProperty "imageArchiveSha256" root = sha256 retainedImagePath) "release image archive digest changed"
    require (stringProperty "imageReference" root = imageReference && stringProperty "imageId" root = imageId) "release image identity changed"
    require (stringProperty "imageDigest" root = imageDigest) "release image digest changed"
    require (stringProperty "imageQualificationSha256" root = sha256 imageQualificationPath) "release image qualification changed"
    require (not (boolProperty "publicationAuthorized" root) && not (boolProperty "tagAuthorized" root) && not (boolProperty "activationAuthorized" root)) "release preparation cannot authorize effects"
    printfn "PORTABLE_WORKSPACE_RELEASE_VERIFIED source=%s package=%s bundle=%s image=%s" source (sha256 packagePath) (sha256 bundlePath) (sha256 retainedImagePath)

let tree, imageReference, imageId, imageDigest = validateInputs ()
match command with
| "prepare" ->
    require (capture repo "git" [ "rev-parse"; "HEAD" ] = source) "source does not equal HEAD"
    require (String.IsNullOrWhiteSpace(capture repo "git" [ "status"; "--porcelain" ])) "source worktree is not clean"
    require (not (Directory.Exists output) || Directory.GetFileSystemEntries(output).Length = 0) "output must be empty"
    Directory.CreateDirectory output |> ignore
    createBundle bundlePath (bundleEntries source tree imageReference imageId imageDigest)
    File.Copy(imageArchivePath, retainedImagePath)
    let manifest = JsonObject()
    manifest.Add("schema", "fsgg.portable-workspace-release/1")
    manifest.Add("sourceCommit", source)
    manifest.Add("sourceTree", tree)
    manifest.Add("version", version)
    manifest.Add("tag", $"v{version}")
    manifest.Add("packageName", packageName)
    manifest.Add("packageSha256", sha256 packagePath)
    manifest.Add("packageManifestSha256", sha256 packageManifestPath)
    manifest.Add("bundleName", bundleName)
    manifest.Add("bundleSha256", sha256 bundlePath)
    manifest.Add("imageArchiveName", imageName)
    manifest.Add("imageArchiveSha256", sha256 retainedImagePath)
    manifest.Add("imageReference", imageReference)
    manifest.Add("imageId", imageId)
    manifest.Add("imageDigest", imageDigest)
    manifest.Add("imageManifestSha256", sha256 imageManifestPath)
    manifest.Add("imageQualificationSha256", sha256 imageQualificationPath)
    manifest.Add("executorEvidenceSha256", sha256 executorEvidencePath)
    manifest.Add("publicationAuthorized", false)
    manifest.Add("tagAuthorized", false)
    manifest.Add("activationAuthorized", false)
    File.WriteAllBytes(releaseManifestPath, canonicalJson manifest)
    verifyOutputs source tree imageReference imageId imageDigest
    printfn "PORTABLE_WORKSPACE_RELEASE_PREPARED source=%s output=%s" source output
| "verify" -> verifyOutputs source tree imageReference imageId imageDigest
| _ -> fail "command must be prepare or verify"
