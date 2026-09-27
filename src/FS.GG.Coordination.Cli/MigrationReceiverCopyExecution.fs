namespace FS.GG.Coordination.Cli

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

type MigrationReceiverCopyOperation =
    | CreateReceiverCopies
    | ReadReceiverCopies
    | RemoveReceiverCopies

type MigrationReceiverCopyExecutionAuthority =
    { TargetRepository: string
      ContentsWrite: bool
      WorkflowsWrite: bool
      ActionsSuppressed: bool
      ProtectedCustody: bool }

type MigrationReceiverCopyExecutionReceipt =
    { Schema: string
      Operation: MigrationReceiverCopyOperation
      AttemptId: string
      ManifestFingerprint: string
      Applied: bool
      DispatchCount: int
      Refs: Map<string, string>
      Fingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyExecution =
    [<Literal>]
    let private fixedTarget = "https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox.git"
    let private require condition code = if not condition then failwith code
    let private sha256 (value: string) = SHA256.HashData(Encoding.UTF8.GetBytes value) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private operationName = function CreateReceiverCopies -> "create" | ReadReceiverCopies -> "read" | RemoveReceiverCopies -> "remove"
    module private Native =
        [<Literal>]
        let O_RDONLY = 0
        [<Literal>]
        let O_DIRECTORY = 0x10000
        [<Literal>]
        let O_NOFOLLOW = 0x20000
        [<Literal>]
        let O_CLOEXEC = 0x80000
        [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
        extern int openNative(string path, int flags, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int fsync(int descriptor)
    let private syncDirectory path =
        if not (OperatingSystem.IsWindows()) then
            let descriptor = Native.openNative(path, Native.O_RDONLY ||| Native.O_DIRECTORY ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0u)
            require (descriptor >= 0) "receiver-copy-execution-directory-open"
            use handle = new SafeFileHandle(nativeint descriptor, true)
            require (Native.fsync(handle.DangerousGetHandle().ToInt32()) = 0) "receiver-copy-execution-directory-fsync"
    let private prefix (manifest: MigrationReceiverCopyTransferManifest) = $"refs/heads/gs2-09-7/{manifest.RunIdentity.RunNonce}/receivers/"
    let private expectedRefs manifest = manifest.DerivedRefs |> List.map (fun row -> row.DerivedRef, row.DerivedCommit) |> Map.ofList
    let private fingerprint operation attempt manifest applied dispatch refs =
        String.concat "\000" ([ "fsgg.receiver-copy-execution-receipt/1"; operationName operation; attempt; manifest; string applied; string dispatch ] @ [ for KeyValue(name, commit) in refs do yield name; yield commit ]) |> sha256
    let private receipt operation attempt manifest applied dispatch refs =
        { Schema = "fsgg.receiver-copy-execution-receipt/1"; Operation = operation; AttemptId = attempt
          ManifestFingerprint = manifest; Applied = applied; DispatchCount = dispatch; Refs = refs
          Fingerprint = fingerprint operation attempt manifest applied dispatch refs }

    let private validateAuthority (authority: MigrationReceiverCopyExecutionAuthority) =
        require (authority.TargetRepository = fixedTarget) "receiver-copy-execution-target"
        require authority.ProtectedCustody "receiver-copy-execution-protected-custody"
        require authority.ContentsWrite "receiver-copy-execution-contents-grant"
        require authority.WorkflowsWrite "receiver-copy-execution-workflows-grant"
        require authority.ActionsSuppressed "receiver-copy-execution-actions-enabled"

    let private validateManifest (manifest: MigrationReceiverCopyTransferManifest) =
        require (manifest.Schema = "fsgg.receiver-copy-transfer-manifest/1" && manifest.TargetRepository = fixedTarget) "receiver-copy-execution-manifest"
        require (manifest.DerivedRefs.Length = 7) "receiver-copy-execution-ref-count"
        let expectedPrefix = prefix manifest
        let ids = manifest.DerivedRefs |> List.map _.ReceiverCopyId |> Set.ofList
        require (ids = Set.ofList [ "audio"; "game"; "governance"; "net"; "rendering"; "sdd"; "templates" ]) "receiver-copy-execution-receiver-population"
        for row in manifest.DerivedRefs do
            require (row.DerivedRef = expectedPrefix + row.ReceiverCopyId) "receiver-copy-execution-ref-mapping"
            require (row.DerivedCommit.Length = 40 && row.DerivedTree.Length = 40) "receiver-copy-execution-object-id"

    let private ensureRoot path =
        let full = Path.GetFullPath path
        if not (Directory.Exists full) then
            Directory.CreateDirectory full |> ignore
            if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(full, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        require (isNull (DirectoryInfo(full).LinkTarget)) "receiver-copy-execution-attempt-root-link"
        if not (OperatingSystem.IsWindows()) then
            let mode = File.GetUnixFileMode full
            require ((mode &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)) = enum 0) "receiver-copy-execution-attempt-root-mode"
        full

    let private reserve root operation attempt manifest =
        let path = Path.Combine(root, operationName operation + ".intent")
        let bytes = Encoding.UTF8.GetBytes($"fsgg.receiver-copy-execution-intent/1\n{operationName operation}\n{attempt}\n{manifest}\n")
        try
            use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)
            if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            stream.Write bytes; stream.Flush true
            syncDirectory root
            true
        with :? IOException ->
            require (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes.AsSpan())) "receiver-copy-execution-intent-conflict"
            false

    let private appliedPath root operation = Path.Combine(root, operationName operation + ".applied")
    let private appliedText operation attempt manifest =
        $"fsgg.receiver-copy-execution-applied/1\n{operationName operation}\n{attempt}\n{manifest}\n"
    let private persistApplied root operation attempt manifest =
        let path = appliedPath root operation
        let text = appliedText operation attempt manifest
        if File.Exists path then require (File.ReadAllText path = text) "receiver-copy-execution-applied-conflict"
        else
            use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)
            if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            let bytes = Encoding.UTF8.GetBytes text
            stream.Write bytes; stream.Flush true
            syncDirectory root

    let private verifyApplied root operation attempt manifest =
        let path = appliedPath root operation
        require (File.Exists path && isNull (FileInfo(path).LinkTarget)) "receiver-copy-execution-create-marker-missing"
        if not (OperatingSystem.IsWindows()) then
            let mode = File.GetUnixFileMode path
            require ((mode &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)) = enum 0) "receiver-copy-execution-create-marker-mode"
        require (File.ReadAllText path = appliedText operation attempt manifest) "receiver-copy-execution-create-marker-binding"

    let private classify expected observed =
        if observed |> Map.isEmpty then "empty"
        elif observed = expected then "exact"
        elif observed |> Map.exists (fun name _ -> not (expected |> Map.containsKey name)) then "unknown"
        elif observed |> Map.exists (fun name commit -> expected[name] <> commit) then "wrong"
        else "mixed"

    let private executeCore
        (authority: MigrationReceiverCopyExecutionAuthority)
        (verifiedTransfer: MigrationReceiverCopyVerifiedTransfer)
        operation
        attemptRoot
        (transport: IMigrationReceiverCopyGitTransport)
        cutAfterReservation =
        try
            let manifest = MigrationReceiverCopyTransfer.verifiedManifest verifiedTransfer
            validateAuthority authority; validateManifest manifest
            require transport.SupportsAtomic "receiver-copy-execution-atomic-unsupported"
            let root = ensureRoot attemptRoot
            let expected = expectedRefs manifest
            let attempt = sha256 ($"{operationName operation}\000{manifest.Fingerprint}")
            let verifyFresh () =
                let readbacks = Path.Combine(root, "readbacks")
                if not (Directory.Exists readbacks) then
                    Directory.CreateDirectory readbacks |> ignore
                    if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(readbacks, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                let store = Path.Combine(readbacks, Guid.NewGuid().ToString("N") + ".git")
                match transport.VerifyFresh(fixedTarget, store, manifest) with
                | Ok value when value.Refs = expected -> value
                | Ok _ -> failwith "receiver-copy-execution-fresh-readback"
                | Error error -> failwith error
            let read () =
                match transport.Read(fixedTarget, prefix manifest) with
                | Ok value -> value
                | Error error -> failwith error
            match operation with
            | ReadReceiverCopies ->
                let observed = read ()
                match classify expected observed.Refs with
                | "empty" -> Ok(receipt operation attempt manifest.Fingerprint true 0 Map.empty)
                | "exact" ->
                    verifyFresh () |> ignore
                    Ok(receipt operation attempt manifest.Fingerprint true 0 observed.Refs)
                | code -> Error($"receiver-copy-execution-read-{code}")
            | CreateReceiverCopies ->
                let before = read ()
                match classify expected before.Refs with
                | "exact" ->
                    verifyFresh () |> ignore
                    persistApplied root operation attempt manifest.Fingerprint
                    Ok(receipt operation attempt manifest.Fingerprint true 0 before.Refs)
                | "empty" ->
                    let fresh = reserve root operation attempt manifest.Fingerprint
                    if not fresh then Error "receiver-copy-execution-recovery-absent"
                    elif cutAfterReservation then Error "receiver-copy-execution-cut-after-reservation"
                    else
                        let updates = [ for row in manifest.DerivedRefs -> { RefName = row.DerivedRef; ExpectedOldCommit = None; NewCommit = Some row.DerivedCommit } ]
                        match transport.PushAtomic(manifest.ObjectStorePath, fixedTarget, updates) with
                        | Error error -> Error error
                        | Ok() ->
                            let after = read ()
                            require (after.UnrelatedRefsFingerprint = before.UnrelatedRefsFingerprint) "receiver-copy-execution-unrelated-ref-drift"
                            require (after.Refs = expected) "receiver-copy-execution-create-readback"
                            verifyFresh () |> ignore
                            persistApplied root operation attempt manifest.Fingerprint
                            Ok(receipt operation attempt manifest.Fingerprint true 1 after.Refs)
                | code -> Error($"receiver-copy-execution-create-{code}")
            | RemoveReceiverCopies ->
                let createAttempt = sha256 ($"{operationName CreateReceiverCopies}\000{manifest.Fingerprint}")
                verifyApplied root CreateReceiverCopies createAttempt manifest.Fingerprint
                let before = read ()
                match classify expected before.Refs with
                | "empty" ->
                    persistApplied root operation attempt manifest.Fingerprint
                    Ok(receipt operation attempt manifest.Fingerprint true 0 Map.empty)
                | "exact" ->
                    let fresh = reserve root operation attempt manifest.Fingerprint
                    if not fresh then Error "receiver-copy-execution-cleanup-recovery-present"
                    elif cutAfterReservation then Error "receiver-copy-execution-cut-after-reservation"
                    else
                        let updates = [ for row in manifest.DerivedRefs -> { RefName = row.DerivedRef; ExpectedOldCommit = Some row.DerivedCommit; NewCommit = None } ]
                        match transport.PushAtomic(manifest.ObjectStorePath, fixedTarget, updates) with
                        | Error error -> Error error
                        | Ok() ->
                            let after = read ()
                            require (after.UnrelatedRefsFingerprint = before.UnrelatedRefsFingerprint) "receiver-copy-execution-unrelated-ref-drift"
                            require after.Refs.IsEmpty "receiver-copy-execution-cleanup-readback"
                            persistApplied root operation attempt manifest.Fingerprint
                            Ok(receipt operation attempt manifest.Fingerprint true 1 Map.empty)
                | code -> Error($"receiver-copy-execution-cleanup-{code}")
        with ex -> Error ex.Message

    let execute authority verifiedTransfer operation attemptRoot transport = executeCore authority verifiedTransfer operation attemptRoot transport false
    let internal executeWithCutForTests authority verifiedTransfer operation attemptRoot transport cutAfterReservation =
        executeCore authority verifiedTransfer operation attemptRoot transport cutAfterReservation
