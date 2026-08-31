module FS.GG.Coordination.GitHubTransportArchitectureTests

open System
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``transport and qualification signatures precede implementations`` () =
    let githubProject = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/FS.GG.Coordination.GitHub.fsproj"))
    Assert.True(githubProject.IndexOf("Transport.fsi", StringComparison.Ordinal) < githubProject.IndexOf("Transport.fs\"", StringComparison.Ordinal))
    let qualificationProject = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"))
    Assert.True(qualificationProject.IndexOf("GitHubTransportQualification.fsi", StringComparison.Ordinal) < qualificationProject.IndexOf("GitHubTransportQualification.fs\"", StringComparison.Ordinal))

[<Fact>]
let ``independent control inventory is closed and mutations really red`` () =
    let independentInventory =
        [ Truncation; UnsafeReplay; StaleRevision; RateExhaustion; IncompletePagination; RedactionLeakage; AmbiguousMapping ]
    Assert.Equal<GitHubTransportControl list>(GitHubTransportQualification.requiredControls, independentInventory)
    let generated = independentInventory |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    let independent = independentInventory |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubTransportQualification.validate generated independent)
    let broken = independent |> List.mapi (fun index result -> if index = 0 then { result with MutationRed = false } else result)
    match GitHubTransportQualification.validate generated broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GTQ-INDEPENDENT-NOT-RED" && finding.ControlId = "truncation")
    | Ok () -> failwith "an independent mutation that stayed green was accepted"

[<Fact>]
let ``Q3 fixture is canonical closed and contains no real GitHub endpoint`` () =
    let path = Path.Combine(root, "tests/fixtures/github-transport/contract.json")
    let bytes = File.ReadAllBytes path
    Assert.Equal(byte '\n', Array.last bytes)
    use document = JsonDocument.Parse bytes
    let controls = document.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(GitHubTransportQualification.requiredControls |> List.map GitHubTransportQualification.controlId, controls)
    let text = Text.Encoding.UTF8.GetString bytes
    Assert.DoesNotContain("api.github.com", text, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("github.com/graphql", text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``registered Q3 validator is offline and runs both producers`` () =
    let validator = File.ReadAllText(Path.Combine(root, "eng/validate-github-transport.fsx"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Environment.GetEnvironmentVariable" ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("generatedResults", validator, StringComparison.Ordinal)
    Assert.Contains("independentResults", validator, StringComparison.Ordinal)
    Assert.Contains("tests/fixtures/github-transport/contract.json", validator, StringComparison.Ordinal)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-transport.fsx\", \"--\", \".\"]", catalog, StringComparison.Ordinal)

[<Fact>]
let ``qualification contracts do not invert the production dependency graph`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj"))
    Assert.DoesNotContain("FS.GG.Coordination.GitHub.fsproj", project, StringComparison.Ordinal)
