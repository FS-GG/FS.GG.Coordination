#load "../../../src/FS.GG.Coordination.Qualification.Contracts/CanonicalProtocolSourceIdentity.fs"
#load "../../../src/FS.GG.Coordination.Qualification.Contracts/QualificationReuse.fs"
#load "../../../src/FS.GG.Coordination.Qualification.Contracts/MilestoneQualification.fs"
#load "../../../src/FS.GG.Coordination.Qualification.Contracts/QualificationCadence.fs"
#load "../../../src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs"

open System
open System.IO
open System.Reflection
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

// Inspect the real private production guard without adding a production test API.
// This performs only receipt parsing, hashing and refusal inspection.
let inspector =
    typeof<BootstrapCi.BootstrapContract>
        .DeclaringType.GetMethod(
            "inspectCanonicalQuintReceipt",
            BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic
        )

if isNull inspector then
    failwith "production receipt inspector is absent"

let inspect path =
    inspector.Invoke(null, [| box path |]) :?> string list

let fixtures = Path.Combine(__SOURCE_DIRECTORY__, "fixtures")
let original = Path.Combine(fixtures, "native-bootstrap-receipt-37153833192.json")

if not (List.isEmpty (inspect original)) then
    failwith (String.concat "\n" (inspect original))

let history =
    inspect (Path.Combine(fixtures, "historical-bootstrap-receipt-20261003.json"))

for rule in [ "quint-receipt-inventory"; "quint-receipt-input-digest" ] do
    if not (history |> List.exists (fun error -> error.Contains("rule=" + rule + " "))) then
        failwith ("historical receipt did not refuse " + rule)

let scratch =
    Path.Combine(Path.GetTempPath(), "fsgg-bootstrap-receipt-control-" + Guid.NewGuid().ToString("N"))

Directory.CreateDirectory scratch |> ignore

let setNumber (node: JsonNode) (name: string) value =
    node[name] <- JsonValue.Create(value: int)

let setString (node: JsonNode) (name: string) value =
    node[name] <- JsonValue.Create(value: string)

let controls: (string * string * (JsonNode -> unit)) list =
    [
        "negative-count", "quint-receipt-inventory", fun node -> setNumber node "negativeControlCount" 173
        "logical-count", "quint-receipt-process-count", fun node -> setNumber node["processCounts"] "external" 262
        "parallel-as-retained",
        "quint-receipt-physical-process-count",
        fun node -> setNumber node["physicalProcessCounts"] "external" 323
        "retry", "quint-receipt-startup-retries", fun node -> setNumber node["startupRetries"] "total" 2
        "source",
        "quint-receipt-input-digest",
        fun node -> setString node["inputs"] "sourceSha256" (String.replicate 64 "0")
        "contract",
        "quint-receipt-input-digest",
        fun node -> setString node["inputs"] "contractSha256" (String.replicate 64 "0")
        "result", "quint-receipt-result-digest", fun node -> setString node "resultSha256" (String.replicate 64 "0")
        "tool", "quint-receipt-tool-digest", fun node -> setString node["tools"] "quintSha256" (String.replicate 64 "0")
        "roster",
        "quint-receipt-formal-counterexamples",
        fun node -> node["formalCounterexamples"].AsArray().RemoveAt(0)
        "timing", "quint-receipt-timing", fun node -> setNumber node "totalDurationMs" 1
        "schema", "quint-receipt-schema", fun node -> setString node "schema" "drift"
        "properties", "quint-receipt-properties", fun node -> setString node "unexpected" "drift"
        "outcome", "quint-receipt-outcome", fun node -> setString node "q1Outcome" "failed"
    ]

try
    for name, rule, mutate in controls do
        let node = JsonNode.Parse(File.ReadAllText original)
        let before = node.ToJsonString()
        mutate node
        let changed = node.ToJsonString()

        if before = changed then
            failwith (name + " did not change bytes")

        let path = Path.Combine(scratch, name + ".json")
        File.WriteAllText(path, changed)
        let errors = inspect path

        if not (errors |> List.exists (fun error -> error.Contains("rule=" + rule + " "))) then
            failwith (name + " did not refuse " + rule + "\n" + String.concat "\n" errors)

    let malformed = Path.Combine(scratch, "malformed.json")
    File.WriteAllText(malformed, "not-json")

    if
        not (
            inspect malformed
            |> List.exists (fun error -> error.Contains "rule=quint-receipt-unreadable ")
        )
    then
        failwith "malformed receipt did not refuse"
finally
    Directory.Delete(scratch, true)

printfn "BOOTSTRAP_RECEIPT_CONTROLS_OK authentic-current=1 historical-refusal=1 drift-refusals=14"
