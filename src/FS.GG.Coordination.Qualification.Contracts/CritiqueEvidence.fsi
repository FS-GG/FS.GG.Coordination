namespace FS.GG.Coordination.Qualification.Contracts

open System

[<RequireQualifiedAccess>]
type CritiquePerspective =
    | Architecture
    | Security
    | Adapter
    | Migration
    | Cutover

[<RequireQualifiedAccess>]
type CritiqueDecision =
    | Passed
    | ChangesRequired

type CritiqueCandidate =
    { CommitSha: string
      TreeSha256: string
      UnitContractSha256: string }

type CritiqueEvidenceFingerprint =
    { Id: string
      Sha256: string }

type CritiqueFindingInput =
    { Id: string
      Perspective: CritiquePerspective
      PhaseId: string
      Author: string
      Decision: CritiqueDecision
      ContentSha256: string
      CompletedAt: DateTimeOffset }

type CritiqueEvidenceInput =
    { Candidate: CritiqueCandidate
      Evidence: CritiqueEvidenceFingerprint list
      AccountableOwner: string
      Findings: CritiqueFindingInput list
      CreatedAt: DateTimeOffset }

type CritiqueEvidenceSummary =
    { CandidateFingerprintSha256: string
      EvidenceSetSha256: string
      FindingSetSha256: string
      Outcome: string
      Digest: string }

type CritiqueEvidenceFinding =
    { Code: string
      Path: string
      Expected: string
      Actual: string }

[<RequireQualifiedAccess>]
module CritiqueEvidence =
    [<Literal>]
    val Schema: string = "fsgg.coordination.critique-evidence/1"

    val generate:
        input: CritiqueEvidenceInput -> Result<byte array, CritiqueEvidenceFinding list>

    val validate:
        expected: CritiqueEvidenceInput -> artifact: ReadOnlyMemory<byte> -> Result<CritiqueEvidenceSummary, CritiqueEvidenceFinding list>
