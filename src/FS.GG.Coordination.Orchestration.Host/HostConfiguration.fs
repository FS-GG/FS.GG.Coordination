namespace FS.GG.Coordination.Orchestration.Host

open System
open System.IO
open System.Net
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open FS.GG.Coordination.Core.Orchestration

type HostConfiguration =
    { ConnectionString: string
      Token: string
      Prefix: string
      StoreId: string
      BackupIdentity: string
      MinimumGenerationFence: int64
      PermitId: Guid
      PilotPrincipalId: string
      WorkItemId: WorkItemId
      RequestTimeout: TimeSpan
      MaximumConcurrentRequests: int }

[<RequireQualifiedAccess>]
module HostConfiguration =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private Timespec = { Seconds: int64; Nanoseconds: int64 }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private LinuxStat =
        { Device: uint64; Inode: uint64; Links: uint64; Mode: uint32; UserId: uint32; GroupId: uint32
          Padding: int32; SpecialDevice: uint64; Size: int64; BlockSize: int64; Blocks: int64
          Access: Timespec; Modification: Timespec; Change: Timespec
          Reserved0: int64; Reserved1: int64; Reserved2: int64 }

    [<DllImport("libc", EntryPoint = "lstat", SetLastError = true)>]
    extern int private lstat(string path, LinuxStat& value)
    [<DllImport("libc", EntryPoint = "fstat", SetLastError = true)>]
    extern int private fstat(nativeint descriptor, LinuxStat& value)
    [<DllImport("libc", EntryPoint = "geteuid")>]
    extern uint32 private geteuid()

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()

    let private result = ResultBuilder()

    let private value name (arguments: string array) =
        arguments
        |> Array.tryFindIndex ((=) name)
        |> Option.bind (fun index -> Array.tryItem (index + 1) arguments)
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> function Some found -> Ok found | None -> Error $"missing {name}"

    let private validateArguments allowed (arguments: string array) =
        if arguments.Length % 2 <> 0 then Error "options-must-be-name-value-pairs"
        else
            let names = arguments |> Array.indexed |> Array.choose (fun (index, item) -> if index % 2 = 0 then Some item else None)
            if names |> Array.exists (fun name -> not (Set.contains name allowed)) then Error "unknown-option"
            elif Set.count (Set.ofArray names) <> names.Length then Error "duplicate-option"
            else Ok()

    let private privateFile maximumBytes (path: string) =
        if not (OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture <> Architecture.X64 then Error "linux-x64-private-file-contract-required"
        elif not (Path.IsPathFullyQualified path) then Error "secret-file-path-must-be-absolute"
        else
            try
                let mutable before = Unchecked.defaultof<LinuxStat>
                if lstat(path, &before) <> 0 then Error "secret-file-missing"
                elif before.Mode &&& 0xF000u <> 0x8000u then Error "secret-file-must-be-regular"
                elif before.UserId <> geteuid() then Error "secret-file-owner-mismatch"
                elif before.Size <= 0L || before.Size > int64 maximumBytes then Error "secret-file-size-refused"
                else
                    let parent = DirectoryInfo(Path.GetDirectoryName path)
                    let parentMode = File.GetUnixFileMode parent.FullName
                    let parentWritable = parentMode &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)
                    if not (isNull parent.LinkTarget) then Error "secret-parent-must-not-be-link"
                    elif parentWritable <> enum<UnixFileMode> 0 then Error "secret-parent-permissions-too-broad"
                    else
                        let exposed = enum<UnixFileMode>(int before.Mode) &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)
                        if exposed <> enum<UnixFileMode> 0 then Error "secret-file-permissions-too-broad"
                        else
                            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan)
                            let mutable opened = Unchecked.defaultof<LinuxStat>
                            if fstat(stream.SafeFileHandle.DangerousGetHandle(), &opened) <> 0
                               || opened.Device <> before.Device || opened.Inode <> before.Inode
                               || opened.UserId <> before.UserId || opened.Mode <> before.Mode
                               || opened.Size <> before.Size || opened.Size > int64 maximumBytes then Error "secret-file-changed-during-open"
                            else
                                use reader = new StreamReader(stream)
                                let buffer = Array.zeroCreate<char> (maximumBytes + 1)
                                let count = reader.ReadBlock(buffer, 0, buffer.Length)
                                if count > maximumBytes then Error "secret-file-size-refused"
                                else Ok(String(buffer, 0, count).Trim())
            with _ -> Error "secret-file-refused"

    let private loopbackPrefix (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp && uri.AbsolutePath = "/" && uri.UserInfo = "" && uri.Query = "" && uri.Fragment = "" && (match IPAddress.TryParse uri.Host with true, address -> IPAddress.IsLoopback address | _ -> false) -> Ok value
        | true, uri when uri.Scheme = Uri.UriSchemeHttp && uri.AbsolutePath = "/" && uri.UserInfo = "" && uri.Query = "" && uri.Fragment = "" && String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) -> Ok value
        | _ -> Error "prefix-must-be-loopback-http-root"

    let parseServe arguments =
        result {
            do! validateArguments (set [ "--connection-file"; "--token-file"; "--prefix"; "--store-id"; "--backup-identity"; "--minimum-generation-fence"; "--permit-id"; "--pilot-principal"; "--repository-node-id"; "--repository-database-id"; "--issue-node-id"; "--issue-database-id" ]) arguments
            let! connectionPath = value "--connection-file" arguments
            let! tokenPath = value "--token-file" arguments
            let! connection = privateFile 16384 connectionPath
            let! token = privateFile 4096 tokenPath
            do! if String.IsNullOrWhiteSpace connection then Error "connection-string-required" else Ok()
            do! if token.Length < 32 then Error "operator-token-too-short" else Ok()
            let! prefix = value "--prefix" arguments |> Result.bind loopbackPrefix
            let! storeId = value "--store-id" arguments
            let! backupIdentity = value "--backup-identity" arguments
            let! fenceText = value "--minimum-generation-fence" arguments
            let! permitText = value "--permit-id" arguments
            let! principal = value "--pilot-principal" arguments
            let! repositoryNodeId = value "--repository-node-id" arguments
            let! repositoryDatabaseText = value "--repository-database-id" arguments
            let! issueNodeId = value "--issue-node-id" arguments
            let! issueDatabaseText = value "--issue-database-id" arguments
            match Int64.TryParse fenceText, Guid.TryParse backupIdentity, Guid.TryParse permitText,
                  Int64.TryParse repositoryDatabaseText, Int64.TryParse issueDatabaseText with
            | (true, fence), (true, backup), (true, permit), (true, repositoryDatabaseId), (true, issueDatabaseId)
                when fence >= 0L && backup <> Guid.Empty && permit <> Guid.Empty
                     && repositoryDatabaseId > 0L && issueDatabaseId > 0L
                     && principal = principal.Trim() && principal.Length <= 128
                     && repositoryNodeId = repositoryNodeId.Trim() && repositoryNodeId.Length <= 128
                     && issueNodeId = issueNodeId.Trim() && issueNodeId.Length <= 128 ->
                return
                    { ConnectionString = connection; Token = token; Prefix = prefix; StoreId = storeId
                      BackupIdentity = backup.ToString(); MinimumGenerationFence = fence; PermitId = permit
                      PilotPrincipalId = principal
                      WorkItemId = WorkItemIdentity.create repositoryNodeId repositoryDatabaseId issueNodeId issueDatabaseId
                      RequestTimeout = TimeSpan.FromSeconds 5.; MaximumConcurrentRequests = 4 }
            | _ -> return! Error "invalid-fence-backup-permit-or-work-item-identity"
        }

    let parseInit arguments =
        result {
            do! validateArguments (Set.singleton "--connection-file") arguments
            let! connectionPath = value "--connection-file" arguments
            let! connection = privateFile 16384 connectionPath
            do! if String.IsNullOrWhiteSpace connection then Error "connection-string-required" else Ok()
            return connection
        }
