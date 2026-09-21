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
        "3072f67fa7ad19cc93240eff7b1b3003c12d07882ea9aa85710167273852bf7d"
        "2a61d2f2cad204bb77c5a490d840bbc3e5af2479"
        "setup"
        "installed-execution"
        "independent-readback"
        "cleanup"
    ]

requireContains
    "eng/callable-cli-isolated-operation-proposal.json"
    [
        "prepared-not-authorized"
        "971ed9a9db9ff01b0abb982163ed311fabebc827fabc79472602f3c08b7d3854"
        "c82cf8e0e1f0d794a1d13a4b9ed6612c76cc86c0c230d81fac5248ec2bbbe5c5"
        "9c7e4765c6654589f579859ebf8915bfc4212c2fbecb8dc114b5b774a470a369"
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
        "ccd57e74293b2fb1443614fea6add54525f9f11c8d1126908180479ab5bc18a6"
        "2dca8907815c720086c761eff9283aaf682d5786b1c97668351679d452f5591d"
        "unproved-reviewer-membership-403"
        "unknown-create-without-readback"
        "nonzero-or-pending-installed-cli"
        "cleanup-response-without-404-readback"
        "protected-grant-artifact-canonical-payload-readback"
        "cross-origin-artifact-redirect-strips-credentials-and-refuses-downgrade"
        "execution-credential-protected-branch-read-and-installed-plan-with-execution-credential-advance"
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
if not (combined.Contains("Ran 26 tests", StringComparison.Ordinal) && combined.Contains("OK", StringComparison.Ordinal)) then
    failwith $"isolated operation controls did not report the expected bounded suite: {combined}"

printfn "callable isolated operation source qualification passed; prepared-not-authorized; zero provider effects"
