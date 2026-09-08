module FS.GG.Coordination.GitHubEpochWireArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))
let private read relative = File.ReadAllText(Path.Combine(root, relative))
let private run executable arguments =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- root; info.UseShellExecute <- false
    info.RedirectStandardOutput <- true; info.RedirectStandardError <- true
    for argument in arguments do info.ArgumentList.Add argument
    use child = Process.Start info
    let output, error = child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()
    child.WaitForExit(); child.ExitCode, output, error

[<Fact>]
let ``wire qualification has no network writer or orchestration service`` () =
    let source =
        read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEpochWireQualification.fs"
        + read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEpochWireQualification.fsi"
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "getenvironmentvariable"; "listener" ] do
        Assert.DoesNotContain(forbidden, source.ToLowerInvariant())
    Assert.DoesNotContain("epoch-wire", read "src/FS.GG.Coordination.Cli/Program.fs", StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``generated binding carries the frozen wire identity`` () =
    let binding = read "src/FS.GG.Coordination.Protocol/Generated/Protocol.Generated.fs"
    Assert.Contains("fsgg.github-substrate.epoch-wire/1", binding)
    Assert.Contains("fleet-cutover:fs-gg-production", binding)
    Assert.Contains("OpenV2>ObservingV2>ContractingV1>OperatingV2", binding)

[<Fact>]
let ``generated and independently authored controls qualify the same contract`` () =
    let code, output, error = run "dotnet" [ "fsi"; "eng/validate-github-epoch-wire.fsx"; "--"; root ]
    Assert.Equal(0, code)
    Assert.Equal("", error.Trim())
    Assert.Contains("GITHUB_EPOCH_WIRE_OK", output)
    use generated = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-1/generated-controls.json")
    use independent = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-1/independent-controls.json")
    Assert.Equal(generated.RootElement.GetProperty("controls").GetRawText(), independent.RootElement.GetProperty("controls").GetRawText())
    Assert.NotEqual(generated.RootElement.GetProperty("cases").GetRawText(), independent.RootElement.GetProperty("cases").GetRawText())
