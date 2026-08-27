module FS.GG.Coordination.ArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Xml.Linq
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

let private withRepositoryMutation mutate verify =
    let scratch = Directory.CreateTempSubdirectory("fsgg-gs2014-")

    try
        for fileName in
            [ "Directory.Build.props"
              "Directory.Build.local.props"
              "Directory.Packages.props"
              "Directory.Packages.local.props"
              "global.json" ] do
            File.Copy(Path.Combine(repositoryRoot, fileName), Path.Combine(scratch.FullName, fileName))

        for source in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*", SearchOption.AllDirectories) do
            if not (source.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
               && not (source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) then
                let relative = Path.GetRelativePath(repositoryRoot, source)
                let destination = Path.Combine(scratch.FullName, relative)
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                File.Copy(source, destination)

        mutate scratch.FullName
        verify scratch.FullName
    finally
        scratch.Delete(true)

[<Fact>]
let ``production graph satisfies dependency policy`` () =
    let exitCode, output, error = runVerifier repositoryRoot
    Assert.Equal(0, exitCode)
    Assert.Equal("DEPENDENCY_POLICY_OK projects=6", output)
    Assert.Equal("", error)

[<Fact>]
let ``published kernel lock is exact and feed-served`` () =
    let lockPath =
        Path.Combine(
            repositoryRoot,
            "src/FS.GG.Coordination.Qualification.Contracts/packages.lock.json"
        )

    use document = JsonDocument.Parse(File.ReadAllBytes lockPath)
    let package =
        document.RootElement.GetProperty("dependencies").GetProperty("net10.0").GetProperty("FS.GG.SDD.Artifacts")

    Assert.Equal("Direct", package.GetProperty("type").GetString())
    Assert.Equal("[1.5.0, 1.5.0]", package.GetProperty("requested").GetString())
    Assert.Equal("1.5.0", package.GetProperty("resolved").GetString())
    Assert.Equal(
        "RAVNLuyPScmeoH+v5fSs5Ahd5DlR+S8kO1wSbX+xIOJ6WsLsF9iDIkXbqTCuwZFOWx72fARJEw4nZrBClUUxGw==",
        package.GetProperty("contentHash").GetString()
    )

[<Fact>]
let ``source project reference to the published kernel producer is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let projectPath =
                Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/FS.GG.Coordination.Qualification.Contracts.fsproj")
            let document = XDocument.Load projectPath
            let group = XElement(XName.Get "ItemGroup")
            group.Add(XElement(XName.Get "ProjectReference", XAttribute(XName.Get "Include", "../../FS.GG.SDD/src/FS.GG.SDD.Artifacts/FS.GG.SDD.Artifacts.fsproj")))
            document.Root.Add group
            document.Save projectPath)
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=published-kernel-source-project-reference-forbidden", error))

[<Fact>]
let ``published kernel package consumption outside qualification is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let projectPath = Path.Combine(root, "src/FS.GG.Coordination.Core/FS.GG.Coordination.Core.fsproj")
            let document = XDocument.Load projectPath
            let group = XElement(XName.Get "ItemGroup")
            group.Add(XElement(XName.Get "PackageReference", XAttribute(XName.Get "Include", "FS.GG.SDD.Artifacts")))
            document.Root.Add group
            document.Save projectPath)
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=published-kernel-consumer-not-allowed", error))

[<Fact>]
let ``imported published kernel metadata override is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let targets =
                XDocument(
                    XElement(
                        XName.Get "Project",
                        XElement(
                            XName.Get "ItemGroup",
                            XElement(
                                XName.Get "PackageReference",
                                XAttribute(XName.Get "Update", "FS.GG.SDD.Artifacts"),
                                XAttribute(XName.Get "VersionOverride", "[1.5.0]"),
                                XAttribute(XName.Get "GeneratePathProperty", "false")
                            )
                        )
                    )
                )

            targets.Save(Path.Combine(root, "Directory.Build.targets")))
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("dependency=[1.5.0] rule=published-kernel-version-must-be-central", error)
            Assert.Contains("rule=published-kernel-path-property-required", error))

[<Fact>]
let ``checkout relative package source is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let propsPath = Path.Combine(root, "Directory.Build.local.props")
            let document = XDocument.Load propsPath
            let group = XElement(XName.Get "PropertyGroup")
            group.Add(XElement(XName.Get "RestoreAdditionalProjectSources", "../../FS.GG.SDD/artifacts/packages"))
            document.Root.Add group
            document.Save(propsPath))
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=checkout-relative-package-source-forbidden", error))

[<Fact>]
let ``NuGet config checkout relative package source is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let config =
                XDocument(
                    XElement(
                        XName.Get "configuration",
                        XElement(
                            XName.Get "packageSources",
                            XElement(XName.Get "clear"),
                            XElement(
                                XName.Get "add",
                                XAttribute(XName.Get "key", "checkout"),
                                XAttribute(XName.Get "value", "../FS.GG.SDD/artifacts/packages")
                            )
                        )
                    )
                )
            config.Save(Path.Combine(root, "NuGet.Config")))
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("dependency=../FS.GG.SDD/artifacts/packages", error)
            Assert.Contains("rule=checkout-relative-package-source-forbidden", error))

[<Fact>]
let ``local producer machinery copy is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let path = Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/QuintCompiler.fs")
            File.WriteAllText(path, "// forbidden producer copy"))
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=published-kernel-producer-copy-forbidden", error))

[<Fact>]
let ``non exact central published kernel pin is independently rejected`` () =
    withRepositoryMutation
        (fun root ->
            let propsPath = Path.Combine(root, "Directory.Packages.local.props")
            let document = XDocument.Load propsPath
            let pin =
                document.Descendants(XName.Get "PackageVersion")
                |> Seq.find (fun element -> element.Attribute(XName.Get "Include").Value = "FS.GG.SDD.Artifacts")
            pin.SetAttributeValue(XName.Get "Version", "1.4.0")
            document.Save propsPath)
        (fun root ->
            let exitCode, _, error = runVerifier root
            Assert.NotEqual(0, exitCode)
            Assert.Contains("rule=published-kernel-central-pin-must-equal-1.5.0", error))

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

[<Fact>]
let ``failed pure-core project evaluation is independently rejected`` () =
    let fixtureRoot = Path.Combine(repositoryRoot, "tests/fixtures/failed-project-evaluation")
    let exitCode, _, error = runVerifier fixtureRoot

    Assert.NotEqual(0, exitCode)
    Assert.Contains(
        "DEPENDENCY_POLICY_VIOLATION project=FS.GG.Coordination.Core",
        error
    )
    Assert.Contains("rule=project-evaluation-failed", error)
    Assert.DoesNotContain("rule=project-edge-not-allowed", error)
    Assert.DoesNotContain("rule=runtime-reference-not-allowed-in-pure-layer", error)

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
