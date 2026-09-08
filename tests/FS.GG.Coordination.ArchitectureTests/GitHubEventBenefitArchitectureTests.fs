module FS.GG.Coordination.GitHubEventBenefitArchitectureTests

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
let private run script =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in [ "fsi"; script; "--"; root ] do info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

[<Fact>]
let ``event benefit surface is additive pure and read only`` () =
    let source = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventBenefitQualification.fs"
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubEventBenefitQualification.fsi"
    let program = read "src/FS.GG.Coordination.Cli/Program.fs"
    for forbidden in [ "httpclient"; "webrequest"; "octokit"; "githubclient"; "getenvironmentvariable"; "queueclient"; "webhook" ] do
        Assert.DoesNotContain(forbidden, (source + signature).ToLowerInvariant())
    Assert.DoesNotContain("event-benefit", program, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("GS2-07.8", source + signature + program, StringComparison.Ordinal)

[<Fact>]
let ``retained measurement records all required controls categories and limits`` () =
    use generated = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-7/generated-controls.json")
    use independent = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-7/independent-controls.json")
    let values (name: string) (document: JsonDocument) : string list = document.RootElement.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubEventBenefitQualification.requiredControls, values "controls" generated)
    Assert.Equal<string list>(GitHubEventBenefitQualification.requiredControls, values "controls" independent)
    Assert.True(values "cases" generated <> values "cases" independent)
    use report = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-07-7/measurement-report.json")
    let rootNode = report.RootElement
    Assert.Equal(5, rootNode.GetProperty("pageCount").GetInt32())
    Assert.Equal(2, rootNode.GetProperty("runAttemptCount").GetInt32())
    Assert.False(rootNode.GetProperty("installedBenefit").GetBoolean())
    Assert.False(rootNode.GetProperty("productionBenefit").GetBoolean())
    Assert.Equal("retain", rootNode.GetProperty("pollingDecision").GetString())
    Assert.Contains(rootNode.GetProperty("limits").EnumerateArray() |> Seq.map _.GetString(), fun value -> value = "section-7.4 attribution incomplete")

[<Fact>]
let ``Q3 measurement executes positive and adversarial controls`` () =
    let code, output, error = run "eng/validate-github-event-benefit-measurement.fsx"
    Assert.Equal(0, code)
    Assert.Equal("", error.Trim())
    Assert.Contains("GITHUB_EVENT_BENEFIT_MEASUREMENT_OK sources=5 hints=9 subjects=3 schedules=1 narrowCalls=2 collectorCalls=3 controls=32", output)

[<Fact>]
let ``Q4 provider observation proves complete read only census`` () =
    let code, output, error = run "eng/validate-github-event-benefit-provider-observation.fsx"
    Assert.Equal(0, code)
    Assert.Equal("", error.Trim())
    Assert.Contains("GITHUB_EVENT_BENEFIT_PROVIDER_OBSERVATION_OK pages=2 runs=2 attempts=2 calls=2 writes=0", output)

[<Fact>]
let ``registered command identities remain exact`` () =
    use catalog = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let commands = catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList
    let command id = commands |> List.find (fun value -> value.GetProperty("id").GetString() = id)
    let q3 = command "github-event-benefit-measurement-contract"
    let q4 = command "github-event-benefit-provider-observation-contract"
    Assert.Equal("Q3", q3.GetProperty("qGate").GetString())
    Assert.Equal("Q4", q4.GetProperty("qGate").GetString())
    Assert.Equal("eng/validate-github-event-benefit-measurement.fsx", (q3.GetProperty("args").EnumerateArray() |> Seq.item 1).GetString())
    Assert.Equal("eng/validate-github-event-benefit-provider-observation.fsx", (q4.GetProperty("args").EnumerateArray() |> Seq.item 1).GetString())
