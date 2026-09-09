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
let ``GS2-08-2 registration binds accepted predecessor roadmap and exact Q3 and Q4 commands`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let roadmap = units.RootElement.GetProperty("roadmap")
    Assert.Equal("7eeb0303a21947a36baa01a6a467a3ddf8b64306", roadmap.GetProperty("revision").GetString())
    Assert.Equal("20450bccb71d8656330960cfade25150d370255ac58094523492c98f049e58c1", roadmap.GetProperty("sha256").GetString())
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun x -> x.GetProperty("id").GetString()="GS2-08.2")
    Assert.Equal<string list>(["GS2-08.1"], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("13d10e8fb3d6e69c3fb6fa068fff1b29545733bdad4607e4d922983ffd07ca7a", unitValue.GetProperty("contractSha256").GetString())
    let contracts = unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.toList
    let commands =
        contracts
        |> List.map (fun contract -> gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun command -> command.GetProperty("id").GetString()=contract.GetProperty("id").GetString()))
    Assert.Equal<string list>(["Q3";"Q4"], commands |> List.map (fun command -> command.GetProperty("qGate").GetString()))
    for command, contract in List.zip commands contracts do
        let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
        Assert.Equal(contract.GetProperty("commandSha256").GetString(), components |> String.concat "\u0000" |> sha256Text)
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.1.json")
    Assert.Equal("49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31", receipt.RootElement.GetProperty("digest").GetString())
