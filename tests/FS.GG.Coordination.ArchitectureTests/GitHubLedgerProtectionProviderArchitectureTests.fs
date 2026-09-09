module FS.GG.Coordination.GitHubLedgerProtectionProviderArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read path = File.ReadAllText(Path.Combine(root,path))

[<Fact>]
let ``provider adapter is pure read model with no transport credential or apply surface`` () =
    let text = read "src/FS.GG.Coordination.GitHub/LedgerProtectionProviderAdapter.fsi" + read "src/FS.GG.Coordination.GitHub/LedgerProtectionProviderAdapter.fs"
    for required in [ "PayloadSha256"; "PreviousObservationEvidenceSha256"; "ProviderEnvelopeSha256"; "RawSetSha256"; "NormalizedSetSha256"; "IsTerminal"; "Pages"; "HttpStatus"; "ObservedAt"; "LedgerProtectionPlanAdapter.compile"; "/orgs/FS-GG/installations"; "/user/installations/"; "FS-GG/.github/environments"; "FS.GG.Coordination.Authority/issues" ] do
        Assert.Contains(required,text)
    for forbidden in [ "HttpClient"; "api.github.com"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "let apply"; "val apply"; "PATCH "; "POST "; "DELETE " ] do
        Assert.DoesNotContain(forbidden,text)

[<Fact>]
let ``provider corpus is explicitly sanitized and non operational`` () =
    use corpus = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-2/provider-observation-corpus.json")
    let value = corpus.RootElement
    Assert.Equal("sanitized-deterministic-fixture", value.GetProperty("sourceCategory").GetString())
    Assert.Equal(0, value.GetProperty("writesAttempted").GetInt32())
    Assert.False(value.GetProperty("providerReadbackClaimed").GetBoolean())
    Assert.False(value.GetProperty("applyAuthorized").GetBoolean())

[<Fact>]
let ``capture transport is fixed read only and retained evidence is sanitized`` () =
    let script = read "eng/capture-github-ledger-protection.py"
    for required in ["AUTHORITY = \"FS-GG/FS.GG.Coordination.Authority\"";"GRAPHQL =";"subprocess.run([\"gh\", \"api\", *args]";"GET /installation/repositories?per_page=100";"pass_fds=(key_fd,)";"stderr=subprocess.DEVNULL";"rawSha256";"normalizedSha256";"continuity = \"uninitialized\"";"continuity = \"matched\""] do Assert.Contains(required,script)
    for forbidden in ["shell=True";"--method";"GITHUB_TOKEN";"cookie";"client_secret";"print(token";"print(jwt";"write_bytes(token";"write_bytes(jwt"] do Assert.DoesNotContain(forbidden,script,StringComparison.OrdinalIgnoreCase)
    for name,pass,continuity in [("live-capture-pass1.json",1,"uninitialized");("live-capture-pass2.json",2,"matched")] do
        use document = JsonDocument.Parse(read ("evidence/github-substrate-v2/gs2-08-2/"+name))
        let value = document.RootElement
        Assert.Equal(pass,value.GetProperty("capturePass").GetInt32())
        Assert.Equal(continuity,value.GetProperty("continuity").GetString())
        Assert.Equal(0,value.GetProperty("writesAttempted").GetInt32())
        Assert.False(value.GetProperty("applyAuthorized").GetBoolean())
        Assert.Empty(value.GetProperty("gaps").EnumerateArray())
        Assert.NotEqual(value.GetProperty("rawSetSha256").GetString(), value.GetProperty("normalizedSetSha256").GetString())
        Assert.Equal(11, value.GetProperty("resources").GetArrayLength())

[<Fact>]
let ``App identities are distinct and capture tests prove the private transport boundary`` () =
    use desired = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-2/desired-policy.json")
    let desiredRoot = desired.RootElement
    Assert.Equal(4882140L,desiredRoot.GetProperty("ordinaryWriter").GetProperty("appId").GetInt64())
    Assert.Equal(160261608L,desiredRoot.GetProperty("ordinaryWriter").GetProperty("installationId").GetInt64())
    Assert.Equal(4882399L,desiredRoot.GetProperty("cutoverWriter").GetProperty("appId").GetInt64())
    Assert.Equal(160261436L,desiredRoot.GetProperty("cutoverWriter").GetProperty("installationId").GetInt64())
    let info = ProcessStartInfo("python3", "eng/test-capture-github-ledger-protection.py")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode=0, error)
    Assert.Contains("CAPTURE_APP_AUTH_TESTS_OK",output)

[<Fact>]
let ``independent provider validator passes from a fresh process`` () =
    let info = ProcessStartInfo("dotnet", "fsi eng/validate-github-ledger-protection-provider.fsx -- .")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.True(child.ExitCode=0, error)
    Assert.Contains("GITHUB_LEDGER_PROTECTION_PROVIDER_OK", output)

[<Fact>]
let ``correspondence fixture binds real identities and no operational claim`` () =
    use fixture = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-2/provider-correspondence-fixtures.json")
    let value = fixture.RootElement
    Assert.Equal("FS-GG/FS.GG.Coordination.Authority", value.GetProperty("authorityRepository").GetString())
    Assert.Equal(1351660651L, value.GetProperty("authorityRepositoryId").GetInt64())
    Assert.Equal("refs/tags/fsgg/v2/fleet-cutover/**/*", value.GetProperty("phaseTagPattern").GetString())
    Assert.False(value.GetProperty("providerReadbackClaimed").GetBoolean())
    Assert.False(value.GetProperty("applyAuthorized").GetBoolean())

[<Fact>]
let ``provider qualification is registered as the GS2-08-2 Q4 continuation`` () =
    use units = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    use gates = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let unitValue = units.RootElement.GetProperty("units").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString()="GS2-08.2")
    Assert.Equal<string list>(["Q3";"Q4"], unitValue.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    Assert.Equal<string list>(["github-ledger-protection-contract";"github-ledger-protection-provider-contract"], unitValue.GetProperty("gateCommands").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
    let command = gates.RootElement.GetProperty("commands").EnumerateArray() |> Seq.find (fun value -> value.GetProperty("id").GetString()="github-ledger-protection-provider-contract")
    Assert.Equal("Q4", command.GetProperty("qGate").GetString())
    Assert.Equal<string list>(["fsi";"eng/validate-github-ledger-protection-provider.fsx";"--";"."], command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)

[<Fact>]
let ``post-install conformance remains sealed symbolic preparation`` () =
    let source = read "src/FS.GG.Coordination.GitHub/LedgerProtectionConformance.fs"
    for state in ["CurrentPreInstall";"InstalledFleetProtection";"InstalledProductionProtection";"IncompleteOrUnknown";"DriftOrTamper"] do Assert.Contains(state,source)
    for required in ["ApplyAuthorized=false";"provider administration is not authorized";"credential custody is not established";"fleet initialization is pending";"monitoring is pending"] do Assert.Contains(required,source)
    use evidence = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-08-2/post-install-conformance.json")
    Assert.Equal("CurrentPreInstall",evidence.RootElement.GetProperty("currentState").GetString())
    Assert.False(evidence.RootElement.GetProperty("administrativePreparation").GetProperty("applyAuthorized").GetBoolean())
    Assert.Equal(0,evidence.RootElement.GetProperty("administrativePreparation").GetProperty("providerWritesAttempted").GetInt32())
