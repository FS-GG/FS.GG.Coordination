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
    "eng/callable-cli-isolated-operation-proposal.json"
    [
        "prepared-not-authorized"
        "b0c311aca5481ee21652d428f40c33330d3a03a07636f763b306775387b7d069"
        "b1ccf5bb1a68d577ccab833f3752484c9fb74239d2bfd2e5d592791c85a3c070"
        "5b1b3fbd38cf7ae425b55e84883edb7ff88173a50053959c2bb63f25de927de4"
        "canonical-json-without-artifact-coordinates"
        "--grant-artifact-envelope"
        "identity-bound-operation"
        "new-separate-protected-short-lived-grant-bound-to-exact-plan"
        "external-acceptance-or-Q4-inference"
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
        "b0c311aca5481ee21652d428f40c33330d3a03a07636f763b306775387b7d069"
        "b1ccf5bb1a68d577ccab833f3752484c9fb74239d2bfd2e5d592791c85a3c070"
        "unproved-reviewer-membership-403"
        "unknown-create-without-readback"
        "nonzero-or-pending-installed-cli"
        "cleanup-response-without-404-readback"
        "protected-grant-artifact-canonical-payload-readback"
        "cross-origin-artifact-redirect-strips-credentials-and-refuses-downgrade"
        "setup-credential-read-only-installed-plan-with-execution-credential-advance"
        "exact-legacy-setup-progress-contract-migration"
        "aliased-credential-role-token"
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
        "creation-plan-not-canonical-contract"
        "grant-artifact-readback"
        "grant-artifact-content-binding"
        "grant-artifact-self-reference"
        "grant-artifact-envelope-binding"
        "CredentialStrippingRedirectHandler"
        "credential-token-role-alias"
        "retained-plan-missing"
        "journal-protection-conflict"
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
if not (combined.Contains("Ran 25 tests", StringComparison.Ordinal) && combined.Contains("OK", StringComparison.Ordinal)) then
    failwith $"isolated operation controls did not report the expected bounded suite: {combined}"

printfn "callable isolated operation source qualification passed; prepared-not-authorized; zero provider effects"
