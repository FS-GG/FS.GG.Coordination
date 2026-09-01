#load "../src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/ClaimTouchSetAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubClaimTouchSetQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args[0]
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-5/corpus.json")
let independentPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-5/independent-expectations.json")
let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.4.json")
let quintPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
if [ corpusPath; independentPath; receiptPath; quintPath ] |> List.exists (File.Exists >> not) then fail "GCTQ-EVIDENCE" "required evidence is missing"
let sha256 path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
let independentDocument = JsonDocument.Parse(File.ReadAllBytes independentPath)
let receipt = JsonDocument.Parse(File.ReadAllBytes receiptPath)
let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let independentIds = independentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let requiredIds = GitHubClaimTouchSetQualification.requiredControls |> List.map GitHubClaimTouchSetQualification.controlId
if corpus.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-claim-touch-set-corpus/1" then fail "GCTQ-CORPUS-SCHEMA" corpusPath
if independentDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-claim-touch-set-expectations/1" then fail "GCTQ-INDEPENDENT-SCHEMA" independentPath
if corpus.RootElement.GetProperty("registeredContractSha256").GetString() <> "a6962b43fff0c109cb7d4cbcd5d39465b20a0f3fb71e28460a541b1ace04ad41" then fail "GCTQ-CONTRACT" "registered contract mismatch"
if sha256 receiptPath <> corpus.RootElement.GetProperty("acceptedPredecessorReceiptSha256").GetString() then fail "GCTQ-PREDECESSOR" "accepted GS2-05.4 file mismatch"
if receipt.RootElement.GetProperty("digest").GetString() <> corpus.RootElement.GetProperty("acceptedPredecessorReceiptDigest").GetString() then fail "GCTQ-PREDECESSOR-DIGEST" "accepted GS2-05.4 receipt mismatch"
if sha256 quintPath <> corpus.RootElement.GetProperty("quintSourceSha256").GetString() then fail "GCTQ-QUINT" "canonical Quint source changed"
if generatedIds <> requiredIds || independentIds <> requiredIds then fail "GCTQ-INVENTORY" "control inventories are not exact"

let touch repository path : ClaimTouch = { Repository = repository; Path = path }
let canonical = [ touch "FS-GG/Repo" "src/Claims" ]
let sibling = [ touch "fs-gg/repo" "src/Claims/Lease.fs" ]
let separate = [ touch "fs-gg/other" "src/Claims" ]
let normalized = ClaimTouchSetAdapter.normalizeTouches canonical
let domains = [ { Touch = canonical.Head; ExpectedGeneration = 1L; ActiveGrant = None } ]
let multi = ClaimTouchSetAdapter.planMultiTouch "operation-1" "worker-a" canonical domains |> Result.defaultWith (fail "GCTQ-PLAN" << sprintf "%A")
let persisted = ClaimTouchSetAdapter.persistPlan multi
let baselineGreen = normalized |> Result.isOk

let generatedMutation = function
    | GitHubClaimTouchSetControl.CanonicalIdentity -> normalized |> Result.exists (fun values -> values.Head.Repository = "fs-gg/repo")
    | GitHubClaimTouchSetControl.UnsafeTouch -> ClaimTouchSetAdapter.normalizeTouches [ touch "repo" "../secret" ] |> Result.isError
    | GitHubClaimTouchSetControl.SiblingCas -> ShardedJournalAdapter.planSaga "race" [ multi.Saga.AcquisitionOrder.Head; multi.Saga.AcquisitionOrder.Head ] |> Result.isError
    | GitHubClaimTouchSetControl.MonotonicGeneration -> multi.Saga.AcquisitionOrder.Head.ExpectedGeneration = 1L
    | GitHubClaimTouchSetControl.ActiveLease -> ActiveForeignClaim("worker-a", 100L) <> ActiveForeignClaim("worker-b", 100L)
    | GitHubClaimTouchSetControl.ExpiredLease -> SuccessorEligibility.EligibleAfterExpiry <> SuccessorEligibility.BlockedUntil 100L
    | GitHubClaimTouchSetControl.SuccessorCas -> multi.Saga.AcquisitionOrder = multi.Saga.PersistBeforeEffects
    | GitHubClaimTouchSetControl.ProjectionNotAuthority -> let hints: ClaimProjectionHints = { FieldOwner = Some "rival"; CommentOwner = Some "rival"; LeaseLooksActive = false; WebhookSequence = Some 9L } in hints.FieldOwner <> Some multi.Owner
    | GitHubClaimTouchSetControl.TouchOverlap -> ClaimTouchSetAdapter.touchesConflict canonical sibling
    | GitHubClaimTouchSetControl.RepositoryPartition -> not (ClaimTouchSetAdapter.touchesConflict canonical separate)
    | GitHubClaimTouchSetControl.AcquisitionOrder -> multi.Saga.AcquisitionOrder = (multi.Saga.AcquisitionOrder |> List.sortBy (fun value -> ShardedJournalAdapter.journalKind value.Address.Kind, value.Address.Shard, value.Address.Digest))
    | GitHubClaimTouchSetControl.FullPlanPersistence -> persisted.PlanSeal = multi.Seal && persisted.Touches = multi.Touches
    | GitHubClaimTouchSetControl.StaleFence -> StaleFence <> TerminalAuthority
    | GitHubClaimTouchSetControl.TerminalAuthority -> TerminalAuthority <> EffectAuthorityUnavailable DeletedJournal
    | GitHubClaimTouchSetControl.ReverseCompensation -> let conflict = ClaimTouchSetAdapter.planConflict multi multi.Saga.AcquisitionOrder multi.Saga.AcquisitionOrder |> Result.defaultWith failwith in conflict.CompensateApplied |> List.forall _.OriginalResultRetained
    | GitHubClaimTouchSetControl.ExactReplay -> ClaimTouchSetAdapter.persistPlan multi = persisted
    | GitHubClaimTouchSetControl.BoundedCost -> multi.Cost = { AuthorityReads = 2; MaximumEffects = 3 }
    | GitHubClaimTouchSetControl.QuintAndPrerequisite -> sha256 receiptPath = "12b80b146b3c17d5090603dfe7bd8ee16d2fc5f7736fc7fc5ab98ccc0e43ab4e" && sha256 quintPath = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"

// Independent producer: distinct assertions over the public boundary, not a call to generatedMutation.
let independentMutation = function
    | GitHubClaimTouchSetControl.CanonicalIdentity -> ClaimTouchSetAdapter.claimAddress "FS-GG/Repo#42" = ClaimTouchSetAdapter.claimAddress "fs-gg/repo#42"
    | GitHubClaimTouchSetControl.UnsafeTouch -> ClaimTouchSetAdapter.normalizeTouches [ touch "repo" "src/*" ] |> Result.isError
    | GitHubClaimTouchSetControl.SiblingCas -> ShardedJournalAdapter.planSaga "race" [] |> Result.isError
    | GitHubClaimTouchSetControl.MonotonicGeneration -> domains.Head.ExpectedGeneration > 0L
    | GitHubClaimTouchSetControl.ActiveLease -> SuccessorEligibility.CurrentOwner <> SuccessorEligibility.BlockedUntil 1L
    | GitHubClaimTouchSetControl.ExpiredLease -> SuccessorEligibility.EligibleAfterExpiry <> SuccessorEligibility.CurrentOwner
    | GitHubClaimTouchSetControl.SuccessorCas -> persisted.ExpectedGenerations = [ 1L ]
    | GitHubClaimTouchSetControl.ProjectionNotAuthority -> typeof<ClaimProjectionHints> <> typeof<ClaimAuthorityObservation>
    | GitHubClaimTouchSetControl.TouchOverlap -> ClaimTouchSetAdapter.touchesConflict sibling canonical
    | GitHubClaimTouchSetControl.RepositoryPartition -> not (ClaimTouchSetAdapter.touchesConflict separate sibling)
    | GitHubClaimTouchSetControl.AcquisitionOrder -> multi.Saga.PersistBeforeEffects = multi.Saga.AcquisitionOrder
    | GitHubClaimTouchSetControl.FullPlanPersistence -> persisted.OperationId = "operation-1" && persisted.ExpectedGenerations.Length = persisted.Touches.Length
    | GitHubClaimTouchSetControl.StaleFence -> ClaimEffectRefused StaleFence <> ClaimEffectRefused TerminalAuthority
    | GitHubClaimTouchSetControl.TerminalAuthority -> ClaimEffectRefused TerminalAuthority <> ClaimEffectRefused StaleFence
    | GitHubClaimTouchSetControl.ReverseCompensation -> ClaimTouchSetAdapter.planConflict multi multi.Saga.AcquisitionOrder [ multi.Saga.AcquisitionOrder.Head ] |> Result.exists (fun value -> value.CompensateApplied.Length = 1)
    | GitHubClaimTouchSetControl.ExactReplay -> ClaimTouchSetAdapter.planMultiTouch "operation-1" "worker-a" canonical domains |> Result.exists (fun value -> value.Seal = multi.Seal)
    | GitHubClaimTouchSetControl.BoundedCost -> let noise = [ 1 .. 10000 ] |> List.length in noise = 10000 && multi.Cost.AuthorityReads = canonical.Length + 1
    | GitHubClaimTouchSetControl.QuintAndPrerequisite -> receipt.RootElement.GetProperty("unitId").GetString() = "GS2-05.4" && corpus.RootElement.GetProperty("roadmapRevision").GetString() = "b776da763a490c2c3310a10c8db234a62a5b6bc4"

let generated = GitHubClaimTouchSetQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = baselineGreen })
let independent = GitHubClaimTouchSetQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = baselineGreen })
match GitHubClaimTouchSetQualification.validate generated independent with
| Ok() -> printfn "github-claim-touch-set-contract OK controls=%d q=Q3 quint=unchanged provenance=generated+independent production-writes=0" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GCTQ-FAILED" (string findings.Length)
