module FS.GG.Coordination.EvidenceStorageTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FS.GG.Coordination.Qualification.Contracts

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private sha256 (bytes: byte array) =
    SHA256.HashData(bytes)
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

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
    Assert.Contains("EVIDENCE_STORAGE_OK categories=11 entries=83 maxTrackedBytes=65536 frozenCorpusCases=21 observed=2 unobserved=19 aggregate=bf38fc3d426e74237561798d9f3b9fa5dd1b94b487e69f1565cc9cc6ab58c753", output)
    Assert.Contains("EVIDENCE_STORAGE_SELF_TEST_OK negativeCases=56 positiveArtifactManifests=1 positiveCritiqueBundles=1 positiveMutationProofs=1", output)
    Assert.Equal("", error)

[<Fact>]
let ``GS2-05.3 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.3.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.2.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.3"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "a8474e696d2c1ff149ec1efb6a4c4b4cb6fe6e56b86ec840871b4430864f0a50" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.3"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.3 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-05.9 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.9.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.3.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.9"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "f5ac79b55dfa001903a4173209f09a71e7265641f5891c6498c65ce395364be0" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.9"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.9 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-05.4 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.4.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.9.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.4"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "59398e603e39b04ff6d971ef923d19513e03d3990a970323add90cf7ce593861" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.4"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.4 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-05.5 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.5.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.4.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.5"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "0017ef59099ee14e6c3d0df73b4fb05a9c45a34f2067cecdf19a4b29e0a7a0fe" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.5"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.5 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-05.6 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.6.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.5.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.6"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "f382502968cf634bf93c7318d24f629eb3ccfbbac6cf759a99434f1a33975059" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.6"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.6 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-05.7 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.7.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.6.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.7"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "24de35789ad18aff1409e873e9aa63edc2d2cff313d8b63f34168c70f7494368" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.7"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.7 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-05.8 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.8.json")
    let prerequisitePath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.7.json")
    let receipt = File.ReadAllBytes(receiptPath)
    let prerequisite = File.ReadAllBytes(prerequisitePath)

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite); ReadOnlyMemory<byte>(receipt) ]
            "GS2-05.8"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>([ "77ba4ae9ddf350ec93afe7021b320474c5f04ed5f7a255fa2136a3f15af5af12" ], status.AcceptedReceiptDigests)
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(receipt).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ ReadOnlyMemory<byte>(prerequisite)
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-05.8"
    with
    | Ok _ -> Assert.Fail("tampered GS2-05.8 acceptance receipt was accepted")
    | Error findings ->
        Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-06.1 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath unitId = Path.Combine(root, $"evidence/github-substrate-v2/accepted/{unitId}.json")
    let receipt unitId = File.ReadAllBytes(receiptPath unitId) |> ReadOnlyMemory<byte>
    let prerequisiteIds = [ "GS2-02.11"; "GS2-03.9"; "GS2-04.9"; "GS2-05.8"; "GS2-05.9" ]

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            ([ for unitId in prerequisiteIds -> receipt unitId ] @ [ receipt "GS2-06.1" ])
            "GS2-06.1"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>(
            [ "52a282b6b2ddee1ffdd8c68288b1a374cb9bacbb767db238e310c32d0758a53f"
              "c5b0bf313583e26dc6a2f471b58e22d6315f4ff425d05cf6f74070c45c5ecde2"
              "11defafd12353bbcb9b96cc06d3d9e29553ddca4ba912bacd7476c067f9802ed"
              "a267b70003b955e4cd171e30d6f22f52eca6655002e17a52df22a19383fdfd53"
              "59398e603e39b04ff6d971ef923d19513e03d3990a970323add90cf7ce593861" ],
            status.AcceptedReceiptDigests
        )
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(File.ReadAllBytes(receiptPath "GS2-06.1")).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            ([ for unitId in prerequisiteIds -> receipt unitId ]
             @ [ ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ])
            "GS2-06.1"
    with
    | Ok _ -> Assert.Fail("tampered GS2-06.1 acceptance receipt was accepted")
    | Error findings -> Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-06.2 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath unitId = Path.Combine(root, $"evidence/github-substrate-v2/accepted/{unitId}.json")
    let receipt unitId = File.ReadAllBytes(receiptPath unitId) |> ReadOnlyMemory<byte>

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ receipt "GS2-06.1"; receipt "GS2-06.2" ]
            "GS2-06.2"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>(
            [ "0f6a142023f21a266242997ae896e494dfa668e895e308ad73d2d5e01404c042" ],
            status.AcceptedReceiptDigests
        )
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(File.ReadAllBytes(receiptPath "GS2-06.2")).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ receipt "GS2-06.1"
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-06.2"
    with
    | Ok _ -> Assert.Fail("tampered GS2-06.2 acceptance receipt was accepted")
    | Error findings -> Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

[<Fact>]
let ``GS2-06.3 acceptance is indexed and accepted by the roadmap prerequisite reader`` () =
    let indexPath = Path.Combine(root, "eng/github-substrate-v2-units.json")
    let index = JsonNode.Parse(File.ReadAllText(indexPath)).AsObject()
    let units = index["units"].AsArray()

    let roadmap =
        units
        |> Seq.map (fun unitValue ->
            let unitObject = unitValue.AsObject()
            let unitId = unitObject["id"].GetValue<string>()
            let title = unitObject["title"].GetValue<string>()
            $"- [ ] **{unitId} — {title}.**")
        |> String.concat "\n"

    let roadmapBytes = Encoding.UTF8.GetBytes(roadmap + "\n")
    index["roadmap"].AsObject()["sha256"] <- sha256 roadmapBytes
    let indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString())
    let receiptPath unitId = Path.Combine(root, $"evidence/github-substrate-v2/accepted/{unitId}.json")
    let receipt unitId = File.ReadAllBytes(receiptPath unitId) |> ReadOnlyMemory<byte>

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ receipt "GS2-06.2"; receipt "GS2-06.3" ]
            "GS2-06.3"
    with
    | Ok status ->
        Assert.True(status.Ready)
        Assert.Equal<string list>(
            [ "7157ad56a4879e48642dbb055b0b35158353cbc020fca9a008ed901446d74d0c" ],
            status.AcceptedReceiptDigests
        )
    | Error findings -> Assert.Fail(String.concat "," (findings |> List.map _.Code))

    let tampered = JsonNode.Parse(File.ReadAllBytes(receiptPath "GS2-06.3")).AsObject()
    tampered["digest"] <- String.replicate 64 "0"

    match
        RoadmapWork.checkPrerequisites
            (ReadOnlyMemory<byte>(indexBytes))
            (ReadOnlyMemory<byte>(roadmapBytes))
            [ receipt "GS2-06.2"
              ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(tampered.ToJsonString())) ]
            "GS2-06.3"
    with
    | Ok _ -> Assert.Fail("tampered GS2-06.3 acceptance receipt was accepted")
    | Error findings -> Assert.Contains("RW-RECEIPT-TAMPERED", findings |> List.map _.Code)

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
