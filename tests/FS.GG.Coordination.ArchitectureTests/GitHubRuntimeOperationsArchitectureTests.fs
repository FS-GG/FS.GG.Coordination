module FS.GG.Coordination.GitHubRuntimeOperationsArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))
let private read relative = File.ReadAllText(Path.Combine(root, relative))
let private run executable arguments =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in arguments do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

[<Fact>]
let ``evaluated App project remains an inert non-deployable library`` () =
    let code, output, error =
        run "dotnet" [ "msbuild"; "src/FS.GG.Coordination.App/FS.GG.Coordination.App.fsproj"
                       "-getProperty:OutputType"; "-getProperty:IsPackable"; "-getProperty:PublishProfile"
                       "-getProperty:RuntimeIdentifier"; "-getProperty:SelfContained" ]
    Assert.Equal(0, code)
    Assert.Equal("", error.Trim())
    use result = JsonDocument.Parse output
    let properties = result.RootElement.GetProperty("Properties")
    Assert.Equal("Library", properties.GetProperty("OutputType").GetString())
    Assert.Equal("false", properties.GetProperty("IsPackable").GetString())
    Assert.Equal("", properties.GetProperty("PublishProfile").GetString())
    Assert.Equal("", properties.GetProperty("RuntimeIdentifier").GetString())
    Assert.Equal("", properties.GetProperty("SelfContained").GetString())
    let boundary = read "src/FS.GG.Coordination.App/Library.fs"
    Assert.Contains("Listening = false", boundary)
    Assert.Contains("DeploymentConfigured = false", boundary)
    Assert.Contains("ProductionAuthority = false", boundary)

[<Fact>]
let ``qualification adds no callable production command or network host`` () =
    let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubRuntimeOperationsQualification.fs"
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubRuntimeOperationsQualification.fsi"
    let program = read "src/FS.GG.Coordination.Cli/Program.fs"
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "getenvironmentvariable"; "webhooklistener" ] do
        Assert.DoesNotContain(forbidden, (source + signature).ToLowerInvariant())
    Assert.DoesNotContain("runtime-operations", program, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``Q3 and Q6 commands execute the retained qualification`` () =
    let q3, q3out, q3err = run "dotnet" [ "fsi"; "eng/validate-github-runtime-operations.fsx"; "--"; root ]
    Assert.Equal(0, q3)
    Assert.Equal("", q3err.Trim())
    Assert.Contains("GITHUB_RUNTIME_OPERATIONS_OK", q3out)
    let q6, q6out, q6err = run "dotnet" [ "fsi"; "eng/validate-github-runtime-recovery.fsx"; "--"; root; "--skip-cold" ]
    Assert.Equal(0, q6)
    Assert.Equal("", q6err.Trim())
    Assert.Contains("GITHUB_RUNTIME_RECOVERY_OK", q6out)

[<Fact>]
let ``retained evidence records limitations and independent controls`` () =
    use report = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-8/qualification-report.json")
    Assert.Equal("no-host-scheduled-audit-authoritative", report.RootElement.GetProperty("runtimeDisposition").GetString())
    Assert.False(report.RootElement.GetProperty("productionV2").GetBoolean())
    Assert.False(report.RootElement.GetProperty("installedAuditExecution").GetBoolean())
    Assert.False(report.RootElement.GetProperty("pollingReduced").GetBoolean())
    use generated = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-8/generated-controls.json")
    use independent = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-8/independent-controls.json")
    let controls (document: JsonDocument) = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubRuntimeOperationsQualification.requiredControls, controls generated)
    Assert.Equal<string list>(GitHubRuntimeOperationsQualification.requiredControls, controls independent)
    Assert.NotEqual(generated.RootElement.GetProperty("cases").GetRawText(), independent.RootElement.GetProperty("cases").GetRawText())
