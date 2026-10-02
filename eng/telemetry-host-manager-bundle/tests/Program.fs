module TelemetryHostManagerBundleTests

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open FS.GG.Telemetry.Host.Manager

let require condition message = if not condition then failwith message
let expectRefusal name action =
    try action(); failwith (name+" was accepted")
    with :? InvalidOperationException -> ()

let copyDirectory (source: string) (target: string) =
    Directory.CreateDirectory target |> ignore
    File.SetUnixFileMode(target,File.GetUnixFileMode source)
    for directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories) do
        let copied = Path.Combine(target,Path.GetRelativePath(source,directory))
        Directory.CreateDirectory copied |> ignore
        File.SetUnixFileMode(copied,File.GetUnixFileMode directory)
    for file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories) do
        let copied = Path.Combine(target,Path.GetRelativePath(source,file))
        File.Copy(file,copied)
        File.SetUnixFileMode(copied,File.GetUnixFileMode file)

let selected runtimeRoot sdkRoot sdkExecutable : Bundle.SelectionInput =
    { runtime=
        { image=Bundle.selectedRuntimeImage
          manifestDigest=Bundle.selectedRuntimeManifestDigest
          configDigest=Bundle.selectedRuntimeConfigDigest
          os=Bundle.selectedOs
          architecture=Bundle.selectedArchitecture }
      runtimeRoot=runtimeRoot
      runtimeCanonicalRoot=Bundle.canonicalRuntimeRoot
      sdk=
        { image=Bundle.selectedSdkImage
          manifestDigest=Bundle.selectedSdkManifestDigest
          configDigest=Bundle.selectedSdkConfigDigest
          os=Bundle.selectedOs
          architecture=Bundle.selectedArchitecture }
      sdkRoot=sdkRoot
      sdkExecutable=sdkExecutable }

type Mutation = Missing | Extra | Changed | Traversal | Duplicate | Link

let mutateArchive source target mutation =
    use input = ZipFile.OpenRead source
    use output = ZipFile.Open(target,ZipArchiveMode.Create)
    let mutable duplicated: (string*byte array*DateTimeOffset*int) option = None
    for entry in input.Entries do
        use stream = entry.Open()
        use memory = new MemoryStream()
        stream.CopyTo memory
        let mutable bytes = memory.ToArray()
        let skip = mutation = Missing && entry.FullName.EndsWith("TelemetryHostManager.deps.json",StringComparison.Ordinal)
        if not skip then
            if mutation = Changed && entry.FullName.EndsWith("TelemetryHostManager.dll",StringComparison.Ordinal) then
                bytes[0] <- bytes[0] ^^^ 0xFFuy
            let copied = output.CreateEntry(entry.FullName,CompressionLevel.SmallestSize)
            copied.LastWriteTime <- entry.LastWriteTime
            copied.ExternalAttributes <- if mutation = Link && entry.FullName.EndsWith("TelemetryHostManager.dll",StringComparison.Ordinal) then (0o120777 <<< 16) else entry.ExternalAttributes
            use destination = copied.Open()
            destination.Write(bytes,0,bytes.Length)
            if mutation = Duplicate && entry.FullName.EndsWith("TelemetryHostManager.dll",StringComparison.Ordinal) then
                duplicated <- Some(entry.FullName,bytes,entry.LastWriteTime,entry.ExternalAttributes)
    let add name =
        let entry = output.CreateEntry(name,CompressionLevel.SmallestSize)
        entry.LastWriteTime <- Bundle.fixedTime
        entry.ExternalAttributes <- (0o100444 <<< 16)
        use destination = entry.Open()
        destination.WriteByte 1uy
    if mutation = Extra then add (Bundle.archiveRoot+"/extra.bin")
    if mutation = Traversal then add (Bundle.archiveRoot+"/../escape")
    match duplicated with
    | Some(name,bytes,time,attributes) ->
        let entry = output.CreateEntry(name,CompressionLevel.SmallestSize)
        entry.LastWriteTime <- time
        entry.ExternalAttributes <- attributes
        use destination = entry.Open()
        destination.Write(bytes,0,bytes.Length)
    | None -> ()

[<EntryPoint>]
let main _ =
    let sourceRoot = Environment.GetEnvironmentVariable "THMB_SOURCE_ROOT" |> Path.GetFullPath
    let publishRoot = Environment.GetEnvironmentVariable "THMB_MANAGER_PUBLISH" |> Path.GetFullPath
    let runtimeRoot = Environment.GetEnvironmentVariable "THMB_RUNTIME_ROOT" |> Path.GetFullPath
    let verifyRuntimeRoot =
        match Environment.GetEnvironmentVariable "THMB_VERIFY_RUNTIME_ROOT" with
        | value when String.IsNullOrWhiteSpace value -> runtimeRoot
        | value -> Path.GetFullPath value
    let sdkRoot = Environment.GetEnvironmentVariable "THMB_SDK_ROOT" |> Path.GetFullPath
    let sdkExecutable = Environment.GetEnvironmentVariable "THMB_SDK_EXECUTABLE" |> Path.GetFullPath
    require (Directory.Exists sourceRoot && Directory.Exists publishRoot && Directory.Exists runtimeRoot && Directory.Exists verifyRuntimeRoot && Directory.Exists sdkRoot && File.Exists sdkExecutable) "test roots are absent"
    let selection = selected runtimeRoot sdkRoot sdkExecutable
    let verifySelection = selected verifyRuntimeRoot sdkRoot sdkExecutable
    let revision = Bundle.run "git" ["rev-parse";"HEAD"] sourceRoot
    let tree = Bundle.run "git" ["rev-parse";"HEAD^{tree}"] sourceRoot
    let workflow = File.ReadAllText(Path.Combine(sourceRoot,".github/workflows/telemetry-host-manager-bundle.yml"))
    let occurrences (needle: string) = workflow.Split(needle,StringSplitOptions.None).Length-1
    for required in ["permissions:\n  contents: read";"github.ref == 'refs/heads/main'";"runs-on: ubuntu-24.04";Bundle.selectedRuntimeManifestDigest;Bundle.selectedRuntimeConfigDigest;Bundle.selectedSdkManifestDigest;Bundle.selectedSdkConfigDigest;"RUNTIME_VERSION: 10.0.12";"TARGET_PLATFORM: linux/amd64";"docker pull --platform \"$TARGET_PLATFORM\" \"$SDK_IMAGE\"";"--volume \"$TARGET_RUNTIME_ROOT:/usr/share/dotnet:ro\"";"--volume \"$VERIFY_RUNTIME_ROOT:/usr/share/dotnet:ro\"";"docker run --rm --network none";"compression-level: 0";"retention-days: 90";"cmp \"$BUNDLE_OUTPUT/manifest.json\" \"$SERVED_OUTPUT/manifest.json\""] do
        require (workflow.Contains(required,StringComparison.Ordinal)) ("workflow binding absent: "+required)
    require (occurrences "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a" = 1) "upload action count differs"
    require (occurrences "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c" = 1) "download action count differs"
    for forbidden in ["packages: write";"contents: write";"id-token: write";"actions/setup-dotnet";"ubuntu-latest";"apt-get";"pip install";"gh release";"docker push"] do
        require (not(workflow.Contains(forbidden,StringComparison.Ordinal))) ("workflow contains unsupported effect: "+forbidden)
    let root = Path.Combine(Path.GetTempPath(),"telemetry-host-manager-bundle-tests-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let first = Path.Combine(root,"first")
        let second = Path.Combine(root,"second")
        let served = Path.Combine(root,"served")
        Directory.CreateDirectory served |> ignore
        let rejectBeforeOutput name candidate candidateRevision candidateTree =
            let output = Path.Combine(root,"refused-"+name)
            expectRefusal name (fun () -> Bundle.prepare sourceRoot publishRoot candidate candidateRevision candidateTree Bundle.supportedRuntime output |> ignore)
            require (not(Directory.Exists output)) (name+" wrote output before selection refusal")
        rejectBeforeOutput "stale-runtime-digest" {selection with runtime={selection.runtime with manifestDigest="sha256:"+String.replicate 64 "0"}} revision tree
        rejectBeforeOutput "wrong-platform" {selection with runtime={selection.runtime with architecture="arm64"}} revision tree
        rejectBeforeOutput "wrong-base-identity" {selection with runtime={selection.runtime with image="example.invalid/runtime"}} revision tree
        rejectBeforeOutput "wrong-canonical-root" {selection with runtimeCanonicalRoot="/opt/dotnet"} revision tree
        rejectBeforeOutput "wrong-runtime-path" {selection with runtimeRoot=Path.Combine(runtimeRoot,"shared")} revision tree
        rejectBeforeOutput "wrong-source" selection (String.replicate 40 "0") tree
        rejectBeforeOutput "stale-sdk-digest" {selection with sdk={selection.sdk with manifestDigest="sha256:"+String.replicate 64 "0"}} revision tree
        let sdkLicense = Path.Combine(sdkRoot,"LICENSE.txt")
        let sdkLicenseBytes = File.ReadAllBytes sdkLicense
        let sdkLicenseMode = File.GetUnixFileMode sdkLicense
        File.SetUnixFileMode(sdkLicense,sdkLicenseMode ||| UnixFileMode.UserWrite)
        File.AppendAllText(sdkLicense,"changed SDK tree")
        File.SetUnixFileMode(sdkLicense,sdkLicenseMode)
        rejectBeforeOutput "changed-sdk-tree" selection revision tree
        File.SetUnixFileMode(sdkLicense,sdkLicenseMode ||| UnixFileMode.UserWrite)
        File.WriteAllBytes(sdkLicense,sdkLicenseBytes)
        File.SetUnixFileMode(sdkLicense,sdkLicenseMode)

        let hostfxr = Path.Combine(runtimeRoot,"host/fxr",Bundle.supportedRuntime,"libhostfxr.so")
        let hostfxrBytes = File.ReadAllBytes hostfxr
        File.SetUnixFileMode(hostfxr,UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        let changedHostfxr = Array.copy hostfxrBytes
        changedHostfxr[0] <- changedHostfxr[0] ^^^ 0xFFuy
        File.WriteAllBytes(hostfxr,changedHostfxr)
        File.SetUnixFileMode(hostfxr,UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
        rejectBeforeOutput "changed-hostfxr-bytes" selection revision tree
        File.SetUnixFileMode(hostfxr,UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        File.WriteAllBytes(hostfxr,hostfxrBytes)
        File.SetUnixFileMode(hostfxr,UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)

        File.SetUnixFileMode(hostfxr,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)
        rejectBeforeOutput "writable-0777-runtime" selection revision tree
        File.SetUnixFileMode(hostfxr,UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)

        let fxrDirectory = Path.GetDirectoryName hostfxr
        let missingHostfxr = hostfxr+".missing"
        File.SetUnixFileMode(fxrDirectory,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        File.Move(hostfxr,missingHostfxr)
        File.SetUnixFileMode(fxrDirectory,UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        rejectBeforeOutput "missing-hostfxr" selection revision tree
        File.SetUnixFileMode(fxrDirectory,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        File.Move(missingHostfxr,hostfxr)
        File.SetUnixFileMode(fxrDirectory,UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)

        let firstArchive = Bundle.prepare sourceRoot publishRoot selection revision tree Bundle.supportedRuntime first
        let secondArchive = Bundle.prepare sourceRoot publishRoot selection revision tree Bundle.supportedRuntime second
        require (File.ReadAllBytes firstArchive = File.ReadAllBytes secondArchive) "repeated actual assembly differs"
        use manifestDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first,"manifest.json")))
        let fixedArgv = manifestDocument.RootElement.GetProperty("fixedArgv").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
        require (fixedArgv = [|Bundle.canonicalRuntimeRoot+"/dotnet";"exec";"--fx-version";Bundle.supportedRuntime;Bundle.installationRoot+"/TelemetryHostManager.dll"|]) "fixed runtime argv differs"
        Bundle.verify sourceRoot verifySelection revision tree (Path.Combine(first,"prepared.json")) None
        let servedArchive = Path.Combine(served,Path.GetFileName firstArchive)
        File.Copy(firstArchive,servedArchive)
        require (File.ReadAllBytes firstArchive = File.ReadAllBytes servedArchive) "served archive copy differs"
        Bundle.verify sourceRoot verifySelection revision tree (Path.Combine(first,"prepared.json")) (Some servedArchive)
        let manifest = File.ReadAllBytes(Path.Combine(first,"manifest.json"))
        for mutation in [Missing;Extra;Changed;Traversal;Duplicate;Link] do
            let path = Path.Combine(root,"mutated-"+mutation.ToString()+".zip")
            mutateArchive firstArchive path mutation
            expectRefusal (mutation.ToString()) (fun () -> Bundle.validateArchive path manifest |> ignore)
        expectRefusal "source drift" (fun () -> Bundle.verify sourceRoot verifySelection (String.replicate 40 "0") tree (Path.Combine(first,"prepared.json")) None)
        expectRefusal "unsupported runtime" (fun () -> Bundle.runtimeFacts selection "10.0.11" |> ignore)
        let missing = Path.Combine(root,"missing")
        copyDirectory publishRoot missing
        File.Delete(Path.Combine(missing,"TelemetryHostManager.dll"))
        expectRefusal "missing entrypoint" (fun () -> Bundle.prepare sourceRoot missing selection revision tree Bundle.supportedRuntime (Path.Combine(root,"missing-output")) |> ignore)
        let linked = Path.Combine(root,"linked")
        copyDirectory publishRoot linked
        let dependency = Path.Combine(linked,"TelemetryHostManager.deps.json")
        File.Delete dependency
        File.CreateSymbolicLink(dependency,Path.Combine(publishRoot,"TelemetryHostManager.deps.json")) |> ignore
        expectRefusal "linked payload" (fun () -> Bundle.prepare sourceRoot linked selection revision tree Bundle.supportedRuntime (Path.Combine(root,"linked-output")) |> ignore)
        let linkedDirectory = Path.Combine(root,"linked-directory")
        copyDirectory publishRoot linkedDirectory
        let foreign = Path.Combine(root,"foreign")
        Directory.CreateDirectory foreign |> ignore
        File.WriteAllText(Path.Combine(foreign,"unowned.bin"),"outside declared publish root")
        Directory.CreateSymbolicLink(Path.Combine(linkedDirectory,"linked-child"),foreign) |> ignore
        expectRefusal "linked payload directory" (fun () -> Bundle.prepare sourceRoot linkedDirectory selection revision tree Bundle.supportedRuntime (Path.Combine(root,"linked-directory-output")) |> ignore)
        let runtimeParent = Path.Combine(root,"runtime-parent")
        Directory.CreateDirectory runtimeParent |> ignore
        let linkedRuntime = Path.Combine(runtimeParent,"selected-runtime")
        Directory.CreateSymbolicLink(linkedRuntime,Path.Combine(runtimeRoot,"shared/Microsoft.NETCore.App",Bundle.supportedRuntime)) |> ignore
        expectRefusal "linked selected runtime directory" (fun () -> Bundle.runtimeFiles runtimeParent linkedRuntime |> ignore)
        let ancestorTarget = Path.Combine(runtimeParent,"ancestor-target")
        let selectedBelowAncestor = Path.Combine(ancestorTarget,"selected")
        Directory.CreateDirectory selectedBelowAncestor |> ignore
        File.WriteAllText(Path.Combine(selectedBelowAncestor,"runtime.bin"),"ordinary selected runtime bytes")
        let linkedAncestor = Path.Combine(runtimeParent,"linked-ancestor")
        Directory.CreateSymbolicLink(linkedAncestor,ancestorTarget) |> ignore
        expectRefusal "linked runtime ancestor" (fun () -> Bundle.runtimeFiles runtimeParent (Path.Combine(linkedAncestor,"selected")) |> ignore)
        let dirtySource = Path.Combine(root,"dirty-source")
        Bundle.run "git" ["clone";"--quiet";"--no-hardlinks";sourceRoot;dirtySource] root |> ignore
        Bundle.run "git" ["checkout";"--quiet";revision] dirtySource |> ignore
        File.AppendAllText(Path.Combine(dirtySource,"eng/telemetry-host-manager/Program.fs"),"\n// causal tracked-source drift witness\n")
        expectRefusal "dirty source assembly" (fun () -> Bundle.prepare dirtySource publishRoot selection revision tree Bundle.supportedRuntime (Path.Combine(root,"dirty-assembly-output")) |> ignore)
        expectRefusal "dirty source verification" (fun () -> Bundle.verify dirtySource verifySelection revision tree (Path.Combine(first,"prepared.json")) None)
        printfn "TELEMETRY_HOST_MANAGER_BUNDLE_TESTS_OK actualPayloadFiles=%d archiveSha256=%s" (Bundle.enumerateFiles publishRoot Bundle.archiveRoot).Length (Bundle.shaFile firstArchive)
        0
    finally
        if Directory.Exists root then Directory.Delete(root,true)
