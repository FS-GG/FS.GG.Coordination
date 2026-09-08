module FS.GG.Coordination.QualificationReuseSelectionTests

open System
open System.IO
open Xunit
open FS.GG.Coordination.Qualification.Contracts.QualificationReuse
open FS.GG.Coordination.Qualification.Contracts

let digest character = String.replicate 64 character
let head character = String.replicate 40 character

let identity binding =
    { BehavioralSha256 = digest "a"; CompiledContractSha256 = digest "b"
      ToolchainProfileSha256 = digest "c"; VerificationBoundsSha256 = digest "d"
      FormalCorpusSha256 = digest "e"; HarnessSha256 = digest "f"; BindingSha256 = digest binding }

let candidate revision binding = createCandidateObligation (head revision) (head "0") (digest "1") (digest "2") (identity binding)
let delta empty = { EvaluatorSha256 = digest "3"; DeltaSha256 = digest (if empty then "0" else "4"); IsEmpty = empty }
let prior value =
    { Candidate = value; RunId = 42L; Attempt = 2; ExecutedReceiptSha256 = digest "5"
      CompletedAt = "2026-09-08T00:00:00Z"; ExpiresAt = "2026-09-10T00:00:00Z"; Authentic = true; Complete = true }
let now = DateTimeOffset.Parse "2026-09-08T12:00:00Z"

[<Fact>]
let ``nonbehavioural binding change reuses while coherent run remains pending`` () =
    let selected = selectReusable now (candidate "b" "7") (Some(prior (candidate "a" "6"))) (delta true) (Some(digest "8"))
    Assert.Equal(Reused, selected.Disposition)
    Assert.True selected.CoherentRunPending
    Assert.Equal(Pending, selected.CoherentState)
    Assert.NotEmpty(selectionBytes selected)

[<Theory>]
[<InlineData("behavior")>]
[<InlineData("contract")>]
[<InlineData("toolchain")>]
[<InlineData("bounds")>]
[<InlineData("corpus")>]
[<InlineData("harness")>]
let ``semantic identity mutation requires current execution`` field =
    let current = candidate "b" "6"
    let oldIdentity = identity "6"
    let mutated =
        match field with
        | "behavior" -> { oldIdentity with BehavioralSha256 = digest "9" }
        | "contract" -> { oldIdentity with CompiledContractSha256 = digest "9" }
        | "toolchain" -> { oldIdentity with ToolchainProfileSha256 = digest "9" }
        | "bounds" -> { oldIdentity with VerificationBoundsSha256 = digest "9" }
        | "corpus" -> { oldIdentity with FormalCorpusSha256 = digest "9" }
        | _ -> { oldIdentity with HarnessSha256 = digest "9" }
    let old = createCandidateObligation (head "a") (head "0") (digest "1") (digest "2") mutated
    let selected = selectReusable now current (Some(prior old)) (delta true) None
    Assert.Equal(Current, selected.Disposition)

[<Fact>]
let ``nonempty semantic delta and absent binding proof require current execution`` () =
    let semantic = selectReusable now (candidate "b" "6") (Some(prior (candidate "a" "6"))) (delta false) None
    let binding = selectReusable now (candidate "b" "7") (Some(prior (candidate "a" "6"))) (delta true) None
    Assert.Equal(Current, semantic.Disposition)
    Assert.Equal(Current, binding.Disposition)

[<Theory>]
[<InlineData(false, true, "2026-09-10T00:00:00Z")>]
[<InlineData(true, false, "2026-09-10T00:00:00Z")>]
[<InlineData(true, true, "2026-09-08T01:00:00Z")>]
let ``incomplete inauthentic or expired prior requires current execution`` authentic complete expires =
    let old = { prior (candidate "a" "6") with Authentic = authentic; Complete = complete; ExpiresAt = expires }
    Assert.Equal(Current, (selectReusable now (candidate "b" "6") (Some old) (delta true) None).Disposition)

[<Fact>]
let ``coherent failures block before merge and dispute after merge`` () =
    let selected = selectReusable now (candidate "b" "6") None (delta true) None
    Assert.Equal(Blocked, (applyCoherentOutcome false false selected).CoherentState)
    Assert.Equal(Disputed, (applyCoherentOutcome true false selected).CoherentState)
    Assert.Equal(Passed, (applyCoherentOutcome true true selected).CoherentState)

[<Fact>]
let ``partition aggregation is complete deterministic and order independent`` () =
    let plan = createPartitionPlan (candidate "b" "6") 6 [ "unit"; "architecture"; "formal"; "security"; "package"; "recovery"; "projection" ]
    let receipts = plan.Partitions |> List.map (fun (index, obligations) -> createPartitionReceipt plan index obligations true)
    Assert.Equal(6, plan.PartitionCount)
    Assert.Equal(Ok true, aggregatePartitions plan (List.rev receipts))
    Assert.Equal(Error "partition coverage incomplete", aggregatePartitions plan (List.tail receipts))
    let substituted = { receipts.Head with Obligations = [ "substituted" ] }
    Assert.Equal(Error "partition receipt is substituted or stale", aggregatePartitions plan (substituted :: List.tail receipts))
    let failed = { receipts.Head with Passed = false; ReceiptSha256 = "" }
    let failed = createPartitionReceipt plan failed.Partition failed.Obligations false
    Assert.Equal(Ok false, aggregatePartitions plan (failed :: List.tail receipts))
    Assert.Equal(Error "partition indexes are missing or duplicated", aggregatePartitions plan (receipts.Head :: List.take 5 receipts))
    Assert.True(parsePartitionPlan plan.Candidate (partitionPlanBytes plan) |> Result.isOk)
    Assert.True(parsePartitionReceipt (partitionReceiptBytes receipts.Head) |> Result.isOk)
    let stale = partitionReceiptBytes receipts.Head |> Text.Encoding.UTF8.GetString |> fun text -> text.Replace(plan.PlanSha256, digest "9") |> Text.Encoding.UTF8.GetBytes
    Assert.True(parsePartitionReceipt stale |> Result.isError)
    let aggregate = createCoherentAggregateReceipt plan receipts |> Result.defaultWith failwith
    Assert.True(parseCoherentAggregateReceipt (coherentAggregateReceiptBytes aggregate) |> Result.isOk)
    let aggregateStale = coherentAggregateReceiptBytes aggregate |> Text.Encoding.UTF8.GetString |> fun text -> text.Replace(receipts.Head.ReceiptSha256, digest "9") |> Text.Encoding.UTF8.GetBytes
    Assert.True(parseCoherentAggregateReceipt aggregateStale |> Result.isError)

[<Fact>]
let ``optimistic workflow projection retains recovery and continuation bounds`` () =
    let root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
    let code, output, error = BootstrapCi.execute [ "optimistic-projection"; "--root"; root ]
    Assert.Equal(0, code)
    Assert.Contains("BOOTSTRAP_CI_OK", output)
    Assert.Equal("", error)

[<Fact>]
let ``three overlapping squash candidates retain independent obligations`` () =
    let candidates = [ candidate "a" "6"; candidate "b" "6"; candidate "c" "6" ]
    Assert.Equal(3, candidates |> List.map _.ObligationSha256 |> Set.ofList |> Set.count)
    let selections = candidates |> List.map (fun value -> selectReusable now value None (delta true) None)
    let failed = applyCoherentOutcome false false selections.Head
    Assert.Equal(Blocked, failed.CoherentState)
    Assert.All(selections.Tail, fun value -> Assert.Equal(Pending, value.CoherentState))

[<Fact>]
let ``squash tree identity is explicit while equal behavior can still reuse`` () =
    let old = candidate "a" "6"
    let changedTree = createCandidateObligation (head "b") (head "0") (digest "9") (digest "8") (identity "6")
    let selected = selectReusable now changedTree (Some(prior old)) (delta true) None
    Assert.NotEqual<string>(old.ObligationSha256, changedTree.ObligationSha256)
    Assert.Equal(Reused, selected.Disposition)

[<Fact>]
let ``workflow recovery is paginated non mutating and never cancels coherent validation`` () =
    let root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
    let workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/optimistic-parallel-validation.yml"))
    let template = File.ReadAllText(Path.Combine(root, "eng/optimistic-parallel-validation.yml.template"))
    let recovery = File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/optimistic-recovery.sh"))
    Assert.Contains("group: optimistic-coherent-${{ inputs.candidate_sha || github.event.pull_request.head.sha || github.event.merge_group.head_sha || github.sha }}", workflow)
    Assert.DoesNotContain("github.event.pull_request.number", workflow)
    Assert.Contains("cancel-in-progress: false", workflow)
    Assert.Contains("fail-fast: false", workflow)
    Assert.Contains("cron: '17 3 * * *'", workflow)
    Assert.True(String.Equals(workflow, template, StringComparison.Ordinal))
    Assert.Contains("gh api --paginate", recovery)
    Assert.Contains("git rev-list origin/main", recovery)
    Assert.Contains("sort -k1,1 -k2,2", recovery)
    Assert.Contains("active-candidates.txt", recovery)
    Assert.Contains("passed-candidates.txt", recovery)
    Assert.Contains("coherentRunPending:true", recovery)
    Assert.Contains("max-parallel: 6", workflow)
    Assert.Contains("optimistic-dispatch-recovery.sh", workflow)
    Assert.Contains("coherent-aggregate-", workflow)
    for forbidden in [ "git commit"; "git push"; "gh release"; "npm publish"; "dotnet nuget push" ] do
        Assert.DoesNotContain(forbidden, workflow + recovery)

[<Fact>]
let ``hosted partition scripts use typed receipts and complete suites`` () =
    let root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
    let run = File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/optimistic-run-partition.sh"))
    let aggregate = File.ReadAllText(Path.Combine(root, "eng/bootstrap-gates/optimistic-aggregate.sh"))
    Assert.Contains("FS.GG.Coordination.UnitTests.fsproj -c Release --no-restore --no-build", run)
    Assert.Contains("FS.GG.Coordination.ArchitectureTests.fsproj -c Release --no-restore --no-build", run)
    Assert.DoesNotContain("--filter", run)
    Assert.Contains("eng/optimistic-validation.fsx -- run-partition", run)
    Assert.Contains("eng/optimistic-validation.fsx -- aggregate", aggregate)
    Assert.DoesNotContain("jq -e", aggregate)
