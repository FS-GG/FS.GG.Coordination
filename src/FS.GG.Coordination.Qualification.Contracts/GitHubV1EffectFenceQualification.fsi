namespace FS.GG.Coordination.Qualification.Contracts

type FenceCase =
    { Name: string
      FreshReadCount: int
      VerifiedBeforeEffect: bool
      EffectCount: int
      Outcome: string }

type FenceControl =
    | CompleteCaseSet
    | FreshReadPerEffect
    | VerifyBeforeFirstEffect
    | AtMostOneEffect
    | EveryRefusalHasZeroEffects
    | OperatingV1Admission
    | PreparingIncumbentOnly
    | ClosedPhaseRefusal
    | GenerationFencing
    | StrictEvidence
    | AuthorityFailureRefusal
    | OutcomeAlgebra
    | NoInstalledFenceClaim

type FenceQualification = { Controls: (FenceControl * bool) list; Findings: string list }

[<RequireQualifiedAccess>]
module GitHubV1EffectFenceQualification =
    val requiredCaseNames: string list
    val controlName: FenceControl -> string
    val qualify: FenceCase list -> FenceQualification
