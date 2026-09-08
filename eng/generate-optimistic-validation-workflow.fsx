open System
open System.IO
open System.Text.Json

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let planPath = Path.Combine(root, "eng/optimistic-qualification-plan.json")
let document = JsonDocument.Parse(File.ReadAllBytes planPath)
let plan = document.RootElement
if plan.GetProperty("schema").GetString() <> "fsgg.coordination.optimistic-qualification-plan/1" then failwith "unsupported plan"
if plan.GetProperty("selection").GetProperty("nightlyRecoveryMaxConcurrentCandidates").GetInt32() <> 2 then failwith "nightly recovery candidate bound must be two"
if plan.GetProperty("coherent").GetProperty("maxPartitionsPerCandidate").GetInt32() <> 6 then failwith "partition bound must be six"
if plan.GetProperty("coherent").GetProperty("cancelInProgress").GetBoolean() then failwith "coherent runs cannot be cancelled"
let workflow = Path.Combine(root, ".github/workflows/optimistic-parallel-validation.yml")
let template = Path.Combine(root, "eng/optimistic-parallel-validation.yml.template")
let expected = File.ReadAllText template
let checking = fsi.CommandLineArgs |> Array.contains "--check"
if checking && (not (File.Exists workflow) || File.ReadAllText workflow <> expected) then failwith "optimistic validation workflow projection is stale"
if not checking then File.WriteAllText(workflow, expected, Text.UTF8Encoding(false))
let text = File.ReadAllText workflow
for required in [ "group: optimistic-coherent-${{ inputs.candidate_sha || github.event.pull_request.head.sha || github.event.merge_group.head_sha || github.sha }}"; "cancel-in-progress: false"; "fail-fast: false"; "max-parallel: 6"; "cron: '17 3 * * *'"; "prepare:"; "needs: [prepare, classify-reuse]"; "run-partition:"; "aggregate:" ] do
    if not (text.Contains required) then failwith $"workflow projection missing {required}"
printfn "optimistic validation projection is current"
