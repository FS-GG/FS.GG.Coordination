module FS.GG.Coordination.GitHubRepositoryProfileArchitectureTests

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
let ``repository profiles are a pure desired-state compiler with no apply path`` () =
    let signature = read "src/FS.GG.Coordination.GitHub/RepositoryProfileAdapter.fsi"
    let implementation = read "src/FS.GG.Coordination.GitHub/RepositoryProfileAdapter.fs"
    for required in [ "type RepositoryRosterSnapshot"; "type RepositoryProfile"; "type AdministrationBoundary"; "val compile"; "val verify" ] do Assert.Contains(required, signature)
    for required in [ "ExternalObserveOnly"; "fsgg_role"; "fsgg_owner_scope"; "fsgg_coordination_mode"; "canonicalRosterDigest"; "SHA256.HashData" ] do Assert.Contains(required, implementation)
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "let apply"; "PATCH"; "POST"; "DELETE" ] do Assert.DoesNotContain(forbidden, signature + implementation)

[<Fact>]
let ``GS2-06-1 registration binds terminal prerequisites and exact Q3 gate`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-06.1")
    Assert.Equal<string list>([ "GS2-02.11"; "GS2-03.9"; "GS2-04.9"; "GS2-05.8"; "GS2-05.9" ], unitValue.GetProperty("prerequisites").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString() = "github-repository-profile-contract")
    Assert.Equal("Q3", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>([ "fsi"; "eng/validate-github-repository-profiles.fsx"; "--"; "." ], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let components = seq { command.GetProperty("executable").GetString(); yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() }
    Assert.Equal(components |> String.concat "\u0000" |> sha256Text, unitValue.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne |> _.GetProperty("commandSha256").GetString())
    Assert.Equal("6de48b57654910625bf65f0bc4d30a8b399dbf0aa9e6d9e6c79b7a209f53a89e", unitValue.GetProperty("contractSha256").GetString())

[<Fact>]
let ``repository profile evidence preserves all reviewed rows and the external boundary`` () =
    use roster = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-1/roster.json")
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-1/corpus.json")
    use expected = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-06-1/independent-expectations.json")
    let rows = roster.RootElement.GetProperty("rows")
    Assert.Equal(10, rows.GetArrayLength())
    Assert.True(roster.RootElement.GetProperty("complete").GetBoolean())
    Assert.Equal("838f80598dcebea1019ae1b0b38f55180502fb2de155d274c891bc245dd8c29d", roster.RootElement.GetProperty("source").GetProperty("artifactSha256").GetString())
    let external = rows.EnumerateArray() |> Seq.find (fun value -> value.GetProperty("fullName").GetString() = "EHotwagner/S.I.R.")
    Assert.Equal("non-participant", external.GetProperty("role").GetString())
    Assert.Equal(0, external.GetProperty("capabilities").GetArrayLength())
    Assert.Equal(1, expected.RootElement.GetProperty("externalObserveOnlyCount").GetInt32())
    Assert.False(expected.RootElement.GetProperty("external").GetProperty("propertyMutationPermitted").GetBoolean())
    Assert.Equal(0, expected.RootElement.GetProperty("external").GetProperty("nativePropertyCount").GetInt32())
    let generatedControls = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    let independentControls = expected.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    Assert.Equal<string list>(generatedControls, independentControls)
    Assert.Equal(16, generatedControls.Length)
    Assert.Equal(generatedControls.Length, generatedControls |> List.distinct |> List.length)

[<Fact>]
let ``repository profile Q3 validator rejects its closed mutation inventory`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; "eng/validate-github-repository-profiles.fsx" ] do startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Contains("GITHUB_REPOSITORY_PROFILES_OK repositories=10 organization=9 external=1 properties=27 controls=16 seal=f3524e8edbd6b88b0783551c14377881dee5dd958ebd4835d77a57913d30d74b", output)
    Assert.Equal("", error)

[<Fact>]
let ``repository profiles preserve canonical Quint source`` () =
    Assert.Equal("7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218", sha256Text (read "src/FS.GG.Coordination.Protocol/Protocol.md"))
