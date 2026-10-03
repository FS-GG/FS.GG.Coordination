module NativeCollectorInstallationV3

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Runtime.InteropServices

let private fail message =
    raise (InvalidOperationException message)

let private sha256 (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private fileSha (path: string) = File.ReadAllBytes path |> sha256
let private utf8 (text: string) = Encoding.UTF8.GetBytes text
let private canonicalSourceRevision = "a1310e14a60d1d025dd3fa9f404970890503d092"
let private canonicalModuleSha256 = "8d6a33beae9a4de84fa7a703809e9b1a1656359a085f92091cf56de3b77fd3ba"

let private canonicalModuleInput (path: string) =
    let info = FileInfo path
    info.Exists
    && Path.IsPathFullyQualified path
    && Path.GetFullPath(path) = path
    && Path.GetFileName(path) = "learn_01_native_source.py"
    && isNull info.LinkTarget
    && fileSha path = canonicalModuleSha256

let private writePrivate (path: string) (bytes: byte array) =
    File.WriteAllBytes(path, bytes)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

let private writeJson path value =
    JsonSerializer.SerializeToUtf8Bytes value |> writePrivate path

let private privateDirectory path =
    Directory.CreateDirectory path |> ignore
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let private verifyCanonicalInputRefusals canonicalModule =
    let root = Path.Combine(Path.GetTempPath(), "native-collector-v3-source-" + Guid.NewGuid().ToString("N"))
    privateDirectory root
    try
        let wrongName = Path.Combine(root, "renamed.py")
        File.Copy(canonicalModule, wrongName)
        let changed = Path.Combine(root, "learn_01_native_source.py")
        File.WriteAllText(changed, "changed canonical source")
        let linkedRoot = Path.Combine(root, "linked")
        privateDirectory linkedRoot
        let linked = Path.Combine(linkedRoot, "learn_01_native_source.py")
        File.CreateSymbolicLink(linked, canonicalModule) |> ignore
        if canonicalModuleInput wrongName || canonicalModuleInput changed || canonicalModuleInput linked then
            fail "canonical native verifier source inversions were accepted"
    finally
        if Directory.Exists root then Directory.Delete(root, true)

type private Fixture =
    {
        Root: string
        Config: string
        Sidecar: string
        Receipt: string
        SourceReference: string
        RuntimeManifest: string
        Runtime: string
        Module: string
        Profile: string
        Collector: string
        CodexHome: string
        Evidence: string
        RuntimeSha: string
        ModuleSha: string
        ManifestSha: string
        CollectorSha: string
    }

let private runtimeManifestBytes runtime modulePath extraEntries =
    let entries =
        [|
            yield
                {|
                    path = runtime
                    bytes = FileInfo(runtime).Length
                    sha256 = fileSha runtime
                |}
            yield
                {|
                    path = modulePath
                    bytes = FileInfo(modulePath).Length
                    sha256 = fileSha modulePath
                |}
            yield! extraEntries
        |]
        |> Array.sortBy _.path

    JsonSerializer.SerializeToUtf8Bytes
        {|
            schema = "fsgg.telemetry.native-verifier-runtime/1"
            sourceRevision = canonicalSourceRevision
            runtimeImageDigest = "sha256:" + sha256 (utf8 "disposable-manager-fixture-not-qualified-production")
            runtimeExecutablePath = runtime
            modulePath = modulePath
            files = entries
        |}

let private sourceReferenceBytes codexHome developmentTarget readerProfileSha manifestSha =
    JsonSerializer.SerializeToUtf8Bytes
        {|
            schema = "fsgg.telemetry.persistent-source-references/3"
            profileSha256 = sha256 (utf8 "disposable-manager-fixture-profile")
            nativeSourceVolume = "learn-native-source-v1"
            developmentTarget = developmentTarget
            collectorReadOnlyTarget = codexHome
            readerProfileSha256 = readerProfileSha
            captureQualified = false
            verifierRuntimeManifestSha256 = manifestSha
        |}

let private prepareFixture root canonicalModule =
    if not (canonicalModuleInput canonicalModule) then
        fail "canonical native verifier source custody differs"

    if Directory.Exists root || File.Exists root then
        fail ("fixture root already exists: " + root)

    privateDirectory root
    let privateRoot = Path.Combine(root, "private")
    let codexHome = Path.Combine(privateRoot, "codex-home")
    let evidence = Path.Combine(privateRoot, "evidence")
    let runtimeRoot = Path.Combine(privateRoot, "runtime")
    let developmentTarget = Path.Combine(privateRoot, "development-native-source")

    [ privateRoot; codexHome; evidence; runtimeRoot; developmentTarget ]
    |> List.iter privateDirectory

    let secret = Path.Combine(privateRoot, "collector.secret")
    let config = Path.Combine(privateRoot, "host.json")
    let collector = Path.Combine(privateRoot, "codex")
    let runtime = Path.Combine(runtimeRoot, "fixture-verifier-runtime")
    let modulePath = Path.Combine(runtimeRoot, "learn_01_native_source.py")
    let manifest = Path.Combine(runtimeRoot, "native-verifier-runtime.json")
    let profile = Path.Combine(evidence, "fixed-native-capability-profile.json")
    let sourceReference = Path.Combine(privateRoot, "source-reference.json")

    writePrivate secret (utf8 (String.replicate 32 "s"))
    writePrivate collector (utf8 "#!/bin/sh\nexit 0\n")
    writePrivate runtime (utf8 "#!/bin/sh\n# disposable manager fixture; not a qualified production runtime\nexit 97\n")
    File.SetUnixFileMode(collector, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    File.SetUnixFileMode(runtime, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    File.Copy(canonicalModule, modulePath, false)
    File.SetUnixFileMode(modulePath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    let moduleSha = fileSha modulePath

    if moduleSha <> canonicalModuleSha256 then
        fail "canonical native verifier module digest differs"

    writePrivate profile (utf8 "{\"schema\":\"fsgg.learn.disposable-reader-profile-fixture/1\"}\n")

    writeJson
        config
        {|
            Schema = "fsgg.telemetry.host-config/2"
            Credentials =
                [|
                    {|
                        Reference = "collector"
                        SecretFile = secret
                        WorkspaceId = "workspace"
                        ProducerId = "protected-collector"
                        StreamId = "native-inventory"
                        Role = "native-collector"
                        GrantId = "learn-native-collector"
                        GrantGeneration = 1
                        Revoked = false
                    |}
                |]
        |}

    let manifestBytes = runtimeManifestBytes runtime modulePath [||]
    writePrivate manifest manifestBytes
    let manifestSha = sha256 manifestBytes
    writePrivate sourceReference (sourceReferenceBytes codexHome developmentTarget (fileSha profile) manifestSha)

    {
        Root = root
        Config = config
        Sidecar = config + ".native-collector.json"
        Receipt = config + ".native-collector.receipt.json"
        SourceReference = sourceReference
        RuntimeManifest = manifest
        Runtime = runtime
        Module = modulePath
        Profile = profile
        Collector = collector
        CodexHome = codexHome
        Evidence = evidence
        RuntimeSha = fileSha runtime
        ModuleSha = moduleSha
        ManifestSha = manifestSha
        CollectorSha = fileSha collector
    }

let private managerArguments fixture version =
    [
        "install-native-collector"
        "--host-config"
        fixture.Config
        "--credential-reference"
        "collector"
        "--executable"
        fixture.Collector
        "--codex-home"
        fixture.CodexHome
        "--evidence-root"
        fixture.Evidence
        "--provider"
        "openai"
        "--model"
        "gpt-6-sol"
        "--effort"
        "medium"
        "--installation-version"
        version
        "--executable-sha256"
        fixture.CollectorSha
        "--verifier-runtime"
        fixture.Runtime
        "--verifier-runtime-sha256"
        fixture.RuntimeSha
        "--verifier-module"
        fixture.Module
        "--verifier-module-sha256"
        fixture.ModuleSha
        "--verifier-runtime-manifest"
        fixture.RuntimeManifest
        "--verifier-runtime-manifest-sha256"
        fixture.ManifestSha
    ]

let private managerV2Arguments fixture =
    [
        "install-native-collector"
        "--host-config"
        fixture.Config
        "--credential-reference"
        "collector"
        "--executable"
        fixture.Collector
        "--codex-home"
        fixture.CodexHome
        "--evidence-root"
        fixture.Evidence
        "--provider"
        "openai"
        "--model"
        "gpt-6-sol"
        "--effort"
        "medium"
        "--installation-version"
        "2"
        "--executable-sha256"
        fixture.CollectorSha
    ]

let private replaceOption name value arguments =
    let rec replace remaining =
        match remaining with
        | key :: _ :: tail when key = name -> key :: value :: tail
        | head :: tail -> head :: replace tail
        | [] -> fail ("missing test option " + name)

    replace arguments

let private removeOption name arguments =
    let rec remove remaining =
        match remaining with
        | key :: _ :: tail when key = name -> tail
        | head :: tail -> head :: remove tail
        | [] -> fail ("missing test option " + name)

    remove arguments

let private run manager arguments =
    let start = ProcessStartInfo("dotnet")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add manager
    arguments |> List.iter start.ArgumentList.Add
    use child = Process.Start start

    if isNull child then
        fail "manager did not start"

    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        fail "manager timed out"

    child.ExitCode, output.Result.Trim(), error.Result.Trim()

let private expectRefusal label manager arguments (expected: string) =
    let code, _, error = run manager arguments

    if code <> 3 || not (error.Contains(expected, StringComparison.Ordinal)) then
        fail (label + " did not refuse as expected: " + error)

let private exactNames expected (root: JsonElement) =
    let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    if names <> Set.ofList expected then
        fail "emitted JSON field set differs"

let private verifyPositive manager fixture =
    let arguments = managerArguments fixture "3"
    let firstCode, firstOutput, firstError = run manager arguments

    if firstCode <> 0 then
        fail ("v3 installation failed: " + firstError)

    let secondCode, secondOutput, secondError = run manager arguments

    if secondCode <> 0 || secondOutput <> firstOutput then
        fail ("v3 reinstall was not idempotent: " + secondError)

    use receiptDocument = JsonDocument.Parse(File.ReadAllBytes fixture.Receipt)
    let receipt = receiptDocument.RootElement

    exactNames
        [
            "schema"
            "status"
            "ownerUid"
            "hostConfigSha256"
            "sidecarSha256"
            "executableSha256"
            "credentialReference"
            "workspaceId"
            "producerId"
            "streamId"
            "grantId"
            "grantGeneration"
            "sourceVerification"
            "snapshotOrigin"
            "sharedCostCompleteness"
            "activationAuthorized"
            "sourceReferenceSha256"
            "verifierRuntimeManifestSha256"
        ]
        receipt

    if
        receipt.GetProperty("schema").GetString()
        <> "fsgg.telemetry.native-collector-installation-receipt/3"
        || receipt.GetProperty("sourceReferenceSha256").GetString()
           <> fileSha fixture.SourceReference
        || receipt.GetProperty("verifierRuntimeManifestSha256").GetString()
           <> fixture.ManifestSha
        || receipt.GetProperty("activationAuthorized").GetBoolean()
    then
        fail "v3 receipt binding differs"

    use sidecarDocument = JsonDocument.Parse(File.ReadAllBytes fixture.Sidecar)
    let sidecar = sidecarDocument.RootElement

    exactNames
        [
            "Schema"
            "CredentialReference"
            "ExecutablePath"
            "CodexHome"
            "EvidenceRoot"
            "Provider"
            "Model"
            "Effort"
            "ExecutableSha256"
            "NativeVerifier"
        ]
        sidecar

    let verifier = sidecar.GetProperty("NativeVerifier")

    use manifestDocument = JsonDocument.Parse(File.ReadAllBytes fixture.RuntimeManifest)
    let manifest = manifestDocument.RootElement

    exactNames
        [
            "RuntimeExecutablePath"
            "RuntimeExecutableSha256"
            "ModulePath"
            "ModuleSha256"
            "RuntimeManifestPath"
            "RuntimeManifestSha256"
        ]
        verifier

    if
        sidecar.GetProperty("Schema").GetString()
        <> "fsgg.telemetry.native-collector-installation/3"
        || verifier.GetProperty("ModuleSha256").GetString() <> fixture.ModuleSha
        || manifest.GetProperty("sourceRevision").GetString() <> canonicalSourceRevision
        || manifest.GetProperty("modulePath").GetString() <> fixture.Module
    then
        fail "v3 sidecar binding differs"

    firstOutput

let private verifyRefusals manager fixture =
    let arguments = managerArguments fixture "3"
    expectRefusal "wrong-version flags" manager (managerArguments fixture "2") "valid only for installation version 3"

    let runtimeBytes = File.ReadAllBytes fixture.Runtime
    File.AppendAllText(fixture.Runtime, "changed")
    expectRefusal "changed runtime" manager arguments "runtime SHA-256 differs"
    writePrivate fixture.Runtime runtimeBytes
    File.SetUnixFileMode(fixture.Runtime, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let missing = fixture.Runtime + ".missing"
    File.Move(fixture.Runtime, missing)
    expectRefusal "missing runtime" manager arguments "runtime executable is unsafe"
    File.Move(missing, fixture.Runtime)

    let sourceBytes = File.ReadAllBytes fixture.SourceReference
    use sourceDocument = JsonDocument.Parse sourceBytes
    let root = sourceDocument.RootElement

    writeJson
        fixture.SourceReference
        {|
            schema = root.GetProperty("schema").GetString()
            profileSha256 = root.GetProperty("profileSha256").GetString()
            nativeSourceVolume = root.GetProperty("nativeSourceVolume").GetString()
            developmentTarget = root.GetProperty("developmentTarget").GetString()
            collectorReadOnlyTarget = root.GetProperty("collectorReadOnlyTarget").GetString()
            readerProfileSha256 = root.GetProperty("readerProfileSha256").GetString()
            captureQualified = false
            verifierRuntimeManifestSha256 = sha256 (utf8 "wrong-manifest-binding")
        |}

    expectRefusal "source binding" manager arguments "source reference binding differs"
    writePrivate fixture.SourceReference sourceBytes

    let manifestBytes = File.ReadAllBytes fixture.RuntimeManifest
    writePrivate fixture.RuntimeManifest (Array.zeroCreate (1024 * 1024 + 1))
    expectRefusal "manifest byte bound" manager arguments "exceeds its byte bound"
    writePrivate fixture.RuntimeManifest manifestBytes

    expectRefusal
        "partial v3 flags"
        manager
        (removeOption "--verifier-module-sha256" arguments)
        "all version 3 native verifier options"

    let excessEntries =
        Array.init 4097 (fun index ->
            {|
                path = Path.Combine(fixture.Root, sprintf "bounded-missing-%04d" index)
                bytes = 1L
                sha256 = sha256 (utf8 (string index))
            |})

    let excessManifest =
        runtimeManifestBytes fixture.Runtime fixture.Module excessEntries

    writePrivate fixture.RuntimeManifest excessManifest

    let excessArguments =
        replaceOption "--verifier-runtime-manifest-sha256" (sha256 excessManifest) arguments

    expectRefusal "manifest file count" manager excessArguments "file count exceeds its bound"
    writePrivate fixture.RuntimeManifest manifestBytes

    let huge = Path.Combine(fixture.Root, "aaa-disposable-sparse-bound")

    use hugeStream =
        new FileStream(huge, FileMode.CreateNew, FileAccess.Write, FileShare.None)

    hugeStream.SetLength(512L * 1024L * 1024L + 1L)
    hugeStream.Flush true
    hugeStream.Close()
    File.SetUnixFileMode(huge, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    let hugeManifest =
        runtimeManifestBytes
            fixture.Runtime
            fixture.Module
            [|
                {|
                    path = huge
                    bytes = FileInfo(huge).Length
                    sha256 = sha256 (utf8 "not-read-after-bound")
                |}
            |]

    writePrivate fixture.RuntimeManifest hugeManifest

    let hugeArguments =
        replaceOption "--verifier-runtime-manifest-sha256" (sha256 hugeManifest) arguments

    expectRefusal "manifest closure byte bound" manager hugeArguments "closure exceeds its byte bound"
    File.Delete huge
    writePrivate fixture.RuntimeManifest manifestBytes

let private verifyNoPromotion manager canonicalModule =
    let root =
        Path.Combine(Path.GetTempPath(), "native-collector-v3-promotion-" + Guid.NewGuid().ToString("N"))

    let fixture = prepareFixture root canonicalModule

    try
        let code, _, error = run manager (managerV2Arguments fixture)

        if code <> 0 then
            fail ("v2 promotion setup failed: " + error)

        expectRefusal "v2-to-v3 promotion" manager (managerArguments fixture "3") "cannot be promoted in place"
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

let private freezeSummary fixture managerReceipt =
    let names =
        [
            "config", fixture.Config
            "sourceReference", fixture.SourceReference
            "runtimeManifest", fixture.RuntimeManifest
            "sidecar", fixture.Sidecar
            "managerReceipt", fixture.Receipt
        ]

    JsonSerializer.Serialize
        {|
            schema = "fsgg.telemetry.native-collector-manager-fixture/1"
            disposition = "disposable-fixture-not-qualified-production"
            managerReceipt = managerReceipt
            files =
                names
                |> List.map (fun (name, path) ->
                    {|
                        name = name
                        path = path
                        bytes = FileInfo(path).Length
                        sha256 = fileSha path
                    |})
        |}

[<DllImport("libc", EntryPoint = "link", SetLastError = true)>]
extern int private createSourceFixtureHardLink(string original, string linked)

// SOURCE-ONLY mode: the focused script supplies delegates extracted verbatim from the
// protected original and candidate Manager validator. No Manager installation is invoked.
let sourceOnlyEmptyDependencyControls (uid: int) validateOriginal validateCandidate canonicalModule pythonRoot root =
    if not (canonicalModuleInput canonicalModule) then fail "source-only canonical module differs"
    if Directory.Exists root || File.Exists root then fail "source-only fixture already exists"
    privateDirectory root
    let runtime = Path.Combine(root, "required-runtime")
    let modulePath = Path.Combine(root, "learn_01_native_source.py")
    let manifest = Path.Combine(root, "runtime-manifest.json")
    let runtimeBytes = utf8 "positive validator fixture; never executed"
    writePrivate runtime runtimeBytes
    File.SetUnixFileMode(runtime, enum<UnixFileMode> 0o700)
    File.Copy(canonicalModule, modulePath)
    File.SetUnixFileMode(modulePath, enum<UnixFileMode> 0o600)
    let sourceRows = ResizeArray<_>()
    let dependencies =
        [|
            "compression/__init__.py"
            "compression/_common/__init__.py"
            "email/mime/__init__.py"
            "pydoc_data/__init__.py"
            "urllib/__init__.py"
        |]
        |> Array.map (fun relative ->
            let original = Path.Combine(pythonRoot, relative)
            let info = FileInfo original
            if not info.Exists || not (isNull info.LinkTarget) || info.Length <> 0L
               || fileSha original <> "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" then
                fail "source-only actual initializer differs"
            let target = Path.Combine(root, "dependencies", relative)
            privateDirectory (Path.GetDirectoryName target)
            File.Copy(original, target)
            File.SetUnixFileMode(target, enum<UnixFileMode> 0o444)
            sourceRows.Add {| sourcePath = original; sourceMode = int (File.GetUnixFileMode original);
                             path = target; bytes = 0L; sha256 = fileSha target; mode = "0444" |}
            {| path = target; bytes = 0L; sha256 = fileSha target |})
    let results = ResizeArray<_>()
    let emptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    let writeManifest entries = runtimeManifestBytes runtime modulePath entries |> writePrivate manifest
    let call validate = validate uid runtime (fileSha runtime) modulePath (fileSha modulePath) manifest (fileSha manifest)
    let expect label accepted action =
        let mutable refused = None
        try action () |> ignore with :? InvalidOperationException as error -> refused <- Some error.Message
        if accepted = refused.IsSome then fail ("source-only expectation failed: " + label)
        results.Add {| case = label; accepted = accepted; refusal = refused |}
    try
        writeManifest [||]
        expect "original positive required files" true (fun () -> call validateOriginal)
        expect "candidate positive required files" true (fun () -> call validateCandidate)
        writeManifest dependencies
        expect "original actual empty initializer rejection" false (fun () -> call validateOriginal)
        expect "candidate actual readonly initializer census" true (fun () -> call validateCandidate)
        for digest in [ String.replicate 64 "a"; String.replicate 64 "0"; "bad" ] do
            writeManifest [| {| dependencies[0] with sha256 = digest |} |]
            expect ("wrong empty digest " + digest.Substring(0, 3)) false (fun () -> call validateCandidate)
        for mode in [ 0o400; 0o600; 0o555; 0o755; 0o4444 ] do
            writeManifest dependencies
            File.SetUnixFileMode(dependencies[0].path, enum<UnixFileMode> mode)
            expect ("non-readonly empty mode " + string mode) false (fun () -> call validateCandidate)
            File.SetUnixFileMode(dependencies[0].path, enum<UnixFileMode> 0o444)
        for requiredPath in [ runtime; modulePath; manifest ] do
            writeManifest [| {| path = requiredPath; bytes = 0L; sha256 = emptySha |} |]
            expect ("empty required row " + Path.GetFileName requiredPath) false (fun () -> call validateCandidate)
        writeManifest dependencies
        let malformedSize = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes manifest).AsObject()
        let malformedRow = malformedSize["files"].AsArray() |> Seq.find (fun row -> row["path"].GetValue<string>() = dependencies[0].path)
        malformedRow["bytes"] <- System.Text.Json.Nodes.JsonValue.Create("0")
        malformedSize.ToJsonString() |> utf8 |> writePrivate manifest
        expect "string dependency size" false (fun () -> call validateCandidate)
        writeManifest [| {| dependencies[0] with bytes = -1L |} |]
        expect "negative dependency size" false (fun () -> call validateCandidate)
        writeManifest [| {| dependencies[0] with bytes = 1L |} |]
        expect "declared extent differs" false (fun () -> call validateCandidate)
        writeManifest [| dependencies[0]; dependencies[0] |]
        expect "duplicate census row" false (fun () -> call validateCandidate)
        writeManifest (Array.create 4097 dependencies[0])
        expect "over-bound census" false (fun () -> call validateCandidate)
        writeManifest [| {| dependencies[0] with bytes = 512L * 1024L * 1024L + 1L |} |]
        expect "over-bound aggregate" false (fun () -> call validateCandidate)
        writeManifest dependencies
        let originalBytes = File.ReadAllBytes manifest
        let rewriteWithoutRequiredCode () =
            use document = JsonDocument.Parse originalBytes
            let node = System.Text.Json.Nodes.JsonNode.Parse(originalBytes).AsObject()
            let files = node["files"].AsArray()
            let withoutModule = files |> Seq.filter (fun row -> row["path"].GetValue<string>() <> modulePath) |> Seq.map (fun row -> row.DeepClone()) |> Seq.toArray
            node["files"] <- System.Text.Json.Nodes.JsonArray(withoutModule)
            node.ToJsonString() |> utf8 |> writePrivate manifest
        rewriteWithoutRequiredCode ()
        expect "required module omitted" false (fun () -> call validateCandidate)
        for requiredPath in [ runtime; modulePath; manifest ] do
            let prior = File.ReadAllBytes requiredPath
            File.WriteAllBytes(requiredPath, [||])
            expect ("empty required physical file " + Path.GetFileName requiredPath) false (fun () -> call validateCandidate)
            File.WriteAllBytes(requiredPath, prior)
        writeManifest dependencies
        let linked = dependencies[0].path
        File.Delete linked
        File.CreateSymbolicLink(linked, sourceRows[0].sourcePath) |> ignore
        expect "symlink empty dependency" false (fun () -> call validateCandidate)
        File.Delete linked
        File.Copy(sourceRows[0].sourcePath, linked)
        File.SetUnixFileMode(linked, enum<UnixFileMode> 0o444)
        let hardLink = Path.Combine(root, "extra-empty-link")
        if createSourceFixtureHardLink(linked, hardLink) <> 0 then fail "source-only hardlink fixture failed"
        expect "hardlink empty dependency" false (fun () -> call validateCandidate)
        File.Delete hardLink
        expect "restored actual initializer census" true (fun () -> call validateCandidate)
        {| schema = "fsgg.learn.manager-empty-source-controls/1"; scope = "production source slices only; no installer or Host qualification";
           sourceInitializers = sourceRows.ToArray(); results = results.ToArray(); installerExecuted = false |}
    finally
        for path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
            File.SetUnixFileMode(path, enum<UnixFileMode> 0o600)
        Directory.Delete(root, true)

[<EntryPoint>]
let main arguments =
    try
        if arguments.Length <> 3 then
            eprintfn "usage: NativeCollectorInstallationV3 MANAGER.dll CANONICAL_MODULE.py FROZEN_FIXTURE_ROOT"
            2
        else
            let manager = Path.GetFullPath arguments[0]
            let canonicalModule = Path.GetFullPath arguments[1]
            let frozenRoot = Path.GetFullPath arguments[2]
            verifyCanonicalInputRefusals canonicalModule
            let fixture = prepareFixture frozenRoot canonicalModule
            let managerReceipt = verifyPositive manager fixture
            verifyRefusals manager fixture
            verifyNoPromotion manager canonicalModule
            printfn "%s" (freezeSummary fixture managerReceipt)
            0
    with error ->
        eprintfn "v3 manager behavioral harness failed: %s" error.Message
        1
