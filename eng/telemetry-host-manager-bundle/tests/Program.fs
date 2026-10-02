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

let copyDirectory source target =
    Directory.CreateDirectory target |> ignore
    for directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories) do
        Directory.CreateDirectory(Path.Combine(target,Path.GetRelativePath(source,directory))) |> ignore
    for file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories) do
        File.Copy(file,Path.Combine(target,Path.GetRelativePath(source,file)))

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
    require (Directory.Exists sourceRoot && Directory.Exists publishRoot && Directory.Exists runtimeRoot) "test roots are absent"
    let revision = Bundle.run "git" ["rev-parse";"HEAD"] sourceRoot
    let tree = Bundle.run "git" ["rev-parse";"HEAD^{tree}"] sourceRoot
    let workflow = File.ReadAllText(Path.Combine(sourceRoot,".github/workflows/telemetry-host-manager-bundle.yml"))
    let occurrences (needle: string) = workflow.Split(needle,StringSplitOptions.None).Length-1
    for required in ["permissions:\n  contents: read";"github.ref == 'refs/heads/main'";"dotnet-version: 10.0.400";"RUNTIME_VERSION: 10.0.12";"compression-level: 0";"retention-days: 90";"dotnet exec --fx-version \"$RUNTIME_VERSION\" eng/telemetry-host-manager-bundle/bin/Release/net10.0/TelemetryHostManagerBundle.dll";"dotnet exec --fx-version \"$RUNTIME_VERSION\" eng/telemetry-host-manager-bundle/tests/bin/Release/net10.0/TelemetryHostManagerBundle.Tests.dll";"cmp \"$BUNDLE_OUTPUT/manifest.json\" \"$SERVED_OUTPUT/manifest.json\""] do
        require (workflow.Contains(required,StringComparison.Ordinal)) ("workflow binding absent: "+required)
    require (occurrences "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a" = 1) "upload action count differs"
    require (occurrences "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c" = 1) "download action count differs"
    for forbidden in ["packages: write";"contents: write";"id-token: write";"gh release";"docker push"] do
        require (not(workflow.Contains(forbidden,StringComparison.Ordinal))) ("workflow contains unsupported effect: "+forbidden)
    let root = Path.Combine(Path.GetTempPath(),"telemetry-host-manager-bundle-tests-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let first = Path.Combine(root,"first")
        let second = Path.Combine(root,"second")
        let served = Path.Combine(root,"served")
        Directory.CreateDirectory served |> ignore
        let firstArchive = Bundle.prepare sourceRoot publishRoot runtimeRoot revision tree Bundle.supportedRuntime first
        let secondArchive = Bundle.prepare sourceRoot publishRoot runtimeRoot revision tree Bundle.supportedRuntime second
        require (File.ReadAllBytes firstArchive = File.ReadAllBytes secondArchive) "repeated actual assembly differs"
        use manifestDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first,"manifest.json")))
        let fixedArgv = manifestDocument.RootElement.GetProperty("fixedArgv").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
        require (fixedArgv = [|runtimeRoot+"/dotnet";"exec";"--fx-version";Bundle.supportedRuntime;Bundle.installationRoot+"/TelemetryHostManager.dll"|]) "fixed runtime argv differs"
        Bundle.verify sourceRoot runtimeRoot revision tree (Path.Combine(first,"prepared.json")) None
        let servedArchive = Path.Combine(served,Path.GetFileName firstArchive)
        File.Copy(firstArchive,servedArchive)
        require (File.ReadAllBytes firstArchive = File.ReadAllBytes servedArchive) "served archive copy differs"
        Bundle.verify sourceRoot runtimeRoot revision tree (Path.Combine(first,"prepared.json")) (Some servedArchive)
        let manifest = File.ReadAllBytes(Path.Combine(first,"manifest.json"))
        for mutation in [Missing;Extra;Changed;Traversal;Duplicate;Link] do
            let path = Path.Combine(root,"mutated-"+mutation.ToString()+".zip")
            mutateArchive firstArchive path mutation
            expectRefusal (mutation.ToString()) (fun () -> Bundle.validateArchive path manifest |> ignore)
        expectRefusal "source drift" (fun () -> Bundle.verify sourceRoot runtimeRoot (String.replicate 40 "0") tree (Path.Combine(first,"prepared.json")) None)
        expectRefusal "unsupported runtime" (fun () -> Bundle.runtimeFacts sourceRoot runtimeRoot "10.0.11" |> ignore)
        let missing = Path.Combine(root,"missing")
        copyDirectory publishRoot missing
        File.Delete(Path.Combine(missing,"TelemetryHostManager.dll"))
        expectRefusal "missing entrypoint" (fun () -> Bundle.prepare sourceRoot missing runtimeRoot revision tree Bundle.supportedRuntime (Path.Combine(root,"missing-output")) |> ignore)
        let linked = Path.Combine(root,"linked")
        copyDirectory publishRoot linked
        let dependency = Path.Combine(linked,"TelemetryHostManager.deps.json")
        File.Delete dependency
        File.CreateSymbolicLink(dependency,Path.Combine(publishRoot,"TelemetryHostManager.deps.json")) |> ignore
        expectRefusal "linked payload" (fun () -> Bundle.prepare sourceRoot linked runtimeRoot revision tree Bundle.supportedRuntime (Path.Combine(root,"linked-output")) |> ignore)
        let linkedDirectory = Path.Combine(root,"linked-directory")
        copyDirectory publishRoot linkedDirectory
        let foreign = Path.Combine(root,"foreign")
        Directory.CreateDirectory foreign |> ignore
        File.WriteAllText(Path.Combine(foreign,"unowned.bin"),"outside declared publish root")
        Directory.CreateSymbolicLink(Path.Combine(linkedDirectory,"linked-child"),foreign) |> ignore
        expectRefusal "linked payload directory" (fun () -> Bundle.prepare sourceRoot linkedDirectory runtimeRoot revision tree Bundle.supportedRuntime (Path.Combine(root,"linked-directory-output")) |> ignore)
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
        expectRefusal "dirty source assembly" (fun () -> Bundle.prepare dirtySource publishRoot runtimeRoot revision tree Bundle.supportedRuntime (Path.Combine(root,"dirty-assembly-output")) |> ignore)
        expectRefusal "dirty source verification" (fun () -> Bundle.verify dirtySource runtimeRoot revision tree (Path.Combine(first,"prepared.json")) None)
        printfn "TELEMETRY_HOST_MANAGER_BUNDLE_TESTS_OK actualPayloadFiles=%d archiveSha256=%s" (Bundle.enumerateFiles publishRoot Bundle.archiveRoot).Length (Bundle.shaFile firstArchive)
        0
    finally
        if Directory.Exists root then Directory.Delete(root,true)
