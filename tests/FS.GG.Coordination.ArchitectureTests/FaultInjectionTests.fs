module FS.GG.Coordination.FaultInjectionArchitectureTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private artifactRelative = "src/FS.GG.Coordination.Qualification.Contracts/Generated/fault-injection.json"
let private artifactPath = Path.Combine(root, artifactRelative)

let private parseArtifact () = JsonNode.Parse(File.ReadAllBytes artifactPath).AsObject()
let private canonicalBytes (node: JsonNode) = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n")

let private mutate action =
    let document = parseArtifact ()
    action document
    canonicalBytes document

let private assertRejected expected bytes =
    match FaultInjection.validate root bytes with
    | Ok _ -> failwith "mutated fault matrix unexpectedly validated"
    | Error error -> Assert.StartsWith(expected, error)

[<Fact>]
let ``committed matrix is deterministic complete and source bound`` () =
    let committed = File.ReadAllBytes artifactPath
    let generated = FaultInjection.generate root |> Result.defaultWith failwith
    Assert.Equal<byte>(committed, generated)
    let summary = FaultInjection.validate root committed |> Result.defaultWith failwith
    Assert.Equal(4, summary.ExternalStepCount)
    Assert.Equal(15, summary.ScenarioCount)
    Assert.Equal(11, summary.ConvergedCount)
    Assert.Equal(4, summary.RefusedCount)
    Assert.Equal("7802d4fd2d955a123e59d8e095ca227484e37254a0f60691e107f45cc302bcbf", summary.SelfSha256)

[<Fact>]
let ``every modeled external step has before and after convergence`` () =
    let document = parseArtifact ()
    let steps = document["externalSteps"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList
    let executions = document["executions"].AsArray()
    let byId = executions |> Seq.map (fun item -> item["id"].GetValue<string>(), item) |> Map.ofSeq
    Assert.Equal(4, steps.Length)
    for step in steps do
        for boundary in [ "before"; "after" ] do
            let scenario = byId[$"%s{boundary}/%s{step}"]
            Assert.Equal("converged", scenario["outcome"].GetValue<string>())
            Assert.Null(scenario["refusalCode"])
            Assert.True(scenario["trace"].AsArray().Count > steps.Length)

[<Fact>]
let ``transport shaped ambiguity converges or refuses with exact codes`` () =
    let document = parseArtifact ()
    let byId =
        document["executions"].AsArray()
        |> Seq.map (fun item -> item["id"].GetValue<string>(), item)
        |> Map.ofSeq
    for id in [ "lost-response"; "duplicate-event"; "reordered-events" ] do
        Assert.Equal("converged", (byId[id]["outcome"]).GetValue<string>())
        Assert.Null(byId[id]["refusalCode"])
    let refusals =
        [ "partial-page", "OBS-Incomplete"
          "rate-budget-exhausted", "MOUT-RateLimited"
          "permission-revoked", "OBS-Unauthorized"
          "concurrent-revision", "MOUT-RevisionConflict" ]
    for id, code in refusals do
        Assert.Equal("refused", (byId[id]["outcome"]).GetValue<string>())
        Assert.Equal(code, (byId[id]["refusalCode"]).GetValue<string>())

[<Theory>]
[<InlineData("malformed", "FI-ARTIFACT-MALFORMED")>]
[<InlineData("canonical", "FI-ARTIFACT-CANONICAL")>]
[<InlineData("source", "FI-ARTIFACT-SOURCE")>]
[<InlineData("step", "FI-STEP-INVENTORY")>]
[<InlineData("missing", "FI-SCENARIO-COUNT")>]
[<InlineData("order", "FI-SCENARIO-ORDER")>]
[<InlineData("outcome", "FI-EXECUTION-TRACE")>]
[<InlineData("trace", "FI-EXECUTION-TRACE")>]
[<InlineData("digest", "FI-SELF-DIGEST")>]
let ``independent inversions fail closed`` name expected =
    let bytes =
        match name with
        | "malformed" -> Encoding.UTF8.GetBytes("{not-json")
        | "canonical" ->
            let document = parseArtifact ()
            Encoding.UTF8.GetBytes(document.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
        | "source" -> mutate (fun document -> document["source"]["contractSha256"] <- String.replicate 64 "0")
        | "step" -> mutate (fun document -> document["externalSteps"].AsArray().RemoveAt(0))
        | "missing" -> mutate (fun document -> document["executions"].AsArray().RemoveAt(0))
        | "order" -> mutate (fun document ->
            let scenarios = document["executions"].AsArray()
            let first = scenarios[0].DeepClone()
            scenarios[0] <- scenarios[1].DeepClone()
            scenarios[1] <- first)
        | "outcome" -> mutate (fun document ->
            let scenario = document["executions"].AsArray()[0]
            scenario["outcome"] <- "refused")
        | "trace" -> mutate (fun document ->
            let execution = document["executions"].AsArray()[0]
            execution["trace"].AsArray().RemoveAt(0))
        | "digest" -> mutate (fun document -> document["selfSha256"] <- String.replicate 64 "0")
        | unknown -> invalidArg "name" unknown
    assertRejected expected bytes

[<Fact>]
let ``independent oracle accepts the executed subject and rejects every subject defect`` () =
    let executions = FaultInjection.execute root FaultInjection.SubjectDefect.None |> Result.defaultWith failwith
    FaultInjectionOracle.validate root executions |> Result.defaultWith failwith
    let defects =
        [ FaultInjection.SubjectDefect.SkipRetry
          FaultInjection.SubjectDefect.DuplicateIsApplied
          FaultInjection.SubjectDefect.PreserveArrivalOrder
          FaultInjection.SubjectDefect.AcceptPartialPage
          FaultInjection.SubjectDefect.IgnoreRateBudget
          FaultInjection.SubjectDefect.IgnorePermission
          FaultInjection.SubjectDefect.IgnoreRevision ]
    for defect in defects do
        let mutated = FaultInjection.execute root defect |> Result.defaultWith failwith
        match FaultInjectionOracle.validate root mutated with
        | Ok _ -> failwith $"subject defect %A{defect} escaped the independent oracle"
        | Error error -> Assert.StartsWith("FIO-", error)

[<Fact>]
let ``semantic oracle rejects duplicate and reorder defects even with healthy labels restored`` () =
    let healthy = FaultInjection.execute root FaultInjection.SubjectDefect.None |> Result.defaultWith failwith
    let healthyById = healthy |> List.map (fun execution -> execution.Id, execution) |> Map.ofList
    let probes =
        [ FaultInjection.SubjectDefect.DuplicateIsApplied, "duplicate-event", "FIO-DUPLICATE"
          FaultInjection.SubjectDefect.PreserveArrivalOrder, "reordered-events", "FIO-REORDER" ]
    for defect, id, expected in probes do
        let forged =
            FaultInjection.execute root defect
            |> Result.defaultWith failwith
            |> List.map (fun execution ->
                if execution.Id = id then { execution with Trace = healthyById[id].Trace }
                else execution)
        Assert.NotEqual(healthyById[id].FinalStateSha256, (forged |> List.find (fun execution -> execution.Id = id)).FinalStateSha256)
        match FaultInjectionOracle.validate root forged with
        | Ok _ -> failwith $"semantic defect %A{defect} escaped after trace-label restoration"
        | Error error -> Assert.StartsWith(expected, error)
