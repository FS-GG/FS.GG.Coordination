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
    plan.GetProperty("coherent").GetProperty("pullRequestAdmission").GetString()
    <> "ready-only-with-explicit-dispatch"
then
    failwith "coherent pull request admission must defer drafts and retain explicit dispatch"

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
let offlineEnabled = shadow.GetProperty("enabled").GetBoolean()
let profiles = plan.GetProperty("profiles")
let scoped = profiles.GetProperty("scoped")

if profiles.GetProperty("default").GetString() <> "full" then
    failwith "unknown events must default to full CI"

let scopedPaths =
    scoped.GetProperty("exactModifiedPaths").EnumerateArray()
    |> Seq.map _.GetString()
    |> Seq.toList

if
    scoped.GetProperty("event").GetString() <> "ready-pull-request"
    || scoped.GetProperty("requiredDisposition").GetString() <> "reused"
    || scoped.GetProperty("donor").GetString() <> "authentic-complete-unexpired-full-six-partition-aggregate"
    || scopedPaths <> [ "README.md"; "src/FS.GG.Coordination.Cli/ObserverViewCommand.fs" ]
    || scoped.GetProperty("aggregateArtifactPrefix").GetString() <> "scoped-aggregate-"
then
    failwith "scoped profile must remain confined to audited paths and full prior evidence"

let scopedExecutions =
    scoped.GetProperty("executions").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList

if
    scopedExecutions
    <> [ "formal:base"; "partition:0"; "partition:2"; "partition:3"; "partition:4"; "partition:5" ]
then
    failwith "scoped execution must retain formal base and all five nonformal partitions"

if
    shadow.GetProperty("shard").GetString() <> "authority-reconciliation"
    || shadow.GetProperty("fragmentArtifact").GetString() <> "coherent-formal-fragment"
then
    failwith "offline formal pilot must retain the named shard and fragment artifact"

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
        "types: [opened, synchronize, reopened, ready_for_review]"
        "if: ${{ github.event_name != 'pull_request' || !github.event.pull_request.draft }}"
        "if: ${{ always() && needs.prepare.result != 'skipped' }}"
        "fail-fast: false"
        "max-parallel: 6"
        "cron: '17 3 * * *'"
        "prepare:"
        "needs: [prepare, classify-reuse]"
        "run-partition:"
        "formal-aggregate:"
        "aggregate:"
        "offline-formal-shadow:"
        "bash \"$FSGG_TRUSTED_ROOT/eng/offline-formal-join.sh\""
        "matrix: ${{ fromJSON(needs.classify-reuse.outputs.matrix) }}"
        "python3 eng/optimistic-profile.py"
        "FSGG_PR_BASE_SHA: ${{ github.event.pull_request.base.sha }}"
        "name: scoped-aggregate-${{ needs.prepare.outputs.candidate }}"
    ] do
    if not (text.Contains required) then
        failwith $"workflow projection missing {required}"

let offlineRouteChecks =
    if offlineEnabled then
        [
            "if: ${{ github.event_name == 'pull_request' }}"
            "needs: [prepare, classify-reuse, run-partition, offline-formal-shadow]"
            "matrix.kind == 'formal' && (matrix.shard != 'authority-reconciliation' || github.event_name != 'pull_request')"
            "always() && matrix.kind == 'formal' && (matrix.shard != 'authority-reconciliation' || github.event_name != 'pull_request')"
        ]
    else
        [
            "if: ${{ false && github.event_name == 'pull_request' }}"
            "needs: [prepare, classify-reuse, run-partition]"
            "if: ${{ matrix.kind == 'formal' }}"
            "if: ${{ always() && matrix.kind == 'formal' }}"
        ]

for required in offlineRouteChecks do
    if not (text.Contains required) then
        failwith $"offline formal route missing {required}"

if
    not (text.Contains("if: ${{ always() && needs.prepare.result != 'skipped' && needs.run-partition.result != 'skipped' && needs.classify-reuse.outputs.profile == 'full' }}"))
    || not (text.Contains("if: ${{ always() && needs.prepare.result != 'skipped' }}"))
then
    failwith "both coherent aggregates must skip unadmitted draft pull requests"

if text.Contains("- { kind: formal, shard:") || text.Contains("- { kind: partition, partition:") then
    failwith "workflow must use plan-derived profile matrix"

printfn "optimistic validation projection is current"
