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
    for required in [ "ApplyAuthorized"; "DedicatedWriterApp"; "AdditionalWritePermissions"; "PreviousObservationSha256"; "PagesComplete"; "ShardedJournalAdapter.address Cutover" ] do Assert.Contains(required,text)
    for forbidden in [ "HttpClient"; "api.github.com"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "let apply"; "val apply" ] do Assert.DoesNotContain(forbidden,text)

[<Fact>]
let ``historical sharded journal implementation remains unmodified`` () =
    let status = Diagnostics.ProcessStartInfo("git", "diff --exit-code 07b9dbbdfdf1760cb4350f5b5ee69834db2fb81c -- src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fsi")
    status.WorkingDirectory <- root; status.UseShellExecute <- false
    use child = Diagnostics.Process.Start status
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)

[<Fact>]
let ``GS2-08-2 registration binds accepted predecessor roadmap and exact Q3 command`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let roadmap = units.RootElement.GetProperty("roadmap")
    Assert.Equal("71c7ae798db7fc33cd186ae4d97ae103f24d99d7", roadmap.GetProperty("revision").GetString())
    Assert.Equal("20450bccb71d8656330960cfade25150d370255ac58094523492c98f049e58c1", roadmap.GetProperty("sha256").GetString())
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun x -> x.GetProperty("id").GetString()="GS2-08.2")
    Assert.Equal<string list>(["GS2-08.1"], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("4de31989d1bddaf63c231fc2a3e8fb26f820ff9ae5834bf39a26e95cd528533e", unitValue.GetProperty("contractSha256").GetString())
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun x -> x.GetProperty("id").GetString()="github-ledger-protection-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal("09806459596dae7d79efdc1a6fd0e370261ef954cfaedecef8ab67edea6cd91b", components |> String.concat "\u0000" |> sha256Text)
    use receipt = JsonDocument.Parse(read "evidence/github-substrate-v2/accepted/GS2-08.1.json")
    Assert.Equal("49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31", receipt.RootElement.GetProperty("digest").GetString())
