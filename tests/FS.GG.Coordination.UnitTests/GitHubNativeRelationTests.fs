module FS.GG.Coordination.GitHubNativeRelationTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private liveId value = LiveId.tryCreate value |> Result.defaultWith failwith
let private edge kind source target = { Kind = kind; Source = liveId source; Target = liveId target }
let private complete revision scope pages = RelationsComplete(revision, scope, pages)
let private page number terminal edges = { Number = number; TerminalPage = terminal; Edges = edges }

[<Fact>]
let ``complete reads require a contiguous terminal page chain and sort all edges`` () =
    let first = edge ParentChild "P2" "C2"
    let second = edge ParentChild "P1" "C1"
    let observed = complete "rev-1" ParentChild [ page 1 false [ first ]; page 2 true [ second ] ]
    match NativeRelations.read observed with
    | Ok snapshot ->
        Assert.Equal(2, snapshot.PageCount)
        Assert.Equal(2, snapshot.NodeCount)
        Assert.Equal<RelationEdge list>([ second; first ], snapshot.Edges)
    | Error error -> failwithf "%A" error
    Assert.Equal(Error InvalidRelationPageChain, NativeRelations.read (complete "rev-1" ParentChild [ page 2 true [ first ] ]))
    Assert.Equal(Error InvalidRelationPageChain, NativeRelations.read (complete "rev-1" ParentChild [ page 1 false [ first ] ]))

[<Fact>]
let ``reads preserve refusals and reject duplicate self and wrong-kind edges`` () =
    let relation = edge Blocks "B1" "B2"
    Assert.Equal(Error(RelationObservationRefused(ObservationUnauthorized "denied")), NativeRelations.read (RelationsUnauthorized "denied"))
    Assert.Equal(Error(DuplicateRelationEdge relation), NativeRelations.read (complete "rev" Blocks [ page 1 true [ relation; relation ] ]))
    let self = edge Blocks "B1" "B1"
    Assert.Equal(Error(InvalidRelationEdge self), NativeRelations.read (complete "rev" Blocks [ page 1 true [ self ] ]))
    Assert.Equal(Error(RelationKindMismatch(ParentChild, Blocks)), NativeRelations.read (complete "rev" ParentChild [ page 1 true [ relation ] ]))

[<Fact>]
let ``planning is edge-local deterministic collision-safe and typed for no-ops`` () =
    let existing = edge Blocks "B1" "B2"
    let unrelated = edge Blocks "B8" "B9"
    let snapshot = NativeRelations.read (complete "rev" Blocks [ page 1 true [ unrelated; existing ] ]) |> Result.defaultWith (failwithf "%A")
    let planned = NativeRelations.plan "rev" "cause" (RemoveEdge existing) snapshot
    Assert.Equal(planned, NativeRelations.plan "rev" "cause" (RemoveEdge existing) snapshot)
    match planned with
    | Ok(RelationPlanned value) ->
        Assert.Equal<RelationEdge list>([ existing; unrelated ], value.Before.Edges)
        Assert.Matches("^[0-9a-f]{64}$", value.IdempotencyIdentity)
    | value -> failwithf "%A" value
    match NativeRelations.plan "rev" "cause" (AddEdge existing) snapshot with
    | Ok(RelationNoOp receipt) -> Assert.Equal("rev", receipt.ObservedRevision)
    | value -> failwithf "%A" value
    let collisionA = edge Blocks "A|B" "C"
    let collisionB = edge Blocks "A" "B|C"
    let empty = NativeRelations.read (complete "rev" Blocks [ page 1 true [] ]) |> Result.defaultWith (failwithf "%A")
    let key intent = match NativeRelations.plan "rev" "cause" intent empty with Ok(RelationPlanned value) -> value.IdempotencyIdentity | value -> failwithf "%A" value
    Assert.NotEqual<string>(key (AddEdge collisionA), key (AddEdge collisionB))

[<Fact>]
let ``stale reread and concurrent pre-state changes refuse before execution`` () =
    let target = edge ParentChild "P" "C"
    let before = NativeRelations.read (complete "rev-1" ParentChild [ page 1 true [] ]) |> Result.defaultWith (failwithf "%A")
    let plan = match NativeRelations.plan "rev-1" "cause" (AddEdge target) before with Ok(RelationPlanned value) -> value | value -> failwithf "%A" value
    Assert.Equal(Error(ReReadRequired("rev-1", "rev-2")), NativeRelations.checkPreState plan (complete "rev-2" ParentChild [ page 1 true [] ]))
    match NativeRelations.checkPreState plan (complete "rev-1" ParentChild [ page 1 true [ edge ParentChild "P2" "C2" ] ]) with
    | Error(ConcurrentPreStateChange([], [ _ ])) -> ()
    | value -> failwithf "%A" value

[<Fact>]
let ``post-state verification requires exact delta revision and unrelated-edge preservation`` () =
    let target = edge Blocks "B1" "B2"
    let unrelated = edge Blocks "B8" "B9"
    let before = NativeRelations.read (complete "rev-1" Blocks [ page 1 true [ unrelated ] ]) |> Result.defaultWith (failwithf "%A")
    let plan = match NativeRelations.plan "rev-1" "cause" (AddEdge target) before with Ok(RelationPlanned value) -> value | value -> failwithf "%A" value
    Assert.Equal(Error(ResultRevisionDidNotAdvance "rev-1"), NativeRelations.verifyPostState "rev-1" plan (complete "rev-1" Blocks [ page 1 true [ target; unrelated ] ]))
    Assert.True(NativeRelations.verifyPostState "rev-2" plan (complete "rev-2" Blocks [ page 1 true [ target; unrelated ] ]) |> Result.isOk)
    Assert.Equal(Error(ResultRevisionMismatch("rev-2", "rev-3")), NativeRelations.verifyPostState "rev-2" plan (complete "rev-3" Blocks [ page 1 true [ target; unrelated ] ]))
    match NativeRelations.verifyPostState "rev-2" plan (complete "rev-2" Blocks [ page 1 true [ target ] ]) with
    | Error(PostStateMismatch(_, _)) -> ()
    | value -> failwithf "%A" value

[<Fact>]
let ``native relation qualification inventory is exact and every mutation must turn red`` () =
    let passing: GitHubNativeRelationControlResult list =
        GitHubNativeRelationQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubNativeRelationQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index result -> if index = 2 then { result with MutationRed = false } else result)
    match GitHubNativeRelationQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GNRQ-INDEPENDENT-NOT-RED" && finding.ControlId = "reversed-endpoint")
    | Ok () -> failwith "accepted a mutation that stayed green"
