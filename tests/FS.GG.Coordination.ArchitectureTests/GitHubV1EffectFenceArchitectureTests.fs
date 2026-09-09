module FS.GG.Coordination.GitHubV1EffectFenceArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private digest path = File.ReadAllBytes(Path.Combine(root, path)) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private sha256Text (value: string) = Text.Encoding.UTF8.GetBytes(value) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``common fence has only injected read and effect boundaries`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/V1EffectFenceAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/V1EffectFenceAdapter.fs"
    for required in [ "FreshEpochReader"; "ReadFresh"; "V1EffectPort"; "ApplyOnce"; "V1RefusedBeforeEffect"; "V1Applied"; "V1ProvenAbsent"; "V1Partial"; "V1Indeterminate" ] do
        Assert.Contains(required, signature)
    for forbidden in [ "HttpClient"; "api.github.com"; "GITHUB_TOKEN"; "Authorization:"; "Octokit"; "Process.Start"; "retry"; "ApplyAuthorized" ] do
        Assert.DoesNotContain(forbidden, signature + implementation, StringComparison.OrdinalIgnoreCase)
    Assert.True(implementation.IndexOf("reader.ReadFresh()", StringComparison.Ordinal) < implementation.IndexOf("effect.ApplyOnce request", StringComparison.Ordinal))

[<Fact>]
let ``GS2-08-4 registration binds accepted epoch and both source census gates without receipt fiction`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.4")
    Assert.Equal<string list>([ "GS2-08.1" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("ceb6164cd8dfd21a2cb0efe6010dcfc843b382b894de337d63d0d4ef155e50a0", unitValue.GetProperty("contractSha256").GetString())
    Assert.Contains("both exact landed GS2-08.3 writer and receiver source-census", (unitValue.GetProperty("permissionCeiling")[1]).GetString())
    Assert.False(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.3.json")))
    let contract = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-effect-fence-contract")
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(contract.GetProperty("commandSha256").GetString(), components |> String.concat "\u0000" |> sha256Text)

[<Fact>]
let ``retained census dependencies are byte exact`` () =
    use binding = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-4/source-binding.json")
    let writer = binding.RootElement.GetProperty("writerCensusGate")
    let receiver = binding.RootElement.GetProperty("receiverCensusGate")
    Assert.Equal(writer.GetProperty("sourceBindingSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/source-binding.json")
    Assert.Equal(writer.GetProperty("censusSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json")
    Assert.Equal(receiver.GetProperty("sourceBindingSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/receiver-source/source-binding.json")
    Assert.Equal(receiver.GetProperty("censusSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/receiver-source/producer-v1-writer-receiver-census.json")

[<Fact>]
let ``independent Q3 validator passes without provider access`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-v1-effect-fence.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, error)
    Assert.Contains("GITHUB_V1_EFFECT_FENCE_OK cases=21 controls=13", output)
    Assert.Equal("", error)
