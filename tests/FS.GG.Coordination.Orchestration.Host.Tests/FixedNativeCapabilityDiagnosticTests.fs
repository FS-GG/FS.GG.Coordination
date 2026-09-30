module FS.GG.Coordination.Orchestration.Host.Tests.FixedNativeCapabilityDiagnosticTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Orchestration.Host
open Xunit

let private sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()

let private script root body =
    let path = Path.Combine(root, "codex-native-sentinel")
    File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n")
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    path

let private appServer scenario =
    match scenario with
    | "supported" ->
        """IFS= read -r initialize
printf '%s\n' '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}'
IFS= read -r initialized
IFS= read -r request
printf '%s\n' '{"id":2,"result":{"data":[{"model":"gpt-5.6-sol","supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"fixture"}]}],"nextCursor":null}}'"""
    | "unsupported" ->
        """IFS= read -r initialize
printf '%s\n' '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}'
IFS= read -r initialized
IFS= read -r request
printf '%s\n' '{"id":2,"result":{"data":[{"model":"different","supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"fixture"}]}],"nextCursor":null}}'"""
    | "duplicate" ->
        """IFS= read -r initialize
printf '%s\n' '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}'
IFS= read -r initialized
IFS= read -r request
printf '%s\n' '{"id":2,"result":{"data":[],"nextCursor":"again"}}'
IFS= read -r request
printf '%s\n' '{"id":3,"result":{"data":[],"nextCursor":"again"}}'"""
    | "incomplete" ->
        """IFS= read -r initialize
printf '%s\n' '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}'
IFS= read -r initialized
page=2
while [ "$page" -le 9 ]; do
  IFS= read -r request
  printf '{"id":%s,"result":{"data":[{"model":"different-%s","supportedReasoningEfforts":[]}],"nextCursor":"page-%s"}}\n' "$page" "$page" "$page"
  page=$((page+1))
done"""
    | "timeout" ->
        """IFS= read -r initialize
printf '%s\n' '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}'
IFS= read -r initialized
IFS= read -r request
sleep 10"""
    | "drift" ->
        """IFS= read -r initialize
printf '%s\n' '{"id":1,"result":{"userAgent":"fixture","platformFamily":"unix","platformOs":"linux"}}'
IFS= read -r initialized
IFS= read -r request
printf '%s\n' '{"id":2,"result":{"data":[{"model":"gpt-5.6-sol","supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"fixture"}]}],"nextCursor":null}}'
printf '%s\n' '# drift' >> "$0"; true"""
    | value -> failwithf "unknown scenario %s" value

let private provider root scenario =
    let login =
        if scenario = "authentication" then
            "printf '%s\\n' 'Not logged in'; exit 1"
        else
            "printf '%s\\n' 'Logged in using ChatGPT'; exit 0"

    let native = if scenario = "authentication" then "exit 92" else appServer scenario

    script root $"""printf '%%s\n' "$*" >> '{root}/calls'
printf '%%s\n' "${{FSGG_FORBIDDEN_FIXTURE-unset}}" >> '{root}/environment'
if [ "$1" = --version ]; then printf '%%s\n' 'codex-cli 0.154.0'; exit 0; fi
if [ "$1" = login ]; then {login}; fi
if [ "$1" = app-server ]; then
{native}
  cat >/dev/null
  exit 0
fi
printf '%%s\n' started > '{root}/model-started'
exit 91"""

let private profile root provider runtime =
    let workspace = Path.Combine(root, "owned-workspace")
    let hostPath = Environment.ProcessPath
    let value: FixedNativeCapabilityProfile =
        {
            Schema = FixedNativeCapabilityDiagnostic.profileSchema
            Operation = FixedNativeCapabilityDiagnostic.operation
            Revision = "fixture-review-1"
            HostExecutableSha256 = sha256 hostPath
            ProviderExecutable = provider
            ProviderExecutableSha256 = sha256 provider
            ExpectedAdapterVersion = FixedNativeCapabilityDiagnostic.adapterVersion
            ExpectedCodexVersion = "0.154.0"
            EnvironmentAllowList = [| "PATH" |]
            CredentialScope = FixedNativeCapabilityDiagnostic.credentialScope
            MaximumRuntimeSeconds = runtime
            MaximumStreamBytes = 65536
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 5.
            DisposableWorkspace = workspace
            Cleanup = FixedNativeCapabilityDiagnostic.cleanupKind
        }

    JsonSerializer.SerializeToUtf8Bytes(
        value,
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    ), workspace

let private run scenario runtime =
    task {
        let root = Directory.CreateTempSubdirectory("fixed-native-capability-").FullName
        let executable = provider root scenario
        let bytes, workspace = profile root executable runtime
        let! result = FixedNativeCapabilityDiagnostic.execute bytes
        return root, workspace, result
    }

[<Fact>]
let ``fixed diagnostic observes frozen native support and never launches a model session`` () =
    task {
        let! root, workspace, result = run "supported" 3
        Assert.True(result.Disposition = "advertised-supported", result.Detail)
        Assert.Equal("gpt-5.6-sol", result.RequestedModel)
        Assert.Equal("medium", result.RequestedEffort)
        Assert.Equal(0, result.ModelSessionStarts)
        Assert.False(File.Exists(Path.Combine(root, "model-started")))
        Assert.False(Directory.Exists workspace)
        Assert.All(File.ReadAllLines(Path.Combine(root, "environment")), fun value -> Assert.Equal("unset", value))
        Assert.Equal<string array>(
            [| "--version"; "login status"; "--version"; "app-server --strict-config --stdio" |],
            File.ReadAllLines(Path.Combine(root, "calls"))
        )
    }

[<Fact>]
let ``fixed diagnostic distinguishes advertised unsupported and unavailable authentication`` () =
    task {
        let! unsupportedRoot, _, unsupported = run "unsupported" 3
        Assert.True(unsupported.Disposition = "advertised-unsupported", unsupported.Detail)
        Assert.Equal("requested-model-unsupported", unsupported.Detail)
        Assert.False(File.Exists(Path.Combine(unsupportedRoot, "model-started")))

        let! authenticationRoot, _, authentication = run "authentication" 3
        Assert.Equal("unavailable-authentication", authentication.Disposition)
        Assert.Equal("codex-authentication-not-authenticated", authentication.Detail)
        Assert.Equal<string array>(
            [| "--version"; "login status" |],
            File.ReadAllLines(Path.Combine(authenticationRoot, "calls"))
        )
        Assert.False(File.Exists(Path.Combine(authenticationRoot, "model-started")))
    }

[<Theory>]
[<InlineData("duplicate", "codex-model-list-duplicate-page", 3)>]
[<InlineData("incomplete", "codex-model-list-incomplete", 3)>]
[<InlineData("timeout", "codex-model-list-deadline-exceeded", 1)>]
[<InlineData("drift", "codex-model-list-executable-changed", 3)>]
let ``fixed diagnostic preserves bounded production discovery failures`` scenario expected runtime =
    task {
        let! root, workspace, result = run scenario runtime
        Assert.Equal("unavailable", result.Disposition)
        Assert.Equal(expected, result.Detail)
        Assert.Equal(0, result.ModelSessionStarts)
        Assert.True(result.Cleanup.ProcessTreeTerminated)
        Assert.True(result.Cleanup.WorkspaceRemoved)
        Assert.False(Directory.Exists workspace)
        Assert.False(File.Exists(Path.Combine(root, "model-started")))
    }

[<Fact>]
let ``fixed diagnostic rejects request shaped profile injection before provider`` () =
    task {
        let root = Directory.CreateTempSubdirectory("fixed-native-injection-").FullName
        let executable = provider root "supported"
        let bytes, workspace = profile root executable 3
        let node = JsonNode.Parse bytes |> _.AsObject()
        node["model"] <- "different"
        let! result = FixedNativeCapabilityDiagnostic.execute (JsonSerializer.SerializeToUtf8Bytes node)
        Assert.Equal("refused", result.Disposition)
        Assert.Equal("fixed-native-capability-profile-shape-refused", result.Detail)
        Assert.False(File.Exists(Path.Combine(root, "calls")))
        Assert.False(Directory.Exists workspace)
    }
