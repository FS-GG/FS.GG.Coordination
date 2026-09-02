module FS.GG.Coordination.GitHubRulesetPlanArchitectureTests

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

[<Fact>]
let ``ruleset plan is a pure desired-state compiler with no apply path`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/RulesetPlanAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/RulesetPlanAdapter.fs"
    for required in [ "type RulesetPlanSnapshot"; "type DefaultBranchRulesetTarget"; "type ReleaseTagRulesetTarget"; "type RepositoryMergePolicyTarget"; "val compile"; "val verify" ] do Assert.Contains(required, signature)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "let apply"; "PATCH"; "POST"; "DELETE" ] do Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``GS2-06-3 registration binds accepted predecessor and exact Q3 gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-06.3")
    Assert.Equal<string list>([ "GS2-06.2" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("694cb05954c0683be424dbafcd1c4b79b215737ad95cdd34f3d730147f6dfa96", unitValue.GetProperty("contractSha256").GetString())
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-ruleset-plan-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-ruleset-plans.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(components |> String.concat "\u0000" |> sha256Text, unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString())
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-06.2.json")
    Assert.Equal("7157ad56a4879e48642dbb055b0b35158353cbc020fca9a008ed901446d74d0c", receipt.RootElement.GetProperty("digest").GetString())

[<Fact>]
let ``retained ruleset target is exact secure and census gated`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-3/corpus.json")
    use expected = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-3/independent-expectations.json")
    Assert.True(corpus.RootElement.GetProperty("complete").GetBoolean())
    Assert.Equal(0, corpus.RootElement.GetProperty("approvedBypass").GetArrayLength())
    Assert.Equal(0, corpus.RootElement.GetProperty("requestedBypass").GetArrayLength())
    Assert.Equal(0, corpus.RootElement.GetProperty("exceptions").GetArrayLength())
    Assert.Equal(6, expected.RootElement.GetProperty("requiredChecks").GetArrayLength())
    Assert.False(expected.RootElement.GetProperty("mergeQueue").GetBoolean())
    let mergeMethods = expected.RootElement.GetProperty("mergeMethods")
    Assert.Equal("squash", mergeMethods[0].GetString())
    let controls = expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal(26, controls.Length)
    Assert.Equal(controls.Length, controls |> List.distinct |> List.length)

[<Fact>]
let ``ruleset plan Q3 validator rejects its closed mutation inventory`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; "eng/validate-github-ruleset-plans.fsx" ] do startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Contains("GITHUB_RULESET_PLANS_OK repository=FS-GG/FS.GG.Coordination checks=6 mergeQueue=false bypass=0 exceptions=0 controls=26 seal=6c9e6bb05e1f3a217dca56ddcaf0a0ea4df0517ee492a34c633bd5f5183356ed", output)
    Assert.Equal("", error)

[<Fact>]
let ``ruleset plans preserve canonical Quint source`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))
