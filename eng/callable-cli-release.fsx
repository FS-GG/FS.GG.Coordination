open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

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
let version = options |> Map.tryFind "--version" |> Option.defaultValue "0.1.0"
let packageId = "FS.GG.Coordination.Cli"
let packageName = $"{packageId}.{version}.nupkg"
let packagePath = Path.Combine(output, packageName)
let manifestPath = Path.Combine(output, "callable-cli-release-manifest.json")

let verify () =
    require (File.Exists manifestPath) "manifest is missing"
    require (File.Exists packagePath) "candidate package is missing"
    use document = JsonDocument.Parse(File.ReadAllBytes manifestPath)
    let root = document.RootElement
    require (root.GetProperty("schema").GetString() = "fsgg.coordination.callable-cli-release-preparation/1") "manifest schema changed"
    require (root.GetProperty("packageId").GetString() = packageId) "package identity changed"
    require (root.GetProperty("version").GetString() = version) "package version changed"
    require (root.GetProperty("sourceCommit").GetString() = source) "source identity changed"
    require (root.GetProperty("packageSha256").GetString() = sha256 packagePath) "candidate package digest changed"
    require (root.GetProperty("publicationAuthorized").GetBoolean() = false) "preparation cannot authorize publication"
    require (root.GetProperty("tagAuthorized").GetBoolean() = false) "preparation cannot authorize a tag"
    require (root.GetProperty("ownership").GetProperty("reservation").GetString() = "unreserved") "package identity must remain unreserved"
    printfn "CALLABLE_CLI_RELEASE_VERIFIED source=%s package=%s sha256=%s" source packageName (sha256 packagePath)

match command with
| "prepare" ->
    require (version = "0.1.0") "only the proposed first stable version 0.1.0 may be prepared"
    require (source.Length = 40 && source |> Seq.forall Uri.IsHexDigit) "source must be an exact 40-character Git SHA"
    require (capture repo "git" [ "rev-parse"; "HEAD" ] = source) "source does not equal HEAD"
    require (String.IsNullOrWhiteSpace(capture repo "git" [ "status"; "--porcelain" ])) "source worktree is not clean"
    let tags = capture repo "git" [ "tag"; "--list"; "v0.1.0" ]
    require (String.IsNullOrWhiteSpace tags) "v0.1.0 already exists"
    require (not (Directory.Exists output) || Directory.GetFileSystemEntries(output).Length = 0) "output must be empty"
    Directory.CreateDirectory output |> ignore
    let scratch = Path.Combine(Path.GetTempPath(), "fsgg-callable-cli-" + Guid.NewGuid().ToString("N"))
    try
        let first = Path.Combine(scratch, "first")
        let second = Path.Combine(scratch, "second-independent-root")
        Directory.CreateDirectory first |> ignore
        Directory.CreateDirectory second |> ignore
        let project = Path.Combine(repo, "src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj")
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
        let manifest = JsonObject()
        manifest.Add("packageId", packageId)
        manifest.Add("packageSha256", sha256 packagePath)
        manifest.Add("publicationAuthorized", false)
        manifest.Add("publicationOrder", "github-packages-then-byte-identical-nuget-org")
        manifest.Add("schema", "fsgg.coordination.callable-cli-release-preparation/1")
        manifest.Add("sourceCommit", source.ToLowerInvariant())
        manifest.Add("sourceTree", capture repo "git" [ "rev-parse"; source + "^{tree}" ])
        manifest.Add("tag", "v0.1.0")
        manifest.Add("tagAuthorized", false)
        manifest.Add("version", version)
        let ownership = JsonObject()
        ownership.Add("githubPackages", "unverified-403")
        ownership.Add("nugetOrg", "observed-404")
        ownership.Add("reservation", "unreserved")
        manifest.Add("ownership", ownership)
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonSerializerOptions(WriteIndented = false)) + "\n", UTF8Encoding(false))
        verify ()

        let installRoot = Path.Combine(scratch, "installed")
        let config = Path.Combine(scratch, "NuGet.Config")
        File.WriteAllText(config, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear/><add key=\"candidate\" value=\"{output}\"/></packageSources></configuration>")
        run repo "dotnet" [ "tool"; "install"; packageId; "--version"; version; "--tool-path"; installRoot; "--configfile"; config; "--no-cache" ]
        run scratch (Path.Combine(installRoot, "fsgg-coordination")) []
        printfn "CALLABLE_CLI_RELEASE_PREPARED source=%s package=%s sha256=%s" source packageName (sha256 packagePath)
    finally
        if Directory.Exists scratch then Directory.Delete(scratch, true)
| "verify" -> verify ()
| _ -> fail "command must be prepare or verify"
