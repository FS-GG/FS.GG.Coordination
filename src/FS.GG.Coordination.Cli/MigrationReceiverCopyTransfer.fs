namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

type MigrationReceiverCopyDerivedRef =
    { ReceiverCopyId: string
      SourceRepository: string
      SourceCommit: string
      SourceTree: string
      DerivedRef: string
      DerivedTree: string
      DerivedCommit: string
      BlobCount: int }

type MigrationReceiverCopyTransferManifest =
    { Schema: string
      TargetRepository: string
      RunIdentity: MigrationSandboxSeedRequest
      PlanFingerprint: string
      BlobCoverageFingerprint: string
      BlobSha256BySha1: Map<string, string>
      ObjectStorePath: string
      DerivedRefs: MigrationReceiverCopyDerivedRef list
      Fingerprint: string }

type MigrationReceiverCopyVerifiedTransfer = private MigrationReceiverCopyVerifiedTransfer of MigrationReceiverCopyTransferManifest

[<RequireQualifiedAccess>]
module MigrationReceiverCopyTransfer =
    [<Literal>]
    let private target = "https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox.git"
    let private utf8 = UTF8Encoding(false, true)
    let private require condition code = if not condition then failwith code
    module private Native =
        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type FileStat =
            val mutable Device: uint64
            val mutable Inode: uint64
            val mutable LinkCount: uint64
            val mutable Mode: uint32
            val mutable UserId: uint32
            val mutable GroupId: uint32
            val mutable Padding: int32
            val mutable DeviceType: uint64
            val mutable Size: int64
            val mutable BlockSize: int64
            val mutable Blocks: int64
            val mutable AccessSeconds: int64
            val mutable AccessNanoseconds: int64
            val mutable ModifySeconds: int64
            val mutable ModifyNanoseconds: int64
            val mutable ChangeSeconds: int64
            val mutable ChangeNanoseconds: int64
            val mutable Reserved0: int64
            val mutable Reserved1: int64
            val mutable Reserved2: int64
        [<Literal>]
        let O_RDONLY = 0
        [<Literal>]
        let O_DIRECTORY = 0x10000
        [<Literal>]
        let O_NOFOLLOW = 0x20000
        [<Literal>]
        let O_CLOEXEC = 0x80000
        [<Literal>]
        let EEXIST = 17
        [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
        extern int openNative(string path, int flags, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int mkdirat(int directory, string path, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int fsync(int descriptor)
        [<DllImport("libc")>]
        extern uint32 getuid()
        [<DllImport("libc", SetLastError = true)>]
        extern int lstat(string path, FileStat& status)
    let private descriptor (handle: SafeFileHandle) = handle.DangerousGetHandle().ToInt32()
    let private privateDirectoryMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let private privateFileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let private publicMode =
        UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute
        ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute

    let private statUid path =
        let mutable status = Unchecked.defaultof<Native.FileStat>
        require (Native.lstat(path, &status) = 0) $"receiver-copy-transfer-stat:{Marshal.GetLastPInvokeError()}"
        status.UserId

    let private verifyPrivateStore root =
        require (Directory.Exists root && isNull (DirectoryInfo(root).LinkTarget)) "receiver-copy-transfer-store-link"
        if not (OperatingSystem.IsWindows()) then
            require (File.GetUnixFileMode(root) = privateDirectoryMode) "receiver-copy-transfer-store-mode"
            require (statUid root = Native.getuid()) "receiver-copy-transfer-store-owner"
            // An unprivileged foreign owner cannot inject beneath this owner-only 0700
            // root; exact nested UID checks also refuse privileged restore/tamper drift.
            let rec walk directory =
                for path in Directory.EnumerateFileSystemEntries directory do
                    let info = FileInfo path
                    require (isNull info.LinkTarget) "receiver-copy-transfer-store-nested-link"
                    require (statUid path = Native.getuid()) "receiver-copy-transfer-store-nested-owner"
                    let mode = File.GetUnixFileMode path
                    require ((mode &&& publicMode) = enum 0) "receiver-copy-transfer-store-nested-mode"
                    if Directory.Exists path then
                        require ((mode &&& privateDirectoryMode) = privateDirectoryMode) "receiver-copy-transfer-store-directory-mode"
                        walk path
                    else require ((mode &&& UnixFileMode.UserRead) = UnixFileMode.UserRead) "receiver-copy-transfer-store-file-mode"
            walk root

    let private makeStorePrivate (root: string) =
        if not (OperatingSystem.IsWindows()) then
            let rec walk (directory: string) =
                for path in Directory.EnumerateFileSystemEntries directory do
                    require (isNull (FileInfo(path).LinkTarget)) "receiver-copy-transfer-store-nested-link"
                    if Directory.Exists path then
                        File.SetUnixFileMode(path, privateDirectoryMode); walk path
                    else File.SetUnixFileMode(path, privateFileMode)
            File.SetUnixFileMode(root, privateDirectoryMode)
            walk root
    let private sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private blobSha1 (bytes: byte array) =
        let header = utf8.GetBytes($"blob {bytes.LongLength}\000")
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private fingerprint (values: seq<string>) =
        use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
        for value in values do
            let bytes = utf8.GetBytes value
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder bytes.Length))
            hash.AppendData bytes
        hash.GetHashAndReset() |> Convert.ToHexString |> _.ToLowerInvariant()

    let private manifestFingerprint target runNonce planFingerprint coverageFingerprint coverageDigests objectStorePath rows =
        fingerprint (seq {
            yield "fsgg.receiver-copy-transfer-manifest/1"; yield target; yield runNonce
            yield planFingerprint; yield coverageFingerprint; yield objectStorePath
            for KeyValue(objectId, digest) in coverageDigests do yield objectId; yield digest
            for row in rows do
                yield row.ReceiverCopyId; yield row.SourceRepository; yield row.SourceCommit; yield row.SourceTree
                yield row.DerivedRef; yield row.DerivedTree; yield row.DerivedCommit; yield string row.BlobCount })

    let private commitMessage sourceRepository sourceCommit sourceTree planFingerprint runNonce =
        String.concat "\n"
            [ "FS.GG receiver copy"; ""; $"Original-Repository: {sourceRepository}"
              $"Original-Commit: {sourceCommit}"; $"Original-Tree: {sourceTree}"
              $"Copy-Plan-Fingerprint: {planFingerprint}"; $"Copy-Run-Nonce: {runNonce}"
              "Source-Ancestry-Replicated: false"; "" ]

    let private treeSha (entries: (string * string * string) list) =
        use body = new MemoryStream()
        for mode, _, objectId, name in
            entries
            |> List.map (fun (path, mode, objectId) -> mode, (if mode = "040000" then "tree" else "blob"), objectId, Path.GetFileName path)
            |> List.sortBy (fun (_, kind, _, name) -> utf8.GetBytes(name + if kind = "tree" then "/" else "") |> Convert.ToHexString) do
            let storedMode = if mode = "040000" then "40000" else mode
            let prefix = utf8.GetBytes($"{storedMode} {name}\000")
            body.Write(prefix, 0, prefix.Length)
            let raw = Convert.FromHexString objectId
            body.Write(raw, 0, raw.Length)
        let bytes = body.ToArray()
        let header = utf8.GetBytes($"tree {bytes.Length}\000")
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private predictRows run planFingerprint receiverBytes =
        [ for receiverId, sourceRepository, sourceCommit, sourceTree, desiredRef, entries in receiverBytes do
            let objectIds = Dictionary<string, string>(StringComparer.Ordinal)
            let blobModes = entries |> List.map (fun (path, mode, _, _) -> path, mode) |> Map.ofList
            for path, _, _, bytes in entries do objectIds[path] <- blobSha1 bytes
            let directoryPaths =
                entries |> List.map (fun (path, _, _, _) -> path)
                |> List.collect (fun path -> let parts = path.Split('/') in [ for count in 1 .. parts.Length - 1 -> String.Join('/', parts[0..count-1]) ])
                |> Set.ofList |> Set.toList |> List.sortByDescending (fun path -> path.Split('/').Length)
            for directory in directoryPaths do
                let prefix = directory + "/"
                let children =
                    [ for KeyValue(path, objectId) in objectIds do
                        if path.StartsWith(prefix, StringComparison.Ordinal) && not ((path.Substring(prefix.Length)).Contains('/')) then
                            yield path, (if directoryPaths |> List.contains path then "040000" else blobModes[path]), objectId ]
                objectIds[directory] <- treeSha children
            let roots =
                [ for KeyValue(path, objectId) in objectIds do
                    if not (path.Contains('/')) then yield path, (if directoryPaths |> List.contains path then "040000" else blobModes[path]), objectId ]
            let derivedTree = treeSha roots
            let boundSourceTree = if String.IsNullOrEmpty sourceTree then derivedTree else sourceTree
            require (derivedTree = boundSourceTree) "receiver-copy-transfer-tree-drift"
            let message = commitMessage sourceRepository sourceCommit boundSourceTree planFingerprint run.RunNonce
            let body =
                String.concat ""
                    [ $"tree {derivedTree}\n"
                      "author FS.GG Migration Copy <migration-copy@invalid> 946684800 +0000\n"
                      "committer FS.GG Migration Copy <migration-copy@invalid> 946684800 +0000\n\n"
                      message ] |> utf8.GetBytes
            let header = utf8.GetBytes($"commit {body.Length}\000")
            let commit = SHA1.HashData(Array.append header body) |> Convert.ToHexString |> _.ToLowerInvariant()
            yield { ReceiverCopyId = receiverId; SourceRepository = sourceRepository; SourceCommit = sourceCommit
                    SourceTree = boundSourceTree; DerivedRef = desiredRef; DerivedTree = derivedTree
                    DerivedCommit = commit; BlobCount = entries.Length } ] |> List.sortBy _.ReceiverCopyId

    let private configure (info: ProcessStartInfo) =
        info.UseShellExecute <- false
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        info.Environment.Keys
        |> Seq.cast<string>
        |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
        |> Array.ofSeq
        |> Array.iter (fun key -> info.Environment.Remove key |> ignore)
        info.Environment["LC_ALL"] <- "C"
        info.Environment["GIT_NO_LAZY_FETCH"] <- "1"
        info.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"

    let private gitBytes repository arguments (input: byte array) =
        use child = new Process()
        let info = ProcessStartInfo("/usr/bin/git")
        for argument in seq { yield "--no-replace-objects"; if repository <> "" then yield "-C"; yield repository; yield! arguments } do
            if argument <> "" then info.ArgumentList.Add argument
        configure info
        child.StartInfo <- info
        require (child.Start()) "receiver-copy-transfer-git-start"
        child.StandardInput.BaseStream.Write(input, 0, input.Length)
        child.StandardInput.Close()
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        require (child.WaitForExit(30000)) "receiver-copy-transfer-git-timeout"
        let stdout, stderr = output.GetAwaiter().GetResult().Trim(), error.GetAwaiter().GetResult().Trim()
        let command = String.concat "," arguments
        require (child.ExitCode = 0) $"receiver-copy-transfer-git-exit:{child.ExitCode}:{command}:{stderr}"
        stdout
    let private git repository arguments = gitBytes repository arguments Array.empty

    let private initializeStore root =
        let full = Path.GetFullPath root
        require (not (Directory.Exists full) && not (File.Exists full)) "receiver-copy-transfer-store-exists"
        let parent = Directory.GetParent full
        require (not (isNull parent) && parent.Exists && isNull parent.LinkTarget) "receiver-copy-transfer-store-parent"
        if OperatingSystem.IsWindows() then
            Directory.CreateDirectory full |> ignore
        else
            let parentDescriptor = Native.openNative(parent.FullName, Native.O_RDONLY ||| Native.O_DIRECTORY ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0u)
            require (parentDescriptor >= 0) "receiver-copy-transfer-store-parent-open"
            use parentHandle = new SafeFileHandle(nativeint parentDescriptor, true)
            let created = Native.mkdirat(descriptor parentHandle, Path.GetFileName full, 0x1C0u)
            let error = Marshal.GetLastPInvokeError()
            require (created = 0) (if error = Native.EEXIST then "receiver-copy-transfer-store-exists" else $"receiver-copy-transfer-store-create:{error}")
            require (Native.fsync(descriptor parentHandle) = 0) "receiver-copy-transfer-store-parent-fsync"
        verifyPrivateStore full
        git full [ "init"; "--bare"; "--object-format=sha1"; "--shared=0600"; "." ] |> ignore
        makeStorePrivate full
        verifyPrivateStore full
        full

    let private tree repository (entries: (string * string * string) list) =
        use stream = new MemoryStream()
        for mode, kind, objectId, name in
            entries
            |> List.map (fun (path, mode, objectId) -> mode, (if mode = "040000" then "tree" else "blob"), objectId, Path.GetFileName path)
            |> List.sortBy (fun (_, kind, _, name) -> utf8.GetBytes(name + if kind = "tree" then "/" else "") |> Convert.ToHexString) do
            let prefix = utf8.GetBytes($"{mode} {kind} {objectId}\t{name}\000")
            stream.Write(prefix, 0, prefix.Length)
        gitBytes repository [ "mktree"; "-z" ] (stream.ToArray())

    let private materialize
        (run: MigrationSandboxSeedRequest)
        (planFingerprint: string)
        (coverageFingerprint: string)
        (coverageDigests: Map<string, string>)
        (root: string)
        (receiverBytes: (string * string * string * string * string * (string * string * string * byte array) list) list) =
        try
            let repository = initializeStore root
            let predicted = predictRows run planFingerprint receiverBytes |> List.map (fun row -> row.ReceiverCopyId, row) |> Map.ofList
            let derived = ResizeArray<MigrationReceiverCopyDerivedRef>()
            for receiverId, sourceRepository, sourceCommit, sourceTree, desiredRef, entries in receiverBytes do
                require (not entries.IsEmpty) "receiver-copy-transfer-empty-tree"
                let paths = entries |> List.map (fun (path, _, _, _) -> path)
                require (paths.Length = (paths |> Set.ofList |> Set.count)) "receiver-copy-transfer-path-duplicate"
                let objectIds = Dictionary<string, string>(StringComparer.Ordinal)
                let blobModes = entries |> List.map (fun (path, mode, _, _) -> path, mode) |> Map.ofList
                for path, mode, kind, bytes in entries do
                    require (kind = "blob" && (mode = "100644" || mode = "100755")) "receiver-copy-transfer-entry-shape"
                    let objectId = gitBytes repository [ "hash-object"; "-w"; "--stdin" ] bytes
                    require (objectId = blobSha1 bytes) "receiver-copy-transfer-written-blob"
                    objectIds[path] <- objectId
                let directoryPaths =
                    paths
                    |> List.collect (fun path ->
                        let parts = path.Split('/')
                        [ for count in 1 .. parts.Length - 1 -> String.Join('/', parts[0..count-1]) ])
                    |> Set.ofList
                    |> Set.toList
                    |> List.sortByDescending (fun path -> path.Split('/').Length)
                for directory in directoryPaths do
                    let prefix = directory + "/"
                    let children =
                        [ for KeyValue(path, objectId) in objectIds do
                            if path.StartsWith(prefix, StringComparison.Ordinal) && not ((path.Substring(prefix.Length)).Contains('/')) then
                                yield path, (if directoryPaths |> List.contains path then "040000" else blobModes[path]), objectId ]
                    objectIds[directory] <- tree repository children
                let roots =
                    [ for KeyValue(path, objectId) in objectIds do
                        if not (path.Contains('/')) then
                            let mode = if directoryPaths |> List.contains path then "040000" else entries |> List.find (fun (p, _, _, _) -> p = path) |> fun (_, m, _, _) -> m
                            yield path, mode, objectId ]
                let derivedTree = tree repository roots
                let boundSourceTree = if String.IsNullOrEmpty sourceTree then derivedTree else sourceTree
                require (derivedTree = boundSourceTree) "receiver-copy-transfer-tree-drift"
                let message = commitMessage sourceRepository sourceCommit boundSourceTree planFingerprint run.RunNonce
                let infoEnv =
                    [ "GIT_AUTHOR_NAME", "FS.GG Migration Copy"
                      "GIT_AUTHOR_EMAIL", "migration-copy@invalid"
                      "GIT_AUTHOR_DATE", "2000-01-01T00:00:00Z"
                      "GIT_COMMITTER_NAME", "FS.GG Migration Copy"
                      "GIT_COMMITTER_EMAIL", "migration-copy@invalid"
                      "GIT_COMMITTER_DATE", "2000-01-01T00:00:00Z" ]
                // commit-tree reads identity from its sanitized child environment.
                use child = new Process()
                let info = ProcessStartInfo("/usr/bin/git")
                for argument in [ "--no-replace-objects"; "-C"; repository; "commit-tree"; derivedTree ] do info.ArgumentList.Add argument
                configure info
                for key, value in infoEnv do info.Environment[key] <- value
                child.StartInfo <- info
                require (child.Start()) "receiver-copy-transfer-commit-start"
                child.StandardInput.Write message; child.StandardInput.Close()
                let commit = child.StandardOutput.ReadToEnd().Trim()
                let error = child.StandardError.ReadToEnd().Trim()
                require (child.WaitForExit(30000) && child.ExitCode = 0) $"receiver-copy-transfer-commit:{error}"
                git repository [ "update-ref"; desiredRef; commit; String.replicate 40 "0" ] |> ignore
                let parents = git repository [ "rev-list"; "--parents"; "-n"; "1"; commit ]
                require (parents = commit) "receiver-copy-transfer-commit-parent"
                require (git repository [ "rev-parse"; $"{commit}^{{tree}}" ] = derivedTree) "receiver-copy-transfer-commit-tree"
                require (predicted[receiverId].DerivedCommit = commit) "receiver-copy-transfer-predicted-commit"
                derived.Add
                    { ReceiverCopyId = receiverId; SourceRepository = sourceRepository; SourceCommit = sourceCommit
                      SourceTree = boundSourceTree; DerivedRef = desiredRef; DerivedTree = derivedTree
                      DerivedCommit = commit; BlobCount = entries.Length }
            let rows = derived |> Seq.sortBy _.ReceiverCopyId |> List.ofSeq
            require (rows.Length = (rows |> List.map _.DerivedRef |> Set.ofList |> Set.count)) "receiver-copy-transfer-ref-duplicate"
            require (rows = (predicted |> Map.values |> List.ofSeq)) "receiver-copy-transfer-predicted-rows"
            makeStorePrivate repository
            verifyPrivateStore repository
            let digest = manifestFingerprint target run.RunNonce planFingerprint coverageFingerprint coverageDigests repository rows
            Ok { Schema = "fsgg.receiver-copy-transfer-manifest/1"; TargetRepository = target; RunIdentity = run
                 PlanFingerprint = planFingerprint; BlobCoverageFingerprint = coverageFingerprint; BlobSha256BySha1 = coverageDigests
                 ObjectStorePath = repository; DerivedRefs = rows; Fingerprint = digest }
        with ex -> Error ex.Message

    let private verifiedInputs accepted run plan batches artifacts coverage =
        MigrationReceiverCopyPlan.verify accepted run plan
        |> Result.bind (fun verifiedPlan ->
            MigrationReceiverCopyBlobCapture.verifyCoverage accepted run verifiedPlan batches artifacts
            |> Result.bind (fun verifiedCoverage ->
                if verifiedCoverage <> coverage then Error "receiver-copy-transfer-coverage-drift"
                else Ok(verifiedPlan, verifiedCoverage)))

    let private canonicalObjectStore (run: MigrationSandboxSeedRequest) =
        Path.Combine(Path.GetTempPath(), $"gs2-09-7-receiver-transfer-{run.RunNonce}.git") |> Path.GetFullPath

    let private loadReceivers (source: IMigrationReceiverCopyVerifiedObjectSource) (plan: MigrationReceiverCopyPlanResult) (coverage: MigrationReceiverCopyBlobCoverage) =
        [ for mapping in plan.ReceiverCopyMappings do
            let entries =
                [ for entry in mapping.ReceiverCopyRequiredEntries do
                    if entry.EntryKind = "blob" then
                        let digest = coverage.ReceiverCopyBlobCoverageSha256BySha1[entry.EntrySha]
                        let bytes =
                            match source.ReadBlob entry.EntrySha with
                            | Ok value -> value.ToArray()
                            | Error error -> failwith error
                        require (entry.EntrySize = Some(int64 bytes.Length)) "receiver-copy-transfer-object-size"
                        require (sha256 bytes = digest) "receiver-copy-transfer-object-sha256"
                        require (blobSha1 bytes = entry.EntrySha) "receiver-copy-transfer-object-sha1"
                        yield entry.EntryPath, entry.EntryMode, entry.EntryKind, bytes ]
            yield mapping.ReceiverCopyId, mapping.ReceiverSourceRepository, mapping.ReceiverCopySourceRevision,
                  mapping.ReceiverCopySourceTree, mapping.ReceiverCopyPlannedRef, entries ]

    let prepare acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage objectStoreRoot =
        verifiedInputs acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage
        |> Result.bind (fun (verifiedPlan, verifiedCoverage) ->
            try
                require (Path.GetFullPath objectStoreRoot = canonicalObjectStore runIdentity) "receiver-copy-transfer-store-path"
                MigrationReceiverCopyBlobCapture.createVerifiedCompleteObjectSource acceptedEvidence runIdentity verifiedPlan batches artifacts verifiedCoverage
                |> Result.bind (fun source ->
                    use source = source
                    try loadReceivers source verifiedPlan verifiedCoverage |> materialize runIdentity verifiedPlan.ReceiverCopyFingerprint verifiedCoverage.ReceiverCopyBlobCoverageFingerprint verifiedCoverage.ReceiverCopyBlobCoverageSha256BySha1 objectStoreRoot
                    with ex -> Error ex.Message)
            with ex -> Error ex.Message)

    let verify acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage observed =
        verifiedInputs acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage
        |> Result.bind (fun (verifiedPlan, verifiedCoverage) ->
            let expectedPath = canonicalObjectStore runIdentity
            try
                require (observed.ObjectStorePath = expectedPath) "receiver-copy-transfer-store-identity"
                verifyPrivateStore expectedPath
                Ok()
            with ex -> Error ex.Message
            |> Result.bind (fun () ->
                MigrationReceiverCopyBlobCapture.createVerifiedCompleteObjectSource acceptedEvidence runIdentity verifiedPlan batches artifacts verifiedCoverage
                |> Result.bind (fun source ->
                    use source = source
                    try
                        let receiverBytes = loadReceivers source verifiedPlan verifiedCoverage
                        let expectedRows = predictRows runIdentity verifiedPlan.ReceiverCopyFingerprint receiverBytes
                        require (observed.Schema = "fsgg.receiver-copy-transfer-manifest/1" && observed.TargetRepository = target) "receiver-copy-transfer-manifest-shape"
                        require (observed.RunIdentity = runIdentity && observed.PlanFingerprint = verifiedPlan.ReceiverCopyFingerprint && observed.BlobCoverageFingerprint = verifiedCoverage.ReceiverCopyBlobCoverageFingerprint) "receiver-copy-transfer-manifest-binding"
                        require (observed.BlobSha256BySha1 = verifiedCoverage.ReceiverCopyBlobCoverageSha256BySha1) "receiver-copy-transfer-blob-coverage"
                        require (observed.DerivedRefs = expectedRows) "receiver-copy-transfer-derived-refs"
                        let expectedFingerprint = manifestFingerprint target runIdentity.RunNonce verifiedPlan.ReceiverCopyFingerprint verifiedCoverage.ReceiverCopyBlobCoverageFingerprint verifiedCoverage.ReceiverCopyBlobCoverageSha256BySha1 expectedPath expectedRows
                        require (observed.Fingerprint = expectedFingerprint) "receiver-copy-transfer-manifest-fingerprint"
                        require (git expectedPath [ "rev-parse"; "--is-bare-repository" ] = "true") "receiver-copy-transfer-store-bare"
                        for row in expectedRows do
                            require (git expectedPath [ "rev-parse"; row.DerivedRef ] = row.DerivedCommit) "receiver-copy-transfer-ref-drift"
                            require (git expectedPath [ "rev-parse"; $"{row.DerivedCommit}^{{tree}}" ] = row.DerivedTree) "receiver-copy-transfer-tree-readback"
                            require (git expectedPath [ "rev-list"; "--parents"; "-n"; "1"; row.DerivedCommit ] = row.DerivedCommit) "receiver-copy-transfer-parent-readback"
                        Ok(MigrationReceiverCopyVerifiedTransfer observed)
                    with ex -> Error ex.Message)))

    let internal prepareSyntheticForTests runIdentity planFingerprint coverageFingerprint objectStoreRoot receivers =
        let digests =
            [ for _, _, _, _, _, entries in receivers do
                for _, _, _, bytes in entries do yield blobSha1 bytes, sha256 bytes ] |> Map.ofList
        materialize runIdentity planFingerprint coverageFingerprint digests objectStoreRoot receivers

    let internal verifySyntheticForTests runIdentity planFingerprint coverageFingerprint objectStoreRoot (receivers: (string * string * string * string * string * (string * string * string * byte array) list) list) observed =
        try
            let digests = [ for _, _, _, _, _, entries in receivers do for _, _, _, bytes in entries do yield blobSha1 bytes, sha256 bytes ] |> Map.ofList
            let rows = predictRows runIdentity planFingerprint receivers
            require (observed.Schema = "fsgg.receiver-copy-transfer-manifest/1" && observed.TargetRepository = target) "receiver-copy-transfer-manifest-shape"
            require (observed.RunIdentity = runIdentity && observed.PlanFingerprint = planFingerprint && observed.BlobCoverageFingerprint = coverageFingerprint) "receiver-copy-transfer-manifest-binding"
            require (observed.ObjectStorePath = Path.GetFullPath objectStoreRoot) "receiver-copy-transfer-store-identity"
            verifyPrivateStore observed.ObjectStorePath
            require (observed.BlobSha256BySha1 = digests && observed.DerivedRefs = rows) "receiver-copy-transfer-derived-refs"
            let expectedFingerprint = manifestFingerprint target runIdentity.RunNonce planFingerprint coverageFingerprint digests (Path.GetFullPath objectStoreRoot) rows
            require (observed.Fingerprint = expectedFingerprint) "receiver-copy-transfer-manifest-fingerprint"
            Ok(MigrationReceiverCopyVerifiedTransfer observed)
        with ex -> Error ex.Message

    let internal recomputeFingerprintForTests observed =
        let rows = observed.DerivedRefs |> List.sortBy _.ReceiverCopyId
        { observed with DerivedRefs = rows
                        Fingerprint = manifestFingerprint observed.TargetRepository observed.RunIdentity.RunNonce observed.PlanFingerprint observed.BlobCoverageFingerprint observed.BlobSha256BySha1 observed.ObjectStorePath rows }

    let internal verifiedManifest (MigrationReceiverCopyVerifiedTransfer manifest) = manifest

    let internal validateVerifiedStore (MigrationReceiverCopyVerifiedTransfer manifest) =
        try
            verifyPrivateStore manifest.ObjectStorePath
            for row in manifest.DerivedRefs do
                require (git manifest.ObjectStorePath [ "rev-parse"; row.DerivedRef ] = row.DerivedCommit) "receiver-copy-transfer-ref-drift"
            Ok()
        with ex -> Error ex.Message
