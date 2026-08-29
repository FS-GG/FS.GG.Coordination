module FS.GG.Coordination.QuintQualificationTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private execute selfTest =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add "eng/validate-quint-qualification.fsx"
    info.ArgumentList.Add "--"
    if selfTest then info.ArgumentList.Add "--self-test"
    info.ArgumentList.Add "--root"
    info.ArgumentList.Add "."
    info.ArgumentList.Add "--config"
    info.ArgumentList.Add "eng/quint-qualification.json"
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

[<Fact>]
let ``bounded roots classifications selection and admission are complete`` () =
    let exitCode, output, error = execute false
    Assert.True((exitCode = 0), $"%s{output}\n%s{error}")
    Assert.Contains("roots=7 oracles=11 negativeControls=0", output)

[<Fact>]
let ``independent oracles and qualification contracts reject every focused mutation`` () =
    let exitCode, output, error = execute true
    Assert.True((exitCode = 0), $"%s{output}\n%s{error}")
    Assert.Contains("roots=7 oracles=11 negativeControls=12", output)

[<Fact>]
let ``missing or over budget measurements are rejected before reuse`` () =
    let scratch = Directory.CreateTempSubdirectory("fsgg-quint-budget-")
    try
        let protocolDirectory = Path.Combine(scratch.FullName, "src/FS.GG.Coordination.Protocol")
        let engDirectory = Path.Combine(scratch.FullName, "eng")
        Directory.CreateDirectory protocolDirectory |> ignore
        Directory.CreateDirectory engDirectory |> ignore
        File.Copy(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"), Path.Combine(protocolDirectory, "Protocol.md"))
        File.Copy(Path.Combine(root, "eng/quint-qualification.json"), Path.Combine(engDirectory, "quint-qualification.json"))
        let baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "eng/quint-qualification-baseline.json"))).AsObject()
        (((baseline["measurements"].AsArray())[0]).AsObject())["elapsedMs"] <- JsonValue.Create(30001)
        File.WriteAllText(Path.Combine(engDirectory, "quint-qualification-baseline.json"), baseline.ToJsonString())
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in [ "fsi"; "eng/validate-quint-qualification.fsx"; "--"; "--root"; scratch.FullName; "--config"; "eng/quint-qualification.json" ] do
            info.ArgumentList.Add argument
        use child = Process.Start info
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.NotEqual(0, child.ExitCode)
        Assert.Contains("QQ-BASELINE-BUDGET", output + error)
    finally
        scratch.Delete true
