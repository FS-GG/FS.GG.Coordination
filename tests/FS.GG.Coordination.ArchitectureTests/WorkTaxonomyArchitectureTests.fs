module FS.GG.Coordination.WorkTaxonomyArchitectureTests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Xunit
open FS.GG.Coordination.Core

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

[<Fact>]
let ``work taxonomy signature precedes implementation and stays in pure core`` () =
    let project = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Core/FS.GG.Coordination.Core.fsproj"))
    Assert.True(project.IndexOf("WorkTaxonomy.fsi", StringComparison.Ordinal) < project.IndexOf("WorkTaxonomy.fs\"", StringComparison.Ordinal))
    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Core/WorkTaxonomy.fs"))
    for forbidden in [ "Octokit"; "HttpClient"; "GITHUB_TOKEN"; "api.github.com"; "GraphQL"; "FS.GG.Coordination.GitHub" ] do
        Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``native issue type catalogue is closed and lifecycle derives from it`` () =
    Assert.Equal<string list>(
        [ "Epic"; "Feature"; "Task"; "Bug"; "Decision"; "Register"; "Directive" ],
        WorkTaxonomy.nativeIssueTypes |> List.map WorkTaxonomy.nativeIssueTypeName)
    Assert.Equal(
        [ "work"; "work"; "work"; "work"; "work"; "standing-exempt"; "standing-exempt" ],
        WorkTaxonomy.nativeIssueTypes
        |> List.map (fun native ->
            let sample =
                { StableRowId = WorkTaxonomy.nativeIssueTypeName native
                  RepositoryScope = "FS-GG/repo"
                  Revision = "revision"
                  NativeIssueType = Some(WorkTaxonomy.nativeIssueTypeName native)
                  LegacyClass = None
                  LegacyKind = None
                  HierarchyPresent = false
                  HierarchyPreservable = true
                  RepositoryScopePreservable = true
                  Complete = true
                  Current = true
                  Readable = true }
            match WorkTaxonomy.classify sample with
            | Ok classification -> WorkTaxonomy.lifecycleName classification.Lifecycle
            | Error diagnostics -> failwith (diagnostics |> List.map WorkTaxonomy.diagnosticCode |> String.concat ",")))

[<Fact>]
let ``frozen taxonomy corpus is complete and registered validator is offline`` () =
    let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-1/corpus.json")
    use document = JsonDocument.Parse(File.ReadAllBytes corpusPath)
    Assert.Equal("fsgg.github-substrate-v2.work-taxonomy-corpus/1", document.RootElement.GetProperty("schema").GetString())
    Assert.Equal(32, document.RootElement.GetProperty("cases").GetArrayLength())
    let validator = File.ReadAllText(Path.Combine(root, "eng/validate-github-work-taxonomy.fsx"))
    for forbidden in [ "HttpClient"; "GITHUB_TOKEN"; "Environment.GetEnvironmentVariable"; "gh api" ] do
        Assert.DoesNotContain(forbidden, validator, StringComparison.Ordinal)
    Assert.Contains("WTX-OMISSION-INVERSION", validator, StringComparison.Ordinal)
    Assert.Contains("independentRefusals", validator, StringComparison.Ordinal)
    let gates = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
    Assert.Contains("\"id\": \"github-work-taxonomy-contract\"", gates, StringComparison.Ordinal)
    Assert.Contains("\"args\": [\"fsi\", \"eng/validate-github-work-taxonomy.fsx\", \"--\", \".\"]", gates, StringComparison.Ordinal)

[<Fact>]
let ``registered work taxonomy Q2 command passes repository local evidence`` () =
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "fsi"; "eng/validate-github-work-taxonomy.fsx"; "--"; "." ] do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    Assert.Equal(0, child.ExitCode)
    Assert.Equal("", error.Trim())
    Assert.Contains("github-work-taxonomy-contract OK cases=32 accepted=18 refused=14 q=Q2 network=offline inversions=17", output, StringComparison.Ordinal)
