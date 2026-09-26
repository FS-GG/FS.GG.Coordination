open System
open System.IO
open System.Text.Json

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let planPath = Path.Combine(root, "eng/optimistic-qualification-plan.json")
let document = JsonDocument.Parse(File.ReadAllBytes planPath)
let plan = document.RootElement

if
    plan.GetProperty("schema").GetString()
    <> "fsgg.coordination.optimistic-qualification-plan/1"
then
    failwith "unsupported plan"

if
    plan.GetProperty("selection").GetProperty("nightlyRecoveryMaxConcurrentCandidates").GetInt32()
    <> 2
then
    failwith "nightly recovery candidate bound must be two"

if
    plan.GetProperty("selection").GetProperty("priorAggregateCandidateLimit").GetInt32()
    <> 25
then
    failwith "prior aggregate candidate search bound must be twenty-five"

if
    plan.GetProperty("coherent").GetProperty("maxPartitionsPerCandidate").GetInt32()
    <> 6
then
    failwith "partition bound must be six"

if plan.GetProperty("coherent").GetProperty("cancelInProgress").GetBoolean() then
    failwith "coherent runs cannot be cancelled"

let fanout = plan.GetProperty("formalFanout")

if fanout.GetProperty("logicalPartition").GetInt32() <> 1 then
    failwith "formal logical partition must remain one"

if fanout.GetProperty("maxConcurrentExecutions").GetInt32() <> 6 then
    failwith "formal fanout must share the six-execution bound"

let semanticShards =
    fanout.GetProperty("semanticShards").EnumerateArray()
    |> Seq.map _.GetString()
    |> Seq.toList

let quintPlan =
    JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/quint-qualification.json")))

let performanceShard = fanout.GetProperty("performanceShard").GetString()

let expectedSemantic =
    "base"
    :: (quintPlan.RootElement.GetProperty("formalTests").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("id").GetString())
        |> Seq.filter ((<>) performanceShard)
        |> Seq.toList)

if
    semanticShards.Head <> "base"
    || Set.ofList semanticShards <> Set.ofList expectedSemantic
    || semanticShards.Length <> (semanticShards |> List.distinct |> List.length)
then
    failwith "formal fanout must exactly retain base and every non-performance formal scenario"

if fanout.GetProperty("performanceShard").GetString() <> "epoch" then
    failwith "formal performance shard must remain epoch"

let shadow = plan.GetProperty("offlineFormalShadow")

if
    shadow.GetProperty("shard").GetString() <> "authority-reconciliation"
    || shadow.GetProperty("enabled").GetBoolean()
    || shadow.GetProperty("fragmentArtifact").GetString() <> "coherent-formal-fragment"
then
    failwith "offline formal shadow must remain disabled for the named shard"

let workflow =
    Path.Combine(root, ".github/workflows/optimistic-parallel-validation.yml")

let template = Path.Combine(root, "eng/optimistic-parallel-validation.yml.template")
let expected = File.ReadAllText template
let checking = fsi.CommandLineArgs |> Array.contains "--check"

if
    checking
    && (not (File.Exists workflow) || File.ReadAllText workflow <> expected)
then
    failwith "optimistic validation workflow projection is stale"

if not checking then
    File.WriteAllText(workflow, expected, Text.UTF8Encoding(false))

let text = File.ReadAllText workflow

for required in
    [
        "group: optimistic-coherent-${{ inputs.candidate_sha || github.event.pull_request.head.sha || github.event.merge_group.head_sha || github.sha }}"
        "cancel-in-progress: false"
        "fail-fast: false"
        "max-parallel: 6"
        "cron: '17 3 * * *'"
        "prepare:"
        "needs: [prepare, classify-reuse]"
        "run-partition:"
        "formal-aggregate:"
        "aggregate:"
        "shard: base"
        "shard: epoch"
        "offline-formal-shadow:"
        "if: ${{ false && github.event_name == 'pull_request' }}"
        "bash \"$FSGG_TRUSTED_ROOT/eng/offline-formal-join.sh\""
    ] do
    if not (text.Contains required) then
        failwith $"workflow projection missing {required}"

for shard in semanticShards @ [ performanceShard ] do
    let token = $"- {{ kind: formal, shard: %s{shard} }}"

    if text.Split(token, StringSplitOptions.None).Length <> 2 then
        failwith $"workflow projection must schedule formal shard exactly once: %s{shard}"

for partition in [ 0; 2; 3; 4; 5 ] do
    let token = $"- {{ kind: partition, partition: %d{partition} }}"

    if text.Split(token, StringSplitOptions.None).Length <> 2 then
        failwith $"workflow projection must schedule nonformal partition exactly once: %d{partition}"

if text.Contains("kind: partition, partition: 1") then
    failwith "workflow must reserve logical partition one for formal aggregation"

printfn "optimistic validation projection is current"
