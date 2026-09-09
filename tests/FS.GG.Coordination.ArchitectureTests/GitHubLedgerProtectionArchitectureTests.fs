module FS.GG.Coordination.GitHubLedgerProtectionArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root,path))
let private sha256Text (value:string) = value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

[<Fact>]
let ``ledger protection surface is deterministic and has no apply or provider path`` () =
    let text = read "src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fsi" + read "src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fs"
    for required in [ "ApplyAuthorized"; "DedicatedWriterApp"; "AdditionalWritePermissions"; "PreviousObservationEvidenceSha256"; "ProviderEnvelopeSha256"; "PagesComplete"; "ShardedJournalAdapter.address Cutover"; "FS-GG/FS.GG.Coordination.Authority"; "1351660651"; "refs/heads/fsgg/v2/journal/**/*"; "refs/tags/fsgg/v2/fleet-cutover/**/*" ] do Assert.Contains(required,text)
    for forbidden in [ "HttpClient"; "api.github.com"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "let apply"; "val apply" ] do Assert.DoesNotContain(forbidden,text)

[<Fact>]
let ``historical sharded journal implementation remains unmodified`` () =
    let status = Diagnostics.ProcessStartInfo("git", "diff --exit-code 07b9dbbdfdf1760cb4350f5b5ee69834db2fb81c -- src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fsi")
    status.WorkingDirectory <- root; status.UseShellExecute <- false
    use child = Diagnostics.Process.Start status
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)

[<Fact>]
let ``GS2-08-2 registration binds accepted predecessor roadmap and exact Q3 Q4 and Q6 commands`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let roadmap = units.RootElement.GetProperty("roadmap")
    Assert.Equal("3719b6cfc6f2d766ad56f930b56f025e20c4b2cc", roadmap.GetProperty("revision").GetString())
    Assert.Equal("9c49a0efd1440d8a71130758be39394ae4cdd67f3d10b9cb6cb71998154c1a17", roadmap.GetProperty("sha256").GetString())
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun x -> x.GetProperty("id").GetString()="GS2-08.2")
    Assert.Equal<string list>(["GS2-08.1"], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let contracts = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList
    let commands =
        contracts
        |> List.map (fun contract -> gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun command -> command.GetProperty("id").GetString()=contract.GetProperty("id").GetString()))
    Assert.Equal<string list>(["Q3";"Q4";"Q6"], commands |> List.map (fun command -> command.GetProperty("qGate").GetString()))
    for command, contract in List.zip commands contracts do
        let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
        Assert.Equal(contract.GetProperty("commandSha256").GetString(), components |> String.concat "\u0000" |> sha256Text)
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.1.json")
    Assert.Equal("49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31", receipt.RootElement.GetProperty("digest").GetString())
