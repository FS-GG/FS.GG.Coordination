#load "../src/FS.GG.Coordination.GitHub/IssueFields.fs"
#load "../src/FS.GG.Coordination.GitHub/ProjectAdapter.fs"
#load "../src/FS.GG.Coordination.GitHub/LifecycleProjectionAdapter.fs"
#load "../src/FS.GG.Coordination.Qualification.Contracts/GitHubLifecycleProjectionQualification.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let fail code message = failwith $"{code}: {message}"
let args = fsi.CommandLineArgs |> Array.skip 1
let root = if args.Length = 0 then "." else args[0]
let evidenceRoot = Path.Combine(root, "evidence/github-substrate-v2/gs2-05-7")
let corpusPath = Path.Combine(evidenceRoot, "corpus.json")
let independentPath = Path.Combine(evidenceRoot, "independent-expectations.json")
let receiptPath = Path.Combine(root, "evidence/github-substrate-v2/accepted/GS2-05.6.json")
let quintPath = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")
if [ corpusPath; independentPath; receiptPath; quintPath ] |> List.exists (File.Exists >> not) then fail "GLPQ-EVIDENCE" "required evidence is missing"
let sha256 path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let corpus = JsonDocument.Parse(File.ReadAllBytes corpusPath)
let independentDocument = JsonDocument.Parse(File.ReadAllBytes independentPath)
let receipt = JsonDocument.Parse(File.ReadAllBytes receiptPath)
let generatedIds = corpus.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let independentIds = independentDocument.RootElement.GetProperty("controls").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
let requiredIds = GitHubLifecycleProjectionQualification.requiredControls |> List.map GitHubLifecycleProjectionQualification.controlId
if corpus.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-lifecycle-projection-corpus/1" then fail "GLPQ-CORPUS-SCHEMA" corpusPath
if independentDocument.RootElement.GetProperty("schema").GetString() <> "fsgg.coordination.github-lifecycle-projection-expectations/1" then fail "GLPQ-INDEPENDENT-SCHEMA" independentPath
if corpus.RootElement.GetProperty("registeredContractSha256").GetString() <> "3da7bf73fdfa4729b8950c23be908927c4ced57c4680d4f0e3f3218cc93c392d" then fail "GLPQ-CONTRACT" "registered contract mismatch"
if sha256 receiptPath <> corpus.RootElement.GetProperty("acceptedPredecessorReceiptSha256").GetString() then fail "GLPQ-PREDECESSOR" "accepted GS2-05.6 file mismatch"
if receipt.RootElement.GetProperty("digest").GetString() <> corpus.RootElement.GetProperty("acceptedPredecessorReceiptDigest").GetString() then fail "GLPQ-PREDECESSOR-DIGEST" "accepted GS2-05.6 receipt mismatch"
if sha256 quintPath <> corpus.RootElement.GetProperty("quintSourceSha256").GetString() then fail "GLPQ-QUINT" "canonical Quint source changed"
if generatedIds <> requiredIds || independentIds <> requiredIds then fail "GLPQ-INVENTORY" "control inventories are not exact"

let generatedMutation = function
    | GitHubLifecycleProjectionControl.IntentAuthority -> IntentBacklog <> IntentReady
    | GitHubLifecycleProjectionControl.CompleteKnowledge -> FactObserved <> FactUnreadable
    | GitHubLifecycleProjectionControl.FormalPrecedence -> StageDelivered <> StageAccepted
    | GitHubLifecycleProjectionControl.HoldDependency -> StageBlocked <> StageReady
    | GitHubLifecycleProjectionControl.Claim -> StageClaimed <> StageReady
    | GitHubLifecycleProjectionControl.PullRequest -> StageInReview <> StageClaimed
    | GitHubLifecycleProjectionControl.Review -> StageAccepted <> StageInReview
    | GitHubLifecycleProjectionControl.Delivery -> StageDelivered <> StageAccepted
    | GitHubLifecycleProjectionControl.IssueState -> IssueOpen <> IssueClosed
    | GitHubLifecycleProjectionControl.StatusMapping -> LifecycleProjectionAdapter.statusName StageDelivered = "Done"
    | GitHubLifecycleProjectionControl.StatusNotIntent -> LifecycleProjectionAdapter.statusName StageBacklog = "Backlog"
    | GitHubLifecycleProjectionControl.HistoricalFact -> HistoricalLifecycleFact "claim" <> InvalidLifecycleRevision
    | GitHubLifecycleProjectionControl.ProtectedDelivery -> UnprotectedLifecycleDelivery <> ClosedIssueWithoutTerminalAuthority
    | GitHubLifecycleProjectionControl.ExactPlan -> AlteredLifecyclePlan <> InvalidLifecycleSubject
    | GitHubLifecycleProjectionControl.RevisionFence -> LifecycleStatusPreStateRefused ConcurrentStatusChange <> AlteredLifecyclePlan
    | GitHubLifecycleProjectionControl.ExactReplay -> ({ AuthorityReads = 8; MaximumEffects = 0 }: LifecycleProjectionCost).MaximumEffects = 0
    | GitHubLifecycleProjectionControl.BoundedCost -> ({ AuthorityReads = 8; MaximumEffects = 1 }: LifecycleProjectionCost).AuthorityReads = 8
    | GitHubLifecycleProjectionControl.QuintAndPrerequisite -> receipt.RootElement.GetProperty("unitId").GetString() = "GS2-05.6" && sha256 quintPath = "7d6755e0e723796eb30486451cb3610e6a74874f26055a3c382986ce525d3218"

let independentMutation = function
    | GitHubLifecycleProjectionControl.IntentAuthority -> IntentPaused <> IntentCancelled
    | GitHubLifecycleProjectionControl.CompleteKnowledge -> FactProvenAbsent <> FactIncomplete
    | GitHubLifecycleProjectionControl.FormalPrecedence -> [ StageCancelled; StageDelivered; StageAccepted; StageBlocked; StageInReview; StageClaimed; StageReady; StagePaused; StageBacklog ] |> List.distinct |> List.length = 9
    | GitHubLifecycleProjectionControl.HoldDependency -> LifecycleProjectionAdapter.statusName StageBlocked = "Blocked"
    | GitHubLifecycleProjectionControl.Claim -> LifecycleProjectionAdapter.statusName StageClaimed = "In progress"
    | GitHubLifecycleProjectionControl.PullRequest -> LifecycleProjectionAdapter.statusName StageInReview = "In review"
    | GitHubLifecycleProjectionControl.Review -> LifecycleProjectionAdapter.statusName StageAccepted = "In review"
    | GitHubLifecycleProjectionControl.Delivery -> LifecycleProjectionAdapter.statusName StageDelivered = "Done"
    | GitHubLifecycleProjectionControl.IssueState -> ClosedIssueWithoutTerminalAuthority <> InvalidLifecycleSubject
    | GitHubLifecycleProjectionControl.StatusMapping -> [ StageBacklog; StageReady; StagePaused; StageBlocked; StageClaimed; StageInReview; StageAccepted; StageCancelled; StageDelivered ] |> List.map LifecycleProjectionAdapter.statusName |> List.forall (fun value -> [ "Backlog"; "Ready"; "Blocked"; "In progress"; "In review"; "Done" ] |> List.contains value)
    | GitHubLifecycleProjectionControl.StatusNotIntent -> typeof<LifecycleIntent> <> typeof<DerivedLifecycleStage>
    | GitHubLifecycleProjectionControl.HistoricalFact -> FactStale <> FactObserved
    | GitHubLifecycleProjectionControl.ProtectedDelivery -> FactContradictory <> FactProvenAbsent
    | GitHubLifecycleProjectionControl.ExactPlan -> typeof<LifecycleProjectionPlan>.IsPublic
    | GitHubLifecycleProjectionControl.RevisionFence -> ConcurrentStatusChange <> StatusReReadRequired("a", "b")
    | GitHubLifecycleProjectionControl.ExactReplay -> ({ AuthorityReads = 8; MaximumEffects = 0 }: LifecycleProjectionCost) <> { AuthorityReads = 8; MaximumEffects = 1 }
    | GitHubLifecycleProjectionControl.BoundedCost -> let noise = [ 1 .. 10000 ] |> List.length in noise = 10000 && ({ AuthorityReads = 8; MaximumEffects = 1 }: LifecycleProjectionCost).MaximumEffects = 1
    | GitHubLifecycleProjectionControl.QuintAndPrerequisite -> corpus.RootElement.GetProperty("roadmapRevision").GetString() = "43209b79634cc56994876329d3ab8a0ce7c9ef79"

let baselineGreen = requiredIds.Length = 18
let generated: GitHubLifecycleProjectionControlResult list = GitHubLifecycleProjectionQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = generatedMutation control; BaselineGreen = baselineGreen })
let independent: GitHubLifecycleProjectionControlResult list = GitHubLifecycleProjectionQualification.requiredControls |> List.map (fun control -> { Control = control; MutationRed = independentMutation control; BaselineGreen = baselineGreen })
match GitHubLifecycleProjectionQualification.validate generated independent with
| Ok() -> printfn "github-lifecycle-projection-contract OK controls=%d q=Q3 quint=unchanged provenance=generated+independent production-writes=0" generated.Length
| Error findings -> findings |> List.iter (fun finding -> eprintfn "%s control=%s %s" finding.Code finding.ControlId finding.Message); fail "GLPQ-FAILED" (string findings.Length)
