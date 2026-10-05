#r "../src/FS.GG.Coordination.Orchestration.Execution/bin/Debug/net10.0/FS.GG.Coordination.Orchestration.Execution.dll"

// Run only through the existing exact-package rewriter, from a copied external script.
// This qualifies a synthetic installed API capsule; it grants no product operation.
open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open FS.GG.Coordination.Orchestration.Execution

// Same Linux statx inode/device ABI as the accepted capsule boundary. These
// observations bind the qualification's external inputs and held output root.
[<Struct; StructLayout(LayoutKind.Explicit, Size=256)>]
type NodeStat =
    [<FieldOffset(0)>] val mutable Mask: uint32
    [<FieldOffset(20)>] val mutable Uid: uint32
    [<FieldOffset(28)>] val mutable Mode: uint16
    [<FieldOffset(32)>] val mutable Inode: uint64
    [<FieldOffset(136)>] val mutable DeviceMajor: uint32
    [<FieldOffset(140)>] val mutable DeviceMinor: uint32
module Nodes =
    [<DllImport("libc",SetLastError=true,EntryPoint="statx")>]
    extern int statx(int descriptor,string path,int flags,uint32 mask,NodeStat& result)
    [<DllImport("libc",SetLastError=true,EntryPoint="open")>]
    extern int openDirectory(string path,int flags)
    [<DllImport("libc",SetLastError=true,EntryPoint="mkdir")>]
    extern int mkdir(string path,uint32 mode)
    [<DllImport("libc",SetLastError=true,EntryPoint="openat")>]
    extern int openAt(int descriptor,string path,int flags)
    [<DllImport("libc",SetLastError=true,EntryPoint="mkdirat")>]
    extern int mkdirAt(int descriptor,string path,uint32 mode)
    [<DllImport("libc",SetLastError=true,EntryPoint="renameat2")>]
    extern int captureAt(int sourceDescriptor,string source,int targetDescriptor,string target,uint32 flags)
    [<DllImport("libc",SetLastError=true,EntryPoint="unlinkat")>]
    extern int unlinkAt(int descriptor,string path,int flags)
    [<DllImport("libc",SetLastError=true,EntryPoint="fchmod")>]
    extern int chmodHeld(int descriptor,uint32 mode)

let checkerSource = """import importlib,json,sys,unittest
mode=sys.argv[1]
if mode=='imports':
 importlib.import_module('entry')
 names=['entry','dependency']
elif mode=='discovery':
 def ids(suite):
  for case in suite:
   if isinstance(case,unittest.TestSuite): yield from ids(case)
   else: yield case.id()
 suite=unittest.defaultTestLoader.discover('.',pattern='test_*.py')
 names=list(ids(suite))
 if any('unittest.loader._FailedTest' in name for name in names):
  raise RuntimeError('discovery import failed')
else:
 raise ValueError('unknown check')
print(json.dumps({'schema':'fsgg.capsule-observation/1','checkId':mode,'discovered':names}))
"""

let hash (bytes: byte array) = bytes |> SHA256.HashData |> Convert.ToHexStringLower
let fileHash (path: string) = File.ReadAllBytes path |> hash
let demand condition reason = if not condition then invalidOp reason
let node descriptor path flags =
    let mutable stat = Unchecked.defaultof<NodeStat>
    demand (Nodes.statx(descriptor,path,flags,0x7ffu,&stat)=0 && stat.Mask &&& 0x10bu = 0x10bu) "node-identity-unavailable"
    stat
let nodeId (stat: NodeStat) = $"{stat.DeviceMajor}:{stat.DeviceMinor}:{stat.Inode}"
let rec plainAncestors (path: string) =
    let parent = Path.GetDirectoryName path
    if not (String.IsNullOrEmpty parent) && parent <> path then
        if Directory.Exists parent then demand ((File.GetAttributes parent &&& FileAttributes.ReparsePoint) = enum 0) "linked-input-output-ancestor"
        plainAncestors parent
let args =
    let values = fsi.CommandLineArgs |> Array.skip 1
    let values = if values.Length > 0 && values[0] = "--" then values[1..] else values
    demand (values.Length % 2 = 0) "arguments-must-be-pairs"
    values |> Array.chunkBySize 2 |> Array.map (fun p -> p[0],p[1])
let allowed =
    set [ "--package"; "--package-version"; "--expected-package-sha256";
          "--expected-execution-sha256"; "--expected-host-sha256"; "--expected-core-sha256"; "--fsi-dll"; "--expected-fsi-sha256";
          "--source-script"; "--expected-source-sha256"; "--python"; "--expected-python-sha256";
          "--work-dir"; "--evidence" ]
demand (args.Length = allowed.Count && (args |> Array.map fst |> Set.ofArray) = allowed) "exact-arguments-required"
let options = Map.ofArray args
let get name = options[name]
let regular (path: string) =
    demand (Path.IsPathFullyQualified path && File.Exists path) "absolute-file-required"
    demand ((File.GetAttributes path &&& FileAttributes.ReparsePoint) = enum 0) "linked-file-refused"
    plainAncestors path
let pinned path key =
    regular path
    let expected = get key
    demand (expected.Length = 64 && expected |> Seq.forall (fun c -> "0123456789abcdef".Contains c)) "invalid-sha256"
    demand (fileHash path = expected) "identity-drift-refused"
let package = get "--package"
let source = get "--source-script"
let python = get "--python"
let assembly = typeof<PreparedAttempt>.Assembly.Location
let core = typeof<unit>.Assembly.Location
let host = Environment.ProcessPath
let fsiDll = get "--fsi-dll"
let rewrittenScript = Path.GetFullPath fsi.CommandLineArgs[0]
pinned package "--expected-package-sha256"
pinned source "--expected-source-sha256"
pinned assembly "--expected-execution-sha256"
pinned core "--expected-core-sha256"
pinned host "--expected-host-sha256"
pinned python "--expected-python-sha256"
pinned fsiDll "--expected-fsi-sha256"
demand (Environment.GetCommandLineArgs() |> Array.exists (fun a -> Path.IsPathFullyQualified a && Path.GetFullPath a = fsiDll)) "running-fsi-identity-unavailable"
let reference = "#r \"../src/FS.GG.Coordination.Orchestration.Execution/bin/Debug/net10.0/FS.GG.Coordination.Orchestration.Execution.dll\""
let original = File.ReadAllText source
demand (original.StartsWith(reference+"\n",StringComparison.Ordinal)) "source-reference-drift"
let escaped (path: string) = path.Replace("\"", "\"\"")
let akka = typeof<Akka.Actor.Props>.Assembly.Location
let rewritten = "#r @\""+escaped akka+"\"\n#r @\""+escaped assembly+"\""+original.Substring(reference.Length)
demand (File.ReadAllText rewrittenScript = rewritten) "rewritten-source-drift"
demand (typeof<PreparedAttempt>.GetConstructors().Length = 0) "prepared-value-public-constructor"
use archive = ZipFile.OpenRead package
let entries = archive.Entries |> Seq.filter (fun e -> e.FullName = "tools/net10.0/any/FS.GG.Coordination.Orchestration.Execution.dll") |> Seq.toArray
demand (entries.Length = 1) "exact-execution-entry-required"
use input = entries[0].Open()
use bytes = new MemoryStream()
input.CopyTo bytes
demand (hash (bytes.ToArray()) = get "--expected-execution-sha256") "archive-assembly-drift"
// Closed census for the exact selected genuine package. The unchanged packager
// extracts the21 top-level assemblies;13 culture and5 platform DLLs remain
// inactive archive-only objects, bound by the whole archive and per-entry hashes.
let selectedAssemblyNames =
    set [
        "Akka.dll"
        "FS.GG.Contracts.dll"
        "FS.GG.Coordination.Cli.dll"
        "FS.GG.Coordination.Core.dll"
        "FS.GG.Coordination.GitHub.dll"
        "FS.GG.Coordination.Orchestration.Execution.dll"
        "FS.GG.Coordination.Orchestration.Observer.dll"
        "FS.GG.Coordination.Protocol.dll"
        "FS.GG.Coordination.Qualification.Contracts.dll"
        "FS.GG.SDD.Artifacts.dll"
        "FSharp.Core.dll"
        "FSharp.SystemTextJson.dll"
        "Microsoft.Extensions.ObjectPool.dll"
        "Microsoft.Win32.SystemEvents.dll"
        "Newtonsoft.Json.dll"
        "System.Configuration.ConfigurationManager.dll"
        "System.Drawing.Common.dll"
        "System.Security.Cryptography.ProtectedData.dll"
        "System.Security.Permissions.dll"
        "System.Windows.Extensions.dll"
        "YamlDotNet.dll"
    ]
let inactiveAssemblyRelativeNames =
    set [
        "cs/FSharp.Core.resources.dll"
        "de/FSharp.Core.resources.dll"
        "es/FSharp.Core.resources.dll"
        "fr/FSharp.Core.resources.dll"
        "it/FSharp.Core.resources.dll"
        "ja/FSharp.Core.resources.dll"
        "ko/FSharp.Core.resources.dll"
        "pl/FSharp.Core.resources.dll"
        "pt-BR/FSharp.Core.resources.dll"
        "ru/FSharp.Core.resources.dll"
        "runtimes/unix/lib/net6.0/System.Drawing.Common.dll"
        "runtimes/win/lib/net6.0/Microsoft.Win32.SystemEvents.dll"
        "runtimes/win/lib/net6.0/System.Drawing.Common.dll"
        "runtimes/win/lib/net6.0/System.Security.Cryptography.ProtectedData.dll"
        "runtimes/win/lib/net6.0/System.Windows.Extensions.dll"
        "tr/FSharp.Core.resources.dll"
        "zh-Hans/FSharp.Core.resources.dll"
        "zh-Hant/FSharp.Core.resources.dll"
    ]
let assemblyPrefix = "tools/net10.0/any/"
let packageAssemblyEntries = archive.Entries |> Seq.filter (fun e -> e.FullName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)) |> Seq.toArray
let packageAssemblyNames =
    packageAssemblyEntries |> Array.map (fun e ->
        demand (e.FullName.StartsWith(assemblyPrefix,StringComparison.Ordinal)) "unexpected-assembly-layout"
        e.FullName.Substring(assemblyPrefix.Length))
demand (packageAssemblyEntries.Length = 39 && Set.count (Set.ofArray packageAssemblyNames) = packageAssemblyEntries.Length) "duplicate-or-missing-package-assembly"
demand (Set.ofArray packageAssemblyNames = Set.union selectedAssemblyNames inactiveAssemblyRelativeNames) "unexpected-package-assembly-census"
let inactiveArchiveAssemblies =
    packageAssemblyEntries
    |> Array.filter (fun e -> Set.contains (e.FullName.Substring(assemblyPrefix.Length)) inactiveAssemblyRelativeNames)
    |> Array.map (fun e ->
        use stream = e.Open()
        use content = new MemoryStream()
        stream.CopyTo content
        e.FullName,hash (content.ToArray()))
let assemblyInputs =
    packageAssemblyEntries
    |> Seq.filter (fun e -> Set.contains (e.FullName.Substring(assemblyPrefix.Length)) selectedAssemblyNames)
    |> Seq.map (fun e ->
        demand (e.FullName.Split('/').Length = 4) "unexpected-assembly-layout"
        use stream = e.Open()
        use content = new MemoryStream()
        stream.CopyTo content
        let path = Path.Combine(Path.GetDirectoryName assembly,Path.GetFileName e.FullName)
        regular path
        let expected = hash (content.ToArray())
        demand (fileHash path = expected) "extracted-assembly-drift"
        path,e.FullName,expected)
    |> Seq.toArray
demand (assemblyInputs.Length = 21 && inactiveArchiveAssemblies.Length = 18) "selected-inactive-assembly-count"
demand (assemblyInputs |> Array.map (fun (p,_,_) -> p) |> Set.ofArray |> Set.count = assemblyInputs.Length) "duplicate-assembly-input"
let nuspecs = archive.Entries |> Seq.filter (fun e -> e.FullName.EndsWith ".nuspec") |> Seq.toArray
demand (nuspecs.Length = 1) "exact-nuspec-required"
use nuspec = nuspecs[0].Open()
let metadata = XDocument.Load nuspec
let versions = metadata.Descendants() |> Seq.filter (fun e -> e.Name.LocalName = "version") |> Seq.map _.Value |> Seq.toList
demand (versions = [get "--package-version"] && not (String.IsNullOrWhiteSpace(get "--package-version"))) "package-version-drift"

let work = get "--work-dir"
let evidence = get "--evidence"
demand (Path.IsPathFullyQualified work && not (Directory.Exists work) && not (File.Exists work)) "fresh-output-root-required"
demand (Path.GetDirectoryName evidence = work && not (File.Exists evidence)) "evidence-must-be-new-in-output-root"
plainAncestors work
for immutable in [package;source;assembly;core;host;python;fsiDll;rewrittenScript] do
    demand (not (immutable.StartsWith(work+string Path.DirectorySeparatorChar,StringComparison.Ordinal)) && work <> immutable) "output-overlaps-input"
demand (not (work.StartsWith(Path.GetDirectoryName assembly + string Path.DirectorySeparatorChar, StringComparison.Ordinal))) "output-overlaps-installed-input"
demand (Nodes.mkdir(work,448u)=0) "exclusive-output-root-required"
let createdOutputIdentity = node -100 work 0x100 |> nodeId
let outputDescriptor = Nodes.openDirectory(work,0xb0000) // directory, no-follow, close-on-exec
demand (outputDescriptor >= 0) "output-directory-custody-unavailable"
let outputLease = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint outputDescriptor,true)
let outputIdentity = node outputDescriptor "" 0x1000 |> nodeId
let outputOwner = (node outputDescriptor "" 0x1000).Uid
demand (createdOutputIdentity = outputIdentity) "new-output-directory-replaced"
let verifyOutput () =
    plainAncestors work
    let named = node -100 work 0x100
    demand (named.Mode &&& 0xf000us = 0x4000us && named.Mode &&& 0xfffus = 0x1c0us && named.Uid=outputOwner && nodeId named = outputIdentity) "output-directory-replaced"
    let held = node outputDescriptor "" 0x1000
    demand (nodeId held = outputIdentity && held.Mode &&& 0xfffus = 0x1c0us && held.Uid=outputOwner) "held-output-directory-replaced"
verifyOutput ()
let anchored descriptor = $"/proc/self/fd/{descriptor}"
type OwnedOutput =
    { Parent: int; Name: string; Descriptor: int
      Lease: Microsoft.Win32.SafeHandles.SafeFileHandle; Identity: string; Directory: bool
      mutable Retired: bool }
let ownedOutputs = ResizeArray<OwnedOutput>()
let liveOwnedOutputs () = ownedOutputs |> Seq.filter (fun value -> not value.Retired)
let holdOutput parent name directory =
    // Retire diagnostic groups before subsequent groups. Keep the original live-node bound.
    demand (liveOwnedOutputs () |> Seq.length |> fun count -> count < 70) "owned-output-count-refused"
    // Nineteen fixed capsules (6 nodes), seven journals (2 dirs + at most2 leaves):142 lifetime records.
    demand (ownedOutputs.Count < 142) "owned-output-metadata-count-refused"
    demand (name <> "" && name <> "." && name <> ".." && Path.GetFileName name = name) "invalid-owned-child-name"
    let observed = node parent name 0x100
    let kind = if directory then 0x4000us else 0x8000us
    demand (observed.Mode &&& 0xf000us = kind && observed.Uid=outputOwner) "owned-child-kind-refused"
    let descriptor = Nodes.openAt(parent,name,if directory then 0xb0000 else 0xa0800)
    demand (descriptor >= 0) "owned-child-custody-unavailable"
    let lease = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint descriptor,true)
    let held = node descriptor "" 0x1000
    if nodeId held <> nodeId observed || held.Uid<>outputOwner || nodeId (node parent name 0x100) <> nodeId held then
        lease.Dispose()
        invalidOp "owned-child-replaced"
    let value = { Parent=parent; Name=name; Descriptor=descriptor; Lease=lease; Identity=nodeId held; Directory=directory; Retired=false }
    ownedOutputs.Add value
    value
let createDirectory parent name =
    verifyOutput ()
    demand (Nodes.mkdirAt(parent,name,448u)=0) "exclusive-owned-directory-required"
    holdOutput parent name true
let disposeOwned () = for value in liveOwnedOutputs () do value.Lease.Dispose()
let retireOwned value =
    demand (not value.Retired) "owned-child-already-retired"
    verifyOutput ()
    let held = node value.Descriptor "" 0x1000
    demand (nodeId held = value.Identity && held.Uid=outputOwner) "held-child-identity-drift"
    // Capture atomically, then inspect. Never unlink the original observed name.
    let captured = ".retire-" + Guid.NewGuid().ToString("N")
    demand (Nodes.captureAt(value.Parent,value.Name,outputDescriptor,captured,1u)=0) "owned-child-capture-refused"
    let observed = node outputDescriptor captured 0x100
    if nodeId observed <> value.Identity || observed.Uid<>outputOwner then
        // Restore without replacing a newer original slot; otherwise retain it.
        Nodes.captureAt(outputDescriptor,captured,value.Parent,value.Name,1u) |> ignore
        invalidOp "foreign-child-replacement-retained"
    demand (node value.Descriptor "" 0x1000 |> nodeId = value.Identity) "captured-held-child-drift"
    if value.Directory then
        // isEmpty reads at most one entry; no recursive walk is admitted.
        demand (Directory.EnumerateFileSystemEntries(anchored value.Descriptor) |> Seq.isEmpty) "owned-directory-not-empty"
    demand (node outputDescriptor captured 0x100 |> nodeId = value.Identity) "captured-child-replaced"
    demand (Nodes.unlinkAt(outputDescriptor,captured,if value.Directory then 0x200 else 0)=0) "owned-child-retirement-refused"
    value.Lease.Dispose()
    value.Retired <- true
let cleanupOwned () =
    // Reverse creation order removes only known leaves and then their held parents.
    for value in liveOwnedOutputs () |> Seq.rev do retireOwned value
    demand (Directory.EnumerateFileSystemEntries(anchored outputDescriptor) |> Seq.isEmpty) "owned-cleanup-incomplete"
    verifyOutput ()
let immutableInputs =
    ([package;source;assembly;core;host;python;fsiDll;rewrittenScript] @ (assemblyInputs |> Array.map (fun (p,_,_) -> p) |> Array.toList))
    |> List.distinct
    |> List.map (fun path -> path,(node -100 path 0x100 |> nodeId),fileHash path)

let files =
    [ "dependency.py", "def ready(): return 'ready'\n"
      "entry.py", "from dependency import ready\n"
      "test_entry.py", "import unittest\nfrom entry import ready\nclass Entry(unittest.TestCase):\n def test_ready(self): raise RuntimeError('discovered workload must never execute')\n"
      "checker.py", checkerSource ]
let fixture name =
    let root = Path.Combine(work, name)
    let held = createDirectory outputDescriptor name
    createDirectory held.Descriptor "app" |> ignore
    for name,text in files do
        File.WriteAllText(Path.Combine(anchored held.Descriptor,name),text)
        holdOutput held.Descriptor name false |> ignore
    { Root = root; Inputs = files |> List.map fst |> List.sort
      Checks = [ { Id="imports"; Kind=CapsuleCheckKind.Import; Executable=python; Arguments=["checker.py"; "imports"]; ExpectedDiscoveries=["entry"; "dependency"] }
                 { Id="discovery"; Kind=CapsuleCheckKind.Discovery; Executable=python; Arguments=["checker.py"; "discovery"]; ExpectedDiscoveries=["test_entry.Entry.test_ready"] } ]
      Environment=Map ["PATH","/usr/bin:/bin"; "LANG","C.UTF-8"]
      MaximumInputBytes=65536UL; MaximumOutputBytes=4096UL; MaximumCheckSeconds=1 }

type InstrumentedRunner() =
    let mutable calls = 0
    let mutable cleanups = 0
    member _.Calls = calls
    member _.Cleanups = cleanups
    interface IPortableProcessRunner with
        member _.RunAsync(_,_) =
            calls <- calls + 1
            Task.FromResult {
                ExecutionStarted=true; ExitCode=Some 0; StandardOutput=Array.empty; StandardError=Array.empty
                CancellationRequested=false; TerminationObserved=true; Interrupted=false; OutputLimitExceeded=false
                OutputComplete=true; SourceTree=None; SnapshotSha256=None; RuntimeIdentity=None; ContainerIdentity=None
                VerificationObserved=true; VerificationOutput=Some Array.empty; VerificationCustodyLimitExceeded=false; Refusal=None }
        member _.RecoverAsync(_,_) = invalidOp "unexpected-recovery"
        member _.CleanupAsync(_,_) = cleanups <- cleanups + 1; Task.FromResult true

let execute name (spec: CapsulePreparation) =
    let runner = InstrumentedRunner()
    // Diverse fixture-label digests satisfy the unchanged adapter pin guards;
    // they are synthetic policy data, never real source/image/package selection.
    let revision = hash (Encoding.UTF8.GetBytes "installed-preparation-synthetic-source-v1") |> fun value -> value.Substring(0,40)
    let sha = hash (Encoding.UTF8.GetBytes "installed-preparation-synthetic-policy-v1")
    let image = "ghcr.io/fs-gg/installed-fixture@sha256:" + sha
    let profile: PortableWorkspaceProfile =
        { ProfileId="installed-preparation-v1"; Revision=1UL; WorkspaceScope="fs-gg/installed-preparation"
          SourceRevision=revision; QualifiedImage=image
          Components=[{Id="app"; Language="python"; WorkingDirectory="app"; Toolchain={Id="cpython";Version="fixture"}
                       EntryPoints={Build="fixture-build";Test="fixture-test";Lint=None;Artifact=None}}]
          ProductBuild="product-build"; ProductTest="product-test"; ProductJourney="product-journey"
          MaximumRuntimeSeconds=20UL; MaximumOutputBytes=65536UL }
    let operation: PortableReviewedOperation =
        { EntryPoint="fixture-build"; OperationIdentity="build"; ComponentId=Some "app"; WorkingDirectory="app"
          QualifiedImage=image; RequiredToolchains=["cpython","fixture"]; Executable=python; Arguments=["workload-must-not-run.py"]
          VerificationIdentity="fixture-proof"; VerificationPath="proof.json"; VerificationSha256=sha; RecipeSha256=sha }
    let state = createDirectory outputDescriptor ("journal-"+name)
    let journal = createDirectory state.Descriptor "journal-v1"
    let runtime: PortableRuntimePolicy =
        { GitExecutable="/usr/bin/git"; TarExecutable="/usr/bin/tar"; PodmanExecutable="/usr/bin/podman"; PodmanGlobalArguments=[]
          StateRoot=Path.Combine(work,"journal-"+name); ContainerPath="/usr/bin:/bin"; ContainerUser="32768:32768"
          HostEnvironment=Map ["HOME","/tmp";"PATH","/usr/bin:/bin"]
          ContainerEnvironment=Map ["HOME","/tmp";"PATH","/usr/bin:/bin"]
          MaximumSnapshotBytes=65536UL; TerminationGrace=TimeSpan.FromSeconds 1. }
    let policy: PortableExecutorPolicy =
        { WorkspaceRoot=spec.Root; WorkspaceScope=profile.WorkspaceScope; SourceRevision=revision; QualifiedImage=image
          MaximumRuntimeSeconds=20UL; MaximumOutputBytes=65536UL; Operations=[operation]; Runtime=runtime }
    let current = DateTimeOffset.UtcNow
    let current = DateTimeOffset(current.Ticks-current.Ticks%10L,TimeSpan.Zero)
    let authority: PortableWorkspaceAuthority = {WorkspaceScope=profile.WorkspaceScope;WorkflowRevision=1UL;FenceGeneration=1UL;ObservedAt=current.AddSeconds(-1.)}
    let id = Guid.NewGuid()
    let command: PortableWorkspaceCommand =
        { CommandId=id; IdempotencyId="fixture-"+id.ToString("N"); WorkspaceScope=profile.WorkspaceScope; ProfileId=profile.ProfileId
          ProfileRevision=profile.Revision; SourceRevision=revision; ExpectedWorkflowRevision=1UL; FenceGeneration=1UL
          CausationId=None; Deadline=current.AddSeconds 15.; Operation="build"; ComponentId=Some "app" }
    let executor = PortableWorkspaceExecutor.Executor(policy,runner,prerequisites=Map [operation.EntryPoint,PortablePrerequisiteRequirement.CapsuleRequired spec])
    let outcome = executor.ExecuteAsync(authority,profile,command,CancellationToken.None).GetAwaiter().GetResult()
    verifyOutput ()
    demand (node state.Parent state.Name 0x100 |> nodeId = state.Identity) "journal-state-replaced"
    demand (node journal.Parent journal.Name 0x100 |> nodeId = journal.Identity) "journal-directory-replaced"
    let key = Encoding.UTF8.GetBytes(command.WorkspaceScope+"\n"+command.IdempotencyId) |> hash
    // One lock and at most one settled bin; a third entry refuses without a walk.
    let journalEntries = Directory.EnumerateFileSystemEntries(anchored journal.Descriptor) |> Seq.truncate 3 |> Seq.toArray
    demand (journalEntries.Length <= 2) "journal-entry-count-refused"
    for path in journalEntries do
        let leaf = Path.GetFileName path
        demand (leaf=key+".bin" || leaf=key+".lock") "unexpected-journal-leaf"
        holdOutput journal.Descriptor leaf false |> ignore
    outcome,runner

let observations = ResizeArray<obj>()
let mutable firstDiagnosticFailure: string option = None
let mutable diagnosticReportingFailed = false
let time action =
    let clock = Diagnostics.Stopwatch.StartNew()
    let value = action ()
    clock.Stop()
    value,clock.Elapsed.TotalMilliseconds
let record name result calls cleanup milliseconds =
    observations.Add(box {| name=name; result=result; runnerCalls=calls; cleanupCalls=cleanup; elapsedMilliseconds=milliseconds |})
let refuse name spec =
    let (outcome,runner),milliseconds = time (fun () -> execute name spec)
    match outcome with
    | Refused reason ->
        demand (reason.StartsWith "preparation-") ("unexpected-refusal-"+name+":"+reason)
        demand (runner.Calls=0 && runner.Cleanups=0) "refusal-caused-runner-effects"
        record name reason runner.Calls runner.Cleanups milliseconds
    | _ -> invalidOp ("negative-control-admitted-"+name)

try
    let good = fixture "good"
    let binding = "synthetic-runner-argv-and-configuration"
    let attempt, cold = time (fun () -> PreparedAttempt.prepareAsync binding (DateTimeOffset.UtcNow.AddSeconds 15.) good CancellationToken.None |> fun t -> t.GetAwaiter().GetResult())
    let prepared = match attempt with Ok value -> value | Error reason -> invalidOp reason
    let validation,warm = time (fun () -> PreparedAttempt.validate DateTimeOffset.UtcNow binding good prepared)
    demand (validation = Ok()) "prepared-consumption-failed"
    record "cold-prepare" "ready" 0 0 cold
    record "warm-consume" "ready" 0 0 warm
    let driftClock = Diagnostics.Stopwatch.StartNew()
    demand (PreparedAttempt.validate DateTimeOffset.UtcNow "changed-argv" good prepared = Error "preparation-binding-invalidated") "argv-drift-admitted"
    demand (PreparedAttempt.validate DateTimeOffset.UtcNow binding {good with Environment=Map.add "LANG" "C" good.Environment} prepared = Error "preparation-binding-invalidated") "configuration-drift-admitted"
    demand (PreparedAttempt.validate (DateTimeOffset.UtcNow.AddMinutes 1.) binding good prepared = Error "preparation-deadline-refused") "expired-attempt-admitted"
    File.AppendAllText(Path.Combine(good.Root,"dependency.py"),"# consumed-input drift\n")
    demand (PreparedAttempt.validate DateTimeOffset.UtcNow binding good prepared = Error "preparation-input-invalidated") "input-drift-admitted"
    driftClock.Stop()
    record "consume-drift-controls" "refused" 0 0 driftClock.Elapsed.TotalMilliseconds
    // Additive diagnostics use the same installed actual checker and held custody path.
    let declarations (spec: CapsulePreparation) =
        spec.Checks |> List.map (fun check -> {CheckId=check.Id; DependsOn=[]; SharedStateScopes=[]})
    let cleanupDiagnostics () =
        try cleanupOwned ()
        with error ->
            // Preserve bounded observation state even when every optional emission fails.
            let emitBestEffort action =
                try action ()
                with _ -> diagnosticReportingFailed <- true
            emitBestEffort (fun () ->
                eprintfn "PREPARED_INSTALLED_DIAGNOSTIC_FIRST_CAUSE %s" (Option.defaultValue "none" firstDiagnosticFailure))
            emitBestEffort (fun () ->
                eprintfn "PREPARED_INSTALLED_DIAGNOSTIC_RETIREMENT_REFUSED %s" error.Message)
            emitBestEffort (fun () ->
                eprintfn "PREPARED_INSTALLED_DIAGNOSTIC_OBSERVATIONS %s" (JsonSerializer.Serialize(observations.ToArray())))
            emitBestEffort (fun () ->
                eprintfn "PREPARED_INSTALLED_DIAGNOSTIC_REPORTING_REFUSED %b" diagnosticReportingFailed)
            // No reporting exception can escape before the original retirement exception.
            reraise ()
    let detailed name spec selected deadline =
        let report,milliseconds = time (fun () -> PreparedAttempt.prepareDetailedAsync binding deadline spec selected CancellationToken.None |> fun t -> t.GetAwaiter().GetResult())
        if firstDiagnosticFailure.IsNone then firstDiagnosticFailure <- report.FirstFailure
        verifyOutput ()
        let findingProjection =
            report.Findings |> List.map (fun f ->
                {| checkId = f.CheckId; outcome = string f.Outcome;
                   cause = Option.toObj f.Cause; exitCode = Option.toNullable f.ExitCode;
                   cleanupObserved = Option.toNullable f.CleanupObserved;
                   reportingFailure = Option.toObj f.ReportingFailure |})
        observations.Add(box
            {| name = name; result = Option.defaultValue "ready" report.FirstFailure;
               candidateBinding = report.CandidateBinding; dependenciesBinding = report.DependenciesBinding;
               prepared = report.Prepared.IsSome; firstFailure = Option.toObj report.FirstFailure;
               additionalFailures = report.AdditionalFailures; findings = findingProjection;
               omittedChecks = report.OmittedChecks; truncated = report.Truncated;
               elapsedMilliseconds = milliseconds |})
        report
    let success = fixture "diagnostic-success"
    let selected = declarations success
    let report = detailed "diagnostic-success" success selected (DateTimeOffset.UtcNow.AddSeconds 15.)
    demand (report.Prepared.IsSome && report.FirstFailure.IsNone && (report.Findings |> List.forall (fun f -> f.Outcome=PreparationCheckOutcome.Passed))) "detailed-success-incomplete"
    demand (PreparedAttempt.validateDetailed DateTimeOffset.UtcNow binding success selected report.Prepared.Value = Ok()) "detailed-consume-refused"
    demand (PreparedAttempt.validateDetailed DateTimeOffset.UtcNow binding success (selected |> List.map (fun d -> {d with SharedStateScopes=["changed"]})) report.Prepared.Value = Error "preparation-dependencies-invalidated") "declaration-drift-admitted"
    // Completed controls retain descriptive observations, then retire their exact held fixture leaves.
    // This bounds the live output population without increasing the existing 128-entry envelope.
    cleanupDiagnostics ()
    for name in ["independent";"dependent";"shared-state";"unknown-dependency";"unknown-result";"contamination";"cleanup";"reporting";"output";"deadline"] do
        let mutable spec = fixture ("diagnostic-"+name)
        let source =
            match name with
            | "independent" -> "import sys\nif sys.argv[1]=='imports': import absent_first\nelse: import absent_second\n"
            | "unknown-result" -> "print('unknown')\n"
            | "contamination" -> "open('dependency.py','a').write('# changed\\n')\nraise RuntimeError('first cause')\n"
            | "cleanup" -> "import os\nos.rmdir(os.environ['TMPDIR'])\nraise RuntimeError('first cause')\n"
            | "reporting" -> "import sys\nsys.stdout.write('x'*2200)\nsys.stderr.write('y'*2200)\nsys.exit(1)\n"
            | "output" -> "print('x'*10000)\n"
            | _ -> "raise RuntimeError('first cause')\n"
        File.WriteAllText(Path.Combine(spec.Root,"checker.py"),source)
        let selected =
            declarations spec |> List.map (fun d ->
                match name with
                | "dependent" when d.CheckId="discovery" -> {d with DependsOn=["imports"]}
                | "unknown-dependency" when d.CheckId="discovery" -> {d with DependsOn=["missing"]}
                | "shared-state" -> {d with SharedStateScopes=["shared"]}
                | _ -> d)
        let deadline = DateTimeOffset.UtcNow.AddSeconds(if name="deadline" then -1. else 15.)
        let report = detailed ("diagnostic-"+name) spec selected deadline
        demand (report.Prepared.IsNone && report.FirstFailure.IsSome) ("diagnostic-negative-admitted:"+name)
        if name="independent" then
            demand (report.Findings.Length=2 && (report.Findings |> List.forall (fun f -> f.Outcome=PreparationCheckOutcome.Failed && f.CleanupObserved=Some true))) "independent-findings-lost"
            demand (report.FirstFailure=Some "preparation-import-missing:absent_first" && List.contains "preparation-import-missing:absent_second" report.AdditionalFailures) "independent-causes-lost"
        elif name<>"deadline" then
            let first,second = report.Findings[0],report.Findings[1]
            demand (second.ExitCode.IsNone && second.Outcome<>PreparationCheckOutcome.Passed) ("dependent-check-ran:"+name)
            if name="cleanup" then
                demand (report.FirstFailure=Some "preparation-check-failed:imports" && first.CleanupObserved=Some false && List.contains "preparation-cleanup-unobserved" report.AdditionalFailures) "cleanup-masked-first-cause"
            elif name="reporting" then
                demand (report.FirstFailure=Some "preparation-check-failed:imports" && first.ReportingFailure.IsSome) "reporting-masked-first-cause"
            elif name="unknown-result" then demand (first.Outcome=PreparationCheckOutcome.Unknown) "unknown-result-misclassified"
            elif name="output" then demand (second.Outcome=PreparationCheckOutcome.NotRunBound) "output-bound-unreported"
        cleanupDiagnostics ()
    // These are prerequisite-only capsules. The legacy executor controls below prove zero workload effects.
    for name in ["missing";"empty";"malformed";"timeout";"tool";"during-check-drift"] do
        let mutable spec = fixture name
        match name with
        | "missing" ->
            let root = liveOwnedOutputs () |> Seq.find (fun value -> value.Parent=outputDescriptor && value.Name=name && value.Directory)
            let dependency = liveOwnedOutputs () |> Seq.find (fun value -> value.Parent=root.Descriptor && value.Name="dependency.py")
            retireOwned dependency
            spec <- {spec with Inputs=spec.Inputs |> List.filter ((<>) "dependency.py")}
        | "empty" -> File.WriteAllText(Path.Combine(spec.Root,"test_entry.py"),"import unittest\n")
        | "malformed" -> File.WriteAllText(Path.Combine(spec.Root,"checker.py"),"print('malformed')\n")
        | "timeout" -> File.WriteAllText(Path.Combine(spec.Root,"checker.py"),"import time\ntime.sleep(3)\n")
        | "tool" -> spec <- {spec with Checks=spec.Checks |> List.map (fun c -> {c with Executable=Path.Combine(work,"absent-python")})}
        | "during-check-drift" -> File.WriteAllText(Path.Combine(spec.Root,"checker.py"),checkerSource.Replace("print(json.dumps", "if mode=='discovery':\n open('dependency.py','a').write('# drift\\n')\nprint(json.dumps"))
        | _ -> invalidOp "unknown-scenario"
        refuse name spec
    let positive = fixture "executor-good"
    let (outcome,runner),elapsed = time (fun () -> execute "executor-good" positive)
    match outcome with
    | Completed receipt -> demand (receipt.CleanupCompleted && runner.Calls=1 && runner.Cleanups=1) "positive-runner-control-failed"
    | _ -> invalidOp "positive-executor-refused"
    for value in liveOwnedOutputs () do
        if value.Directory then
            demand (not (Directory.Exists(Path.Combine(anchored value.Descriptor,"__pycache__")))) "checker-output-in-inputs"
    record "executor-good" "completed-instrumented-runner" runner.Calls runner.Cleanups elapsed
    // Recheck every external immutable input before issuing descriptive evidence.
    pinned package "--expected-package-sha256"
    pinned source "--expected-source-sha256"
    pinned assembly "--expected-execution-sha256"
    pinned core "--expected-core-sha256"
    pinned host "--expected-host-sha256"
    pinned python "--expected-python-sha256"
    pinned fsiDll "--expected-fsi-sha256"
    demand (File.ReadAllText rewrittenScript = rewritten) "rewritten-source-drift"
    for path,_,expected in assemblyInputs do demand (fileHash path = expected) "extracted-assembly-drift"
    for path,identity,sha in immutableInputs do
        regular path
        demand (node -100 path 0x100 |> nodeId = identity) "immutable-input-inode-drift"
        demand (fileHash path = sha) "immutable-input-byte-drift"
    verifyOutput ()
    // Remove test-owned capsules/journals before sealing the sole output.
    cleanupOwned ()
    let anchoredEvidence = $"/proc/self/fd/{outputDescriptor}/{Path.GetFileName evidence}"
    use output = new FileStream(anchoredEvidence,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)
    let evidenceDescriptor = output.SafeFileHandle.DangerousGetHandle() |> int
    let evidenceIdentity = node evidenceDescriptor "" 0x1000 |> nodeId
    demand (Nodes.chmodHeld(evidenceDescriptor,384u)=0) "held-evidence-permissions-refused"
    demand ((node evidenceDescriptor "" 0x1000).Mode &&& 0xfffus = 0x180us) "evidence-mode-not-0600"
    let receipt =
        {| schema="fsgg.prepared-attempt-installed-qualification/1"
           packageVersion=get "--package-version"; packageSha256=fileHash package; executionAssemblySha256=fileHash assembly
           hostSha256=fileHash host; coreSha256=fileHash core; pythonSha256=fileHash python; sourceScriptSha256=fileHash source
           fsiSha256=fileHash fsiDll; rewrittenScriptSha256=fileHash rewrittenScript; checkerSourceSha256=hash (Encoding.UTF8.GetBytes checkerSource)
           assemblyInputs=assemblyInputs |> Array.map (fun (_,entry,sha) -> {| entry=entry; sha256=sha |})
           inactiveArchiveAssemblies=inactiveArchiveAssemblies |> Array.map (fun (entry,sha) -> {| entry=entry; sha256=sha; scope="archive-only; not extracted or qualified" |})
           immutableInputs=immutableInputs |> List.map (fun (_,identity,sha) -> {| identity=identity; sha256=sha |})
           outputDirectoryIdentity=outputIdentity
           outputFileIdentity=evidenceIdentity
           executionAssemblyVersion=typeof<PreparedAttempt>.Assembly.GetName().Version.ToString()
           runtimeVersion=Environment.Version.ToString()
           observations=observations.ToArray(); productAdoptionQualified=false; nativeOperationAuthorized=false; publicationAuthorized=false |}
    let data = JsonSerializer.SerializeToUtf8Bytes receipt
    output.Write(data,0,data.Length)
    output.Flush(true)
    verifyOutput ()
    demand (node outputDescriptor (Path.GetFileName evidence) 0x100 |> nodeId = evidenceIdentity) "output-file-replaced"
    let heldEvidence = node evidenceDescriptor "" 0x1000
    demand (nodeId heldEvidence = evidenceIdentity && heldEvidence.Uid=outputOwner && heldEvidence.Mode &&& 0xfffus = 0x180us) "held-evidence-custody-drift"
    output.Position <- 0L
    use reread = new MemoryStream()
    output.CopyTo reread
    demand (reread.ToArray() = data) "held-evidence-readback-drift"
    verifyOutput ()
    demand (node outputDescriptor (Path.GetFileName evidence) 0x100 |> nodeId = evidenceIdentity) "output-file-replaced"
    printfn "PREPARED_INSTALLED_QUALIFICATION_PASSED evidenceSha256=%s" (hash (reread.ToArray()))
    outputLease.Dispose()
with error ->
    // Preserve diagnostic artifacts for the finite operator to inspect; no successful receipt.
    try eprintfn "PREPARED_INSTALLED_QUALIFICATION_REFUSED %s" error.Message with _ -> ()
    disposeOwned ()
    outputLease.Dispose()
    exit 2
