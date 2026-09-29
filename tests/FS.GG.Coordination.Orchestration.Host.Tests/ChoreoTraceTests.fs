module FS.GG.Coordination.Orchestration.Host.Tests.ChoreoTraceTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FsQuint
open Xunit

[<Fact>]
let ``all deterministic Choreo scenarios are raw identity-bound Quint traces`` () =
    let scenarios = ChoreoTrace.loadAll ()

    Assert.Equal(8, scenarios.Length)
    Assert.Equal(180, scenarios |> List.sumBy _.StateCount)
    Assert.Equal(69, scenarios |> List.sumBy _.Milestones.Length)

    scenarios
    |> List.iter (fun scenario ->
        Assert.Equal("safety", scenario.Invariant)
        Assert.Empty(QuintReplay.validateTrace scenario.Replay)
        Assert.Equal(scenario.Milestones.Length - 1, scenario.Replay.Steps.Length))

    let terminal id = (ChoreoTrace.load id).Milestones |> List.last |> _.Snapshot
    Assert.Equal(7, (terminal "happy-path").Stage)
    Assert.True((terminal "lost-applied").UnknownObserved)
    Assert.True((terminal "proven-absent-retry").RetryObserved)
    Assert.True((terminal "restart-gates").RestartObserved)
    Assert.True((terminal "duplicate-response").DuplicateRejected)
    Assert.True((terminal "stale-generation").StaleRejected)
    Assert.True((terminal "wrong-identity").IdentityRejected)
    Assert.Equal(6, (terminal "missing-native-readback").Stage)

[<Fact>]
let ``current Choreo manifest binds the qualified protocol while retaining exact traces`` () =
    let fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Choreo")
    let manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "manifest.json"))).AsObject()
    let source = manifest["source"].AsObject()
    let expectedSourceSha = "740c9e55cc02067d04f43eeeaae26a71ab492c96c921eb012bade0883a35d937"
    let protocolBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol.md"))

    Assert.Equal("38820f22535eabedefc3aa2590a05ab7498cb5c6", source["commit"].GetValue<string>())
    Assert.Equal(expectedSourceSha, source["sha256"].GetValue<string>())

    let actualSourceSha =
        protocolBytes |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    Assert.Equal(expectedSourceSha, actualSourceSha)

    for scenario in manifest["scenarios"].AsArray() do
        let value = scenario.AsObject()
        let actualTraceSha =
            File.ReadAllBytes(Path.Combine(fixtureRoot, value["file"].GetValue<string>()))
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        Assert.Equal(value["traceSha256"].GetValue<string>(), actualTraceSha)

    let changedProtocolBytes = Array.append protocolBytes [| byte '\n' |]
    let error =
        Assert.ThrowsAny<Exception>(fun () ->
            ChoreoTrace.validateSourceProvenance source changedProtocolBytes |> ignore)

    Assert.Contains("protocol source digest differs", error.Message)

[<Fact>]
let ``raw Choreo projection rejects a crossed generation even when JSON remains valid`` () =
    let path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Choreo", "happy-path.itf.json")
    let root = JsonNode.Parse(File.ReadAllBytes path).AsObject()
    let states = root["states"].AsArray()
    let state = states[2].AsObject()

    let host =
        let globalState = state["O2HostedWriterChoreoModel::choreo::s"].AsObject()
        let system = globalState["system"].AsObject()
        let processMap = system["#map"].AsArray()

        processMap
        |> Seq.map _.AsArray()
        |> Seq.find (fun pair -> pair[0].GetValue<string>() = "Host")
        |> fun pair ->
            let processState = pair[1].AsObject()
            let local = processState["local"].AsObject()
            local["value"].AsObject()

    let current = host["current"].AsObject()
    let operation = current["value"].AsObject()
    let generation = operation["generation"].AsObject()
    generation["#bigint"] <- "9"

    let error =
        Assert.ThrowsAny<Exception>(fun () -> ChoreoTrace.validateRawText (root.ToJsonString()))

    Assert.Contains("operation envelope differs", error.Message)

[<Fact>]
let ``projected replay rejects a state mutation instead of accepting a permissive projection`` () =
    let original = (ChoreoTrace.load "happy-path").Replay
    let first = original.Steps.Head

    let mutatedState =
        { first.Expected with
            Identity = String.replicate 64 "0"
        }

    let mutated =
        { original with
            Steps = { first with Expected = mutatedState } :: original.Steps.Tail
        }

    Assert.NotEmpty(QuintReplay.validateTrace mutated)
