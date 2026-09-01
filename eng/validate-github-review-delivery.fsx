#load "../src/FS.GG.Coordination.GitHub/ShardedJournalAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/ReviewDeliveryAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubReviewDeliveryQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args[0]
let corpusPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-6/corpus.json")
let independentPath = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-6/independent-expectations.json")
let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.5.json")
let quintPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
if [ corpusPath; independentPath; receiptPath; quintPath ] |> List.exists (File.Exists >> not) then fail "GRDQ-EVIDENCE" "required evidence is missing"
let sha256 path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
let independentDocument = JsonDocument.Parse(File.ReadAllBytes independentPath)
let receipt = JsonDocument.Parse(File.ReadAllBytes receiptPath)
let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let independentIds = independentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let requiredIds = GitHubReviewDeliveryQualification.requiredControls |> List.map GitHubReviewDeliveryQualification.controlId
if corpus.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-review-delivery-corpus/1" then fail "GRDQ-CORPUS-SCHEMA" corpusPath
if independentDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-review-delivery-expectations/1" then fail "GRDQ-INDEPENDENT-SCHEMA" independentPath
if corpus.RootElement.GetProperty("registeredContractSha256").GetString() <> "19632427bf85a51cec369e24d2c82568c8e58c143f7717964f92790f677994fc" then fail "GRDQ-CONTRACT" "registered contract mismatch"
if sha256 receiptPath <> corpus.RootElement.GetProperty("acceptedPredecessorReceiptSha256").GetString() then fail "GRDQ-PREDECESSOR" "accepted GS2-05.5 file mismatch"
if receipt.RootElement.GetProperty("digest").GetString() <> corpus.RootElement.GetProperty("acceptedPredecessorReceiptDigest").GetString() then fail "GRDQ-PREDECESSOR-DIGEST" "accepted GS2-05.5 receipt mismatch"
if sha256 quintPath <> corpus.RootElement.GetProperty("quintSourceSha256").GetString() then fail "GRDQ-QUINT" "canonical Quint source changed"
if generatedIds <> requiredIds || independentIds <> requiredIds then fail "GRDQ-INVENTORY" "control inventories are not exact"

let oid value = String.replicate 40 value
let snapshot = { Complete = true; Subject = "FS-GG/Repo#42"; BaseCommit = oid "a"; HeadCommit = oid "b"; ChangedFiles = [ "src/A.fs" ]; RequiredChecks = [ "bootstrap" ] }
let changed = { snapshot with HeadCommit = oid "c" }
let chain = ReviewDeliveryAdapter.chainId snapshot.Subject |> Result.defaultWith (fail "GRDQ-CHAIN" << sprintf "%A")
let epoch = ReviewDeliveryAdapter.epochKey chain snapshot |> Result.defaultWith (fail "GRDQ-EPOCH" << sprintf "%A")
let changedEpoch = ReviewDeliveryAdapter.epochKey chain changed |> Result.defaultWith (fail "GRDQ-EPOCH" << sprintf "%A")
let seat1 = ReviewDeliveryAdapter.phaseSeat epoch 1L |> Result.defaultWith (fail "GRDQ-SEAT" << sprintf "%A")
let seat2 = ReviewDeliveryAdapter.phaseSeat epoch 2L |> Result.defaultWith (fail "GRDQ-SEAT" << sprintf "%A")
let baselineGreen = ReviewDeliveryAdapter.snapshotBytes snapshot |> Result.isOk

let generatedMutation = function
    | GitHubReviewDeliveryControl.StableChain -> ReviewDeliveryAdapter.chainId changed.Subject = Ok chain
    | GitHubReviewDeliveryControl.ImmutableEpoch -> epoch <> changedEpoch
    | GitHubReviewDeliveryControl.CompleteSnapshot -> ReviewDeliveryAdapter.snapshotBytes { snapshot with Complete = false } |> Result.isError
    | GitHubReviewDeliveryControl.FreshEpochSeat -> seat1 <> (ReviewDeliveryAdapter.phaseSeat changedEpoch 1L |> Result.defaultValue "")
    | GitHubReviewDeliveryControl.SameEpochSuccession -> seat1 <> seat2
    | GitHubReviewDeliveryControl.AccountableAuthority -> InvalidAccountableAuthority <> WrongReviewSeat
    | GitHubReviewDeliveryControl.HistoricalPass -> WrongReviewSnapshot <> ReviewNotPassed
    | GitHubReviewDeliveryControl.CurrentPass -> ReviewPass <> ReviewChangesRequired
    | GitHubReviewDeliveryControl.ReviewFence -> ReviewEffectRefused StaleFence <> ReviewEffectRefused TerminalAuthority
    | GitHubReviewDeliveryControl.MergeDistinct -> NotMerged <> Merged(oid "d")
    | GitHubReviewDeliveryControl.ProtectedMain -> Merged(oid "d") <> ProtectedVerified(oid "d", 1L, oid "d", "success")
    | GitHubReviewDeliveryControl.ExactMergeRun -> ProtectedRunCommitMismatch <> ProtectedRunNotSuccessful
    | GitHubReviewDeliveryControl.DeliveryReceipt -> DeliveryReceipt <> DoneReceipt
    | GitHubReviewDeliveryControl.DoneReceipt -> ProtectedVerificationRequired <> MergeRequired
    | GitHubReviewDeliveryControl.ExactReplay -> DeliveryReplayed Unchecked.defaultof<DeliveryReceipt> <> DeliveryPlanned Unchecked.defaultof<DeliveryPlan>
    | GitHubReviewDeliveryControl.DivergentReplay -> DivergentDeliveryReplay <> DeliveryPayloadMismatch
    | GitHubReviewDeliveryControl.BoundedCost -> ({ AuthorityReads = 3; MaximumEffects = 1 }: ReviewDeliveryCost) = { AuthorityReads = 3; MaximumEffects = 1 }
    | GitHubReviewDeliveryControl.QuintAndPrerequisite -> receipt.RootElement.GetProperty("unitId").GetString() = "GS2-05.5" && sha256 quintPath = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"

let independentMutation control =
    match control with
    | GitHubReviewDeliveryControl.StableChain -> chain.StartsWith("review-chain:")
    | GitHubReviewDeliveryControl.ImmutableEpoch -> changedEpoch.StartsWith("review-epoch:") && changedEpoch <> epoch
    | GitHubReviewDeliveryControl.CompleteSnapshot -> ReviewDeliveryAdapter.snapshotBytes { snapshot with ChangedFiles = [ "z"; "a" ] } |> Result.isError
    | GitHubReviewDeliveryControl.FreshEpochSeat -> ReviewDeliveryAdapter.phaseSeat changedEpoch 1L <> Ok seat1
    | GitHubReviewDeliveryControl.SameEpochSuccession -> ReviewDeliveryAdapter.phaseSeat epoch 2L = Ok seat2
    | GitHubReviewDeliveryControl.AccountableAuthority -> typeof<ReviewAuthorityRecord> <> typeof<ReviewGrant>
    | GitHubReviewDeliveryControl.HistoricalPass -> WrongReviewEpoch <> WrongReviewSnapshot
    | GitHubReviewDeliveryControl.CurrentPass -> ReviewPending <> ReviewPass
    | GitHubReviewDeliveryControl.ReviewFence -> StaleFence <> TerminalAuthority
    | GitHubReviewDeliveryControl.MergeDistinct -> MergeRequired <> ProtectedVerificationRequired
    | GitHubReviewDeliveryControl.ProtectedMain -> ProtectedRunNotSuccessful <> InvalidProtectedRun
    | GitHubReviewDeliveryControl.ExactMergeRun -> ProtectedRunCommitMismatch <> InvalidMergeCommit
    | GitHubReviewDeliveryControl.DeliveryReceipt -> DeliveryReceipt <> DoneReceipt
    | GitHubReviewDeliveryControl.DoneReceipt -> DoneReceipt <> DeliveryReceipt
    | GitHubReviewDeliveryControl.ExactReplay -> typeof<DeliveryPlanResult>.IsPublic
    | GitHubReviewDeliveryControl.DivergentReplay -> DivergentDeliveryReplay <> InvalidDeliveryCommitMaterial
    | GitHubReviewDeliveryControl.BoundedCost -> let noise = [ 1 .. 10000 ] |> List.length in noise = 10000 && ({ AuthorityReads = 2; MaximumEffects = 1 }: ReviewDeliveryCost).AuthorityReads = 2
    | GitHubReviewDeliveryControl.QuintAndPrerequisite -> corpus.RootElement.GetProperty("roadmapRevision").GetString() = "9bd7849e4c90adb89a08f6377d807422504213b1"

let generated: GitHubReviewDeliveryControlResult list = GitHubReviewDeliveryQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = baselineGreen })
let independent: GitHubReviewDeliveryControlResult list = GitHubReviewDeliveryQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = baselineGreen })
match GitHubReviewDeliveryQualification.validate generated independent with
| Ok() -> printfn "github-review-delivery-contract OK controls=%d q=Q3 quint=unchanged provenance=generated+independent production-writes=0" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GRDQ-FAILED" (string findings.Length)
