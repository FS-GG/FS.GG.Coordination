module FS.GG.Coordination.Orchestration.Host.Tests.CompatibilityDiagnosticTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Orchestration.Host
open Xunit

let private sourceRoot =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then
            directory.FullName
        else
            find directory.Parent

    find (DirectoryInfo AppContext.BaseDirectory)

let private configuration =
    if AppContext.BaseDirectory.Contains("/Release/") then
        "Release"
    else
        "Debug"

let private executable project name =
    Path.Combine(sourceRoot, "src", project, "bin", configuration, "net10.0", "linux-x64", name)

let private host =
    executable "FS.GG.Coordination.Orchestration.Host" "fsgg-coord-orchestration-host"

let private runner =
    executable "FS.GG.Coordination.Orchestration.Runner.Client" "fsgg-coord-orchestration-runner"

let private sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()

let private script root name body =
    let path = Path.Combine(root, name)
    File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n")
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    path

let private rawScript (root: string) (name: string) (body: string) =
    let path = Path.Combine(root, name)
    File.WriteAllText(path, body)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    path

let private run
    (path: string)
    (workingDirectory: string)
    (arguments: string list)
    (input: byte array option)
    (timeout: int)
    =
    let start =
        ProcessStartInfo(
            path,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    arguments |> List.iter start.ArgumentList.Add
    start.Environment["FSGG_FORBIDDEN_FIXTURE"] <- "secret"
    use child = Process.Start start

    match input with
    | Some(bytes: byte array) ->
        child.StandardInput.BaseStream.Write(bytes)
        child.StandardInput.Close()
    | None -> ()

    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit timeout) then
        child.Kill(true)
        failwith "fixture process did not terminate"

    child.ExitCode, output.Result.Trim(), error.Result.Trim()

let private hostArguments runnerPath runnerHash provider providerHash working expected =
    [
        "probe-executor-compatibility"
        "--runner-executable"
        runnerPath
        "--runner-sha256"
        runnerHash
        "--provider-executable"
        provider
        "--provider-sha256"
        providerHash
        "--working-directory"
        working
    ]
    @ (expected
       |> Option.toList
       |> List.collect (fun value -> [ "--expected-codex-version"; value ]))
    @ [ "--timeout-seconds"; "2" ]

let private sentinel root version =
    script
        root
        "codex-sentinel"
        $"""printf '%%s\n' "$*" >> '{root}/calls'
case "$*" in
  --version) printf '%%s\n' 'codex-cli {version}' ;;
  "login status") printf '%%s\n' 'Logged in using ChatGPT' ;;
  *) printf '%%s\n' 'unexpected provider command' >&2; exit 91 ;;
esac"""

let private fixedProfile root provider =
    let workspace = Path.Combine(root, "owned-workspace")
    let profilePath = Path.Combine(root, "reviewed-profile.json")
    let resultPath = Path.Combine(root, "result.json")

    let profile =
        {
            Schema = FixedQualificationOperation.profileSchema
            Operation = FixedQualificationOperation.operation
            Revision = "fixture-review-1"
            HostExecutableSha256 = sha256 host
            RunnerExecutable = runner
            RunnerExecutableSha256 = sha256 runner
            ProviderExecutable = provider
            ProviderExecutableSha256 = sha256 provider
            ExpectedRunnerProtocol = FixedQualificationOperation.runnerProtocol
            ExpectedAdapterVersion = FixedQualificationOperation.adapterVersion
            ExpectedCodexVersion = "0.158.0"
            EnvironmentAllowList = [| "PATH" |]
            CredentialScope = FixedQualificationOperation.credentialScope
            MaximumRuntimeSeconds = 2
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 5.
            DisposableWorkspace = workspace
            Cleanup = FixedQualificationOperation.cleanupKind
        }

    File.WriteAllBytes(
        profilePath,
        JsonSerializer.SerializeToUtf8Bytes(
            profile,
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        )
    )

    profilePath, resultPath, workspace

[<Fact>]
let ``fixed qualification runs only the reviewed diagnostic and records cleanup`` () =
    let root = Directory.CreateTempSubdirectory("fixed-qualification-").FullName

    let provider =
        script
            root
            "fixed-codex-sentinel"
            $"""printf '%%s\n' "$*" >> '{root}/calls'
printf '%%s\n' "${{FSGG_FORBIDDEN_FIXTURE-unset}}" >> '{root}/environment'
case "$*" in
  --version) printf '%%s\n' 'codex-cli 0.158.0' ;;
  "login status") printf '%%s\n' 'Logged in using ChatGPT' ;;
  *) exit 91 ;;
esac"""

    let profile, result, workspace = fixedProfile root provider

    let code, output, error =
        run host root [ "qualify-fixed-job"; "--profile"; profile; "--result"; result ] None 10000

    Assert.Equal(0, code)
    Assert.Equal("", output)
    Assert.Equal("", error)
    Assert.False(Directory.Exists workspace)
    use document = JsonDocument.Parse(File.ReadAllBytes result)
    let value = document.RootElement
    Assert.Equal(FixedQualificationOperation.resultSchema, value.GetProperty("schema").GetString())
    Assert.Equal("executor-compatibility/1", value.GetProperty("operation").GetString())
    Assert.Equal("passed", value.GetProperty("disposition").GetString())
    Assert.True(value.GetProperty("cleanup").GetProperty("processTreeTerminated").GetBoolean())
    Assert.True(value.GetProperty("cleanup").GetProperty("workspaceRemovalAttempted").GetBoolean())
    Assert.True(value.GetProperty("cleanup").GetProperty("workspaceRemoved").GetBoolean())

    Assert.Equal<string list>(
        [ "--version"; "login status" ],
        File.ReadAllLines(Path.Combine(root, "calls")) |> Array.toList
    )

    Assert.All(File.ReadAllLines(Path.Combine(root, "environment")), fun value -> Assert.Equal("unset", value))

[<Fact>]
let ``fixed qualification rejects profile field injection before provider launch`` () =
    let root =
        Directory.CreateTempSubdirectory("fixed-qualification-injection-").FullName

    let provider = sentinel root "0.158.0"
    let profile, result, workspace = fixedProfile root provider
    let node = JsonNode.Parse(File.ReadAllBytes profile).AsObject()
    node["command"] <- "sh -c arbitrary"
    File.WriteAllText(profile, node.ToJsonString())

    let code, _, error =
        run host root [ "qualify-fixed-job"; "--profile"; profile; "--result"; result ] None 10000

    Assert.Equal(3, code)
    Assert.Contains("fixed-profile-shape-refused", error)
    Assert.False(File.Exists(Path.Combine(root, "calls")))
    Assert.False(Directory.Exists workspace)
    use document = JsonDocument.Parse(File.ReadAllBytes result)
    Assert.Equal("refused", document.RootElement.GetProperty("disposition").GetString())
    Assert.Equal("fixed-profile-shape-refused", document.RootElement.GetProperty("detail").GetString())

[<Fact>]
let ``fixed qualification kills a timed out reviewed runner and removes its workspace`` () =
    let root = Directory.CreateTempSubdirectory("fixed-qualification-timeout-").FullName
    let provider = sentinel root "0.158.0"
    let profile, result, workspace = fixedProfile root provider

    let hung =
        script root "hung-reviewed-runner" $"printf '%%s' $$ > '{root}/hung-pid'; sleep 30"

    let node = JsonNode.Parse(File.ReadAllBytes profile).AsObject()
    node["runnerExecutable"] <- hung
    node["runnerExecutableSha256"] <- sha256 hung
    node["maximumRuntimeSeconds"] <- 1
    File.WriteAllText(profile, node.ToJsonString())

    let code, _, error =
        run host root [ "qualify-fixed-job"; "--profile"; profile; "--result"; result ] None 10000

    Assert.Equal(3, code)
    Assert.Contains("compatibility-diagnostic-timeout", error)
    Assert.False(Directory.Exists workspace)
    let pid = File.ReadAllText(Path.Combine(root, "hung-pid"))
    Assert.False(Directory.Exists("/proc/" + pid))
    use document = JsonDocument.Parse(File.ReadAllBytes result)
    let cleanup = document.RootElement.GetProperty("cleanup")
    Assert.True(cleanup.GetProperty("processTreeTerminationRequired").GetBoolean())
    Assert.True(cleanup.GetProperty("processTreeTerminated").GetBoolean())
    Assert.True(cleanup.GetProperty("workspaceRemoved").GetBoolean())

[<Fact>]
let ``served Host diagnostic uses only production version and login probes`` () =
    let root = Directory.CreateTempSubdirectory("served-compatibility-").FullName
    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName
    let provider = sentinel root "0.158.0"

    let code, output, error =
        run
            host
            working
            (hostArguments runner (sha256 runner) provider (sha256 provider) working (Some "0.158.0"))
            None
            10000

    Assert.Equal(0, code)
    Assert.Equal("", error)
    use document = JsonDocument.Parse output
    let result = document.RootElement
    Assert.Equal("compatibility-diagnostic-only", result.GetProperty("scope").GetString())
    Assert.Equal("Codex", result.GetProperty("provider").GetString())
    Assert.Equal("matched", result.GetProperty("versionState").GetString())
    Assert.Equal("authenticated", result.GetProperty("authenticationState").GetString())
    Assert.Equal(sha256 runner, result.GetProperty("runnerExecutableSha256").GetString())
    Assert.Equal(sha256 provider, result.GetProperty("providerExecutableSha256").GetString())

    Assert.Equal<string list>(
        [ "--version"; "login status" ],
        File.ReadAllLines(Path.Combine(root, "calls")) |> Array.toList
    )

    Assert.Empty(Directory.EnumerateFileSystemEntries working)

[<Fact>]
let ``omitted version keeps 0.154 default and mismatch stops before login`` () =
    let root =
        Directory.CreateTempSubdirectory("served-compatibility-default-").FullName

    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName
    let provider = sentinel root "0.158.0"

    let code, output, _ =
        run host working (hostArguments runner (sha256 runner) provider (sha256 provider) working None) None 10000

    Assert.Equal(0, code)
    use document = JsonDocument.Parse output
    Assert.Equal("mismatch", document.RootElement.GetProperty("versionState").GetString())
    Assert.Equal("unknown", document.RootElement.GetProperty("authenticationState").GetString())
    Assert.Equal<string list>([ "--version" ], File.ReadAllLines(Path.Combine(root, "calls")) |> Array.toList)

[<Fact>]
let ``served Host rejects malformed duplicate and wrong pinned selections before provider`` () =
    let root =
        Directory.CreateTempSubdirectory("served-compatibility-options-").FullName

    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName
    let provider = sentinel root "0.158.0"

    let basic =
        hostArguments runner (sha256 runner) provider (sha256 provider) working (Some "0.158.0")

    for arguments in
        [
            basic @ [ "--expected-codex-version"; "0.158.0" ]
            hostArguments runner (sha256 runner) provider (sha256 provider) working (Some "0.158.0\n")
            hostArguments runner (sha256 runner) provider (sha256 provider) working (Some "0.158")
            hostArguments runner (sha256 runner) provider (sha256 provider) working (Some "v0.158.0")
            hostArguments runner (sha256 runner) provider (sha256 provider) working (Some "00.158.0")
            hostArguments runner (String.replicate 64 "0") provider (sha256 provider) working (Some "0.158.0")
            hostArguments
                runner
                (sha256 runner)
                (Path.Combine(root, "missing"))
                (sha256 provider)
                working
                (Some "0.158.0")
        ] do
        let code, _, _ = run host working arguments None 10000
        Assert.NotEqual(0, code)

    Assert.False(File.Exists(Path.Combine(root, "calls")))

[<Fact>]
let ``diagnostic runner rejects a non-diagnostic frame before provider`` () =
    let root = Directory.CreateTempSubdirectory("runner-diagnostic-frame-").FullName
    let provider = sentinel root "0.158.0"

    let payload =
        Text.Encoding.UTF8.GetBytes("{\"schema\":\"fsgg.orchestration.executor-command/2\"}")

    let frame = Array.zeroCreate<byte>(payload.Length + 4)
    Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length)
    payload.CopyTo(frame, 4)

    let arguments =
        [
            "diagnostic-stdio"
            "--provider-executable"
            provider
            "--provider-sha256"
            sha256 provider
            "--expected-codex-version"
            "0.158.0"
            "--timeout-seconds"
            "2"
        ]

    let code, _, _ = run runner root arguments (Some frame) 10000
    Assert.NotEqual(0, code)
    Assert.False(File.Exists(Path.Combine(root, "calls")))

[<Fact>]
let ``served Host bounds hostile runner output and terminates a hung child`` () =
    let root =
        Directory.CreateTempSubdirectory("served-compatibility-hostile-").FullName

    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName
    let provider = sentinel root "0.158.0"

    let hostile =
        script root "hostile-runner" $"printf '%%s' $$ > '{root}/pid'; printf '\\000\\000\\100\\001'; sleep 30"

    let arguments =
        hostArguments hostile (sha256 hostile) provider (sha256 provider) working (Some "0.158.0")

    let stopwatch = Stopwatch.StartNew()

    let code, _, error =
        run host working ((arguments |> List.take (arguments.Length - 1)) @ [ "1" ]) None 10000

    stopwatch.Stop()
    Assert.NotEqual(0, code)
    Assert.Contains("compatibility-response-frame-refused", error)
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds 5.)
    let pid = File.ReadAllText(Path.Combine(root, "pid"))
    Assert.False(Directory.Exists("/proc/" + pid))
    Assert.False(File.Exists(Path.Combine(root, "calls")))

    let hung =
        script root "hung-runner" $"printf '%%s' $$ > '{root}/hung-pid'; sleep 30"

    let hungArguments =
        hostArguments hung (sha256 hung) provider (sha256 provider) working (Some "0.158.0")

    let code, _, error =
        run host working ((hungArguments |> List.take (hungArguments.Length - 1)) @ [ "1" ]) None 10000

    Assert.NotEqual(0, code)
    Assert.Contains("compatibility-diagnostic-timeout", error)
    let hungPid = File.ReadAllText(Path.Combine(root, "hung-pid"))
    Assert.False(Directory.Exists("/proc/" + hungPid))

[<Fact>]
let ``served Host refuses stale malformed and extra diagnostic responses`` () =
    let root =
        Directory.CreateTempSubdirectory("served-compatibility-response-").FullName

    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName
    let provider = sentinel root "0.158.0"
    let providerHash = sha256 provider

    let python mode =
        let body =
            $"""#!/usr/bin/env python3
import json, struct, sys
size = struct.unpack('>I', sys.stdin.buffer.read(4))[0]
request = json.loads(sys.stdin.buffer.read(size))
if '{mode}' == 'malformed':
    sys.stdout.buffer.write(struct.pack('>I', 2) + b'x')
    sys.exit(0)
response = dict(
    schema='fsgg.orchestration.compatibility-diagnostic-response/1',
    correlationId='10000000-0000-0000-0000-000000000001' if '{mode}' == 'stale' else request['correlationId'],
    scope='compatibility-diagnostic-only', provider='Codex', adapterVersion='codex-subscription-exec/1',
    versionState='matched', observedVersion='codex-cli 0.158.0', authenticationState='authenticated',
    authenticationProvenance='fixture', supportsResume=True, observedAt='2026-09-29T00:00:00+00:00',
    providerExecutableSha256='{providerHash}'
)
encoded = json.dumps(response, separators=(',', ':')).encode()
sys.stdout.buffer.write(struct.pack('>I', len(encoded)) + encoded)
if '{mode}' == 'extra': sys.stdout.buffer.write(b'x')
"""

        rawScript root ("runner-" + mode) body

    for mode in [ "stale"; "malformed"; "extra" ] do
        let fakeRunner = python mode

        let code, _, _ =
            run
                host
                working
                (hostArguments fakeRunner (sha256 fakeRunner) provider providerHash working (Some "0.158.0"))
                None
                10000

        Assert.NotEqual(0, code)

    Assert.False(File.Exists(Path.Combine(root, "calls")))

[<Fact>]
let ``served Host reports an unsupported old runner without execution fallback`` () =
    let root =
        Directory.CreateTempSubdirectory("served-compatibility-old-runner-").FullName

    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName
    let provider = sentinel root "0.158.0"

    let oldRunner =
        script root "old-runner" $"""printf '%%s' "$1" > '{root}/mode'; exit 2"""

    let code, _, error =
        run
            host
            working
            (hostArguments oldRunner (sha256 oldRunner) provider (sha256 provider) working (Some "0.158.0"))
            None
            10000

    Assert.NotEqual(0, code)
    Assert.Contains("compatibility-runner-unsupported", error)
    Assert.Equal("diagnostic-stdio", File.ReadAllText(Path.Combine(root, "mode")))
    Assert.False(File.Exists(Path.Combine(root, "calls")))

[<Fact>]
let ``provider byte drift refuses after the approved probes`` () =
    let root =
        Directory.CreateTempSubdirectory("served-compatibility-provider-drift-").FullName

    let working = Directory.CreateDirectory(Path.Combine(root, "working")).FullName

    let provider =
        script
            root
            "changing-provider"
            $"""printf '%%s\n' "$*" >> '{root}/calls'
if [ "$1" = --version ]; then printf '# changed\n' >> "$0"; printf 'codex-cli 0.158.0\n'; exit 0; fi
if [ "$*" = "login status" ]; then printf 'Logged in using ChatGPT\n'; exit 0; fi
exit 91"""

    let originalHash = sha256 provider

    let code, _, _ =
        run
            host
            working
            (hostArguments runner (sha256 runner) provider originalHash working (Some "0.158.0"))
            None
            10000

    Assert.NotEqual(0, code)
    Assert.True(originalHash <> sha256 provider)

    Assert.Equal<string list>(
        [ "--version"; "login status" ],
        File.ReadAllLines(Path.Combine(root, "calls")) |> Array.toList
    )
