namespace FS.GG.Coordination.Cli

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text

type MigrationReceiverCopyRefUpdate =
    { RefName: string
      ExpectedOldCommit: string option
      NewCommit: string option }

type MigrationReceiverCopyGitReadback =
    { TargetRepository: string
      Refs: Map<string, string>
      UnrelatedRefsFingerprint: string }

type IMigrationReceiverCopyGitTransport =
    abstract SupportsAtomic: bool
    abstract Read: targetRepository:string * refPrefix:string -> Result<MigrationReceiverCopyGitReadback, string>
    abstract PushAtomic: sourceRepository:string * targetRepository:string * updates:MigrationReceiverCopyRefUpdate list -> Result<unit, string>
    abstract VerifyFresh: targetRepository:string * verificationRoot:string * manifest:MigrationReceiverCopyTransferManifest -> Result<MigrationReceiverCopyGitReadback, string>

[<RequireQualifiedAccess>]
module MigrationReceiverCopyGitTransport =
    [<Literal>]
    let private fixedTarget = "https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox.git"
    let private require value code = if not value then failwith code
    let private hex40 (value: string) = not (isNull value) && value.Length = 40 && value |> Seq.forall (fun c -> Char.IsAsciiHexDigit c && not (Char.IsUpper c))
    let private fingerprint values =
        let text = values |> String.concat "\000" |> Encoding.UTF8.GetBytes
        SHA256.HashData text |> Convert.ToHexString |> _.ToLowerInvariant()
    let private sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private blobSha1 (bytes: byte array) =
        let header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\000")
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private configure (info: ProcessStartInfo) =
        info.UseShellExecute <- false; info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true; info.RedirectStandardError <- true; info.CreateNoWindow <- true
        info.Environment.Keys |> Seq.cast<string> |> Seq.filter (fun key -> key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) |> Array.ofSeq
        |> Array.iter (fun key -> info.Environment.Remove key |> ignore)
        info.Environment["LC_ALL"] <- "C"; info.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        info.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"; info.Environment["GIT_NO_LAZY_FETCH"] <- "1"
    let private git repository arguments =
        use child = new Process()
        let info = ProcessStartInfo("/usr/bin/git")
        for argument in seq { yield "--no-replace-objects"; yield "-C"; yield repository; yield! arguments } do info.ArgumentList.Add argument
        configure info; child.StartInfo <- info
        require (child.Start()) "receiver-copy-git-start"; child.StandardInput.Close()
        let output, error = child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()
        require (child.WaitForExit(30000)) "receiver-copy-git-timeout"
        let stdout, stderr = output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()
        if child.ExitCode = 0 then Ok(stdout.Trim()) else Error($"receiver-copy-git-exit:{child.ExitCode}:{stderr.Trim()}")

    let private readAll repository =
            match git repository [ "for-each-ref"; "--format=%(refname) %(objectname)"; "refs/heads" ] with
            | Error error -> Error error
            | Ok output ->
                try
                    Ok([ for line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
                            let fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            require (fields.Length = 2 && fields[0].StartsWith("refs/heads/", StringComparison.Ordinal) && hex40 fields[1]) "receiver-copy-git-ref-shape"
                            yield fields[0], fields[1] ] |> Map.ofList)
                with ex -> Error ex.Message

    let private verifyFreshObjects targetPath verificationRoot (manifest: MigrationReceiverCopyTransferManifest) =
        try
            let root = Path.GetFullPath verificationRoot
            require (not (Directory.Exists root) && not (File.Exists root)) "receiver-copy-git-verification-store-exists"
            let parent = Directory.GetParent root
            require (not (isNull parent) && parent.Exists && isNull parent.LinkTarget) "receiver-copy-git-verification-parent"
            Directory.CreateDirectory root |> ignore
            match git root [ "init"; "--bare"; "--object-format=sha1"; "." ] with | Error error -> failwith error | Ok _ -> ()
            let refspecs = [ for row in manifest.DerivedRefs -> $"+{row.DerivedRef}:{row.DerivedRef}" ]
            match git root ([ "fetch"; "--no-tags"; targetPath ] @ refspecs) with | Error error -> failwith error | Ok _ -> ()
            let refs = readAll root |> function Ok value -> value | Error error -> failwith error
            let expected = manifest.DerivedRefs |> List.map (fun row -> row.DerivedRef, row.DerivedCommit) |> Map.ofList
            require (refs |> Map.filter (fun name _ -> name.StartsWith($"refs/heads/gs2-09-7/{manifest.RunIdentity.RunNonce}/receivers/", StringComparison.Ordinal)) = expected) "receiver-copy-git-verification-refs"
            let blobs = ResizeArray<string>()
            for row in manifest.DerivedRefs do
                let parents = git root [ "rev-list"; "--parents"; "-n"; "1"; row.DerivedCommit ] |> function Ok value -> value | Error error -> failwith error
                require (parents = row.DerivedCommit) "receiver-copy-git-verification-parentless"
                let tree = git root [ "rev-parse"; $"{row.DerivedCommit}^{{tree}}" ] |> function Ok value -> value | Error error -> failwith error
                require (tree = row.DerivedTree) "receiver-copy-git-verification-tree"
                let listing = git root [ "ls-tree"; "-r"; row.DerivedCommit ] |> function Ok value -> value | Error error -> failwith error
                let rows = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                require (rows.Length = row.BlobCount) "receiver-copy-git-verification-blob-count"
                for line in rows do
                    let tab = line.IndexOf('\t')
                    require (tab > 0) "receiver-copy-git-verification-tree-row"
                    let fields = line.Substring(0, tab).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    require (fields.Length = 3 && fields[1] = "blob" && hex40 fields[2]) "receiver-copy-git-verification-tree-row"
                    blobs.Add fields[2]
            let unique = blobs |> Seq.distinct |> Seq.sort |> List.ofSeq
            use child = new Process()
            let info = ProcessStartInfo("/usr/bin/git")
            for argument in [ "--no-replace-objects"; "-C"; root; "cat-file"; "--batch" ] do info.ArgumentList.Add argument
            configure info; child.StartInfo <- info
            require (child.Start()) "receiver-copy-git-verification-cat-file-start"
            let readLine () =
                let bytes = ResizeArray<byte>()
                let mutable doneLine = false
                while not doneLine do
                    let value = child.StandardOutput.BaseStream.ReadByte()
                    require (value >= 0) "receiver-copy-git-verification-cat-file-eof"
                    if value = int '\n' then doneLine <- true else bytes.Add(byte value)
                Encoding.ASCII.GetString(bytes.ToArray())
            for objectId in unique do
                child.StandardInput.WriteLine objectId; child.StandardInput.Flush()
                let fields = readLine().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                require (fields.Length = 3 && fields[0] = objectId && fields[1] = "blob") "receiver-copy-git-verification-object-header"
                let size = Int32.Parse fields[2]
                let bytes = Array.zeroCreate<byte> size
                child.StandardOutput.BaseStream.ReadExactly bytes
                require (child.StandardOutput.BaseStream.ReadByte() = int '\n') "receiver-copy-git-verification-object-delimiter"
                require (blobSha1 bytes = objectId) "receiver-copy-git-verification-object-sha1"
                require (manifest.BlobSha256BySha1 |> Map.tryFind objectId = Some(sha256 bytes)) "receiver-copy-git-verification-object-sha256"
            child.StandardInput.Close()
            require (child.WaitForExit(30000) && child.ExitCode = 0) "receiver-copy-git-verification-cat-file-exit"
            Ok { TargetRepository = fixedTarget; Refs = expected; UnrelatedRefsFingerprint = "" }
        with ex -> Error ex.Message

    type private LocalBare(path: string, supportsAtomic: bool) =
        interface IMigrationReceiverCopyGitTransport with
            member _.SupportsAtomic = supportsAtomic
            member _.Read(targetRepository, refPrefix) =
                try
                    require (targetRepository = fixedTarget) "receiver-copy-git-target"
                    require (refPrefix.StartsWith("refs/heads/gs2-09-7/", StringComparison.Ordinal) && refPrefix.EndsWith("/receivers/", StringComparison.Ordinal)) "receiver-copy-git-prefix"
                    readAll path |> Result.map (fun refs ->
                        let selected = refs |> Map.filter (fun name _ -> name.StartsWith(refPrefix, StringComparison.Ordinal))
                        let unrelated = refs |> Map.filter (fun name _ -> not (name.StartsWith(refPrefix, StringComparison.Ordinal)))
                        { TargetRepository = fixedTarget; Refs = selected
                          UnrelatedRefsFingerprint = fingerprint [ for KeyValue(name, commit) in unrelated do yield name; yield commit ] })
                with ex -> Error ex.Message
            member _.VerifyFresh(targetRepository, verificationRoot, manifest) =
                if targetRepository <> fixedTarget then Error "receiver-copy-git-target"
                else verifyFreshObjects path verificationRoot manifest
            member _.PushAtomic(sourceRepository, targetRepository, updates) =
                try
                    require supportsAtomic "receiver-copy-git-atomic-unsupported"
                    require (targetRepository = fixedTarget) "receiver-copy-git-target"
                    require (Directory.Exists sourceRepository && Directory.Exists path) "receiver-copy-git-repository"
                    require (updates.Length = 7 && updates.Length = (updates |> List.map _.RefName |> Set.ofList |> Set.count)) "receiver-copy-git-update-count"
                    for update in updates do
                        require (update.RefName.StartsWith("refs/heads/gs2-09-7/", StringComparison.Ordinal) && update.RefName.Contains("/receivers/")) "receiver-copy-git-ref"
                        require (update.ExpectedOldCommit |> Option.forall hex40 && update.NewCommit |> Option.forall hex40) "receiver-copy-git-commit"
                    let leases =
                        [ for update in updates ->
                            match update.ExpectedOldCommit with
                            | None -> $"--force-with-lease={update.RefName}:"
                            | Some commit -> $"--force-with-lease={update.RefName}:{commit}" ]
                    let refspecs =
                        [ for update in updates ->
                            match update.NewCommit with
                            | None -> $":{update.RefName}"
                            | Some commit -> $"{commit}:{update.RefName}" ]
                    match git sourceRepository ([ "push"; "--atomic"; "--porcelain" ] @ leases @ [ path ] @ refspecs) with
                    | Ok _ -> Ok()
                    | Error error -> Error error
                with ex -> Error ex.Message

    type private InertHttps() =
        interface IMigrationReceiverCopyGitTransport with
            member _.SupportsAtomic = false
            member _.Read(targetRepository, _) =
                if targetRepository <> fixedTarget then Error "receiver-copy-git-target"
                else Error "receiver-copy-git-protected-host-unavailable"
            member _.PushAtomic(_, targetRepository, _) =
                if targetRepository <> fixedTarget then Error "receiver-copy-git-target"
                else Error "receiver-copy-git-protected-host-unavailable"
            member _.VerifyFresh(targetRepository, _, _) =
                if targetRepository <> fixedTarget then Error "receiver-copy-git-target"
                else Error "receiver-copy-git-protected-host-unavailable"

    let internal localBareWithAtomicSupportForTests targetRepositoryPath supportsAtomic =
        try
            let path = Path.GetFullPath targetRepositoryPath
            require (Directory.Exists path && isNull (DirectoryInfo(path).LinkTarget)) "receiver-copy-git-target-repository"
            match git path [ "rev-parse"; "--is-bare-repository" ] with
            | Ok "true" -> Ok(LocalBare(path, supportsAtomic) :> IMigrationReceiverCopyGitTransport)
            | _ -> Error "receiver-copy-git-target-not-bare"
        with ex -> Error ex.Message

    let localBare targetRepositoryPath = localBareWithAtomicSupportForTests targetRepositoryPath true
    let inertHttps () = InertHttps() :> IMigrationReceiverCopyGitTransport
