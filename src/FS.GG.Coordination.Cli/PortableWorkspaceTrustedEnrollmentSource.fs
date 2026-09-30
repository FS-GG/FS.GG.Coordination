namespace FS.GG.Coordination.Cli

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open FS.GG.Coordination.Orchestration.Execution

[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FS.GG.Coordination.UnitTests")>]
do ()

type PortableWorkspaceTrustedGrantFile = { Bytes: byte array; EffectiveUserId: uint32 }

type IPortableWorkspaceTrustedGrantReader =
    abstract Read: unit -> Result<PortableWorkspaceTrustedGrantFile, string>

type IPortableWorkspaceTrustedReceiverInspector =
    abstract Validate: PortableWorkspacePythonHelloGrant -> Result<unit, string>

type IPortableWorkspacePrivateRuntimePreparer =
    abstract Prepare: PortableWorkspacePythonHelloGrant -> Result<unit, string>

type internal PortableWorkspaceTrustedPathEvidence =
    {
        IsDirectory: bool
        IsSymbolicLink: bool
        OwnerUserId: uint32
        GroupOrOtherWritable: bool
        AccessAclAdministratorOnlyWritable: bool
        DefaultAclAdministratorOnlyWritable: bool
    }

[<RequireQualifiedAccess>]
module internal PortableWorkspaceTrustedFileSecurity =
    let validate expectDirectory evidence =
        not evidence.IsSymbolicLink
        && evidence.IsDirectory = expectDirectory
        && evidence.OwnerUserId = 0u
        && not evidence.GroupOrOtherWritable
        && evidence.AccessAclAdministratorOnlyWritable
        && (not expectDirectory || evidence.DefaultAclAdministratorOnlyWritable)

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private TrustedTimespec = { Seconds: int64; Nanoseconds: int64 }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private TrustedLinuxStat =
    {
        Device: uint64
        Inode: uint64
        Links: uint64
        Mode: uint32
        UserId: uint32
        GroupId: uint32
        Padding: int32
        SpecialDevice: uint64
        Size: int64
        BlockSize: int64
        Blocks: int64
        Access: TrustedTimespec
        Modification: TrustedTimespec
        Change: TrustedTimespec
        Reserved0: int64
        Reserved1: int64
        Reserved2: int64
    }

[<RequireQualifiedAccess>]
module private TrustedLinuxFile =
    [<Literal>]
    let FixedPath = "/etc/fsgg/portable-workspaces/python-hello-v1.json"
    [<Literal>]
    let MaxBytes = 65536L
    [<Literal>]
    let O_RDONLY = 0
    [<Literal>]
    let O_CLOEXEC = 0x80000
    [<Literal>]
    let O_NOFOLLOW = 0x20000
    [<Literal>]
    let O_DIRECTORY = 0x10000
    [<Literal>]
    let ACL_TYPE_ACCESS = 0x8000
    [<Literal>]
    let ACL_TYPE_DEFAULT = 0x4000
    [<Literal>]
    let ACL_FIRST_ENTRY = 0
    [<Literal>]
    let ACL_NEXT_ENTRY = 1
    [<Literal>]
    let ACL_USER_OBJ = 0x01
    [<Literal>]
    let ACL_WRITE = 0x02

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openFile(string path, int flags)
    [<DllImport("libc", EntryPoint = "fstat", SetLastError = true)>]
    extern int fstat(int descriptor, TrustedLinuxStat& value)
    [<DllImport("libc", EntryPoint = "lstat", SetLastError = true)>]
    extern int lstat(string path, TrustedLinuxStat& value)
    [<DllImport("libc", EntryPoint = "geteuid")>]
    extern uint32 geteuid()
    [<DllImport("libacl.so.1", EntryPoint = "acl_get_fd", SetLastError = true)>]
    extern nativeint acl_get_fd(int descriptor)
    [<DllImport("libacl.so.1", EntryPoint = "acl_get_file", SetLastError = true)>]
    extern nativeint acl_get_file(string path, int aclType)
    [<DllImport("libacl.so.1", EntryPoint = "acl_get_entry", SetLastError = true)>]
    extern int acl_get_entry(nativeint acl, int entryId, nativeint& entry)
    [<DllImport("libacl.so.1", EntryPoint = "acl_get_tag_type", SetLastError = true)>]
    extern int acl_get_tag_type(nativeint entry, int& tagType)
    [<DllImport("libacl.so.1", EntryPoint = "acl_get_permset", SetLastError = true)>]
    extern int acl_get_permset(nativeint entry, nativeint& permset)
    [<DllImport("libacl.so.1", EntryPoint = "acl_get_perm", SetLastError = true)>]
    extern int acl_get_perm(nativeint permset, int permission)
    [<DllImport("libacl.so.1", EntryPoint = "acl_free", SetLastError = true)>]
    extern int acl_free(nativeint value)

    let private aclOnlyAdministratorWritable acl =
        if acl = nativeint 0 then false
        else
            try
                let mutable entry = nativeint 0
                let mutable selector = ACL_FIRST_ENTRY
                let mutable valid = true
                let mutable reading = true
                while valid && reading do
                    match acl_get_entry(acl, selector, &entry) with
                    | 0 -> reading <- false
                    | 1 ->
                        selector <- ACL_NEXT_ENTRY
                        let mutable tag = 0
                        let mutable permissions = nativeint 0
                        if acl_get_tag_type(entry, &tag) <> 0 || acl_get_permset(entry, &permissions) <> 0 then
                            valid <- false
                        elif tag <> ACL_USER_OBJ then
                            let writable = acl_get_perm(permissions, ACL_WRITE)
                            if writable <> 0 then valid <- false
                    | _ -> valid <- false
                valid
            finally
                acl_free acl |> ignore

    let private descriptorTrusted expectDirectory descriptor =
        let mutable status = Unchecked.defaultof<TrustedLinuxStat>
        let expectedType = if expectDirectory then 0x4000u else 0x8000u
        if fstat(descriptor, &status) <> 0 then false, status
        else
            let accessAcl = aclOnlyAdministratorWritable (acl_get_fd descriptor)
            let defaultAcl =
                if expectDirectory then
                    let path = $"/proc/self/fd/%d{descriptor}"
                    let defaultAcl = acl_get_file(path, ACL_TYPE_DEFAULT)
                    if defaultAcl = nativeint 0 then Marshal.GetLastPInvokeError() = 61
                    else aclOnlyAdministratorWritable defaultAcl
                else true
            let evidence =
                {
                    IsDirectory = status.Mode &&& 0xF000u = 0x4000u
                    IsSymbolicLink = false
                    OwnerUserId = status.UserId
                    GroupOrOtherWritable = status.Mode &&& 0x12u <> 0u
                    AccessAclAdministratorOnlyWritable = accessAcl
                    DefaultAclAdministratorOnlyWritable = defaultAcl
                }
            status.Mode &&& 0xF000u = expectedType
            && PortableWorkspaceTrustedFileSecurity.validate expectDirectory evidence, status

    let read () =
        if not (OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture <> Architecture.X64 then
            Error "portable-trusted-grant-linux-x64-required"
        else
            try
                let parts = FixedPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                let mutable current = ""
                let mutable failure = None
                let rootDescriptor = openFile("/", O_RDONLY ||| O_CLOEXEC ||| O_NOFOLLOW ||| O_DIRECTORY)
                if rootDescriptor < 0 then
                    failure <- Some "portable-trusted-grant-parent-refused"
                else
                    use rootHandle = new SafeFileHandle(nativeint rootDescriptor, true)
                    let trusted, _ = descriptorTrusted true rootDescriptor
                    if not trusted then failure <- Some "portable-trusted-grant-parent-refused"
                for index in 0 .. parts.Length - 2 do
                    current <- current + "/" + parts[index]
                    if failure.IsNone then
                        let descriptor = openFile(current, O_RDONLY ||| O_CLOEXEC ||| O_NOFOLLOW ||| O_DIRECTORY)
                        if descriptor < 0 then
                            failure <- Some "portable-trusted-grant-parent-refused"
                        else
                            use handle = new SafeFileHandle(nativeint descriptor, true)
                            let trusted, _ = descriptorTrusted true descriptor
                            if not trusted then failure <- Some "portable-trusted-grant-parent-refused"
                match failure with
                | Some reason -> Error reason
                | None ->
                    let descriptor = openFile(FixedPath, O_RDONLY ||| O_CLOEXEC ||| O_NOFOLLOW)
                    if descriptor < 0 then Error "portable-trusted-grant-missing"
                    else
                        use handle = new SafeFileHandle(nativeint descriptor, true)
                        let trusted, status = descriptorTrusted false descriptor
                        if not trusted then Error "portable-trusted-grant-file-refused"
                        elif status.Size <= 0L || status.Size > MaxBytes then Error "portable-trusted-grant-size-refused"
                        else
                            use stream = new FileStream(handle, FileAccess.Read, 4096, false)
                            use memory = new MemoryStream()
                            stream.CopyTo memory
                            if memory.Length <> status.Size || memory.Length > MaxBytes then
                                Error "portable-trusted-grant-changed-during-read"
                            else
                                Ok { Bytes = memory.ToArray(); EffectiveUserId = geteuid () }
            with _ -> Error "portable-trusted-grant-read-refused"

    let ownedPath expectedDirectory path =
        let mutable status = Unchecked.defaultof<TrustedLinuxStat>
        let expectedType = if expectedDirectory then 0x4000u else 0x8000u
        lstat(path, &status) = 0
        && (status.Mode &&& 0xF000u) = expectedType
        && status.UserId = geteuid()
        && (status.Mode &&& 0x3Fu) = 0u

type LinuxAdministratorPortableWorkspaceGrantReader() =
    interface IPortableWorkspaceTrustedGrantReader with
        member _.Read() = TrustedLinuxFile.read ()

[<RequireQualifiedAccess>]
module private TrustedGrantCodec =
    let private sha = Regex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)
    let private gitSha = Regex("\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)
    let private version = Regex("\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)([-+][A-Za-z0-9.-]+)?\\z", RegexOptions.CultureInvariant)

    let private exact (expected: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then false
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = expected.Length && Set.ofList names = Set.ofList expected

    let private string (name: string) (element: JsonElement) : string option =
        let value = element.GetProperty(name)
        if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None

    let private sha256 (name: string) (element: JsonElement) = string name element |> Option.filter (fun value -> sha.IsMatch(value: string))
    let private commit (name: string) (element: JsonElement) = string name element |> Option.filter (fun value -> gitSha.IsMatch(value: string))
    let private positiveUInt (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)
        match value.TryGetUInt64() with
        | true, number when number > 0UL -> Some number
        | _ -> None

    let private executable (element: JsonElement) : PortableWorkspaceTrustedExecutable option =
        if exact [ "path"; "sha256" ] element then
            match string "path" element, sha256 "sha256" element with
            | Some path, Some digest when Path.IsPathFullyQualified(path: string) -> Some({ Path = path; Sha256 = digest }: PortableWorkspaceTrustedExecutable)
            | _ -> None
        else None

    let parse (bytes: byte array) : Result<PortableWorkspacePythonHelloGrant, string> =
        try
            use document = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow))
            let root = document.RootElement
            let fields =
                [ "schema"; "enrollmentId"; "grantId"; "allowedUid"; "cli"; "provider"; "receiver";
                  "profileSha256"; "workspaceScope"; "workflowRevision"; "fenceGeneration"; "observedAt";
                  "stateRoot"; "executables"; "image" ]
            if not (exact fields root) || string "schema" root <> Some "fsgg.portable-workspace-python-enrollment/v1" then
                Error "portable-trusted-grant-schema-refused"
            else
                let cli, provider, receiver = root.GetProperty("cli"), root.GetProperty("provider"), root.GetProperty("receiver")
                let executables, image = root.GetProperty("executables"), root.GetProperty("image")
                let payload = receiver.GetProperty("projectedPayload")
                let observedText = string "observedAt" root
                let mutable observed = DateTimeOffset.MinValue
                let observedOk =
                    match observedText with
                    | Some value -> DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal, &observed)
                    | None -> false
                let allowed = root.GetProperty("allowedUid")
                let allowedOk, allowedUid = allowed.TryGetUInt32()
                let payloadValues =
                    if payload.ValueKind <> JsonValueKind.Array then None
                    else
                        payload.EnumerateArray()
                        |> Seq.map (fun item ->
                            if exact [ "path"; "sha256" ] item then
                                match string "path" item, sha256 "sha256" item with
                                | Some path, Some digest when path.Length > 0 && path.Length <= 512 && not (Path.IsPathFullyQualified path) && not (path.Split('/') |> Array.contains "..") -> Some { Path = path.Replace('\\', '/'); Sha256 = digest }
                                | _ -> None
                            else None)
                        |> Seq.toList
                        |> fun values -> if values |> List.forall Option.isSome then Some(List.choose id values) else None
                if not (exact [ "version"; "packageSha256"; "payloadSha256" ] cli)
                   || not (exact [ "version"; "packageSha256"; "producerSourceRevision" ] provider)
                   || not (exact [ "repositoryPath"; "commit"; "tree"; "projectedPayload" ] receiver)
                   || not (exact [ "git"; "tar"; "podman" ] executables)
                   || not (exact [ "qualifiedImage"; "archiveSha256"; "manifestDigest"; "configDigest"; "recipeSha256" ] image)
                   || not observedOk || not allowedOk || allowedUid = 0u then
                    Error "portable-trusted-grant-shape-refused"
                else
                    match
                        string "enrollmentId" root, string "grantId" root,
                        string "version" cli |> Option.filter (fun value -> version.IsMatch(value: string)), sha256 "packageSha256" cli, sha256 "payloadSha256" cli,
                        string "version" provider |> Option.filter (fun value -> version.IsMatch(value: string)), sha256 "packageSha256" provider, commit "producerSourceRevision" provider,
                        string "repositoryPath" receiver, commit "commit" receiver, commit "tree" receiver, payloadValues,
                        sha256 "profileSha256" root, string "workspaceScope" root, positiveUInt "workflowRevision" root, positiveUInt "fenceGeneration" root,
                        string "stateRoot" root, executable (executables.GetProperty("git")), executable (executables.GetProperty("tar")), executable (executables.GetProperty("podman")),
                        string "qualifiedImage" image, sha256 "archiveSha256" image, sha256 "manifestDigest" image, sha256 "configDigest" image, sha256 "recipeSha256" image
                    with
                    | Some enrollmentId, Some grantId, Some cliVersion, Some cliPackage, Some cliPayload,
                      Some providerVersion, Some providerPackage, Some producerSource, Some workspaceRoot,
                      Some receiverCommit, Some receiverTree, Some projectedPayload, Some profileSha, Some scope,
                      Some workflow, Some fence, Some stateRoot, Some git, Some tar, Some podman, Some qualifiedImage,
                      Some archive, Some manifest, Some config, Some recipe
                        when grantId.Length <= 128 && scope.Length > 0 && scope.Length <= 256
                             && Path.IsPathFullyQualified workspaceRoot && Path.IsPathFullyQualified stateRoot
                             && not projectedPayload.IsEmpty && projectedPayload.Length <= 4096
                             && (projectedPayload |> List.map _.Path |> Set.ofList |> Set.count) = projectedPayload.Length ->
                        Ok ({
                            EnrollmentId = enrollmentId; GrantId = grantId; AllowedUserId = allowedUid
                            CliVersion = cliVersion; CliPackageSha256 = cliPackage; CliPayloadSha256 = cliPayload
                            ProviderVersion = providerVersion; ProviderPackageSha256 = providerPackage; ProducerSourceRevision = producerSource
                            WorkspaceRoot = workspaceRoot; ReceiverCommit = receiverCommit; ReceiverTree = receiverTree
                            ProjectedPayload = projectedPayload; ProfileSha256 = profileSha; WorkspaceScope = scope
                            WorkflowRevision = workflow; FenceGeneration = fence; ObservedAt = observed; JournalStateRoot = stateRoot
                            Git = git; Tar = tar; Podman = podman; QualifiedImage = qualifiedImage
                            ImageArchiveSha256 = archive; ImageManifestDigest = manifest; ImageConfigDigest = config; ImageRecipeSha256 = recipe
                        }: PortableWorkspacePythonHelloGrant)
                    | _ -> Error "portable-trusted-grant-field-refused"
        with _ -> Error "portable-trusted-grant-json-refused"

type LinuxPortableWorkspaceTrustedReceiverInspector() =
    [<Literal>]
    let GitTimeoutSeconds = 5
    [<Literal>]
    let GitOutputLimit = 4 * 1024 * 1024

    let digestFile path =
        try
            use stream = File.OpenRead path
            Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant() |> Ok
        with _ -> Error "portable-trusted-executable-read-refused"

    let readBounded (stream: Stream) maximumBytes cancellationToken =
        task {
            use memory = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 8192
            let mutable complete = false
            while not complete do
                let! count = stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                if count = 0 then complete <- true
                elif memory.Length + int64 count > int64 maximumBytes then
                    raise (InvalidDataException "portable-trusted-git-output-limit")
                else
                    memory.Write(buffer, 0, count)
            return Encoding.UTF8.GetString(memory.ToArray())
        }

    let runGit executable root arguments =
        try
            let gitDirectory = Path.Combine(root, ".git")
            let gitInfo = DirectoryInfo gitDirectory
            let rootInfo = DirectoryInfo root
            if not rootInfo.Exists || not (isNull rootInfo.LinkTarget)
               || not gitInfo.Exists || not (isNull gitInfo.LinkTarget) then
                Error "portable-trusted-receiver-git-layout-refused"
            else
                let start = ProcessStartInfo(executable, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
                start.Environment.Clear()
                start.Environment.Add("HOME", "/nonexistent")
                start.Environment.Add("PATH", "/usr/bin:/bin")
                start.Environment.Add("GIT_CONFIG_NOSYSTEM", "1")
                start.Environment.Add("GIT_CONFIG_GLOBAL", "/dev/null")
                start.Environment.Add("GIT_ATTR_NOSYSTEM", "1")
                start.Environment.Add("GIT_OPTIONAL_LOCKS", "0")
                start.Environment.Add("GIT_TERMINAL_PROMPT", "0")
                start.ArgumentList.Add "--no-optional-locks"
                start.ArgumentList.Add "-c"
                start.ArgumentList.Add "core.fsmonitor=false"
                start.ArgumentList.Add "-c"
                start.ArgumentList.Add "core.hooksPath=/dev/null"
                start.ArgumentList.Add "-c"
                start.ArgumentList.Add "credential.helper="
                start.ArgumentList.Add "-c"
                start.ArgumentList.Add "protocol.file.allow=never"
                start.ArgumentList.Add "-c"
                start.ArgumentList.Add "submodule.recurse=false"
                start.ArgumentList.Add "-c"
                start.ArgumentList.Add "status.showUntrackedFiles=all"
                start.ArgumentList.Add "--git-dir"
                start.ArgumentList.Add gitDirectory
                start.ArgumentList.Add "--work-tree"
                start.ArgumentList.Add root
                arguments |> List.iter start.ArgumentList.Add
                use child = new Process(StartInfo = start)
                if not (child.Start()) then Error "portable-trusted-receiver-git-refused"
                else
                    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds(float GitTimeoutSeconds))
                    let stdout = readBounded child.StandardOutput.BaseStream GitOutputLimit deadline.Token
                    let stderr = readBounded child.StandardError.BaseStream 65536 deadline.Token
                    let wait = child.WaitForExitAsync(deadline.Token)
                    try
                        Task.WhenAll(stdout :> Task, stderr :> Task, wait).GetAwaiter().GetResult()
                        let output = stdout.GetAwaiter().GetResult()
                        let _ = stderr.GetAwaiter().GetResult()
                        if child.ExitCode = 0 then Ok output else Error "portable-trusted-receiver-git-refused"
                    with _ ->
                        try
                            if not child.HasExited then child.Kill(true)
                            child.WaitForExit(1000) |> ignore
                        with _ -> ()
                        Error "portable-trusted-receiver-git-refused"
        with _ -> Error "portable-trusted-receiver-git-refused"

    let validate (grant: PortableWorkspacePythonHelloGrant) =
        let fixedPaths = grant.Git.Path = "/usr/bin/git" && grant.Tar.Path = "/usr/bin/tar" && grant.Podman.Path = "/usr/bin/podman"
        if not fixedPaths then Error "portable-trusted-executable-path-refused"
        elif grant.WorkspaceRoot = grant.JournalStateRoot || grant.JournalStateRoot.StartsWith(grant.WorkspaceRoot + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
            Error "portable-trusted-state-root-refused"
        else
            let entryAssembly = System.Reflection.Assembly.GetEntryAssembly()
            let entryPath = if isNull entryAssembly then "" else entryAssembly.Location
            let entryVersion =
                if isNull entryAssembly || isNull (entryAssembly.GetName().Version) then ""
                else
                    let value = entryAssembly.GetName().Version
                    $"%d{value.Major}.%d{value.Minor}.%d{value.Build}"
            (if entryVersion <> grant.CliVersion then Error "portable-trusted-cli-version-refused"
             else digestFile entryPath |> Result.bind (fun actual -> if actual = grant.CliPayloadSha256 then Ok() else Error "portable-trusted-cli-payload-refused"))
            |> Result.bind (fun () ->
                ([ grant.Git; grant.Tar; grant.Podman ]: PortableWorkspaceTrustedExecutable list)
                |> List.fold
                    (fun state (executable: PortableWorkspaceTrustedExecutable) ->
                        state
                        |> Result.bind (fun () ->
                            digestFile executable.Path
                            |> Result.bind (fun actual ->
                                if actual = executable.Sha256 then Ok()
                                else Error "portable-trusted-executable-identity-refused")))
                    (Ok()))
            |> Result.bind (fun () -> runGit grant.Git.Path grant.WorkspaceRoot [ "rev-parse"; "HEAD" ] |> Result.bind (fun value -> if value.Trim() = grant.ReceiverCommit then Ok() else Error "portable-trusted-receiver-commit-refused"))
            |> Result.bind (fun () -> runGit grant.Git.Path grant.WorkspaceRoot [ "rev-parse"; "HEAD^{tree}" ] |> Result.bind (fun value -> if value.Trim() = grant.ReceiverTree then Ok() else Error "portable-trusted-receiver-tree-refused"))
            |> Result.bind (fun () -> runGit grant.Git.Path grant.WorkspaceRoot [ "status"; "--porcelain=v1"; "-z" ] |> Result.bind (fun value -> if value.Length = 0 then Ok() else Error "portable-trusted-receiver-dirty-refused"))
            |> Result.bind (fun () ->
                runGit grant.Git.Path grant.WorkspaceRoot [ "ls-files"; "-z" ]
                |> Result.bind (fun listed ->
                    let actualPaths = listed.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                    let expectedPaths = grant.ProjectedPayload |> List.map _.Path
                    if actualPaths <> expectedPaths then Error "portable-trusted-receiver-inventory-refused"
                    else
                        grant.ProjectedPayload
                        |> List.fold (fun state item ->
                            state |> Result.bind (fun () ->
                                let full = Path.GetFullPath(item.Path, grant.WorkspaceRoot)
                                let root = Path.GetFullPath(grant.WorkspaceRoot) + string Path.DirectorySeparatorChar
                                let info = FileInfo full
                                if not (full.StartsWith(root, StringComparison.Ordinal)) || not info.Exists || not (isNull info.LinkTarget) then
                                    Error "portable-trusted-receiver-payload-refused"
                                else
                                    digestFile full |> Result.bind (fun actual -> if actual = item.Sha256 then Ok() else Error "portable-trusted-receiver-payload-refused"))) (Ok())))

    interface IPortableWorkspaceTrustedReceiverInspector with
        member _.Validate grant = validate grant

type LinuxPortableWorkspacePrivateRuntimePreparer() =
    let directoryMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let safePath = Regex("\\A/[A-Za-z0-9._/-]+\\z", RegexOptions.CultureInvariant)

    let ensureDirectory path =
        try
            let existed = Directory.Exists path
            if existed then
                let info = DirectoryInfo path
                if not (isNull info.LinkTarget) then Error "portable-trusted-runtime-layout-refused"
                else
                    File.SetUnixFileMode(path, directoryMode)
                    if TrustedLinuxFile.ownedPath true path then Ok() else Error "portable-trusted-runtime-layout-refused"
            elif File.Exists path then Error "portable-trusted-runtime-layout-refused"
            else
                Directory.CreateDirectory path |> ignore
                File.SetUnixFileMode(path, directoryMode)
                if TrustedLinuxFile.ownedPath true path then Ok() else Error "portable-trusted-runtime-layout-refused"
        with _ -> Error "portable-trusted-runtime-layout-refused"

    let ensureFile path content =
        try
            if File.Exists path then
                let info = FileInfo path
                if not (isNull info.LinkTarget) || not (TrustedLinuxFile.ownedPath false path) then
                    Error "portable-trusted-runtime-layout-refused"
                elif File.ReadAllText(path, Encoding.UTF8) <> content then
                    Error "portable-trusted-runtime-config-refused"
                else Ok()
            elif Directory.Exists path then Error "portable-trusted-runtime-layout-refused"
            else
                let options = FileStreamOptions()
                options.Mode <- FileMode.CreateNew
                options.Access <- FileAccess.Write
                options.Share <- FileShare.None
                options.UnixCreateMode <- fileMode
                use stream = new FileStream(path, options)
                let bytes = Encoding.UTF8.GetBytes content
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)
                if TrustedLinuxFile.ownedPath false path then Ok() else Error "portable-trusted-runtime-layout-refused"
        with _ -> Error "portable-trusted-runtime-layout-refused"

    let prepare (grant: PortableWorkspacePythonHelloGrant) =
        if not (OperatingSystem.IsLinux()) || not (safePath.IsMatch grant.JournalStateRoot) then
            Error "portable-trusted-runtime-layout-refused"
        else
            let layout = PortableWorkspacePythonHelloPolicy.privateRuntimeLayout grant.JournalStateRoot
            let directories =
                [ grant.JournalStateRoot; layout.Root; layout.Home; layout.ConfigRoot
                  Path.GetDirectoryName layout.ContainersConfig; layout.RuntimeRoot; layout.StorageRoot; layout.RunRoot ]
            directories
            |> List.fold (fun state path -> state |> Result.bind (fun () -> ensureDirectory path)) (Ok())
            |> Result.bind (fun () -> ensureFile layout.ContainersConfig "")
            |> Result.bind (fun () ->
                let content =
                    $"[storage]\ndriver = \"vfs\"\ngraphroot = \"%s{layout.StorageRoot}\"\nrunroot = \"%s{layout.RunRoot}\"\n"
                ensureFile layout.StorageConfig content)

    interface IPortableWorkspacePrivateRuntimePreparer with
        member _.Prepare grant = prepare grant

type PortableWorkspaceTrustedEnrollmentSource
    (reader: IPortableWorkspaceTrustedGrantReader,
     inspector: IPortableWorkspaceTrustedReceiverInspector,
     runtimePreparer: IPortableWorkspacePrivateRuntimePreparer) =
    interface IPortableWorkspaceRuntimeEnrollmentSource with
        member _.Resolve enrollmentId =
            if enrollmentId <> PortableWorkspacePythonHelloPolicy.EnrollmentId then
                Error "portable-runtime-enrollment-not-found"
            else
                reader.Read()
                |> Result.bind (fun trusted ->
                    if trusted.EffectiveUserId = 0u then Error "portable-trusted-runtime-root-refused"
                    else
                        TrustedGrantCodec.parse trusted.Bytes
                        |> Result.bind (fun grant ->
                            if grant.AllowedUserId <> trusted.EffectiveUserId then Error "portable-trusted-runtime-uid-refused"
                            else
                                inspector.Validate grant
                                |> Result.bind (fun () -> runtimePreparer.Prepare grant)
                                |> Result.bind (fun () -> PortableWorkspacePythonHelloPolicy.create grant)))

[<RequireQualifiedAccess>]
module PortableWorkspaceTrustedEnrollment =
    let productionDependencies () =
        let source =
            PortableWorkspaceTrustedEnrollmentSource(
                LinuxAdministratorPortableWorkspaceGrantReader(),
                LinuxPortableWorkspaceTrustedReceiverInspector(),
                LinuxPortableWorkspacePrivateRuntimePreparer())
        {
            Enrollments = source
            CreateRunner = fun runtime -> PortableWorkspacePodmanRunner runtime
            Clock = PortableWorkspacePythonHelloPolicy.portableClock
        }
