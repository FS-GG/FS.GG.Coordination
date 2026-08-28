module FS.GG.Coordination.RoadmapWorkArchitectureTests

open System
open System.Diagnostics
open System.IO
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

[<Fact>]
let ``hosted compiler gate invokes the exact canonical Quint Q1 and Q2 subject`` () =
    let workflow =
        File.ReadAllText(Path.Combine(root, ".github/workflows/bootstrap-qualification.yml"))

    let qualification =
        File.ReadAllText(Path.Combine(root, "eng/qualify-canonical-quint.sh"))

    let validator =
        File.ReadAllText(Path.Combine(root, "eng/validate-canonical-quint-protocol.fsx"))

    Assert.Contains("run: bash eng/qualify-canonical-quint.sh", workflow)
    Assert.Contains("dotnet fsi eng/validate-canonical-quint-protocol.fsx -- --root . --compiler-only", qualification)
    Assert.Contains("dotnet fsi eng/validate-canonical-quint-protocol.fsx -- --root .", qualification)
    Assert.Contains("quint-linux-amd64", qualification)
    Assert.Contains("sha256sum --check --status", qualification)
    Assert.Contains("evidence --root . --work 66-gs2-02-11-deterministic-identity", qualification)
    Assert.Contains("--sync-observed-run artifacts/test-results/66-gs2-02-11-deterministic-identity/architecture-tests.trx", qualification)
    Assert.Contains("fsgg-sdd\" analyze --root . --work 66-gs2-02-11-deterministic-identity", qualification)
    Assert.Contains("fsgg-sdd\" verify --root . --work 66-gs2-02-11-deterministic-identity", qualification)
    Assert.Contains("fsgg-sdd\" ship --root . --work 66-gs2-02-11-deterministic-identity", qualification)
    Assert.Contains("sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0", workflow)
    Assert.Contains("/usr/bin/unshare --user --map-root-user --net -- /usr/bin/true", workflow)
    Assert.Contains("equivalent-named-block-partition", validator)
    Assert.Contains("equivalent-fence-indentation", validator)
    Assert.Contains("equivalent-crlf", validator)
    Assert.Contains("equivalent-quint-trivia", validator)

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
let ``roadmap unit index advances through GS2-02-11 without the rejected runtime branch`` () =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/github-substrate-v2-units.json")))

    let units = document.RootElement.GetProperty("units").EnumerateArray() |> Seq.toList

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
             "GS2-02.11" ]
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

[<Fact>]
let ``gate catalog is literal dotnet only and matches selected unit`` () =
    use catalog =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/github-substrate-v2-gates.json")))

    use index =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/github-substrate-v2-units.json")))

    let commands =
        catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList

    Assert.Equal(8, commands.Length)

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

        let exitCode, _, error =
            runAt
                clone
                "dotnet"
                [ "fsi"
                  "eng/validate-canonical-quint-protocol.fsx"
                  "--"
                  "--root"
                  "."
                  "--static-only" ]

        Assert.NotEqual(0, exitCode)
        Assert.Contains($"code={expectedCode}", error)

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
            "rival"
            (fun clone ->
                File.WriteAllText(
                    Path.Combine(clone, "src/FS.GG.Coordination.Protocol/Rival.fs"),
                    "type Subject = | Rival"
                ))
            "PARALLEL-AST"

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

        Assert.Equal(2, exitCode)
        Assert.Contains("index: path escapes repository root", error)
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
