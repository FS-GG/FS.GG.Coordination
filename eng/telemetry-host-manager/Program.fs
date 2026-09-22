module TelemetryHostManager

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions

let private fail reason = raise (InvalidOperationException reason)
let private sha256 (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
let private hex64 (value: string) = Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
let private sha40 (value: string) = Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

let private options (arguments: string list) =
    let rec collect (current: Map<string, string>) (rest: string list) =
        match rest with
        | [] -> current
        | key :: value :: tail when key.StartsWith("--", StringComparison.Ordinal) && not (current |> Map.containsKey key) ->
            collect (current |> Map.add key value) tail
        | _ -> fail "expected distinct --name value options"
    collect Map.empty arguments

let private required name values =
    match values |> Map.tryFind name with
    | Some value when not (String.IsNullOrWhiteSpace value) -> value
    | _ -> fail ("missing " + name)

let private only allowed values =
    values |> Map.iter (fun key _ -> if not (Set.contains key allowed) then fail ("unknown option " + key))

let private json value = JsonSerializer.Serialize value

let private command (timeout: int) (executable: string) (arguments: string list) =
    let start = ProcessStartInfo(executable)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in arguments do start.ArgumentList.Add argument
    use child = Process.Start start
    if isNull child then fail "command did not start"
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit(timeout)) then
        child.Kill(true)
        fail "command timed out"
    child.ExitCode, output.Result.Trim(), error.Result.Trim()

let private checkedCommand timeout executable arguments =
    let code, output, _ = command timeout executable arguments
    if code <> 0 then fail ("command refused: " + Path.GetFileName executable)
    output

let private verifyHash path expected =
    if not (hex64 expected) then fail "expected SHA-256 must be lowercase hex"
    let actual = File.ReadAllBytes path |> sha256
    if actual <> expected then fail "release archive SHA-256 differs"
    actual

let private safeDirectory (path: string) =
    if not (Path.IsPathFullyQualified path) then fail "absolute directory required"
    let full = Path.GetFullPath path
    if not (Regex.IsMatch(full, "^/[A-Za-z0-9_./-]+$")) then fail "installation root contains unsafe characters"
    let mutable current = DirectoryInfo full
    while not (isNull current) do
        if current.Exists && not (isNull current.LinkTarget) then fail "symlink directory refused"
        current <- current.Parent
    full

let private installEngine values =
    only (Set.ofList [ "--package"; "--sha256"; "--version"; "--root" ]) values
    let package = Path.GetFullPath(required "--package" values)
    let expected = required "--sha256" values
    let version = required "--version" values
    let root = required "--root" values |> safeDirectory
    if not (Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid engine version"
    let digest = verifyHash package expected
    Directory.CreateDirectory root |> ignore
    let destination = Path.Combine(root, version)
    if Directory.Exists destination || File.Exists destination then fail "engine version already exists; inspect it before replacement"
    let staging = Path.Combine(root, ".engine-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory staging |> ignore
    try
        use archive = ZipFile.OpenRead package
        let prefix = "tools/net10.0/any/"
        let mutable count = 0
        let mutable total = 0L
        for entry in archive.Entries do
            if entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && not (entry.FullName.EndsWith("/", StringComparison.Ordinal)) then
                let relative = entry.FullName.Substring(prefix.Length)
                let parts = relative.Split('/')
                if parts |> Array.exists (fun part -> part = "" || part = "." || part = "..") then fail "unsafe package member path"
                if (entry.ExternalAttributes >>> 16) &&& 0xF000 = 0xA000 then fail "package symlink refused"
                count <- count + 1
                total <- total + entry.Length
                if count > 512 || total > 200L * 1024L * 1024L then fail "package extraction bound exceeded"
                let target = Path.Combine(Array.append [| staging |] parts)
                let parent = Path.GetDirectoryName target
                Directory.CreateDirectory parent |> ignore
                entry.ExtractToFile(target, false)
        if count = 0 || not (File.Exists(Path.Combine(staging, "fsgg-coord-engine.dll"))) then fail "engine payload missing"
        let wrapper = Path.Combine(staging, "fsgg-coord-engine")
        File.WriteAllText(wrapper, "#!/bin/sh\nexec /usr/bin/dotnet " + Path.Combine(destination, "fsgg-coord-engine.dll") + " \"$@\"\n")
        for file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories) do
            File.SetUnixFileMode(file, if file = wrapper then UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute else UnixFileMode.UserRead ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
        for directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories) do
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        File.SetUnixFileMode(staging, UnixFileMode.UserRead ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        let observed = checkedCommand 30000 "/usr/bin/dotnet" [ Path.Combine(staging, "fsgg-coord-engine.dll"); "--version" ]
        if observed <> version + ".0" then fail "engine package version readback differs"
        Directory.Move(staging, destination)
        let installed = checkedCommand 30000 (Path.Combine(destination, "fsgg-coord-engine")) [ "--version" ]
        if installed <> observed then fail "installed engine version readback differs"
        printfn "%s" (json {| status = "installed"; version = version; packageSha256 = digest; path = destination |})
    finally
        if Directory.Exists staging then
            for file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories) do File.SetUnixFileMode(file, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            for directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories) do File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            Directory.Delete(staging, true)

let private installManager values =
    only (Set.ofList [ "--root"; "--version" ]) values
    if Environment.UserName <> "root" then fail "root required to install manager"
    let root = required "--root" values |> safeDirectory
    let version = required "--version" values
    if not (Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid manager version"
    let source = AppContext.BaseDirectory
    let names = [ "TelemetryHostManager"; "TelemetryHostManager.dll"; "TelemetryHostManager.deps.json"; "TelemetryHostManager.runtimeconfig.json"; "FSharp.Core.dll" ]
    let destination = Path.Combine(root, version)
    Directory.CreateDirectory root |> ignore
    if Directory.Exists destination || File.Exists destination then fail "manager version already exists; inspect it before replacement"
    let staging = Path.Combine(root, ".manager-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory staging |> ignore
    try
        let receipts =
            names |> List.map (fun name ->
                let original = Path.Combine(source, name)
                let info = FileInfo original
                if not info.Exists || not (isNull info.LinkTarget) then fail "manager source file unsafe"
                let bytes = File.ReadAllBytes original
                let target = Path.Combine(staging, name)
                File.WriteAllBytes(target, bytes)
                File.SetUnixFileMode(target, if name = "TelemetryHostManager" then UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute else UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
                name, sha256 bytes)
        File.SetUnixFileMode(staging, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        Directory.Move(staging, destination)
        printfn "%s" (json {| status = "manager-installed"; version = version; path = destination; files = receipts |})
    finally
        if Directory.Exists staging then Directory.Delete(staging, true)

let private verifyHostRelease values =
    only (Set.ofList [ "--package"; "--manifest"; "--sha256"; "--verifier" ]) values
    let package = required "--package" values |> Path.GetFullPath
    let manifest = required "--manifest" values |> Path.GetFullPath
    let verifier = required "--verifier" values |> Path.GetFullPath
    let digest = verifyHash package (required "--sha256" values)
    let result = checkedCommand 60000 "/usr/bin/python3" [ verifier; "verify"; "--package"; package; "--manifest"; manifest ]
    use document = JsonDocument.Parse result
    if document.RootElement.GetProperty("verified").GetBoolean() <> true || document.RootElement.GetProperty("archiveSha256").GetString() <> digest then
        fail "Host verifier result differs"
    printfn "%s" result

let private verifyEngineManifest manifest expected version packageDigest =
    verifyHash manifest expected |> ignore
    use document = JsonDocument.Parse(File.ReadAllBytes manifest)
    let root = document.RootElement
    let descriptor = root.GetProperty("descriptor")
    if root.GetProperty("schema").GetString() <> "fsgg.release-saga/1"
       || root.GetProperty("state").GetProperty("phase").GetString() <> "promoted"
       || descriptor.GetProperty("channel").GetString() <> "stable"
       || descriptor.GetProperty("version").GetString() <> version then fail "CLI release manifest differs"
    let packages = descriptor.GetProperty("packages").EnumerateArray() |> Seq.toList
    let matches = packages |> List.filter (fun package -> package.GetProperty("id").GetString() = "FS.GG.Coord.Cli")
    if matches.Length <> 1 || matches[0].GetProperty("version").GetString() <> version
       || matches[0].GetProperty("artifact").GetProperty("sha256").GetString() <> packageDigest then fail "CLI package manifest differs"

let private verifyEngineRelease values =
    only (Set.ofList [ "--package"; "--sha256"; "--manifest"; "--manifest-sha256"; "--version" ]) values
    let version = required "--version" values
    let digest = verifyHash (required "--package" values) (required "--sha256" values)
    verifyEngineManifest (required "--manifest" values) (required "--manifest-sha256" values) version digest
    printfn "%s" (json {| status = "verified"; version = version; packageSha256 = digest |})

let private serviceAccount = "fsgg-telemetry-podman"
let private serviceHome = "/var/lib/fs-gg/telemetry-podman"

let private requireServiceAccount () =
    if Environment.UserName <> serviceAccount then fail "run as the telemetry service account"
    if Environment.GetEnvironmentVariable("HOME") <> serviceHome then fail "telemetry service HOME differs"
    Directory.SetCurrentDirectory serviceHome

let private createAccount () =
    if Environment.UserName <> "root" then fail "root required to create service account"
    let code, old, _ = command 10000 "/usr/bin/getent" [ "passwd"; serviceAccount ]
    if code = 0 then
        let parts = old.Split(':')
        if parts.Length < 7 || parts[5] <> serviceHome || parts[6] <> "/usr/bin/nologin" then fail "existing telemetry account differs"
    elif code = 2 then
        checkedCommand 30000 "/usr/sbin/useradd" [ "--system"; "--create-home"; "--add-subids-for-system"; "--home-dir"; serviceHome; "--shell"; "/usr/bin/nologin"; serviceAccount ] |> ignore
    else fail "service account lookup refused"
    for path in [ "/etc/fs-gg/telemetry-host-container"; "/var/lib/fs-gg/telemetry-host-container-schema10"; "/var/lib/fs-gg/telemetry-host-container-restores"; "/var/backups/fs-gg/telemetry-host-container"; serviceHome + "/releases" ] do
        if Directory.Exists path then
            let info = DirectoryInfo path
            if not (isNull info.LinkTarget) then fail "service directory symlink refused"
        checkedCommand 10000 "/usr/bin/install" [ "-d"; "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0700"; path ] |> ignore
    printfn "%s" (json {| status = "account-prepared"; account = serviceAccount; home = serviceHome; unitsEnabled = false |})

let private prepareRootlessRuntime () =
    if Environment.UserName <> "root" then fail "root required to prepare rootless runtime"
    checkedCommand 30000 "/usr/bin/loginctl" [ "enable-linger"; serviceAccount ] |> ignore
    let linger = checkedCommand 10000 "/usr/bin/loginctl" [ "show-user"; serviceAccount; "--property=Linger"; "--value" ]
    if linger <> "yes" then fail "service account lingering was not enabled"
    printfn "%s" (json {| status = "rootless-runtime-prepared"; account = serviceAccount; telemetryUnitsEnabled = false |})

let private stageHostAssets values =
    only (Set.ofList [ "--package"; "--manifest"; "--journal"; "--package-sha256"; "--manifest-sha256"; "--journal-sha256"; "--version"; "--verifier" ]) values
    if Environment.UserName <> "root" then fail "root required to stage Host assets"
    let package = required "--package" values |> Path.GetFullPath
    let manifest = required "--manifest" values |> Path.GetFullPath
    let journal = required "--journal" values |> Path.GetFullPath
    let verifier = required "--verifier" values |> Path.GetFullPath
    let version = required "--version" values
    if not (Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+$")) then fail "invalid Host version"
    let digest = verifyHash package (required "--package-sha256" values)
    verifyHash manifest (required "--manifest-sha256" values) |> ignore
    verifyHash journal (required "--journal-sha256" values) |> ignore
    let report = checkedCommand 60000 "/usr/bin/python3" [ verifier; "verify"; "--package"; package; "--manifest"; manifest ]
    use verified = JsonDocument.Parse report
    if verified.RootElement.GetProperty("verified").GetBoolean() <> true || verified.RootElement.GetProperty("archiveSha256").GetString() <> digest || verified.RootElement.GetProperty("version").GetString() <> version then fail "verified Host release differs"
    let release = Path.Combine(serviceHome, "releases", version)
    if Directory.Exists release || File.Exists release then fail "existing Host release directory requires inspection"
    let staging = Path.Combine(serviceHome, "releases", ".host-" + Guid.NewGuid().ToString("N"))
    checkedCommand 10000 "/usr/bin/install" [ "-d"; "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0700"; staging ] |> ignore
    try
        for source, name, expected in [ package, "FS.GG.Telemetry.Host." + version + ".nupkg", digest; manifest, "manifest.json", required "--manifest-sha256" values; journal, "publication-journal.json", required "--journal-sha256" values ] do
            checkedCommand 10000 "/usr/bin/install" [ "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0600"; source; Path.Combine(staging, name) ] |> ignore
            verifyHash (Path.Combine(staging, name)) expected |> ignore
        Directory.Move(staging, release)
    finally
        if Directory.Exists staging then Directory.Delete(staging, true)
    printfn "%s" (json {| status = "host-assets-staged"; version = version; packageSha256 = digest; path = release; serviceStarted = false |})

let private copyExact (source: string) (destination: string) (mode: UnixFileMode) =
    let sourceInfo = FileInfo source
    if not sourceInfo.Exists || not (isNull sourceInfo.LinkTarget) then fail "installation source is unsafe"
    let parent = Path.GetDirectoryName destination |> safeDirectory
    let relative = Path.GetRelativePath(serviceHome, parent)
    if relative = "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified relative then fail "installation target escaped service home"
    let mutable current = serviceHome
    for segment in relative.Split(Path.DirectorySeparatorChar) do
        current <- Path.Combine(current, segment)
        let directory = DirectoryInfo current
        if directory.Exists && not (isNull directory.LinkTarget) then fail "service directory symlink refused"
        checkedCommand 10000 "/usr/bin/install" [ "-d"; "-o"; serviceAccount; "-g"; serviceAccount; "-m"; "0700"; current ] |> ignore
    let original = File.ReadAllBytes source
    if File.Exists destination then
        let existing = FileInfo destination
        if not (isNull existing.LinkTarget) || File.ReadAllBytes destination <> original then fail "installed source differs"
    else
        let temporary = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"))
        try
            File.WriteAllBytes(temporary, original)
            File.SetUnixFileMode(temporary, mode)
            checkedCommand 10000 "/usr/bin/chown" [ serviceAccount + ":" + serviceAccount; temporary ] |> ignore
            File.Move(temporary, destination)
        finally
            if File.Exists temporary then File.Delete temporary
    File.SetUnixFileMode(destination, mode)
    let installed = FileInfo destination
    let expectedOwner = checkedCommand 10000 "/usr/bin/id" [ "-u"; serviceAccount ] |> int
    if installed.UnixFileMode <> mode || File.GetUnixFileMode(destination) <> mode then fail "installed file mode differs"
    let owner = checkedCommand 10000 "/usr/bin/stat" [ "-c"; "%u"; destination ] |> int
    if owner <> expectedOwner then fail "installed file owner differs"

let private installHostFiles values =
    only (Set.ofList [ "--systemadmin-root"; "--commit" ]) values
    if Environment.UserName <> "root" then fail "root required to install Host files"
    let root = required "--systemadmin-root" values |> safeDirectory
    let expectedCommit = required "--commit" values
    if not (sha40 expectedCommit) then fail "SystemAdmin commit must be a full SHA"
    let gitPrefix = [ "-c"; "safe.directory=" + root; "-C"; root ]
    let observedCommit = checkedCommand 30000 "/usr/bin/git" (gitPrefix @ [ "rev-parse"; "HEAD" ])
    if observedCommit <> expectedCommit then fail "SystemAdmin source commit differs"
    let sourcePaths = [ "Services/telemetry-host-podman"; "Services/telemetry-host" ]
    let clean, _, _ = command 30000 "/usr/bin/git" (gitPrefix @ [ "diff"; "--quiet"; "HEAD"; "--" ] @ sourcePaths)
    if clean <> 0 then fail "reviewed SystemAdmin source paths are dirty"
    let executable = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    let privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let podman = Path.Combine(root, "Services/telemetry-host-podman")
    let host = Path.Combine(root, "Services/telemetry-host")
    let scripts = [ "build-image.sh"; "telemetry-host-podman.sh"; "telemetry_host_podman_backup.py"; "telemetry_host_update.py"; "Containerfile" ]
    let target = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host-podman")
    for name in scripts do copyExact (Path.Combine(podman, name)) (Path.Combine(target, name)) (if name = "Containerfile" then privateFile else executable)
    let hostTarget = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host")
    for name in [ "telemetry_host_release.py"; "telemetry_host_config_backup.py" ] do
        copyExact (Path.Combine(host, name)) (Path.Combine(hostTarget, name)) executable
    copyExact (Path.Combine(podman, "fsgg-telemetry-host-podman.service.example"))
        (Path.Combine(serviceHome, ".config/systemd/user/fsgg-telemetry-host-podman.service")) privateFile
    copyExact (Path.Combine(podman, "fsgg-telemetry-host-update.service.example"))
        (Path.Combine(serviceHome, ".config/systemd/user/fsgg-telemetry-host-update.service")) privateFile
    copyExact (Path.Combine(podman, "fsgg-telemetry-host-update.timer.example"))
        (Path.Combine(serviceHome, ".config/systemd/user/fsgg-telemetry-host-update.timer")) privateFile
    printfn "%s" (json {| status = "host-files-installed"; account = serviceAccount; unitsEnabled = false |})

let private buildHostImage values =
    only (Set.ofList [ "--package"; "--manifest"; "--sha256"; "--runtime-image"; "--image" ]) values
    requireServiceAccount ()
    let package = required "--package" values |> Path.GetFullPath
    let manifest = required "--manifest" values |> Path.GetFullPath
    let digest = required "--sha256" values
    let runtime = required "--runtime-image" values
    let image = required "--image" values
    if not (Regex.IsMatch(runtime, "^[^ @]+@sha256:[0-9a-f]{64}$")) || not (Regex.IsMatch(image, "^[a-z0-9./-]+:[0-9][a-z0-9.-]+$")) then fail "image identity is invalid"
    verifyHash package digest |> ignore
    checkedCommand 900000 "/usr/bin/podman" [ "pull"; runtime ] |> ignore
    let builder = Path.Combine(serviceHome, ".local/libexec/fs-gg/telemetry-host-podman/build-image.sh")
    let output = checkedCommand 900000 builder [ "--package"; package; "--manifest"; manifest; "--archive-sha256"; digest; "--runtime-image"; runtime; "--image"; image ]
    use receipt = JsonDocument.Parse output
    if receipt.RootElement.GetProperty("schema").GetString() <> "fsgg.telemetry.host-container-image/1" then fail "image receipt schema differs"
    printfn "%s" output

let private unitState unit property =
    let code, output, _ = command 10000 "/usr/bin/systemctl" [ "show"; unit; "--property=" + property; "--value" ]
    if code <> 0 then fail ("systemd readback failed: " + unit)
    output

let private enabled unit =
    let code, output, _ = command 10000 "/usr/bin/systemctl" [ "is-enabled"; unit ]
    if code <> 0 && code <> 1 && code <> 4 then fail ("systemd enablement unreadable: " + unit)
    output

let private status () =
    let publisher = "fsgg-telemetry-dashboard-publisher-member-v3"
    let registry = "fsgg-telemetry-member-registry-update"
    let stage = "fsgg-telemetry-dashboard-stage-member-v3"
    let publicCommit = checkedCommand 30000 "/usr/bin/git" [ "ls-remote"; "https://github.com/FS-GG/.github.git"; "refs/heads/telemetry-data" ]
    let fields = publicCommit.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    if fields.Length <> 2 || not (sha40 fields[0]) || fields[1] <> "refs/heads/telemetry-data" then fail "public ref readback malformed"
    printfn "%s" (json {| schema = "fsgg.telemetry.host-manager-status/1"; observedAt = DateTimeOffset.UtcNow.ToString("O"); publicCommit = fields[0]; publisherTimer = unitState (publisher + ".timer") "ActiveState"; publisherEnabled = enabled (publisher + ".timer"); publisherService = unitState (publisher + ".service") "ActiveState"; registryTimer = unitState (registry + ".timer") "ActiveState"; stageTimer = unitState (stage + ".timer") "ActiveState" |})

let private guard values =
    only (Set.ofList [ "--host-id" ]) values
    let hostId = required "--host-id" values
    if not (Regex.IsMatch(hostId, "^[a-z][a-z0-9-]{0,39}$")) then fail "invalid host id"
    let main = checkedCommand 30000 "/usr/bin/git" [ "ls-remote"; "https://github.com/FS-GG/.github.git"; "refs/heads/main" ]
    let fields = main.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    if fields.Length <> 2 || not (sha40 fields[0]) || fields[1] <> "refs/heads/main" then fail "protected main readback malformed"
    let uri = "https://raw.githubusercontent.com/FS-GG/.github/" + fields[0] + "/telemetry-dashboard/active-publisher.json"
    use client = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
    let raw = client.GetByteArrayAsync(uri).GetAwaiter().GetResult()
    if raw.Length > 4096 then fail "publisher selector too large"
    use document = JsonDocument.Parse raw
    let root = document.RootElement
    let keys = root.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq
    if keys <> Set.ofList [ "schema"; "generation"; "activeHost" ] || root.GetProperty("schema").GetString() <> "fsgg.telemetry.active-publisher/1" then fail "publisher selector schema differs"
    let generation = root.GetProperty("generation").GetInt32()
    let selected = root.GetProperty("activeHost").GetString()
    if generation < 1 || isNull selected || not (Regex.IsMatch(selected, "^[a-z][a-z0-9-]{0,39}$")) then fail "publisher selector invalid"
    if selected = hostId then
        printfn "%s" (json {| status = "selected"; host = hostId; generation = generation; mainCommit = fields[0] |})
        0
    else
        eprintfn "publisher not selected for this host"
        1

let private installGuardDropins values =
    only (Set.ofList [ "--host-id"; "--manager" ]) values
    if Environment.UserName <> "root" then fail "root required to install publisher guards"
    let hostId = required "--host-id" values
    let manager = required "--manager" values
    if not (Regex.IsMatch(hostId, "^[a-z][a-z0-9-]{0,39}$")) then fail "invalid host id"
    if not (Regex.IsMatch(manager, "^/[A-Za-z0-9_./-]+$")) || not (File.Exists manager) || not (isNull (FileInfo(manager).LinkTarget)) then fail "unsafe manager executable"
    // A standby must receive the guard before it can be selected. Both a
    // selected and an unselected host can install it; malformed control fails.
    guard (Map.ofList [ "--host-id", hostId ]) |> ignore
    let units = [ "fsgg-telemetry-dashboard-publisher-member-v3.service"; "fsgg-telemetry-member-registry-update.service" ]
    let content = "[Service]\nExecCondition=" + manager + " guard --host-id " + hostId + "\n"
    // Refuse conflicting drop-ins before writing either unit.
    for unit in units do
        let target = Path.Combine("/etc/systemd/system", unit + ".d", "active-publisher.conf")
        if File.Exists target && File.ReadAllText target <> content then fail "existing publisher guard differs"
    for unit in units do
        let directory = Path.Combine("/etc/systemd/system", unit + ".d")
        Directory.CreateDirectory directory |> ignore
        File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
        let target = Path.Combine(directory, "active-publisher.conf")
        if not (File.Exists target) then
            let temporary = Path.Combine(directory, ".guard-" + Guid.NewGuid().ToString("N"))
            try
                File.WriteAllText(temporary, content)
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead)
                File.Move(temporary, target)
            finally
                if File.Exists temporary then File.Delete temporary
    checkedCommand 10000 "/usr/bin/systemctl" [ "daemon-reload" ] |> ignore
    for unit in units do
        let loaded = unitState unit "ExecCondition"
        if not (loaded.Contains(manager, StringComparison.Ordinal) && loaded.Contains(hostId, StringComparison.Ordinal)) then fail "installed publisher guard was not loaded"
    printfn "%s" (json {| status = "guards-installed"; host = hostId; units = units; servicesStarted = false |})

let private installLegacyWriterGuard values =
    only (Set.ofList [ "--host-id"; "--root"; "--version" ]) values
    if Environment.UserName <> "root" then fail "root required to install legacy writer guard"
    let hostId = required "--host-id" values
    // The currently selected writer must remain selected throughout this
    // initial installation. A missing or changed selector refuses the batch.
    if guard (Map.ofList [ "--host-id", hostId ]) <> 0 then fail "legacy writer is not selected"
    let root = required "--root" values |> safeDirectory
    let version = required "--version" values
    installManager (Map.ofList [ "--root", root; "--version", version ])
    let manager = Path.Combine(root, version, "TelemetryHostManager")
    installGuardDropins (Map.ofList [ "--host-id", hostId; "--manager", manager ])
    if guard (Map.ofList [ "--host-id", hostId ]) <> 0 then fail "legacy writer selector changed during installation"
    printfn "%s" (json {| status = "legacy-writer-guard-installed"; host = hostId; manager = manager; servicesStarted = false |})

let private runHostUpdater values =
    only (Set.ofList [ "--updater"; "--config" ]) values
    requireServiceAccount ()
    let updater = required "--updater" values |> Path.GetFullPath
    let config = required "--config" values |> Path.GetFullPath
    let output = checkedCommand 900000 "/usr/bin/python3" [ updater; "--config"; config ]
    printfn "%s" output

let private backup values =
    only (Set.ofList [ "--operator"; "--deployment"; "--backup-id"; "--host-unit" ]) values
    let name = required "--backup-id" values
    if not (Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,79}$")) then fail "invalid backup id"
    let unit = required "--host-unit" values
    requireServiceAccount ()
    let state = checkedCommand 10000 "/usr/bin/systemctl" [ "--user"; "show"; unit; "--property=ActiveState"; "--value" ]
    if state <> "inactive" then fail "Host service must be stopped before backup"
    let operatorPath = required "--operator" values |> Path.GetFullPath
    let deployment = required "--deployment" values |> Path.GetFullPath
    let output = checkedCommand 300000 operatorPath [ deployment; "backup"; name ]
    printfn "%s" output

let private prepareInert values =
    if Environment.UserName <> "root" then fail "root required to prepare inert Host"
    let allowed = Set.ofList [ "--systemadmin-root"; "--systemadmin-commit"; "--package"; "--manifest"; "--journal"; "--package-sha256"; "--manifest-sha256"; "--journal-sha256"; "--version"; "--engine-package"; "--engine-sha256"; "--engine-manifest"; "--engine-manifest-sha256"; "--engine-version"; "--engine-root" ]
    only allowed values
    let root = required "--systemadmin-root" values |> safeDirectory
    let verifier = Path.Combine(root, "Services/telemetry-host/telemetry_host_release.py")
    let assetKeys = Set.ofList [ "--package"; "--manifest"; "--journal"; "--package-sha256"; "--manifest-sha256"; "--journal-sha256"; "--version" ]
    let assetValues = values |> Map.filter (fun key _ -> Set.contains key assetKeys) |> Map.add "--verifier" verifier
    let engineValues = Map.ofList [ "--package", required "--engine-package" values; "--sha256", required "--engine-sha256" values; "--version", required "--engine-version" values; "--root", required "--engine-root" values ]
    verifyEngineManifest (required "--engine-manifest" values) (required "--engine-manifest-sha256" values) (required "--engine-version" values) (required "--engine-sha256" values)
    installEngine engineValues
    createAccount ()
    installHostFiles (Map.ofList [ "--systemadmin-root", root; "--commit", required "--systemadmin-commit" values ])
    stageHostAssets assetValues
    printfn "%s" (json {| status = "inert-host-prepared"; account = serviceAccount; servicesStarted = false; unitsEnabled = false |})

[<EntryPoint>]
let main arguments =
    try
        match Array.toList arguments with
        | "status" :: [] -> status (); 0
        | "guard" :: rest -> guard (options rest)
        | "install-guard-dropins" :: rest -> installGuardDropins (options rest); 0
        | "install-legacy-writer-guard" :: rest -> installLegacyWriterGuard (options rest); 0
        | "install-engine" :: rest -> installEngine (options rest); 0
        | "install-manager" :: rest -> installManager (options rest); 0
        | "create-host-account" :: [] -> createAccount (); 0
        | "prepare-rootless-runtime" :: [] -> prepareRootlessRuntime (); 0
        | "stage-host-assets" :: rest -> stageHostAssets (options rest); 0
        | "install-host-files" :: rest -> installHostFiles (options rest); 0
        | "build-host-image" :: rest -> buildHostImage (options rest); 0
        | "verify-host-release" :: rest -> verifyHostRelease (options rest); 0
        | "verify-engine-release" :: rest -> verifyEngineRelease (options rest); 0
        | "update-host" :: rest -> runHostUpdater (options rest); 0
        | "backup-stopped-host" :: rest -> backup (options rest); 0
        | "prepare-inert" :: rest -> prepareInert (options rest); 0
        | _ ->
            eprintfn "usage: telemetry-host-manager <status|guard|install-guard-dropins|install-legacy-writer-guard|install-engine|install-manager|create-host-account|prepare-rootless-runtime|stage-host-assets|install-host-files|build-host-image|verify-host-release|verify-engine-release|update-host|backup-stopped-host|prepare-inert> [--name value ...]"
            2
    with error ->
        eprintfn "telemetry host manager refused: %s" error.Message
        3
