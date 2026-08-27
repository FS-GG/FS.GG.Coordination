module FS.GG.Coordination.EvidenceStorageTests

open System
open System.Diagnostics
open System.IO
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``evidence storage contract and all independent negative cases pass`` () =
    let startInfo = ProcessStartInfo("dotnet")
    for argument in
        [ "fsi"; "eng/validate-evidence-storage.fsx"; "--"; "--self-test"; "evidence/github-substrate-v2" ] do
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
    Assert.Contains("EVIDENCE_STORAGE_OK categories=8 entries=13 maxTrackedBytes=65536", output)
    Assert.Contains("EVIDENCE_STORAGE_SELF_TEST_OK negativeCases=21 positiveArtifactManifests=1", output)
    Assert.Equal("", error)

[<Fact>]
let ``bulky generated payloads have only immutable external stores`` () =
    let policy = File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/storage-policy.json"))
    let manifestSchema = File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/schemas/v1/artifact-manifests.schema.json"))
    Assert.Contains("\"trackedMaxBytes\":65536", policy)
    Assert.Contains("github-actions-artifact", policy)
    Assert.Contains("github-release-asset", policy)
    Assert.DoesNotContain("http://", manifestSchema, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("mutable", manifestSchema, StringComparison.OrdinalIgnoreCase)
