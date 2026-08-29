module FS.GG.Coordination.QualificationManifestArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private retained = Path.Combine(root, "evidence/github-substrate-v2/qualification-manifests/GS2-03.1.json")

let private findings bytes =
    match QualificationManifest.validate (ReadOnlyMemory<byte>(bytes)) with
    | Ok _ -> Set.empty
    | Error values -> values |> List.map _.Code |> Set.ofList

[<Fact>]
let ``retained qualification manifest is complete canonical and independently bound`` () =
    let bytes = File.ReadAllBytes retained
    Assert.True(Set.isEmpty (findings bytes))
    let text = Text.Encoding.UTF8.GetString bytes
    Assert.Contains("\"networkMode\":\"isolated\"", text)
    Assert.Contains("\"independentCases\"", text)
    Assert.Contains("\"principal\":\"independent-critic\"", text)

[<Fact>]
let ``retained qualification manifest rejects a substituted candidate`` () =
    let rootNode = JsonNode.Parse(File.ReadAllBytes retained).AsObject()
    rootNode["candidate"].AsObject()["commitSha"] <- String('b', 40)
    let bytes = Text.Encoding.UTF8.GetBytes(rootNode.ToJsonString() + "\n")
    let observed = findings bytes
    Assert.Contains("QM-CANDIDATE-BINDING", observed)
    Assert.Contains("QM-SELF-DIGEST", observed)

[<Fact>]
let ``qualification manifest CLI validates the retained artifact read only`` () =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add(Path.Combine(root, "src/FS.GG.Coordination.Cli/bin/Release/net10.0/FS.GG.Coordination.Cli.dll"))
    for argument in [ "qualification-manifest"; "validate"; "--file"; retained; "--text" ] do
        startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Contains("QUALIFICATION_MANIFEST_OK", output)
    Assert.Equal("", error)
