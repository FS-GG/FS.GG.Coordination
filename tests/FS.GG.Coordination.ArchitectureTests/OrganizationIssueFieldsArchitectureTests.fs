module FS.GG.Coordination.OrganizationIssueFieldsArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``organization issue field contract stays pure and signature first`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Core/FS.GG.Coordination.Core.fsproj"))
    Assert.True(project.IndexOf("OrganizationIssueFields.fsi", StringComparison.Ordinal) < project.IndexOf("OrganizationIssueFields.fs\"", StringComparison.Ordinal))
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Core/OrganizationIssueFields.fs"))
    for forbidden in [ "Octokit"; "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "GraphQL"; "FS.GG.Coordination.GitHub" ] do
        Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``frozen organization field corpus and independent expectations remain separate`` () =
    use corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/gs2-05-2/corpus.json")))
    use expectations = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/gs2-05-2/independent-expectations.json")))
    Assert.Equal("fsgg.github-substrate-v2.organization-issue-fields-corpus/1", corpus.RootElement.GetProperty("schema").GetString())
    Assert.Equal(12, corpus.RootElement.GetProperty("fieldDefinitions").GetArrayLength())
    Assert.Equal(4, corpus.RootElement.GetProperty("cases").GetArrayLength())
    Assert.Equal("fsgg.github-substrate-v2.organization-issue-fields-expectations/1", expectations.RootElement.GetProperty("schema").GetString())
    Assert.Equal(36, expectations.RootElement.GetProperty("requiredRefusalCodes").GetArrayLength())

[<Fact>]
let ``registered organization field validator is offline and model tests are separate`` () =
    let validator = File.ReadAllText(Path.Combine(root, "eng/validate-github-organization-issue-fields.fsx"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "Environment.GetEnvironmentVariable"; "gh api" ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("OIF-FIELD-OMISSION-INVERSION", validator, StringComparison.Ordinal)
    Assert.Contains("independentRefusals", validator, StringComparison.Ordinal)
    Assert.True(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/gs2-05-2/organizationIssueFields.quint")))
    Assert.True(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/gs2-05-2/organizationIssueFields_test.quint")))
    let gates = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"id\": \"github-organization-issue-fields-contract\"", gates, StringComparison.Ordinal)
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-organization-issue-fields.fsx\", \"--\", \".\"]", gates, StringComparison.Ordinal)

[<Fact>]
let ``registered organization issue field Q2 command passes local evidence`` () =
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-organization-issue-fields.fsx"; "--"; "." ] do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-organization-issue-fields-contract OK fields=12 cases=4 accepted=4 q=Q2 network=offline inversions=38", output, StringComparison.Ordinal)
