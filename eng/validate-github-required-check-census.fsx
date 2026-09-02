#load "../src/FS.GG.Coordination.GitHub/RequiredCheckCensusAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubRequiredCheckCensusQualification.fs"

open System
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
            let dropped = { snapshot with Requirements = snapshot.Requirements |> List.filter (fun value -> value.Source <> ClassicProtection) }
            report.Entries.Head.Sources = [ ClassicProtection; Ruleset 81L ]
            && RequiredCheckCensusAdapter.verify report.Seal asOf (TimeSpan.FromHours 1) dropped = Error [ AlteredRequiredCheckCensusSeal ]
        | ProvenanceRetention -> report.Entries.Head.Sources.Length = 2 && report.Entries[1].Sources = [ Ruleset 81L ]
        | ProducerCompleteness -> refused { snapshot with Producers = snapshot.Producers.Tail }
        | PullRequestProduction -> refused (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with Declared = false } }))
        | MergeGroupProduction -> refused (replaceProducer "compiler-and-tests" (fun value -> { value with MergeGroup = { value.MergeGroup with Declared = false } }))
        | EventFilters ->
            refused (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with PathFilters = [ "src/**" ] } }))
            && refused (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with BranchFilters = [ "main" ] } }))
            && refused (replaceProducer "compiler-and-tests" (fun value -> { value with PullRequest = { value.PullRequest with ActivityTypes = [ "synchronize" ] } }))
        | JobConditions ->
            refused (replaceProducer "compiler-and-tests" (fun value -> { value with Conditional = true }))
            && refused (replaceProducer "compiler-and-tests" (fun value -> { value with ContinueOnError = true }))
        | DependencyClosure -> refused (replaceProducer "compiler-and-tests" (fun value -> { value with DependenciesComplete = false }))
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
        | SourceBinding -> snapshot.SourceRevision = "f3a92488d6c15e1a4592686c6f00c375c62b167d"
        | CompleteAuthorities -> snapshot.Complete && snapshot.ClassicComplete && snapshot.RulesetsComplete && snapshot.ProducersComplete
        | StableOrdering -> report.Entries |> List.map _.Context = [ "compiler-and-tests"; "policy" ]
        | ExactIdentity -> report.Entries |> List.forall (_.IntegrationId >> (=) (Some 15368L))
        | AuthorityUnion -> aggregate.DualSourceCount = 1 && aggregate.RulesetOnlyCount = 1
        | ProvenanceRetention -> report.Entries |> List.sumBy (_.Sources >> List.length) = snapshot.Requirements.Length
        | ProducerCompleteness -> report.Entries.Length = snapshot.Producers.Length
        | PullRequestProduction -> aggregate.PullRequestReady && aggregate.PullRequestUnconditionalCount = aggregate.RequiredCount
        | MergeGroupProduction -> aggregate.MergeGroupReady && aggregate.MergeGroupUnconditionalCount = aggregate.RequiredCount
        | EventFilters -> snapshot.Producers |> List.forall (fun value -> value.PullRequest.PathFilters.IsEmpty && value.MergeGroup.PathFilters.IsEmpty)
        | JobConditions -> snapshot.Producers |> List.forall (fun value -> not value.Conditional && not value.ContinueOnError)
        | DependencyClosure -> snapshot.Producers |> List.forall _.DependenciesComplete
        | RepositoryBoundary -> snapshot.Requirements |> List.forall (_.Repository >> (=) snapshot.Repository) && snapshot.Producers |> List.forall (_.Repository >> (=) snapshot.Repository)
        | Freshness -> asOf - snapshot.ObservedAt <= TimeSpan.FromHours 1
        | StableAggregates -> aggregate.RequiredCount = 2 && aggregate.DualSourceCount + aggregate.RulesetOnlyCount + aggregate.ClassicOnlyCount = aggregate.RequiredCount
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
