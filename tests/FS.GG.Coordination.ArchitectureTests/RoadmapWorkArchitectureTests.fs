module FS.GG.Coordination.RoadmapWorkArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
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
let ``roadmap unit index advances through GS2-02-3 without the rejected runtime branch`` () =
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
             "GS2-02.3" ]
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
