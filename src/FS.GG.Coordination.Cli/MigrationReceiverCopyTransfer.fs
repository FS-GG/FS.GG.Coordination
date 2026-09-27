namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text

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

[<RequireQualifiedAccess>]
module MigrationReceiverCopyTransfer =
    [<Literal>]
    let private target = "https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox.git"
    let private utf8 = UTF8Encoding(false, true)
    let private require condition code = if not condition then failwith code
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
        Directory.CreateDirectory full |> ignore
        git full [ "init"; "--bare"; "--object-format=sha1"; "." ] |> ignore
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
                let message =
                    String.concat "\n"
                        [ "FS.GG receiver copy"
                          ""
                          $"Original-Repository: {sourceRepository}"
                          $"Original-Commit: {sourceCommit}"
                          $"Original-Tree: {boundSourceTree}"
                          $"Copy-Plan-Fingerprint: {planFingerprint}"
                          $"Copy-Run-Nonce: {run.RunNonce}"
                          "Source-Ancestry-Replicated: false"
                          "" ]
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
                derived.Add
                    { ReceiverCopyId = receiverId; SourceRepository = sourceRepository; SourceCommit = sourceCommit
                      SourceTree = boundSourceTree; DerivedRef = desiredRef; DerivedTree = derivedTree
                      DerivedCommit = commit; BlobCount = entries.Length }
            let rows = derived |> Seq.sortBy _.ReceiverCopyId |> List.ofSeq
            require (rows.Length = (rows |> List.map _.DerivedRef |> Set.ofList |> Set.count)) "receiver-copy-transfer-ref-duplicate"
            let digest = fingerprint (seq {
                yield "fsgg.receiver-copy-transfer-manifest/1"; yield target; yield run.RunNonce
                yield planFingerprint; yield coverageFingerprint
                for KeyValue(objectId, digest) in coverageDigests do yield objectId; yield digest
                for row in rows do
                    yield row.ReceiverCopyId; yield row.SourceRepository; yield row.SourceCommit; yield row.SourceTree
                    yield row.DerivedRef; yield row.DerivedTree; yield row.DerivedCommit; yield string row.BlobCount })
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
            yield mapping.ReceiverCopyId, mapping.ReceiverCopyRepository, mapping.ReceiverCopySourceRevision,
                  mapping.ReceiverCopySourceTree, mapping.ReceiverCopyPlannedRef, entries ]

    let prepare acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage objectStoreRoot =
        verifiedInputs acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage
        |> Result.bind (fun (verifiedPlan, verifiedCoverage) ->
            MigrationReceiverCopyBlobCapture.createVerifiedCompleteObjectSource acceptedEvidence runIdentity verifiedPlan batches artifacts verifiedCoverage
            |> Result.bind (fun source ->
                use source = source
                try loadReceivers source verifiedPlan verifiedCoverage |> materialize runIdentity verifiedPlan.ReceiverCopyFingerprint verifiedCoverage.ReceiverCopyBlobCoverageFingerprint verifiedCoverage.ReceiverCopyBlobCoverageSha256BySha1 objectStoreRoot
                with ex -> Error ex.Message))

    let verify acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage observed =
        verifiedInputs acceptedEvidence runIdentity verifiedCopyPlan batches artifacts verifiedCoverage
        |> Result.bind (fun (verifiedPlan, verifiedCoverage) ->
            try
                require (observed.Schema = "fsgg.receiver-copy-transfer-manifest/1" && observed.TargetRepository = target) "receiver-copy-transfer-manifest-shape"
                require (observed.RunIdentity = runIdentity && observed.PlanFingerprint = verifiedPlan.ReceiverCopyFingerprint && observed.BlobCoverageFingerprint = verifiedCoverage.ReceiverCopyBlobCoverageFingerprint) "receiver-copy-transfer-manifest-binding"
                require (observed.BlobSha256BySha1 = verifiedCoverage.ReceiverCopyBlobCoverageSha256BySha1) "receiver-copy-transfer-blob-coverage"
                require (observed.DerivedRefs.Length = 7) "receiver-copy-transfer-receiver-count"
                for row in observed.DerivedRefs do
                    require (git observed.ObjectStorePath [ "rev-parse"; row.DerivedRef ] = row.DerivedCommit) "receiver-copy-transfer-ref-drift"
                    require (git observed.ObjectStorePath [ "rev-parse"; $"{row.DerivedCommit}^{{tree}}" ] = row.DerivedTree) "receiver-copy-transfer-tree-readback"
                    require (git observed.ObjectStorePath [ "rev-list"; "--parents"; "-n"; "1"; row.DerivedCommit ] = row.DerivedCommit) "receiver-copy-transfer-parent-readback"
                let expectedFingerprint = fingerprint (seq {
                    yield observed.Schema; yield target; yield runIdentity.RunNonce; yield observed.PlanFingerprint; yield observed.BlobCoverageFingerprint
                    for KeyValue(objectId, digest) in observed.BlobSha256BySha1 do yield objectId; yield digest
                    for row in observed.DerivedRefs |> List.sortBy _.ReceiverCopyId do
                        yield row.ReceiverCopyId; yield row.SourceRepository; yield row.SourceCommit; yield row.SourceTree
                        yield row.DerivedRef; yield row.DerivedTree; yield row.DerivedCommit; yield string row.BlobCount })
                require (observed.Fingerprint = expectedFingerprint) "receiver-copy-transfer-manifest-fingerprint"
                Ok observed
            with ex -> Error ex.Message)

    let internal prepareSyntheticForTests runIdentity planFingerprint coverageFingerprint objectStoreRoot receivers =
        let digests =
            [ for _, _, _, _, _, entries in receivers do
                for _, _, _, bytes in entries do yield blobSha1 bytes, sha256 bytes ] |> Map.ofList
        materialize runIdentity planFingerprint coverageFingerprint digests objectStoreRoot receivers
