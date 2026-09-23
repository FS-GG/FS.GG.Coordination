open System
open System.Diagnostics
open System.IO

let root =
    fsi.CommandLineArgs
    |> Array.tryItem 1
    |> Option.defaultValue (Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..")))
    |> Path.GetFullPath

let read (relative: string) =
    File.ReadAllText(Path.Combine(root, relative))

let require (token: string) (relative: string) =
    if not ((read relative).Contains(token, StringComparison.Ordinal)) then
        failwithf "%s omitted %s" relative token

let run (executable: string) (arguments: string list) =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    for argument in arguments do
        info.ArgumentList.Add argument

    use child = Process.Start info

    let output, error =
        child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()

    child.WaitForExit()

    if child.ExitCode <> 0 then
        failwithf "%s failed (%d): %s %s" executable child.ExitCode output error

for token in
    [
        "RSASignaturePadding.Pss"
        "ExpectedAbsent"
        "expected-ref-lease-conflict"
        "observationEnvelope"
    ] do
    require token "src/FS.GG.Coordination.GitHub/LedgerInitializationAdapter.fs"

for token in
    [
        "RSASignaturePadding.Pss"
        "invalid-or-duplicate-operational-evidence"
        "TimeSpan.FromMinutes 15."
    ] do
    require token "src/FS.GG.Coordination.GitHub/LedgerOperationalEvidence.fs"

for token in
    [
        "journal_mode=WAL"
        "begin immediate"
        "store-symlink"
        "database-symlink"
        "outbox"
        "heartbeat"
    ] do
    require token "eng/monitor-github-ledger-protection.py"

for token in
    [
        "initialize\" :: \"payload"
        "initialize\" :: \"plan"
        "initialize\" :: \"apply"
        "initialize\" :: \"verify"
        "--credential-fd"
    ] do
    require token "src/FS.GG.Coordination.Cli/LedgerProtectionCommand.fs"

for token in
    [
        "expected-absence-conflict"
        "commit-object-mismatch"
        "installation-token-refused"
        "verify-does-not-accept-credential"
    ] do
    require token "eng/github-ledger-initialization-transport.py"

for token in
    [
        "fsgg.github-ledger-initial-manifest/1"
        "fsgg.github-ledger-initial-trust/1"
        "protected-reviewers"
        "fsgg.coordination.unit-acceptance-candidate/1"
    ] do
    require token "eng/github-ledger-operation.py"

for token in
    [
        "fsgg.github-ledger-external-runner-config/1"
        "heartbeat-stale"
        "alertTarget"
        "BEGIN IMMEDIATE"
    ] do
    require token "eng/github-ledger-monitor-runner.py"

for token in
    [
        "native-artifact-digest"
        "native-approval-binding"
        "native-environment-branch-policy"
    ] do
    require token "eng/github-v1-admission-protected-read.py"

for token in
    [
        "authority-ref-census-moved"
        "authority-claim-census-not-empty"
        "authority-object-hash"
    ] do
    require token "eng/github-v1-admission-git-read.py"

for token in
    [
        "admission-app-identity"
        "admission-token-repository-scope"
        "admission-ref-scope"
        "admission-object-readback-mismatch"
        "admission-rules-moved"
        "admission-effective-rules"
        "bypass_actors"
    ] do
    require token "eng/github-v1-admission-provider-transport.py"

for token in [ "source-main-moved"; "source-compare-binding"; "source-repository-identity" ] do
    require token "eng/github-v1-admission-source-read.py"

for token in [ "admission-native-approval-expired"; "admission-authorizer-key-mismatch"; "memfd_create" ] do
    require token "eng/github-v1-admission-main-custody.py"

for token in [ "admission-bridge-object-not-planned"; "admission-bridge-ref-not-planned"; "admission-bridge-unbound" ] do
    require token "eng/github-v1-admission-provider-bridge.py"

for token in [ "V1AdmissionGenesisInstaller.apply"; "decodeEnvelope"; "createRaw"; "genesis-runner-trust-digest" ] do
    require token "eng/run-v1-admission-genesis.fsx"

for token in [ "admission-journal-not-installed"; "admission-journal-refs-moved"; "admission-journal-history-bound" ] do
    require token "eng/github-v1-admission-journal-read.py"

for token in [ "V1AdmissionRegistry.restore"; "admission-journal-evidence-moved-or-stale" ] do
    require token "src/FS.GG.Coordination.GitHub/V1AdmissionJournalGitRead.fs"

for token in [ "admission-cas-parent-moved"; "--force-with-lease="; "response-unknown" ] do
    require token "eng/github-v1-admission-journal-cas.py"

let credentialSurface =
    read "src/FS.GG.Coordination.Cli/LedgerProtectionCommand.fs"
    + read "src/FS.GG.Coordination.GitHub/LedgerInitializationAdapter.fs"

for forbidden in [ "GITHUB_TOKEN"; "GetEnvironmentVariable"; "PrivateKey"; "BEGIN PRIVATE KEY" ] do
    if credentialSurface.Contains(forbidden, StringComparison.Ordinal) then
        failwithf "credential discovery or retention surface found: %s" forbidden

run
    "dotnet"
    [
        "test"
        "tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj"
        "-c"
        "Release"
        "--no-restore"
        "--filter"
        "FullyQualifiedName~GitHubLedgerInitializationTests"
        "--logger"
        "console;verbosity=minimal"
    ]

run "python3" [ "eng/test-monitor-github-ledger-protection.py" ]
run "python3" [ "eng/test-github-ledger-live-operation.py" ]
run "python3" [ "eng/test-github-v1-admission-protected-read.py" ]
run "python3" [ "eng/test-github-v1-admission-git-read.py" ]
run "python3" [ "eng/test-github-v1-admission-provider-transport.py" ]
run "python3" [ "eng/test-github-v1-admission-source-read.py" ]
run "python3" [ "eng/test-github-v1-admission-main-custody.py" ]
run "python3" [ "eng/test-github-v1-admission-provider-bridge.py" ]
run "python3" [ "eng/test-github-v1-admission-journal-read.py" ]
run "python3" [ "eng/test-github-v1-admission-journal-cas.py" ]

printfn
    "GITHUB_LEDGER_OPERATIONAL_OK initializer=real-git-objects transport=github-rest protected-authorization=environment monitor=private-wal external-runner=explicit recovery=fail-closed q=Q6"
