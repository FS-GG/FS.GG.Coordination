open System
open System.Diagnostics
open System.IO
open System.Text.Json

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let rec option name =
    function
    | key :: value :: _ when key = name -> Some value
    | _ :: tail -> option name tail
    | [] -> None

let root = option "--root" arguments |> Option.defaultValue "." |> Path.GetFullPath
let path relative = Path.Combine(root, relative)

let requireContains relative (values: string list) =
    let text = File.ReadAllText(path relative)
    for value in values do
        if not (text.Contains(value, StringComparison.Ordinal)) then
            failwith $"{relative} missing {value}"

let run executable values =
    let info = ProcessStartInfo(executable, WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    for value in values do info.ArgumentList.Add value
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    if child.ExitCode <> 0 then failwith $"command failed ({child.ExitCode}): {output.Result}\n{error.Result}"
    output.Result, error.Result

requireContains
    "eng/callable-cli-isolated-operation-contract.json"
    [
        "prepared-not-authorized"
        "effect-before-protected-grant-and-fresh-capability-readback"
        "FS.GG.Coordination.Cli"
        "ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf"
        "587f46e15e1404dbe0dc1e9e6b47cf2861d7b502"
        "setup"
        "installed-execution"
        "independent-readback"
        "cleanup"
    ]

requireContains
    "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"
    [
        "refused-no-compatible-admitted-target"
        "\"accessibleRepositoryCount\": 16"
        "\"compatibleTargetCount\": 0"
        "\"proposedTargetHttpStatus\": 404"
        "\"privateSandboxRequiredStatusChecksHttpStatus\": 403"
        "\"organizationInstallationHttpStatus\": 403"
        "\"actorMembershipHttpStatus\": 403"
        "unobserved-target-only-not-ownership-reservation-or-authority"
    ]

requireContains
    "evidence/github-substrate-v2/gs2-09-9/isolated-operation-coverage.json"
    [
        "7936a97b5e1ba55d4d6a91880a0716cca61cf9317ab9c583b82758d1668bd1ef"
        "525a1bed96c83bd9fe601a7e210ec0bd0767610cf831dc42c7ed20d73a24a53f"
        "unproved-reviewer-membership-403"
        "unknown-create-without-readback"
        "nonzero-or-pending-installed-cli"
        "cleanup-response-without-404-readback"
        "\"externalAcceptance\": false"
    ]

requireContains
    "eng/callable-cli-isolated-operation.py"
    [
        "contract-must-remain-unauthorized"
        "plan-cannot-self-authorize"
        "reviewer-membership-unproved"
        "creation-credential-scope"
        "operation-credential-scope"
        "github-outcome-unknown-requires-readback"
        "creation-pending-requires-readback"
        "installed-advance-not-settled"
        "installed-replay-not-noop"
        "cleanup-pending-requires-readback"
    ]

let inspectOutput, _ =
    run "python3" [ "eng/callable-cli-isolated-operation.py"; "inspect" ]

let inspected = JsonDocument.Parse inspectOutput
let inspection = inspected.RootElement
if inspection.GetProperty("authorized").GetBoolean() then failwith "prepared operation became authorized"
if inspection.GetProperty("liveEffects").GetInt32() <> 0 then failwith "offline inspection reported an effect"
if inspection.GetProperty("disposition").GetString() <> "refused-no-compatible-admitted-target" then
    failwith "preflight refusal changed"

let testOutput, testError = run "python3" [ "eng/test-callable-cli-isolated-operation.py" ]
let combined = testOutput + testError
if not (combined.Contains("Ran 8 tests", StringComparison.Ordinal) && combined.Contains("OK", StringComparison.Ordinal)) then
    failwith $"isolated operation controls did not report the expected bounded suite: {combined}"

printfn "callable isolated operation source qualification passed; prepared-not-authorized; zero provider effects"
