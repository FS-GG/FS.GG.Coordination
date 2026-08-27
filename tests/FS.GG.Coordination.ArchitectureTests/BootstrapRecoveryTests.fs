module FS.GG.Coordination.BootstrapRecoveryTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private sha256Text (value: string) =
    let bytes: byte array = Encoding.UTF8.GetBytes value
    let digest: byte array = SHA256.HashData bytes
    Convert.ToHexString(digest).ToLowerInvariant()

[<Fact>]
let ``recovery runner closes clone cache feed and command overrides`` () =
    let script = File.ReadAllText(Path.Combine(root, "eng/bootstrap-recovery.fsx"))
    Assert.Contains("status\"; \"--porcelain=v1\"; \"--untracked-files=all", script)
    Assert.Contains("clone\"; \"--no-local\"; \"--no-checkout", script)
    Assert.Contains("NUGET_PACKAGES", script)
    Assert.Contains("NUGET_HTTP_CACHE_PATH", script)
    Assert.Contains("NUGET_PLUGINS_CACHE_PATH", script)
    Assert.Contains("https://api.nuget.org/v3/index.json", script)
    Assert.Contains("FSGG_BOOTSTRAP_PACKAGE_OVERRIDE", script)
    Assert.DoesNotContain("dotnet nuget push", script, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("https://github.com/", script, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``recovery receipt contract is compact exact and hosted read only`` () =
    let workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/bootstrap-qualification.yml"))
    let contract = File.ReadAllText(Path.Combine(root, "eng/bootstrap-ci-contract.json"))
    Assert.Contains("bootstrap-recovery:", workflow)
    Assert.Contains("dotnet fsi eng/bootstrap-recovery.fsx -- .", workflow)
    Assert.Contains("bootstrap-recovery/result.json", contract)
    Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n"))
    Assert.DoesNotContain("contents: write", workflow)
    Assert.DoesNotContain("id-token: write", workflow)

[<Fact>]
let ``recovery roadmap gate command is independently pinned`` () =
    use catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json")))
    use index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-units.json")))
    let gate = catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("id").GetString() = "bootstrap-recovery")
    let command =
        seq {
            yield gate.GetProperty("executable").GetString()
            yield! gate.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
        }
        |> String.concat "\u0000"
    let unit = index.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("id").GetString() = "GS2-01.8")
    let gateContract = unit.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    Assert.Equal("Q7", gate.GetProperty("qGate").GetString())
    Assert.Equal("bootstrap-recovery", gateContract.GetProperty("id").GetString())
    Assert.Equal("Q7", gateContract.GetProperty("qGate").GetString())
    Assert.Equal(sha256Text command, gateContract.GetProperty("commandSha256").GetString())

[<Fact>]
let ``recovery evidence shape names every ordered stage`` () =
    let script = File.ReadAllText(Path.Combine(root, "eng/bootstrap-recovery.fsx"))
    for value in
        [ "fsgg.coordination.bootstrap-recovery/1"; "packageSha256"; "publishedSources"
          "clone"; "restore"; "build"; "unit-tests"; "architecture-tests"; "pack"; "install"; "execute" ] do
        Assert.Contains(value, script)
