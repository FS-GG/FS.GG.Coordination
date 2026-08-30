module FS.GG.Coordination.GeneratedStructuralTestsArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private artifactRelative = "src/FS.GG.Coordination.Protocol/Generated/generated-structural-tests.json"
let private artifactPath = Path.Combine(root, artifactRelative)

let private sha256 (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private parseObject path = JsonNode.Parse(File.ReadAllText path).AsObject()

let private setString (node: JsonObject) (name: string) (value: string) = node[name] <- JsonValue.Create(value)
let private setBoolean (node: JsonObject) (name: string) (value: bool) = node[name] <- JsonValue.Create(value)
let private setInteger (node: JsonObject) (name: string) (value: int) = node[name] <- JsonValue.Create(value)

let private mutationBytes name =
    if name = "malformed" then Encoding.UTF8.GetBytes("{not-json")
    else
        let document = parseObject artifactPath
        let cases = document["cases"].AsArray()
        match name with
        | value when value.StartsWith("missing-", StringComparison.Ordinal) ->
            let category = value.Substring("missing-".Length)
            let index = cases |> Seq.findIndex (fun item -> item["category"].GetValue<string>() = category)
            cases.RemoveAt(index)
        | value when value.StartsWith("source-", StringComparison.Ordinal) ->
            let category = value.Substring("source-".Length)
            let item = cases |> Seq.find (fun item -> item["category"].GetValue<string>() = category)
            setString (item.AsObject()) "sourceSha256" (String.replicate 64 "0")
        | "missing" -> cases.RemoveAt(0)
        | "duplicate" -> cases.Add(cases[0].DeepClone())
        | "order" ->
            let first = cases[0].DeepClone()
            let second = cases[1].DeepClone()
            cases[0] <- second
            cases[1] <- first
        | "source" -> setString (cases[0].AsObject()) "sourceSha256" (String.replicate 64 "0")
        | "identity" -> setString document "sourceSha256" (String.replicate 64 "0")
        | "category" ->
            let firstCategory = ((document["categories"].AsArray())[0]).AsObject()
            setInteger firstCategory "count" 0
        | "evidence" -> setString (cases[0].AsObject()) "evidenceClass" "independent-black-box"
        | "digest" -> setString document "selfSha256" (String.replicate 64 "0")
        | "canonical" -> ()
        | unknown -> invalidArg "name" unknown
        let options = JsonSerializerOptions(WriteIndented = (name = "canonical"))
        document.ToJsonString(options) |> Encoding.UTF8.GetBytes

let private assertArtifactMutation name expectedCode =
    match GeneratedStructuralTests.validate root (mutationBytes name) with
    | Ok _ -> failwith $"mutation %s{name} unexpectedly validated"
    | Error error -> Assert.StartsWith(expectedCode, error)

let private withQualifiedCopy action =
    let scratch = Directory.CreateTempSubdirectory("fsgg-generated-structural-")
    try
        let protocolRelative = "src/FS.GG.Coordination.Protocol/Protocol.md"
        let protocolDestination = Path.Combine(scratch.FullName, protocolRelative)
        Directory.CreateDirectory(Path.GetDirectoryName protocolDestination) |> ignore
        File.Copy(Path.Combine(root, protocolRelative), protocolDestination)
        let source = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated")
        for path in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(root, path)
            let destination = Path.Combine(scratch.FullName, relative)
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            File.Copy(path, destination)
        action scratch.FullName
    finally
        scratch.Delete(true)

let private rewriteCompiledOutput scratch fileName family mutate =
    let outputRoot = Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs")
    let outputPath = Path.Combine(outputRoot, fileName)
    let output = parseObject outputPath
    mutate output
    File.WriteAllText(outputPath, output.ToJsonString() + "\n", UTF8Encoding(false))
    let outputDigest = File.ReadAllBytes outputPath |> sha256
    let manifestPath = Path.Combine(outputRoot, "manifest.json")
    let manifest = parseObject manifestPath
    let entry =
        manifest["outputs"].AsArray()
        |> Seq.map _.AsObject()
        |> Seq.find (fun candidate -> candidate["family"].GetValue<string>() = family)
    let firstFile = ((entry["files"].AsArray())[0]).AsObject()
    setString firstFile "contentSha256" outputDigest
    File.WriteAllText(manifestPath, manifest.ToJsonString() + "\n", UTF8Encoding(false))

let private runScript script arguments =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add "fsi"
    startInfo.ArgumentList.Add script
    startInfo.ArgumentList.Add "--"
    for argument in arguments do startInfo.ArgumentList.Add argument
    startInfo.WorkingDirectory <- root
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

[<Fact>]
let ``committed generated suite is complete deterministic and source bound`` () =
    let committed = File.ReadAllBytes artifactPath
    let generated = GeneratedStructuralTests.generate root |> Result.defaultWith failwith
    Assert.Equal<byte>(committed, generated)
    let summary = GeneratedStructuralTests.validate root committed |> Result.defaultWith failwith
    Assert.Equal(221, summary.TotalCount)
    Assert.True(
        summary.CategoryCounts =
            [ "vocabulary", 134; "transition", 14; "command", 14; "mutation", 16; "permission", 6; "schema", 28; "projection", 9 ],
        $"unexpected category counts: %A{summary.CategoryCounts}")
    Assert.Equal("6a1cbdf33117a68fb2f68ddf1346727f6efc6d2201df484e664c5ac8c660699a", summary.SelfSha256)
    Assert.Equal("f3e2f0904f56feaea06c016795e92440c0121852436d78e33e83e6e306dde67f", sha256 committed)

[<Theory>]
[<InlineData("missing", "GST-CASE-COUNT")>]
[<InlineData("duplicate", "GST-CASE-DUPLICATE")>]
[<InlineData("order", "GST-CASE-ORDER")>]
[<InlineData("source", "GST-CASE-SOURCE")>]
[<InlineData("identity", "GST-ARTIFACT-IDENTITY")>]
[<InlineData("category", "GST-CATEGORY")>]
[<InlineData("evidence", "GST-EVIDENCE-CLASS")>]
[<InlineData("digest", "GST-SELF-DIGEST")>]
[<InlineData("canonical", "GST-CANONICAL")>]
[<InlineData("malformed", "GST-ARTIFACT-MALFORMED")>]
let ``artifact inversions fail through the production validator`` name expectedCode =
    assertArtifactMutation name expectedCode

[<Theory>]
[<InlineData("vocabulary")>]
[<InlineData("transition")>]
[<InlineData("command")>]
[<InlineData("mutation")>]
[<InlineData("permission")>]
[<InlineData("schema")>]
[<InlineData("projection")>]
let ``every generated category rejects missing coverage`` category =
    assertArtifactMutation ($"missing-%s{category}") "GST-CASE-COUNT"

[<Theory>]
[<InlineData("vocabulary")>]
[<InlineData("transition")>]
[<InlineData("command")>]
[<InlineData("mutation")>]
[<InlineData("permission")>]
[<InlineData("schema")>]
[<InlineData("projection")>]
let ``every generated category rejects source substitution`` category =
    assertArtifactMutation ($"source-%s{category}") "GST-CASE-SOURCE"

[<Fact>]
let ``stale typed output is rejected before generated evidence`` () =
    withQualifiedCopy (fun scratch ->
        rewriteCompiledOutput scratch "command-metadata.json" "COUT-CommandMetadata" (fun output -> setBoolean output "fresh" false)
        match GeneratedStructuralTests.check scratch artifactRelative with
        | Ok _ -> failwith "stale typed output unexpectedly validated"
        | Error error -> Assert.StartsWith("GST-INPUT-STALE", error))

[<Fact>]
let ``unregistered command mutation and projection changes are rejected`` () =
    let assertSourceMutation fileName family expectedCode mutate =
        withQualifiedCopy (fun scratch ->
            rewriteCompiledOutput scratch fileName family mutate
            match GeneratedStructuralTests.check scratch artifactRelative with
            | Ok _ -> failwith $"%s{family} source mutation unexpectedly validated"
            | Error error -> Assert.StartsWith(expectedCode, error))
    assertSourceMutation "command-metadata.json" "COUT-CommandMetadata" "GST-COMMAND-REGISTRATION" (fun output ->
        let actions = (output["content"]["actions"]).AsArray()
        let added = actions[0].DeepClone().AsObject()
        setString added "actionId" "ACT-Unregistered"
        actions.Add added)
    assertSourceMutation "mutation-census.json" "COUT-MutationCensus" "GST-MUTATION-REGISTRATION" (fun output ->
        (output["content"]["entries"]).AsArray().RemoveAt(0))
    assertSourceMutation "projection-view.json" "COUT-ProjectionViews" "GST-PROJECTION-REGISTRATION" (fun output ->
        (output["content"]["catalogue"]).AsArray().RemoveAt(0))

[<Fact>]
let ``schema and permission censuses agree with their independent producers`` () =
    let assertProducerOmission fileName family (collectionName: string) (expectedCount: int) expectedCode mutate =
        withQualifiedCopy (fun scratch ->
            rewriteCompiledOutput scratch fileName family (fun output ->
                let collection = (output["content"][collectionName]).AsArray()
                Assert.Equal(expectedCount, collection.Count)
                mutate output)
            match GeneratedStructuralTests.check scratch artifactRelative with
            | Ok _ -> failwith $"%s{family} producer omission unexpectedly validated"
            | Error error -> Assert.StartsWith(expectedCode, error))
    assertProducerOmission "schemas.json" "COUT-Schemas" "recordShapes" 28 "GST-SCHEMA-REGISTRATION" (fun output ->
        (output["content"]["recordShapes"]).AsArray().RemoveAt(0))
    assertProducerOmission "permission-census.json" "COUT-PermissionCensus" "requiredPermissions" 6 "GST-PERMISSION-REGISTRATION" (fun output ->
        (output["content"]["requiredPermissions"]).AsArray().RemoveAt(0))
    withQualifiedCopy (fun scratch ->
        let protocolPath = Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.md")
        File.AppendAllText(protocolPath, "\n", UTF8Encoding(false))
        match GeneratedStructuralTests.check scratch artifactRelative with
        | Ok _ -> failwith "qualified source digest drift unexpectedly validated"
        | Error error -> Assert.StartsWith("GST-INPUT-DIGEST", error))

[<Fact>]
let ``compiled output family contract and safe paths are authoritative`` () =
    let assertManifestMutation expectedCode mutate =
        withQualifiedCopy (fun scratch ->
            let manifestPath = Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/manifest.json")
            let manifest = parseObject manifestPath
            mutate manifest
            File.WriteAllText(manifestPath, manifest.ToJsonString() + "\n", UTF8Encoding(false))
            match GeneratedStructuralTests.check scratch artifactRelative with
            | Ok _ -> failwith "compiled output manifest mutation unexpectedly validated"
            | Error error -> Assert.StartsWith(expectedCode, error))
    assertManifestMutation "GST-INPUT-MANIFEST" (fun manifest ->
        let outputs = manifest["outputs"].AsArray()
        let invented = outputs[0].DeepClone().AsObject()
        setString invented "family" "COUT-Invented"
        setInteger invented "ordinal" 10
        outputs.Add invented)
    assertManifestMutation "GST-INPUT-MANIFEST" (fun manifest ->
        let firstOutput = ((manifest["outputs"].AsArray())[0]).AsObject()
        let firstFile = ((firstOutput["files"].AsArray())[0]).AsObject()
        setString firstFile "path" "../contract.json")

[<Fact>]
let ``stable generator and validator adapters execute the committed artifact`` () =
    let generatorExit, generatorOutput, generatorError =
        runScript "eng/generate-generated-structural-tests.fsx" [ "--root"; "."; "--check"; artifactRelative ]
    Assert.Equal(0, generatorExit)
    Assert.Contains("GENERATED_STRUCTURAL_TESTS_OK total=221", generatorOutput)
    Assert.Equal("", generatorError)
    let validatorExit, validatorOutput, validatorError =
        runScript "eng/validate-generated-structural-tests.fsx" [ "--root"; "."; "--artifact"; artifactRelative ]
    Assert.Equal(0, validatorExit)
    Assert.Contains("GENERATED_STRUCTURAL_TESTS_VALID total=221", validatorOutput)
    Assert.Contains("vocabulary=134", validatorOutput)
    Assert.Equal("", validatorError)

[<Fact>]
let ``accepted GS2-03.3 evidence remains bound to its accepted generated bytes`` () =
    let evidencePath = Path.Combine(root, "evidence/github-substrate-v2/generated/GS2-03.3.json")
    use document = JsonDocument.Parse(File.ReadAllBytes evidencePath)
    let evidence = document.RootElement
    Assert.Equal("fsgg.coordination.generated-case/1", evidence.GetProperty("schema").GetString())
    Assert.Equal("GS2-03.3-generated-structural-tests", evidence.GetProperty("id").GetString())
    Assert.Equal("51abd02a655f8ee282c818b3085f32e79927ea160226b4778f9f35798fe60f17", evidence.GetProperty("sha256").GetString())
    Assert.NotEqual(sha256 (File.ReadAllBytes artifactPath), evidence.GetProperty("sha256").GetString())
    Assert.Equal("4d492fb04e73c30f81ad8b96426afc13bc784595336c5a07e866da2e057cf804", evidence.GetProperty("seed").GetString())
