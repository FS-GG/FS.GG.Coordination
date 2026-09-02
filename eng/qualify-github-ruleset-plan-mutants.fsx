open System
open System.Diagnostics
open System.IO

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let sourceRoot = Path.Combine(root, "src/FS.GG.Coordination.GitHub")
let source = File.ReadAllText(Path.Combine(sourceRoot, "RulesetPlanAdapter.fs"))
let probe = """#load "RepositoryProfileAdapter.fs"
#load "RequiredCheckCensusAdapter.fs"
#load "RulesetPlanAdapter.fs"
open System
open FS.GG.Coordination.GitHub
let revision = String.replicate 40 "a"
let digest = String.replicate 64 "b"
let at = DateTimeOffset.Parse("2026-09-02T00:00:00Z")
let authority = { Id = "authority"; FullName = "FS-GG/.github"; Role = Authority; Capabilities = [ "labels" ]; KitDelivery = None; AbsenceCover = None; Reason = None }
let repository = { Id = "example"; FullName = "FS-GG/example"; Role = Framework; Capabilities = [ "labels"; "coordination-kit" ]; KitDelivery = Some "package"; AbsenceCover = Some "required"; Reason = None }
let rows = [ authority; repository ]
let roster = { SchemaVersion = 1; SourceRevision = revision; SourceArtifactSha256 = digest; CanonicalRosterSha256 = RepositoryProfileAdapter.canonicalRosterDigest rows; ReviewedAt = at; Complete = true; Rows = rows }
let profile = RepositoryProfileAdapter.compile at (TimeSpan.FromHours 1) roster |> Result.defaultWith (failwithf "%A")
let event declared = { Declared = declared; BranchFilters = []; PathFilters = []; ActivityTypes = [] }
let requirement = { Repository = repository.FullName; Context = "build"; IntegrationId = Some 15368L; Source = Ruleset 42L }
let producer = { Repository = repository.FullName; Context = "build"; IntegrationId = Some 15368L; Workflow = ".github/workflows/ci.yml"; Job = "build"; WorkflowRevision = revision; WorkflowSha256 = digest; PullRequest = event true; MergeGroup = event false; DependenciesComplete = true; Conditional = false; ContinueOnError = false }
let census = { SchemaVersion = 1; Repository = repository.FullName; ProfileSeal = profile.Seal; PrerequisiteReceiptDigest = digest; AuthorityEvidenceSha256 = digest; SourceRevision = revision; ObservedAt = at; Complete = true; ClassicComplete = true; RulesetsComplete = true; ProducersComplete = true; Requirements = [ requirement ]; Producers = [ producer ] }
let censusReport = RequiredCheckCensusAdapter.compile at (TimeSpan.FromHours 1) census |> Result.defaultWith (failwithf "%A")
let snapshot = { SchemaVersion = 1; Repository = repository.FullName; PrerequisiteReceiptDigest = digest; ProfileSnapshot = roster; ExpectedProfileSeal = profile.Seal; CensusSnapshot = Some census; ExpectedCensusSeal = Some censusReport.Seal; CurrentPolicyRepository = repository.FullName; CurrentPolicyRevision = revision; CurrentPolicyEvidenceSha256 = digest; CurrentPolicyObservedAt = at; CurrentPolicyComplete = true; ObservedAt = at; Complete = true; ApprovedBypass = []; RequestedBypass = []; Exceptions = [] }
let report = RulesetPlanAdapter.compile at (TimeSpan.FromHours 1) snapshot |> Result.defaultWith (failwithf "%A")
let branch = report.DefaultBranch.Value
let tags = report.ReleaseTags.Value
let policy = report.RepositoryPolicy.Value
if branch.Include <> [ "~DEFAULT_BRANCH" ] || not branch.ProtectDeletion || not branch.BlockNonFastForward || not branch.RequirePullRequest || not branch.DismissStaleReviews || not branch.RequireConversationResolution || not branch.StrictChecks then failwith "default branch target survived mutant"
if tags.Include <> [ "refs/tags/v*" ] || not tags.ProtectDeletion || not tags.BlockNonFastForward || not tags.BlockUpdate || not tags.RequireSignatures then failwith "release tag target survived mutant"
if policy.AllowedMergeMethods <> [ Squash ] then failwith "merge method mutant survived"
if not policy.AllowAutoMerge then failwith "auto merge mutant survived"
if not policy.DeleteBranchOnMerge then failwith "branch deletion mutant survived"
"""

let runProbe directory =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- directory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add "fsi"
    info.ArgumentList.Add "probe.fsx"
    use child = Process.Start info
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    child.ExitCode, output, error

let mutations =
    [ "default-branch-target", "Include = [ \"~DEFAULT_BRANCH\" ]", "Include = [ \"refs/heads/main\" ]"
      "release-tag-target", "Include = [ \"refs/tags/v*\" ]", "Include = [ \"refs/tags/release-*\" ]"
      "merge-methods", "AllowedMergeMethods = [ Squash ]", "AllowedMergeMethods = [ MergeCommit ]"
      "auto-merge", "AllowAutoMerge = true", "AllowAutoMerge = false"
      "branch-deletion", "DeleteBranchOnMerge = true", "DeleteBranchOnMerge = false" ]

let temporary = Path.Combine(Path.GetTempPath(), $"fsgg-ruleset-plan-mutants-{Guid.NewGuid():N}")
Directory.CreateDirectory temporary |> ignore
try
    for name in [ "RepositoryProfileAdapter.fs"; "RequiredCheckCensusAdapter.fs" ] do File.Copy(Path.Combine(sourceRoot, name), Path.Combine(temporary, name))
    File.WriteAllText(Path.Combine(temporary, "probe.fsx"), probe)
    File.WriteAllText(Path.Combine(temporary, "RulesetPlanAdapter.fs"), source)
    let baselineCode, _, baselineError = runProbe temporary
    if baselineCode <> 0 then failwith $"baseline probe failed: {baselineError}"
    for control, original, replacement in mutations do
        if not (source.Contains original) then failwith $"mutation anchor missing: {control}"
        File.WriteAllText(Path.Combine(temporary, "RulesetPlanAdapter.fs"), source.Replace(original, replacement))
        let code, _, _ = runProbe temporary
        if code = 0 then failwith $"mutation survived: {control}"
    printfn "RULESET_PLAN_MUTANTS_OK controls=%s" (mutations |> List.map (fun (name, _, _) -> name) |> String.concat ",")
finally
    Directory.Delete(temporary, true)
