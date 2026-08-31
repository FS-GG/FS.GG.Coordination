module FS.GG.Coordination.GitHubShardedJournalTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private digest c = String.replicate 64 c
let private oid c = String.replicate 40 c
let private address kind id = ShardedJournalAdapter.address kind id |> Result.defaultWith (failwithf "%A")
let private head (address: AggregateAddress) generation terminal prior: JournalHead =
    { SchemaVersion = 1
      Address = address
      Generation = generation
      EventDigest = digest "e"
      SnapshotDigest = (if terminal then Some(digest "s") else None)
      Terminal = terminal
      PriorHeadDigest = prior
      HeadDigest = digest (string generation) }
let private commit address generation terminal parent prior operation commitOid: JournalCommit =
    { CommitOid = commitOid; ParentOid = parent; TreeOid = oid "b"; OperationId = operation; Head = head address generation terminal prior }

[<Fact>]
let ``aggregate addresses are lowercase length-prefixed sharded and protected`` () =
    let actual = address Claim "Issue:FS-GG/Repo#42"
    Assert.Equal("issue:fs-gg/repo#42", actual.CanonicalId)
    Assert.Matches("^[0-9a-f]{64}$", actual.Digest)
    Assert.Equal(actual.Digest.Substring(0, 2), actual.Shard)
    Assert.Equal($"refs/heads/fsgg/v2/journal/claim/{actual.Shard}", actual.Ref)
    Assert.Equal(Error InvalidAggregateId, ShardedJournalAdapter.address Claim " ")

[<Fact>]
let ``canonical json recursively sorts keys and terminates with LF`` () =
    let actual = ShardedJournalAdapter.canonicalJson "{\"z\":2,\"a\":{\"y\":1,\"b\":0}}" |> Result.defaultWith failwith
    Assert.True(System.Text.Encoding.UTF8.GetBytes("{\"a\":{\"b\":0,\"y\":1},\"z\":2}\n") = actual)

[<Fact>]
let ``journal validation requires one-parent monotonic append-only ancestry`` () =
    let location = address Review "review-chain:42"
    let root = commit location 1L false None None "root" (oid "a")
    let next = commit location 2L false (Some root.CommitOid) (Some root.Head.HeadDigest) "next" (oid "c")
    let observation = JournalComplete("rev-2", [ root; next ])
    let snapshot = ShardedJournalAdapter.validate location observation |> Result.defaultWith (failwithf "%A")
    Assert.Equal(next, snapshot.Current)
    Assert.Equal(Error MissingJournalParent, ShardedJournalAdapter.validate location (JournalComplete("bad", [ root; { next with ParentOid = Some(oid "d") } ])))
    Assert.Equal(Error(DuplicateJournalGeneration 1L), ShardedJournalAdapter.validate location (JournalComplete("bad", [ root; { next with Head = { next.Head with Generation = 1L } } ])))
    Assert.Equal(Error TerminalJournalAppend, ShardedJournalAdapter.validate location (JournalComplete("bad", [ { root with Head = { root.Head with Terminal = true } }; next ])))

[<Fact>]
let ``CAS uses exact old oid and ambiguous results require exact authoritative reread`` () =
    let location = address Operation "operation:42"
    let root = commit location 1L false None None "root" (oid "a")
    let before = ShardedJournalAdapter.validate location (JournalComplete("rev-1", [ root ])) |> Result.defaultWith (failwithf "%A")
    let next = commit location 2L false (Some root.CommitOid) (Some root.Head.HeadDigest) "op-42" (oid "c")
    let proposal = ShardedJournalAdapter.planCas "op-42" before next |> Result.defaultWith (failwithf "%A")
    Assert.Equal($"--force-with-lease={location.Ref}:{root.CommitOid}", proposal.ForceWithLease)
    Assert.Equal(Accepted, ShardedJournalAdapter.reconcile proposal ReceiveResponseUnknown (JournalComplete("rev-2", [ root; next ])))
    Assert.Equal(ResponseUnknownRequiresReread, ShardedJournalAdapter.reconcile proposal ReceiveResponseUnknown (JournalComplete("rev-1", [ root ])))
    Assert.Equal(ParentConflict, ShardedJournalAdapter.reconcile proposal ReceiveParentConflict (JournalComplete("rev-1", [ root ])))

[<Fact>]
let ``effect fencing fails closed on stale terminal and incomplete authority`` () =
    let location = address Cutover "cutover:42"
    let root = commit location 1L false None None "root" (oid "a")
    let observation = JournalComplete("rev", [ root ])
    let grant = { Address = location; JournalCommit = root.CommitOid; Generation = 1L }
    Assert.True(ShardedJournalAdapter.authorizeEffect grant observation |> Result.isOk)
    Assert.Equal(Error EffectRefusal.StaleFence, ShardedJournalAdapter.authorizeEffect { grant with Generation = 2L } observation)
    Assert.Equal(Error(EffectAuthorityUnavailable(IncompleteJournal "partial")), ShardedJournalAdapter.authorizeEffect grant (JournalIncomplete "partial"))

[<Fact>]
let ``saga acquisition is globally sorted and compensation is reverse append-only`` () =
    let first = { Address = address Review "z"; ExpectedGeneration = 3L }
    let second = { Address = address Claim "a"; ExpectedGeneration = 2L }
    let plan = ShardedJournalAdapter.planSaga "saga-1" [ first; second ] |> Result.defaultWith failwith
    let expected: SagaTouch list = List.sortBy (fun (touch: SagaTouch) -> ShardedJournalAdapter.journalKind touch.Address.Kind, touch.Address.Shard, touch.Address.Digest) [ first; second ]
    Assert.Equal<SagaTouch list>(expected, plan.AcquisitionOrder)
    Assert.Equal<SagaTouch list>(plan.AcquisitionOrder, plan.PersistBeforeEffects)
    let compensation = ShardedJournalAdapter.compensations plan [ second; first ]
    Assert.Equal(first.Address, compensation.Head.Address)
    Assert.All(compensation, fun value -> Assert.True(value.OriginalResultRetained))

[<Fact>]
let ``protection binds exact repository rulesets target and bypass split`` () =
    let target = "refs/heads/fsgg/v2/journal/**/*"
    let writer = { Id = 21872113L; Name = "v2-journal-writer"; Active = true; Target = target; BypassAppIds = [ 4166418L ]; RestrictsCreationAndUpdate = true; RejectsDeletionAndNonFastForward = false }
    let integrity = { Id = 21872115L; Name = "v2-journal-integrity"; Active = true; Target = target; BypassAppIds = []; RestrictsCreationAndUpdate = false; RejectsDeletionAndNonFastForward = true }
    let observed = { Complete = true; RepositoryId = 1351660651L; Writer = writer; Integrity = integrity; EffectiveRulesComplete = true }
    Assert.Equal(Ok (), ShardedJournalAdapter.validateProtection observed)
    Assert.Equal(Error BypassDrift, ShardedJournalAdapter.validateProtection { observed with Integrity = { integrity with BypassAppIds = [ 4166418L ] } })
    Assert.Equal(Error TargetPatternDrift, ShardedJournalAdapter.validateProtection { observed with Writer = { writer with Target = "refs/heads/fsgg/v2/journal/**" } })

[<Fact>]
let ``journal qualification inventory is closed and every mutation must be red`` () =
    let passing: GitHubShardedJournalControlResult list = GitHubShardedJournalQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok (), GitHubShardedJournalQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index value -> if index = 6 then { value with MutationRed = false } else value)
    match GitHubShardedJournalQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GSJQ-INDEPENDENT-NOT-RED" && finding.ControlId = "ambiguous-response")
    | Ok () -> failwith "accepted an ambiguous-response mutation that stayed green"
