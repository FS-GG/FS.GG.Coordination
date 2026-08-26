module FS.GG.Coordination.ArchitectureTests

open System
open System.Diagnostics
open System.IO
open Xunit

let private repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private verifier = Path.Combine(repositoryRoot, "eng/verify-dependencies.fsx")

let private runVerifier root =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add("fsi")
    startInfo.ArgumentList.Add(verifier)
    startInfo.ArgumentList.Add("--")
    startInfo.ArgumentList.Add("--root")
    startInfo.ArgumentList.Add(root)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false

    use childProcess = Process.Start startInfo
    let output = childProcess.StandardOutput.ReadToEnd()
    let error = childProcess.StandardError.ReadToEnd()
    childProcess.WaitForExit()
    childProcess.ExitCode, output.Trim(), error.Trim()

[<Fact>]
let ``production graph satisfies dependency policy`` () =
    let exitCode, output, error = runVerifier repositoryRoot
    Assert.Equal(0, exitCode)
    Assert.Equal("DEPENDENCY_POLICY_OK projects=6", output)
    Assert.Equal("", error)

[<Fact>]
let ``forbidden pure-core edge is independently rejected`` () =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures/forbidden-dependency")
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.Core dependency=FS.GG.Coordination.GitHub rule=project-edge-not-allowed",
        error
    )

[<Fact>]
let ``forbidden pure-core framework reference is independently rejected`` () =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures/forbidden-framework-reference")
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.Core dependency=Microsoft.AspNetCore.App rule=runtime-reference-not-allowed-in-pure-layer",
        error
    )

[<Fact>]
let ``unapproved pure-core HTTP client package is independently rejected`` () =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures/forbidden-http-client-package")
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.Core dependency=RestSharp rule=runtime-reference-not-allowed-in-pure-layer",
        error
    )

[<Theory>]
[<InlineData("forbidden-root-web-sdk")>]
[<InlineData("forbidden-child-web-sdk")>]
let ``forbidden pure-core web SDK forms are independently rejected`` fixture =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures", fixture)
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.Core dependency=Microsoft.NET.Sdk.Web rule=transport-sdk-in-pure-layer",
        error
    )

[<Fact>]
let ``forbidden App hosting and imported runtime binding forms are independently rejected`` () =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures/forbidden-app-hosting")
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.App dependency=Microsoft.NET.Sdk.Web rule=app-host-runtime-sdk-forbidden",
        error
    )
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.App dependency=OutputType=Exe rule=app-host-must-not-be-executable",
        error
    )
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.App dependency=Microsoft.Extensions.Hosting rule=app-host-runtime-binding-forbidden",
        error
    )
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.App dependency=Microsoft.AspNetCore.App rule=app-host-runtime-binding-forbidden",
        error
    )

[<Fact>]
let ``forbidden App import SDK is independently rejected`` () =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures/forbidden-app-import-web-sdk")
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.App dependency=Microsoft.NET.Sdk.Web rule=app-host-runtime-sdk-forbidden",
        error
    )
