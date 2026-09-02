module FS.GG.Coordination.GitHubPermissionCompilationArchitectureTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Xml.Linq
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private validator = Path.Combine(root, "eng/validate-github-permission-compilation.fsx")
let private runValidator candidateRoot =
    let info = ProcessStartInfo("dotnet")
    for argument in [ "fsi"; validator; "--"; candidateRoot ] do info.ArgumentList.Add argument
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output

let private sha256 path =
    File.ReadAllBytes path
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private tracked paths =
    let info = ProcessStartInfo("git")
    info.WorkingDirectory <- root
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    for argument in [ "ls-files"; "--error-unmatch"; "--" ] @ paths do
        info.ArgumentList.Add argument
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output

[<Fact>]
let ``production Q3 permission compiler succeeds and is wired into bootstrap`` () =
    let code, output = runValidator root
    Assert.Equal(0, code)
    Assert.Contains("GITHUB_PERMISSION_COMPILATION_OK", output)
    let catalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("validate-github-permission-compilation.fsx", catalog)

[<Fact>]
let ``production Q3 rejects an independently isolated release crossover`` () =
    let temporary = Path.Combine(Path.GetTempPath(), "fsgg-permission-compilation-" + Guid.NewGuid().ToString("n"))
    try
        Directory.CreateDirectory(temporary) |> ignore
        for path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(root, path)
            let segments = relative.Split(Path.DirectorySeparatorChar)
            if not (segments |> Array.exists (fun value -> value = ".git" || value = "bin" || value = "obj" || value = "artifacts")) then
                let target = Path.Combine(temporary, relative)
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                File.Copy(path, target)
        let corpus = Path.Combine(temporary, "evidence/github-substrate-v2/gs2-06-5/corpus.json")
        let changed = File.ReadAllText(corpus).Replace("\"environment\": \"release\"", "\"environment\": \"coordination\"")
        File.WriteAllText(corpus, changed)
        let code, output = runValidator temporary
        Assert.NotEqual(0, code)
        Assert.Contains("baseline refused", output)
    finally
        if Directory.Exists temporary then Directory.Delete(temporary, true)

[<Fact>]
let ``production Q3 rejects divergence from the canonical permission producer`` () =
    let temporary = Path.Combine(Path.GetTempPath(), "fsgg-permission-producer-" + Guid.NewGuid().ToString("n"))
    try
        Directory.CreateDirectory(temporary) |> ignore
        for path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(root, path)
            let segments = relative.Split(Path.DirectorySeparatorChar)
            if not (segments |> Array.exists (fun value -> value = ".git" || value = "bin" || value = "obj" || value = "artifacts")) then
                let target = Path.Combine(temporary, relative)
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                File.Copy(path, target)
        let census = Path.Combine(temporary, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/permission-census.json")
        let changed = File.ReadAllText(census).Replace(
            "[\"actions-administration\",\"organization-administration\",\"project-administration\",\"release-administration\",\"repository-administration\",\"security-administration\"]",
            "[\"attacker-invented-permission\"]")
        File.WriteAllText(census, changed)
        let code, output = runValidator temporary
        Assert.NotEqual(0, code)
        Assert.Contains("permission compilation baseline refused", output)
    finally
        if Directory.Exists temporary then Directory.Delete(temporary, true)

[<Fact>]
let ``public contract surface has no production mutation capability`` () =
    let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubPermissionCompilationQualification.fsi"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "PATCH"; "POST"; "DELETE" ] do
        Assert.DoesNotContain(forbidden, surface)

[<Fact>]
let ``accepted provider evidence is durable in the candidate Git tree`` () =
    let analysis = "readiness/246-permission-compilation/analysis.json"
    let qualification = "artifacts/test-results/246-permission-compilation/qualification.trx"
    let workModel = "readiness/246-permission-compilation/work-model.json"
    let verification = "readiness/246-permission-compilation/verify.json"
    let paths = [ analysis; qualification; workModel; verification ]
    let code, output = tracked paths
    if code <> 0 then failwith output

    let expected =
        [ analysis, "cf59f15eb5a04511ab06477b1455f08b869f5ed00472571862577261030afc77"
          qualification, "766aba973103662273625dca96ba9d0639d4a40c16840a5e51fe6a2dc31d1493"
          workModel, "9e2bf23a171a9089eee522c962a9911e13abf71fef14633e0b2a37ca8b8e61a9"
          verification, "478bed672d7f450edeab22af0018f0c924290a1b63435eaaeca0451da17f849f" ]
    for relative, digest in expected do
        let path = Path.Combine(root, relative)
        Assert.True(File.Exists path, $"provider evidence is absent: {relative}")
        Assert.Equal(digest, sha256 path)

    let evidence = File.ReadAllText(Path.Combine(root, "work/246-permission-compilation/evidence.yml"))
    Assert.Contains($"path: {analysis}", evidence)
    Assert.Equal(5, evidence.Split($"source: {qualification}", StringSplitOptions.None).Length - 1)
    Assert.Equal(5, evidence.Split("sha256:766aba973103662273625dca96ba9d0639d4a40c16840a5e51fe6a2dc31d1493", StringSplitOptions.None).Length - 1)
    let verify = File.ReadAllText(Path.Combine(root, verification))
    Assert.Contains($"\"path\": \"{workModel}\"", verify)
    Assert.Contains("9e2bf23a171a9089eee522c962a9911e13abf71fef14633e0b2a37ca8b8e61a9", verify)

    let results =
        XDocument.Load(Path.Combine(root, qualification)).Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = "UnitTestResult")
        |> Seq.map (fun element -> element.Attribute(XName.Get "outcome").Value)
        |> Seq.countBy id
        |> Map.ofSeq
    Assert.Equal(6, results |> Map.tryFind "Passed" |> Option.defaultValue 0)
    Assert.Equal(0, results |> Map.tryFind "Failed" |> Option.defaultValue 0)
    Assert.Equal(0, results |> Map.tryFind "NotExecuted" |> Option.defaultValue 0)
