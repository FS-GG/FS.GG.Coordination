module FS.GG.Coordination.GitHubV1WriterCensusArchitectureTests

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
let ``writer census qualification remains pure and claims no fence or receiver`` () =
    let signature = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubV1WriterCensusQualification.fsi"
    let implementation = read "src/FS.GG.Coordination.Qualification.Contracts/GitHubV1WriterCensusQualification.fs"
    for required in [ "CompleteCommandRoots"; "CompleteSourcePopulation"; "UnknownCommandRefusal"; "DynamicWriterRefusal"; "NoFenceClaim"; "NoReceiverClaim" ] do
        Assert.Contains(required, signature)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "Process.Start"; "ApplyAuthorized"; "val apply"; "let apply" ] do
        Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``retained producer bytes and historical authority bindings are exact`` () =
    use binding = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-3/source-binding.json")
    let value = binding.RootElement
    Assert.Equal("FS-GG/.github", value.GetProperty("producerRepository").GetString())
    Assert.Equal(value.GetProperty("censusSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json")
    Assert.Equal(value.GetProperty("commandContractSha256").GetString(), digest "evidence/github-substrate-v2/gs2-08-3/producer-command-contract.json")
    Assert.Equal("49c70359ebfbc00331ba90c7c5b100a292efa4cc95a5dfa8007867ceceec5c31", value.GetProperty("acceptedEpochReceiptDigest").GetString())
    Assert.Equal("95de1c77674b9dd8d7a9ce568d1ee175a7797e5e", value.GetProperty("sourceBaseRevision").GetString())
    Assert.Contains("no installed ledger", value.GetProperty("qualificationScope").GetString())

[<Fact>]
let ``independent Q3 validator closes the writer census mutation inventory`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-v1-writer-census.fsx"; "--"; "." ] do startInfo.ArgumentList.Add argument
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode = 0, error)
    Assert.Contains("roots=54 always=20 conditional=6 never=28 sources=62 controls=18", output)
    Assert.Equal("", error)

[<Fact>]
let ``retained command contract and census join by exact ordered name and write class`` () =
    use census = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-3/producer-v1-writer-census.json")
    use contract = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-3/producer-command-contract.json")
    let rows (property: string) (document: JsonDocument) : (string * string) list =
        document.RootElement.GetProperty(property).EnumerateArray()
        |> Seq.map (fun value -> value.GetProperty("name").GetString(), value.GetProperty("writes").GetString())
        |> Seq.toList
    Assert.Equal<(string * string) list>(rows "commandRoots" census, rows "commands" contract)
    Assert.Equal(54, rows "commandRoots" census |> List.length)
    Assert.Equal(62, census.RootElement.GetProperty("sources").GetArrayLength())

[<Fact>]
let ``GS2-08-3 registration preserves accepted epoch prerequisite and writer Q3 gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue =
        units.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-08.3")
    Assert.Equal(".github", unitValue.GetProperty("owner").GetString())
    Assert.Equal<string list>([ "GS2-08.1" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal("686482156555baf835958726ab3750c06c6df5d7ca2ff90bec6dadd83e6cf8ea", unitValue.GetProperty("contractSha256").GetString())
    Assert.Equal<string list>([ "Q3" ], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command =
        gates.RootElement.GetProperty("commands").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-v1-writer-census-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-v1-writer-census.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.False(File.Exists(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-08.3.json")))
