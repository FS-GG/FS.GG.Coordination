module FS.GG.Coordination.EvidenceStorageTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
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
    Assert.Contains("EVIDENCE_STORAGE_OK categories=10 entries=54 maxTrackedBytes=65536 frozenCorpusCases=21 observed=2 unobserved=19 aggregate=bf38fc3d426e74237561798d9f3b9fa5dd1b94b487e69f1565cc9cc6ab58c753", output)
    Assert.Contains("EVIDENCE_STORAGE_SELF_TEST_OK negativeCases=52 positiveArtifactManifests=1", output)
    Assert.Equal("", error)

[<Fact>]
let ``frozen corpus preserves the exact Q0 inventory and provenance`` () =
    let corpusRoot = Path.Combine(root, "evidence/github-substrate-v2/corpus")
    let metadata = Directory.GetFiles(corpusRoot, "C-*.json", SearchOption.TopDirectoryOnly)
    let originals = Directory.GetFiles(Path.Combine(corpusRoot, "originals"), "*.source", SearchOption.TopDirectoryOnly)
    Assert.Equal(21, metadata.Length)
    Assert.Equal(21, originals.Length)
    let digest relative =
        File.ReadAllBytes(Path.Combine(corpusRoot, relative))
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
    Assert.Equal("5c94fa3ee60e02b7fbee80918b45e5e2046a152a2342f6b88044ac169c1dc67b", digest "provenance/q0-corpus-originals.source")
    Assert.Equal("3a0a73d81823c1667f61f9493c1611aa89b85e24d3e1580cd922d309e2f12f87", digest "provenance/q0-evidence.source")
    let resultStates =
        metadata
        |> Array.map (fun path ->
            use document = JsonDocument.Parse(File.ReadAllBytes path)
            document.RootElement.GetProperty("input").GetProperty("currentV1Result").GetProperty("state").GetString())
        |> Array.countBy id
        |> Map.ofArray
    Assert.Equal(2, Map.find "observed" resultStates)
    Assert.Equal(19, Map.find "not-atomically-observed" resultStates)

[<Fact>]
let ``bulky generated payloads have only immutable external stores`` () =
    let policy = File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/storage-policy.json"))
    let manifestSchema = File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/schemas/v1/artifact-manifests.schema.json"))
    Assert.Contains("\"trackedMaxBytes\":65536", policy)
    Assert.Contains("github-actions-artifact", policy)
    Assert.Contains("github-release-asset", policy)
    Assert.DoesNotContain("http://", manifestSchema, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("mutable", manifestSchema, StringComparison.OrdinalIgnoreCase)
