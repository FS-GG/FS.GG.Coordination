namespace FS.GG.Coordination.GitHub

open System

/// Native reads supplied by a protected runner. Each Git evidence callback must collect again;
/// the adapter does not cache an absent or installed journal observation.
type GenesisInstallerNativeReaders =
    {
        Now: unit -> DateTimeOffset
        ReadAbsentGit: unit -> Result<byte array, string>
        ReadInstalledGit: GitObjectId -> Result<byte array, string>
        ReadCutoverHead: unit -> Result<GitObjectId, string>
        ReadRef: string -> GenesisRefRead
        ReadTrustAnchor: unit -> Result<byte array, string>
        ReadSource: unit -> Result<GenesisSourceRead, string>
        ReadProtection: unit -> Result<GenesisProtectionRead, string>
        ReadApproval: int64 -> Result<GenesisProtectedNativeRead, string>
    }

/// Raw evidence collectors for the runner boundary. Every callback must perform a new native
/// read; no decoded observation or success result is retained by this adapter.
type GenesisInstallerRawReaders =
    {
        Now: unit -> DateTimeOffset
        ReadAbsentGit: unit -> Result<byte array, string>
        ReadInstalledGit: GitObjectId -> Result<byte array, string>
        ReadCutoverHead: unit -> Result<GitObjectId, string>
        ReadRef: string -> GenesisRefRead
        ReadTrustAnchor: unit -> Result<byte array, string>
        ReadSource: unit -> Result<byte array, string>
        ReadProtection: unit -> Result<byte array, string>
        ReadApproval: int64 -> Result<byte array, string>
    }

/// Already-scoped provider operations. This is a capability boundary, not credential discovery.
type GenesisInstallerObjectPort =
    {
        PutObject: string -> GitObjectId -> byte array -> Result<GitObjectId, string>
        ReadObject: string -> GitObjectId -> Result<byte array, string>
        CreateRefExpectedAbsent: string -> GitObjectId -> Result<unit, string>
    }

[<RequireQualifiedAccess>]
module V1AdmissionGenesisPortBinding =
    /// Decode native source, effective rules and protected approval on each read. This only
    /// constructs a port; it does not invoke the installer or grant a write capability.
    val createRaw:
        initialAbsentGit: ReadOnlyMemory<byte> ->
        plan: RegistryGenesisPlan ->
        native: GenesisInstallerRawReaders ->
        objects: GenesisInstallerObjectPort ->
            Result<GenesisInstallerPort, string list>

    /// Bind a fresh, verified preinstall snapshot to the exact plan. Every registry callback
    /// obtains independent native evidence, including after an ambiguous create response.
    val create:
        initialAbsentGit: ReadOnlyMemory<byte> ->
        plan: RegistryGenesisPlan ->
        native: GenesisInstallerNativeReaders ->
        objects: GenesisInstallerObjectPort ->
            Result<GenesisInstallerPort, string list>
