module NativeCollectorInstallationV3

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

let private fail message =
    raise (InvalidOperationException message)

let private sha256 (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private fileSha (path: string) = File.ReadAllBytes path |> sha256
let private utf8 (text: string) = Encoding.UTF8.GetBytes text

let private writePrivate (path: string) (bytes: byte array) =
    File.WriteAllBytes(path, bytes)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

let private writeJson path value =
    JsonSerializer.SerializeToUtf8Bytes value |> writePrivate path

let private privateDirectory path =
    Directory.CreateDirectory path |> ignore
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

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
            sourceRevision = "02bfd323ba8f272d668e30f77964281b3c8c9184"
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

    if moduleSha <> "8d6a33beae9a4de84fa7a703809e9b1a1656359a085f92091cf56de3b77fd3ba" then
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
            let fixture = prepareFixture frozenRoot canonicalModule
            let managerReceipt = verifyPositive manager fixture
            verifyRefusals manager fixture
            verifyNoPromotion manager canonicalModule
            printfn "%s" (freezeSummary fixture managerReceipt)
            0
    with error ->
        eprintfn "v3 manager behavioral harness failed: %s" error.Message
        1
