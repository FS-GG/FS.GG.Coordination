open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Xml.Linq

let fail message = eprintfn "CALLABLE_CLI_RELEASE_REFUSED %s" message; exit 2
let require condition message = if not condition then fail message
let sha256 path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let run workingDirectory command arguments =
    let start = ProcessStartInfo(command, WorkingDirectory = workingDirectory, UseShellExecute = false)
    for argument in arguments do start.ArgumentList.Add argument
    use proc = Process.Start start
    proc.WaitForExit()
    let invocation = String.concat " " arguments
    require (proc.ExitCode = 0) $"command failed: {command} {invocation}"

let capture workingDirectory command arguments =
    let start = ProcessStartInfo(command, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true)
    for argument in arguments do start.ArgumentList.Add argument
    use proc = Process.Start start
    let output = proc.StandardOutput.ReadToEnd().Trim()
    proc.WaitForExit()
    let invocation = String.concat " " arguments
    require (proc.ExitCode = 0) $"command failed: {command} {invocation}"
    output

let captureBytes workingDirectory command arguments =
    let start = ProcessStartInfo(command, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true)
    for argument in arguments do start.ArgumentList.Add argument
    use proc = Process.Start start
    use output = new MemoryStream()
    proc.StandardOutput.BaseStream.CopyTo output
    proc.WaitForExit()
    let invocation = String.concat " " arguments
    require (proc.ExitCode = 0) $"command failed: {command} {invocation}"
    output.ToArray()

let runForExit workingDirectory command arguments =
    let start = ProcessStartInfo(command, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    for argument in arguments do start.ArgumentList.Add argument
    use proc = Process.Start start
    proc.StandardOutput.ReadToEnd() |> ignore
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()
    proc.ExitCode

let canonicalize packagePath =
    let entries =
        use archive = ZipFile.OpenRead packagePath
        archive.Entries
        |> Seq.map (fun entry ->
            use source = entry.Open()
            use memory = new MemoryStream()
            source.CopyTo memory
            entry.FullName.Replace('\\', '/'), entry.ExternalAttributes, memory.ToArray())
        |> Seq.sortWith (fun (left, _, _) (right, _, _) -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.toArray
    let temporary = packagePath + ".canonical"
    use file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)
    use archive = new ZipArchive(file, ZipArchiveMode.Create, false, UTF8Encoding(false))
    for name, attributes, bytes in entries do
        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
        entry.ExternalAttributes <- attributes
        if not (name.EndsWith("/", StringComparison.Ordinal)) then
            use target = entry.Open()
            target.Write bytes
    archive.Dispose()
    file.Dispose()
    File.Move(temporary, packagePath, true)

let parse (arguments: string list) =
    let rec loop (values: Map<string, string>) = function
        | (name: string) :: value :: rest when name.StartsWith("--", StringComparison.Ordinal) -> loop (Map.add name value values) rest
        | [] -> values
        | _ -> fail "options must be --name value pairs"
    loop Map.empty arguments

let required (name: string) (values: Map<string, string>) =
    values |> Map.tryFind name |> Option.defaultWith (fun () -> fail $"missing {name}")

let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let command, options =
    match arguments with
    | command :: rest -> command, parse rest
    | [] -> fail "usage: callable-cli-release.fsx <prepare|verify> --source SHA --output DIRECTORY"

let source = required "--source" options
let output = required "--output" options |> Path.GetFullPath
let profile = options |> Map.tryFind "--profile" |> Option.defaultValue "cli"
require (profile = "cli" || profile = "execution-pair" || profile = "execution-pair-forward") "profile must be cli, execution-pair or execution-pair-forward"
// The forward profile has no default: its version is selected after authoritative occupancy.
let version = options |> Map.tryFind "--version" |> Option.defaultValue (if profile = "execution-pair" then "0.3.0" elif profile = "execution-pair-forward" then "" else "0.1.1")
let isPair = profile = "execution-pair" || profile = "execution-pair-forward"
let isForward = profile = "execution-pair-forward"
// Must equal eng/execution_pair_forward.py FORWARD_VERSION; a repository control compares both.
let forwardVersionPattern = "^(0\\.3\\.[1-9][0-9]*|0\\.([4-9]|[1-9][0-9]+)\\.(0|[1-9][0-9]*)|[1-9][0-9]*\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*))$"
let packageId = "FS.GG.Coordination.Cli"
let packageName = $"{packageId}.{version}.nupkg"
let packagePath = Path.Combine(output, packageName)
let manifestPath = Path.Combine(output, "callable-cli-release-manifest.json")
let tag = $"v{version}"
let project = Path.Combine(repo, "src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj")

let projectPackageVersion () =
    let document = XDocument.Load project
    document.Descendants(XName.Get "PackageVersion")
    |> Seq.tryExactlyOne
    |> Option.map _.Value
    |> Option.defaultWith (fun () -> fail "callable CLI project must declare exactly one PackageVersion")

let verify () =
    require (File.Exists manifestPath) "manifest is missing"
    require (File.Exists packagePath) "candidate package is missing"
    use document = JsonDocument.Parse(File.ReadAllBytes manifestPath)
    let root = document.RootElement
    require (root.GetProperty("schema").GetString() = "fsgg.coordination.callable-cli-release-preparation/1") "manifest schema changed"
    require (root.GetProperty("packageId").GetString() = packageId) "package identity changed"
    require (root.GetProperty("version").GetString() = version) "package version changed"
    require (root.GetProperty("tag").GetString() = tag) "package tag changed"
    require (root.GetProperty("sourceCommit").GetString() = source) "source identity changed"
    require (root.GetProperty("packageSha256").GetString() = sha256 packagePath) "candidate package digest changed"
    require (root.GetProperty("publicationAuthorized").GetBoolean() = false) "preparation cannot authorize publication"
    require (root.GetProperty("tagAuthorized").GetBoolean() = false) "preparation cannot authorize a tag"
    require (root.GetProperty("ownership").GetProperty("reservation").GetString() = "unreserved") "package identity must remain unreserved"
    printfn "CALLABLE_CLI_RELEASE_VERIFIED source=%s package=%s sha256=%s" source packageName (sha256 packagePath)


// The pair uses the same prepare/verify entry point and immutable candidate boundary.
// Historical CLI manifests and publication admissions remain a separate profile.
let pairIds = [ "FS.GG.Execution.Contracts"; "FS.GG.Execution.Processes" ]
let pairManifestPath = Path.Combine(output, "execution-pair-release-manifest.json")
let pairTag = $"execution/v{version}"
let pairProject id = Path.Combine(repo, "src", id, id + ".fsproj")
// The original profile remains exactly 0.3.0; the forward profile is exactly disjoint from it.
let requirePairVersion () =
    if isForward then require (System.Text.RegularExpressions.Regex.IsMatch(version, forwardVersionPattern) && not (version.EndsWith "\n")) "forward Execution pair version must be a newer stable release than 0.3.0"
    else require (version = "0.3.0") "only selected Execution pair 0.3.0 may be prepared"
let pairFile id = $"{id}.{version}.nupkg"
let pairPath id = Path.Combine(output, pairFile id)
let localElement (parent: XElement) name =
    parent.Descendants() |> Seq.filter (fun element -> element.Name.LocalName = name) |> Seq.exactlyOne
let packageProperty (path: string) (name: string) =
    let document = XDocument.Load(path: string)
    document.Descendants(XName.Get name) |> Seq.exactlyOne |> _.Value

let requirePairProject id =
    let versionOk = isForward || packageProperty (pairProject id) "PackageVersion" = version
    require (packageProperty (pairProject id) "PackageId" = id && versionOk && packageProperty (pairProject id) "IsPackable" = "true") "pair project metadata mismatch"

let pairMembersSupported (id: string) (names: string array) =
    let metadata = names |> Array.filter (fun name -> name.StartsWith("package/services/metadata/core-properties/",StringComparison.Ordinal))
    if metadata.Length <> 1 || Set.count(Set.ofArray names) <> names.Length then false
    else
        let leaf = Path.GetFileName metadata[0]
        let validMetadata = leaf = "nuget.psmdcp" || System.Text.RegularExpressions.Regex.IsMatch(leaf,"^[0-9a-fA-F]{32}\\.psmdcp$")
        let expected =
            Set.ofList ["_rels/.rels"; "[Content_Types].xml"; id + ".nuspec"; metadata[0]; "README.md"; $"lib/net10.0/{id}.dll"; $"lib/net10.0/{id}.xml"]
            |> Set.union (Directory.GetFiles(Path.GetDirectoryName(pairProject id),"*.fsi") |> Array.map (fun path -> "api/" + Path.GetFileName path) |> Set.ofArray)
        validMetadata && Set.ofArray names = expected

let checkPairMemberControls () =
    // Independently stated library package contract, not a model of the validator.
    for id,signatures in ["FS.GG.Execution.Contracts",["Types.fsi";"Validation.fsi";"Budget.fsi";"BoundedOutput.fsi";"Lifecycle.fsi"]; "FS.GG.Execution.Processes",["ProcessSessions.fsi"]] do
        let supported = ["_rels/.rels";"[Content_Types].xml";id + ".nuspec";"package/services/metadata/core-properties/nuget.psmdcp";"README.md";$"lib/net10.0/{id}.dll";$"lib/net10.0/{id}.xml"] @ (signatures |> List.map (fun name -> "api/" + name)) |> List.toArray
        require (pairMembersSupported id supported) "supported library member control refused"
        require (not(pairMembersSupported id (Array.append supported [|"build/transitive.targets"|]))) "package build target control accepted"
        require (not(pairMembersSupported id (Array.append supported [|"content/unsafe.exe"|]))) "executable content control accepted"

let inspectPairPackage (id: string) (path: string) =
    require (FileInfo(path).Length > 0L && FileInfo(path).Length <= 16777216L) "pair package exceeds its finite archive bound"
    use archive = ZipFile.OpenRead path
    let entries = archive.Entries |> Seq.toArray
    let names = entries |> Array.map _.FullName
    require (entries.Length <= 64 && Set.count(Set.ofArray names) = entries.Length) "pair package member population invalid"
    for entry in entries do
        require (entry.Length <= 8388608L && not (entry.FullName.StartsWith("/", StringComparison.Ordinal)) && not (entry.FullName.Contains("\\")) && not (entry.FullName.Split('/') |> Array.contains "..")) "unsafe or oversized pair package member"
        require ((entry.ExternalAttributes >>> 16) &&& 0o170000 <> 0o120000) "pair package contains a symlink"
    let bytes name =
        let entry = entries |> Array.filter (fun item -> item.FullName = name) |> Array.exactlyOne
        use stream = entry.Open()
        use memory = new MemoryStream()
        stream.CopyTo memory
        memory.ToArray()
    let nuspec = id + ".nuspec"
    require (pairMembersSupported id names) "pair package contains unsupported or missing members"
    use specification = new MemoryStream(bytes nuspec)
    let metadata = XDocument.Load(specification).Root |> fun root -> localElement root "metadata"
    require ((localElement metadata "id").Value = id && (localElement metadata "version").Value = version) "pair nuspec identity mismatch"
    let repository = localElement metadata "repository"
    require (repository.Attribute(XName.Get "type").Value = "git" && repository.Attribute(XName.Get "url").Value = "https://github.com/FS-GG/FS.GG.Coordination" && repository.Attribute(XName.Get "commit").Value = source) "pair repository provenance mismatch"
    let license = localElement metadata "license"
    require (license.Attribute(XName.Get "type").Value = "expression" && license.Value = "MIT") "pair license mismatch"
    require ((localElement metadata "readme").Value = "README.md" && not (String.IsNullOrWhiteSpace((localElement metadata "description").Value)) && (localElement metadata "description").Value <> "Package Description") "pair descriptive metadata missing"
    let projectRoot = Path.GetDirectoryName(pairProject id)
    require ((bytes "README.md").AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(projectRoot,"README.md")).AsSpan())) "pair packaged readme differs from source"
    let apiNames = Directory.GetFiles(projectRoot, "*.fsi") |> Array.map Path.GetFileName |> Array.sort
    require ((names |> Array.filter (fun name -> name.StartsWith("api/",StringComparison.Ordinal)) |> Array.sort) = (apiNames |> Array.map (fun name -> "api/" + name))) "pair API signature population mismatch"
    for name in apiNames do
        require ((bytes ("api/" + name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(projectRoot,name)).AsSpan())) "pair packaged API signature differs from source"
    let assemblyName = $"lib/net10.0/{id}.dll"
    require ((names |> Array.filter (fun name -> name.EndsWith(".dll",StringComparison.Ordinal))) = [|assemblyName|]) "pair assembly population mismatch"
    require ((bytes ($"lib/net10.0/{id}.xml")).Length > 0) "pair XML documentation missing"
    require (names |> Array.forall (fun name -> not (name.StartsWith("tools/",StringComparison.Ordinal) || name.StartsWith("runtimes/",StringComparison.Ordinal) || name = ".signature.p7s"))) "pair includes unexpected executable/native/signature assets"
    let dependencies = metadata.Descendants() |> Seq.filter (fun element -> element.Name.LocalName = "dependency") |> Seq.toArray
    let depIds = dependencies |> Array.map (fun element -> element.Attribute(XName.Get "id").Value.ToLowerInvariant()) |> Array.sort
    let expected = if id = "FS.GG.Execution.Contracts" then [|"fsharp.core"|] else [|"fs.gg.execution.contracts";"fsharp.core"|] |> Array.sort
    require (depIds = expected) "pair dependency direction/population mismatch"
    if id = "FS.GG.Execution.Processes" then
        let dependency = dependencies |> Array.find (fun element -> element.Attribute(XName.Get "id").Value = "FS.GG.Execution.Contracts")
        let range = dependency.Attribute(XName.Get "version").Value.Replace(" ","")
        require (range = version || range = $"[{version},)" || range = $"[{version}]") "pair Contracts dependency version mismatch"
    let result = JsonObject()
    result.Add("packageId",id)
    result.Add("file",pairFile id)
    result.Add("sha256",sha256 path)
    result.Add("bytes",FileInfo(path).Length)
    let assemblySha: string = bytes assemblyName |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    result.Add("assemblySha256",assemblySha)
    result

let verifyPair () =
    requirePairVersion ()
    require (File.Exists pairManifestPath) "pair manifest is missing"
    require (Set.ofArray(Directory.GetFiles(output) |> Array.map Path.GetFileName) = Set.ofList [pairFile pairIds[0];pairFile pairIds[1];Path.GetFileName pairManifestPath]) "pair candidate must contain exactly two packages and its manifest"
    use document = JsonDocument.Parse(File.ReadAllBytes pairManifestPath)
    let root = document.RootElement
    require (root.GetProperty("schema").GetString() = "fsgg.coordination.execution-pair-release-preparation/1" && root.GetProperty("profile").GetString() = "execution-pair") "pair manifest profile mismatch"
    require (root.GetProperty("version").GetString() = version && root.GetProperty("tag").GetString() = pairTag && root.GetProperty("sourceCommit").GetString() = source) "pair manifest source/version/tag mismatch"
    require (root.GetProperty("sourceTree").GetString() = capture repo "git" ["rev-parse";source + "^{tree}"]) "pair manifest tree mismatch"
    require (not (root.GetProperty("publicationAuthorized").GetBoolean()) && not (root.GetProperty("tagAuthorized").GetBoolean()) && root.GetProperty("ownership").GetProperty("reservation").GetString() = "unreserved") "pair preparation cannot authorize effects"
    let members = root.GetProperty("packages").EnumerateArray() |> Seq.toArray
    require (members.Length = 2) "pair manifest member population mismatch"
    for id in pairIds do
        let memberRow = members |> Array.filter (fun item -> item.GetProperty("packageId").GetString() = id) |> Array.exactlyOne
        let actual = inspectPairPackage id (pairPath id)
        require (memberRow.GetRawText() = actual.ToJsonString()) "pair manifest member differs from actual package"
    printfn "EXECUTION_PAIR_RELEASE_VERIFIED source=%s version=%s" source version

let preparePair () =
    requirePairVersion ()
    require (source.Length = 40 && source |> Seq.forall Uri.IsHexDigit) "source must be an exact 40-character Git SHA"
    require (capture repo "git" ["rev-parse";"HEAD"] = source && String.IsNullOrWhiteSpace(capture repo "git" ["status";"--porcelain"])) "pair source must be exact clean HEAD"
    require (String.IsNullOrWhiteSpace(capture repo "git" ["tag";"--list";pairTag])) "pair component tag already exists"
    for id in pairIds do
        requirePairProject id
    require (not (Directory.Exists output) || Directory.GetFileSystemEntries(output).Length = 0) "pair output must be empty"
    Directory.CreateDirectory output |> ignore
    let scratch = Path.Combine(Path.GetTempPath(),"fsgg-execution-pair-" + Guid.NewGuid().ToString("N"))
    try
        let first,second = Path.Combine(scratch,"first"),Path.Combine(scratch,"second-independent-root")
        Directory.CreateDirectory first |> ignore
        Directory.CreateDirectory second |> ignore
        for target in [first;second] do
            for id in pairIds do
                run repo "dotnet" (["pack";pairProject id;"--configuration";"Release";"--output";target;"--no-restore";"-p:ContinuousIntegrationBuild=true";"-p:Deterministic=true";$"-p:RepositoryCommit={source}";"-p:RepositoryBranch=main"] @ (if isForward then [$"-p:PackageVersion={version}"] else []))
                canonicalize (Path.Combine(target,pairFile id))
        for id in pairIds do
            let a,b = File.ReadAllBytes(Path.Combine(first,pairFile id)),File.ReadAllBytes(Path.Combine(second,pairFile id))
            require (a.AsSpan().SequenceEqual(b.AsSpan())) "pair candidate bytes differ across independent output roots"
            File.Copy(Path.Combine(first,pairFile id),pairPath id)
        let manifest = JsonObject()
        manifest.Add("schema","fsgg.coordination.execution-pair-release-preparation/1")
        manifest.Add("profile","execution-pair")
        manifest.Add("sourceCommit",source)
        manifest.Add("sourceTree",capture repo "git" ["rev-parse";source + "^{tree}"])
        manifest.Add("version",version)
        manifest.Add("tag",pairTag)
        manifest.Add("publicationAuthorized",false)
        manifest.Add("tagAuthorized",false)
        manifest.Add("publicationOrder","github-packages-pair-then-identical-nuget-org-inputs")
        let ownership = JsonObject()
        ownership.Add("reservation","unreserved")
        ownership.Add("githubPackages","not-observed-by-preparation")
        ownership.Add("nugetOrg","not-observed-by-preparation")
        manifest.Add("ownership",ownership)
        let members = JsonArray()
        for id in pairIds do members.Add(inspectPairPackage id (pairPath id))
        manifest.Add("packages",members)
        File.WriteAllText(pairManifestPath,manifest.ToJsonString() + "\n",UTF8Encoding(false))
        verifyPair ()
        printfn "EXECUTION_PAIR_RELEASE_PREPARED source=%s version=%s" source version
    finally
        if Directory.Exists scratch then Directory.Delete(scratch,true)

match command with
| "inspect" when isPair ->
    requirePairVersion ()
    for id in pairIds do inspectPairPackage id (pairPath id) |> ignore
    printfn "EXECUTION_PAIR_MEMBERS_INSPECTED version=%s publicationAuthorized=false" version
| "check-profile" when isPair ->
    requirePairVersion ()
    for id in pairIds do
        requirePairProject id
    checkPairMemberControls ()
    printfn "EXECUTION_PAIR_PROFILE_CHECKED version=%s publicationAuthorized=false" version
| "prepare" when isPair -> preparePair ()
| "verify" when isPair -> verifyPair ()
| "prepare" ->
    require
        (Set.contains version (Set.ofList [ "0.1.1"; "0.1.2"; "0.1.3"; "0.1.4"; "0.1.5"; "0.1.6"; "0.1.7"; "0.2.0"; "0.2.1"; "0.3.0" ]))
        "only reviewed callable CLI versions 0.1.1 through 0.1.7 and 0.2.0 through 0.2.1 and CLI-only 0.3.0 may be prepared"
    require (projectPackageVersion () = version) "requested version does not equal the callable CLI PackageVersion"
    require (source.Length = 40 && source |> Seq.forall Uri.IsHexDigit) "source must be an exact 40-character Git SHA"
    require (capture repo "git" [ "rev-parse"; "HEAD" ] = source) "source does not equal HEAD"
    require (String.IsNullOrWhiteSpace(capture repo "git" [ "status"; "--porcelain" ])) "source worktree is not clean"
    let tags = capture repo "git" [ "tag"; "--list"; tag ]
    require (String.IsNullOrWhiteSpace tags) $"{tag} already exists"
    require (not (Directory.Exists output) || Directory.GetFileSystemEntries(output).Length = 0) "output must be empty"
    Directory.CreateDirectory output |> ignore
    let scratch = Path.Combine(Path.GetTempPath(), "fsgg-callable-cli-" + Guid.NewGuid().ToString("N"))
    try
        let first = Path.Combine(scratch, "first")
        let second = Path.Combine(scratch, "second-independent-root")
        Directory.CreateDirectory first |> ignore
        Directory.CreateDirectory second |> ignore
        let pack target =
            run repo "dotnet" [ "pack"; project; "--configuration"; "Release"; "--output"; target; "--no-restore"; "-p:ContinuousIntegrationBuild=true"; "-p:Deterministic=true"; $"-p:PackageVersion={version}"; $"-p:RepositoryCommit={source}"; "-p:RepositoryBranch=main" ]
            let path = Path.Combine(target, packageName)
            require (File.Exists path) "pack did not create the exact candidate"
            canonicalize path
            path
        let firstPackage = pack first
        let secondPackage = pack second
        require (File.ReadAllBytes(firstPackage).AsSpan().SequenceEqual(File.ReadAllBytes(secondPackage).AsSpan())) "candidate bytes differ across independent output roots"
        File.Copy(firstPackage, packagePath)
        use package = ZipFile.OpenRead packagePath
        let executionAssemblies =
            package.Entries
            |> Seq.filter (fun entry -> entry.FullName.EndsWith("/FS.GG.Coordination.Orchestration.Execution.dll", StringComparison.Ordinal))
            |> Seq.length
        require (executionAssemblies = 1) "candidate package must contain exactly one Execution assembly"
        let manifest = JsonObject()
        manifest.Add("packageId", packageId)
        manifest.Add("packageSha256", sha256 packagePath)
        manifest.Add("publicationAuthorized", false)
        manifest.Add("publicationOrder", "github-packages-then-byte-identical-nuget-org")
        manifest.Add("schema", "fsgg.coordination.callable-cli-release-preparation/1")
        manifest.Add("sourceCommit", source.ToLowerInvariant())
        manifest.Add("sourceTree", capture repo "git" [ "rev-parse"; source + "^{tree}" ])
        manifest.Add("tag", tag)
        manifest.Add("tagAuthorized", false)
        manifest.Add("version", version)
        let ownership = JsonObject()
        ownership.Add("githubPackages", if version = "0.3.0" then "not-observed-by-preparation" else "unverified-403")
        ownership.Add("nugetOrg", if version = "0.3.0" then "not-observed-by-preparation" else "observed-404")
        ownership.Add("reservation", "unreserved")
        manifest.Add("ownership", ownership)
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonSerializerOptions(WriteIndented = false)) + "\n", UTF8Encoding(false))
        verify ()

        let installRoot = Path.Combine(scratch, "installed")
        let config = Path.Combine(scratch, "NuGet.Config")
        File.WriteAllText(config, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear/><add key=\"candidate\" value=\"{output}\"/></packageSources></configuration>")
        run repo "dotnet" [ "tool"; "install"; packageId; "--version"; version; "--tool-path"; installRoot; "--configfile"; config; "--no-cache" ]
        let installed = Path.Combine(installRoot, "fsgg-coordination")
        run scratch installed []
        if version = "0.3.0" then
            let refusal = Path.Combine(scratch, "installed-execute-main-refusal.json")
            run repo "python3" [ "eng/test-callable-cli-installed-harness.py"; "--execute-main-refusal"; installed; refusal ]
            printfn "%s" (File.ReadAllText refusal)
        for name in [ "toolchain-profile"; "command"; "result" ] do
            let expected = File.ReadAllBytes(Path.Combine(repo, "contracts", "portable-workspace", "v1", name + ".schema.json"))
            let actual = captureBytes scratch installed [ "workspace-contract"; "schema"; "export"; name ]
            require (actual.AsSpan().SequenceEqual(expected)) $"installed schema export changed: {name}"
        let malformed = Path.Combine(scratch, "malformed-portable-command.json")
        File.WriteAllText(malformed, "{}\n", UTF8Encoding(false))
        require
            (runForExit scratch installed [ "workspace-contract"; "validate"; "--schema"; "fsgg.workspace.command/1"; "--file"; malformed ] = 3)
            "installed CLI did not refuse a malformed portable contract"
        printfn "CALLABLE_CLI_RELEASE_PREPARED source=%s package=%s sha256=%s" source packageName (sha256 packagePath)
    finally
        if Directory.Exists scratch then Directory.Delete(scratch, true)
| "verify" -> verify ()
| _ -> fail "command must be prepare or verify"
