open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json

let expectedPackage = "FS.GG.SDD.Artifacts/1.5.0"
let expectedProfile = "fsgg-quint-profile/2"

let expectedToolchain =
    "79b32dacc5bb150e23c4017eef16f3f688cde062441583d5ea1ffa5cc9e62486"

let expectedQuint =
    "939b64095b706017f2f202c6f99c860c40be7c31bddc2b98557316e50f42cd7f"

let expectedLmt = "37e0b0365c2641edce40b48605471f61fa12e97c3e2376152f0e849abdc31f10"

let expectedSource =
    "6a27deb32cbb096a273f5ac03f320667850d460a86fbfa6885fca44426dac14a"

let expectedContract =
    "581d45c6db8d302df5a2ea6413d4d2cf4107388617ef6f820c55bf4fbb4d4e1b"

let expectedApalacheJar =
    "4753c0ebb2cbb266e2c6ac19ab5ca3827d726cc80fd1fc5d7c1eeb64736cd60b"

let fail code detail =
    eprintfn "CANONICAL_QUINT_PROTOCOL_RED code=%s detail=%s" code detail
    exit 1

let sha256 path =
    use stream = File.OpenRead path
    SHA256.HashData stream |> Convert.ToHexString |> _.ToLowerInvariant()

let requireFile code path =
    if not (File.Exists path) then
        fail code path

let run workingDirectory executable arguments environment =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- workingDirectory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    for argument in arguments do
        info.ArgumentList.Add argument

    for name, value in environment do
        info.Environment[name] <- value

    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, output.Result.Trim(), error.Result.Trim()

let requireGreen code workingDirectory executable arguments environment =
    let exitCode, output, error = run workingDirectory executable arguments environment

    if exitCode <> 0 then
        fail code ($"exit={exitCode}; stdout={output}; stderr={error}")

    output, error

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec parse root staticOnly compilerOnly remaining =
    match remaining with
    | [] -> root, staticOnly, compilerOnly
    | "--root" :: value :: tail -> parse (Path.GetFullPath value) staticOnly compilerOnly tail
    | "--static-only" :: tail -> parse root true compilerOnly tail
    | "--compiler-only" :: tail -> parse root staticOnly true tail
    | value :: _ -> fail "ARGUMENT" value

let root, staticOnly, compilerOnly =
    parse (Path.GetFullPath ".") false false arguments

let source = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")

let selectors =
    Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

let retained = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated")
let authority = Path.Combine(retained, "typed-authority.json")
let contract = Path.Combine(retained, "contract.json")
let binding = Path.Combine(retained, "Protocol.Generated.fs")
let sourceMap = Path.Combine(retained, "source-map.json")
let receipt = Path.Combine(retained, "receipt.json")

for code, path in
    [ "SOURCE-MISSING", source
      "SELECTOR-MISSING", selectors
      "AUTHORITY-MISSING", authority
      "CONTRACT-MISSING", contract
      "BINDING-MISSING", binding
      "SOURCE-MAP-MISSING", sourceMap
      "RECEIPT-MISSING", receipt ] do
    requireFile code path

if sha256 source <> expectedSource then
    fail "SOURCE-DIGEST" (sha256 source)

if sha256 contract <> expectedContract then
    fail "CONTRACT-DIGEST" (sha256 contract)

let authorityDocument = JsonDocument.Parse(File.ReadAllBytes authority)
let authorityRoot = authorityDocument.RootElement

if authorityRoot.GetProperty("schemaVersion").GetInt32() <> 2 then
    fail "AUTHORITY-SCHEMA" "not-v2"

if authorityRoot.GetProperty("backend").GetString() <> "quint-specification-v1" then
    fail "BACKEND" "wrong"

if authorityRoot.GetProperty("profileIdentity").GetString() <> expectedProfile then
    fail "PROFILE" "wrong"

if authorityRoot.GetProperty("packageIdentity").GetString() <> expectedPackage then
    fail "PACKAGE" "wrong"

if authorityRoot.GetProperty("toolchainIdentity").GetString() <> expectedToolchain then
    fail "TOOLCHAIN" "wrong"

let contractDocument = JsonDocument.Parse(File.ReadAllBytes contract)
let contractRoot = contractDocument.RootElement

if
    contractRoot.GetProperty("schema").GetString()
    <> "fsgg.quint.compiled-contract/v2"
then
    fail "CONTRACT-SCHEMA" "wrong"

if contractRoot.GetProperty("profile").GetString() <> expectedProfile then
    fail "CONTRACT-PROFILE" "wrong"

if contractRoot.GetProperty("catalogue").GetArrayLength() <> 80 then
    fail "CATALOGUE" "wrong-cardinality"

if contractRoot.GetProperty("relationships").GetArrayLength() <> 17 then
    fail "RELATIONSHIPS" "wrong-cardinality"

if contractRoot.GetProperty("actionEffects").GetArrayLength() <> 12 then
    fail "ACTIONS" "wrong-cardinality"

let trackedExit, trackedQnt, trackedError =
    run root "git" [ "ls-files"; "*.qnt" ] []

if trackedExit <> 0 then
    fail "GIT-INVENTORY" trackedError

if not (String.IsNullOrWhiteSpace trackedQnt) then
    fail "GENERATED-QNT-TRACKED" trackedQnt

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
    if authoredFsharp.Contains(rival, StringComparison.Ordinal) then
        fail "PARALLEL-AST" rival

if staticOnly then
    printfn "CANONICAL_QUINT_PROTOCOL_STATIC_OK contract=%s profile=%s" expectedContract expectedProfile
    exit 0

let cacheValue = Environment.GetEnvironmentVariable "FSGG_QUINT_CACHE"

if String.IsNullOrWhiteSpace cacheValue then
    fail "CACHE" "FSGG_QUINT_CACHE is required"

let cache = Path.GetFullPath cacheValue
let quint = Path.Combine(cache, "objects", expectedQuint)
let lmt = Path.Combine(cache, "objects", expectedLmt)
requireFile "QUINT-MISSING" quint
requireFile "LMT-MISSING" lmt

if sha256 quint <> expectedQuint then
    fail "QUINT-DIGEST" (sha256 quint)

if sha256 lmt <> expectedLmt then
    fail "LMT-DIGEST" (sha256 lmt)

let cli =
    match Environment.GetEnvironmentVariable "FSGG_SDD_CLI" with
    | null
    | "" -> "fsgg-sdd"
    | value -> value

let version, _ = requireGreen "CLI-VERSION" root cli [ "--version" ] []

if version.Trim() <> "1.5.0" then
    fail "CLI-VERSION" version

let scratch =
    Path.Combine(Path.GetTempPath(), $"fsgg-canonical-quint-{Guid.NewGuid():N}")

try
    let scratchSource =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.md")

    let scratchSelectors =
        Path.Combine(scratch, "src/FS.GG.Coordination.Protocol/Protocol.bindings.json")

    Directory.CreateDirectory(Path.GetDirectoryName scratchSource) |> ignore
    File.Copy(source, scratchSource)
    File.Copy(selectors, scratchSelectors)

    requireGreen
        "AUTHOR"
        root
        cli
        [ "typed-sdd"
          "author"
          "--root"
          scratch
          "--work"
          "42-native-relation-algebra"
          "--title"
          "Implement native relation algebra"
          "--agent"
          "avocet-bdf8"
          "--session"
          "gs2-02-5-profile2"
          "--backend"
          "quint-specification-v1"
          "--profile"
          expectedProfile
          "--source"
          "src/FS.GG.Coordination.Protocol/Protocol.md"
          "--bindings"
          "src/FS.GG.Coordination.Protocol/Protocol.bindings.json"
          "--cache"
          cache ]
        []
    |> ignore

    requireGreen "INSPECT" root cli [ "typed-sdd"; "inspect"; "--root"; scratch; "--work"; "42-native-relation-algebra" ] []
    |> ignore

    let generatedRoot = Path.Combine(scratch, "readiness/42-native-relation-algebra")

    let comparisons =
        [ authority, Path.Combine(generatedRoot, "typed-authority.json")
          contract, Path.Combine(generatedRoot, "quint/contract.json")
          binding, Path.Combine(generatedRoot, "quint/bindings.fs")
          sourceMap, Path.Combine(generatedRoot, "quint/source-map.json")
          receipt, Path.Combine(generatedRoot, "quint/receipt.json") ]

    for expected, actual in comparisons do
        requireFile "REGEN-MISSING" actual

        if not (File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(actual))) then
            fail "STALE-PROJECTION" (Path.GetFileName expected)

    let qnt = Path.Combine(generatedRoot, "quint/protocol.qnt")
    requireGreen "QUINT-TYPECHECK" scratch quint [ "typecheck"; qnt ] [] |> ignore

    if compilerOnly then
        Directory.Delete(scratch, true)

        printfn
            "CANONICAL_QUINT_COMPILER_OK contract=%s source=%s profile=%s"
            expectedContract
            expectedSource
            expectedProfile

        exit 0

    requireGreen
        "QUINT-RUN"
        scratch
        quint
        [ "run"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "acceptedVocabularyIsQualified"
          "--max-steps"
          "4"
          "--max-samples"
          "20"
          "--seed"
          "1"
          "--verbosity"
          "0" ]
        []
    |> ignore

    requireGreen
        "QUINT-TEST"
        scratch
        quint
        [ "test"
          qnt
          "--main"
          "CoordinationProtocolTests"
          "--backend"
          "rust"
          "--match"
          "^test"
          "--verbosity"
          "0" ]
        []
    |> ignore

    let quintHome = Environment.GetEnvironmentVariable "FSGG_QUINT_HOME"
    let javaHome = Environment.GetEnvironmentVariable "JAVA_HOME"

    if String.IsNullOrWhiteSpace quintHome then
        fail "APALACHE-CACHE" "FSGG_QUINT_HOME is required"

    if String.IsNullOrWhiteSpace javaHome then
        fail "JAVA" "JAVA_HOME is required"

    let apalacheJar =
        Path.Combine(quintHome, "apalache-dist-0.56.1/apalache/lib/apalache.jar")

    let java = Path.Combine(javaHome, "bin/java")
    requireFile "APALACHE-MISSING" apalacheJar
    requireFile "JAVA-MISSING" java

    if sha256 apalacheJar <> expectedApalacheJar then
        fail "APALACHE-DIGEST" (sha256 apalacheJar)

    let environment =
        [ "QUINT_HOME", quintHome
          "PATH",
          Path.GetDirectoryName(java)
          + string Path.PathSeparator
          + Environment.GetEnvironmentVariable("PATH") ]

    requireGreen
        "QUINT-VERIFY"
        scratch
        quint
        [ "verify"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "acceptedVocabularyIsQualified"
          "--max-steps"
          "4"
          "--verbosity"
          "1" ]
        environment
    |> ignore

    requireGreen
        "QUINT-AUTHORITY-VERIFY"
        scratch
        quint
        [ "verify"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "acceptedAuthoritiesAreQualified"
          "--max-steps"
          "4"
          "--verbosity"
          "1" ]
        environment
    |> ignore

    requireGreen
        "QUINT-LIFECYCLE-VERIFY"
        scratch
        quint
        [ "verify"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "humanIntentIsObservationIndependent"
          "--max-steps"
          "4"
          "--verbosity"
          "1" ]
        environment
    |> ignore

    requireGreen
        "QUINT-LIFECYCLE-DERIVATION-VERIFY"
        scratch
        quint
        [ "verify"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "lifecycleStatusIsDerived"
          "--max-steps"
          "4"
          "--verbosity"
          "1" ]
        environment
    |> ignore

    requireGreen
        "QUINT-RELATION-VALIDITY-VERIFY"
        scratch
        quint
        [ "verify"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "nativeRelationEdgesAreValid"
          "--max-steps"
          "4"
          "--verbosity"
          "1" ]
        environment
    |> ignore

    requireGreen
        "QUINT-RELATION-PRESERVATION-VERIFY"
        scratch
        quint
        [ "verify"
          qnt
          "--main"
          "CoordinationProtocol"
          "--init"
          "init"
          "--step"
          "step"
          "--invariant"
          "relationChangesPreserveUnrelatedEdges"
          "--max-steps"
          "4"
          "--verbosity"
          "1" ]
        environment
    |> ignore

    let mutatedQnt = Path.Combine(scratch, "protocol-missing-evidence-guard.qnt")
    let originalQnt = File.ReadAllText qnt
    let guard = "    evidenceObserved,\n    evidenceObserved' = evidenceObserved,"

    if not (originalQnt.Contains(guard, StringComparison.Ordinal)) then
        fail "NEGATIVE-CONTROL" "guard fixture absent"

    File.WriteAllText(mutatedQnt, originalQnt.Replace(guard, "    evidenceObserved' = evidenceObserved,"))

    let counterexample = Path.Combine(scratch, "counterexample.itf.json")

    let redExit, redOutput, redError =
        run
            scratch
            quint
            [ "verify"
              mutatedQnt
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "acceptedVocabularyIsQualified"
              "--max-steps"
              "1"
              "--out-itf"
              counterexample
              "--verbosity"
              "1" ]
            environment

    if redExit = 0 then
        fail "NEGATIVE-CONTROL" "missing evidence guard passed"

    if not (File.Exists counterexample) then
        fail "NEGATIVE-CONTROL" ($"missing ITF; {redOutput}; {redError}")

    let lifecycleMutant = Path.Combine(scratch, "protocol-intent-follows-claim.qnt")
    let preservedIntent =
        "    humanIntentId' = humanIntentId,\n    authorizedHumanIntentId' = authorizedHumanIntentId,\n    lifecycleFacts' = facts,"
    let collapsedIntent =
        "    humanIntentId' = if (facts.claimPresent) \"INTENT-Ready\" else humanIntentId,\n    authorizedHumanIntentId' = authorizedHumanIntentId,\n    lifecycleFacts' = facts,"

    if not (originalQnt.Contains(preservedIntent, StringComparison.Ordinal)) then
        fail "LIFECYCLE-NEGATIVE-CONTROL" "intent-preservation fixture absent"

    File.WriteAllText(lifecycleMutant, originalQnt.Replace(preservedIntent, collapsedIntent))
    let lifecycleCounterexample = Path.Combine(scratch, "counterexample-intent-follows-claim.itf.json")

    let lifecycleRedExit, lifecycleRedOutput, lifecycleRedError =
        run
            scratch
            quint
            [ "verify"
              lifecycleMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "humanIntentIsObservationIndependent"
              "--max-steps"
              "1"
              "--out-itf"
              lifecycleCounterexample
              "--verbosity"
              "1" ]
            environment

    if lifecycleRedExit = 0 then
        fail "LIFECYCLE-NEGATIVE-CONTROL" "claim-to-intent collapse passed"

    if not (File.Exists lifecycleCounterexample) then
        fail
            "LIFECYCLE-NEGATIVE-CONTROL"
            ($"missing ITF; {lifecycleRedOutput}; {lifecycleRedError}")

    let replacementMutant = Path.Combine(scratch, "protocol-relation-whole-set-replacement.qnt")
    let edgeLocalAdd = "    nativeRelationEdges' = nativeRelationEdges.union(Set(edge)),"
    let wholeSetReplacement = "    nativeRelationEdges' = Set(edge),"

    if not (originalQnt.Contains(edgeLocalAdd, StringComparison.Ordinal)) then
        fail "RELATION-NEGATIVE-CONTROL" "edge-local add fixture absent"

    File.WriteAllText(replacementMutant, originalQnt.Replace(edgeLocalAdd, wholeSetReplacement))
    let replacementCounterexample = Path.Combine(scratch, "counterexample-relation-whole-set-replacement.itf.json")

    let replacementRedExit, replacementRedOutput, replacementRedError =
        run
            scratch
            quint
            [ "verify"
              replacementMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "relationChangesPreserveUnrelatedEdges"
              "--max-steps"
              "2"
              "--out-itf"
              replacementCounterexample
              "--verbosity"
              "1" ]
            environment

    if replacementRedExit = 0 then
        fail "RELATION-NEGATIVE-CONTROL" "whole-set replacement passed"

    if not (File.Exists replacementCounterexample) then
        fail
            "RELATION-NEGATIVE-CONTROL"
            ($"whole-set replacement missing ITF; {replacementRedOutput}; {replacementRedError}")

    let selfEdgeMutant = Path.Combine(scratch, "protocol-relation-self-edge.qnt")
    let validRelationStep = "    addNativeRelation(parentChildEdge),"
    let invalidRelationStep = "    addNativeRelation({ ...parentChildEdge, targetId: parentChildEdge.sourceId }),"
    let relationGuard = "    nativeRelationEdgeIsValid(edge),\n    evidenceObserved' = evidenceObserved,"

    if not (originalQnt.Contains(validRelationStep, StringComparison.Ordinal)) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "relation step fixture absent"

    if not (originalQnt.Contains(relationGuard, StringComparison.Ordinal)) then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "relation guard fixture absent"

    let withoutRelationGuard =
        originalQnt.Replace(relationGuard, "    evidenceObserved' = evidenceObserved,")

    File.WriteAllText(selfEdgeMutant, withoutRelationGuard.Replace(validRelationStep, invalidRelationStep))
    let selfEdgeCounterexample = Path.Combine(scratch, "counterexample-relation-self-edge.itf.json")

    let selfEdgeRedExit, selfEdgeRedOutput, selfEdgeRedError =
        run
            scratch
            quint
            [ "verify"
              selfEdgeMutant
              "--main"
              "CoordinationProtocol"
              "--init"
              "init"
              "--step"
              "step"
              "--invariant"
              "nativeRelationEdgesAreValid"
              "--max-steps"
              "1"
              "--out-itf"
              selfEdgeCounterexample
              "--verbosity"
              "1" ]
            environment

    if selfEdgeRedExit = 0 then
        fail "RELATION-VALIDITY-NEGATIVE-CONTROL" "self edge passed"

    if not (File.Exists selfEdgeCounterexample) then
        fail
            "RELATION-VALIDITY-NEGATIVE-CONTROL"
            ($"self edge missing ITF; {selfEdgeRedOutput}; {selfEdgeRedError}")

    let requireAuthorityRed name (observationMutation: string -> string) (sourceMutation: string -> string) =
        let mutated = Path.Combine(scratch, $"protocol-%s{name}.qnt")

        let withoutQualificationGuard =
            originalQnt.Replace(
                "    authorityObservationIsQualified(authorityObservation),\n    evidenceObserved' = evidenceObserved,",
                "    evidenceObserved' = evidenceObserved,"
            )

        if withoutQualificationGuard = originalQnt then
            fail "AUTHORITY-NEGATIVE-CONTROL" "qualification guard fixture absent"

        let changedObservation = observationMutation withoutQualificationGuard
        let changedSource = sourceMutation changedObservation
        File.WriteAllText(mutated, changedSource)
        let outItf = Path.Combine(scratch, $"counterexample-%s{name}.itf.json")

        let exitCode, output, error =
            run
                scratch
                quint
                [ "verify"
                  mutated
                  "--main"
                  "CoordinationProtocol"
                  "--init"
                  "init"
                  "--step"
                  "step"
                  "--invariant"
                  "acceptedAuthoritiesAreQualified"
                  "--max-steps"
                  "2"
                  "--out-itf"
                  outItf
                  "--verbosity"
                  "1" ]
                environment

        if exitCode = 0 then
            fail "AUTHORITY-NEGATIVE-CONTROL" ($"%s{name} passed")

        if not (File.Exists outItf) then
            fail "AUTHORITY-NEGATIVE-CONTROL" ($"%s{name} missing ITF; %s{output}; %s{error}")

    let mutateStep replacement (text: string) =
        text.Replace("    observeAuthority(nativeGitHubObservation),", $"    observeAuthority(%s{replacement}),")

    let unchanged (text: string) = text
    requireAuthorityRed "incomplete" (mutateStep "{ ...nativeGitHubObservation, complete: false }") unchanged

    requireAuthorityRed
        "stale-revision"
        (mutateStep "{ ...nativeGitHubObservation, revisionValue: \"stale-revision\" }")
        unchanged

    requireAuthorityRed
        "wrong-revision-kind"
        (mutateStep "{ ...nativeGitHubObservation, revisionKind: \"wrong-kind\" }")
        unchanged

    requireAuthorityRed
        "wrong-authority"
        (mutateStep "{ ...nativeGitHubObservation, authorityId: \"AUTH-PackageFeed\" }")
        unchanged

    requireAuthorityRed "contradictory" (mutateStep "{ ...nativeGitHubObservation, contradictory: true }") unchanged

    let omittedFamilyRow =
        "    { id: \"AUTH-ClassifiedExternal\", kind: \"authorityBinding\", family: \"classified-external\", revisionKind: \"classified-external-revision\", revisionValue: \"declared-source-revision\", completenessContract: \"complete-required-fields\", evidenceRelationship: \"REL-AUTH-ClassifiedExternal-Evidence\" }"

    requireAuthorityRed "omitted-family" (mutateStep "nativeGitHubObservation") (fun text ->
        text.Replace(omittedFamilyRow, ""))

    printfn
        "CANONICAL_QUINT_PROTOCOL_OK contract=%s source=%s profile=%s"
        expectedContract
        expectedSource
        expectedProfile
finally
    if Directory.Exists scratch then
        Directory.Delete(scratch, true)
