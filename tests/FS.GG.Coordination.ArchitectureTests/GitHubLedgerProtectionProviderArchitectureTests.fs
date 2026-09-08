module FS.GG.Coordination.GitHubLedgerProtectionProviderArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root,path))

[<Fact>]
let ``provider adapter is pure read model with no transport credential or apply surface`` () =
    let text = read "src/FS.GG.Coordination.GitHub/LedgerProtectionProviderAdapter.fsi" + read "src/FS.GG.Coordination.GitHub/LedgerProtectionProviderAdapter.fs"
    for required in [ "PayloadSha256"; "PreviousObservationSha256"; "Pages"; "HttpStatus"; "ObservedAt"; "LedgerProtectionPlanAdapter.compile" ] do
        Assert.Contains(required,text)
    for forbidden in [ "HttpClient"; "api.github.com"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "let apply"; "val apply"; "PATCH "; "POST "; "DELETE " ] do
        Assert.DoesNotContain(forbidden,text)

[<Fact>]
let ``provider corpus is explicitly sanitized and non operational`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-2/provider-observation-corpus.json")
    let value = corpus.RootElement
    Assert.Equal("sanitized-deterministic-fixture", value.GetProperty("sourceCategory").GetString())
    Assert.Equal(0, value.GetProperty("writesAttempted").GetInt32())
    Assert.False(value.GetProperty("providerReadbackClaimed").GetBoolean())
    Assert.False(value.GetProperty("applyAuthorized").GetBoolean())

[<Fact>]
let ``independent provider validator passes from a fresh process`` () =
    let info = ProcessStartInfo("dotnet", "fsi eng/validate-github-ledger-protection-provider.fsx -- .")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode=0, error)
    Assert.Contains("GITHUB_LEDGER_PROTECTION_PROVIDER_OK", output)

[<Fact>]
let ``historical Q3 protection source remains unchanged`` () =
    let info = ProcessStartInfo("git", "diff --exit-code d2d6557613d682c83b54b867a47f1767fad1a77d -- src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fsi src/FS.GG.Coordination.GitHub/LedgerProtectionPlanAdapter.fs src/FS.GG.Coordination.Qualification.Contracts/GitHubLedgerProtectionQualification.fsi src/FS.GG.Coordination.Qualification.Contracts/GitHubLedgerProtectionQualification.fs eng/validate-github-ledger-protection.fsx")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    use child = Process.Start info
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)

[<Fact>]
let ``provider qualification is registered as the GS2-08-2 Q4 continuation`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString()="GS2-08.2")
    Assert.Equal<string list>(["Q3";"Q4"], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>(["github-ledger-protection-contract";"github-ledger-protection-provider-contract"], unitValue.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString()="github-ledger-protection-provider-contract")
    Assert.Equal("Q4", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>(["fsi";"eng/validate-github-ledger-protection-provider.fsx";"--";"."], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
