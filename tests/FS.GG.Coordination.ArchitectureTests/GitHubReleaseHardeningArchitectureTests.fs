module FS.GG.Coordination.GitHubReleaseHardeningArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Xml.Linq
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private shaFile path = File.ReadAllBytes(path) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

let private tracked paths =
    let startInfo = ProcessStartInfo("git")
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    for argument in [ "ls-files"; "--error-unmatch"; "--" ] @ paths do startInfo.ArgumentList.Add(argument)
    use child = Process.Start(startInfo)
    let output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output

[<Fact>]
let ``release hardening public surface is pure and mutation free`` () =
    let surface = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubReleaseHardeningQualification.fsi"
    for required in [ "type GitHubReleaseHardeningSnapshot"; "type GitHubReleaseHardeningReport"; "val compile"; "val verify" ] do Assert.Contains(required, surface)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "val publish"; "PATCH"; "POST"; "DELETE" ] do Assert.DoesNotContain(forbidden, surface)

[<Fact>]
let ``retained release corpus carries exact hardening inventory`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-6/corpus.json")
    let value = corpus.RootElement
    Assert.True(value.GetProperty("complete").GetBoolean())
    Assert.True(value.GetProperty("environmentProtected").GetBoolean())
    Assert.False(value.GetProperty("longLivedCredential").GetBoolean())
    Assert.Equal(1, value.GetProperty("packCount").GetInt32())
    Assert.Equal(11, value.GetProperty("stages").GetArrayLength())
    Assert.Equal(2, value.GetProperty("feedPublications").GetArrayLength())
    Assert.Equal(value.GetProperty("packageSha256").GetString(), value.GetProperty("publicDownloadSha256").GetString())
    use expectations = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-6/independent-expectations.json")
    Assert.Equal(21, expectations.RootElement.GetProperty("independentCases").GetArrayLength())
    Assert.Equal(5, expectations.RootElement.GetProperty("shapeCases").GetArrayLength())

[<Fact>]
let ``roadmap and gate catalog register exact GS2-06.6 command`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use catalog = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-06.6")
    let command = catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-release-hardening-contract")
    let components = command.GetProperty("executable").GetString() :: (command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq)
    let gateDigest = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString()
    let prerequisite = unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.exactlyOne |> _.GetString()
    Assert.Equal(components |> String.concat "\u0000" |> sha, gateDigest)
    Assert.Equal("GS2-06.5", prerequisite)

[<Fact>]
let ``exact release hardening Q3 validator passes`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; "eng/validate-github-release-hardening.fsx"; "--"; "." ] do startInfo.ArgumentList.Add(argument)
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start(startInfo)
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, error)
    Assert.Contains("GITHUB_RELEASE_HARDENING_OK", output)

[<Fact>]
let ``release hardening corpus rejects unknown properties at every object boundary`` () =
    let validator = read "eng/validate-github-release-hardening.fsx"
    for required in [ "corpus-top-level-extra"; "feed-publication-extra"; "recovery-extra"; "expectations-top-level-extra"; "independent-case-extra" ] do
        Assert.Contains(required, validator)
    Assert.Contains("unknown-property fail-closed self-test failed", validator)

[<Fact>]
let ``accepted release hardening provider evidence is durable in the candidate Git tree`` () =
    let analysis = "readiness/254-release-hardening/analysis.json"
    let qualification = "artifacts/test-results/254-release-hardening/qualification.trx"
    let workModel = "readiness/254-release-hardening/work-model.json"
    let verification = "readiness/254-release-hardening/verify.json"
    let paths = [ analysis; qualification; workModel; verification ]
    let code, output = tracked paths
    if code <> 0 then failwith output

    let expected =
        [ analysis, "7cc01dce90044e0b765a77d86feb8fa869441ceeba81b94ed15aa1843307dd80"
          qualification, "892127d103cd4cad4e6f253ba8363bbdf6be29df57a0b5975583d798bc20ce7d"
          workModel, "21cdd50ce4cc4c68ee83798b42dca65331c61e4ef3ef08061c951f39c7ef0a68"
          verification, "0c78fccbe4c2a6519b896f214a324dd0dbc1fd4f9f4bc3b7cba5fef7dd09d11b" ]
    for relative, digest in expected do
        let path = Path.Combine(root, relative)
        Assert.True(File.Exists(path), $"provider evidence is absent: {relative}")
        Assert.Equal(digest, shaFile(path))

    let evidence = read "work/254-release-hardening/evidence.yml"
    Assert.Contains($"path: {analysis}", evidence)
    Assert.Equal(10, evidence.Split($"source: {qualification}", StringSplitOptions.None).Length - 1)
    Assert.Equal(10, evidence.Split("sha256:892127d103cd4cad4e6f253ba8363bbdf6be29df57a0b5975583d798bc20ce7d", StringSplitOptions.None).Length - 1)
    let verify = read verification
    Assert.Contains($"\"path\": \"{workModel}\"", verify)
    Assert.Contains("21cdd50ce4cc4c68ee83798b42dca65331c61e4ef3ef08061c951f39c7ef0a68", verify)

    let results =
        XDocument.Load(Path.Combine(root, qualification)).Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = "UnitTestResult")
        |> Seq.map (fun element -> element.Attribute(XName.Get("outcome")).Value)
        |> Seq.countBy id
        |> Map.ofSeq
    Assert.Equal(182, results |> Map.tryFind "Passed" |> Option.defaultValue 0)
    Assert.Equal(0, results |> Map.tryFind "Failed" |> Option.defaultValue 0)
    Assert.Equal(0, results |> Map.tryFind "NotExecuted" |> Option.defaultValue 0)
