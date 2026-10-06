namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// Observation only. The absent independent image/active-host proof prevents
/// creation of TrustedEnrollment or Admission; no helper/process is launched.
module internal PortableWorkspaceC2Admission =
    type Diagnostic = { Field: string; Code: string }
    type RequestIdentity = PortableWorkspaceC2Phase.RequestIdentity
    exception private Refusal of string * string
    let private refuse field code = raise (Refusal(field, code))
    let private error field code = Error [ { Field = field; Code = code } ]
    let private guard action =
        try Ok(action ()) with
        | Refusal(field, code) -> error field code
        | :? OutOfMemoryException -> reraise ()
        | _ -> error "$" "observation-refused"
    let private utf8 = UTF8Encoding(false, true)
    let private digest (raw: byte array) = SHA256.HashData(raw) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
    let private stateRoot = "/var/lib/fsgg/portable-workspaces/c2/1000"
    let private spool = stateRoot + "/requests"
    let private nsNow () =
        let value = decimal (Stopwatch.GetTimestamp()) * 1000000000M / decimal Stopwatch.Frequency
        if value < 0M || value > decimal Int64.MaxValue then refuse "$clock" "clock-overflow"
        int64 value
    let private expires deadline =
        if nsNow () >= deadline then refuse "$clock" "original-deadline-expired"

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private Timespec = { Seconds: int64; Nanoseconds: int64 }
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private LinuxStat =
        { Device: uint64; Inode: uint64; Links: uint64; Mode: uint32
          UserId: uint32; GroupId: uint32; Padding: int32; SpecialDevice: uint64
          Size: int64; BlockSize: int64; Blocks: int64; Access: Timespec
          Modification: Timespec; Change: Timespec
          Reserved0: int64; Reserved1: int64; Reserved2: int64 }
    type private Physical = { Path: string; Status: LinuxStat; Sha256: string }
    module private Linux =
        [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
        extern int openFile(string path, int flags)
        [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
        extern int openAt(int parent, string name, int flags)
        [<DllImport("libc", EntryPoint = "fstat", SetLastError = true)>]
        extern int fstat(int descriptor, LinuxStat& value)
        [<DllImport("libc", EntryPoint = "read", SetLastError = true)>]
        extern nativeint read(int descriptor, byte[] buffer, unativeint count)
        [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
        extern int close(int descriptor)
        [<DllImport("libc", EntryPoint = "geteuid")>]
        extern uint32 geteuid()
        [<DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)>]
        extern nativeint fgetxattr(int descriptor, string name, byte[] value, unativeint size)
        // RDONLY/CLOEXEC/NOFOLLOW/NONBLOCK; DIRECTORY added for every parent.
        let private flags = 0x80000 ||| 0x20000 ||| 0x800
        let private stat fd =
            let mutable value = Unchecked.defaultof<LinuxStat>
            if fstat(fd, &value) <> 0 then refuse "$physical" "stat-refused"
            value
        let same (a: LinuxStat) (b: LinuxStat) =
            a.Device = b.Device && a.Inode = b.Inode && a.Links = b.Links
            && a.Mode = b.Mode && a.UserId = b.UserId && a.GroupId = b.GroupId
            && a.Size = b.Size && a.Modification = b.Modification && a.Change = b.Change
        let private acl deadline fd name =
            expires deadline
            let raw = Array.zeroCreate<byte> 4096
            let count = int (fgetxattr(fd, name, raw, unativeint raw.Length))
            if count < 0 then
                if Marshal.GetLastPInvokeError() <> 61 then refuse "$physical" "acl-read-refused"
            else
                if count < 4 || (count - 4) % 8 <> 0 || BitConverter.ToUInt32(raw, 0) <> 2u then
                    refuse "$physical" "acl-malformed"
                let mutable index = 4
                while index < count do
                    let tag = BitConverter.ToUInt16(raw, index)
                    let perms = BitConverter.ToUInt16(raw, index + 2)
                    if not (List.contains tag [ 1us; 2us; 4us; 8us; 16us; 32us ])
                       || perms > 7us || (tag <> 1us && perms &&& 2us <> 0us) then
                        refuse "$physical" "acl-write-refused"
                    index <- index + 8
            expires deadline
        let private check deadline isDirectory owner fd =
            expires deadline
            let value = stat fd
            let expected = if isDirectory then 0x4000u else 0x8000u
            if value.Mode &&& 0xF000u <> expected || value.UserId <> owner
               || value.Mode &&& 0x12u <> 0u || (not isDirectory && value.Links <> 1UL)
               || (owner = 1000u && value.Mode &&& 0x3Fu <> 0u) then
                refuse "$physical" "owner-mode-type-refused"
            acl deadline fd "system.posix_acl_access"
            if isDirectory then acl deadline fd "system.posix_acl_default"
            value
        let private canonical (path: string) =
            if isNull path || path.Length = 0 || path.Length > 4096 || not (path.StartsWith("/", StringComparison.Ordinal))
               || Path.GetFullPath(path) <> path || path.Contains('\000')
               || path.Split('/').Length > 32 then refuse "$physical" "path-refused"
        /// Every parent fd stays held through read and final current-path joins.
        /// A descriptor is removed from close authority before close is attempted;
        /// close error refuses, never closes an ambiguous/reused integer again.
        let private held (deadline: int64) (path: string) privateRoot (leafDirectory: bool option) action =
            canonical path
            expires deadline
            let owned = ResizeArray<int>()
            let mutable result = Error [ { Field = "$physical"; Code = "open-refused" } ]
            let mutable firstClose = false
            try
                result <- guard (fun () ->
                    let add fd =
                        if fd < 0 then refuse "$physical" "open-refused"
                        owned.Add fd
                        fd
                    let root = openFile("/", flags ||| 0x10000) |> add
                    let chain = ResizeArray<int * string * int * LinuxStat>()
                    let rootStatus = check deadline true 0u root
                    let parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    let mutable parent = root
                    let mutable prefix = ""
                    for index in 0 .. parts.Length - 1 do
                        expires deadline
                        prefix <- prefix + "/" + parts[index]
                        let needsDirectory = index < parts.Length - 1 || leafDirectory = Some true
                        let owner = if privateRoot && (prefix = stateRoot || prefix.StartsWith(stateRoot + "/", StringComparison.Ordinal)) then 1000u else 0u
                        let fd = openAt(parent, parts[index], flags ||| (if needsDirectory then 0x10000 else 0)) |> add
                        let directory = if index < parts.Length - 1 then true else defaultArg leafDirectory ((stat fd).Mode &&& 0xF000u = 0x4000u)
                        let status = check deadline directory owner fd
                        chain.Add(parent, parts[index], fd, status)
                        parent <- fd
                    let value = action parent (stat parent)
                    if not (same rootStatus (stat root)) then refuse "$physical" "parent-drift"
                    for (parentFd, name, fd, before) in chain do
                        expires deadline
                        if not (same before (stat fd)) then refuse "$physical" "held-object-drift"
                        let current = openAt(parentFd, name, flags ||| (if before.Mode &&& 0xF000u = 0x4000u then 0x10000 else 0)) |> add
                        if not (same before (stat current)) then refuse "$physical" "current-path-drift"
                    expires deadline
                    value)
            finally
                while owned.Count > 0 do
                    let index = owned.Count - 1
                    let fd = owned[index]
                    owned.RemoveAt index
                    try
                        if close fd <> 0 then firstClose <- true
                    with _ -> firstClose <- true
            match result with
            | Error _ -> result
            | Ok _ when firstClose -> error "$physical" "descriptor-close-refused"
            | Ok value -> guard (fun () -> expires deadline; value)
        let readFile deadline path privateRoot maximum =
            held deadline path privateRoot (Some false) (fun fd status ->
                if status.Size < 0L || status.Size > maximum then refuse "$physical" "file-size-refused"
                use buffer = new MemoryStream()
                let chunk = Array.zeroCreate<byte> 16384
                let mutable reading = true
                while reading do
                    expires deadline
                    let allowed = min (int64 chunk.Length) (maximum + 1L - buffer.Length)
                    if allowed <= 0L then refuse "$physical" "file-overflow"
                    let count = int (read(fd, chunk, unativeint allowed))
                    if count < 0 then refuse "$physical" "file-read-refused"
                    elif count = 0 then reading <- false
                    else buffer.Write(chunk, 0, count)
                expires deadline
                if buffer.Length <> status.Size || buffer.Length > maximum then refuse "$physical" "file-changed-or-truncated"
                let raw = buffer.ToArray()
                raw, { Path = path; Status = status; Sha256 = digest raw })
        let directory deadline path privateRoot =
            held deadline path privateRoot (Some true) (fun _ status -> status)
        let kind deadline path privateRoot =
            held deadline path privateRoot None (fun _ status -> status)
        let inventory deadline path privateRoot maximum =
            held deadline path privateRoot (Some true) (fun fd _ ->
                let names = ResizeArray<string>()
                expires deadline
                use iterator = Directory.EnumerateFileSystemEntries($"/proc/self/fd/{fd}").GetEnumerator()
                let mutable reading = true
                while reading do
                    expires deadline
                    if iterator.MoveNext() then
                        if names.Count >= maximum then refuse "$inventory" "inventory-bound"
                        let name = Path.GetFileName(iterator.Current)
                        if String.IsNullOrEmpty(name) || name = "." || name = ".." then refuse "$inventory" "inventory-name-refused"
                        names.Add name
                    else reading <- false
                expires deadline
                names |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) |> Seq.toList)
        let platform () = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture = Architecture.X64

    type private Entry = { Generation: Guid; Uid: uint32; Utc: DateTimeOffset; MonotonicNs: int64; BootId: string; ReadEnd: int64 }
    type EntryContext = private EntryContext of Entry
    type CapturedRequest = private CapturedRequest of Entry * PortableWorkspaceC2Phase.Request * Physical
    type EnrollmentDeclaration = private EnrollmentDeclaration of JsonElement * byte array
    type private Enrolled = { Context: Entry; Declaration: EnrollmentDeclaration; RequestDigest: string; Deadline: int64 }
    type TrustedEnrollment = private TrustedEnrollment of Enrolled
    type CapturedRoles = private CapturedRoles of Entry * string * Physical list
    type Admission = private Admission of Entry * string
    type RetiredHandoff = private RetiredHandoff of unit

    let private field (node: JsonElement) (name: string) = node.GetProperty(name).GetString()
    let private child (node: JsonElement) (name: string) = node.GetProperty(name)
    let private number (node: JsonElement) (name: string) =
        let value = node.GetProperty(name)
        if value.ValueKind <> JsonValueKind.Number then refuse ("$enrollment." + name) "integer-required"
        match value.TryGetInt64() with
        | true, result -> result
        | _ -> refuse ("$enrollment." + name) "integer-required"
    let private exact (names: string list) (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then refuse "$enrollment" "object-required"
        let actual = node.EnumerateObject() |> Seq.map (fun value -> value.Name) |> Seq.toList
        if actual.Length <> names.Length || Set.ofList actual <> Set.ofList names then refuse "$enrollment" "closed-members-required"
    let private text maximum (node: JsonElement) name =
        if (child node name).ValueKind <> JsonValueKind.String then refuse ("$enrollment." + name) "string-required"
        let value = field node name
        if isNull value || value.Length = 0 || value.Length > maximum || (value |> Seq.exists Char.IsControl) then
            refuse ("$enrollment." + name) "bounded-string-required"
        utf8.GetBytes(value) |> ignore
        value
    let private hex length node name =
        let value = text length node name
        if not (Regex.IsMatch(value, $"\\A[0-9a-f]{{%d{length}}}\\z", RegexOptions.CultureInvariant)) then
            refuse ("$enrollment." + name) "canonical-digest-required"
        value
    let private uuid node name =
        let value = text 36 node name
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed.ToString("D") = value -> value
        | _ -> refuse ("$enrollment." + name) "canonical-uuid-required"
    let private utc node name =
        let value = text 40 node name
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, parsed when value.EndsWith("Z", StringComparison.Ordinal) -> parsed
        | _ -> refuse ("$enrollment." + name) "utc-required"
    let private fixedMappings =
        [ "local-c2-dotnet-scaffold-fixture-v1", ("c2-dotnet-scaffold-fixture/1", "FS-GG/FS.GG.SDD", "/etc/fsgg/portable-workspaces/c2-dotnet-scaffold-v1.json")
          "local-c2-dotnet-compiler-fixture-v1", ("c2-dotnet-compiler-fixture/1", "FS-GG/FS.GG.Governance", "/etc/fsgg/portable-workspaces/c2-dotnet-compiler-v1.json") ] |> Map.ofList
    let private validateEnrollment root =
        exact [ "schema"; "enrollmentId"; "grantId"; "fenceGeneration"; "allowedUid"; "profileId"; "profileSha256"; "cli"; "image"; "executables"; "stateRoot"; "operation" ] root
        if text 64 root "schema" <> "fsgg.portable-workspace-c2-enrollment/v1" then refuse "$enrollment.schema" "schema-refused"
        let id = text 512 root "enrollmentId"
        let profile, consumer, _ =
            match Map.tryFind id fixedMappings with
            | Some value -> value
            | None -> refuse "$enrollment.enrollmentId" "enrollment-unknown"
        if text 512 root "profileId" <> profile || number root "allowedUid" <> 1000L || text 4096 root "stateRoot" <> stateRoot then
            refuse "$enrollment" "fixed-profile-uid-root-required"
        text 512 root "grantId" |> ignore
        if number root "fenceGeneration" < 1L then refuse "$enrollment.fenceGeneration" "fence-required"
        hex 64 root "profileSha256" |> ignore
        let cli = child root "cli"
        exact [ "packageId"; "version"; "packageSha256"; "payloadSha256"; "installationRoot"; "entryRelativePath"; "apphost"; "dotnetHost"; "hostClosureSha256"; "installedQualificationSha256" ] cli
        if text 512 cli "packageId" <> "FS.GG.Coordination.Cli" then refuse "$enrollment.cli" "package-required"
        text 512 cli "version" |> ignore
        let package = hex 64 cli "packageSha256"
        let installation = text 4096 cli "installationRoot"
        if installation <> "/opt/fsgg/coordination/c2/installed/" + package then refuse "$enrollment.cli" "installation-package-join"
        for name in [ "payloadSha256"; "hostClosureSha256"; "installedQualificationSha256" ] do hex 64 cli name |> ignore
        let entry = text 512 cli "entryRelativePath"
        if Path.IsPathRooted(entry) || (entry.Split('/') |> Array.exists (fun item -> item = "" || item = "." || item = "..")) then refuse "$enrollment.cli" "entry-path-refused"
        for name in [ "apphost"; "dotnetHost" ] do
            let executable = child cli name
            exact [ "path"; "sha256" ] executable
            let path = text 4096 executable "path"
            if not (Path.IsPathFullyQualified(path)) || Path.GetFullPath(path) <> path then refuse "$enrollment.cli" "host-path-refused"
            if name = "apphost" && not (path.StartsWith(installation + "/", StringComparison.Ordinal)) then refuse "$enrollment.cli" "apphost-installation-join"
            hex 64 executable "sha256" |> ignore
        let image = child root "image"
        exact [ "qualifiedImage"; "archiveSha256"; "manifestDigest"; "configDigest"; "recipeSha256"; "sdk"; "runtime"; "qualificationReceiptSha256" ] image
        text 512 image "qualifiedImage" |> ignore
        for name in [ "archiveSha256"; "manifestDigest"; "configDigest"; "recipeSha256"; "qualificationReceiptSha256" ] do hex 64 image name |> ignore
        if text 512 image "sdk" <> "10.0.401" || text 512 image "runtime" <> "10.0.12" then refuse "$enrollment.image" "fixed-sdk-runtime-required"
        let executables = child root "executables"
        exact [ "git"; "tar"; "podman" ] executables
        for name in [ "git"; "tar"; "podman" ] do
            let executable = child executables name
            exact [ "path"; "sha256" ] executable
            if text 4096 executable "path" <> "/usr/bin/" + name then refuse "$enrollment.executables" "fixed-executable-required"
            hex 64 executable "sha256" |> ignore
        let op = child root "operation"
        exact [ "operationId"; "idempotencyId"; "requestSha256"; "consumerRepository"; "consumerSource"; "inputInventorySha256"; "bootId"; "phaseStartedUtc"; "notAfterUtc"; "phaseStartMonotonicNs"; "workDeadlineMonotonicNs"; "producerDeadlineMonotonicNs"; "phaseDeadlineMonotonicNs" ] op
        uuid op "operationId" |> ignore
        uuid op "bootId" |> ignore
        text 512 op "idempotencyId" |> ignore
        if text 512 op "consumerRepository" <> consumer then refuse "$enrollment.operation" "fixed-consumer-required"
        for name in [ "requestSha256"; "inputInventorySha256" ] do hex 64 op name |> ignore
        hex 40 op "consumerSource" |> ignore
        let started = utc op "phaseStartedUtc"
        let ending = utc op "notAfterUtc"
        if ending - started <> TimeSpan.FromSeconds 60.0 then refuse "$enrollment.operation" "original-phase-window-required"
        let start = number op "phaseStartMonotonicNs"
        let work = number op "workDeadlineMonotonicNs"
        let producer = number op "producerDeadlineMonotonicNs"
        let finish = number op "phaseDeadlineMonotonicNs"
        if start < 0L || work <= start || producer <= work || finish <= producer
           || work - start > 40000000000L || producer - start > 55000000000L || finish - start <> 60000000000L then
            refuse "$enrollment.operation" "original-monotonic-window-required"
    /// Pure closed declaration decoder; success never supplies protected evidence.
    let decodeEnrollmentDeclaration (raw: byte array) =
        guard (fun () ->
            if isNull raw || raw.Length = 0 || raw.Length > 65536 then refuse "$enrollment" "enrollment-size-refused"
            utf8.GetString(raw) |> ignore
            use doc = JsonDocument.Parse(raw, JsonDocumentOptions(MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow))
            validateEnrollment doc.RootElement
            EnrollmentDeclaration(doc.RootElement.Clone(), Array.copy raw))

    let beginEntry () =
        guard (fun () ->
            if not (Linux.platform ()) then refuse "$entry" "linux-x64-required"
            let started = nsNow ()
            if started > Int64.MaxValue - 5000000000L then refuse "$clock" "clock-overflow"
            let utcNow = DateTimeOffset.UtcNow
            let uid = Linux.geteuid ()
            // Fixed bounded boot-domain observation, not a caller boolean/grant.
            let fd = Linux.openFile("/proc/sys/kernel/random/boot_id", 0x80000 ||| 0x20000 ||| 0x800)
            if fd < 0 then refuse "$clock" "boot-read-refused"
            let raw = Array.zeroCreate<byte> 38
            let mutable closed = false
            let boot =
                try
                    let count = int (Linux.read(fd, raw, unativeint raw.Length))
                    if count <> 37 then refuse "$clock" "boot-read-refused"
                    let text = utf8.GetString(raw, 0, count)
                    if text[36] <> '\n' then refuse "$clock" "boot-read-refused"
                    let value = text.Substring(0, 36)
                    match Guid.TryParseExact(value, "D") with
                    | true, parsed when parsed.ToString("D") = value -> value
                    | _ -> refuse "$clock" "boot-read-refused"
                finally
                    closed <- Linux.close fd = 0
            if not closed then refuse "$clock" "descriptor-close-refused"
            expires (started + 5000000000L)
            EntryContext { Generation = Guid.NewGuid(); Uid = uid; Utc = utcNow; MonotonicNs = started; BootId = boot; ReadEnd = started + 5000000000L })
    let private entryOf (EntryContext entry) = entry
    let private sameContext entry captured =
        if entry.Generation <> captured.Generation || entry.Uid <> captured.Uid || entry.BootId <> captured.BootId then refuse "$context" "capture-context-mismatch"
    let readRequest context invocation =
        let entry = entryOf context
        guard (fun () ->
            let path = PortableWorkspaceC2RuntimeCommand.requestPath invocation
            if not (Path.IsPathFullyQualified(path)) || Path.GetFullPath(path) <> path
               || not (path.StartsWith(spool + "/", StringComparison.Ordinal)) then refuse "$request" "request-path-outside-spool"
            if entry.Uid <> 1000u || Linux.geteuid () <> entry.Uid then refuse "$request" "runtime-uid-refused"
            expires entry.ReadEnd
            path)
        |> Result.bind (fun path ->
            Linux.readFile entry.ReadEnd path true 1048576L
            |> Result.bind (fun (raw, physical) ->
                PortableWorkspaceC2Phase.decodeRequest raw
                |> Result.mapError (List.map (fun (value: PortableWorkspaceC2Phase.Diagnostic) -> { Field = value.Field; Code = value.Code }))
                |> Result.bind (fun request -> guard (fun () -> expires entry.ReadEnd; CapturedRequest(entry, request, physical)))))
    let requestIdentity (CapturedRequest(_, request, _)) = PortableWorkspaceC2Phase.describeRequest request
    let requestBytes (CapturedRequest(_, request, _)) = PortableWorkspaceC2Phase.encodeRequest request
    let private rootOf (EnrollmentDeclaration(root, _)) = root
    let private checkOriginalClock (entry: Entry) (root: JsonElement) =
        let op = child root "operation"
        if field op "bootId" <> entry.BootId then refuse "$clock" "boot-domain-mismatch"
        let start = number op "phaseStartMonotonicNs"
        let producer = number op "producerDeadlineMonotonicNs"
        let started = utc op "phaseStartedUtc"
        if entry.MonotonicNs < start || entry.Utc < started then refuse "$clock" "phase-origin-mismatch"
        let monotonicElapsed = decimal (entry.MonotonicNs - start) / 1000000000M
        let utcElapsed = decimal (entry.Utc - started).TotalSeconds
        if abs (monotonicElapsed - utcElapsed) > 1M then refuse "$clock" "clock-domain-uncertain"
        expires producer
        producer
    let private reread (deadline: int64) (physical: Physical) privateRoot maximum =
        Linux.readFile deadline physical.Path privateRoot maximum
        |> Result.bind (fun (_, observed) ->
            if not (Linux.same physical.Status observed.Status) || physical.Sha256 <> observed.Sha256 then error "$physical" "capture-drift"
            else Ok())
    let private getObserved = function
        | Ok value -> value
        | Error (first :: _) -> refuse first.Field first.Code
        | Error [] -> refuse "$physical" "observation-refused"
    let private observeRoles deadline (declaration: PortableWorkspaceC2Phase.RequestReadDeclaration) =
        if declaration.Inputs.Length > 32 then refuse "$roles" "input-count-bound"
        let mutable bytes = 0L
        let rows = ResizeArray<Physical>()
        let roots = ResizeArray<string * string list>()
        let mutable nodes = 0
        let rec walk depth path =
            if depth > 32 then refuse "$roles" "input-depth-bound"
            let names = Linux.inventory deadline path true 1024 |> getObserved
            roots.Add(path, names)
            for name in names do
                nodes <- nodes + 1
                if nodes > 1024 then refuse "$roles" "input-inventory-bound"
                let childPath = path + "/" + name
                let kind = Linux.kind deadline childPath true |> getObserved
                if kind.Mode &&& 0xF000u = 0x4000u then walk (depth + 1) childPath
                else
                    let raw, physical = Linux.readFile deadline childPath true 16777216L |> getObserved
                    if not (Linux.same kind physical.Status) then refuse "$roles" "input-generation-drift"
                    bytes <- bytes + int64 raw.Length
                    if bytes > 16777216L || rows.Count >= 32 then refuse "$roles" "input-bytes-or-count-bound"
                    rows.Add physical
        let inputsRoot = stateRoot + "/inputs"
        let operationRoots =
            declaration.Inputs
            |> List.map (fun row ->
                if String.IsNullOrEmpty(row.RelativePath) || Path.IsPathRooted(row.RelativePath)
                   || not (row.HostPath.EndsWith("/" + row.RelativePath, StringComparison.Ordinal)) then refuse "$roles" "relative-input-join"
                let root = row.HostPath.Substring(0, row.HostPath.Length - row.RelativePath.Length - 1)
                row.Role, root)
            |> List.distinct
        for (role, root) in operationRoots do
            if not (root.StartsWith(inputsRoot + "/", StringComparison.Ordinal)) || Path.GetFullPath(root) <> root then refuse "$roles" "input-root-outside-spool"
            // Fixed operation/role layout is checked by the caller below; the
            // entire role directory must match all declared files, not a subset.
            role |> ignore
            walk 0 root
        let actual = rows |> Seq.map (fun row -> row.Path, row.Status.Size, row.Sha256) |> Set.ofSeq
        let expected = declaration.Inputs |> List.map (fun row -> row.HostPath, row.Bytes, row.Sha256) |> Set.ofList
        if actual.Count <> rows.Count || expected.Count <> declaration.Inputs.Length || actual <> expected then refuse "$roles" "complete-input-inventory-join"
        // Re-observe every directory membership and captured file at the end;
        // a partial traversal or later replacement never supplies a complete row.
        for (path, before) in roots do
            if (Linux.inventory deadline path true 1024 |> getObserved) <> before then refuse "$roles" "input-membership-drift"
        for row in rows do reread deadline row true 16777216L |> getObserved
        expires deadline
        rows |> Seq.toList
    let private observeInstallation deadline root =
        let cli = child root "cli"
        let installation = field cli "installationRoot"
        let files = ResizeArray<string * string * int64>()
        let directories = ResizeArray<string * string list>()
        let physicalFiles = ResizeArray<Physical>()
        let mutable total = 0L
        let mutable nodes = 0
        let rec walk depth path =
            if depth > 32 then refuse "$enrollment.cli" "payload-depth-bound"
            let names = Linux.inventory deadline path false 4096 |> getObserved
            directories.Add(path, names)
            for name in names do
                nodes <- nodes + 1
                if nodes > 4096 then refuse "$enrollment.cli" "payload-node-bound"
                let childPath = path + "/" + name
                let kind = Linux.kind deadline childPath false |> getObserved
                if kind.Mode &&& 0xF000u = 0x4000u then walk (depth + 1) childPath
                else
                    let raw, physical = Linux.readFile deadline childPath false 16777216L |> getObserved
                    if not (Linux.same kind physical.Status) then refuse "$enrollment.cli" "payload-generation-drift"
                    total <- total + int64 raw.Length
                    if total > 134217728L then refuse "$enrollment.cli" "payload-byte-bound"
                    let relative = Path.GetRelativePath(installation, childPath)
                    if relative.Length > 512 || relative.Contains('\n') || relative.Contains('\r') then refuse "$enrollment.cli" "payload-path-refused"
                    files.Add(relative, physical.Sha256, int64 raw.Length)
                    physicalFiles.Add physical
        walk 0 installation
        if files.Count = 0 then refuse "$enrollment.cli" "payload-empty"
        for (path, names) in directories do
            if (Linux.inventory deadline path false 4096 |> getObserved) <> names then refuse "$enrollment.cli" "payload-membership-drift"
        for physical in physicalFiles do reread deadline physical false 16777216L |> getObserved
        let payload =
            files
            |> Seq.sortWith (fun (a, _, _) (b, _, _) -> StringComparer.Ordinal.Compare(a, b))
            |> Seq.map (fun (path, sha, bytes) -> $"{sha} {bytes} {path}\n")
            |> String.concat ""
            |> utf8.GetBytes
            |> digest
        if payload <> field cli "payloadSha256" then refuse "$enrollment.cli" "payload-digest-mismatch"
        for name in [ "apphost"; "dotnetHost" ] do
            let expected = child cli name
            let _, observed = Linux.readFile deadline (field expected "path") false 16777216L |> getObserved
            if observed.Sha256 <> field expected "sha256" then refuse "$enrollment.cli" "host-executable-drift"
        for name in [ "git"; "tar"; "podman" ] do
            let expected = child (child root "executables") name
            let _, observed = Linux.readFile deadline (field expected "path") false 16777216L |> getObserved
            if observed.Sha256 <> field expected "sha256" then refuse "$enrollment.executables" "fixed-executable-drift"
        expires deadline
    let resolveEnrollment context (CapturedRequest(captured, request, physical)) : Result<TrustedEnrollment, Diagnostic list> =
        guard (fun () ->
            let entry = entryOf context
            sameContext entry captured
            expires entry.ReadEnd
            if entry.Uid <> 1000u || Linux.geteuid () <> 1000u then refuse "$enrollment" "runtime-uid-refused"
            let identity = PortableWorkspaceC2Phase.describeRequest request
            let profile, _, path =
                match Map.tryFind identity.EnrollmentId fixedMappings with
                | Some value -> value
                | None -> refuse "$enrollment" "enrollment-unknown"
            let raw, enrollmentPhysical = Linux.readFile entry.ReadEnd path false 65536L |> getObserved
            let declaration = decodeEnrollmentDeclaration raw |> getObserved
            let root = rootOf declaration
            let op = child root "operation"
            if field root "enrollmentId" <> identity.EnrollmentId || field root "profileId" <> profile
               || field root "profileSha256" <> identity.ProfileSha256 || field op "operationId" <> identity.OperationId
               || field op "idempotencyId" <> identity.IdempotencyId || field op "requestSha256" <> identity.RequestSha256 then
                refuse "$enrollment" "request-enrollment-join"
            let reads = PortableWorkspaceC2Phase.describeReads request
            if field op "consumerRepository" <> reads.ConsumerRepository || field op "consumerSource" <> reads.ConsumerSource
               || field op "inputInventorySha256" <> reads.InputInventorySha256
               || field op "phaseStartedUtc" <> reads.Window.PhaseStartedUtc || field op "notAfterUtc" <> reads.Window.NotAfterUtc
               || reads.Window.MaximumPhaseMs <> 60000L || reads.Window.MaximumWorkMs <> 40000L
               || reads.Window.CleanupReserveMs <> 15000L || reads.Window.CallerFinishReserveMs <> 5000L then
                refuse "$enrollment.operation" "request-origin-consumer-join"
            let deadline = checkOriginalClock entry root
            reread deadline physical true 1048576L |> getObserved
            let cli = child root "cli"
            let installation = field cli "installationRoot"
            let entryPath = installation + "/" + field cli "entryRelativePath"
            if Path.GetFullPath(typeof<PortableWorkspaceRuntimeCommandDependencies>.Assembly.Location) <> entryPath then
                refuse "$enrollment.cli" "installed-entry-mismatch"
            observeInstallation deadline root
            let operationId = field op "operationId"
            for input in reads.Inputs do
                let requiredPath = stateRoot + "/inputs/" + operationId + "/" + input.Role + "/" + input.RelativePath
                if input.HostPath <> requiredPath then refuse "$roles" "fixed-operation-input-role-required"
            let owned = reads.OwnedRoots |> List.map snd
            if owned.Length <> (Set.ofList owned).Count then refuse "$roles" "writable-role-alias"
            for (role, path) in reads.OwnedRoots do
                if path <> stateRoot + "/execution/" + operationId + "/" + role then
                    refuse "$roles" "fixed-operation-writable-role-required"
            observeRoles deadline reads |> ignore
            reread deadline enrollmentPhysical false 65536L |> getObserved
            checkOriginalClock entry root |> ignore
            // No current loaded image or active host/search closure observation
            // exists. Declaration hashes never create TrustedEnrollment/Admission.
            refuse "$enrollment.image" "image-availability-unproved")
    let captureRoles context (TrustedEnrollment enrolled) (CapturedRequest(captured, request, _)) : Result<CapturedRoles, Diagnostic list> =
        guard (fun () ->
            let entry = entryOf context
            sameContext entry enrolled.Context
            sameContext entry captured
            expires enrolled.Deadline
            if entry.Uid <> 1000u || Linux.geteuid () <> entry.Uid then refuse "$admission" "runtime-uid-refused"
            if PortableWorkspaceC2Phase.requestSha256 request <> enrolled.RequestDigest then refuse "$roles" "request-enrollment-join"
            // No TrustedEnrollment can currently be obtained; future selected proof
            // must precede role inventory/mount authority, never supplied declarations.
            refuse "$enrollment.image" "image-availability-unproved")
    let admit context (TrustedEnrollment enrolled) (CapturedRequest(captured, request, _)) (CapturedRoles(roleContext, digestValue, _)) : Result<Admission, Diagnostic list> =
        guard (fun () ->
            let entry = entryOf context
            sameContext entry enrolled.Context
            sameContext entry captured
            sameContext entry roleContext
            expires enrolled.Deadline
            if entry.Uid <> 1000u || Linux.geteuid () <> entry.Uid then refuse "$admission" "runtime-uid-refused"
            if PortableWorkspaceC2Phase.requestSha256 request <> enrolled.RequestDigest || digestValue <> enrolled.RequestDigest then
                refuse "$admission" "request-enrollment-join"
            refuse "$enrollment.image" "image-availability-unproved")
