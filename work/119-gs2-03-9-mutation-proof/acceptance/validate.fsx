#load "../../../src/FS.GG.Coordination.Qualification.Contracts/QualificationManifest.fs"
#load "../../../src/FS.GG.Coordination.Qualification.Contracts/HarnessMutationProof.fs"
#load "../../../src/FS.GG.Coordination.Qualification.Contracts/CritiqueEvidence.fs"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open FS.GG.Coordination.Qualification.Contracts

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../.."))
let acceptance = Path.Combine(root, "work/119-gs2-03-9-mutation-proof/acceptance")
let evidencePath name = Path.Combine(acceptance, "evidence", name)
let findingPath name = Path.Combine(acceptance, "findings", name)
let proofPath = Path.Combine(root, "evidence/github-substrate-v2/mutation-proofs/GS2-03.9.json")
let reviewPath = Path.Combine(root, "evidence/github-substrate-v2/reviews/GS2-03.9.json")
let inventoryPath = Path.Combine(root, "evidence/github-substrate-v2/qualification-inventories/GS2-03.1.json")
let baselinePath = Path.Combine(root, "evidence/github-substrate-v2/qualification-manifests/GS2-03.1.json")
let merge = "53f0338dea988fd79b95092286709df7c0fb4745"
let pullRequestHead = "fa1426cdd80697d54ad179c243bea50f706ec25a"
let subject = "d16d4c29f251442ce15a61abe44a4025383a8f42ca555e8e96f03e7eb09c9727"
let tree = "4704c92d7a937fae38b023c388333f52ac787c94e588b9cf4ade35676189c92a"
let unitContract = "acb013dd87697c21886dca39fa9ca97ff48e24402e964000cdc1d4c4645be40b"

let fail message = failwith $"GS2_03_9_ACCEPTANCE_INVALID %s{message}"
let require condition message = if not condition then fail message
let sha256Bytes (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let sha256 path = File.ReadAllBytes path |> sha256Bytes
let json path = JsonNode.Parse(File.ReadAllBytes path).AsObject()
let text (node: JsonObject) (name: string) = node[name].GetValue<string>()

let validatorBoundary =
    [ "src/FS.GG.Coordination.Qualification.Contracts/HarnessMutationProof.fs"
      "src/FS.GG.Coordination.Qualification.Contracts/QualificationManifest.fs" ]
    |> List.map (fun path -> $"%s{path}:%s{sha256 (Path.Combine(root, path))}")
    |> String.concat "\n"
    |> fun value -> sha256Bytes (Encoding.UTF8.GetBytes(value + "\n"))

let proofContext =
    { CandidateCommit = merge
      CandidateTreeSha256 = tree
      UnitContractSha256 = unitContract
      ValidatorSha256 = validatorBoundary }

let inventory = File.ReadAllBytes inventoryPath
let baseline = File.ReadAllBytes baselinePath
let generatedProof =
    HarnessMutationProof.generate proofContext (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(baseline))
    |> Result.defaultWith (failwithf "proof generation failed: %A")

let generating = fsi.CommandLineArgs |> Array.contains "--generate"
if generating then
    Directory.CreateDirectory(Path.GetDirectoryName proofPath) |> ignore
    File.WriteAllBytes(proofPath, generatedProof)

require (File.Exists proofPath) "retained mutation proof is missing"
match HarnessMutationProof.validate proofContext (ReadOnlyMemory<byte>(inventory)) (ReadOnlyMemory<byte>(baseline)) (ReadOnlyMemory<byte>(File.ReadAllBytes proofPath)) with
| Error findings -> fail (sprintf "mutation proof validation failed: %A" findings)
| Ok _ -> ()

let proof = json proofPath
require (text proof "candidateCommit" = merge) "mutation proof candidate differs"
require (text proof "candidateTreeSha256" = tree) "mutation proof tracked tree differs"
require (text proof "validatorSha256" = validatorBoundary) "mutation proof validator boundary differs"
require (proof["controls"].AsArray().Count = 10) "mutation proof control inventory differs"
require (proof["observations"].AsArray().Count = 60) "mutation proof observation inventory differs"
require (proof["controls"].AsArray() |> Seq.forall (fun item -> item["outcome"].GetValue<string>() = "passed")) "a mutation control is not green"
require (proof["observations"].AsArray() |> Seq.forall (fun item -> item["outcome"].GetValue<string>() = "rejected")) "a negative mutation is not red"

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
require (mainPrior["runId"].GetValue<int64>() = 33348201595L) "protected-main prior run differs"
require (text mainPrior "evidenceSha256" = sha256 (evidencePath "pr-bootstrap-evidence.json")) "protected-main prior evidence digest differs"

let reuse = json (evidencePath "main-reuse-decision.json")
require (text reuse "candidate" = merge) "reuse decision candidate differs"
require (text reuse "decision" = "reuse") "reuse decision is not reuse"
require (text reuse "reason" = "identical-complete-tree") "reuse decision reason differs"

let qualificationSubject = json (evidencePath "qualification-subject.json")
require (text qualificationSubject "treeSha256" = tree) "tracked-tree digest differs"
require (text qualificationSubject "subjectSha256" = subject) "qualification subject digest differs"

let candidate = json (evidencePath "roadmap-candidate.json")
require (text candidate "unitId" = "GS2-03.9") "roadmap candidate unit differs"
require (text candidate "candidateCommit" = pullRequestHead) "roadmap candidate head differs"
let artifactNames = candidate["artifacts"].AsArray() |> Seq.map (fun item -> item["name"].GetValue<string>()) |> Set.ofSeq
for requiredArtifact in [ "formal-validator"; "formal-observability-tests"; "proof-contract"; "sdd-evidence"; "gate-catalog" ] do
    require (Set.contains requiredArtifact artifactNames) $"roadmap candidate omits %s{requiredArtifact}"

let gates = json (evidencePath "roadmap-gates.json")
require (text gates "unitId" = "GS2-03.9") "roadmap gate unit differs"
require (text gates "candidateCommit" = pullRequestHead) "roadmap gate candidate differs"
require (gates["stoppedAtUnitBoundary"].GetValue<bool>()) "roadmap gates crossed the unit boundary"
let gateResults = gates["results"].AsArray()
require (gateResults.Count = 2) "roadmap gate inventory differs"
require (gateResults |> Seq.forall (fun item -> item["exitCode"].GetValue<int>() = 0)) "a roadmap gate is not green"

let formal = json (evidencePath "hosted-formal-qualification.json")
require (text formal "q1Outcome" = "passed" && text formal "q2Outcome" = "passed") "hosted formal phases differ"
require (formal["positiveInvariantCount"].GetValue<int>() = 8) "hosted positive invariant count differs"
require (formal["negativeControlCount"].GetValue<int>() = 126) "hosted negative control count differs"
require (formal["formalCounterexamples"].AsArray().Count = 11) "hosted counterexample inventory differs"
let processCounts = formal["processCounts"].AsObject()
require (processCounts["external"].GetValue<int>() = 186) "hosted external process census differs"
require (processCounts["quintCli"].GetValue<int>() = 161) "hosted Quint process census differs"
require (processCounts["apalacheVerify"].GetValue<int>() = 47) "hosted verify process census differs"
require (isNull formal["failure"]) "hosted formal receipt records a failure"
let formalGate = prEvidence["gates"].AsArray() |> Seq.find (fun item -> item["id"].GetValue<string>() = "canonical-quint")
require (text (formalGate.AsObject()) "sha256" = sha256 (evidencePath "hosted-formal-qualification.json")) "terminal evidence does not bind retained formal receipt"

let hosted = json (evidencePath "hosted-runs.json")
require (text hosted "protectedMerge" = merge) "hosted observation merge differs"
let runs = hosted["runs"].AsArray()
require (runs.Count = 4) "hosted run inventory differs"
require (runs |> Seq.forall (fun item -> item["conclusion"].GetValue<string>() = "success")) "a hosted run is not green"

let fingerprint id name =
    { Id = id
      Sha256 = sha256 (evidencePath name) }

let completed second = DateTimeOffset(2026, 8, 31, 2, 3, second, TimeSpan.Zero)
let finding id perspective phase name second =
    { Id = id
      Perspective = perspective
      PhaseId = phase
      Author = "codex-accountable-delivery-owner"
      Decision = CritiqueDecision.Passed
      ContentSha256 = sha256 (findingPath name)
      CompletedAt = completed second }

let critiqueInput =
    { Candidate =
        { CommitSha = merge
          TreeSha256 = tree
          UnitContractSha256 = unitContract }
      Evidence =
        [ fingerprint "hosted-formal-qualification" "hosted-formal-qualification.json"
          fingerprint "hosted-runs" "hosted-runs.json"
          fingerprint "main-bootstrap-evidence" "main-bootstrap-evidence.json"
          fingerprint "main-reuse-decision" "main-reuse-decision.json"
          fingerprint "pr-bootstrap-evidence" "pr-bootstrap-evidence.json"
          fingerprint "qualification-subject" "qualification-subject.json"
          fingerprint "roadmap-candidate" "roadmap-candidate.json"
          fingerprint "roadmap-gates" "roadmap-gates.json"
          { Id = "mutation-proof"; Sha256 = sha256 proofPath } ]
      AccountableOwner = "codex-accountable-delivery-owner"
      Findings =
        [ finding "gs2-03-9-architecture" CritiquePerspective.Architecture "gs2-03-9-architecture-acceptance" "architecture.md" 5
          finding "gs2-03-9-security" CritiquePerspective.Security "gs2-03-9-security-acceptance" "security.md" 6
          finding "gs2-03-9-adapter" CritiquePerspective.Adapter "gs2-03-9-adapter-acceptance" "adapter.md" 7
          finding "gs2-03-9-migration" CritiquePerspective.Migration "gs2-03-9-migration-acceptance" "migration.md" 8
          finding "gs2-03-9-cutover" CritiquePerspective.Cutover "gs2-03-9-cutover-acceptance" "cutover.md" 9 ]
      CreatedAt = completed 9 }

match CritiqueEvidence.generate critiqueInput with
| Error findings -> fail (sprintf "critique generation failed: %A" findings)
| Ok expected when generating ->
    Directory.CreateDirectory(Path.GetDirectoryName reviewPath) |> ignore
    File.WriteAllBytes(reviewPath, expected)
| Ok _ -> ()

require (File.Exists reviewPath) "retained critique bundle is missing"
match CritiqueEvidence.validate critiqueInput (ReadOnlyMemory<byte>(File.ReadAllBytes reviewPath)) with
| Error findings -> fail (sprintf "critique validation failed: %A" findings)
| Ok summary ->
    require (summary.Outcome = "passed") "derived critique outcome is not passed"
    printfn "GS2_03_9_ACCEPTANCE_OK candidate=%s proof=%s validatorBoundary=%s critique=%s evidenceSet=%s findingSet=%s" merge (sha256 proofPath) validatorBoundary summary.Digest summary.EvidenceSetSha256 summary.FindingSetSha256
