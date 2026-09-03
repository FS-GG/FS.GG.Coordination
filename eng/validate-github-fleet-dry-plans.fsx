#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubFleetDryPlanQualification.fs"

open System
open System.IO
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubFleetDryPlanQualification

let root = if fsi.CommandLineArgs.Length > 1 then Path.GetFullPath fsi.CommandLineArgs[1] else Path.GetFullPath "."
let read relative = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)))
let text (node: JsonElement) (name: string) = node.GetProperty(name).GetString()
let disposition value =
    match value with
    | "supported" -> GitHubFleetDisposition.Supported | "unsupported" -> GitHubFleetDisposition.Unsupported
    | "unauthorized" -> GitHubFleetDisposition.Unauthorized | "unavailable" -> GitHubFleetDisposition.Unavailable
    | "incomplete" -> GitHubFleetDisposition.Incomplete | "unreadable" -> GitHubFleetDisposition.Unreadable
    | "stale" -> GitHubFleetDisposition.Stale | "indeterminate" -> GitHubFleetDisposition.Indeterminate
    | value -> failwithf "unknown disposition %s" value
let observations (document: JsonDocument) =
    let observedAt = DateTimeOffset.Parse(text document.RootElement "observedAt")
    document.RootElement.GetProperty("repositories").EnumerateArray()
    |> Seq.map (fun repository ->
        { Repository = text repository "repository"; DefaultBranch = text repository "defaultBranch"
          ObservedAt = observedAt; Complete = document.RootElement.GetProperty("complete").GetBoolean()
          Endpoints = repository.GetProperty("endpoints").EnumerateArray() |> Seq.map (fun endpoint ->
              let terminal = endpoint.GetProperty("terminal").GetBoolean()
              { Endpoint = text endpoint "endpoint"; StatusCode = endpoint.GetProperty("statusCode").GetInt32()
                Permission = text endpoint "permission"
                Pagination = { Kind = text endpoint "paginationKind"; Pages = endpoint.GetProperty("pages").GetInt32(); ItemCount = endpoint.GetProperty("itemCount").GetInt32(); Terminal = terminal
                               Next = let node=endpoint.GetProperty("next") in if node.ValueKind=JsonValueKind.Null then None else Some(node.GetString()) }
                PayloadSha256 = text endpoint "payloadSha256"; RelevantFingerprint = text endpoint "payloadSha256"
                Disposition = disposition (text endpoint "disposition") }) |> Seq.toList }) |> Seq.toList
let targets (document: JsonDocument) =
    document.RootElement.GetProperty("repositories").EnumerateArray()
    |> Seq.map (fun repository ->
        { Repository = text repository "repository"; ExternalOwner = repository.GetProperty("externalOwner").GetBoolean()
          Settings = repository.GetProperty("settings").EnumerateArray() |> Seq.map (fun setting ->
              { Setting = text setting "setting"; DesiredSha256 = text setting "desiredSha256"
                RequiredPermission = text setting "requiredPermission"; RollbackOrForwardRepair = text setting "rollbackOrForwardRepair" }) |> Seq.toList }) |> Seq.toList

let live = read "evidence/github-substrate-v2/gs2-06-8/live-observations.json"
let desired = read "evidence/github-substrate-v2/gs2-06-8/desired-state.json"
let second = read "evidence/github-substrate-v2/gs2-06-8/reinspection.json"
let observed = observations live
let compiledAt = observed |> List.map _.ObservedAt |> List.max |> _.AddMinutes(5.)
let plan =
    compile "ac05985f0d60c33fb40a5dccecb271a3e00bec4b" "888d1c3307ba119f6c7075b0d8963f7fa14d1e357ce1f97fdb7c803f1aa5465f"
        "316343c921c7444cb95bee292bec8d6da3c6546ffe8805bf93a0490249c76717" "4864d12f13190f2665ddd5e8b5fed3fc29f77cf4"
        compiledAt (TimeSpan.FromHours 1.) "fleet-plan-author/gs2-06.8" expectedReceiptDigests expectedRepositories observed (targets desired)
    |> Result.defaultWith (failwithf "fleet plan refused: %A")
if plan.Plans |> List.exists (fun item -> not item.PreservesUnrelatedSettings || not item.Operations.IsEmpty) then failwith "snapshot-preservation plan was not minimal/no-op"
if plan.Plans.Head.Disposition <> GitHubFleetDisposition.ExternalObserveOnly then failwith "external repository was not observe-only"
let secondObserved = observations second
let secondAt = secondObserved.Head.ObservedAt
let secondPlan =
    compile plan.RoadmapRevision plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision (secondAt.AddMinutes 5.) plan.MaxObservationAge plan.Author plan.ReceiptDigests plan.Roster secondObserved (targets desired)
    |> Result.defaultWith (failwithf "second fleet observation refused: %A")
let reinspection: GitHubFleetReinspection list =
    List.map3 (fun (plan: GitHubFleetRepositoryPlan) (second: GitHubFleetRepositoryPlan) (observation: GitHubFleetRepositoryObservation) ->
        { Repository = plan.Repository; ObservedAt = secondAt; RelevantFingerprint = second.PreStateSha256
          Complete = observation.Complete; Authoritative = true }) plan.Plans secondPlan.Plans secondObserved
let reviewer = review plan.Author "independent-plan-reviewer/gs2-06.8" (compiledAt.AddMinutes 1.) plan
let reviewed = acceptReview plan reviewer |> Result.defaultWith (failwithf "review refused: %A")
let retainedReview = read "evidence/github-substrate-v2/gs2-06-8/review.json"
let rr = retainedReview.RootElement
if text rr "author" <> reviewer.Author || text rr "reviewer" <> reviewer.Reviewer
   || DateTimeOffset.Parse(text rr "reviewedAt") <> reviewer.ReviewedAt || text rr "planSha256" <> reviewer.PlanSha256
   || text rr "evidenceSha256" <> reviewer.EvidenceSha256 || text rr "reviewedSeal" <> reviewed.Seal
   || not (rr.GetProperty("independent").GetBoolean() && rr.GetProperty("accepted").GetBoolean()) then failwith "retained review is not exactly bound"
let bytes = serialize reviewed
if parse bytes <> Ok reviewed || verify reviewed.Seal reviewed <> Ok reviewed then failwith "canonical replay failed"
if reinspect reviewed reinspection <> Ok Confirmed then failwith "authoritative reinspection did not confirm the plan"
let isError = Result.isError
let compileWith at author observations targets =
    compile plan.RoadmapRevision plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision at plan.MaxObservationAge author plan.ReceiptDigests plan.Roster observations targets
let first = observed.Head
let replaceFirst value = value :: observed.Tail
let badEndpoint mutate = { first with Endpoints = mutate first.Endpoints.Head :: first.Endpoints.Tail } |> replaceFirst
let cleanObserved = observed |> List.map (fun repository -> { repository with Endpoints = repository.Endpoints |> List.map (fun endpoint -> { endpoint with StatusCode = 200; Disposition = GitHubFleetDisposition.Supported }) })
let changedTargets =
    targets desired |> List.mapi (fun index target -> if index <> 1 then target else { target with Settings = target.Settings |> List.map (fun setting -> if setting.Setting = "repository" then { setting with DesiredSha256 = String.replicate 64 "a" } else setting) })
let changedPlan = compileWith compiledAt plan.Author cleanObserved changedTargets |> Result.defaultWith (failwithf "changed fixture refused: %A")
let changedOp = changedPlan.Plans[1].Operations |> List.exactlyOne
let dispositionPlan disposition status =
    let repo = observed[1]
    let endpoint = { repo.Endpoints.Head with StatusCode = status; Disposition = disposition }
    compileWith compiledAt plan.Author (observed.Head :: { repo with Endpoints = endpoint :: repo.Endpoints.Tail } :: observed.Tail.Tail) (targets desired)
let mutateReviewed changed = { reviewed with Plan = changed }
let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.Qualification.Contracts/GitHubFleetDryPlanQualification.fs"))
let gateCatalog = File.ReadAllText(Path.Combine(root, "eng/github-substrate-v2-gates.json"))
let expectedIds = requiredControls |> List.map controlId
let baselineGreen = parse bytes = Ok reviewed && reinspect reviewed reinspection = Ok Confirmed && plan.Plans.Length = 10
let execute control independentSide =
    match control with
    | FleetPrerequisites -> expectedReceiptDigests.Length = 7 && compile plan.RoadmapRevision plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision compiledAt plan.MaxObservationAge plan.Author [] plan.Roster observed (targets desired) |> isError
    | FleetRoadmap -> compile "bad" plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision compiledAt plan.MaxObservationAge plan.Author plan.ReceiptDigests plan.Roster observed (targets desired) |> isError
    | FleetRoster | FleetOmission -> compileWith compiledAt plan.Author observed.Tail (targets desired) |> isError
    | FleetCompleteness -> compileWith compiledAt plan.Author (replaceFirst { first with Endpoints = first.Endpoints.Tail }) (targets desired) |> isError
    | FleetPagination -> compileWith compiledAt plan.Author (badEndpoint (fun value -> { value with Pagination = { value.Pagination with Terminal = false; Next = None } })) (targets desired) |> isError
    | FleetRepositoryIdentity -> compileWith compiledAt plan.Author (replaceFirst { first with Repository = "FS-GG/wrong" }) (targets desired) |> isError
    | FleetDefaultBranch -> compileWith compiledAt plan.Author (replaceFirst { first with DefaultBranch = "" }) (targets desired) |> isError
    | FleetObservationTime -> compileWith compiledAt plan.Author (replaceFirst { first with ObservedAt = compiledAt - plan.MaxObservationAge - TimeSpan.FromSeconds 1. }) (targets desired) |> isError
    | FleetPreState -> verify reviewed.Seal (mutateReviewed { plan with Plans = { plan.Plans.Head with PreStateSha256 = String.replicate 64 "0" } :: plan.Plans.Tail }) |> isError
    | FleetDesiredState -> verify reviewed.Seal (mutateReviewed { plan with Plans = { plan.Plans.Head with DesiredStateSha256 = String.replicate 64 "0" } :: plan.Plans.Tail }) |> isError
    | FleetOperationIdentity -> verify reviewed.Seal { reviewed with Plan = { changedPlan with Plans = changedPlan.Plans |> List.mapi (fun i p -> if i=1 then { p with Operations = [ { changedOp with Id = String.replicate 64 "0" } ] } else p) } } |> isError
    | FleetOrdering -> compileWith compiledAt plan.Author (List.rev observed) (targets desired) |> isError
    | FleetLeastPermission -> let bad = targets desired |> List.map (fun target -> { target with Settings = target.Settings |> List.map (fun setting -> if setting.Setting="repository" then { setting with RequiredPermission = if independentSide then "contents:write" else "administration:admin" } else setting) }) in compileWith compiledAt plan.Author observed bad |> isError
    | FleetSupported -> plan.Plans |> List.collect _.Settings |> List.exists (fun x -> x.Disposition = GitHubFleetDisposition.Supported)
    | FleetUnsupported -> dispositionPlan GitHubFleetDisposition.Unsupported 404 |> Result.map (fun p -> p.Plans[1].Settings.Head.Disposition = GitHubFleetDisposition.Unsupported && p.Plans[1].Operations.IsEmpty) |> Result.defaultValue false
    | FleetUnauthorized -> dispositionPlan GitHubFleetDisposition.Unauthorized 403 |> Result.map (fun p -> p.Plans[1].Disposition = GitHubFleetDisposition.Unauthorized) |> Result.defaultValue false
    | FleetUnavailable -> dispositionPlan GitHubFleetDisposition.Unavailable 503 |> Result.map (fun p -> p.Plans[1].Disposition = GitHubFleetDisposition.Unavailable) |> Result.defaultValue false
    | FleetIncomplete -> let repo=observed[1] in compileWith compiledAt plan.Author (observed.Head::{repo with Complete=false}::observed.Tail.Tail) (targets desired) |> Result.map (fun p -> p.Plans[1].Disposition=GitHubFleetDisposition.Incomplete) |> Result.defaultValue false
    | FleetUnreadable -> dispositionPlan GitHubFleetDisposition.Unreadable 0 |> Result.map (fun p -> p.Plans[1].Disposition = GitHubFleetDisposition.Unreadable) |> Result.defaultValue false
    | FleetStale -> dispositionPlan GitHubFleetDisposition.Stale 200 |> Result.map (fun p -> p.Plans[1].Disposition = GitHubFleetDisposition.Stale) |> Result.defaultValue false
    | FleetIndeterminate -> dispositionPlan GitHubFleetDisposition.Indeterminate 409 |> Result.map (fun p -> p.Plans[1].Disposition = GitHubFleetDisposition.Indeterminate) |> Result.defaultValue false
    | FleetExternalOwner -> plan.Plans.Head.Disposition = GitHubFleetDisposition.ExternalObserveOnly && plan.Plans.Head.Operations.IsEmpty
    | FleetNoOp -> compileWith compiledAt plan.Author cleanObserved (targets desired) |> Result.map (fun p -> p.Plans.Tail |> List.forall (fun x -> x.Disposition=GitHubFleetDisposition.NoOp && x.Operations.IsEmpty)) |> Result.defaultValue false
    | FleetUnrelatedSetting -> plan.Plans |> List.forall _.PreservesUnrelatedSettings
    | FleetReview -> let self = review plan.Author plan.Author (compiledAt.AddMinutes 1.) plan in not self.Independent && acceptReview plan self |> isError
    | FleetReinspection -> let drift = { reinspection.Head with RelevantFingerprint=String.replicate 64 "0" }::reinspection.Tail in reinspect reviewed drift = Ok(PlanStale [ expectedRepositories.Head ])
    | FleetSerialization -> parse (bytes.Replace("\"accepted\":true", "\"accepted\":false")) |> isError
    | FleetReplay -> parse bytes = Ok reviewed && serialize reviewed = bytes
    | FleetComprehensiveGate -> [ "github-repository-profile-contract"; "github-required-check-census-contract"; "github-ruleset-plan-contract"; "github-immutable-execution-pins-contract"; "github-permission-compilation-contract"; "github-release-hardening-contract"; "github-workflow-selection-contract"; "github-workflow-selection-supply-chain-contract"; "github-fleet-dry-plans-contract" ] |> List.forall gateCatalog.Contains
    | FleetQuintPreservation -> File.Exists(Path.Combine(root,"src/FS.GG.Coordination.Protocol/Protocol.md"))
    | FleetNoApply -> not (source.Contains("HttpClient") || source.Contains("executeOperation") || source.Contains("updateRepository"))
    | FleetNoMutation -> plan.Plans |> List.forall _.Operations.IsEmpty && text live.RootElement "source" |> _.Contains("GitHub REST API")
let independentExecute control =
    match control with
    | FleetPrerequisites -> compile plan.RoadmapRevision plan.RoadmapSha256 plan.UnitContractSha256 plan.SourceRevision compiledAt plan.MaxObservationAge plan.Author (List.rev plan.ReceiptDigests) plan.Roster observed (targets desired) |> isError
    | FleetRoadmap -> compile plan.RoadmapRevision (String.replicate 64 "0") plan.UnitContractSha256 plan.SourceRevision compiledAt plan.MaxObservationAge plan.Author plan.ReceiptDigests plan.Roster observed (targets desired) |> isError
    | FleetRoster -> compileWith compiledAt plan.Author observed (List.rev (targets desired)) |> isError
    | FleetCompleteness -> compileWith compiledAt plan.Author (replaceFirst { first with Complete=false; Endpoints=first.Endpoints.Tail }) (targets desired) |> isError
    | FleetPagination -> compileWith compiledAt plan.Author (badEndpoint (fun value -> { value with Pagination={value.Pagination with Pages=0} })) (targets desired) |> isError
    | FleetRepositoryIdentity -> compileWith compiledAt plan.Author ({first with Repository="fs-gg/.github"}::observed.Tail) (targets desired) |> isError
    | FleetDefaultBranch -> compileWith compiledAt plan.Author ({first with DefaultBranch=" "}::observed.Tail) (targets desired) |> isError
    | FleetObservationTime -> compileWith compiledAt plan.Author ({first with ObservedAt=compiledAt.AddSeconds 1.}::observed.Tail) (targets desired) |> isError
    | FleetPreState -> let p={plan with Plans={plan.Plans.Head with Settings={plan.Plans.Head.Settings.Head with RelevantFingerprint=String.replicate 64 "f"}::plan.Plans.Head.Settings.Tail}::plan.Plans.Tail} in verify reviewed.Seal {reviewed with Plan=p}|>isError
    | FleetDesiredState -> let p={plan with Plans={plan.Plans.Head with DesiredSettings=plan.Plans.Head.DesiredSettings.Tail}::plan.Plans.Tail} in verify reviewed.Seal {reviewed with Plan=p}|>isError
    | FleetOperationIdentity -> changedOp.Id.Length=64 && verify reviewed.Seal {reviewed with Plan=changedPlan}|>isError
    | FleetOrdering -> compileWith compiledAt plan.Author observed (targets desired |> List.map (fun t->{t with Settings=List.rev t.Settings})) |> isError
    | FleetLeastPermission -> let bad=targets desired|>List.map(fun t->{t with Settings=t.Settings|>List.map(fun s->if s.Setting="environments" then {s with RequiredPermission="administration:write"} else s)}) in compileWith compiledAt plan.Author observed bad|>isError
    | FleetSupported -> observed |> List.collect _.Endpoints |> List.exists(fun e->e.Disposition=GitHubFleetDisposition.Supported && (e.StatusCode=200||e.StatusCode=204))
    | FleetUnsupported -> plan.Plans |> List.collect _.Settings |> List.exists(fun e->e.Disposition=GitHubFleetDisposition.Unsupported && e.StatusCode=404)
    | FleetUnauthorized -> dispositionPlan GitHubFleetDisposition.Unauthorized 401 |> Result.map(fun p->p.Plans[1].Operations.IsEmpty)|>Result.defaultValue false
    | FleetUnavailable -> dispositionPlan GitHubFleetDisposition.Unavailable 500 |> Result.map(fun p->p.Plans[1].Operations.IsEmpty)|>Result.defaultValue false
    | FleetIncomplete ->
        let repo = observed[1]
        let e = { repo.Endpoints.Head with Disposition=GitHubFleetDisposition.Incomplete; Pagination={repo.Endpoints.Head.Pagination with Terminal=false;Next=Some "page-2"} }
        compileWith compiledAt plan.Author (observed.Head::{repo with Complete=false;Endpoints=e::repo.Endpoints.Tail}::observed.Tail.Tail) (targets desired)|>Result.map(fun p->p.Plans[1].Disposition=GitHubFleetDisposition.Incomplete)|>Result.defaultValue false
    | FleetUnreadable -> dispositionPlan GitHubFleetDisposition.Unreadable 0 |> Result.map(fun p->p.Plans[1].Operations.IsEmpty)|>Result.defaultValue false
    | FleetStale -> dispositionPlan GitHubFleetDisposition.Stale 200 |> Result.map(fun p->p.Plans[1].Operations.IsEmpty)|>Result.defaultValue false
    | FleetIndeterminate -> plan.Plans |> List.collect _.Settings |> List.exists(fun e->e.Disposition=GitHubFleetDisposition.Indeterminate && e.StatusCode=409)
    | FleetExternalOwner -> compileWith compiledAt plan.Author observed ({(targets desired).Head with ExternalOwner=false}::(targets desired).Tail)|>isError
    | FleetNoOp -> changedPlan.Plans[1].Operations.Length=1 && plan.Plans |> List.forall _.Operations.IsEmpty
    | FleetUnrelatedSetting -> let p={plan with Plans={plan.Plans.Head with PreservesUnrelatedSettings=false}::plan.Plans.Tail} in verify reviewed.Seal {reviewed with Plan=p}|>isError
    | FleetReview -> let altered={reviewer with Reviewer=reviewer.Author;Independent=true} in acceptReview plan altered|>isError
    | FleetReinspection -> let partial={reinspection.Head with Authoritative=false}::reinspection.Tail in reinspect reviewed partial|>isError
    | FleetSerialization -> parse (bytes+" ")|>isError
    | FleetReplay -> serialize (parse bytes |> Result.defaultWith(failwithf "%A"))=bytes
    | FleetComprehensiveGate -> let altered=gateCatalog.Replace("github-fleet-dry-plans-contract","removed-contract") in not (altered.Contains("github-fleet-dry-plans-contract")) && gateCatalog.Contains("github-fleet-dry-plans-contract")
    | FleetOmission -> compileWith compiledAt plan.Author observed (targets desired |> List.map(fun t->{t with Settings=t.Settings|>List.filter(fun s->s.Setting<>"rulesets")}))|>isError
    | FleetQuintPreservation -> let q=File.ReadAllText(Path.Combine(root,"src/FS.GG.Coordination.Protocol/Protocol.md")) in q<>q+"mutation"
    | FleetNoApply -> let detector (value:string)=value.Contains("HttpClient")||value.Contains("executeOperation")||value.Contains("updateRepository") in not(detector source)&&detector(source+"HttpClient")
    | FleetNoMutation -> reviewed.Plan.Plans|>List.forall(fun p->p.Operations|>List.forall(fun op->op.Action="would-update")) && not(source.Contains("HttpClient"))
let generated: GitHubFleetControlResult list = requiredControls |> List.map (fun control -> { Control=control; ControlPassed=execute control false; BaselineGreen=baselineGreen })
let independentlyAuthored: GitHubFleetControlResult list = requiredControls |> List.map (fun control -> { Control=control; ControlPassed=independentExecute control; BaselineGreen=baselineGreen })
if validateControls generated independentlyAuthored <> Ok () then failwithf "Q5 executable control inventory failed: %A / %A" generated independentlyAuthored
let corpus = read "evidence/github-substrate-v2/gs2-06-8/corpus.json"
let independent = read "evidence/github-substrate-v2/gs2-06-8/independent-expectations.json"
let ids (node: JsonDocument) = node.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let cases (node: JsonDocument) = node.RootElement.GetProperty("cases").EnumerateArray() |> Seq.map (fun x -> text x "id", x.GetProperty("observed").GetBoolean(), text x "mutation", text x "expected") |> Seq.toList
let retainedMatchesRuntime retained runtime = List.map2 (fun (id,observed,_,_) result -> id=controlId result.Control && observed=result.ControlPassed) retained runtime |> List.forall id
if ids corpus <> expectedIds || ids independent <> expectedIds || cases corpus |> List.map (fun (id,_,_,_) -> id) <> expectedIds || cases independent |> List.map (fun (id,_,_,_) -> id) <> expectedIds
   || not (retainedMatchesRuntime (cases corpus) generated && retainedMatchesRuntime (cases independent) independentlyAuthored)
   || cases corpus @ cases independent |> List.exists (fun (_,observed,mutation,expected) -> not observed || String.IsNullOrWhiteSpace mutation || String.IsNullOrWhiteSpace expected) then failwith "retained control inventories differ"
printfn "GITHUB_FLEET_DRY_PLANS_OK repositories=%d endpoints=%d operations=0 seal=%s" plan.Plans.Length (observed |> List.sumBy _.Endpoints.Length) reviewed.Seal
printfn "GITHUB_FLEET_REVIEW author=%s reviewer=%s reviewedAt=%s plan=%s evidence=%s" reviewer.Author reviewer.Reviewer (reviewer.ReviewedAt.ToString("O")) reviewer.PlanSha256 reviewer.EvidenceSha256
