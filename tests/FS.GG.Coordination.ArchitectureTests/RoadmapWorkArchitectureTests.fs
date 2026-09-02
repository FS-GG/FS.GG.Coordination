module FS.GG.Coordination.RoadmapWorkArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit

let private root =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

let private runAt workingDirectory executable arguments =
    let startInfo = ProcessStartInfo(executable)

    for argument in arguments do
        startInfo.ArgumentList.Add argument

    startInfo.WorkingDirectory <- workingDirectory
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use child = Process.Start startInfo
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output.Trim(), error.Trim()

let private run executable arguments = runAt root executable arguments

let private gateCommandSha256 (command: JsonElement) =
    seq {
        command.GetProperty("executable").GetString()
        yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
    }
    |> String.concat "\u0000"
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private hasClosedStagedIntakeVocabulary (source: string) =
    let lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n') |> Set.ofArray
    lines.Contains "type CaptureAuthorityRead = IssueIdentity | NativeTypeAndFields | ProjectMembership | Relations | RepositoryScope | ProtocolState"
    && lines.Contains "type ReadyPromotionSurface = RootCause | TouchSet | VerificationContract | Dependencies | RouteDecision | NativeIssueType | OrganizationFields | RepositoryScope | WorkClassification"

[<Fact>]
let ``staged intake authority and promotion vocabularies are closed and phase local`` () =
    let source =
        File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/IntakeAdapter.fs"))

    Assert.True(hasClosedStagedIntakeVocabulary source)

    let globalMutation =
        source.Replace(
            "| ProtocolState",
            "| ProtocolState | OrganizationWideReconcile",
            StringComparison.Ordinal
        )

    let laterPhaseMutation =
        source.Replace(
            "| WorkClassification",
            "| WorkClassification | Claim | PullRequest",
            StringComparison.Ordinal
        )

    Assert.False(hasClosedStagedIntakeVocabulary globalMutation)
    Assert.False(hasClosedStagedIntakeVocabulary laterPhaseMutation)

[<Fact>]
let ``current source tree contains no rival protocol AST`` () =
    let authoredFsharp =
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun path -> not (path.EndsWith("Protocol.Generated.fs", StringComparison.Ordinal)))
        |> Seq.map File.ReadAllText
        |> String.concat "\n"

    for rival in
        [ "type Subject ="
          "type Authority ="
          "type Mutation ="
          "type ObservationPlan =" ] do
        Assert.DoesNotContain(rival, authoredFsharp)

[<Fact>]
let ``hosted compiler gate invokes the exact canonical Quint Q1 and Q2 subject`` () =
    let workflow =
        File.ReadAllText(Path.Combine(root, ".github/workflows/bootstrap-qualification.yml"))

    let qualification =
        File.ReadAllText(Path.Combine(root, "eng/qualify-canonical-quint.sh"))

    let validator =
        File.ReadAllText(Path.Combine(root, "eng/validate-canonical-quint-protocol.fsx"))

    let gate =
        File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/canonical-quint.sh"))

    Assert.Contains("  canonical-quint:", workflow)
    Assert.Contains("needs: [reuse-decision, deterministic-build, compiler-and-tests, canonical-quint, dependency-and-security, package-install-smoke, bootstrap-recovery]", workflow)
    Assert.Contains("run: bash eng/bootstrap-gates/canonical-quint.sh", workflow)
    Assert.Contains("bash eng/qualify-canonical-quint.sh", gate)
    let validatorInvocation = "dotnet fsi eng/validate-canonical-quint-protocol.fsx -- --root . --output"
    Assert.Contains(validatorInvocation, qualification)
    Assert.Equal(
        qualification.IndexOf(validatorInvocation, StringComparison.Ordinal),
        qualification.LastIndexOf(validatorInvocation, StringComparison.Ordinal)
    )
    Assert.DoesNotContain("--compiler-only", qualification)
    Assert.Contains("quint-linux-amd64", qualification)
    Assert.Contains("sha256sum --check --status", qualification)
    Assert.DoesNotContain("70-gs2-03-1-qualification-manifest", qualification)
    Assert.Contains("sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0", gate)
    Assert.Contains("/usr/bin/unshare --user --map-root-user --net -- /usr/bin/true", gate)
    Assert.Contains("equivalent-named-block-partition", validator)
    Assert.Contains("equivalent-fence-indentation", validator)
    Assert.Contains("equivalent-crlf", validator)
    Assert.Contains("equivalent-quint-trivia", validator)
    Assert.Contains("executedEquivalentVariants.Add name", validator)
    Assert.Contains("EQUIVALENT-AUTHORING-COVERAGE", validator)
    Assert.Contains("\"--invariants\"", validator)
    Assert.Contains("fsgg.coordination.canonical-quint-qualification/1", validator)

    let receiptSchema =
        File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/schemas/v1/canonical-quint-qualifications.schema.json"))
    Assert.Contains("fsgg.coordination.canonical-quint-qualification/1", receiptSchema)
    Assert.Contains("\"negativeControlCount\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":126}", receiptSchema)
    Assert.Contains("\"formalCounterexamples\"", receiptSchema)
    Assert.Contains("\"apalacheVerify\"", receiptSchema)
    Assert.Contains("ParallelOptions(MaxDegreeOfParallelism = 2)", validator)
    Assert.Contains("if: ${{ always() }}", workflow)
    Assert.Contains("q1Outcome <- \"failed\"", validator)
    Assert.Contains("q2Outcome <- \"failed\"", validator)
    Assert.Contains("requireCompletedProcessInventory ()", validator)
    Assert.Contains("expectedInvocationInventory", validator)
    Assert.Contains("ConcurrentDictionary<string, int>", validator)
    Assert.Contains("actualInvocationInventory.AddOrUpdate", validator)
    Assert.Contains("let isolateApalacheEndpoint isQuint arguments", validator)
    Assert.Contains("Interlocked.Increment(&apalacheEndpointOrdinal)", validator)
    Assert.Contains("APALACHE-ENDPOINT-DUPLICATE", validator)
    Assert.Contains("arguments @ [ \"--server-endpoint\"; $\"localhost:%d{port}\" ]", validator)
    Assert.Contains("let startApalacheServer workingDirectory environment port", validator)
    Assert.Contains("client.Connect(\"127.0.0.1\", port)", validator)
    Assert.Contains("Process.Kill(true)", validator)
    Assert.Contains("use serverGuard", validator)
    Assert.Contains("APALACHE-SERVER-START", validator)
    Assert.Contains("stdout=%s{temporalOutput}; stderr=%s{temporalError}", validator)
    Assert.Contains("transition-removal did not violate %s{violatedTemporal}; exit=%d{temporalExitCode}; stdout=%s{temporalOutput}; stderr=%s{temporalError}", validator)
    Assert.Contains("counterexample start marker missing; exit=%d{temporalExitCode}", validator)
    Assert.Contains("firstProjection=%s{sha256Text first}", validator)
    Assert.Contains("quint/formal-safety-mutant", validator)
    Assert.Contains("actual <> expected", validator)

[<Fact>]
let ``hosted canonical Quint gate cannot silently downgrade to static validation`` () =
    let qualification =
        File.ReadAllText(Path.Combine(root, "eng/qualify-canonical-quint.sh"))

    Assert.DoesNotContain("--static-only", qualification)

[<Fact>]
let ``roadmap work skill satisfies its independent structure ceiling`` () =
    let exitCode, output, error =
        run
            "dotnet"
            [ "fsi"
              "eng/validate-roadmap-work-skill.fsx"
              "--"
              ".agents/skills/github-substrate-v2-work/SKILL.md" ]

    Assert.Equal(0, exitCode)
    Assert.StartsWith("ROADMAP_WORK_SKILL_OK", output)
    Assert.Equal("", error)

[<Fact>]
let ``roadmap unit index advances through GS2-06-2 required check census`` () =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/github-substrate-v2-units.json")))

    let units = document.RootElement.GetProperty("units").EnumerateArray() |> Seq.toList

    let roadmap = document.RootElement.GetProperty("roadmap")
    Assert.Equal("a13e7c16af028979e93c836efe1bd786f0f2d7f8", roadmap.GetProperty("revision").GetString())
    Assert.Equal("3fe2f665ab75d88d253e74ac6647bbb27b0ead930d47ee6d2430905dfa22f8c9", roadmap.GetProperty("sha256").GetString())

    let ids =
        units |> List.map (fun unitValue -> unitValue.GetProperty("id").GetString())

    if
        ids
        <> [ "GS2-01.1"
             "GS2-01.2"
             "GS2-01.3"
             "GS2-01.4"
             "GS2-01.5"
             "GS2-01.6"
             "GS2-01.7"
             "GS2-01.8"
             "GS2-02.1"
             "GS2-02.2"
             "GS2-02.3"
             "GS2-02.4"
             "GS2-02.5"
             "GS2-02.6"
             "GS2-02.7"
             "GS2-02.8"
             "GS2-02.9"
             "GS2-02.10"
             "GS2-02.11"
             "GS2-03.1"
             "GS2-03.2"
             "GS2-03.3"
             "GS2-03.4"
             "GS2-03.5"
             "GS2-03.6"
             "GS2-03.7"
             "GS2-03.8"
             "GS2-03.9"
             "GS2-04.1"
             "GS2-04.2"
             "GS2-04.3"
             "GS2-04.4"
             "GS2-04.5"
             "GS2-04.6"
             "GS2-04.7"
             "GS2-04.8"
             "GS2-04.9"
             "GS2-05.1"
             "GS2-05.2"
             "GS2-05.3"
             "GS2-05.9"
             "GS2-05.4"
             "GS2-05.5"
             "GS2-05.6"
             "GS2-05.7"
             "GS2-05.8"
             "GS2-06.1"
             "GS2-06.2" ]
    then
        Assert.Fail("roadmap unit inventory differs")

    Assert.Equal(ids.Length, ids |> List.distinct |> List.length)

    let selected =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-01.6")

    let commands =
        selected.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList

    if
        commands
        <> [ "skill-structure"; "locked-build"; "unit-tests"; "architecture-tests" ]
    then
        Assert.Fail("GS2-01.6 gate inventory differs")

    Assert.DoesNotContain("GS2-01.7", commands)

    let protocolUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.1")

    let prerequisites =
        protocolUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList

    Assert.Equal<string list>([ for index in 1..8 -> $"GS2-01.{index}" ], prerequisites)
    Assert.Equal(2, protocolUnit.GetProperty("gateContracts").GetArrayLength())

    let authorityUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.2")

    Assert.Equal<string list>(
        [ "GS2-02.1" ],
        authorityUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, authorityUnit.GetProperty("gateContracts").GetArrayLength())

    let observationUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.3")

    Assert.Equal<string list>(
        [ "GS2-02.2" ],
        observationUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, observationUnit.GetProperty("gateContracts").GetArrayLength())

    let lifecycleUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.4")

    Assert.Equal<string list>(
        [ "GS2-02.3" ],
        lifecycleUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, lifecycleUnit.GetProperty("gateContracts").GetArrayLength())

    let relationUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.5")

    Assert.Equal<string list>(
        [ "GS2-02.4" ],
        relationUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, relationUnit.GetProperty("gateContracts").GetArrayLength())

    let streamUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.6")

    Assert.Equal<string list>(
        [ "GS2-02.5" ],
        streamUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, streamUnit.GetProperty("gateContracts").GetArrayLength())

    let mutationUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.7")

    Assert.Equal<string list>(
        [ "GS2-02.6" ],
        mutationUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, mutationUnit.GetProperty("gateContracts").GetArrayLength())

    let durablePlanUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.8")

    Assert.Equal<string list>(
        [ "GS2-02.7" ],
        durablePlanUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, durablePlanUnit.GetProperty("gateContracts").GetArrayLength())

    let desiredStateUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.9")

    Assert.Equal<string list>(
        [ "GS2-02.8" ],
        desiredStateUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, desiredStateUnit.GetProperty("gateContracts").GetArrayLength())

    let compiledContractUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.10")

    Assert.Equal<string list>(
        [ "GS2-02.9" ],
        compiledContractUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, compiledContractUnit.GetProperty("gateContracts").GetArrayLength())

    let deterministicIdentityUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.11")

    Assert.Equal<string list>(
        [ "GS2-02.10" ],
        deterministicIdentityUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(2, deterministicIdentityUnit.GetProperty("gateContracts").GetArrayLength())

    let qualificationManifestUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.1")

    Assert.Equal<string list>(
        [ "GS2-02.11" ],
        qualificationManifestUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal(3, qualificationManifestUnit.GetProperty("gateContracts").GetArrayLength())

    let frozenCorpusUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.2")

    Assert.Equal<string list>(
        [ "GS2-03.1" ],
        frozenCorpusUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "Q2"; "Q7" ],
        frozenCorpusUnit.GetProperty("gateContracts").EnumerateArray()
        |> Seq.map (fun value -> value.GetProperty("qGate").GetString())
        |> Seq.toList
    )

    let generatedStructuralUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.3")

    Assert.Equal<string list>(
        [ "GS2-03.2" ],
        generatedStructuralUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "canonical-quint-pure-model"; "architecture-tests"; "evidence-storage-contract" ],
        generatedStructuralUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let independentOracleUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.4")

    Assert.Equal<string list>(
        [ "GS2-03.3" ],
        independentOracleUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "canonical-quint-compiler"
          "canonical-quint-pure-model"
          "architecture-tests"
          "evidence-storage-contract" ],
        independentOracleUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let oracleExitGate = independentOracleUnit.GetProperty("exitGate").GetString()
    Assert.Contains("03.4a", oracleExitGate)
    Assert.Contains("03.4b", oracleExitGate)
    Assert.Contains("03.4c", oracleExitGate)
    Assert.Contains("anti-vacuity", oracleExitGate)

    let nativeQuintTestsUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.5")

    Assert.Equal<string list>(
        [ "GS2-03.4" ],
        nativeQuintTestsUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "canonical-quint-pure-model"; "architecture-tests"; "evidence-storage-contract" ],
        nativeQuintTestsUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let nativeQuintExitGate = nativeQuintTestsUnit.GetProperty("exitGate").GetString()

    for requiredTerm in
        [ "examples"
          "simulation"
          "reachability witnesses"
          "safety properties"
          "temporal liveness checks"
          "bounded model checking"
          "claim/election"
          "relation mutation"
          "lifecycle"
          "operation saga"
          "epoch"
          "rollback"
          "Quint/ITF counterexamples" ] do
        Assert.Contains(requiredTerm, nativeQuintExitGate)

    let faultInjectionUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.6")

    Assert.Equal<string list>(
        [ "GS2-03.5" ],
        faultInjectionUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "architecture-tests"; "evidence-storage-contract" ],
        faultInjectionUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "Q7" ],
        faultInjectionUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let faultInjectionExitGate = faultInjectionUnit.GetProperty("exitGate").GetString()

    for requiredTerm in
        [ "before and after every modeled external step"
          "lost responses"
          "duplicate and reordered events"
          "partial pages"
          "exhausted rate budgets"
          "revoked permission"
          "concurrent revision mutation"
          "convergence"
          "typed refusal" ] do
        Assert.Contains(requiredTerm, faultInjectionExitGate)

    let supplyChainUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.7")

    Assert.Equal<string list>(
        [ "GS2-03.6" ],
        supplyChainUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "architecture-tests"; "evidence-storage-contract"; "bootstrap-recovery" ],
        supplyChainUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "Q7" ],
        supplyChainUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let supplyChainExitGate = supplyChainUnit.GetProperty("exitGate").GetString()

    for requiredTerm in
        [ "packed once"
          "byte-for-byte reproducibly"
          "portable-symbol package"
          "installed-assembly digests"
          "SBOM"
          "attestations"
          "allowed pre-production channel"
          "served exact bytes"
          "clean consumers" ] do
        Assert.Contains(requiredTerm, supplyChainExitGate)

    let reviewGatesUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.8")

    Assert.Equal("Add critique evidence gates", reviewGatesUnit.GetProperty("title").GetString())

    Assert.Equal<string list>(
        [ "GS2-03.7" ],
        reviewGatesUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "architecture-tests"; "evidence-storage-contract" ],
        reviewGatesUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let reviewExitGate = reviewGatesUnit.GetProperty("exitGate").GetString()

    for requiredTerm in
        [ "architecture"
          "security"
          "adapter"
          "migration"
          "cutover"
          "exact candidate"
          "evidence fingerprints"
          "distinct phase identity"
          "Accountable Delivery Owner"
          "sole acceptance decision"
          "self-authored"
          "prose-only" ] do
        Assert.Contains(requiredTerm, reviewExitGate)

    let mutationProofUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-03.9")

    Assert.Equal("Prove the harness can fail", mutationProofUnit.GetProperty("title").GetString())

    Assert.Equal<string list>(
        [ "GS2-03.8" ],
        mutationProofUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    Assert.Equal<string list>(
        [ "architecture-tests"; "evidence-storage-contract" ],
        mutationProofUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let mutationProofExitGate = mutationProofUnit.GetProperty("exitGate").GetString()

    for requiredTerm in
        [ "closed inventory"
          "every gate class"
          "vacuous"
          "absent"
          "stale"
          "truncated"
          "forged"
          "generated-only"
          "typed diagnostics"
          "mutation adequacy"
          "unmutated controls"
          "self-attested" ] do
        Assert.Contains(requiredTerm, mutationProofExitGate)

    let transportUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.1")

    Assert.Equal("Transport foundation", transportUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-03.9" ],
        transportUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-transport-contract" ],
        transportUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        transportUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let transportExitGate = transportUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "typed REST/GraphQL"
          "idempotency"
          "ETag"
          "rate budgets"
          "complete pagination"
          "redacts secrets"
          "deterministic fixtures"
          "independent truncation"
          "without claiming live Q4" ] do
        Assert.Contains(requiredTerm, transportExitGate)

    let issueFieldUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.2")

    Assert.Equal("Issue/type/field adapter", issueFieldUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.1" ],
        issueFieldUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-issue-field-contract" ],
        issueFieldUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        issueFieldUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let issueFieldExitGate = issueFieldUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "semantic identities"
          "complete typed observations"
          "closed option sets"
          "without inventing absence"
          "guarded create, update, and clear plans"
          "expected revisions"
          "idempotency identities"
          "independent pagination"
          "type-drift"
          "option-drift"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, issueFieldExitGate)

    let nativeRelationUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.3")

    Assert.Equal("Native relation adapter", nativeRelationUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.2" ],
        nativeRelationUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-native-relation-contract" ],
        nativeRelationUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        nativeRelationUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let nativeRelationExitGate = nativeRelationUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "complete hierarchy and dependency edge sets"
          "without inventing absence"
          "relation kind"
          "endpoint direction"
          "guarded add and remove plans"
          "expected revisions"
          "idempotency identities"
          "re-read and replan"
          "exact post-state verification"
          "unchanged unrelated edges"
          "independent pagination"
          "reversed-endpoint"
          "concurrent-change"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, nativeRelationExitGate)

    let projectAdapterUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.4")

    Assert.Equal("Project adapter", projectAdapterUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.3" ],
        projectAdapterUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-project-adapter-contract" ],
        projectAdapterUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        projectAdapterUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let projectAdapterExitGate = projectAdapterUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "membership and Status only as projections"
          "complete membership, item, and field observations"
          "archived"
          "duplicated"
          "external"
          "draft"
          "missing"
          "unreadable"
          "without inventing absence or authority"
          "expected revisions"
          "idempotency identities"
          "independent pagination"
          "concurrent-change"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, projectAdapterExitGate)

    let commentProjectionUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.5")

    Assert.Equal("Comment/projection adapter", commentProjectionUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.4" ],
        commentProjectionUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-comment-projection-contract" ],
        commentProjectionUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        commentProjectionUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let commentProjectionExitGate = commentProjectionUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "server-issued comment identity and order"
          "without treating comment order as concurrency authority"
          "marker JSON"
          "referenced journal digests"
          "edited"
          "deleted"
          "tampered"
          "malformed"
          "without inventing authority or absence"
          "durable journal authority"
          "independent pagination"
          "reordered-page"
          "mismatched-journal-digest"
          "concurrent-change"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, commentProjectionExitGate)

    let shardedJournalUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.6")

    Assert.Equal("Sharded Git journal adapter", shardedJournalUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.5" ],
        shardedJournalUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-sharded-journal-contract" ],
        shardedJournalUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        shardedJournalUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let shardedJournalExitGate = shardedJournalUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "canonical aggregate digests and two-hex shards"
          "refs/heads/fsgg/v2/journal/<kind>/<shard>"
          "one-parent append-only commits"
          "monotonic fencing generations"
          "exact old-OID --force-with-lease"
          "response-unknown-requires-reread"
          "without inferring success from comments or object existence"
          "current journal commit and generation"
          "reverse compensation"
          "v2-journal-writer"
          "v2-journal-integrity"
          "refs/heads/fsgg/v2/journal/**/*"
          "writer-App-only bypass"
          "no integrity bypass"
          "stale-parent"
          "ambiguous-response"
          "ruleset-drift"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, shardedJournalExitGate)

    let repositorySettingsUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.7")

    Assert.Equal("Repository/settings adapter", repositorySettingsUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.6" ],
        repositorySettingsUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-repository-settings-contract" ],
        repositorySettingsUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        repositorySettingsUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let repositorySettingsExitGate = repositorySettingsUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "exact repository node and default branch"
          "custom-property values"
          "branch and tag rulesets"
          "effective branch rules"
          "merge policies"
          "Actions permissions"
          "environments without secret values"
          "code-security controls"
          "dependency graph"
          "supported, unauthorized, unavailable, incomplete, and unreadable"
          "without inventing absence or compliance"
          "canonical complete pre-state fingerprints"
          "least required permissions"
          "refuses a plan from partial observations"
          "stale or indeterminate result"
          "authoritative post-state verification"
          "without inferring success from an API response"
          "secret-redaction"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, repositorySettingsExitGate)

    let actionsReleaseFeedUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.8")

    Assert.Equal("Actions/release/feed adapter", actionsReleaseFeedUnit.GetProperty("title").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.7" ],
        actionsReleaseFeedUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-actions-release-feed-contract" ],
        actionsReleaseFeedUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        actionsReleaseFeedUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let actionsReleaseFeedExitGate = actionsReleaseFeedUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "run attempt"
          "check suite"
          "merge-group head"
          "without conflating check state with merge authority"
          "immutable, deleted, and tampered"
          "artifact-attestation subjects"
          "package, version, and feed identities"
          "separates upload acceptance"
          "anonymous public served-download bytes"
          "redirect and content identity"
          "without inventing absence, provenance, or served availability"
          "explicit pagination completeness"
          "upload-response"
          "without performing live writes"
          "without claiming Q4" ] do
        Assert.Contains(requiredTerm, actionsReleaseFeedExitGate)

    let sandboxClosureUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-04.9")

    Assert.Equal(
        "Sandbox qualification and comprehensive GS2-04 closure",
        sandboxClosureUnit.GetProperty("title").GetString()
    )
    Assert.Equal<string list>(
        [ "GS2-04.8" ],
        sandboxClosureUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3"; "Q4" ],
        sandboxClosureUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal(9, sandboxClosureUnit.GetProperty("gateCommands").GetArrayLength())
    let sandboxClosureExitGate = sandboxClosureUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "isolated test repositories"
          "non-production identities"
          "records exact pre-state"
          "authoritative reread rather than response inference"
          "reverse-order compensation"
          "cleanup receipts"
          "GS2-04.1 through GS2-04.8 receipts"
          "ADR-0080 comprehensive mode"
          "protected GS2-04 closure receipt"
          "before GS2-05 may consume"
          "production-target"
          "omitted-adapter inversions all fail closed" ] do
        Assert.Contains(requiredTerm, sandboxClosureExitGate)

    let workTaxonomyUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.1")

    Assert.Equal("Finalize taxonomy", workTaxonomyUnit.GetProperty("title").GetString())
    Assert.Equal("FS.GG.Coordination", workTaxonomyUnit.GetProperty("owner").GetString())
    Assert.Equal<string list>(
        [ "GS2-04.9" ],
        workTaxonomyUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q2" ],
        workTaxonomyUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-work-taxonomy-contract" ],
        workTaxonomyUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let workTaxonomyExitGate = workTaxonomyUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "Epic, Feature, Task, Bug, Decision, Register, and Directive"
          "sole classification authority"
          "without retaining Class or Kind as parallel semantic input"
          "standing Register and Directive exemptions"
          "every current native-type, Class, Kind"
          "complete fingerprinted corpus"
          "one deterministic canonical migration disposition"
          "exact pre-state fingerprint"
          "retired projections"
          "refusing missing, unknown, contradictory, ambiguous"
          "proves totality, uniqueness, deterministic ordering, idempotent replay"
          "omitted-combination inversions all fail closed"
          "without mutating GitHub"
          "without claiming GS2-05.2" ] do
        Assert.Contains(requiredTerm, workTaxonomyExitGate)

    let organizationIssueFieldsUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.2")

    Assert.Equal("Finalize organization issue fields", organizationIssueFieldsUnit.GetProperty("title").GetString())
    Assert.Equal("FS.GG.Coordination", organizationIssueFieldsUnit.GetProperty("owner").GetString())
    Assert.Equal<string list>(
        [ "GS2-05.1" ],
        organizationIssueFieldsUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q2" ],
        organizationIssueFieldsUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-organization-issue-fields-contract" ],
        organizationIssueFieldsUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let organizationIssueFieldsExitGate = organizationIssueFieldsUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "Backlog, Ready, Paused, and Cancelled"
          "lifecycle Status is only its derived projection"
          "minimal not-yet-actionable, dependency, decision, external, and operator vocabulary"
          "priority Critical, High, Normal, and Low"
          "effort S, M, L, and XL"
          "target not before start"
          "severity Critical, High, Medium, Low, and Unset"
          "phase Planning, Execution, Verification, and Operations"
          "workstream Composition, Coordination, Docs, Governance, Lifecycle, and Versioning"
          "authoritative revision-bound records rather than competing ledgers"
          "complete fingerprinted corpus"
          "one deterministic canonical migration disposition"
          "preserving issue identity, native type, hierarchy, dependencies, repository scope, and lifecycle exemptions"
          "refuses missing, unknown, contradictory, ambiguous, lossy"
          "proves totality, uniqueness, deterministic ordering, idempotent replay"
          "omitted-combination inversions all fail closed"
          "without mutating GitHub"
          "without claiming GS2-05.3" ] do
        Assert.Contains(requiredTerm, organizationIssueFieldsExitGate)

    let intakeUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.3")

    Assert.Equal("Implement intake", intakeUnit.GetProperty("title").GetString())
    Assert.Equal("FS.GG.Coordination", intakeUnit.GetProperty("owner").GetString())
    Assert.Equal<string list>(
        [ "GS2-05.2" ],
        intakeUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        intakeUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-intake-contract" ],
        intakeUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let intakeExitGate = intakeUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "issues, native types and organization fields, Project membership, hierarchy, dependencies, and protocol initialization"
          "validate is pure, total, and deterministic"
          "complete revision-bound observation"
          "stable operation identities"
          "exact expected revisions and preconditions"
          "re-observes and proves every precondition and fencing revision"
          "authoritative post-state rather than response inference"
          "idempotent replay, partial-failure resume, roll-forward, and reverse-order compensation"
          "follows every page, proves terminal pagination and observation completeness"
          "distinguishes absent, unauthorized, unsupported, partial, stale, and indeterminate state"
          "Project fields as projections rather than an execution ledger"
          "missing-page, repeated-page, cursor-cycle"
          "altered-plan, reordered-operation, precondition-drift, postcondition-mismatch"
          "all fail closed"
          "no production write"
          "claims no GS2-05.4" ] do
        Assert.Contains(requiredTerm, intakeExitGate)

    let stagedIntakeUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.9")

    Assert.Equal("Implement staged intake admission", stagedIntakeUnit.GetProperty("title").GetString())
    Assert.Equal("FS.GG.Coordination", stagedIntakeUnit.GetProperty("owner").GetString())
    Assert.Equal<string list>(
        [ "GS2-05.3" ],
        stagedIntakeUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        stagedIntakeUnit.GetProperty("qGates").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-intake-contract" ],
        stagedIntakeUnit.GetProperty("gateCommands").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    let stagedExitGate = stagedIntakeUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "known or explicitly unknown root cause"
          "known or deferred verification"
          "declared or unspecified touch set"
          "exactly the six closed authority-read classes"
          "no more than six actual mutation effects"
          "unrelated Project and Backlog cardinality grows"
          "no organization-wide reconcile"
          "Ready promotion is a separate pure decision"
          "complete root cause, touch set, verification contract, dependency declaration, route decision"
          "cannot require claim or pull-request evidence"
          "fsgg.coord.intake/v1"
          "nine missing-promotion-fact"
          "without performing a production write"
          "without performing a production write or claiming GS2-05.4" ] do
        Assert.Contains(requiredTerm, stagedExitGate)

    let roadmapIntakeUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.4")

    Assert.Equal<string list>(
        [ "GS2-05.9" ],
        roadmapIntakeUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Contains("accepted GS2-05.9 receipt", roadmapIntakeUnit.GetProperty("exitGate").GetString())
    Assert.Equal<string list>(
        [ "Q3" ],
        roadmapIntakeUnit.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-roadmap-intake-contract" ],
        roadmapIntakeUnit.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    )
    let roadmapIntakeExitGate = roadmapIntakeUnit.GetProperty("exitGate").GetString()
    for requiredTerm in
        [ "stable source keys"; "native Epic"; "native parent and dependency edges"; "ISO start and target dates"
          "duplicate targets, foreign identity collisions, stale owned revisions"; "fixed operation cost"
          "Project membership and Project field values are projections only"; "drift inspection reports only authoritative owned"
          "idempotent replay, partial application, and indeterminate outcomes"; "without production GitHub mutation" ] do
        Assert.Contains(requiredTerm, roadmapIntakeExitGate)

    let claimTouchSetUnit =
        units
        |> List.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.5")

    Assert.Equal<string list>(
        [ "GS2-05.4" ],
        claimTouchSetUnit.GetProperty("prerequisites").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "Q3" ],
        claimTouchSetUnit.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    )
    Assert.Equal<string list>(
        [ "github-claim-touch-set-contract" ],
        claimTouchSetUnit.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    )
    Assert.Equal(
        "a6962b43fff0c109cb7d4cbcd5d39465b20a0f3fb71e28460a541b1ace04ad41",
        claimTouchSetUnit.GetProperty("contractSha256").GetString()
    )

    use acceptedOrganizationIssueFields =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.2.json"))
        )

    Assert.Equal(
        organizationIssueFieldsUnit.GetProperty("contractSha256").GetString(),
        acceptedOrganizationIssueFields.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedWorkTaxonomy =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.1.json"))
        )

    Assert.Equal(
        workTaxonomyUnit.GetProperty("contractSha256").GetString(),
        acceptedWorkTaxonomy.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedPrerequisite =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.2.json"))
        )

    Assert.Equal(
        issueFieldUnit.GetProperty("contractSha256").GetString(),
        acceptedPrerequisite.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedNativeRelation =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.3.json"))
        )

    Assert.Equal(
        nativeRelationUnit.GetProperty("contractSha256").GetString(),
        acceptedNativeRelation.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedProjectAdapter =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.4.json"))
        )

    Assert.Equal(
        projectAdapterUnit.GetProperty("contractSha256").GetString(),
        acceptedProjectAdapter.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedShardedJournal =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.6.json"))
        )

    Assert.Equal(
        shardedJournalUnit.GetProperty("contractSha256").GetString(),
        acceptedShardedJournal.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedRepositorySettings =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.7.json"))
        )

    Assert.Equal(
        repositorySettingsUnit.GetProperty("contractSha256").GetString(),
        acceptedRepositorySettings.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedActionsReleaseFeed =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.8.json"))
        )

    Assert.Equal(
        actionsReleaseFeedUnit.GetProperty("contractSha256").GetString(),
        acceptedActionsReleaseFeed.RootElement.GetProperty("unitContractSha256").GetString()
    )

    use acceptedSandboxClosure =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-04.9.json"))
        )

    Assert.Equal(
        sandboxClosureUnit.GetProperty("contractSha256").GetString(),
        acceptedSandboxClosure.RootElement.GetProperty("unitContractSha256").GetString()
    )

[<Fact>]
let ``gate catalog is literal dotnet only and matches selected unit`` () =
    use catalog =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/github-substrate-v2-gates.json")))

    use index =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/github-substrate-v2-units.json")))

    let commands =
        catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList

    Assert.Equal(27, commands.Length)

    for command in commands do
        Assert.Equal("dotnet", command.GetProperty("executable").GetString())

        for argument in command.GetProperty("args").EnumerateArray() do
            let value = argument.GetString()

            for token in [ "$"; "`"; ";"; "&&"; "||"; "|"; "\n"; "\r" ] do
                Assert.DoesNotContain(token, value)

    let selected =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-01.6")

    let contracts = selected.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    let selectedCommands =
        commands
        |> List.filter (fun command ->
            contracts
            |> List.exists (fun contract ->
                contract.GetProperty("id").GetString() = command.GetProperty("id").GetString()))

    Assert.Equal(4, contracts.Length)

    for command, contract in List.zip selectedCommands contracts do
        Assert.Equal(command.GetProperty("id").GetString(), contract.GetProperty("id").GetString())
        Assert.Equal(command.GetProperty("qGate").GetString(), contract.GetProperty("qGate").GetString())

    let transportCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-transport-contract")
    Assert.Equal("Q3", transportCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-transport.fsx"; "--"; "." ],
        transportCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let issueFieldCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-issue-field-contract")
    Assert.Equal("Q3", issueFieldCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-issue-field.fsx"; "--"; "." ],
        issueFieldCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let nativeRelationCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-native-relation-contract")
    Assert.Equal("Q3", nativeRelationCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-native-relation.fsx"; "--"; "." ],
        nativeRelationCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let projectAdapterCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-project-adapter-contract")
    Assert.Equal("Q3", projectAdapterCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-project-adapter.fsx"; "--"; "." ],
        projectAdapterCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let commentProjectionCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-comment-projection-contract")
    Assert.Equal("Q3", commentProjectionCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-comment-projection.fsx"; "--"; "." ],
        commentProjectionCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let shardedJournalCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-sharded-journal-contract")
    Assert.Equal("Q3", shardedJournalCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-sharded-journal.fsx"; "--"; "." ],
        shardedJournalCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let repositorySettingsCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-repository-settings-contract")
    Assert.Equal("Q3", repositorySettingsCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-repository-settings.fsx"; "--"; "." ],
        repositorySettingsCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let actionsReleaseFeedCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-actions-release-feed-contract")
    Assert.Equal("Q3", actionsReleaseFeedCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-actions-release-feed.fsx"; "--"; "." ],
        actionsReleaseFeedCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let sandboxClosureCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-sandbox-closure-contract")
    Assert.Equal("Q4", sandboxClosureCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-sandbox-closure.fsx"; "--"; "." ],
        sandboxClosureCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let workTaxonomyCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-work-taxonomy-contract")
    Assert.Equal("Q2", workTaxonomyCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-work-taxonomy.fsx"; "--"; "." ],
        workTaxonomyCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let organizationIssueFieldsCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-organization-issue-fields-contract")
    Assert.Equal("Q2", organizationIssueFieldsCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-organization-issue-fields.fsx"; "--"; "." ],
        organizationIssueFieldsCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let intakeCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-intake-contract")
    Assert.Equal("Q3", intakeCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-intake.fsx"; "--"; "." ],
        intakeCommand.GetProperty("args").EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList
    )

    let intakeUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.3")
    let intakeGateContract = intakeUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    Assert.Equal(intakeCommand.GetProperty("id").GetString(), intakeGateContract.GetProperty("id").GetString())
    Assert.Equal(intakeCommand.GetProperty("qGate").GetString(), intakeGateContract.GetProperty("qGate").GetString())
    Assert.Equal(gateCommandSha256 intakeCommand, intakeGateContract.GetProperty("commandSha256").GetString())

    let stagedIntakeUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.9")
    let stagedIntakeGateContract = stagedIntakeUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    Assert.Equal(intakeCommand.GetProperty("id").GetString(), stagedIntakeGateContract.GetProperty("id").GetString())
    Assert.Equal(intakeCommand.GetProperty("qGate").GetString(), stagedIntakeGateContract.GetProperty("qGate").GetString())
    Assert.Equal(gateCommandSha256 intakeCommand, stagedIntakeGateContract.GetProperty("commandSha256").GetString())

    let roadmapIntakeUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.4")
    let roadmapIntakeCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-roadmap-intake-contract")
    Assert.Equal("Q3", roadmapIntakeCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-roadmap-intake.fsx"; "--"; "." ],
        roadmapIntakeCommand.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    )
    let roadmapIntakeGateContract = roadmapIntakeUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    Assert.Equal(roadmapIntakeCommand.GetProperty("id").GetString(), roadmapIntakeGateContract.GetProperty("id").GetString())
    Assert.Equal(roadmapIntakeCommand.GetProperty("qGate").GetString(), roadmapIntakeGateContract.GetProperty("qGate").GetString())
    Assert.Equal(gateCommandSha256 roadmapIntakeCommand, roadmapIntakeGateContract.GetProperty("commandSha256").GetString())

    let claimTouchSetUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-05.5")
    let claimTouchSetCommand =
        commands
        |> List.find (fun command -> command.GetProperty("id").GetString() = "github-claim-touch-set-contract")
    Assert.Equal("Q3", claimTouchSetCommand.GetProperty("qGate").GetString())
    Assert.Equal<string list>(
        [ "fsi"; "eng/validate-github-claim-touch-set.fsx"; "--"; "." ],
        claimTouchSetCommand.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
    )
    let claimTouchSetGateContract = claimTouchSetUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.exactlyOne
    Assert.Equal(claimTouchSetCommand.GetProperty("id").GetString(), claimTouchSetGateContract.GetProperty("id").GetString())
    Assert.Equal(claimTouchSetCommand.GetProperty("qGate").GetString(), claimTouchSetGateContract.GetProperty("qGate").GetString())
    Assert.Equal(gateCommandSha256 claimTouchSetCommand, claimTouchSetGateContract.GetProperty("commandSha256").GetString())

    let protocolUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.1")

    let protocolContracts =
        protocolUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        protocolContracts
        |> List.map (fun value -> value.GetProperty("qGate").GetString())
    )

    let observationUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.3")

    let observationContracts =
        observationUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        observationContracts
        |> List.map (fun value -> value.GetProperty("qGate").GetString())
    )

    let lifecycleUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.4")

    let lifecycleContracts =
        lifecycleUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        lifecycleContracts
        |> List.map (fun value -> value.GetProperty("qGate").GetString())
    )

    let streamUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.6")

    let streamContracts =
        streamUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        streamContracts
        |> List.map (fun value -> value.GetProperty("qGate").GetString())
    )

    let mutationUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.7")

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        mutationUnit.GetProperty("gateContracts").EnumerateArray()
        |> Seq.map (fun value -> value.GetProperty("qGate").GetString())
        |> Seq.toList
    )

    let durablePlanUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.8")

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        durablePlanUnit.GetProperty("gateContracts").EnumerateArray()
        |> Seq.map (fun value -> value.GetProperty("qGate").GetString())
        |> Seq.toList
    )

    let desiredStateUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.9")

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        desiredStateUnit.GetProperty("gateContracts").EnumerateArray()
        |> Seq.map (fun value -> value.GetProperty("qGate").GetString())
        |> Seq.toList
    )

    let authorityUnit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun unitValue -> unitValue.GetProperty("id").GetString() = "GS2-02.2")

    let authorityContracts =
        authorityUnit.GetProperty("gateContracts").EnumerateArray() |> Seq.toList

    Assert.Equal<string list>(
        [ "Q1"; "Q2" ],
        authorityContracts
        |> List.map (fun value -> value.GetProperty("qGate").GetString())
    )

[<Fact>]
let ``canonical Quint authority passes the independent static gate`` () =
    let exitCode, output, error =
        run
            "dotnet"
            [ "fsi"
              "eng/validate-canonical-quint-protocol.fsx"
              "--"
              "--root"
              "."
              "--static-only" ]

    Assert.Equal(0, exitCode)
    Assert.StartsWith("CANONICAL_QUINT_PROTOCOL_STATIC_OK", output)
    Assert.Equal("", error)

[<Theory>]
[<InlineData("q1", "failed", "not-run", "RECEIPT-SELF-TEST-Q1", JsonValueKind.Null)>]
[<InlineData("q2", "passed", "failed", "RECEIPT-SELF-TEST-Q2", JsonValueKind.String)>]
let ``canonical Quint failure receipts retain the failed phase`` phase q1 q2 code preparationKind =
    let receipt =
        Path.Combine(Path.GetTempPath(), $"fsgg-quint-failure-%s{phase}-" + Guid.NewGuid().ToString("N") + ".json")

    try
        let exitCode, _, error =
            run
                "dotnet"
                [ "fsi"
                  "eng/validate-canonical-quint-protocol.fsx"
                  "--"
                  "--root"
                  "."
                  "--output"
                  receipt
                  "--exercise-failure-receipt"
                  phase ]

        Assert.NotEqual(0, exitCode)
        Assert.Contains($"code=%s{code}", error)
        use document = JsonDocument.Parse(File.ReadAllBytes receipt)
        let value = document.RootElement
        Assert.Equal(q1, value.GetProperty("q1Outcome").GetString())
        Assert.Equal(q2, value.GetProperty("q2Outcome").GetString())
        Assert.Equal(code, value.GetProperty("failure").GetProperty("code").GetString())
        Assert.Equal(preparationKind, value.GetProperty("preparationSha256").ValueKind)
        Assert.Equal(0, value.GetProperty("positiveInvariantCount").GetInt32())
        Assert.Equal(0, value.GetProperty("negativeControlCount").GetInt32())
    finally
        if File.Exists receipt then File.Delete receipt

[<Fact>]
let ``canonical Quint retained process inventory near miss fails closed`` () =
    let receipt =
        Path.Combine(Path.GetTempPath(), "fsgg-quint-process-inventory-" + Guid.NewGuid().ToString("N") + ".json")

    try
        let exitCode, _, error =
            run
                "dotnet"
                [ "fsi"
                  "eng/validate-canonical-quint-protocol.fsx"
                  "--"
                  "--root"
                  "."
                  "--output"
                  receipt
                  "--exercise-failure-receipt"
                  "process-inventory" ]

        Assert.NotEqual(0, exitCode)
        Assert.Contains("code=PROCESS-INVENTORY-COVERAGE", error)
        Assert.Contains("exercise-required", error)
        use document = JsonDocument.Parse(File.ReadAllBytes receipt)
        let value = document.RootElement
        Assert.Equal("passed", value.GetProperty("q1Outcome").GetString())
        Assert.Equal("failed", value.GetProperty("q2Outcome").GetString())
        Assert.Equal(0, value.GetProperty("processCounts").GetProperty("external").GetInt32())
        Assert.Equal("PROCESS-INVENTORY-COVERAGE", value.GetProperty("failure").GetProperty("code").GetString())
    finally
        if File.Exists receipt then File.Delete receipt

[<Fact>]
let ``canonical Quint authority mutations fail closed`` () =
    let tempRoot =
        Path.Combine(Path.GetTempPath(), $"fsgg-quint-authority-test-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tempRoot) |> ignore

    let runMutation name mutate expectedCode =
        let clone = Path.Combine(tempRoot, name)

        let cloneExit, _, cloneError =
            run
                "git"
                [ "-c"
                  "advice.detachedHead=false"
                  "clone"
                  "--shared"
                  "--quiet"
                  root
                  clone ]

        Assert.Equal(0, cloneExit)
        Assert.Equal("", cloneError)
        mutate clone
        let failureReceipt = Path.Combine(clone, "failure-receipt.json")

        let exitCode, _, error =
            runAt
                clone
                "dotnet"
                [ "fsi"
                  "eng/validate-canonical-quint-protocol.fsx"
                  "--"
                  "--root"
                  "."
                  "--static-only"
                  "--output"
                  failureReceipt ]

        Assert.NotEqual(0, exitCode)
        Assert.Contains($"code={expectedCode}", error)
        Assert.True(File.Exists failureReceipt)
        use receipt = JsonDocument.Parse(File.ReadAllBytes failureReceipt)
        Assert.Equal("failed", receipt.RootElement.GetProperty("q1Outcome").GetString())
        Assert.Equal("not-run", receipt.RootElement.GetProperty("q2Outcome").GetString())
        Assert.Equal(expectedCode, receipt.RootElement.GetProperty("failure").GetProperty("code").GetString())
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("preparationSha256").ValueKind)

    try
        runMutation
            "source"
            (fun clone ->
                let path = Path.Combine(clone, "src/FS.GG.Coordination.Protocol/Protocol.md")
                File.AppendAllText(path, "\nbehavioral drift\n"))
            "SOURCE-DIGEST"

        runMutation
            "identity"
            (fun clone ->
                let path =
                    Path.Combine(clone, "src/FS.GG.Coordination.Protocol/Generated/typed-authority.json")

                File.WriteAllText(
                    path,
                    File.ReadAllText(path).Replace("FS.GG.SDD.Artifacts/1.5.0", "FS.GG.SDD.Artifacts/9.9.9")
                ))
            "PACKAGE"

        runMutation
            "generated-qnt"
            (fun clone ->
                let path = Path.Combine(clone, "src/FS.GG.Coordination.Protocol/protocol.qnt")
                File.WriteAllText(path, "module Rival {}")

                let addExit, _, addError =
                    runAt clone "git" [ "add"; "src/FS.GG.Coordination.Protocol/protocol.qnt" ]

                Assert.Equal(0, addExit)
                Assert.Equal("", addError))
            "GENERATED-QNT-TRACKED"

        let mutateOutputManifest name mutate expectedCode =
            runMutation
                name
                (fun clone ->
                    let path =
                        Path.Combine(clone, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/manifest.json")

                    let document = JsonNode.Parse(File.ReadAllText path).AsObject()
                    mutate document
                    File.WriteAllText(path, document.ToJsonString()))
                expectedCode

        mutateOutputManifest
            "compiled-output-duplicate"
            (fun document ->
                let outputs = document["outputs"].AsArray()
                outputs.Add(outputs[0].DeepClone()))
            "COMPILED-OUTPUT-COUNT"

        mutateOutputManifest
            "compiled-output-substitution"
            (fun document -> document["outputs"].AsArray().[0].AsObject()["family"] <- "COUT-Diagrams")
            "COMPILED-OUTPUT-ORDER"

        mutateOutputManifest
            "compiled-output-unsupported"
            (fun document -> document["outputs"].AsArray().[0].AsObject()["supported"] <- false)
            "COMPILED-OUTPUT-UNSUPPORTED"

        mutateOutputManifest
            "compiled-output-incomplete"
            (fun document -> document["outputs"].AsArray().[0].AsObject()["complete"] <- false)
            "COMPILED-OUTPUT-INCOMPLETE"

        mutateOutputManifest
            "compiled-output-stale"
            (fun document -> document["outputs"].AsArray().[0].AsObject()["fresh"] <- false)
            "COMPILED-OUTPUT-STALE"

        runMutation
            "compiled-output-missing-format"
            (fun clone ->
                File.Delete(
                    Path.Combine(
                        clone,
                        "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/projection-view.md"
                    )
                ))
            "COMPILED-OUTPUT-FILES"

        runMutation
            "compiled-output-content"
            (fun clone ->
                File.AppendAllText(
                    Path.Combine(clone, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/diagrams.md"),
                    "substituted\n"
                ))
            "COMPILED-OUTPUT-CONTENT"
    finally
        if Directory.Exists(tempRoot) then
            Directory.Delete(tempRoot, true)

[<Fact>]
let ``manifest refuses an external untracked unit index before evidence creation`` () =
    let tempRoot =
        Path.Combine(Path.GetTempPath(), $"fsgg-roadmap-index-test-{Guid.NewGuid():N}")

    let clone = Path.Combine(tempRoot, "candidate")
    Directory.CreateDirectory(tempRoot) |> ignore

    try
        let cloneExit, _, cloneError =
            run
                "git"
                [ "-c"
                  "advice.detachedHead=false"
                  "clone"
                  "--shared"
                  "--quiet"
                  root
                  clone ]

        Assert.Equal(0, cloneExit)
        Assert.Equal("", cloneError)
        let headExit, head, headError = run "git" [ "rev-parse"; "HEAD" ]
        Assert.Equal(0, headExit)
        Assert.Equal("", headError)

        let checkoutExit, _, checkoutError =
            runAt clone "git" [ "-c"; "advice.detachedHead=false"; "checkout"; "--quiet"; head ]

        Assert.Equal(0, checkoutExit)
        Assert.Equal("", checkoutError)
        let externalIndex = Path.Combine(tempRoot, "external-index.json")
        File.Copy(Path.Combine(clone, "eng/github-substrate-v2-units.json"), externalIndex)
        let externalRoadmap = Path.Combine(tempRoot, "roadmap.md")
        File.WriteAllText(externalRoadmap, "external bytes are read but must not reach contract evaluation")

        let cli =
            Path.Combine(root, "src/FS.GG.Coordination.Cli/bin/Release/net10.0/FS.GG.Coordination.Cli.dll")

        let exitCode, _, error =
            runAt
                clone
                "dotnet"
                [ cli
                  "roadmap-work"
                  "manifest"
                  "--index"
                  externalIndex
                  "--roadmap"
                  externalRoadmap
                  "--unit"
                  "GS2-01.6"
                  "--receipts"
                  "evidence/github-substrate-v2/accepted"
                  "--repo"
                  clone
                  "--created-at"
                  "2026-08-27T00:00:00Z"
                  "--artifact"
                  "skill=.agents/skills/github-substrate-v2-work/SKILL.md"
                  "--output"
                  "artifacts/roadmap-work/GS2-01.6/external-index.json" ]

        Assert.Contains("index: path escapes repository root", error)
        Assert.Equal(2, exitCode)
    finally
        if Directory.Exists(tempRoot) then
            Directory.Delete(tempRoot, true)

[<Fact>]
let ``roadmap command and skill contain no remote mutation or v1 completion route`` () =
    let paths =
        [ "src/FS.GG.Coordination.Qualification.Contracts/RoadmapWork.fs"
          "src/FS.GG.Coordination.Cli/RoadmapCommand.fs"
          ".agents/skills/github-substrate-v2-work/SKILL.md"
          "eng/github-substrate-v2-gates.json" ]

    let forbidden =
        [ "Octokit"
          "HttpClient"
          "GitHubClient"
          "gh api"
          "gh repo edit"
          "fsgg-coord-engine take"
          "fsgg-coord-engine claim"
          "repository_dispatch"
          "workflow_dispatch"
          "fsgg:delivery-completion"
          "fsgg:done" ]

    for path in paths do
        let text = File.ReadAllText(Path.Combine(root, path))

        for token in forbidden do
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``roadmap command output is confined to the ignored evidence boundary`` () =
    let source =
        File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Cli/RoadmapCommand.fs"))

    Assert.Contains("artifacts", source)
    Assert.Contains("roadmap-work", source)
    Assert.Contains("symlink or reparse-point path is refused", source)
    Assert.Contains("candidate worktree is not clean", source)
