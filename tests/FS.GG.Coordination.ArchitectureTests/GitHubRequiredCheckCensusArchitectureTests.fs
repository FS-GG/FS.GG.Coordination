module FS.GG.Coordination.GitHubRequiredCheckCensusArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root, path))
let private sha256Text (value: string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private readRevision revision path =
    let startInfo = ProcessStartInfo("git")
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.ArgumentList.Add "show"
    startInfo.ArgumentList.Add $"{revision}:{path}"
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, error)
    output

[<Fact>]
let ``required check census is pure and exposes no plan or apply path`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fs"
    for required in [ "type RequiredCheckCensusSnapshot"; "type RequiredCheckCensusEntry"; "type RequiredCheckCensusAggregate"; "val compile"; "val verify" ] do Assert.Contains(required, signature)
    for required in [ "ClassicProtection"; "Ruleset"; "PullRequestUnconditional"; "MergeGroupUnconditional"; "SHA256.HashData" ] do Assert.Contains(required, implementation)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val plan"; "let plan"; "val apply"; "let apply"; "PATCH"; "POST"; "DELETE" ] do Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``GS2-06-2 registration binds accepted predecessor and exact Q3 gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-06.2")
    Assert.Equal<string list>([ "GS2-06.1" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("2c27bbe68be5ce3767bb87bfaa1c42c01290a3d79b041f518335fef84daed030", unitValue.GetProperty("contractSha256").GetString())
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-required-check-census-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-required-check-census.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(components |> String.concat "\u0000" |> sha256Text, unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString())
    let receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-06.1.json")
    Assert.Equal("0f6a142023f21a266242997ae896e494dfa668e895e308ad73d2d5e01404c042", receipt.RootElement.GetProperty("digest").GetString())

[<Fact>]
let ``required check census evidence separates complete provenance from stable aggregates`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-2/corpus.json")
    use expected = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-2/independent-expectations.json")
    use authorities = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-2/authorities.json")
    Assert.True(corpus.RootElement.GetProperty("complete").GetBoolean())
    Assert.True(corpus.RootElement.GetProperty("classicComplete").GetBoolean())
    Assert.True(corpus.RootElement.GetProperty("rulesetsComplete").GetBoolean())
    Assert.True(corpus.RootElement.GetProperty("producersComplete").GetBoolean())
    Assert.Equal(6, corpus.RootElement.GetProperty("requirements").GetArrayLength())
    Assert.Equal(6, corpus.RootElement.GetProperty("producers").GetArrayLength())
    Assert.Equal(6, expected.RootElement.GetProperty("requiredCount").GetInt32())
    Assert.Equal(0, expected.RootElement.GetProperty("dualSourceCount").GetInt32())
    Assert.Equal(6, expected.RootElement.GetProperty("rulesetOnlyCount").GetInt32())
    Assert.Equal(404, authorities.RootElement.GetProperty("classicProtection").GetProperty("httpStatus").GetInt32())
    let ruleset = (authorities.RootElement.GetProperty("rulesets"))[0]
    Assert.Equal(21633423L, ruleset.GetProperty("id").GetInt64())
    Assert.Equal(6, ruleset.GetProperty("requiredStatusChecks").GetArrayLength())
    Assert.Equal(sha256Text (read "evidence/github-substrate-v2/gs2-06-2/authorities.json"), corpus.RootElement.GetProperty("authorityEvidenceSha256").GetString())
    let revision = corpus.RootElement.GetProperty("sourceRevision").GetString()
    let workflowPath = ".github/workflows/bootstrap-qualification.yml"
    let workflow = readRevision revision workflowPath
    Assert.Contains("\n  pull_request:\n", workflow)
    Assert.DoesNotContain("\n  merge_group:\n", workflow)
    Assert.Equal("0b913aab5149d035addd280adbe7ed069dc2df9a25a062add4b46a0aba44bd4a", sha256Text workflow)
    Assert.NotEqual(sha256Text workflow, sha256Text (read workflowPath))
    let controls = expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal(22, controls.Length)
    Assert.Equal(controls.Length, controls |> List.distinct |> List.length)

[<Fact>]
let ``required check census Q3 validator rejects its closed mutation inventory`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; "eng/validate-github-required-check-census.fsx" ] do startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Contains("GITHUB_REQUIRED_CHECK_CENSUS_OK repository=FS-GG/FS.GG.Coordination required=6 classicOnly=0 rulesetOnly=6 dual=0 controls=22 seal=db294ff75dbfb97a81433331ac7d696a0321a3433a8dd6b29694cbebf37396a3", output)
    Assert.Equal("", error)

[<Fact>]
let ``required check census preserves canonical Quint source`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))
