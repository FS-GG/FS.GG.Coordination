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
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.Core dependency=Microsoft.AspNetCore.App rule=transport-reference-in-pure-layer",
        error
    )
