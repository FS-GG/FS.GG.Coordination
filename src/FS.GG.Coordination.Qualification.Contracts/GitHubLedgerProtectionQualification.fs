namespace FS.GG.Coordination.Qualification.Contracts

type GitHubLedgerProtectionControl = AuthorityIdentity | ExactFleetRef | SelectorSemantics | RulesetSemantics | SharedWriterCarveOut | DedicatedWriterIdentity | NamespaceIntegrity | PhaseTagCreation | PhaseTagImmutability | EffectiveComposition | UnknownVsAbsent | ObservationFreshness | Pagination | ObservationDigest | Continuity | CompleteSeal | Tamper | ProtectedEnvironment | ControlIssue | NoApply
type GitHubLedgerProtectionControlResult = { Control: GitHubLedgerProtectionControl; Passed: bool }
type GitHubLedgerProtectionFinding = { Code: string; ControlId: string }
module GitHubLedgerProtectionQualification =
    let requiredControls = [ AuthorityIdentity; ExactFleetRef; SelectorSemantics; RulesetSemantics; SharedWriterCarveOut; DedicatedWriterIdentity; NamespaceIntegrity; PhaseTagCreation; PhaseTagImmutability; EffectiveComposition; UnknownVsAbsent; ObservationFreshness; Pagination; ObservationDigest; Continuity; CompleteSeal; Tamper; ProtectedEnvironment; ControlIssue; NoApply ]
    let controlId = function AuthorityIdentity->"authority-identity" | ExactFleetRef->"exact-fleet-ref" | SelectorSemantics->"selector-semantics" | RulesetSemantics->"ruleset-semantics" | SharedWriterCarveOut->"shared-writer-carve-out" | DedicatedWriterIdentity->"dedicated-writer-identity" | NamespaceIntegrity->"namespace-integrity" | PhaseTagCreation->"phase-tag-creation" | PhaseTagImmutability->"phase-tag-immutability" | EffectiveComposition->"effective-composition" | UnknownVsAbsent->"unknown-vs-absent" | ObservationFreshness->"observation-freshness" | Pagination->"pagination" | ObservationDigest->"observation-digest" | Continuity->"continuity" | CompleteSeal->"complete-seal" | Tamper->"tamper" | ProtectedEnvironment->"protected-environment" | ControlIssue->"control-issue" | NoApply->"no-apply"
    let validate generated independent =
        let validateSet source values =
            let groups = values |> List.groupBy (fun x -> controlId x.Control) |> Map.ofList
            [ for control in requiredControls do
                let id = controlId control
                match Map.tryFind id groups with
                | None -> { Code="LP-CONTROL-MISSING"; ControlId=source+":"+id }
                | Some xs when xs.Length <> 1 -> { Code="LP-CONTROL-DUPLICATE"; ControlId=source+":"+id }
                | Some [x] when not x.Passed -> { Code="LP-CONTROL-FAILED"; ControlId=source+":"+id }
                | _ -> () ]
        let findings = validateSet "generated" generated @ validateSet "independent" independent
        if findings.IsEmpty then Ok () else Error findings
