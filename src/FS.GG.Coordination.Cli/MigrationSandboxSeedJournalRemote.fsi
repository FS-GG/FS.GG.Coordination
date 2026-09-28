namespace FS.GG.Coordination.Cli

open System

/// Opaque proof of an installed protected S2 binding. Current source-only S2 bytes cannot create it.
type MigrationSandboxSeedInstalledS2Binding = private MigrationSandboxSeedInstalledS2Binding of byte array

/// Opaque proof of exact installed journal rulesets and an authenticated protected readback.
/// The current candidate policy and its public redacted readback cannot create this value.
type MigrationSandboxSeedInstalledJournalPolicy = private MigrationSandboxSeedInstalledJournalPolicy of byte array

/// Exact retained inputs presented to a separately installed protected provenance verifier.
type MigrationSandboxSeedInstalledProvenanceEvidence =
    {
        BindingBytes: byte array
        PolicyReadbackBytes: byte array
        WorkflowRunId: int64
        WorkflowRunAttempt: int
        WorkflowSha: string
        ApprovedArtifactSourceSha256: string
    }

/// This port has no implementation in the current source packet. A future implementation must
/// execute in the protected host and verify native workflow/run provenance, immutable artifact
/// custody and the authenticated, non-redacted installed ruleset readback.
type IMigrationSandboxSeedInstalledProvenanceVerifier =
    abstract VerifyExact: MigrationSandboxSeedInstalledProvenanceEvidence -> bool

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
    | InvalidInstalledBinding
    | InvalidInstalledPolicy
    | InstalledProvenanceRejected
    | InstalledBindingMismatch
    | InvalidJournalProposal

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournalRemote =
    /// Join exact document validation to an independent installed provenance verifier. Canonical
    /// JSON and recomputable fingerprints alone cannot construct either opaque capability.
    val establishInstalledAuthority:
        verifier: IMigrationSandboxSeedInstalledProvenanceVerifier ->
        evidence: MigrationSandboxSeedInstalledProvenanceEvidence ->
            Result<
                MigrationSandboxSeedInstalledS2Binding * MigrationSandboxSeedInstalledJournalPolicy,
                MigrationSandboxSeedRemoteFailure
             >

    /// Push one exact S1 proposal, then reread for every outcome. Both installed receipts are
    /// required. A lost response with the old snapshot still present yields JournalRetryOnly;
    /// no result from this module is a native effect permit.
    val writeAndRead:
        installed: MigrationSandboxSeedInstalledS2Binding ->
        policy: MigrationSandboxSeedInstalledJournalPolicy ->
        previous: MigrationSandboxSeedJournalSnapshot option ->
        proposal: MigrationSandboxSeedJournalPlan ->
        transport: IMigrationSandboxSeedJournalRemoteTransport ->
            Result<MigrationSandboxSeedRemoteResult, MigrationSandboxSeedRemoteFailure>
