module FS.GG.Coordination.Orchestration.Host.Tests.ChoreoTraceTests

open System
open System.IO
open System.Text.Json.Nodes
open FS.GG.SDD.Artifacts.TypedSpecifications
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
