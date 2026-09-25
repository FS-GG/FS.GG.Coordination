open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec option name = function
    | key :: value :: _ when key = name -> Some value
    | _ :: tail -> option name tail
    | [] -> None

let root = option "--root" arguments |> Option.defaultValue "." |> Path.GetFullPath
let phase = option "--phase" arguments |> Option.defaultValue "contract"
if phase <> "contract" && phase <> "recovery" then failwith "unsupported qualification phase"

let absolute relative = Path.Combine(root, relative)
let digest (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let bytes relative =
    let file = absolute relative
    if not (File.Exists file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) then
        failwith $"qualification input is missing or linked: {relative}"
    File.ReadAllBytes file
let sha relative = bytes relative |> digest
let document relative = JsonDocument.Parse(bytes relative)
let property (name: string) (value: JsonElement) = value.GetProperty name
let string (name: string) (value: JsonElement) = property name value |> _.GetString()
let boolean (name: string) (value: JsonElement) = property name value |> _.GetBoolean()
let require condition reason = if not condition then failwith reason

let contractPath = "eng/callable-cli-isolated-operation-v2-contract.json"
let proposalPath = "eng/callable-cli-isolated-operation-v2-proposal.json"
let sourcePath = "eng/callable-cli-isolated-operation-v2.py"
let testsPath = "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
let preflightPath = "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"
let identity = "v2-call-01-4b-isolated-native-v2-provisional"

// The protected native operation is historical. Its source and evidence are never rotated in place.
for relative, expected in [
    "eng/callable-cli-isolated-operation.py", "e35f525b3c17c5c870d08d5d8d2fc3caa661fe98374c38463c6e2018575d6046"
    "eng/callable-cli-isolated-operation-contract.json", "072d724a366f288c4a50d67f237ba3e3bc42a593e0a9ccf557be5917365974d2"
    "eng/callable-cli-isolated-operation-proposal.json", "61d58b91453fce62d6d4d05de03078b4152b9861f7a8a90ab5738a844632b016"
    "eng/validate-github-callable-isolated-operation.fsx", "4ac72caaf5fd02d1f66bbb06c20fb977ab01bcd6b6fc5d2428539ec359c3657a"
    "evidence/github-substrate-v2/gs2-09-9/native-acceptance.json", "3cb0dbf912c406f6a3a20607f06c307da69e7e50e0551936f31ef0dd7832b67e"
] do
    require (sha relative = expected) $"historical protected byte drift: {relative}"

let contract = document contractPath
let c = contract.RootElement
require (string "schema" c = "fsgg.coordination.callable-isolated-operation-contract/5") "v5 contract schema"
require (string "identity" c = identity) "v5 contract identity"
require (string "state" c = "prepared-not-authorized" && not (boolean "authorized" c)) "v5 contract authorization"
require (string "scope" c = "offline-inspect-and-controlled-transcript-only; no live provider authority or effect command") "v5 contract scope"
let source = property "source" c
require (string "operationSource" source = sourcePath) "v5 source path"
require (string "operationSourceSha256" source = sha sourcePath) "v5 source bytes"
let controls = property "qualificationControls" c
require (string "path" controls = testsPath) "v5 control path"
require (string "sha256" controls = sha testsPath) "v5 control bytes"
let historical = property "historicalPreflight" c
require (string "path" historical = preflightPath) "historical preflight path"
require (string "sha256" historical = sha preflightPath) "historical preflight bytes"
require (string "operationIdentity" historical = "v2-call-01-4b-isolated-native-v1") "historical preflight identity"
require (string "authority" historical = "historical-observation-only") "historical preflight authority"
require (sha preflightPath = "ca9ee64633866425ced8d4578964df70eb3dc4cac2fd420f2d2963d5222e7de8") "historical preflight changed"

let proposal = document proposalPath
let p = proposal.RootElement
require (string "schema" p = "fsgg.coordination.callable-isolated-operation-proposal/5") "v5 proposal schema"
require (string "identity" p = identity) "v5 proposal identity"
require (string "state" p = "prepared-not-authorized" && not (boolean "authorized" p)) "v5 proposal authorization"
require (string "requiredBeforeAnyEffect" p = "new typed authority contract, sealed candidate, protected grant, and fresh capability readback") "v5 future effect boundary"
let proposalContract = property "contract" p
require (string "path" proposalContract = contractPath) "v5 proposal contract path"
require (string "sha256" proposalContract = string "contractSha256" c) "v5 proposal contract digest"
require (string "operationSourceSha256" proposalContract = sha sourcePath) "v5 proposal source digest"
let proposalHistorical = property "historicalPreflight" p
for name in [ "path"; "sha256"; "operationIdentity"; "authority" ] do
    require (string name proposalHistorical = string name historical) $"v5 proposal historical preflight {name}"

let run executable values =
    let info = ProcessStartInfo(executable, WorkingDirectory = root, UseShellExecute = false,
                                RedirectStandardOutput = true, RedirectStandardError = true)
    info.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"
    for value in values do info.ArgumentList.Add value
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    require (child.ExitCode = 0) $"offline qualification command failed with exit {child.ExitCode}"
    output.Result, error.Result

let inspectionText, _ =
    run "python3" [ sourcePath; "--contract"; contractPath; "--proposal"; proposalPath;
                    "--preflight"; preflightPath; "inspect" ]
let inspection = JsonDocument.Parse inspectionText
let observed = inspection.RootElement
require (string "operationIdentity" observed = identity) "v5 inspection identity"
require (string "schema" observed = "fsgg.coordination.callable-isolated-operation-inspection/2") "v5 inspection schema"
require (string "contractSha256" observed = string "contractSha256" c) "v5 inspection contract digest"
require (string "state" observed = "prepared-not-authorized") "v5 inspection state"
require (not (boolean "authorized" observed)) "v5 inspection authorized"
require (boolean "historicalObservationOnly" observed) "v5 historical observation authority"
require (property "liveEffects" observed |> _.GetInt32() = 0) "v5 inspection effects"
require (string "disposition" observed = "refused-no-compatible-admitted-target") "v5 historical disposition"

let testOutput, testError = run "python3" [ testsPath; "-v" ]
let testReport = testOutput + testError
require (testReport.Contains("Ran 12 tests", StringComparison.Ordinal)
         && testReport.Contains("OK", StringComparison.Ordinal)) "v5 negative controls did not pass"

let phaseCases =
    if phase = "contract" then
        [ "test_q3_exact_native_pr_and_protection_runtime"
          "test_q3_runtime_refuses_force_push_after_put"
          "test_q3_incomplete_page_and_false_terminal_refuse_before_write" ]
    else
        [ "test_q6_lost_response_unknown_and_no_repeat"
          "test_q6_controlled_exception_sentinel_not_surfaced"
          "test_q6_restart_replay_cli_and_v5_inspect_binding" ]
for case in phaseCases do
    require (testReport.Contains(case, StringComparison.Ordinal)) $"v5 {phase} control missing: {case}"

printfn "versioned callable isolated operation %s qualification passed; historical observation only; zero effects" phase
