namespace FS.GG.Coordination.GitHub

type AdmissionServicePorts =
    {
        Authority: AuthorityGitPort
        Journal: RegistryJournalPort
    }

type AdmissionServiceDecision =
    | AdmissionDurablyAppended of OperationHandle * GitObjectId
    | AdmissionAlreadyDurable of OperationHandle * GitObjectId
    | AdmissionParentConflict of GitObjectId option
    | AdmissionServiceRefused of string list
    | AdmissionServiceIndeterminate of string list

[<RequireQualifiedAccess>]
module V1AdmissionService =
    /// Admit exactly one caller-inventoried v1 operation through fresh fleet authority and
    /// a restored admission journal. This port cannot create the genesis ref or send a provider effect.
    val admit: AdmissionServicePorts -> MutationContext -> AdmissionServiceDecision
