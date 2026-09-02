module FS.GG.Coordination.GitHubImmutableExecutionPinsArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha256Text (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private runAt workingDirectory executable arguments =
    let startInfo = ProcessStartInfo(executable)
    for argument in arguments do startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- workingDirectory
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

[<Fact>]
let ``immutable execution pin contract is offline and has no apply or publication path`` () =
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubImmutableExecutionPinsQualification.fsi"
    let implementation = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubImmutableExecutionPinsQualification.fs"
    for required in [ "type ImmutableExecutionPinsSnapshot"; "type ImmutableWorkflowPublication"; "type ImmutablePinUpdaterAuthority"; "val compile"; "val verify" ] do
        Assert.Contains(required, signature)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "let apply"; "val publish"; "let publish"; "PATCH"; "POST"; "DELETE" ] do
        Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``retained workflow corpus is complete full SHA pinned and Renovate only`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-4/corpus.json")
    use expected = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-4/independent-expectations.json")
    Assert.True(corpus.RootElement.GetProperty("complete").GetBoolean())
    Assert.Equal(2, corpus.RootElement.GetProperty("workflows").GetArrayLength())
    Assert.Equal(0, corpus.RootElement.GetProperty("publications").GetArrayLength())
    Assert.Equal(15, corpus.RootElement.GetProperty("updaterConfigurationPaths").GetArrayLength())
    Assert.Equal(11, corpus.RootElement.GetProperty("updaterInvocationSelectors").GetArrayLength())
    Assert.Equal(0, corpus.RootElement.GetProperty("updaterConfigurations").GetArrayLength())
    let updaters = corpus.RootElement.GetProperty("updaters")
    Assert.Equal(1, updaters.GetArrayLength())
    Assert.Equal("renovate", updaters[0].GetProperty("name").GetString())
    Assert.True(updaters[0].GetProperty("pullRequestOnly").GetBoolean())
    Assert.False(updaters[0].GetProperty("directPush").GetBoolean())
    for workflow in corpus.RootElement.GetProperty("workflows").EnumerateArray() do
        for reference in workflow.GetProperty("references").EnumerateArray() do
            let revision = reference.GetProperty("revision").GetString()
            Assert.Equal(40, revision.Length)
            Assert.True(revision |> Seq.forall Uri.IsHexDigit)
    Assert.Equal(20, expected.RootElement.GetProperty("controls").GetArrayLength())
    Assert.Contains(
        "local-execution-reference-rejection",
        expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString())
    Assert.Equal(13, expected.RootElement.GetProperty("officialRenovatePaths").GetArrayLength())
    Assert.Equal(11, expected.RootElement.GetProperty("invocationSelectors").GetArrayLength())

[<Fact>]
let ``validator classifies and refuses repository-local execution literals`` () =
    let validator = read "eng/validate-github-immutable-execution-pins.fsx"
    Assert.Contains("classifyReferenceLiteral target", validator)
    Assert.DoesNotContain("if not (target.StartsWith(\"./\"))", validator)
    for literal in [ "./.github/workflows/reusable.yml"; "./local-action"; "./local-action@0000000000000000000000000000000000000000" ] do
        Assert.Equal(
            Error [ LocalExecutionReferenceNotImmutable ],
            GitHubImmutableExecutionPinsQualification.classifyReferenceLiteral literal)

[<Fact>]
let ``production Q3 rejects every competing or alternate updater configuration route`` () =
    let cases =
        [ "dependabot", [ ".github/dependabot.yml", "version: 2\nupdates:\n  - package-ecosystem: github-actions\n    directory: /\n    schedule:\n      interval: weekly\n" ]
          "root-jsonc", [ "renovate.jsonc", "{ \"enabledManagers\": [\"github-actions\"], \"automerge\": true, \"automergeType\": \"branch\" }\n" ]
          "github-jsonc", [ ".github/renovate.jsonc", "{ \"enabledManagers\": [\"github-actions\"] }\n" ]
          "rc-jsonc", [ ".renovaterc.jsonc", "{ \"enabledManagers\": [\"github-actions\"] }\n" ]
          "package-json", [ "package.json", "{ \"name\": \"mutation\", \"renovate\": { \"enabledManagers\": [\"github-actions\"] } }\n" ]
          "custom-selector",
              [ "scripts/run-custom-renovate.sh", "#!/usr/bin/env bash\nrenovate --config-file custom-renovate.json\n"
                "custom-renovate.json", "{ \"enabledManagers\": [\"github-actions\"] }\n" ]
          "custom-filenames",
              [ "scripts/run-custom-filenames.sh", "#!/usr/bin/env bash\nRENOVATE_CONFIG_FILE_NAMES=custom-renovate.json renovate\n"
                "custom-renovate.json", "{ \"enabledManagers\": [\"github-actions\"] }\n" ]
          "inline-config",
              [ "scripts/run-inline-renovate.sh", "#!/usr/bin/env bash\nRENOVATE_CONFIG='{\"enabledManagers\":[\"github-actions\"],\"automerge\":true,\"automergeType\":\"branch\"}' renovate\n" ]
          "per-option-env",
              [ "run-renovate.sh", "#!/usr/bin/env bash\nRENOVATE_AUTOMERGE=true RENOVATE_AUTOMERGE_TYPE=branch RENOVATE_ENABLED_MANAGERS=github-actions renovate\n" ]
          "cli-options",
              [ "scripts/run-renovate-cli.sh", "#!/usr/bin/env bash\nrenovate --enabled-managers=github-actions --automerge=true --automerge-type=branch\n" ]
          "container-cli",
              [ "scripts/run-renovate-container.sh", "#!/usr/bin/env bash\ndocker run --rm renovate/renovate --automerge=true --automerge-type=branch\n" ] ]
    for label, files in cases do
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fsgg-gs2-06-4-{label}-{Guid.NewGuid():N}")
        try
            let cloneExit, _, cloneError = runAt root "git" [ "clone"; "--quiet"; "--no-hardlinks"; root; tempRoot ]
            Assert.True((cloneExit = 0), cloneError)
            for path, content in files do
                let fullPath = Path.Combine(tempRoot, path)
                Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
                File.WriteAllText(fullPath, content)
            let paths = files |> List.map fst
            let addExit, _, addError = runAt tempRoot "git" ([ "add"; "--" ] @ paths)
            Assert.True((addExit = 0), addError)
            let exitCode, output, error =
                runAt tempRoot "dotnet" [ "fsi"; "eng/validate-github-immutable-execution-pins.fsx"; "--"; tempRoot ]
            Assert.True(exitCode <> 0, $"{label}: production Q3 unexpectedly passed: {output}{error}")
            Assert.Contains("updater configuration inventory differs", output + error)
        finally
            if Directory.Exists tempRoot then Directory.Delete(tempRoot, true)

[<Fact>]
let ``GS2-06-4 registration binds accepted predecessor and exact Q3 gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-06.4")
    Assert.Equal<string list>([ "GS2-06.3" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("efcb0f9fcc9bee8572f1768857b079ce714caa64fbd3aab06e4e31d74f9d6036", unitValue.GetProperty("contractSha256").GetString())
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-immutable-execution-pins-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-immutable-execution-pins.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(components |> String.concat "\u0000" |> sha256Text, unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString())
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-06.3.json")
    Assert.Equal("eec15747e2e5c1cf0ae91fbf370eb82a3e6ea88d6fe3c0f2f738a556e63e5063", receipt.RootElement.GetProperty("digest").GetString())

[<Fact>]
let ``immutable execution pin Q3 validator rejects the closed mutation inventory`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; "eng/validate-github-immutable-execution-pins.fsx" ] do startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, $"immutable execution pin validator failed with exit code {child.ExitCode}: {error}{output}")
    Assert.Contains("GITHUB_IMMUTABLE_EXECUTION_PINS_OK workflows=2 references=7 publications=0 updaterConfigurations=0 updater=renovate controls=20 seal=a3aa9af6c20c035ceda40d638757c82adf98a712dc28b7d713aebc13c5f1e936", output)
    Assert.Equal("", error)

[<Fact>]
let ``immutable execution pins preserve canonical Quint source`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))
