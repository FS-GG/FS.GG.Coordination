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
    val qualify: AdmissionCase list -> AdmissionQualification
