namespace FS.GG.Coordination.Qualification.Contracts

type AdmissionCase =
    {
        Name: string
        Outcome: string
        JournalAppends: int
        ProviderEffects: int
    }

type AdmissionQualification =
    {
        RequiredCases: string list
        Findings: string list
    }

[<RequireQualifiedAccess>]
module GitHubV1AdmissionQualification =
    let qualify cases =
        let required =
            [
                "admission-seal-cas-race"
                "lost-admission-response-restart"
                "sealed-member-preparing"
                "sealed-nonmember-refused"
                "claim-generation-replaced"
                "operation-generation-replaced"
                "changed-manifest-refused"
                "changed-target-refused"
                "changed-payload-refused"
                "changed-receiver-refused"
                "changed-generation-refused"
                "two-workers-one-effect"
                "crash-before-send"
                "crash-after-send-indeterminate"
                "proven-absence-fresh-retry"
                "partial-no-retry"
                "freeze-between-effects"
                "read-only-no-admission"
                "mutation-as-read-refused"
                "legacy-open-adoption"
                "legacy-preparing-refused"
                "closing-drains-member"
                "closing-refuses-new"
                "real-initializer-git-objects"
            ]

        let names = cases |> List.map _.Name
        let findings = ResizeArray<string>()

        if names <> required then
            findings.Add "case-order-or-completeness"

        if names |> Set.ofList |> Set.count <> names.Length then
            findings.Add "duplicate-case"

        for value in cases do
            if value.Outcome <> "passed" then
                findings.Add("case-failed:" + value.Name)

            if
                value.JournalAppends < 0
                || value.ProviderEffects < 0
                || value.ProviderEffects > 1
            then
                findings.Add("invalid-count:" + value.Name)

        for name in
            [
                "changed-manifest-refused"
                "changed-target-refused"
                "changed-payload-refused"
                "changed-receiver-refused"
                "changed-generation-refused"
                "mutation-as-read-refused"
                "legacy-preparing-refused"
                "closing-refuses-new"
            ] do
            match cases |> List.tryFind (fun value -> value.Name = name) with
            | Some value when value.ProviderEffects = 0 -> ()
            | _ -> findings.Add("refusal-effect:" + name)

        {
            RequiredCases = required
            Findings = List.ofSeq findings
        }
