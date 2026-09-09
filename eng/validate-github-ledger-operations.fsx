open System
open System.Diagnostics
open System.IO

let root =
    fsi.CommandLineArgs
    |> Array.tryItem 1
    |> Option.defaultValue (Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..")))
    |> Path.GetFullPath

let read (relative: string) = File.ReadAllText(Path.Combine(root, relative))
let require (token: string) (relative: string) =
    if not ((read relative).Contains(token, StringComparison.Ordinal)) then
        failwithf "%s omitted %s" relative token

let run (executable: string) (arguments: string list) =
    let info = ProcessStartInfo(executable)
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    for argument in arguments do info.ArgumentList.Add argument
    use child = Process.Start info
    let output, error = child.StandardOutput.ReadToEnd(), child.StandardError.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then failwithf "%s failed (%d): %s %s" executable child.ExitCode output error

for token in [ "RSASignaturePadding.Pss"; "ExpectedAbsent"; "expected-ref-lease-conflict"; "observationEnvelope" ] do
    require token "src/FS.GG.Coordination.GitHub/LedgerInitializationAdapter.fs"
for token in [ "RSASignaturePadding.Pss"; "invalid-or-duplicate-operational-evidence"; "TimeSpan.FromMinutes 15." ] do
    require token "src/FS.GG.Coordination.GitHub/LedgerOperationalEvidence.fs"
for token in [ "journal_mode=WAL"; "begin immediate"; "store-symlink"; "database-symlink"; "outbox"; "heartbeat" ] do
    require token "eng/monitor-github-ledger-protection.py"
for token in [ "initialize\" :: \"plan"; "initialize\" :: \"apply"; "initialize\" :: \"verify"; "--credential-fd" ] do
    require token "src/FS.GG.Coordination.Cli/LedgerProtectionCommand.fs"
let credentialSurface = read "src/FS.GG.Coordination.Cli/LedgerProtectionCommand.fs" + read "src/FS.GG.Coordination.GitHub/LedgerInitializationAdapter.fs"
for forbidden in [ "GITHUB_TOKEN"; "GetEnvironmentVariable"; "PrivateKey"; "BEGIN PRIVATE KEY" ] do
    if credentialSurface.Contains(forbidden, StringComparison.Ordinal) then failwithf "credential discovery or retention surface found: %s" forbidden

run "dotnet" [ "test"; "tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj"; "-c"; "Release"; "--no-restore"; "--filter"; "FullyQualifiedName~GitHubLedgerInitializationTests"; "--logger"; "console;verbosity=minimal" ]
run "python3" [ "eng/test-monitor-github-ledger-protection.py" ]
printfn "GITHUB_LEDGER_OPERATIONAL_OK initializer=real-git-objects monitor=private-wal recovery=fail-closed q=Q6"
