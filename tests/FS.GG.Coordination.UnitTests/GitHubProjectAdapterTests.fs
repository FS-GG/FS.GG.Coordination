module FS.GG.Coordination.GitHubProjectAdapterTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private liveId value = LiveId.tryCreate value |> Result.defaultWith failwith
let private semantic value = SemanticName.tryCreate value |> Result.defaultWith failwith
let private repository owner name = { Owner = owner; Name = name }
let private issue project item repo number content archived =
    { ProjectId = liveId project; ItemId = liveId item; Content = RepositoryIssue(repo, number, liveId content); Archived = archived }
let private draft project item content =
    { ProjectId = liveId project; ItemId = liveId item; Content = DraftIssue(liveId content); Archived = false }
let private page number terminal items = { Number = number; TerminalPage = terminal; Items = items }
let private complete revision pages = ProjectComplete(revision, pages)
let private snapshot observation = ProjectAdapter.readProject observation |> Result.defaultWith (failwithf "%A")
let private option id name: StatusOptionProjection = { Id = liveId id; Name = semantic name }
let private statusField project item selected options =
    { ProjectId = liveId project; ItemId = liveId item; FieldId = liveId "FIELD_STATUS"; FieldName = semantic "Status"; Options = options; SelectedOptionId = selected |> Option.map liveId }
let private status revision field = StatusComplete(revision, { PageCount = 1; NodeCount = 1; TerminalPage = true }, [ field ])

[<Fact>]
let ``Project reads require terminal pagination and reject duplicate item and content identities`` () =
    let repo = repository "FS-GG" "Repo"
    let first = issue "P" "I2" repo 2 "C2" false
    let second = issue "P" "I1" repo 1 "C1" false
    match ProjectAdapter.readProject (complete "rev" [ page 1 false [ first ]; page 2 true [ second ] ]) with
    | Ok observed ->
        Assert.Equal(2, observed.PageCount)
        Assert.Equal(2, observed.NodeCount)
        Assert.Equal<ProjectItem list>([ second; first ], observed.Items)
    | Error error -> failwithf "%A" error
    Assert.Equal(Error InvalidProjectPageChain, ProjectAdapter.readProject (complete "rev" [ page 2 true [ first ] ]))
    Assert.Equal(Error(DuplicateProjectItemId first.ItemId), ProjectAdapter.readProject (complete "rev" [ page 1 true [ first; first ] ]))
    let duplicateContent = { first with ItemId = liveId "I3" }
    Assert.Equal(Error(DuplicateProjectContent(liveId "C2")), ProjectAdapter.readProject (complete "rev" [ page 1 true [ first; duplicateContent ] ]))

[<Fact>]
let ``membership preserves archived external draft redacted unknown missing and unreadable outcomes`` () =
    let expected = repository "FS-GG" "Repo"
    let external = issue "P" "I1" (repository "Other" "Repo") 1 "EXT" false
    let archived = issue "P" "I2" expected 2 "ARC" true
    let draftItem = draft "P" "I3" "DRAFT"
    let redacted = { ProjectId = liveId "P"; ItemId = liveId "I4"; Content = RedactedContent(liveId "RED"); Archived = false }
    let unknown = { ProjectId = liveId "P"; ItemId = liveId "I5"; Content = UnknownContent("future", liveId "UNK"); Archived = false }
    let observed = snapshot (complete "rev" [ page 1 true [ external; archived; draftItem; redacted; unknown ] ])
    Assert.Equal(Ok(ExternalRepositoryMembership external), ProjectAdapter.resolveMembership expected (liveId "EXT") observed)
    Assert.Equal(Ok(ArchivedMembership archived), ProjectAdapter.resolveMembership expected (liveId "ARC") observed)
    Assert.Equal(Ok(DraftMembership draftItem), ProjectAdapter.resolveMembership expected (liveId "DRAFT") observed)
    Assert.Equal(Ok(RedactedMembership redacted), ProjectAdapter.resolveMembership expected (liveId "RED") observed)
    Assert.Equal(Ok(UnknownMembership unknown), ProjectAdapter.resolveMembership expected (liveId "UNK") observed)
    Assert.Equal(Ok MissingMembership, ProjectAdapter.resolveMembership expected (liveId "MISSING") observed)
    Assert.Equal(Error(ProjectObservationUnreadable "timeout"), ProjectAdapter.readProject (ProjectUnreadable "timeout"))

[<Fact>]
let ``Status reads are explicitly projection-only complete and duplicate-safe`` () =
    let ready = option "OPT_READY" "Ready"
    let backlog = option "OPT_BACKLOG" "Backlog"
    let field = statusField "P" "I" (Some "OPT_READY") [ ready; backlog ]
    match ProjectAdapter.readStatus (liveId "P") (liveId "I") (status "rev" field) with
    | Ok observed ->
        Assert.Equal(ProjectionOnly, observed.Nature)
        Assert.Equal(Some(liveId "OPT_READY"), observed.SelectedOptionId)
        Assert.Equal<StatusOptionProjection list>([ backlog; ready ], observed.Options)
    | Error error -> failwithf "%A" error
    let duplicate = { field with Options = [ ready; ready ] }
    Assert.Equal(Error(DuplicateStatusOptionId ready.Id), ProjectAdapter.readStatus (liveId "P") (liveId "I") (status "rev" duplicate))
    Assert.Equal(Error(StatusObservationUnreadable "denied-read"), ProjectAdapter.readStatus (liveId "P") (liveId "I") (StatusUnreadable "denied-read"))

[<Fact>]
let ``membership planning is deterministic idempotent typed for no-ops and blocks archived items`` () =
    let repo = repository "FS-GG" "Repo"
    let existing = issue "P" "I1" repo 1 "C1" false
    let before = snapshot (complete "rev" [ page 1 true [ existing ] ])
    let add = ProjectAdapter.planMembership "rev" "cause" repo (EnsureMember(liveId "P", liveId "C2")) before
    Assert.Equal(add, ProjectAdapter.planMembership "rev" "cause" repo (EnsureMember(liveId "P", liveId "C2")) before)
    match add with
    | Ok(MembershipPlanned plan) -> Assert.Matches("^[0-9a-f]{64}$", plan.IdempotencyIdentity)
    | value -> failwithf "%A" value
    match ProjectAdapter.planMembership "rev" "cause" repo (EnsureMember(liveId "P", liveId "C1")) before with
    | Ok(MembershipNoOp _) -> ()
    | value -> failwithf "%A" value
    let archived = { existing with Archived = true }
    let archivedBefore = snapshot (complete "rev" [ page 1 true [ archived ] ])
    match ProjectAdapter.planMembership "rev" "cause" repo (EnsureMember(liveId "P", liveId "C1")) archivedBefore with
    | Error(MembershipMutationIneligible(ArchivedMembership _)) -> ()
    | value -> failwithf "%A" value

[<Fact>]
let ``Status planning validates options and produces mutation-free no-ops`` () =
    let ready = option "READY" "Ready"
    let backlog = option "BACKLOG" "Backlog"
    let before = ProjectAdapter.readStatus (liveId "P") (liveId "I") (status "rev" (statusField "P" "I" (Some "READY") [ ready; backlog ])) |> Result.defaultWith (failwithf "%A")
    match ProjectAdapter.planStatus "rev" "cause" (SetStatus ready.Id) before with
    | Ok(StatusNoOp receipt) -> Assert.Matches("^[0-9a-f]{64}$", receipt.IdempotencyIdentity)
    | value -> failwithf "%A" value
    Assert.Equal(Error(RequestedStatusOptionMissing(liveId "UNKNOWN")), ProjectAdapter.planStatus "rev" "cause" (SetStatus(liveId "UNKNOWN")) before)
    match ProjectAdapter.planStatus "rev" "cause" (SetStatus backlog.Id) before with
    | Ok(StatusPlanned _) -> ()
    | value -> failwithf "%A" value

[<Fact>]
let ``Status reread and post-state verification require exact projection delta and advanced revision`` () =
    let ready = option "READY" "Ready"
    let backlog = option "BACKLOG" "Backlog"
    let beforeObservation = status "rev-1" (statusField "P" "I" (Some "READY") [ ready; backlog ])
    let before = ProjectAdapter.readStatus (liveId "P") (liveId "I") beforeObservation |> Result.defaultWith (failwithf "%A")
    let plan = match ProjectAdapter.planStatus "rev-1" "cause" (SetStatus backlog.Id) before with Ok(StatusPlanned value) -> value | value -> failwithf "%A" value
    Assert.True(ProjectAdapter.checkStatusPreState plan beforeObservation |> Result.isOk)
    Assert.Equal(Error(StatusReReadRequired("rev-1", "rev-2")), ProjectAdapter.checkStatusPreState plan (status "rev-2" (statusField "P" "I" (Some "READY") [ ready; backlog ])))
    Assert.Equal(Error ConcurrentStatusChange, ProjectAdapter.checkStatusPreState plan (status "rev-1" (statusField "P" "I" None [ ready; backlog ])))
    Assert.Equal(Error(StatusResultRevisionDidNotAdvance "rev-1"), ProjectAdapter.verifyStatusPostState "rev-1" plan beforeObservation)
    Assert.True(ProjectAdapter.verifyStatusPostState "rev-2" plan (status "rev-2" (statusField "P" "I" (Some "BACKLOG") [ ready; backlog ])) |> Result.isOk)
    Assert.Equal(Error StatusPostStateMismatch, ProjectAdapter.verifyStatusPostState "rev-2" plan (status "rev-2" (statusField "P" "I" None [ ready; backlog ])))

[<Fact>]
let ``mandatory rereads and exact post-state checks reject stale and concurrent changes`` () =
    let repo = repository "FS-GG" "Repo"
    let existing = issue "P" "I1" repo 1 "C1" false
    let before = snapshot (complete "rev-1" [ page 1 true [ existing ] ])
    let plan = match ProjectAdapter.planMembership "rev-1" "cause" repo (EnsureMember(liveId "P", liveId "C2")) before with Ok(MembershipPlanned value) -> value | value -> failwithf "%A" value
    Assert.Equal(Error(MembershipReReadRequired("rev-1", "rev-2")), ProjectAdapter.checkMembershipPreState plan (complete "rev-2" [ page 1 true [ existing ] ]))
    let concurrent = issue "P" "IX" repo 9 "CX" false
    Assert.Equal(Error ConcurrentMembershipChange, ProjectAdapter.checkMembershipPreState plan (complete "rev-1" [ page 1 true [ existing; concurrent ] ]))
    let result = issue "P" "I2" repo 2 "C2" false
    Assert.True(ProjectAdapter.verifyMembershipPostState "rev-2" (Some result) plan (complete "rev-2" [ page 1 true [ existing; result ] ]) |> Result.isOk)
    Assert.Equal(Error MembershipPostStateMismatch, ProjectAdapter.verifyMembershipPostState "rev-2" (Some result) plan (complete "rev-2" [ page 1 true [ result ] ]))

[<Fact>]
let ``Project qualification inventory is exact and every mutation must turn red`` () =
    let passing: GitHubProjectAdapterControlResult list =
        GitHubProjectAdapterQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubProjectAdapterQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index result -> if index = 3 then { result with MutationRed = false } else result)
    match GitHubProjectAdapterQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GPAQ-INDEPENDENT-NOT-RED" && finding.ControlId = "external-item")
    | Ok () -> failwith "accepted a mutation that stayed green"
