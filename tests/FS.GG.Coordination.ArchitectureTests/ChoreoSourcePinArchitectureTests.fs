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

[<Fact>]
let ``hosted writer Choreo foundation keeps four closed authorities and explicit message consumption`` () =
    let protocol, _, _ = fixture ()

    let modelStart =
        protocol.IndexOf("module O2HostedWriterChoreoModel {", StringComparison.Ordinal)

    let modelEnd =
        protocol.IndexOf("module ChoreoSourcePinSmoke {", modelStart, StringComparison.Ordinal)

    Assert.True(modelStart >= 0 && modelEnd > modelStart)
    let model = protocol.Substring(modelStart, modelEnd - modelStart)

    Assert.Contains("Set(HOST, JOURNAL, RUNNER, GITHUB_PROVIDER)", model)
    Assert.Contains("type ProcessState =", model)
    Assert.Contains("type Message =", model)
    Assert.Contains("type CustomEffect = Consume({ at: Process, key: MessageKey })", model)
    Assert.Contains("messages.filter(message => messageKey(message) != record.key)", model)
    Assert.DoesNotContain("type CustomEffect = Consume({ at: Process, message: Message })", model)
    Assert.Contains("val typedMessageSoup", model)
    Assert.DoesNotContain("action hold", model)

[<Fact>]
let ``hosted writer Choreo routes all seven effects through one protocol`` () =
    let protocol, _, _ = fixture ()

    for witness in
        [
            "claimFoundation"
            "processFoundation"
            "candidateFoundation"
            "branchFoundation"
            "pullRequestFoundation"
            "mergeFoundation"
            "nativeReadbackFoundation"
        ] do
        Assert.Contains($"run %s{witness}", protocol)

    Assert.Contains("operation.effectKind == ProcessWork", protocol)
    Assert.Contains("else GITHUB_PROVIDER", protocol)

[<Fact>]
let ``hosted writer Choreo faults remain durable identity fenced and project to the legacy model`` () =
    let protocol, _, _ = fixture ()

    for required in
        [
            "type JournalStatus = JournalEmpty | Intent | Dispatching | Unknown | ProvenAbsent | Applied"
            "val legacyProjection: LegacyProjection"
            "val retainedProjectionSafety"
            "temporal progress: bool"
            "temporal faultSafety: bool"
            "run lostAppliedReconciles"
            "run provenAbsentRetriesSameOperation"
            "run restartRequiresThreeGates"
            "run duplicateResponseRejected"
            "run staleGenerationRejected"
            "run wrongIdentityRejected"
            "run journalSequenceRejected"
            "run missingNativeReadbackCannotComplete"
            "current.revision == previous.revision + 1"
        ] do
        Assert.Contains(required, protocol)

    Assert.Contains("module O2HostedWriterChoreoTests {", protocol)
    Assert.DoesNotContain("action hold", protocol.Substring(protocol.IndexOf("module O2HostedWriterChoreoModel {")))

[<Fact>]
let ``hosted writer correspondence consumes normalized raw Quint Choreo ITF`` () =
    let fixtureRoot =
        Path.Combine(root, "tests/FS.GG.Coordination.Orchestration.Host.Tests/Fixtures/Choreo")

    let manifest =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "manifest.json"))).AsObject()

    Assert.Equal("fsgg.quint.choreo-trace-manifest/1", manifest["schema"].GetValue<string>())
    Assert.Equal("O2HostedWriterChoreoModel::choreo::s", manifest["rawVariable"].GetValue<string>())
    Assert.Equal(8, manifest["scenarios"].AsArray().Count)

    let mutable stateCount = 0

    for scenarioNode in manifest["scenarios"].AsArray() do
        let scenario = scenarioNode.AsObject()
        let tracePath = Path.Combine(fixtureRoot, scenario["file"].GetValue<string>())
        Assert.Equal(scenario["traceSha256"].GetValue<string>(), sha256Bytes (File.ReadAllBytes tracePath))
        let trace = JsonNode.Parse(File.ReadAllBytes tracePath).AsObject()
        let metadata = trace["#meta"].AsObject()
        Assert.Null(metadata["description"])
        Assert.Null(metadata["timestamp"])

        Assert.Equal(
            "src/FS.GG.Coordination.Protocol/Protocol.md#O2HostedWriterChoreoTests",
            metadata["source"].GetValue<string>()
        )

        let variables = trace["vars"].AsArray()
        Assert.Equal("O2HostedWriterChoreoModel::choreo::s", variables[0].GetValue<string>())
        stateCount <- stateCount + trace["states"].AsArray().Count

    Assert.Equal(180, stateCount)

    let replay =
        File.ReadAllText(
            Path.Combine(root, "tests/FS.GG.Coordination.Orchestration.Host.Tests/HostedWriterQuintReplayTests.fs")
        )

    Assert.Contains("ChoreoTrace.load scenarioId", replay)
    Assert.DoesNotContain("type private WriterModel", replay)
    Assert.DoesNotContain("let private modelStep", replay)
    Assert.DoesNotContain("let private trace actions", replay)

    let bounded =
        File.ReadAllText(Path.Combine(root, "eng/verify-choreo-c2-bounded.sh"))

    Assert.Contains("verify-choreo-c3-traces.sh\"", bounded)
    Assert.Contains("verify-choreo-c5-parity.py", bounded)

[<Fact>]
let ``hosted writer Choreo bounded roots cover provider and runner fault schedules`` () =
    let protocol, _, _ = fixture ()
    let script = File.ReadAllText(Path.Combine(root, "eng/verify-choreo-c2-bounded.sh"))

    let validator =
        File.ReadAllText(Path.Combine(root, "eng/validate-canonical-quint-protocol.fsx"))

    let shard =
        File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/canonical-quint-shard.sh"))

    let boundedStart =
        protocol.IndexOf("action boundedFaultStep(effect: EffectKind): bool", StringComparison.Ordinal)

    let boundedEnd =
        protocol.IndexOf("action completeEffect(effect: EffectKind): bool", boundedStart, StringComparison.Ordinal)

    Assert.True(boundedStart >= 0 && boundedEnd > boundedStart)
    let boundedRoot = protocol.Substring(boundedStart, boundedEnd - boundedStart)

    for required in
        [
            "restartObserved: bool"
            "action initAfterClaim = choreo::init({"
            "pure def firstProvenAbsentRetry"
            "pure def firstCrash"
            "action boundedFaultStep(effect: EffectKind): bool"
            "module O2HostedWriterChoreoProviderBounded {"
            "action step = model::boundedFaultStep(model::Claim)"
            "module O2HostedWriterChoreoRunnerBounded {"
            "action step = model::boundedFaultStep(model::ProcessWork)"
        ] do
        Assert.Contains(required, protocol)

    for listener in
        [
            "start(effect)"
            "journalRecordsIntent"
            "hostAcceptsIntent"
            "journalRecordsDispatch"
            "hostDispatches"
            "runnerPerforms"
            "providerPerforms"
            "externalOutcomeUnknown"
            "hostRecordsUnknown"
            "journalRecordsUnknown"
            "hostBeginsReconciliation"
            "externalReconciles"
            "hostAcceptsReconciliation"
            "journalRecordsAbsent"
            "firstProvenAbsentRetry"
            "hostObservesApplied"
            "journalRecordsApplied"
            "hostSettles"
            "firstCrash"
            "journalRecovers"
            "hostAcceptsRecovery"
            "externalReadsAuthority"
            "hostAcceptsAuthority"
            "hostAuthenticatesResume"
            "hostRejectsInvalidResponse"
            "journalRejectsInvalidAppend"
            "hostAcceptsAppendRejection"
        ] do
        Assert.Contains(listener, boundedRoot)

    Assert.Contains("verify_lane O2HostedWriterChoreoProviderBounded", script)
    Assert.Contains("verify_lane O2HostedWriterChoreoRunnerBounded", script)
    Assert.Contains("--max-steps=20", script)
    Assert.Contains("timeout 150s", script)
    Assert.Contains("let choreoBoundary = \"// BEGIN PINNED quint-co/choreo spells/basicSpells.qnt\"", validator)
    Assert.Contains("let qualificationQnt = Path.Combine(scratch, \"protocol-q2-legacy-qualification.qnt\")", validator)
    Assert.Contains("File.WriteAllText(qualificationQnt, q2Source.Substring(0, choreoBoundaryIndex)", validator)
    Assert.Contains("bash eng/verify-choreo-c2-bounded.sh", shard)
