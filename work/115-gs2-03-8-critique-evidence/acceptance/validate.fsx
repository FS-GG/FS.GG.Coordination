#load "../../../src/FS.GG.Coordination.Qualification.Contracts/CritiqueEvidence.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../.."))
let acceptance = Path.Combine(root, "work/115-gs2-03-8-critique-evidence/acceptance")
let evidencePath name = Path.Combine(acceptance, "evidence", name)
let findingPath name = Path.Combine(acceptance, "findings", name)
let bundlePath = Path.Combine(root, "evidence/github-substrate-v2/reviews/GS2-03.8.json")
let merge = "2427478b6fffba470e86ff46cf2ca22106a11a6d"
let pullRequestHead = "11a30b6d0914e79d7c0d23bd3532440f96c24431"
let subject = "3f33d551fc27099b9b1991f11dfc992354dfadbc87f461c833d760aeabc6f801"
let tree = "9f8867ce29ce3092d13fea6f5eed9aaf7badc83315821077131d7673f8e0d61e"
let unitContract = "c38b9ed4a473a99ee1bb6b43dddcafc06c153e6f1b698fbb44f45af018f52920"

let fail message = failwith $"CRITIQUE_ACCEPTANCE_INVALID %s{message}"
let require condition message = if not condition then fail message
let sha256 path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
let json path = JsonNode.Parse(File.ReadAllBytes path).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()

let prEvidence = json (evidencePath "pr-bootstrap-evidence.json")
require (text prEvidence "candidate" = pullRequestHead) "PR terminal evidence candidate differs"
require (text prEvidence "route" = "execute") "PR qualification did not execute"
require (text prEvidence "subjectSha256" = subject) "PR qualification subject differs"

let mainEvidence = json (evidencePath "main-bootstrap-evidence.json")
require (text mainEvidence "candidate" = merge) "protected-main terminal evidence candidate differs"
require (text mainEvidence "route" = "reuse") "protected-main qualification did not reuse"
require (text mainEvidence "subjectSha256" = subject) "protected-main qualification subject differs"
let mainPrior = mainEvidence["prior"].AsObject()
require (text mainPrior "head" = pullRequestHead) "protected-main prior head differs"
require (mainPrior["runId"].GetValue<int64>() = 33342488041L) "protected-main prior run differs"
require (text mainPrior "evidenceSha256" = sha256 (evidencePath "pr-bootstrap-evidence.json")) "protected-main prior evidence digest differs"

let reuse = json (evidencePath "main-reuse-decision.json")
require (text reuse "candidate" = merge) "reuse decision candidate differs"
require (text reuse "decision" = "reuse") "reuse decision is not reuse"
require (text reuse "reason" = "identical-complete-tree") "reuse decision reason differs"

let qualificationSubject = json (evidencePath "qualification-subject.json")
require (text qualificationSubject "treeSha256" = tree) "tracked-tree digest differs"
require (text qualificationSubject "subjectSha256" = subject) "qualification subject digest differs"

let candidate = json (evidencePath "roadmap-candidate.json")
require (text candidate "unitId" = "GS2-03.8") "roadmap candidate unit differs"
require (text candidate "candidateCommit" = pullRequestHead) "roadmap candidate head differs"

let gates = json (evidencePath "roadmap-gates.json")
require (text gates "unitId" = "GS2-03.8") "roadmap gate unit differs"
require (text gates "candidateCommit" = pullRequestHead) "roadmap gate candidate differs"
require (gates["stoppedAtUnitBoundary"].GetValue<bool>()) "roadmap gates crossed the unit boundary"
let gateResults = gates["results"].AsArray()
require (gateResults.Count = 2) "roadmap gate inventory differs"
require (gateResults |> Seq.forall (fun item -> item["exitCode"].GetValue<int>() = 0)) "a roadmap gate is not green"

let hosted = json (evidencePath "hosted-runs.json")
require (text hosted "protectedMerge" = merge) "hosted observation merge differs"
require (hosted["runs"].AsArray() |> Seq.forall (fun item -> item["conclusion"].GetValue<string>() = "success")) "a hosted run is not green"

let fingerprint id name =
    { Id = id
      Sha256 = sha256 (evidencePath name) }

let completed second = DateTimeOffset(2026, 8, 31, 0, 5, second, TimeSpan.Zero)

let finding id perspective phase name second =
    { Id = id
      Perspective = perspective
      PhaseId = phase
      Author = "codex-accountable-delivery-owner"
      Decision = CritiqueDecision.Passed
      ContentSha256 = sha256 (findingPath name)
      CompletedAt = completed second }

let input =
    { Candidate =
        { CommitSha = merge
          TreeSha256 = tree
          UnitContractSha256 = unitContract }
      Evidence =
        [ fingerprint "hosted-runs" "hosted-runs.json"
          fingerprint "main-bootstrap-evidence" "main-bootstrap-evidence.json"
          fingerprint "main-reuse-decision" "main-reuse-decision.json"
          fingerprint "pr-bootstrap-evidence" "pr-bootstrap-evidence.json"
          fingerprint "qualification-subject" "qualification-subject.json"
          fingerprint "roadmap-candidate" "roadmap-candidate.json"
          fingerprint "roadmap-gates" "roadmap-gates.json" ]
      AccountableOwner = "codex-accountable-delivery-owner"
      Findings =
        [ finding "gs2-03-8-architecture" CritiquePerspective.Architecture "gs2-03-8-architecture-acceptance" "architecture.md" 5
          finding "gs2-03-8-security" CritiquePerspective.Security "gs2-03-8-security-acceptance" "security.md" 6
          finding "gs2-03-8-adapter" CritiquePerspective.Adapter "gs2-03-8-adapter-acceptance" "adapter.md" 7
          finding "gs2-03-8-migration" CritiquePerspective.Migration "gs2-03-8-migration-acceptance" "migration.md" 8
          finding "gs2-03-8-cutover" CritiquePerspective.Cutover "gs2-03-8-cutover-acceptance" "cutover.md" 9 ]
      CreatedAt = completed 9 }

match CritiqueEvidence.generate input with
| Error findings -> fail (sprintf "generation failed: %A" findings)
| Ok expected when fsi.CommandLineArgs |> Array.contains "--generate" ->
    Directory.CreateDirectory(Path.GetDirectoryName bundlePath) |> ignore
    File.WriteAllBytes(bundlePath, expected)
    printfn "CRITIQUE_ACCEPTANCE_GENERATED path=%s sha256=%s" (Path.GetRelativePath(root, bundlePath)) (sha256 bundlePath)
| Ok _ ->
    require (File.Exists bundlePath) "critique bundle is missing"
    match CritiqueEvidence.validate input (ReadOnlyMemory<byte>(File.ReadAllBytes bundlePath)) with
    | Error findings -> fail (sprintf "bundle validation failed: %A" findings)
    | Ok summary ->
        require (summary.Outcome = "passed") "derived outcome is not passed"
        printfn "CRITIQUE_ACCEPTANCE_OK candidate=%s evidenceSet=%s findingSet=%s digest=%s" merge summary.EvidenceSetSha256 summary.FindingSetSha256 summary.Digest
