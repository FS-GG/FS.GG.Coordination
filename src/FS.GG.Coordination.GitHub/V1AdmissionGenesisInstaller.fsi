namespace FS.GG.Coordination.GitHub

open System

type GenesisSourceRead =
    {
        ObservedAt: DateTimeOffset
        RepositoryId: int64
        Commit: GitObjectId
        Tree: GitObjectId
        IsOnMain: bool
    }

type GenesisRefRead =
    | GenesisRefAbsent
    | GenesisRefAt of GitObjectId
    | GenesisRefUnknown of string

/// A freshly decoded, complete effective-rules and App credential observation. The live reader
/// must derive these fields from native repository rulesets and App installation/token endpoints.
type GenesisProtectionRead =
    {
        ObservedAt: DateTimeOffset
        RepositoryId: int64
        WriterRulesetId: int64
        WriterRulesetActive: bool
        WriterRulesetMatchesRef: bool
        WriterBypassAppIds: int64 list
        IntegrityRulesetId: int64
        IntegrityRulesetActive: bool
        IntegrityRulesetMatchesRef: bool
        IntegrityRejectsDeletion: bool
        IntegrityRejectsNonFastForward: bool
        IntegrityBypassAppIds: int64 list
        CredentialAppId: int64
        CredentialInstallationId: int64
        CredentialRepositoryIds: int64 list
        CredentialContentsWrite: bool
        CredentialHasOtherWritePermissions: bool
    }

/// This port is exclusive to the protected one-time installer; it must not be used by ordinary
/// production commands. All reads are native provider reads, including independent readback.
type GenesisInstallerPort =
    {
        Now: unit -> DateTimeOffset
        Authority: AuthorityGitPort
        ReadRegistry: AggregateAddress -> Result<RegistryJournalRead, string>
        ReadRef: string -> GenesisRefRead
        ReadTrustAnchor: unit -> Result<byte array, string>
        ReadSource: unit -> Result<GenesisSourceRead, string>
        ReadProtection: unit -> Result<GenesisProtectionRead, string>
        ReadApproval: int64 -> Result<GenesisProtectedNativeRead, string>
        PutObject: string -> GitObjectId -> byte array -> Result<GitObjectId, string>
        ReadObject: string -> GitObjectId -> Result<byte array, string>
        CreateRefExpectedAbsent: string -> GitObjectId -> Result<unit, string>
    }

type GenesisInstallOutcome =
    | GenesisInstalled
    | GenesisAlreadyInstalled
    | GenesisInstallRefused of string list
    | GenesisInstallIndeterminate of string list

[<RequireQualifiedAccess>]
module V1AdmissionGenesisInstaller =
    val apply:
        GenesisInstallerPort ->
        RegistryGenesisPlan ->
        GenesisAuthorizationIntent ->
        GenesisSignature ->
            GenesisInstallOutcome
