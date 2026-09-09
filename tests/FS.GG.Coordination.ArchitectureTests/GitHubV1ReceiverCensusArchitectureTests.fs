module FS.GG.Coordination.GitHubV1ReceiverCensusArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private digest path = File.ReadAllBytes(Path.Combine(root, path)) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``receiver census qualification is pure source evidence and cannot apply`` () =
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubV1ReceiverCensusQualification.fsi"
    let implementation = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubV1ReceiverCensusQualification.fs"
    for required in [ "CompleteReceiverRoster"; "DelegatedWriterClosure"; "LegacyToolCorrespondence"; "NoInstallationClaim"; "NoFenceClaim"; "NoAcceptanceClaim" ] do
        Assert.Contains(required, signature)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Process.Start"; "ApplyAuthorized"; "val apply"; "let apply" ] do
        Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``retained landed producer evidence is byte exact`` () =
    use binding = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-3/receiver-source/source-binding.json")
    let value = binding.RootElement
    Assert.Equal("3719b6cfc6f2d766ad56f930b56f025e20c4b2cc", value.GetProperty("producerLandedRevision").GetString())
    Assert.Equal("d20205d921f5ce128385d2a9ba6361c47d3f9a49", value.GetProperty("producerTree").GetString())
    Assert.Equal(value.GetProperty("censusSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-v1-writer-receiver-census.json")
    Assert.Equal(value.GetProperty("sourceManifestsSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-manifests.json.gz")
    Assert.Equal(value.GetProperty("sourceBlobsSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-source-blobs.json.gz")

[<Fact>]
let ``independent Q3 validator closes receiver-source controls`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-v1-receiver-census.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, error)
    Assert.Contains("receivers=7 sources=555 blobs=460 routes=615 dependencies=95 controls=22", output)
    Assert.Equal("", error)

[<Fact>]
let ``GS2-08-3 registration adds receiver census Q3 without changing prerequisite`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.3")
    Assert.Equal<string list>([ "GS2-08.1" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>([ "github-v1-writer-census-contract"; "github-v1-receiver-census-contract" ], unitValue.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let gateContract = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-receiver-census-contract")
    Assert.Equal("fbff7ac12feadedf1fc5a440848bfecfd4ac799e1c8bc45cc15fed666b68d0e9", gateContract.GetProperty("commandSha256").GetString())
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-receiver-census-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-v1-receiver-census.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.False(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.3.json")))
