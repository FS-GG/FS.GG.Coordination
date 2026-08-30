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
    Assert.Equal(12, summary.ExternalStepCount)
    Assert.Equal(31, summary.ScenarioCount)
    Assert.Equal(27, summary.ConvergedCount)
    Assert.Equal(4, summary.RefusedCount)
    Assert.Equal("c7a1d1372dd341f57e6f591f2b8a7c77783124de48828b8a61645cbb7eee5c84", summary.SelfSha256)

[<Fact>]
let ``every modeled external step has before and after convergence`` () =
    let document = parseArtifact ()
    let steps = document["externalSteps"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList
    let scenarios = document["scenarios"].AsArray()
    let byId = scenarios |> Seq.map (fun item -> item["id"].GetValue<string>(), item) |> Map.ofSeq
    Assert.Equal(12, steps.Length)
    for step in steps do
        for boundary in [ "before"; "after" ] do
            let scenario = byId[$"%s{boundary}/%s{step}"]
            Assert.Equal("converged", scenario["outcome"].GetValue<string>())
            Assert.Null(scenario["refusalCode"])

[<Fact>]
let ``transport shaped ambiguity converges or refuses with exact codes`` () =
    let document = parseArtifact ()
    let byId =
        document["scenarios"].AsArray()
        |> Seq.map (fun item -> item["id"].GetValue<string>(), item)
        |> Map.ofSeq
    for id in [ "lost-response"; "duplicate-event"; "reordered-events" ] do
        Assert.Equal("converged", (byId[id]["outcome"]).GetValue<string>())
        Assert.Null(byId[id]["refusalCode"])
    let refusals =
        [ "partial-page", "FI-PARTIAL-PAGE"
          "rate-budget-exhausted", "FI-RATE-BUDGET-EXHAUSTED"
          "permission-revoked", "FI-PERMISSION-REVOKED"
          "concurrent-revision", "FI-REVISION-CONFLICT" ]
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
[<InlineData("outcome", "FI-SCENARIO-OUTCOME")>]
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
        | "missing" -> mutate (fun document -> document["scenarios"].AsArray().RemoveAt(0))
        | "order" -> mutate (fun document ->
            let scenarios = document["scenarios"].AsArray()
            let first = scenarios[0].DeepClone()
            scenarios[0] <- scenarios[1].DeepClone()
            scenarios[1] <- first)
        | "outcome" -> mutate (fun document ->
            let scenario = document["scenarios"].AsArray()[0]
            scenario["outcome"] <- "refused")
        | "digest" -> mutate (fun document -> document["selfSha256"] <- String.replicate 64 "0")
        | unknown -> invalidArg "name" unknown
    assertRejected expected bytes
