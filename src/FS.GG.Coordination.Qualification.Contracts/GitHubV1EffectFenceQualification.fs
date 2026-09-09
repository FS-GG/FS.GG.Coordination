namespace FS.GG.Coordination.Qualification.Contracts

type FenceCase = { Name: string; FreshReadCount: int; VerifiedBeforeEffect: bool; EffectCount: int; Outcome: string }
type FenceControl =
    | CompleteCaseSet | FreshReadPerEffect | VerifyBeforeFirstEffect | AtMostOneEffect
    | EveryRefusalHasZeroEffects | OperatingV1Admission | PreparingIncumbentOnly | ClosedPhaseRefusal
    | GenerationFencing | StrictEvidence | AuthorityFailureRefusal | OutcomeAlgebra | NoInstalledFenceClaim
type FenceQualification = { Controls: (FenceControl * bool) list; Findings: string list }

[<RequireQualifiedAccess>]
module GitHubV1EffectFenceQualification =
    let requiredCaseNames =
        [ "operating-v1-new-applied"; "operating-v1-incumbent-applied"; "preparing-incumbent-applied"
          "preparing-new-refused"; "preparing-ineligible-refused"; "freeze-requested-refused"
          "rolling-back-refused"; "open-v2-refused"; "stale-authority-refused"; "cache-refused"
          "manifest-mismatch-refused"; "epoch-generation-refused"; "claim-generation-refused"
          "operation-generation-refused"; "unknown-field-refused"; "duplicate-field-refused"
          "unreadable-refused"; "contradictory-refused"; "effect-proven-absent"
          "effect-partial"; "effect-indeterminate" ]
    let controlName = function
        | CompleteCaseSet -> "complete-case-set" | FreshReadPerEffect -> "fresh-read-per-effect"
        | VerifyBeforeFirstEffect -> "verify-before-first-effect" | AtMostOneEffect -> "at-most-one-effect"
        | EveryRefusalHasZeroEffects -> "every-refusal-zero-effects" | OperatingV1Admission -> "operating-v1-admission"
        | PreparingIncumbentOnly -> "preparing-incumbent-only" | ClosedPhaseRefusal -> "closed-phase-refusal"
        | GenerationFencing -> "generation-fencing" | StrictEvidence -> "strict-evidence"
        | AuthorityFailureRefusal -> "authority-failure-refusal" | OutcomeAlgebra -> "outcome-algebra"
        | NoInstalledFenceClaim -> "no-installed-fence-claim"
    let qualify cases =
        let uniqueNames = cases |> List.map _.Name |> Set.ofList
        let find name = cases |> List.tryFind (fun value -> value.Name = name)
        let has name outcome effects = find name |> Option.exists (fun value -> value.Outcome = outcome && value.EffectCount = effects)
        let refusal name = has name "refused-before-effect" 0
        let controls =
            [ CompleteCaseSet, (cases.Length = requiredCaseNames.Length && uniqueNames = Set.ofList requiredCaseNames)
              FreshReadPerEffect, (cases |> List.forall (fun value -> value.FreshReadCount = 1))
              VerifyBeforeFirstEffect, (cases |> List.filter (fun value -> value.EffectCount = 1) |> List.forall _.VerifiedBeforeEffect)
              AtMostOneEffect, (cases |> List.forall (fun value -> value.EffectCount >= 0 && value.EffectCount <= 1))
              EveryRefusalHasZeroEffects, (cases |> List.filter (fun value -> value.Outcome = "refused-before-effect") |> List.forall (fun value -> value.EffectCount = 0))
              OperatingV1Admission, (has "operating-v1-new-applied" "applied" 1 && has "operating-v1-incumbent-applied" "applied" 1)
              PreparingIncumbentOnly, (has "preparing-incumbent-applied" "applied" 1 && refusal "preparing-new-refused" && refusal "preparing-ineligible-refused")
              ClosedPhaseRefusal, ([ "freeze-requested-refused"; "rolling-back-refused"; "open-v2-refused" ] |> List.forall refusal)
              GenerationFencing, ([ "epoch-generation-refused"; "claim-generation-refused"; "operation-generation-refused" ] |> List.forall refusal)
              StrictEvidence, ([ "unknown-field-refused"; "duplicate-field-refused" ] |> List.forall refusal)
              AuthorityFailureRefusal, ([ "stale-authority-refused"; "cache-refused"; "manifest-mismatch-refused"; "unreadable-refused"; "contradictory-refused" ] |> List.forall refusal)
              OutcomeAlgebra, (has "effect-proven-absent" "proven-absent" 1 && has "effect-partial" "partial" 1 && has "effect-indeterminate" "indeterminate" 1)
              NoInstalledFenceClaim, true ]
        { Controls = controls
          Findings = controls |> List.choose (fun (control, passed) -> if passed then None else Some(controlName control)) }
