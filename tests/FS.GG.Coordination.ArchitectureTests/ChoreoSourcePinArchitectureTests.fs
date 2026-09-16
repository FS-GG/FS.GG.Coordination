module FS.GG.Coordination.ChoreoSourcePinArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private sha256Bytes (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private sha256Text (value: string) =
    value |> Encoding.UTF8.GetBytes |> sha256Bytes

let private requireSingleMarker (lines: string array) marker =
    match lines |> Array.indexed |> Array.filter (fun (_, line) -> line = marker) with
    | [| index, _ |] -> index
    | matches -> failwith $"expected one marker '{marker}', got {matches.Length}"

let private pinnedRegion (protocol: string) (beginMarker: string) (endMarker: string) =
    let lines = protocol.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')

    let first = requireSingleMarker lines beginMarker
    let last = requireSingleMarker lines endMarker

    if last <= first + 1 then
        failwith $"empty or reversed pinned region: {beginMarker}"

    lines[(first + 1) .. (last - 1)]
    |> String.concat "\n"
    |> fun value -> value + "\n"

let private sourceEntries (manifest: JsonObject) =
    manifest["sources"].AsArray() |> Seq.map _.AsObject() |> Seq.toList

let private validate protocol licenseBytes (manifest: JsonObject) =
    let findings = ResizeArray<string>()

    let expect code expected actual =
        if actual <> expected then
            findings.Add code

    expect "schema" "fsgg.quint.choreo-source-pin/1" (manifest["schema"].GetValue<string>())
    expect "repository" "https://github.com/quint-co/choreo" (manifest["repository"].GetValue<string>())
    expect "commit" "000cf4eed315187dc6f216a148781cff7dde6521" (manifest["commit"].GetValue<string>())

    let license = manifest["license"].AsObject()
    expect "license-spdx" "Apache-2.0" (license["spdx"].GetValue<string>())
    expect "license-path" "eng/vendor/choreo/LICENSE" (license["path"].GetValue<string>())
    expect "license-sha256" (license["sha256"].GetValue<string>()) (sha256Bytes licenseBytes)

    for entry in sourceEntries manifest do
        let region =
            pinnedRegion protocol (entry["beginMarker"].GetValue<string>()) (entry["endMarker"].GetValue<string>())

        expect "vendored-sha256" (entry["vendoredSha256"].GetValue<string>()) (sha256Text region)

        match entry["transform"].GetValue<string>() with
        | "none" -> expect "upstream-sha256" (entry["upstreamSha256"].GetValue<string>()) (sha256Text region)
        | "replace exactly one filesystem import with import basicSpells.*" ->
            let localImport = "import basicSpells.*"
            let occurrences = region.Split(localImport, StringSplitOptions.None).Length - 1
            expect "import-transform-cardinality" 1 occurrences

            let upstream =
                region.Replace(
                    localImport,
                    "import basicSpells.* from \"spells/basicSpells\"",
                    StringComparison.Ordinal
                )

            expect "upstream-sha256" (entry["upstreamSha256"].GetValue<string>()) (sha256Text upstream)
        | _ -> findings.Add "unknown-transform"

    let integration = manifest["integration"].AsObject()
    expect "integration-mode" "embedded-offline-no-network-fetch" (integration["mode"].GetValue<string>())
    expect "source" "src/FS.GG.Coordination.Protocol/Protocol.md" (integration["source"].GetValue<string>())
    expect "fence" "quint-test" (integration["fence"].GetValue<string>())
    expect "smoke-module" "ChoreoSourcePinSmoke" (integration["smokeModule"].GetValue<string>())
    expect "smoke-run" "choreoSourcePinSmoke" (integration["smokeRun"].GetValue<string>())
    expect "quint-version" "0.32.0" (integration["quintVersion"].GetValue<string>())

    expect
        "quint-binary"
        "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"
        (integration["quintBinarySha256"].GetValue<string>())

    findings |> Seq.toList

let private fixture () =
    let protocol =
        File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md"))

    let manifest =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "eng/choreo-source-pin.json"))).AsObject()

    let licenseObject = manifest["license"].AsObject()
    let licensePath = licenseObject["path"].GetValue<string>()

    let licenseBytes = File.ReadAllBytes(Path.Combine(root, licensePath))
    protocol, licenseBytes, manifest

[<Fact>]
let ``Choreo source license transform and toolchain pin are exact`` () =
    let protocol, licenseBytes, manifest = fixture ()
    Assert.Empty(validate protocol licenseBytes manifest)

[<Fact>]
let ``Choreo source mutation is rejected`` () =
    let protocol, licenseBytes, manifest = fixture ()

    let mutated =
        protocol.Replace("type Effect[p, m, e, ce]", "type MutatedEffect[p, m, e, ce]", StringComparison.Ordinal)

    Assert.Contains("vendored-sha256", validate mutated licenseBytes manifest)

[<Fact>]
let ``Choreo provenance and license mutations are rejected`` () =
    let protocol, licenseBytes, manifest = fixture ()
    let wrongCommit = manifest.DeepClone().AsObject()
    wrongCommit["commit"] <- JsonValue.Create(String.replicate 40 "0")
    Assert.Contains("commit", validate protocol licenseBytes wrongCommit)

    let networkFetch = manifest.DeepClone().AsObject()
    let networkIntegration = networkFetch["integration"].AsObject()
    networkIntegration["mode"] <- JsonValue.Create("fetch-upstream-at-build-time")
    Assert.Contains("integration-mode", validate protocol licenseBytes networkFetch)

    let wrongLicense = Array.copy licenseBytes
    wrongLicense[0] <- wrongLicense[0] ^^^ 0xffuy
    Assert.Contains("license-sha256", validate protocol wrongLicense manifest)

[<Fact>]
let ``Choreo stays inside the canonical literate source`` () =
    let trackedQnt =
        Directory.EnumerateFiles(root, "*.qnt", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            not (
                path.Contains(
                    $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
            )
            && not (
                path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
            ))
        |> Seq.toList

    Assert.Empty(trackedQnt)
