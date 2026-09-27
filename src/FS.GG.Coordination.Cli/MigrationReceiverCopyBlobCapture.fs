namespace FS.GG.Coordination.Cli

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles

type IMigrationReceiverCopyLocalGitObjectSource =
    inherit IDisposable
    abstract ReadBlob: sha1:string -> Result<ReadOnlyMemory<byte>, string>

type IMigrationReceiverCopyVerifiedObjectSource =
    inherit IDisposable
    abstract ReadBlob: sha1:string -> Result<ReadOnlyMemory<byte>, string>

type MigrationReceiverCopyBlobBatch =
    { ReceiverCopyBlobPlanFingerprint: string
      ReceiverCopyBlobBatchOrdinal: int
      ReceiverCopyBlobSha1s: string list
      ReceiverCopyBlobExpectedBytes: int64
      ReceiverCopyBlobBatchFingerprint: string }

type MigrationReceiverCopyBlobBatchArtifact =
    { ReceiverCopyBlobArtifactPlanFingerprint: string
      ReceiverCopyBlobArtifactBatchOrdinal: int
      ReceiverCopyBlobArtifactPath: string
      ReceiverCopyBlobArtifactSha256: string
      ReceiverCopyBlobSha256BySha1: Map<string, string>
      ReceiverCopyBlobArtifactFingerprint: string }

type MigrationReceiverCopyBlobCoverage =
    { ReceiverCopyBlobCoveragePlanFingerprint: string
      ReceiverCopyBlobCoverageBatchFingerprints: string list
      ReceiverCopyBlobCoverageSha256BySha1: Map<string, string>
      ReceiverCopyBlobCoverageFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyBlobCapture =
    let private utf8 = UTF8Encoding(false, true)
    let private jsonOptions = JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    let private require condition code = if not condition then failwith code
    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
    let private sha256 (bytes: byte array) = SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private blobSha1 (bytes: byte array) =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1)
        hash.AppendData(utf8.GetBytes($"blob {bytes.Length}\000")); hash.AppendData bytes
        hash.GetHashAndReset() |> Convert.ToHexString |> _.ToLowerInvariant()
    let private fingerprint values =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        for value: string in values do
            let bytes = utf8.GetBytes value
            let length = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteInt64BigEndian(length.AsSpan(), int64 bytes.Length)
            hash.AppendData length; hash.AppendData bytes
        hash.GetHashAndReset() |> Convert.ToHexString |> _.ToLowerInvariant()

    type private Population = { Sizes: Map<string, int64>; Sources: Map<string, string list> }

    let private verifyInputs evidence run observed =
        MigrationReceiverCopyPlan.verify evidence run observed
        |> Result.bind (fun plan ->
            try
                require (plan.ReceiverCopyMappings.Length = 7) "receiver-copy-blob-receiver-count"
                require (plan.ReceiverCopyMappings |> List.map _.ReceiverCopyId |> Set.ofList |> Set.count = 7) "receiver-copy-blob-receiver-duplicate"
                let sizes = Dictionary<string, int64>(StringComparer.Ordinal)
                let sources = Dictionary<string, ResizeArray<string>>(StringComparer.Ordinal)
                let mutable occurrences = 0
                for mapping in plan.ReceiverCopyMappings do
                    require (mapping.ReceiverCopyMissingBlobSha1s.Length = (mapping.ReceiverCopyMissingBlobSha1s |> Set.ofList |> Set.count)) "receiver-copy-blob-mapping-duplicate"
                    let entries =
                        mapping.ReceiverCopyRequiredEntries
                        |> List.choose (fun entry -> if entry.EntryKind = "blob" then entry.EntrySize |> Option.map (fun size -> entry.EntrySha, size) else None)
                        |> Map.ofList
                    for objectId in mapping.ReceiverCopyMissingBlobSha1s do
                        require (hex 40 objectId) "receiver-copy-blob-sha1"
                        let size = entries |> Map.tryFind objectId |> Option.defaultWith (fun () -> failwith "receiver-copy-blob-occurrence")
                        require (size >= 0L && size <= 1048576L) "receiver-copy-blob-size"
                        match sizes.TryGetValue objectId with
                        | true, previous -> require (previous = size) "receiver-copy-blob-cross-receiver-size"
                        | _ -> sizes.Add(objectId, size)
                        match sources.TryGetValue objectId with
                        | true, value -> value.Add mapping.ReceiverCopyId
                        | _ -> sources.Add(objectId, ResizeArray([ mapping.ReceiverCopyId ]))
                        occurrences <- occurrences + 1
                let sizeMap = sizes |> Seq.map (fun row -> row.Key, row.Value) |> Map.ofSeq
                require (occurrences = 8808) "receiver-copy-blob-occurrence-count"
                require (sizeMap.Count = 8539) "receiver-copy-blob-deduplicated-count"
                require (sizeMap |> Map.values |> Seq.sum = 78453001L) "receiver-copy-blob-byte-count"
                require (sizeMap |> Map.values |> Seq.max = 876541L) "receiver-copy-blob-maximum-size"
                require (plan.ReceiverCopyRetainedBlobSha256BySha1.Count = 460) "receiver-copy-blob-retained-count"
                require
                    (Set.intersect
                        (sizeMap |> Map.keys |> Set.ofSeq)
                        (plan.ReceiverCopyRetainedBlobSha256BySha1 |> Map.keys |> Set.ofSeq)
                     |> Set.isEmpty)
                    "receiver-copy-blob-retained-missing-overlap"
                let requiredBlobs =
                    plan.ReceiverCopyMappings
                    |> List.collect (fun mapping ->
                        mapping.ReceiverCopyRequiredEntries
                        |> List.choose (fun entry -> if entry.EntryKind = "blob" then Some entry.EntrySha else None))
                    |> Set.ofList
                let completePopulation =
                    Set.union
                        (sizeMap |> Map.keys |> Set.ofSeq)
                        (plan.ReceiverCopyRetainedBlobSha256BySha1 |> Map.keys |> Set.ofSeq)
                require
                    (completePopulation.Count = 8999
                     && requiredBlobs.Count = 8986
                     && Set.isSubset requiredBlobs completePopulation
                     && Set.difference completePopulation requiredBlobs |> Set.count = 13)
                    "receiver-copy-blob-required-population"
                let sourceMap = sources |> Seq.map (fun row -> row.Key, row.Value |> Seq.distinct |> Seq.sort |> List.ofSeq) |> Map.ofSeq
                Ok(plan, { Sizes = sizeMap; Sources = sourceMap })
            with ex -> Error ex.Message)

    let private makeBatches planFingerprint population =
        let result = ResizeArray<MigrationReceiverCopyBlobBatch>()
        let current = ResizeArray<string>()
        let mutable bytes = 0L
        let finish () =
            if current.Count > 0 then
                let ordinal = result.Count
                let objectIds = List.ofSeq current
                let digest = fingerprint (seq { yield "fsgg.receiver-copy-blob-batch/1"; yield planFingerprint; yield string ordinal; yield string bytes; yield! objectIds })
                result.Add { ReceiverCopyBlobPlanFingerprint = planFingerprint; ReceiverCopyBlobBatchOrdinal = ordinal
                             ReceiverCopyBlobSha1s = objectIds; ReceiverCopyBlobExpectedBytes = bytes
                             ReceiverCopyBlobBatchFingerprint = digest }
                current.Clear(); bytes <- 0L
        for KeyValue(objectId, size) in population.Sizes do
            if current.Count = 64 || bytes + size > 8388608L then finish ()
            current.Add objectId; bytes <- bytes + size
        finish (); List.ofSeq result

    let private validateBatch plan population batch =
        let all = makeBatches plan.ReceiverCopyFingerprint population
        if batch.ReceiverCopyBlobBatchOrdinal < 0 || batch.ReceiverCopyBlobBatchOrdinal >= all.Length then Error "receiver-copy-blob-batch-ordinal"
        elif all[batch.ReceiverCopyBlobBatchOrdinal] <> batch then Error "receiver-copy-blob-batch-drift"
        else Ok batch

    let private configure (info: ProcessStartInfo) =
        info.UseShellExecute <- false; info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true; info.RedirectStandardError <- true; info.CreateNoWindow <- true
        info.Environment.Keys |> Seq.cast<string> |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) |> Array.ofSeq
        |> Array.iter (fun key -> info.Environment.Remove key |> ignore)
        info.Environment["GIT_NO_LAZY_FETCH"] <- "1"; info.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"; info.Environment["LC_ALL"] <- "C"

    let private drainBounded (reader: StreamReader) =
        Task.Run(fun () ->
            let retained = StringBuilder()
            let buffer = Array.zeroCreate<char> 4096
            let mutable complete = false
            let mutable truncated = false
            while not complete do
                let count = reader.Read(buffer, 0, buffer.Length)
                if count = 0 then complete <- true
                else
                    let available = 65536 - retained.Length
                    if available > 0 then retained.Append(buffer, 0, min available count) |> ignore
                    if count > available then truncated <- true
            if truncated then retained.Append("...[diagnostics-truncated]") |> ignore
            retained.ToString())

    let internal drainDiagnosticsForTests (bytes: ReadOnlyMemory<byte>) =
        use stream = new MemoryStream(bytes.ToArray(), false)
        use reader = new StreamReader(stream, utf8, false, 4096, false)
        drainBounded reader |> _.GetAwaiter().GetResult()

    let private terminate (gitProcess: Process) =
        let mutable killError: exn option = None
        if not gitProcess.HasExited then
            try gitProcess.Kill(true)
            with ex -> killError <- Some ex
        let exited = gitProcess.WaitForExit(5000)
        gitProcess.Dispose()
        match killError with
        | Some error -> raise (InvalidOperationException("receiver-copy-blob-process-kill", error))
        | None when not exited -> failwith "receiver-copy-blob-process-post-kill-timeout"
        | None -> ()

    let internal runDiagnosticProcessForTests (executable: string) (arguments: string list) timeoutMilliseconds =
        use childProcess = new Process()
        try
            require (timeoutMilliseconds > 0 && timeoutMilliseconds <= 15000) "receiver-copy-blob-test-timeout"
            let info = ProcessStartInfo(executable)
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.CreateNoWindow <- true
            for argument in arguments do info.ArgumentList.Add argument
            childProcess.StartInfo <- info
            require (childProcess.Start()) "receiver-copy-blob-test-process-start"
            let outputTask, errorTask = drainBounded childProcess.StandardOutput, drainBounded childProcess.StandardError
            if not (childProcess.WaitForExit(timeoutMilliseconds)) then
                terminate childProcess
                outputTask.GetAwaiter().GetResult() |> ignore
                let diagnostics = errorTask.GetAwaiter().GetResult()
                Error($"receiver-copy-blob-test-process-timeout:{diagnostics.Length}")
            else
                Ok(outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult())
        with ex -> Error ex.Message

    let private git repository arguments =
        use gitProcess = new Process()
        let info = ProcessStartInfo("/usr/bin/git")
        for argument in seq { yield "--no-replace-objects"; yield "-C"; yield repository; yield! arguments } do info.ArgumentList.Add argument
        configure info
        gitProcess.StartInfo <- info
        require (gitProcess.Start()) "receiver-copy-blob-git-start"
        gitProcess.StandardInput.Close()
        let outputTask, errorTask = drainBounded gitProcess.StandardOutput, drainBounded gitProcess.StandardError
        if not (gitProcess.WaitForExit(15000)) then
            terminate gitProcess
            failwith "receiver-copy-blob-git-timeout"
        let output, error = outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult()
        if gitProcess.ExitCode = 0 then Ok(output.Trim())
        else Error($"receiver-copy-blob-git-exit:{gitProcess.ExitCode}:{error.Trim()}")

    let private repositoryNameFromRemote (remote: string) =
        let httpsPrefix, sshPrefix = "https://github.com/", "git@github.com:"
        let name =
            if remote.StartsWith(httpsPrefix, StringComparison.Ordinal) then remote.Substring(httpsPrefix.Length)
            elif remote.StartsWith(sshPrefix, StringComparison.Ordinal) then remote.Substring(sshPrefix.Length)
            else failwith "receiver-copy-blob-repository-remote"
        let normalized = if name.EndsWith(".git", StringComparison.Ordinal) then name.Substring(0, name.Length - 4) else name
        require (normalized.Split('/').Length = 2) "receiver-copy-blob-repository-remote"
        normalized

    type private BatchReader(repository: string) =
        let gitProcess = new Process()
        let gate = obj()
        let mutable disposed = false
        let mutable diagnostics: Task<string> option = None
        do
            let info = ProcessStartInfo("/usr/bin/git")
            for argument in [ "--no-replace-objects"; "-C"; repository; "cat-file"; "--batch" ] do info.ArgumentList.Add argument
            configure info
            gitProcess.StartInfo <- info
            require (gitProcess.Start()) "receiver-copy-blob-cat-file-start"
            diagnostics <- Some(drainBounded gitProcess.StandardError)
        let readHeader () =
            let bytes = ResizeArray<byte>()
            let mutable finished = false
            while not finished do
                let value = gitProcess.StandardOutput.BaseStream.ReadByte()
                require (value >= 0) "receiver-copy-blob-cat-file-eof"
                if value = int '\n' then finished <- true
                else
                    require (bytes.Count < 256) "receiver-copy-blob-cat-file-header"
                    bytes.Add(byte value)
            utf8.GetString(bytes.ToArray())
        member _.Read(objectId: string, expectedSize: int64) = lock gate (fun () ->
            try
                require (not disposed) "receiver-copy-blob-source-disposed"
                let readTask = Task.Run(fun () ->
                    gitProcess.StandardInput.WriteLine objectId
                    gitProcess.StandardInput.Flush()
                    let header = readHeader ()
                    let fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    require (fields.Length = 3 && fields[0] = objectId && fields[1] = "blob") "receiver-copy-blob-object-type"
                    let size = Int64.Parse(fields[2], Globalization.CultureInfo.InvariantCulture)
                    require (size = expectedSize && size <= 1048576L) "receiver-copy-blob-object-size"
                    let bytes = Array.zeroCreate<byte> (int size)
                    gitProcess.StandardOutput.BaseStream.ReadExactly bytes
                    require (gitProcess.StandardOutput.BaseStream.ReadByte() = int '\n') "receiver-copy-blob-cat-file-delimiter"
                    require (blobSha1 bytes = objectId) "receiver-copy-blob-object-sha1"
                    ReadOnlyMemory<byte>(bytes))
                if not (readTask.Wait(15000)) then
                    try terminate gitProcess
                    finally
                        diagnostics |> Option.iter (fun task -> task.GetAwaiter().GetResult() |> ignore)
                        disposed <- true
                    failwith "receiver-copy-blob-cat-file-timeout"
                Ok(readTask.GetAwaiter().GetResult())
            with ex -> Error ex.Message)
        interface IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    if not disposed then
                        disposed <- true
                        try gitProcess.StandardInput.Close() with _ -> ()
                        if not (gitProcess.WaitForExit(2000)) then terminate gitProcess
                        diagnostics |> Option.iter (fun task -> task.GetAwaiter().GetResult() |> ignore)
                        gitProcess.Dispose())

    type private LocalSource(readers: Map<string, BatchReader>, population: Population) =
        interface IMigrationReceiverCopyLocalGitObjectSource with
            member _.ReadBlob objectId =
                match Map.tryFind objectId population.Sizes, Map.tryFind objectId population.Sources with
                | Some size, Some(receiver :: _) -> readers[receiver].Read(objectId, size)
                | _ -> Error "receiver-copy-blob-not-planned"
            member _.Dispose() = readers |> Map.iter (fun _ reader -> (reader :> IDisposable).Dispose())

    let createLocalGitSource acceptedEvidence runIdentity verifiedCopyPlan repositoryLocations =
        let evidence, run, observed, locations = acceptedEvidence, runIdentity, verifiedCopyPlan, repositoryLocations
        verifyInputs evidence run observed |> Result.bind (fun (plan, population) ->
            let opened = ResizeArray<BatchReader>()
            try
                require (locations |> Map.keys |> Set.ofSeq = (plan.ReceiverCopyMappings |> List.map _.ReceiverCopyId |> Set.ofList)) "receiver-copy-blob-repository-locations"
                let readers =
                    let censusRepositories =
                        plan.ReceiverCopyCensus.Receivers
                        |> List.map (fun (id, repository, revision, tree, _) -> id, (repository, revision, tree))
                        |> Map.ofList
                    [ for mapping in plan.ReceiverCopyMappings do
                        let path = Path.GetFullPath locations[mapping.ReceiverCopyId]
                        require (Directory.Exists path && isNull (DirectoryInfo(path).LinkTarget)) "receiver-copy-blob-repository-missing"
                        let expectedRepository, censusRevision, censusTree = censusRepositories[mapping.ReceiverCopyId]
                        require (censusRevision = mapping.ReceiverCopySourceRevision && censusTree = mapping.ReceiverCopySourceTree) "receiver-copy-blob-census-source-binding"
                        match git path [ "config"; "--get"; "remote.origin.url" ] with
                        | Ok remote -> require (repositoryNameFromRemote remote = expectedRepository) "receiver-copy-blob-repository-identity"
                        | Error error -> failwith error
                        match git path [ "rev-parse"; "--verify"; $"{mapping.ReceiverCopySourceRevision}^{{commit}}" ] with
                        | Ok value -> require (value = mapping.ReceiverCopySourceRevision) "receiver-copy-blob-source-revision"
                        | Error error -> failwith error
                        match git path [ "rev-parse"; "--verify"; $"{mapping.ReceiverCopySourceRevision}^{{tree}}" ] with
                        | Ok value -> require (value = mapping.ReceiverCopySourceTree) "receiver-copy-blob-source-tree"
                        | Error error -> failwith error
                        let reader = new BatchReader(path)
                        opened.Add reader
                        yield mapping.ReceiverCopyId, reader ] |> Map.ofList
                Ok(new LocalSource(readers, population) :> IMigrationReceiverCopyLocalGitObjectSource)
            with ex ->
                opened |> Seq.iter (fun reader -> (reader :> IDisposable).Dispose())
                Error ex.Message)

    let planBatches acceptedEvidence runIdentity verifiedCopyPlan =
        verifyInputs acceptedEvidence runIdentity verifiedCopyPlan
        |> Result.map (fun (plan, population) -> makeBatches plan.ReceiverCopyFingerprint population)

    module private Native =
        [<Literal>]
        let O_RDONLY = 0
        [<Literal>]
        let O_WRONLY = 1
        [<Literal>]
        let O_CREAT = 0x40
        [<Literal>]
        let O_EXCL = 0x80
        [<Literal>]
        let O_DIRECTORY = 0x10000
        [<Literal>]
        let O_NOFOLLOW = 0x20000
        [<Literal>]
        let O_CLOEXEC = 0x80000
        [<Literal>]
        let RENAME_NOREPLACE = 1u
        [<Literal>]
        let EEXIST = 17
        [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
        extern int openNative(string path, int flags, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int openat(int directory, string path, int flags, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int mkdirat(int directory, string path, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int renameat2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint32 flags)
        [<DllImport("libc", SetLastError = true)>]
        extern int unlinkat(int directory, string path, int flags)
        [<DllImport("libc", SetLastError = true)>]
        extern int fsync(int descriptor)
        [<DllImport("libc", SetLastError = true)>]
        extern int fchmod(int descriptor, uint32 mode)
        [<DllImport("libc", SetLastError = true)>]
        extern int fstat(int descriptor, byte[] buffer)
        [<DllImport("libc")>]
        extern uint32 getuid()

    let internal durabilityResultForTests (operation: string) result (errorNumber: int) =
        if result = 0 then Ok()
        else Error($"{operation}:{errorNumber}")
    let private requireDurability operation result =
        match durabilityResultForTests operation result (Marshal.GetLastPInvokeError()) with
        | Ok () -> ()
        | Error error -> failwith error
    let private nativeError code = failwith $"{code}:{Marshal.GetLastPInvokeError()}"
    let private descriptor (handle: SafeFileHandle) = handle.DangerousGetHandle().ToInt32()
    let private ownedHandle fd code =
        if fd < 0 then nativeError code
        new SafeFileHandle(nativeint fd, true)
    let private validateDescriptor expectedType expectedMode code (handle: SafeFileHandle) =
        let stat = Array.zeroCreate<byte> 256
        if Native.fstat(descriptor handle, stat) <> 0 then nativeError $"{code}-stat"
        let mode = BitConverter.ToUInt32(stat, 24)
        let uid = BitConverter.ToUInt32(stat, 28)
        require ((mode &&& 0xF000u) = expectedType && (mode &&& 0x1FFu) = expectedMode && uid = Native.getuid()) code
    let private openDirectoryPath path =
        let handle = ownedHandle (Native.openNative(path, Native.O_RDONLY ||| Native.O_DIRECTORY ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0u)) "receiver-copy-blob-directory-open"
        validateDescriptor 0x4000u 0x1C0u "receiver-copy-blob-artifact-directory-ownership" handle
        handle
    let private openDirectoryAt (parent: SafeFileHandle) name =
        let handle = ownedHandle (Native.openat(descriptor parent, name, Native.O_RDONLY ||| Native.O_DIRECTORY ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0u)) "receiver-copy-blob-directory-open"
        validateDescriptor 0x4000u 0x1C0u "receiver-copy-blob-artifact-directory-ownership" handle
        handle
    let private ensureDirectoryAt (parent: SafeFileHandle) name =
        let result = Native.mkdirat(descriptor parent, name, 0x1C0u)
        let errorNumber = Marshal.GetLastPInvokeError()
        if result <> 0 && errorNumber <> Native.EEXIST then failwith $"receiver-copy-blob-directory-create:{errorNumber}"
        openDirectoryAt parent name, result = 0
    let private splitArtifactPath path =
        let full = Path.GetFullPath path
        let mutable cursor = FileInfo(full).Directory
        let parts = ResizeArray<string>()
        parts.Add(Path.GetFileName full)
        while not (isNull cursor) && not (cursor.Name.StartsWith("gs2-09-7-receiver-blobs-", StringComparison.Ordinal)) do
            parts.Add cursor.Name
            cursor <- cursor.Parent
        require (not (isNull cursor) && cursor.Parent.FullName = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) "receiver-copy-blob-artifact-path"
        cursor.FullName, parts |> Seq.rev |> List.ofSeq
    let private withParentHandle path action =
        let root, parts = splitArtifactPath path
        require (parts.Length >= 1) "receiver-copy-blob-artifact-path"
        let mutable current = openDirectoryPath root
        try
            for part in parts |> List.take (parts.Length - 1) do
                let next = openDirectoryAt current part
                current.Dispose()
                current <- next
            action current (List.last parts)
        finally current.Dispose()
    let private readSecureBounded maximumBytes path =
        withParentHandle path (fun parent name ->
            use handle = ownedHandle (Native.openat(descriptor parent, name, Native.O_RDONLY ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0u)) "receiver-copy-blob-artifact-open"
            validateDescriptor 0x8000u 0x180u "receiver-copy-blob-artifact-ownership" handle
            use stream = new FileStream(handle, FileAccess.Read)
            require (stream.Length >= 0L && stream.Length <= int64 maximumBytes) "receiver-copy-blob-artifact-size-bound"
            let bytes = Array.zeroCreate<byte> (int stream.Length)
            stream.ReadExactly bytes
            require (stream.ReadByte() = -1) "receiver-copy-blob-artifact-concurrent-growth"
            bytes)
    let private readSecure path = readSecureBounded (8 * 1024 * 1024) path
    let private atomicSecure path (bytes: byte array) =
        withParentHandle path (fun parent name ->
            let temporary = ".tmp-" + Guid.NewGuid().ToString("N")
            use handle = ownedHandle (Native.openat(descriptor parent, temporary, Native.O_WRONLY ||| Native.O_CREAT ||| Native.O_EXCL ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0x180u)) "receiver-copy-blob-artifact-create"
            try
                if Native.fchmod(descriptor handle, 0x180u) <> 0 then nativeError "receiver-copy-blob-artifact-chmod"
                use stream = new FileStream(handle, FileAccess.Write)
                stream.Write bytes
                stream.Flush(true)
                requireDurability "receiver-copy-blob-artifact-file-fsync" (Native.fsync(descriptor handle))
                let outcome = Native.renameat2(descriptor parent, temporary, descriptor parent, name, Native.RENAME_NOREPLACE)
                let renameError = Marshal.GetLastPInvokeError()
                if outcome <> 0 && renameError <> Native.EEXIST then failwith $"receiver-copy-blob-artifact-rename:{renameError}"
                if outcome <> 0 then Native.unlinkat(descriptor parent, temporary, 0) |> ignore
            finally
                Native.unlinkat(descriptor parent, temporary, 0) |> ignore
                requireDurability "receiver-copy-blob-artifact-parent-fsync" (Native.fsync(descriptor parent)))

    let private privateFile path =
        readSecure path |> ignore
    let private atomic (path: string) (bytes: byte array) =
        atomicSecure path bytes
    let private rootFor run = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"gs2-09-7-receiver-blobs-{run.RunNonce}"))
    let private prepare run supplied =
        let root = Path.GetFullPath supplied
        require (root = rootFor run) "receiver-copy-blob-artifact-root"
        let temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        use tempHandle = ownedHandle (Native.openNative(temp, Native.O_RDONLY ||| Native.O_DIRECTORY ||| Native.O_NOFOLLOW ||| Native.O_CLOEXEC, 0u)) "receiver-copy-blob-temp-open"
        let rootName = Path.GetFileName root
        let rootResult = Native.mkdirat(descriptor tempHandle, rootName, 0x1C0u)
        let rootError = Marshal.GetLastPInvokeError()
        if rootResult <> 0 && rootError <> Native.EEXIST then failwith $"receiver-copy-blob-root-create:{rootError}"
        let rootCreated = rootResult = 0
        use rootHandle = openDirectoryAt tempHandle rootName
        let objectsHandle, objectsCreated = ensureDirectoryAt rootHandle "objects"
        use objectsHandle = objectsHandle
        let shaHandle, shaCreated = ensureDirectoryAt objectsHandle "sha256"
        use _shaHandle = shaHandle
        let batchesHandle, batchesCreated = ensureDirectoryAt rootHandle "batches"
        use _batchesHandle = batchesHandle
        if rootCreated then requireDurability "receiver-copy-blob-temp-parent-fsync" (Native.fsync(descriptor tempHandle))
        if objectsCreated || batchesCreated then requireDurability "receiver-copy-blob-root-parent-fsync" (Native.fsync(descriptor rootHandle))
        if shaCreated then requireDurability "receiver-copy-blob-objects-parent-fsync" (Native.fsync(descriptor objectsHandle))
        root, Path.Combine(root, "objects", "sha256")
    let private artifactFingerprint (batch: MigrationReceiverCopyBlobBatch) (digests: Map<string, string>) =
        fingerprint (seq {
            yield "fsgg.receiver-copy-blob-batch-artifact/1"
            yield batch.ReceiverCopyBlobPlanFingerprint
            yield string batch.ReceiverCopyBlobBatchOrdinal
            yield batch.ReceiverCopyBlobBatchFingerprint
            for KeyValue(objectId, digest) in digests do
                yield objectId
                yield digest })
    let private batchBytes (batch: MigrationReceiverCopyBlobBatch) (digests: Map<string, string>) (artifactDigest: string) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, jsonOptions)
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.receiver-copy-blob-batch/1")
        writer.WriteString("planFingerprint", batch.ReceiverCopyBlobPlanFingerprint)
        writer.WriteNumber("batchOrdinal", batch.ReceiverCopyBlobBatchOrdinal)
        writer.WriteString("batchFingerprint", batch.ReceiverCopyBlobBatchFingerprint)
        writer.WriteNumber("expectedBytes", batch.ReceiverCopyBlobExpectedBytes)
        writer.WriteString("artifactFingerprint", artifactDigest)
        writer.WriteStartArray("objects")
        for objectId in batch.ReceiverCopyBlobSha1s do
            writer.WriteStartObject()
            writer.WriteString("sha1", objectId)
            writer.WriteString("sha256", digests[objectId])
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()
    let private parseBatch (bytes: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        let root = document.RootElement
        require (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq = Set.ofList [ "schema"; "planFingerprint"; "batchOrdinal"; "batchFingerprint"; "expectedBytes"; "artifactFingerprint"; "objects" ]) "receiver-copy-blob-artifact-shape"
        require (root.GetProperty("schema").GetString() = "fsgg.receiver-copy-blob-batch/1") "receiver-copy-blob-artifact-schema"
        let rows = [ for row in root.GetProperty("objects").EnumerateArray() do
                         require (row.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq = Set.ofList [ "sha1"; "sha256" ]) "receiver-copy-blob-artifact-object-shape"
                         yield row.GetProperty("sha1").GetString(), row.GetProperty("sha256").GetString() ]
        require (rows.Length = (rows |> List.map fst |> Set.ofList |> Set.count)) "receiver-copy-blob-artifact-object-duplicate"
        root.GetProperty("planFingerprint").GetString(), root.GetProperty("batchOrdinal").GetInt32(), root.GetProperty("batchFingerprint").GetString(), root.GetProperty("expectedBytes").GetInt64(), root.GetProperty("artifactFingerprint").GetString(), Map.ofList rows
    let private verifyObject objects objectId size digest =
        let path = Path.Combine(objects, digest + ".blob")
        privateFile path
        let bytes = readSecure path
        require (bytes.LongLength = size) "receiver-copy-blob-retained-size"
        require (sha256 bytes = digest) "receiver-copy-blob-retained-sha256"
        require (blobSha1 bytes = objectId) "receiver-copy-blob-retained-sha1"

    let captureBatch acceptedEvidence runIdentity verifiedCopyPlan (source: IMigrationReceiverCopyLocalGitObjectSource) artifactRoot (batch: MigrationReceiverCopyBlobBatch) =
        let evidence, run, observed = acceptedEvidence, runIdentity, verifiedCopyPlan
        verifyInputs evidence run observed |> Result.bind (fun (plan, population) -> validateBatch plan population batch |> Result.bind (fun _ ->
            try
                let root, objects = prepare run artifactRoot
                let digests =
                    [ for objectId in batch.ReceiverCopyBlobSha1s do
                        match source.ReadBlob objectId with
                        | Error error -> failwith $"receiver-copy-blob-read:{objectId}:{error}"
                        | Ok memory ->
                            let bytes = memory.ToArray()
                            let size = population.Sizes[objectId]
                            require (bytes.LongLength = size) "receiver-copy-blob-read-size"
                            require (blobSha1 bytes = objectId) "receiver-copy-blob-read-sha1"
                            let digest = sha256 bytes
                            let path = Path.Combine(objects, digest + ".blob")
                            if File.Exists path then verifyObject objects objectId size digest
                            else
                                atomic path bytes
                                privateFile path
                                verifyObject objects objectId size digest
                            yield objectId, digest ] |> Map.ofList
                let artifactDigest = artifactFingerprint batch digests
                let bytes = batchBytes batch digests artifactDigest
                let path = Path.Combine(root, "batches", $"{batch.ReceiverCopyBlobBatchOrdinal:D6}.json")
                if File.Exists path then
                    privateFile path
                    require ((readSecure path).AsSpan().SequenceEqual(bytes.AsSpan())) "receiver-copy-blob-batch-resume-drift"
                else atomic path bytes
                privateFile path
                Ok { ReceiverCopyBlobArtifactPlanFingerprint = plan.ReceiverCopyFingerprint; ReceiverCopyBlobArtifactBatchOrdinal = batch.ReceiverCopyBlobBatchOrdinal
                     ReceiverCopyBlobArtifactPath = path; ReceiverCopyBlobArtifactSha256 = sha256 bytes; ReceiverCopyBlobSha256BySha1 = digests; ReceiverCopyBlobArtifactFingerprint = artifactDigest }
            with ex -> Error ex.Message))

    let verifyBatch acceptedEvidence runIdentity verifiedCopyPlan batch artifact =
        let evidence, run, observed = acceptedEvidence, runIdentity, verifiedCopyPlan
        verifyInputs evidence run observed |> Result.bind (fun (plan, population) -> validateBatch plan population batch |> Result.bind (fun _ ->
            try
                let root, objects = prepare run (rootFor run)
                let path = Path.Combine(root, "batches", $"{batch.ReceiverCopyBlobBatchOrdinal:D6}.json")
                require (Path.GetFullPath artifact.ReceiverCopyBlobArtifactPath = path) "receiver-copy-blob-artifact-path"
                privateFile path
                let bytes = readSecure path
                require (sha256 bytes = artifact.ReceiverCopyBlobArtifactSha256) "receiver-copy-blob-artifact-sha256"
                let planDigest, ordinal, batchDigest, byteCount, storedDigest, digests = parseBatch bytes
                require (planDigest = plan.ReceiverCopyFingerprint && ordinal = batch.ReceiverCopyBlobBatchOrdinal && batchDigest = batch.ReceiverCopyBlobBatchFingerprint && byteCount = batch.ReceiverCopyBlobExpectedBytes) "receiver-copy-blob-artifact-binding"
                require (digests |> Map.keys |> List.ofSeq = batch.ReceiverCopyBlobSha1s) "receiver-copy-blob-artifact-population"
                let digest = artifactFingerprint batch digests
                require (storedDigest = digest) "receiver-copy-blob-artifact-fingerprint"
                let expected = { ReceiverCopyBlobArtifactPlanFingerprint = plan.ReceiverCopyFingerprint; ReceiverCopyBlobArtifactBatchOrdinal = ordinal; ReceiverCopyBlobArtifactPath = path
                                 ReceiverCopyBlobArtifactSha256 = sha256 bytes; ReceiverCopyBlobSha256BySha1 = digests; ReceiverCopyBlobArtifactFingerprint = digest }
                require (artifact = expected) "receiver-copy-blob-artifact-record-drift"
                for KeyValue(objectId, objectDigest) in digests do verifyObject objects objectId population.Sizes[objectId] objectDigest
                Ok expected
            with ex -> Error ex.Message))

    let private coverageBytes (planFingerprint: string) (batches: MigrationReceiverCopyBlobBatch list) (digests: Map<string, string>) (coverageDigest: string) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, jsonOptions)
        writer.WriteStartObject()
        writer.WriteString("schema", "fsgg.receiver-copy-blob-coverage/1")
        writer.WriteString("planFingerprint", planFingerprint)
        writer.WriteString("coverageFingerprint", coverageDigest)
        writer.WriteStartArray("batchFingerprints")
        for batch in batches do writer.WriteStringValue batch.ReceiverCopyBlobBatchFingerprint
        writer.WriteEndArray()
        writer.WriteStartArray("objects")
        for KeyValue(objectId, digest) in digests do
            writer.WriteStartObject()
            writer.WriteString("sha1", objectId)
            writer.WriteString("sha256", digest)
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let internal joinCoverageForTests (retained: Map<string, string>) (captured: Map<string, string>) (required: Set<string>) acceptedExternalCount =
        try
            require (acceptedExternalCount >= 0) "receiver-copy-blob-coverage-external-count"
            let retainedKeys, capturedKeys = retained |> Map.keys |> Set.ofSeq, captured |> Map.keys |> Set.ofSeq
            require (Set.intersect retainedKeys capturedKeys |> Set.isEmpty) "receiver-copy-blob-coverage-overlap"
            let combined = Seq.append (retained |> Map.toSeq) (captured |> Map.toSeq) |> Map.ofSeq
            let combinedKeys = combined |> Map.keys |> Set.ofSeq
            require (Set.isSubset required combinedKeys) "receiver-copy-blob-coverage-required-omission"
            require (Set.difference combinedKeys required |> Set.count = acceptedExternalCount) "receiver-copy-blob-coverage-external-population"
            Ok combined
        with ex -> Error ex.Message

    let verifyCoverage acceptedEvidence runIdentity verifiedCopyPlan (batches: MigrationReceiverCopyBlobBatch list) (artifacts: MigrationReceiverCopyBlobBatchArtifact list) =
        let evidence, run, observed = acceptedEvidence, runIdentity, verifiedCopyPlan
        verifyInputs evidence run observed |> Result.bind (fun (plan, population) ->
            try
                require (batches = makeBatches plan.ReceiverCopyFingerprint population) "receiver-copy-blob-coverage-batches"
                require (artifacts.Length = batches.Length) "receiver-copy-blob-coverage-artifact-count"
                let byOrdinal = artifacts |> List.map (fun artifact -> artifact.ReceiverCopyBlobArtifactBatchOrdinal, artifact) |> Map.ofList
                require (byOrdinal.Count = artifacts.Length) "receiver-copy-blob-coverage-artifact-duplicate"
                let verified = [ for batch in batches do match Map.tryFind batch.ReceiverCopyBlobBatchOrdinal byOrdinal with | None -> failwith "receiver-copy-blob-coverage-artifact-missing" | Some artifact -> match verifyBatch evidence run plan batch artifact with | Ok value -> yield value | Error error -> failwith error ]
                let rows = verified |> List.collect (fun artifact -> artifact.ReceiverCopyBlobSha256BySha1 |> Map.toList)
                let missingDigests = Map.ofList rows
                require (rows.Length = population.Sizes.Count && missingDigests.Count = population.Sizes.Count && (missingDigests |> Map.keys |> Set.ofSeq) = (population.Sizes |> Map.keys |> Set.ofSeq)) "receiver-copy-blob-coverage-population"
                let retainedDigests = plan.ReceiverCopyRetainedBlobSha256BySha1
                require (retainedDigests.Count = 460) "receiver-copy-blob-coverage-retained-count"
                let required =
                    plan.ReceiverCopyMappings
                    |> List.collect (fun mapping -> mapping.ReceiverCopyRequiredEntries |> List.choose (fun entry -> if entry.EntryKind = "blob" then Some entry.EntrySha else None))
                    |> Set.ofList
                let digests =
                    match joinCoverageForTests retainedDigests missingDigests required 13 with
                    | Ok value -> value
                    | Error error -> failwith error
                require (digests.Count = 8999) "receiver-copy-blob-coverage-complete-count"
                let fingerprints = batches |> List.map _.ReceiverCopyBlobBatchFingerprint
                let coverageDigest = fingerprint (seq {
                    yield "fsgg.receiver-copy-blob-coverage/1"
                    yield plan.ReceiverCopyFingerprint
                    yield! fingerprints
                    for KeyValue(objectId, digest) in digests do
                        yield objectId
                        yield digest })
                let root, _ = prepare run (rootFor run)
                let path = Path.Combine(root, "coverage.json")
                let bytes = coverageBytes plan.ReceiverCopyFingerprint batches digests coverageDigest
                if File.Exists path then
                    privateFile path
                    require ((readSecure path).AsSpan().SequenceEqual(bytes.AsSpan())) "receiver-copy-blob-coverage-resume-drift"
                else atomic path bytes
                privateFile path
                Ok { ReceiverCopyBlobCoveragePlanFingerprint = plan.ReceiverCopyFingerprint; ReceiverCopyBlobCoverageBatchFingerprints = fingerprints; ReceiverCopyBlobCoverageSha256BySha1 = digests; ReceiverCopyBlobCoverageFingerprint = coverageDigest }
            with ex -> Error ex.Message)

    let createVerifiedCompleteObjectSource acceptedEvidence runIdentity verifiedCopyPlan batches artifacts coverage =
        verifyCoverage acceptedEvidence runIdentity verifiedCopyPlan batches artifacts
        |> Result.bind (fun actualCoverage ->
            if actualCoverage <> coverage then Error "receiver-copy-blob-coverage-drift"
            else
                MigrationReceiverCopyPlan.readRetainedBlobBytes acceptedEvidence runIdentity verifiedCopyPlan
                |> Result.bind (fun retained ->
                    try
                        require (retained.Count = 460 && coverage.ReceiverCopyBlobCoverageSha256BySha1.Count = 8999) "receiver-copy-blob-source-population"
                        let sizes =
                            verifiedCopyPlan.ReceiverCopyMappings
                            |> List.collect (fun mapping -> mapping.ReceiverCopyRequiredEntries)
                            |> List.choose (fun entry -> if entry.EntryKind = "blob" then entry.EntrySize |> Option.map (fun size -> entry.EntrySha, size) else None)
                            |> List.groupBy fst
                            |> List.map (fun (objectId, entries) ->
                                let values = entries |> List.map snd |> Set.ofList
                                require (values.Count = 1) "receiver-copy-blob-source-size-drift"
                                objectId, Set.minElement values)
                            |> Map.ofList
                        let root, objects = prepare runIdentity (rootFor runIdentity)
                        ignore root
                        let mutable disposed = false
                        Ok
                            { new IMigrationReceiverCopyVerifiedObjectSource with
                                member _.ReadBlob(objectId) =
                                    try
                                        require (not disposed) "receiver-copy-blob-source-disposed"
                                        let digest =
                                            coverage.ReceiverCopyBlobCoverageSha256BySha1
                                            |> Map.tryFind objectId
                                            |> Option.defaultWith (fun () -> failwith "receiver-copy-blob-source-uncovered")
                                        let bytes =
                                            match Map.tryFind objectId retained with
                                            | Some source -> Array.copy source
                                            | None -> readSecureBounded 1048576 (Path.Combine(objects, digest + ".blob"))
                                        require (bytes.Length <= 1048576) "receiver-copy-blob-source-size-bound"
                                        match Map.tryFind objectId sizes with
                                        | Some expected -> require (int64 bytes.Length = expected) "receiver-copy-blob-source-size"
                                        | None -> require (retained.ContainsKey objectId) "receiver-copy-blob-source-undeclared"
                                        require (blobSha1 bytes = objectId && sha256 bytes = digest) "receiver-copy-blob-source-byte-drift"
                                        Ok(ReadOnlyMemory<byte>(bytes))
                                    with ex -> Error ex.Message
                                member _.Dispose() =
                                    if not disposed then
                                        disposed <- true
                                        for KeyValue(_, bytes) in retained do Array.Clear bytes }
                    with ex -> Error ex.Message))
