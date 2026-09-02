#load "../src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubRequiredCheckCensusQualification.fs"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-06-2")
let corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "corpus.json"))).AsObject()
let expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(evidenceRoot, "independent-expectations.json"))).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()
let texts (node: JsonObject) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq
let optionalInt64 (node: JsonObject) (name: string) = if isNull node[name] then None else Some(node[name].GetValue<int64>())
let event (node: JsonNode) =
    let value = node.AsObject()
    { Declared = value["declared"].GetValue<bool>()
      BranchFilters = texts value "branchFilters"
      PathFilters = texts value "pathFilters"
      ActivityTypes = texts value "activityTypes" }
let source value =
    if value = "classic" then ClassicProtection
    elif value.StartsWith("ruleset:") then Ruleset(Int64.Parse(value.Substring("ruleset:".Length)))
    else failwith $"unsupported required-check source {value}"
let requirement repository (node: JsonNode) =
    let value = node.AsObject()
    { Repository = repository
      Context = text value "context"
      IntegrationId = optionalInt64 value "integrationId"
      Source = source (text value "source") }
let producer repository (node: JsonNode) =
    let value = node.AsObject()
    { Repository = repository
      Context = text value "context"
      IntegrationId = optionalInt64 value "integrationId"
      Workflow = text value "workflow"
      Job = text value "job"
      WorkflowRevision = text value "workflowRevision"
      WorkflowSha256 = text value "workflowSha256"
      PullRequest = event value["pullRequest"]
      MergeGroup = event value["mergeGroup"]
      DependenciesComplete = value["dependenciesComplete"].GetValue<bool>()
      Conditional = value["conditional"].GetValue<bool>()
      ContinueOnError = value["continueOnError"].GetValue<bool>() }

let repository = text corpus "repository"
let snapshot =
    { SchemaVersion = 1
      Repository = repository
      ProfileSeal = text corpus "profileSeal"
      PrerequisiteReceiptDigest = text corpus "prerequisiteReceiptDigest"
      AuthorityEvidenceSha256 = text corpus "authorityEvidenceSha256"
      SourceRevision = text corpus "sourceRevision"
      ObservedAt = DateTimeOffset.Parse(text corpus "observedAt")
      Complete = corpus["complete"].GetValue<bool>()
      ClassicComplete = corpus["classicComplete"].GetValue<bool>()
      RulesetsComplete = corpus["rulesetsComplete"].GetValue<bool>()
      ProducersComplete = corpus["producersComplete"].GetValue<bool>()
      Requirements = corpus["requirements"].AsArray() |> Seq.map (requirement repository) |> List.ofSeq
      Producers = corpus["producers"].AsArray() |> Seq.map (producer repository) |> List.ofSeq }
let asOf = snapshot.ObservedAt
let compile candidate = RequiredCheckCensusAdapter.compile asOf (TimeSpan.FromHours 1) candidate
let refused candidate = compile candidate |> Result.isError
let report = compile snapshot |> Result.defaultWith (failwithf "required-check census baseline refused: %A")
let altered candidate = RequiredCheckCensusAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) candidate |> Result.isError

let workflowPath = report.Entries |> List.map _.ProducerWorkflow |> List.distinct |> List.exactlyOne
let gitShow = ProcessStartInfo("git")
gitShow.WorkingDirectory <- root
gitShow.UseShellExecute <- false
gitShow.RedirectStandardOutput <- true
gitShow.RedirectStandardError <- true
gitShow.ArgumentList.Add "show"
gitShow.ArgumentList.Add $"{snapshot.SourceRevision}:{workflowPath}"
let gitChild = Process.Start gitShow
let workflowStream = new MemoryStream()
gitChild.StandardOutput.BaseStream.CopyTo workflowStream
let gitError = gitChild.StandardError.ReadToEnd()
gitChild.WaitForExit()
if gitChild.ExitCode <> 0 then
    failwith $"retained producer workflow revision is unavailable: {gitError}"
let workflowBytes = workflowStream.ToArray()
gitChild.Dispose()
workflowStream.Dispose()
let workflowSha256 = workflowBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
if report.Entries |> List.exists (fun entry -> entry.ProducerWorkflowSha256 <> workflowSha256) then
    failwith "retained producer workflow digest differs from the exact revision-addressed workflow"
let workflowText = Encoding.UTF8.GetString workflowBytes
if not (workflowText.Contains("\n  pull_request:\n")) || workflowText.Contains("\n  merge_group:\n") then
    failwith "retained workflow trigger observation differs from the exact revision-addressed workflow"
for entry in report.Entries do
    if not (workflowText.Contains($"\n  {entry.ProducerJob}:\n")) then
        failwith $"retained producer job is absent from the exact revision-addressed workflow: {entry.ProducerJob}"

let authoritiesPath = Path.Combine(evidenceRoot, "authorities.json")
let authorityBytes = File.ReadAllBytes authoritiesPath
let authoritySha256 = authorityBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
if snapshot.AuthorityEvidenceSha256 <> authoritySha256 then failwith "authority evidence digest differs"
let authorities = JsonNode.Parse(authorityBytes).AsObject()
let classic = authorities["classicProtection"].AsObject()
if classic["httpStatus"].GetValue<int>() <> 404 || classic["requirements"].AsArray().Count <> 0 then
    failwith "classic protection absence evidence differs"
let rulesets = authorities["rulesets"].AsArray()
if rulesets.Count <> 1 then failwith "ruleset authority count differs"
let ruleset = rulesets[0].AsObject()
if ruleset["id"].GetValue<int64>() <> 21633423L then failwith "ruleset authority differs"
let authoritativeChecks =
    ruleset["requiredStatusChecks"].AsArray()
    |> Seq.map (fun value -> value["context"].GetValue<string>(), value["integrationId"].GetValue<int64>())
    |> List.ofSeq
let retainedChecks = report.Entries |> List.map (fun entry -> entry.Context, entry.IntegrationId.Value)
if authoritativeChecks <> retainedChecks then failwith "retained census differs from authority evidence"

if fsi.CommandLineArgs |> Array.contains "--mint" then
    printfn "%s" report.Seal
else
    let expectedControls = GitHubRequiredCheckCensusQualification.requiredControls |> List.map GitHubRequiredCheckCensusQualification.controlId
    if texts expectations "controls" <> expectedControls then failwith "independent control inventory differs from closed qualification contract"
    if text expectations "repository" <> report.Repository then failwith "repository differs"
    if texts expectations "expectedOrder" <> (report.Entries |> List.map _.Context) then failwith "stable order differs"
    let expectedInt (name: string) = expectations[name].GetValue<int>()
    let aggregate = report.Aggregate
    if aggregate.RequiredCount <> expectedInt "requiredCount"
       || aggregate.ClassicOnlyCount <> expectedInt "classicOnlyCount"
       || aggregate.RulesetOnlyCount <> expectedInt "rulesetOnlyCount"
       || aggregate.DualSourceCount <> expectedInt "dualSourceCount"
       || aggregate.IntegrationBoundCount <> expectedInt "integrationBoundCount"
       || aggregate.PullRequestUnconditionalCount <> expectedInt "pullRequestUnconditionalCount"
       || aggregate.MergeGroupUnconditionalCount <> expectedInt "mergeGroupUnconditionalCount" then failwith "stable aggregate differs"
    if report.Seal <> text expectations "expectedSeal" then failwith "census seal differs"
    if RequiredCheckCensusAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) snapshot <> Ok report then failwith "exact census replay failed"

    let replaceProducer context transform =
        { snapshot with Producers = snapshot.Producers |> List.map (fun value -> if value.Context = context then transform value else value) }
    let generatedMutation = function
        | PrerequisiteReceipt -> refused { snapshot with PrerequisiteReceiptDigest = "invalid" }
        | ProfileBinding -> refused { snapshot with ProfileSeal = "invalid" }
        | SourceBinding ->
            let renamed = replaceProducer "compiler-and-tests" (fun value -> { value with Workflow = ".github/workflows/renamed.yml" })
            refused { snapshot with SourceRevision = "main" }
            && RequiredCheckCensusAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) renamed = Error [ AlteredRequiredCheckCensusSeal ]
        | CompleteAuthorities -> refused { snapshot with RulesetsComplete = false }
        | StableOrdering -> compile { snapshot with Requirements = List.rev snapshot.Requirements; Producers = List.rev snapshot.Producers } = Ok report
        | ExactIdentity ->
            let mixed = { snapshot.Requirements.Head with IntegrationId = None }
            refused { snapshot with Requirements = mixed :: snapshot.Requirements }
        | AuthorityUnion ->
            let changed = { snapshot with Requirements = snapshot.Requirements |> List.map (fun value -> { value with Source = Ruleset 99L }) }
            (report.Entries |> List.forall (fun entry -> entry.Sources = [ Ruleset 21633423L ])) && altered changed
        | ProvenanceRetention ->
            report.Entries |> List.forall (fun entry -> entry.Sources = [ Ruleset 21633423L ] && entry.ProducerWorkflow = ".github/workflows/bootstrap-qualification.yml")
        | ProducerCompleteness -> refused { snapshot with Producers = snapshot.Producers.Tail }
        | PullRequestProduction ->
            not report.Aggregate.PullRequestReady
            && (report.Entries |> List.forall (fun entry -> entry.PullRequest.Declared))
            && altered (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with Declared = false } }))
        | MergeGroupProduction ->
            not report.Aggregate.MergeGroupReady
            && (report.Entries |> List.forall (fun entry -> not entry.MergeGroup.Declared))
            && altered (replaceProducer "compiler-and-tests" (fun value -> { value with MergeGroup = { value.MergeGroup with Declared = true } }))
        | EventFilters ->
            altered (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with PathFilters = [ "src/**" ] } }))
            && altered (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with BranchFilters = [ "main" ] } }))
            && altered (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with ActivityTypes = [ "synchronize" ] } }))
        | JobConditions ->
            (report.Entries |> List.forall _.Conditional)
            && altered (replaceProducer "compiler-and-tests" (fun value -> { value with Conditional = false }))
            && altered (replaceProducer "compiler-and-tests" (fun value -> { value with ContinueOnError = true }))
        | DependencyClosure -> altered (replaceProducer "compiler-and-tests" (fun value -> { value with DependenciesComplete = false }))
        | RepositoryBoundary -> refused { snapshot with Requirements = { snapshot.Requirements.Head with Repository = "FS-GG/other" } :: snapshot.Requirements.Tail }
        | Freshness -> RequiredCheckCensusAdapter.compile (asOf.AddHours 2) (TimeSpan.FromHours 1) snapshot |> Result.isError
        | StableAggregates -> not ((string aggregate).Contains("compiler-and-tests")) && aggregate.RequiredCount = report.Entries.Length
        | ExactSeal -> RequiredCheckCensusAdapter.verify (String.replicate 64 "0") asOf (TimeSpan.FromHours 1) snapshot |> Result.isError
        | ExactReplay -> RequiredCheckCensusAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) snapshot = Ok report
        | QuintUnchanged -> File.ReadAllBytes(Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")) |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant() = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"
        | NoPlanSurface -> not (File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fsi")).Contains("val plan"))
        | NoApplySurface -> not (File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fsi")).Contains("val apply"))

    let independentMutation = function
        | PrerequisiteReceipt ->
            let receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-06.1.json")))
            receipt["digest"].GetValue<string>() = snapshot.PrerequisiteReceiptDigest
        | ProfileBinding -> snapshot.ProfileSeal = "f3524e8edbd6b88b0783551c14377881dee5dd958ebd4835d77a57913d30d74b"
        | SourceBinding -> snapshot.SourceRevision = "f3a92488d6c15e1a4592686c6f00c375c62b167d" && workflowSha256 = "0b913aab5149d035addd280adbe7ed069dc2df9a25a062add4b46a0aba44bd4a"
        | CompleteAuthorities -> snapshot.Complete && snapshot.ClassicComplete && snapshot.RulesetsComplete && snapshot.ProducersComplete
        | StableOrdering -> report.Entries |> List.map _.Context = [ "bootstrap-recovery"; "compiler-and-tests"; "dependency-and-security"; "deterministic-build"; "evidence-manifest"; "package-install-smoke" ]
        | ExactIdentity -> report.Entries |> List.forall (_.IntegrationId >> (=) (Some 15368L))
        | AuthorityUnion -> aggregate.DualSourceCount = 0 && aggregate.RulesetOnlyCount = 6
        | ProvenanceRetention -> report.Entries |> List.sumBy (_.Sources >> List.length) = snapshot.Requirements.Length
        | ProducerCompleteness -> report.Entries.Length = snapshot.Producers.Length
        | PullRequestProduction -> not aggregate.PullRequestReady && aggregate.PullRequestUnconditionalCount = 0
        | MergeGroupProduction -> not aggregate.MergeGroupReady && aggregate.MergeGroupUnconditionalCount = 0
        | EventFilters -> snapshot.Producers |> List.forall (fun value -> value.PullRequest.BranchFilters.IsEmpty && value.PullRequest.PathFilters.IsEmpty && value.PullRequest.ActivityTypes.IsEmpty && value.MergeGroup.BranchFilters.IsEmpty && value.MergeGroup.PathFilters.IsEmpty && value.MergeGroup.ActivityTypes.IsEmpty)
        | JobConditions -> snapshot.Producers |> List.forall (fun value -> value.Conditional && not value.ContinueOnError)
        | DependencyClosure -> snapshot.Producers |> List.forall _.DependenciesComplete
        | RepositoryBoundary -> snapshot.Requirements |> List.forall (_.Repository >> (=) snapshot.Repository) && snapshot.Producers |> List.forall (_.Repository >> (=) snapshot.Repository)
        | Freshness -> asOf - snapshot.ObservedAt <= TimeSpan.FromHours 1
        | StableAggregates -> aggregate.RequiredCount = 6 && aggregate.DualSourceCount + aggregate.RulesetOnlyCount + aggregate.ClassicOnlyCount = aggregate.RequiredCount
        | ExactSeal -> report.Seal.Length = 64 && report.Seal = text expectations "expectedSeal"
        | ExactReplay -> generatedMutation ExactReplay
        | QuintUnchanged -> generatedMutation QuintUnchanged
        | NoPlanSurface -> generatedMutation NoPlanSurface
        | NoApplySurface -> generatedMutation NoApplySurface

    let generated = GitHubRequiredCheckCensusQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = true })
    let independent = GitHubRequiredCheckCensusQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = true })
    match GitHubRequiredCheckCensusQualification.validate generated independent with
    | Ok () -> printfn "GITHUB_REQUIRED_CHECK_CENSUS_OK repository=%s required=%d classicOnly=%d rulesetOnly=%d dual=%d controls=%d seal=%s" report.Repository aggregate.RequiredCount aggregate.ClassicOnlyCount aggregate.RulesetOnlyCount aggregate.DualSourceCount generated.Length report.Seal
    | Error findings -> failwithf "required-check census qualification failed: %A" findings
