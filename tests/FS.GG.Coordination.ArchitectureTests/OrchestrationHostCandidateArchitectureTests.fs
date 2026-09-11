module FS.GG.Coordination.OrchestrationHostCandidateArchitectureTests

open System
open System.Diagnostics
open System.IO
open Xunit

let private root =
    let rec find (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "FS.GG.Coordination.sln")) then directory.FullName
        elif isNull directory.Parent then failwith "repository root not found"
        else find directory.Parent
    find (DirectoryInfo(AppContext.BaseDirectory))

[<Fact>]
let ``host candidate workflow and archive controls pass mutation self test`` () =
        let start = ProcessStartInfo("python3")
        start.WorkingDirectory <- root
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        for argument in [ "eng/orchestration-host-candidate.py"; "self-test"; "--repo"; "." ] do
            start.ArgumentList.Add argument
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.True(child.ExitCode = 0, output + error)
        Assert.Contains("ORCHESTRATION_HOST_CANDIDATE_SELF_TEST_OK", output, StringComparison.Ordinal)

[<Fact>]
let ``host project is a locked Linux x64 self contained single file`` () =
        let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Orchestration.Host/FS.GG.Coordination.Orchestration.Host.fsproj"))
        for binding in [ "<IsPackable>false</IsPackable>"; "<RuntimeIdentifier>linux-x64</RuntimeIdentifier>"; "<SelfContained>true</SelfContained>"; "<PublishSingleFile>true</PublishSingleFile>" ] do
            Assert.Contains(binding, project, StringComparison.Ordinal)
        let lockText = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Orchestration.Host/packages.lock.json"))
        Assert.Contains("net10.0/linux-x64", lockText, StringComparison.Ordinal)

[<Fact>]
let ``runner client candidate workflow and archive controls pass mutation self test`` () =
        let start = ProcessStartInfo("python3")
        start.WorkingDirectory <- root
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        for argument in [ "eng/orchestration-runner-client-candidate.py"; "self-test"; "--repo"; "." ] do start.ArgumentList.Add argument
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        Assert.True(child.ExitCode=0,output+error)
        Assert.Contains("ORCHESTRATION_RUNNER_CLIENT_CANDIDATE_SELF_TEST_OK",output,StringComparison.Ordinal)

[<Fact>]
let ``runner client is separately locked and has no host or provider reference`` () =
        let project=File.ReadAllText(Path.Combine(root,"src/FS.GG.Coordination.Orchestration.Runner.Client/FS.GG.Coordination.Orchestration.Runner.Client.fsproj"))
        let source=File.ReadAllText(Path.Combine(root,"src/FS.GG.Coordination.Orchestration.Runner.Client/Program.fs"))
        for binding in ["<IsPackable>false</IsPackable>";"<RuntimeIdentifier>linux-x64</RuntimeIdentifier>";"<SelfContained>true</SelfContained>";"<PublishSingleFile>true</PublishSingleFile>"] do Assert.Contains(binding,project,StringComparison.Ordinal)
        Assert.DoesNotContain("Orchestration.Host",project,StringComparison.Ordinal)
        Assert.DoesNotContain("FS.GG.Coordination.GitHub",project,StringComparison.Ordinal)
        Assert.Contains("net10.0/linux-x64",File.ReadAllText(Path.Combine(root,"src/FS.GG.Coordination.Orchestration.Runner.Client/packages.lock.json")),StringComparison.Ordinal)
        for binding in ["--client-cert-file";"--client-key-file";"--ca-file";"orchestration.main.internal";"18080";"AllowAutoRedirect=false";"CustomRootTrust";"8192"] do Assert.Contains(binding,source,StringComparison.Ordinal)
        Assert.DoesNotContain("Authorization",source,StringComparison.Ordinal)
        Assert.DoesNotContain("--token-file",source,StringComparison.Ordinal)
        Assert.DoesNotContain("File.ReadAllBytes(values[\"--request-file\"])",source,StringComparison.Ordinal)

[<Fact>]
let ``runner candidate media type is identical across protocol host and durable store`` () =
        let expected="application/vnd.fsgg.runner-candidate+zip"
        let legacy=["application/vnd.git.bundle";"application/zip";"application/zstd"]
        for relative in ["src/FS.GG.Coordination.Core/Orchestration.fs";"src/FS.GG.Coordination.Orchestration.Host/RunnerWireRuntime.fs";"src/FS.GG.Coordination.Orchestration.PostgreSql/PostgreSqlStore.fs"] do
            let source=File.ReadAllText(Path.Combine(root,relative))
            Assert.Contains(expected,source,StringComparison.Ordinal)
            for value in legacy do Assert.DoesNotContain(value,source,StringComparison.Ordinal)
