module FS.GG.Coordination.GitHubFleetDryPlanTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubFleetDryPlanQualification

let private sha (value: string) =
    value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let private get = function Ok value -> value | Error findings -> failwithf "unexpected findings: %A" findings

let private instant = DateTimeOffset.Parse("2026-09-03T17:20:00Z")
let private page = { Kind = "terminal-page"; Pages = 1; ItemCount = 1; Terminal = true; Next = None }

let private endpoint repository name disposition status =
    { Endpoint = name; StatusCode = status; Permission = "metadata:read"
      Pagination = page; PayloadSha256 = sha $"{repository}:{name}:payload"
      RelevantFingerprint = sha $"{repository}:{name}:state"; Disposition = disposition }

let private observations () =
    expectedRepositories
    |> List.map (fun repository ->
        { Repository = repository; DefaultBranch = "main"; ObservedAt = instant; Complete = true
          Endpoints = expectedEndpoints |> List.map (fun name -> endpoint repository name GitHubFleetDisposition.Supported 200) })

let private targets () =
    expectedRepositories
    |> List.map (fun repository ->
        { Repository = repository; ExternalOwner = repository = "EHotwagner/S.I.R."
          Settings =
            [ { Setting = "repository"; DesiredSha256 = sha $"{repository}:desired"
                RequiredPermission = "administration:write"
                RollbackOrForwardRepair = "restore captured pre-state or recompile from authoritative state" } ] })

let private compileFixture observations targets =
    compile "ac05985f0d60c33fb40a5dccecb271a3e00bec4b"
        "888d1c3307ba119f6c7075b0d8963f7fa14d1e357ce1f97fdb7c803f1aa5465f"
        "316343c921c7444cb95bee292bec8d6da3c6546ffe8805bf93a0490249c76717"
        "4864d12f13190f2665ddd5e8b5fed3fc29f77cf4" expectedReceiptDigests expectedRepositories observations targets

[<Fact>]
let ``fleet dry plan is deterministic canonical and lossless`` () =
    let first = compileFixture (observations ()) (targets ()) |> get
    let second = compileFixture (observations ()) (targets ()) |> get
    let bytes = serialize first
    Assert.Equal(first, second)
    Assert.Equal(bytes, serialize second)
    Assert.Equal(first, parse bytes |> get)
    Assert.Equal(first, verify first.Seal first |> get)
    Assert.Equal(10, first.Plans.Length)
    Assert.Equal(GitHubFleetDisposition.ExternalObserveOnly, first.Plans.Head.Disposition)
    Assert.All(first.Plans, fun plan -> Assert.True(plan.PreservesUnrelatedSettings))
    Assert.All(first.Plans.Tail, fun plan -> Assert.Equal(GitHubFleetDisposition.Supported, plan.Disposition); Assert.Single(plan.Operations) |> ignore)

[<Fact>]
let ``authority roster and omission fail closed`` () =
    let observed = observations ()
    let target = targets ()
    match compile "bad" "bad" "bad" "bad" [] [] observed target with
    | Error findings -> Assert.Contains(GitHubFleetDryPlanFinding.InvalidFleetAuthority "roadmap", findings); Assert.Contains(GitHubFleetDryPlanFinding.InvalidFleetRoster, findings)
    | Ok _ -> failwith "invalid authority survived"
    match compileFixture (List.tail observed) target with
    | Error findings -> Assert.Contains(GitHubFleetDryPlanFinding.InvalidFleetRoster, findings)
    | Ok _ -> failwith "omitted repository survived"

[<Fact>]
let ``pagination identity and unsupported permissions stay explicit`` () =
    let observed = observations ()
    let first = observed.Head
    let unauthorized = { first.Endpoints.Head with StatusCode = 403; Disposition = GitHubFleetDisposition.Unauthorized }
    let unsupported = { first.Endpoints[1] with StatusCode = 404; Disposition = GitHubFleetDisposition.Unsupported }
    let changed = { first with Endpoints = unauthorized :: unsupported :: first.Endpoints.Tail.Tail }
    let plan = compileFixture (changed :: observed.Tail) (targets ()) |> get
    Assert.Equal(GitHubFleetDisposition.ExternalObserveOnly, plan.Plans.Head.Disposition)
    let malformed = { unauthorized with Pagination = { page with Terminal = false; Next = None } }
    match compileFixture ({ first with Endpoints = malformed :: first.Endpoints.Tail } :: observed.Tail) (targets ()) with
    | Error findings -> Assert.Contains(GitHubFleetDryPlanFinding.InvalidFleetPagination $"{first.Repository}:{malformed.Endpoint}", findings)
    | Ok _ -> failwith "non-terminal pagination survived"

[<Fact>]
let ``reinspection is independent and relevant drift stales`` () =
    let plan = compileFixture (observations ()) (targets ()) |> get
    let bytes = serialize plan
    let decision = review "independent-reviewer" (instant.AddMinutes 1.) bytes
    let same =
        plan.Plans |> List.map (fun value ->
            { Repository = value.Repository; ObservedAt = instant.AddMinutes 2.
              RelevantFingerprint = value.PreStateSha256; Complete = true; Authoritative = true })
    Assert.Equal(Confirmed, reinspect plan decision same |> get)
    let drifted = { same.Head with RelevantFingerprint = sha "relevant-drift" } :: same.Tail
    match reinspect plan decision drifted |> get with
    | PlanStale names -> Assert.Equal([ expectedRepositories.Head ], names)
    | Confirmed -> failwith "relevant drift survived"
    match reinspect plan { decision with Independent = false } same with
    | Error findings -> Assert.Contains(GitHubFleetDryPlanFinding.InvalidFleetReview, findings)
    | Ok _ -> failwith "self review survived"

[<Fact>]
let ``all explicit dispositions compile without invented operations`` () =
    let variants =
        [ GitHubFleetDisposition.Unsupported, 404; GitHubFleetDisposition.Unauthorized, 403
          GitHubFleetDisposition.Unavailable, 503; GitHubFleetDisposition.Unreadable, 0
          GitHubFleetDisposition.Stale, 200; GitHubFleetDisposition.Indeterminate, 200
          GitHubFleetDisposition.NoOp, 200 ]
    for disposition, status in variants do
        let observed = observations ()
        let repository = observed[1]
        let changedEndpoints =
            repository.Endpoints
            |> List.map (fun endpoint ->
                if endpoint.Endpoint = "repository" then
                    let fingerprint = if disposition = GitHubFleetDisposition.NoOp then sha $"{repository.Repository}:desired" else endpoint.RelevantFingerprint
                    { endpoint with StatusCode = status; Disposition = disposition; RelevantFingerprint = fingerprint }
                else endpoint)
        let changed = { repository with Endpoints = changedEndpoints }
        let plan = compileFixture (observed.Head :: changed :: observed.Tail.Tail) (targets ()) |> get
        Assert.Equal(disposition, plan.Plans[1].Disposition)
        Assert.Empty(plan.Plans[1].Operations)
    let observed = observations ()
    let repository = observed[1]
    let incompleteEndpoint =
        { repository.Endpoints.Head with
            StatusCode = 200
            Disposition = GitHubFleetDisposition.Incomplete
            Pagination = { page with Terminal = false; Next = Some "page-2" } }
    let changed = { repository with Complete = false; Endpoints = incompleteEndpoint :: repository.Endpoints.Tail }
    let plan = compileFixture (observed.Head :: changed :: observed.Tail.Tail) (targets ()) |> get
    Assert.Equal(GitHubFleetDisposition.Incomplete, plan.Plans[1].Disposition)
    Assert.Empty(plan.Plans[1].Operations)

[<Fact>]
let ``generated and independent Q5 inventories are exact`` () =
    let side: GitHubFleetControlResult list = requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), validateControls side side)
    match validateControls side.Tail side with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "Q5-INVENTORY")
    | Ok () -> failwith "omitted Q5 control survived"

[<Fact>]
let ``serialization refuses extra fields and altered seals`` () =
    let plan = compileFixture (observations ()) (targets ()) |> get
    let bytes = serialize plan
    let altered = bytes.Replace($"\"seal\":\"{plan.Seal}\"", $"\"extra\":true,\"seal\":\"{plan.Seal}\"")
    Assert.True(Result.isError (parse altered))
    Assert.True(Result.isError (verify (sha "wrong") plan))
