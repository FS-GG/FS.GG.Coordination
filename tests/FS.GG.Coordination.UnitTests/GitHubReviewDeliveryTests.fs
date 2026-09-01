module FS.GG.Coordination.GitHubReviewDeliveryTests

open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private oid value = String.replicate 40 value
let private snapshot = { Complete = true; Subject = "FS-GG/Repo#42"; BaseCommit = oid "a"; HeadCommit = oid "b"; ChangedFiles = [ "src/A.fs" ]; RequiredChecks = [ "bootstrap" ] }
let private chain = ReviewDeliveryAdapter.chainId snapshot.Subject |> Result.defaultWith (failwithf "%A")
let private epoch = ReviewDeliveryAdapter.epochKey chain snapshot |> Result.defaultWith (failwithf "%A")
let private seat = ReviewDeliveryAdapter.phaseSeat epoch 1L |> Result.defaultWith (failwithf "%A")
let private reviewAuthority = { SchemaVersion = 1; ChainId = chain; EpochKey = epoch; SnapshotDigest = ReviewDeliveryAdapter.snapshotBytes snapshot |> Result.defaultWith (failwithf "%A") |> ShardedJournalAdapter.sha256; AccountableAuthority = "maintainer-a"; PhaseSeat = seat; SeatOrdinal = 1L; Verdict = ReviewPass; OperationId = "review-root" }

let private commit (address: AggregateAddress) (generation: int64) (parent: string option) (prior: string option) (operationId: string) (bytes: byte array) (commitOid: string) : JournalCommit =
    let event = { Bytes = bytes; Digest = ShardedJournalAdapter.sha256 bytes }
    let unsigned = { SchemaVersion = 1; Address = address; Generation = generation; EventDigest = event.Digest; SnapshotDigest = None; Terminal = false; PriorHeadDigest = prior; HeadDigest = String.replicate 64 "0" }
    let head = { unsigned with HeadDigest = ShardedJournalAdapter.journalHeadBytes unsigned |> ShardedJournalAdapter.sha256 }
    { CommitOid = commitOid; ParentOid = parent; TreeOid = oid "c"; OperationId = operationId; Head = head; HeadBytes = ShardedJournalAdapter.journalHeadBytes head; Event = event; Checkpoint = None }

let private reviewAddress = ReviewDeliveryAdapter.reviewAddress chain |> Result.defaultWith (failwithf "%A")
let private reviewBytes = ReviewDeliveryAdapter.reviewAuthorityBytes reviewAuthority |> Result.defaultWith (failwithf "%A")
let private reviewCommit = commit reviewAddress 1L None None reviewAuthority.OperationId reviewBytes (oid "d")
let private reviewObservation: ReviewAuthorityObservation = { Complete = true; Journal = JournalComplete("review-1", [ reviewCommit ]); Current = reviewAuthority }
let private grant: ReviewGrant = { Address = reviewAddress; ChainId = chain; EpochKey = epoch; SnapshotDigest = reviewAuthority.SnapshotDigest; AccountableAuthority = reviewAuthority.AccountableAuthority; PhaseSeat = seat; JournalCommit = reviewCommit.CommitOid; Generation = 1L }

let private deliveryRecord = { SchemaVersion = 1; Subject = snapshot.Subject.ToLowerInvariant(); Kind = DeliveryReceipt; ReviewChainId = chain; ReviewEpochKey = epoch; ReviewSeat = seat; MergeCommit = oid "e"; ProtectedRunId = None; OperationId = "delivery-root" }
let private operationAddress = ReviewDeliveryAdapter.deliveryAddress snapshot.Subject |> Result.defaultWith (failwithf "%A")
let private deliveryBytes = ReviewDeliveryAdapter.deliveryAuthorityBytes deliveryRecord |> Result.defaultWith (failwithf "%A")
let private operationCommit = commit operationAddress 1L None None deliveryRecord.OperationId deliveryBytes (oid "f")
let private deliveryObservation: DeliveryAuthorityObservation = { Complete = true; Journal = JournalComplete("delivery-1", [ operationCommit ]); Current = deliveryRecord }

[<Fact>]
let ``stable chain excludes snapshot while epoch binds the complete snapshot`` () =
    let changed = { snapshot with HeadCommit = oid "1" }
    Assert.Equal(chain, ReviewDeliveryAdapter.chainId changed.Subject |> Result.defaultWith (failwithf "%A"))
    Assert.NotEqual<string>(epoch, ReviewDeliveryAdapter.epochKey chain changed |> Result.defaultWith (failwithf "%A"))
    Assert.True(ReviewDeliveryAdapter.snapshotBytes { snapshot with Complete = false } |> Result.isError)

[<Fact>]
let ``current passing epoch and seat alone authorize review effects`` () =
    Assert.True(ReviewDeliveryAdapter.authorizeReview grant snapshot reviewObservation |> Result.isOk)
    let changed = { snapshot with HeadCommit = oid "1" }
    Assert.Equal(Error WrongReviewSnapshot, ReviewDeliveryAdapter.authorizeReview grant changed reviewObservation)
    Assert.Equal(Error WrongReviewSeat, ReviewDeliveryAdapter.authorizeReview { grant with PhaseSeat = "historical-seat" } snapshot reviewObservation)
    let rejectedAuthority = { reviewAuthority with Verdict = ReviewChangesRequired; OperationId = "review-rejected" }
    let rejectedBytes = ReviewDeliveryAdapter.reviewAuthorityBytes rejectedAuthority |> Result.defaultWith (failwithf "%A")
    let rejectedCommit = commit reviewAddress 1L None None rejectedAuthority.OperationId rejectedBytes (oid "7")
    let rejectedObservation: ReviewAuthorityObservation = { Complete = true; Journal = JournalComplete("review-rejected", [ rejectedCommit ]); Current = rejectedAuthority }
    Assert.Equal(Error ReviewNotPassed, ReviewDeliveryAdapter.authorizeReview { grant with JournalCommit = rejectedCommit.CommitOid } snapshot rejectedObservation)

[<Fact>]
let ``succession is fresh inside an epoch and a new epoch keeps accountability`` () =
    let sameEpoch = ReviewDeliveryAdapter.planReview snapshot.Subject "maintainer-a" 2L ReviewPass snapshot reviewObservation { CommitOid = oid "1"; TreeOid = oid "2" } |> Result.defaultWith (failwithf "%A")
    Assert.Equal(epoch, sameEpoch.Grant.EpochKey)
    Assert.NotEqual<string>(seat, sameEpoch.Grant.PhaseSeat)
    Assert.Equal(Error [ ReusedPhaseSeat ], ReviewDeliveryAdapter.planReview snapshot.Subject "maintainer-a" 1L ReviewPass snapshot reviewObservation { CommitOid = oid "1"; TreeOid = oid "2" })
    Assert.True(ReviewDeliveryAdapter.planReview "FS-GG/Other#1" "maintainer-a" 1L ReviewPass snapshot reviewObservation { CommitOid = oid "1"; TreeOid = oid "2" } |> Result.isError)
    let changed = { snapshot with HeadCommit = oid "3" }
    let newEpoch = ReviewDeliveryAdapter.planReview snapshot.Subject "maintainer-a" 1L ReviewPass changed reviewObservation { CommitOid = oid "4"; TreeOid = oid "5" } |> Result.defaultWith (failwithf "%A")
    Assert.NotEqual<string>(epoch, newEpoch.Grant.EpochKey)
    Assert.Equal("maintainer-a", newEpoch.Grant.AccountableAuthority)

[<Fact>]
let ``merged and protected verified are distinct receipt boundaries`` () =
    let delivery = ReviewDeliveryAdapter.planDelivery DeliveryReceipt grant snapshot reviewObservation (Merged(oid "9")) deliveryObservation { CommitOid = oid "1"; TreeOid = oid "2" }
    match delivery with Ok(DeliveryPlanned plan) -> Assert.Equal(DeliveryReceipt, plan.Receipt.Record.Kind) | value -> failwithf "%A" value
    Assert.Equal(Error [ ProtectedVerificationRequired ], ReviewDeliveryAdapter.planDelivery DoneReceipt grant snapshot reviewObservation (Merged(oid "9")) deliveryObservation { CommitOid = oid "1"; TreeOid = oid "2" })
    let donePlan = ReviewDeliveryAdapter.planDelivery DoneReceipt grant snapshot reviewObservation (ProtectedVerified(oid "9", 42L, oid "9", "success")) deliveryObservation { CommitOid = oid "3"; TreeOid = oid "4" }
    match donePlan with Ok(DeliveryPlanned plan) -> Assert.Equal(Some 42L, plan.Receipt.Record.ProtectedRunId) | value -> failwithf "%A" value
    Assert.Equal(Error [ ProtectedRunCommitMismatch ], ReviewDeliveryAdapter.planDelivery DoneReceipt grant snapshot reviewObservation (ProtectedVerified(oid "9", 42L, oid "8", "success")) deliveryObservation { CommitOid = oid "3"; TreeOid = oid "4" })

[<Fact>]
let ``exact delivery replay is idempotent and qualification inventory is closed`` () =
    let first = ReviewDeliveryAdapter.planDelivery DeliveryReceipt grant snapshot reviewObservation (Merged(oid "9")) deliveryObservation { CommitOid = oid "1"; TreeOid = oid "2" } |> Result.defaultWith (failwithf "%A")
    let plan = match first with DeliveryPlanned value -> value | _ -> failwith "expected plan"
    let applied: DeliveryAuthorityObservation = { Complete = true; Journal = JournalComplete("delivery-2", [ operationCommit; plan.Proposal.ProposedCommit ]); Current = plan.ProposedAuthority }
    match ReviewDeliveryAdapter.planDelivery DeliveryReceipt grant snapshot reviewObservation (Merged(oid "9")) applied { CommitOid = oid "7"; TreeOid = oid "8" } with
    | Ok(DeliveryReplayed receipt) -> Assert.Equal(plan.Receipt.Digest, receipt.Digest)
    | value -> failwithf "%A" value
    Assert.True(ReviewDeliveryAdapter.planDelivery DeliveryReceipt grant snapshot reviewObservation (Merged(oid "9")) applied { CommitOid = "ignored-on-replay"; TreeOid = "ignored-on-replay" } |> Result.isOk)
    let passing: GitHubReviewDeliveryControlResult list = GitHubReviewDeliveryQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = true; BaselineGreen = true })
    Assert.Equal(Ok(), GitHubReviewDeliveryQualification.validate passing passing)
    let broken = passing |> List.mapi (fun index value -> if index = 6 then { value with MutationRed = false } else value)
    match GitHubReviewDeliveryQualification.validate passing broken with
    | Error findings -> Assert.Contains(findings, fun value -> value.ControlId = "historical-pass")
    | Ok() -> failwith "historical pass mutation authorized"
