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
let phase = option "--phase" arguments |> Option.defaultValue "contract"

if not (Set.contains phase (set [ "contract"; "recovery" ])) then
    failwith $"unsupported phase: {phase}"

let path relative = Path.Combine(root, relative)
let requireFile relative = if not (File.Exists(path relative)) then failwith $"missing: {relative}"
let requireContains relative (values: string list) =
    requireFile relative
    let text = File.ReadAllText(path relative)
    for value in values do
        if not (text.Contains(value, StringComparison.Ordinal)) then failwith $"{relative} missing {value}"

let run executable values =
    let info = ProcessStartInfo(executable, WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    for value in values do info.ArgumentList.Add value
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    if child.ExitCode <> 0 then failwith $"command failed ({child.ExitCode}): {output.Result}\n{error.Result}"
    output.Result

requireContains
    "src/FS.GG.Coordination.GitHub/OrdinaryDelivery.fs"
    [
        "PersistIntent"
        "ObserveEffect"
        "PersistSettlement"
        "PreOpenV2Refusal"
        "CrossSubjectObservation"
        "StalePolicy"
        "ChangedSource"
    ]

requireContains
    "src/FS.GG.Coordination.Cli/Program.fs"
    [ "\"delivery\" :: rest" ]

requireContains
    "src/FS.GG.Coordination.Cli/DeliveryCommand.fs"
    [ "\"inspect\""; "\"plan\""; "\"advance\""; "--provider-response" ]

requireContains
    "eng/callable-cli-installed-harness.json"
    [
        "ce318148d288051eaeb55ebb0e81bb0172d3194523c95ea9caeed5b5091a15cf"
        "e7f440a2a1f94d51dbcdd7146494c97e6386f9dcc8034a028e3e851d364390e3"
        "587f46e15e1404dbe0dc1e9e6b47cf2861d7b502"
        "separate-protected-.4b-operation"
        "observed-403-entitlement-unknown-not-absence"
    ]

requireContains
    "eng/callable-cli-isolated-operation-proposal.json"
    [
        "v2-call-01-4b-isolated-native-v1"
        "FS-GG/FS.GG.Coordination.CallableSandbox"
        "separateProtectedAuthority"
        "required_status_checks returned 403"
        "billing-change"
        "pendingIsNotAcceptance"
    ]

requireContains
    "eng/test-callable-cli-installed-harness.py"
    [ "127.0.0.1"; "loopback-only"; "AdvancePending"; "providerMutations" ]

requireContains
    "evidence/github-substrate-v2/gs2-09-9/recovery-coverage.json"
    [
        "effect-outcomes-proven-absent-unknown-applied"
        "lost-journal-acknowledgements"
        "native-completion-reconciliation"
        "pending-or-nonzero-is-not-acceptance"
        "productionRuntimeTests"
    ]

requireContains
    "evidence/github-substrate-v2/roadmap-amendments/GS2-09.9.json"
    [
        "7d2db1c32c47b6f9c445a77f9a91510c61a17281"
        "9c49a0efd1440d8a71130758be39394ae4cdd67f3d10b9cb6cb71998154c1a17"
        "legacy Done receipt"
    ]

requireContains
    "evidence/github-substrate-v2/gs2-09-9/model-correspondence.json"
    [
        "6ae56a7c9dce52f3ac25e39145b275ed5e8127a1020ee8c65a392b976661c298"
        "4c6a18a3c8cca8ebd59ce040f63f0192c07f9468ad155e7941f676b0b611719c"
        "OrdinaryIntentPersisted"
        "OrdinaryEffectPending"
        "OrdinarySettled"
        "no production composition proof inherited from GS2-07"
    ]

let units = JsonDocument.Parse(File.ReadAllBytes(path "eng/github-substrate-v2-units.json"))
let unit =
    units.RootElement.GetProperty("units").EnumerateArray()
    |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-09.9")
if unit.GetProperty("contractSha256").GetString() <> "78899871f3716eb393786ae0b0d2266ef9f2df511d9c291dc19252014655c5e8" then
    failwith "GS2-09.9 contract digest drift"

let testOutput =
    run
        "dotnet"
        [
            "test"
            "tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj"
            "--configuration"
            "Release"
            "--no-restore"
            "--filter"
            "FullyQualifiedName~GitHubOrdinaryDeliveryTests|FullyQualifiedName~GitHubOrdinaryRuntimeTests"
            "--logger"
            "console;verbosity=minimal"
        ]

if not (testOutput.Contains("Passed!", StringComparison.Ordinal)) then failwith "focused ordinary-delivery tests did not pass"

let temporary = Path.Combine(Path.GetTempPath(), $"fsgg-ordinary-delivery-{Guid.NewGuid():N}")
Directory.CreateDirectory temporary |> ignore

try
    let cli = "src/FS.GG.Coordination.Cli/FS.GG.Coordination.Cli.fsproj"
    let observation = "evidence/github-substrate-v2/gs2-09-9/observation-open-v2.json"
    let first = run "dotnet" [ "run"; "--project"; cli; "--configuration"; "Release"; "--no-build"; "--"; "delivery"; "plan"; "--observation"; observation ]
    let second = run "dotnet" [ "run"; "--project"; cli; "--configuration"; "Release"; "--no-build"; "--"; "delivery"; "plan"; "--observation"; observation ]
    if first <> second then failwith "CLI plan bytes are nondeterministic"
    let planPath = Path.Combine(temporary, "plan.json")
    File.WriteAllText(planPath, first)
    let advanced =
        run "dotnet" [ "run"; "--project"; cli; "--configuration"; "Release"; "--no-build"; "--"; "delivery"; "advance"; "--observation"; observation; "--plan"; planPath; "--provider-response"; "evidence/github-substrate-v2/gs2-09-9/provider-absent.json" ]
    if phase = "recovery" && (not (advanced.Contains("AdvanceSettled", StringComparison.Ordinal)) || not (advanced.Contains("\"dispatches\":1", StringComparison.Ordinal))) then
        failwith $"controlled advancement did not settle exactly one dispatch: {advanced}"

    if phase = "recovery" then
        let installed = run "python3" [ "eng/test-callable-cli-installed-harness.py" ] |> fun value -> value.Trim()
        let retained = File.ReadAllText(path "evidence/github-substrate-v2/gs2-09-9/installed-harness.json").Trim()
        if installed <> retained then failwith "installed harness readback differs from retained exact evidence"
finally
    Directory.Delete(temporary, true)

printfn "callable ordinary delivery %s qualification passed; controlled provider only; no external mutation" phase
