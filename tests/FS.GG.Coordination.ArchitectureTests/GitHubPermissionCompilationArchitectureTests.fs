module FS.GG.Coordination.GitHubPermissionCompilationArchitectureTests

open System
open System.Diagnostics
open System.IO
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
let ``public contract surface has no production mutation capability`` () =
    let surface = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubPermissionCompilationQualification.fsi"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "GetEnvironmentVariable"; "api.github.com"; "val apply"; "PATCH"; "POST"; "DELETE" ] do
        Assert.DoesNotContain(forbidden, surface)
