namespace FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module V1AdmissionJournalCasPlan =
    /// Encode only the canonical operation-journal append proposal as a bounded,
    /// public CAS plan. This is not a writer credential or permission to push.
    val encode: RegistryAppendProposal -> Result<byte array, string list>
