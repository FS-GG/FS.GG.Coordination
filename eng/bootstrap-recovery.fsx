open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

let fail code detail = failwith $"{code}: {detail}"

let sha256 path =
    File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let run cwd executable arguments environment =
    let start = ProcessStartInfo(executable)
    start.WorkingDirectory <- cwd
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in arguments do start.ArgumentList.Add argument
    for key, value in environment do start.Environment[key] <- value
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEnd()
    let stderr = child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then
        eprintf "%s" stdout
        eprintf "%s" stderr
        let command = String.concat " " arguments
        fail "BR-PROCESS" $"{executable} {command} exited {child.ExitCode}"
    stdout.Trim()

let canonicalWrite (path: string) (node: JsonNode) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, node.ToJsonString() + "\n", UTF8Encoding(false))

let validateRoot root =
    let full = Path.GetFullPath root
    if not (Directory.Exists(Path.Combine(full, ".git"))) && not (File.Exists(Path.Combine(full, ".git"))) then
        fail "BR-ROOT" "expected a git worktree"
    full

let execute root =
    let source = validateRoot root
    let forbiddenOverrides =
        [ "FSGG_BOOTSTRAP_PACKAGE_OVERRIDE"; "FSGG_BOOTSTRAP_RECOVERY_COMMAND"
          "RESTORESOURCES"; "RESTOREADDITIONALPROJECTSOURCES"; "NUGET_RESTORE_MSBUILD_ARGS" ]
    for name in forbiddenOverrides do
        if not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable name)) then
            fail "BR-OVERRIDE" name

    let sourceStatus = run source "git" [ "status"; "--porcelain=v1"; "--untracked-files=all" ] []
    if sourceStatus <> "" then fail "BR-SOURCE-DIRTY" "candidate worktree must be clean"
    let candidate = run source "git" [ "rev-parse"; "--verify"; "HEAD" ] []
    if candidate.Length <> 40 || candidate |> Seq.exists (fun c -> not (c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) then
        fail "BR-REVISION" candidate

    let scratch = Path.Combine(Path.GetTempPath(), $"fsgg-bootstrap-recovery-{Guid.NewGuid():N}")
    let clone = Path.Combine(scratch, "candidate")
    let cliHome = Path.Combine(scratch, "dotnet-home")
    let packages = Path.Combine(scratch, "packages")
    let httpCache = Path.Combine(scratch, "http-cache")
    let pluginsCache = Path.Combine(scratch, "plugins-cache")
    let feed = Path.Combine(scratch, "feed")
    let consumer = Path.Combine(scratch, "consumer")
    let publishedSource = "https://api.nuget.org/v3/index.json"
    let environment =
        [ "DOTNET_CLI_HOME", cliHome
          "DOTNET_NOLOGO", "1"
          "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"
          "NUGET_PACKAGES", packages
          "NUGET_HTTP_CACHE_PATH", httpCache
          "NUGET_PLUGINS_CACHE_PATH", pluginsCache ]
    try
        Directory.CreateDirectory scratch |> ignore
        run source "git" [ "clone"; "--no-local"; "--no-checkout"; "--quiet"; source; clone ] environment |> ignore
        run clone "git" [ "checkout"; "--detach"; "--quiet"; candidate ] environment |> ignore
        if run clone "git" [ "rev-parse"; "HEAD" ] environment <> candidate then fail "BR-CLONE-REVISION" candidate
        if run clone "git" [ "status"; "--porcelain=v1"; "--untracked-files=all" ] environment <> "" then
            fail "BR-CLONE-DIRTY" candidate

        let nugetConfig = Path.Combine(scratch, "NuGet.Config")
        File.WriteAllText(
            nugetConfig,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" /></packageSources><packageSourceMapping><packageSource key=\"nuget.org\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>\n",
            UTF8Encoding(false))

        run clone "dotnet" [ "restore"; "FS.GG.Coordination.sln"; "--locked-mode"; "--configfile"; nugetConfig; "--source"; publishedSource ] environment |> ignore
        run clone "dotnet" [ "build"; "FS.GG.Coordination.sln"; "--configuration"; "Release"; "--no-restore"; "--warnaserror" ] environment |> ignore
        run clone "dotnet" [ "test"; "tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj"; "--configuration"; "Release"; "--no-build"; "--no-restore" ] environment |> ignore
        run clone "dotnet" [ "test"; "tests/FS.GG.Coordination.ArchitectureTests/FS.GG.Coordination.ArchitectureTests.fsproj"; "--configuration"; "Release"; "--no-build"; "--no-restore" ] environment |> ignore

        Directory.CreateDirectory feed |> ignore
        run clone "dotnet" [ "pack"; "src/FS.GG.Coordination.Protocol/FS.GG.Coordination.Protocol.fsproj"; "--configuration"; "Release"; "--no-build"; "--no-restore"; "--output"; feed; "-p:IsPackable=true"; "-p:PackageVersion=0.0.0-bootstrap" ] environment |> ignore
        let package = Path.Combine(feed, "FS.GG.Coordination.Protocol.0.0.0-bootstrap.nupkg")
        if not (File.Exists package) then fail "BR-PACKAGE" package

        Directory.CreateDirectory consumer |> ignore
        for file in [ "Bootstrap.Consumer.fsproj"; "Program.fs" ] do
            File.Copy(Path.Combine(clone, "tests/fixtures/bootstrap-package-consumer", file), Path.Combine(consumer, file))
        let consumerConfig = Path.Combine(consumer, "NuGet.Config")
        File.WriteAllText(
            consumerConfig,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear /><add key=\"bootstrap\" value=\"{feed}\" /><add key=\"nuget.org\" value=\"{publishedSource}\" protocolVersion=\"3\" /></packageSources><packageSourceMapping><packageSource key=\"bootstrap\"><package pattern=\"FS.GG.Coordination.*\" /></packageSource><packageSource key=\"nuget.org\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>\n",
            UTF8Encoding(false))
        let consumerProject = Path.Combine(consumer, "Bootstrap.Consumer.fsproj")
        run consumer "dotnet" [ "restore"; consumerProject; "--use-lock-file"; "--force-evaluate"; "--configfile"; consumerConfig; "--source"; feed; "--source"; publishedSource ] environment |> ignore
        run consumer "dotnet" [ "restore"; consumerProject; "--locked-mode"; "--configfile"; consumerConfig; "--source"; feed; "--source"; publishedSource ] environment |> ignore
        run consumer "dotnet" [ "build"; consumerProject; "--configuration"; "Release"; "--no-restore"; "--warnaserror" ] environment |> ignore
        let observed = run consumer "dotnet" [ "run"; "--project"; consumerProject; "--configuration"; "Release"; "--no-build"; "--no-restore" ] environment
        if observed <> "FS.GG.Coordination.Protocol:1" then fail "BR-EXECUTE" observed

        let result = JsonObject()
        result.Add("schema", "fsgg.coordination.bootstrap-recovery/1")
        result.Add("candidate", candidate)
        result.Add("packageSha256", sha256 package)
        let sources = JsonArray()
        sources.Add publishedSource
        result.Add("publishedSources", sources)
        let stages = JsonArray()
        for stage in [ "clone"; "restore"; "build"; "unit-tests"; "architecture-tests"; "pack"; "install"; "execute" ] do stages.Add stage
        result.Add("stages", stages)
        let output = Path.Combine(source, "artifacts/bootstrap-recovery/result.json")
        canonicalWrite output result
        printfn "BOOTSTRAP_RECOVERY_OK candidate=%s packageSha256=%s stages=8" candidate (sha256 package)
    finally
        if Directory.Exists scratch then Directory.Delete(scratch, true)

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
try
    match arguments with
    | [ root ] -> execute root
    | _ -> fail "BR-USAGE" "bootstrap-recovery.fsx <repository-root>"
with error ->
    eprintfn "%s" error.Message
    exit 2
