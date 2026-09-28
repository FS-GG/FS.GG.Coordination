namespace FS.GG.Coordination.Cli

open System

/// Opaque proof of the installed, protected isolated-CAS profile. Current source-only S2 bytes,
/// self-asserted JSON and repository-ruleset evidence cannot construct this capability.
type MigrationSandboxSeedIsolatedCasAuthority = private MigrationSandboxSeedIsolatedCasAuthority of byte array

/// Journal-only admission for the exact generation-zero S1 proposal. It cannot authorize a
/// seed provider effect or a later journal generation.
type MigrationSandboxSeedBootstrapAdmission =
    private
    | MigrationSandboxSeedBootstrapAdmission of bindingBytes: byte array * admissionBytes: byte array

[<RequireQualifiedAccess>]
type MigrationSandboxSeedRemotePushOutcome =
    | Accepted
    | ParentConflict
    | DefiniteRefusal of reason: string
    | ResponseUnknown

type MigrationSandboxSeedRemoteObject =
    {
        Kind: string
        Oid: string
        Bytes: byte array
    }

type MigrationSandboxSeedRemotePush =
    {
        RefName: string
        Refspec: string
        ForceWithLease: string
        Objects: MigrationSandboxSeedRemoteObject list
    }

type IMigrationSandboxSeedJournalRemoteTransport =
    /// Upload exactly Objects, then issue one authenticated push containing only Refspec and
    /// ForceWithLease. Implementations must reject force, deletion, mirror and extra refspecs.
    abstract PushExact: MigrationSandboxSeedRemotePush -> MigrationSandboxSeedRemotePushOutcome

    /// Fetch the named remote ref and independently read its commit, tree and blob bytes.
    /// A process-local object cache or pre-push observation does not satisfy this read.
    abstract ReadFresh: refName: string -> MigrationSandboxSeedJournalRead

[<RequireQualifiedAccess>]
type MigrationSandboxSeedRemoteResult =
    | Applied of MigrationSandboxSeedJournalRestore
    | JournalRetryOnly of reason: string
    | Conflict
    | Refused of reason: string
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type MigrationSandboxSeedRemoteFailure =
    | InvalidIsolatedCasBinding
    | IsolatedProvenanceRejected
    | IsolatedBindingMismatch
    | InvalidJournalProposal

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournalRemote =
    /// Validate the immutable, inactive S2 declaration before the nonce ref exists.
    val establishBootstrapAdmission:
        verifier: IMigrationSandboxSeedIsolatedProvenanceVerifier ->
        evidence: MigrationSandboxSeedIsolatedProvenanceEvidence ->
        proposal: MigrationSandboxSeedJournalPlan ->
            Result<MigrationSandboxSeedBootstrapAdmission, MigrationSandboxSeedRemoteFailure>

    /// Join retained S2 bytes and native nonce-ref CAS readback to the independent protected-host
    /// verifier. Seed-journal rulesets are not an input: GS2-08.2 owns production protection proof.
    val establishIsolatedCasAuthority:
        verifier: IMigrationSandboxSeedIsolatedProvenanceVerifier ->
        evidence: MigrationSandboxSeedIsolatedProvenanceEvidence ->
            Result<MigrationSandboxSeedIsolatedCasAuthority, MigrationSandboxSeedRemoteFailure>

    /// Install only the exact generation-zero journal proposal under an expected-absence lease.
    /// Success is still a journal readback, never seed issue/Project dispatch authority.
    val writeGenesisAndRead:
        admission: MigrationSandboxSeedBootstrapAdmission ->
        proposal: MigrationSandboxSeedJournalPlan ->
        transport: IMigrationSandboxSeedJournalRemoteTransport ->
            Result<MigrationSandboxSeedRemoteResult, MigrationSandboxSeedRemoteFailure>

    /// Push one exact S1 proposal, then reread for every outcome. The installed isolated-CAS
    /// capability is required. A lost response with the old snapshot still present yields JournalRetryOnly;
    /// no result from this module is a native effect permit.
    val writeAndRead:
        authority: MigrationSandboxSeedIsolatedCasAuthority ->
        previous: MigrationSandboxSeedJournalSnapshot ->
        proposal: MigrationSandboxSeedJournalPlan ->
        transport: IMigrationSandboxSeedJournalRemoteTransport ->
            Result<MigrationSandboxSeedRemoteResult, MigrationSandboxSeedRemoteFailure>
