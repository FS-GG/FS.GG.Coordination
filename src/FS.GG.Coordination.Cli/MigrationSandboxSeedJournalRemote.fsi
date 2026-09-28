namespace FS.GG.Coordination.Cli

open System

/// Opaque proof of an installed protected S2 binding. Current source-only S2 bytes cannot create it.
type MigrationSandboxSeedInstalledS2Binding = private MigrationSandboxSeedInstalledS2Binding of byte array

/// Opaque proof of exact installed journal rulesets and an authenticated protected readback.
/// The current candidate policy and its public redacted readback cannot create this value.
type MigrationSandboxSeedInstalledJournalPolicy = private MigrationSandboxSeedInstalledJournalPolicy of byte array

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
    | InstalledBindingMismatch
    | InvalidJournalProposal

[<RequireQualifiedAccess>]
module MigrationSandboxSeedJournalRemote =
    /// Validate the exact byte contract expected from a future activation-true installed S2
    /// producer. The present source-only S2 document is deliberately rejected. The permission
    /// ceiling excludes ruleset administration; policy installation uses separate authority.
    val authenticateInstalledS2:
        bindingBytes: ReadOnlyMemory<byte> ->
            Result<MigrationSandboxSeedInstalledS2Binding, MigrationSandboxSeedRemoteFailure>

    val authenticateInstalledPolicy:
        policyBytes: ReadOnlyMemory<byte> ->
            Result<MigrationSandboxSeedInstalledJournalPolicy, MigrationSandboxSeedRemoteFailure>

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
