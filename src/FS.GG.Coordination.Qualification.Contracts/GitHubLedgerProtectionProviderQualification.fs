namespace FS.GG.Coordination.Qualification.Contracts

type GitHubLedgerProtectionProviderControl =
    | ProviderBinding | PageOrdering | CompletePagination | PayloadDigest | ProviderFreshness
    | ProviderUnknownVsAbsent | ProviderEffectiveComposition | ProviderContinuity | ProviderExactFleetRef
    | DryOperationSeal | DedicatedWriterBlocker | ProviderNoApply
type GitHubLedgerProtectionProviderControlResult = { Control: GitHubLedgerProtectionProviderControl; Passed: bool }
type GitHubLedgerProtectionProviderFinding = { Code: string; ControlId: string }

module GitHubLedgerProtectionProviderQualification =
    let requiredControls =
        [ ProviderBinding; PageOrdering; CompletePagination; PayloadDigest; ProviderFreshness
          ProviderUnknownVsAbsent; ProviderEffectiveComposition; ProviderContinuity; ProviderExactFleetRef
          DryOperationSeal; DedicatedWriterBlocker; ProviderNoApply ]
    let controlId = function
        | ProviderBinding -> "provider-binding" | PageOrdering -> "page-ordering"
        | CompletePagination -> "complete-pagination" | PayloadDigest -> "payload-digest"
        | ProviderFreshness -> "provider-freshness" | ProviderUnknownVsAbsent -> "provider-unknown-vs-absent"
        | ProviderEffectiveComposition -> "provider-effective-composition" | ProviderContinuity -> "provider-continuity"
        | ProviderExactFleetRef -> "provider-exact-fleet-ref" | DryOperationSeal -> "dry-operation-seal"
        | DedicatedWriterBlocker -> "dedicated-writer-blocker" | ProviderNoApply -> "provider-no-apply"
    let validate generated independent =
        let validateSet source values =
            let groups = values |> List.groupBy (fun x -> controlId x.Control) |> Map.ofList
            [ for control in requiredControls do
                let id = controlId control
                match Map.tryFind id groups with
                | None -> { Code="LPP-CONTROL-MISSING"; ControlId=source+":"+id }
                | Some xs when xs.Length <> 1 -> { Code="LPP-CONTROL-DUPLICATE"; ControlId=source+":"+id }
                | Some [value] when not value.Passed -> { Code="LPP-CONTROL-FAILED"; ControlId=source+":"+id }
                | _ -> () ]
        let findings = validateSet "generated" generated @ validateSet "independent" independent
        if findings.IsEmpty then Ok () else Error findings
