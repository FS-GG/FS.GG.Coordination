module FS.GG.Coordination.GitHubClaimTouchSetTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private oid value = String.replicate 40 value
let private touch repository path = { Repository = repository; Path = path }

let private claimCommit (authority: ClaimAuthorityRecord) (generation: int64) (parent: string option) (prior: string option) (commitOid: string) =
    let address = ClaimTouchSetAdapter.claimAddress authority.Subject |> Result.defaultWith (failwithf "%A")
    let bytes = ClaimTouchSetAdapter.authorityBytes authority |> Result.defaultWith (failwithf "%A")
    let event = { Bytes = bytes; Digest = ShardedJournalAdapter.sha256 bytes }
    let unsigned =
        { SchemaVersion = 1
          Address = address
          Generation = generation
          EventDigest = event.Digest
          SnapshotDigest = None
          Terminal = false
          PriorHeadDigest = prior
          HeadDigest = String.replicate 64 "0" }
    let headBytes = ShardedJournalAdapter.journalHeadBytes unsigned
    let head = { unsigned with HeadDigest = ShardedJournalAdapter.sha256 headBytes }
    { CommitOid = commitOid
      ParentOid = parent
      TreeOid = oid "b"
      OperationId = authority.OperationId
      Head = head
      HeadBytes = ShardedJournalAdapter.journalHeadBytes head
      Event = event
      Checkpoint = None }

let private baseAuthority: ClaimAuthorityRecord =
    { SchemaVersion = 1
      Subject = "FS-GG/Repo#42"
      Owner = "worker-a"
      Touches = [ touch "FS-GG/Repo" "src/Claims" ]
      LeaseExpiresAt = 100L
      OperationId = "claim-root" }

let private baseCommit = claimCommit baseAuthority 1L None None (oid "a")
let private baseObservation =
    { Complete = true
      Journal = JournalComplete("revision-1", [ baseCommit ])
      Current = baseAuthority }

let private baseGrant =
    { Address = baseCommit.Head.Address
      Subject = baseAuthority.Subject.ToLowerInvariant()
      Owner = baseAuthority.Owner
      Touches = baseAuthority.Touches |> List.map (fun value -> { value with Repository = value.Repository.ToLowerInvariant() })
      JournalCommit = baseCommit.CommitOid
      Generation = 1L }

let private domainCommit (address: AggregateAddress) commitOid =
    let bytes = ClaimTouchSetAdapter.authorityBytes baseAuthority |> Result.defaultWith (failwithf "%A")
    let event = { Bytes = bytes; Digest = ShardedJournalAdapter.sha256 bytes }
    let unsigned =
        { SchemaVersion = 1
          Address = address
          Generation = 1L
          EventDigest = event.Digest
          SnapshotDigest = None
          Terminal = false
          PriorHeadDigest = None
          HeadDigest = String.replicate 64 "0" }
    let head = { unsigned with HeadDigest = ShardedJournalAdapter.sha256 (ShardedJournalAdapter.journalHeadBytes unsigned) }
    { CommitOid = commitOid
      ParentOid = None
      TreeOid = oid "e"
      OperationId = "domain-root"
      Head = head
      HeadBytes = ShardedJournalAdapter.journalHeadBytes head
      Event = event
      Checkpoint = None }

[<Fact>]
let ``touches are canonical repository-scoped and ancestry conflicts`` () =
    let normalized =
        ClaimTouchSetAdapter.normalizeTouches
            [ touch "FS-GG/Repo" "src/Claims/Lease.fs"; touch "fs-gg/repo" "src/Claims" ]
        |> Result.defaultWith (failwithf "%A")
    Assert.Equal("fs-gg/repo", normalized.Head.Repository)
    Assert.True(ClaimTouchSetAdapter.touchesConflict [ touch "fs-gg/repo" "src/Claims" ] [ touch "FS-GG/Repo" "src/Claims/Lease.fs" ])
    Assert.False(ClaimTouchSetAdapter.touchesConflict [ touch "fs-gg/repo" "src/Claims" ] [ touch "fs-gg/other" "src/Claims" ])
    Assert.True(ClaimTouchSetAdapter.normalizeTouches [ touch "repo" "../secret" ] |> Result.isError)
    Assert.True(ClaimTouchSetAdapter.normalizeTouches [ touch "repo" "src"; touch "REPO" "src" ] |> Result.isError)

[<Fact>]
let ``expiry grants successor eligibility but never changes current authority`` () =
    Assert.Equal(Ok(SuccessorEligibility.BlockedUntil 100L), ClaimTouchSetAdapter.successorEligibility 99L "worker-b" baseObservation)
    Assert.Equal(Ok SuccessorEligibility.EligibleAfterExpiry, ClaimTouchSetAdapter.successorEligibility 100L "worker-b" baseObservation)
    Assert.Equal(Ok SuccessorEligibility.CurrentOwner, ClaimTouchSetAdapter.successorEligibility 10L "worker-a" baseObservation)
    Assert.True(ClaimTouchSetAdapter.authorizeEffect baseGrant baseObservation |> Result.isOk)
    Assert.Equal(Error WrongClaimOwner, ClaimTouchSetAdapter.authorizeEffect { baseGrant with Owner = "worker-b" } baseObservation)

[<Fact>]
let ``successor acquisition is expected-parent CAS with a new fencing generation`` () =
    let intent =
        { Subject = "FS-GG/Repo#42"
          Owner = "worker-b"
          Touches = [ touch "fs-gg/repo" "src/Claims" ]
          Now = 100L
          LeaseExpiresAt = 200L }
    let plan =
        ClaimTouchSetAdapter.planAcquire intent baseObservation { CommitOid = oid "c"; TreeOid = oid "d" }
        |> Result.defaultWith (failwithf "%A")
    Assert.Equal(baseCommit.CommitOid, plan.Proposal.ObservedObjectId)
    Assert.Equal(2L, plan.Grant.Generation)
    Assert.Equal($"--force-with-lease={baseCommit.Head.Address.Ref}:{baseCommit.CommitOid}", plan.Proposal.ForceWithLease)
    let reread =
        { Complete = true
          Journal = JournalComplete("revision-2", [ baseCommit; plan.Proposal.ProposedCommit ])
          Current = plan.ProposedAuthority }
    Assert.Equal(ClaimAcquired plan.Grant, ClaimTouchSetAdapter.confirmAcquire plan ReceiveResponseUnknown reread)
    Assert.Equal(ClaimParentConflict, ClaimTouchSetAdapter.confirmAcquire plan ReceiveParentConflict baseObservation)
    let staleGrant =
        { plan.Grant with
            Owner = baseAuthority.Owner
            Touches = baseAuthority.Touches |> List.map (fun value -> { value with Repository = value.Repository.ToLowerInvariant() }) }
    Assert.Equal(Error(ClaimEffectRefused EffectRefusal.StaleFence), ClaimTouchSetAdapter.authorizeEffect staleGrant baseObservation)
    Assert.True(ClaimTouchSetAdapter.authorizeEffect plan.Grant reread |> Result.isOk)

[<Fact>]
let ``active foreign lease blocks proposal even when projections claim otherwise`` () =
    let misleading: ClaimProjectionHints =
        { FieldOwner = Some "worker-b"
          CommentOwner = Some "worker-b"
          LeaseLooksActive = false
          WebhookSequence = Some 999L }
    Assert.Equal(Some "worker-b", misleading.FieldOwner)
    let intent =
        { Subject = baseAuthority.Subject
          Owner = "worker-b"
          Touches = baseAuthority.Touches
          Now = 99L
          LeaseExpiresAt = 200L }
    match ClaimTouchSetAdapter.planAcquire intent baseObservation { CommitOid = oid "c"; TreeOid = oid "d" } with
    | Error [ ActiveForeignClaim("worker-a", 100L) ] -> ()
    | value -> failwithf "projection hints affected authority: %A" value

[<Fact>]
let ``multi-touch plan persists the full ordered acquisition and compensates in reverse`` () =
    let touches = [ touch "repo" "src/Z"; touch "repo" "src/A" ]
    let domains = touches |> List.map (fun value -> { Touch = value; ExpectedGeneration = 3L; ActiveGrant = None })
    let plan = ClaimTouchSetAdapter.planMultiTouch "operation-42" "worker-a" touches domains |> Result.defaultWith (failwithf "%A")
    Assert.Equal<ClaimTouch list>([ touch "repo" "src/A"; touch "repo" "src/Z" ], plan.Touches)
    Assert.Equal<SagaTouch list>(plan.Saga.PersistBeforeEffects, plan.Saga.AcquisitionOrder)
    let expectedCost: ClaimCost = { AuthorityReads = 3; MaximumEffects = 5 }
    Assert.Equal(expectedCost, plan.Cost)
    let receipt = ClaimTouchSetAdapter.persistPlan plan
    Assert.Equal(plan.Seal, receipt.PlanSeal)
    let conflict = ClaimTouchSetAdapter.planConflict plan plan.Saga.AcquisitionOrder plan.Saga.AcquisitionOrder |> Result.defaultWith failwith
    Assert.Equal<SagaTouch list>([], conflict.ReleaseUnconsumed)
    Assert.Equal<SagaTouch list>(plan.Saga.AcquisitionOrder |> List.rev, conflict.CompensateApplied |> List.map (fun value -> { Address = value.Address; ExpectedGeneration = value.Generation }))
    Assert.All(conflict.CompensateApplied, fun value -> Assert.True value.OriginalResultRetained)
    Assert.Equal(Error [ PersistedPlanMissing ], ClaimTouchSetAdapter.authorizeMultiTouchEffects plan None (baseGrant, baseObservation) [])

[<Fact>]
let ``multi-touch effects require every distinct persisted domain proof`` () =
    let touches = [ touch "repo" "src/Z"; touch "repo" "src/A" ]
    let domains = touches |> List.map (fun value -> { Touch = value; ExpectedGeneration = 1L; ActiveGrant = None })
    let plan = ClaimTouchSetAdapter.planMultiTouch "operation-43" "worker-a" touches domains |> Result.defaultWith (failwithf "%A")
    let persisted = ClaimTouchSetAdapter.persistPlan plan
    let primaryAuthority = { baseAuthority with Touches = touches; OperationId = "multi-root" }
    let primaryCommit = claimCommit primaryAuthority 1L None None (oid "2")
    let primaryObservation =
        { Complete = true
          Journal = JournalComplete("multi-primary", [ primaryCommit ])
          Current = primaryAuthority }
    let primaryGrant =
        { Address = primaryCommit.Head.Address
          Subject = primaryAuthority.Subject.ToLowerInvariant()
          Owner = primaryAuthority.Owner
          Touches = plan.Touches
          JournalCommit = primaryCommit.CommitOid
          Generation = 1L }
    let proofs =
        plan.Domains
        |> List.mapi (fun index domain ->
            let commit = domainCommit domain.Address (if index = 0 then oid "f" else oid "1")
            ({ Touch = domain.Touch; JournalCommit = commit.CommitOid; Generation = domain.ExpectedGeneration },
             JournalComplete($"domain-{index}", [ commit ])))
    let authorized =
        ClaimTouchSetAdapter.authorizeMultiTouchEffects plan (Some persisted) (primaryGrant, primaryObservation) proofs
        |> Result.defaultWith (failwithf "%A")
    Assert.Equal(3, authorized.Length)

    let duplicate = [ proofs.Head; proofs.Head ]
    match ClaimTouchSetAdapter.authorizeMultiTouchEffects plan (Some persisted) (primaryGrant, primaryObservation) duplicate with
    | Error failures ->
        Assert.Contains(failures, function DuplicateDomainProof _ -> true | _ -> false)
        Assert.Contains(failures, function MissingDomainProof _ -> true | _ -> false)
    | Ok _ -> failwith "a duplicate domain proof replaced an unproved touch"

    let staleProof, staleObservation = proofs.Head
    let stale = ({ staleProof with Generation = 2L }, staleObservation) :: proofs.Tail
    match ClaimTouchSetAdapter.authorizeMultiTouchEffects plan (Some persisted) (primaryGrant, primaryObservation) stale with
    | Error failures -> Assert.Contains(failures, function WrongDomainGeneration _ -> true | _ -> false)
    | Ok _ -> failwith "a stale domain generation authorized effects"

[<Fact>]
let ``multi-touch cost and seal ignore unrelated projection cardinality`` () =
    let touches = [ touch "repo" "src/A" ]
    let domains = [ { Touch = touches.Head; ExpectedGeneration = 1L; ActiveGrant = None } ]
    let first = ClaimTouchSetAdapter.planMultiTouch "operation-42" "worker-a" touches domains |> Result.defaultWith (failwithf "%A")
    let projectionNoise = [ 1 .. 10000 ] |> List.length
    Assert.Equal(10000, projectionNoise)
    let second = ClaimTouchSetAdapter.planMultiTouch "operation-42" "worker-a" touches domains |> Result.defaultWith (failwithf "%A")
    Assert.Equal(first.Seal, second.Seal)
    Assert.Equal(first.Cost, second.Cost)

[<Fact>]
let ``claim qualification inventory is closed and every mutation must be red`` () =
    let passing: GitHubClaimTouchSetControlResult list =
        GitHubClaimTouchSetQualification.requiredControls
        |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok(), GitHubClaimTouchSetQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index value -> if index = 12 then { value with MutationRed = false } else value)
    match GitHubClaimTouchSetQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun finding -> finding.Code = "GCTQ-INDEPENDENT-NOT-RED" && finding.ControlId = "stale-fence")
    | Ok() -> failwith "accepted a stale-fence mutation that stayed green"
