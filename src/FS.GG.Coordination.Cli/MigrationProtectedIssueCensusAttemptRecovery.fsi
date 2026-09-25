namespace FS.GG.Coordination.Cli

type ProtectedIssueCensusNativeAttemptPins =
    { AttemptResourceId: string
      AttemptArtifactSha256: string }

type ProtectedIssueCensusNativeAttemptDescription =
    { AttemptResourceId: string
      AttemptArtifactSha256: string
      NativeAttemptNamespaceId: string
      VaultResourceId: string
      VaultArtifactSha256: string
      CandidateMayRead: bool
      CandidateMayWrite: bool
      AuthoritativeCompleteReadback: bool }

type ProtectedIssueCensusNativeAttemptPhase =
    | InvocationUnknown
    | TokenVaulted
    | NativeRevoked

type ProtectedIssueCensusNativeAttemptRecord =
    { Request: ProtectedIssueCensusHandoffRequest
      ProviderAttemptId: string
      VaultResourceId: string
      Phase: ProtectedIssueCensusNativeAttemptPhase
      TokenFingerprintSha256: string option
      RevocationReceiptSha256: string option }

type ProtectedIssueCensusRecoveryHold =
    | NativeResultUnknown
    | NativeRevocationRequired
    | ProtectedReceiptRequired

/// Read-only fake port. It must come from a protected native attempt authority,
/// never from candidate files or a caller-supplied provider result.
type IProtectedIssueCensusNativeAttemptPort =
    abstract Describe: unit -> ProtectedIssueCensusNativeAttemptDescription
    abstract ReadAttempts: string -> ProtectedIssueCensusNativeAttemptRecord list option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusAttemptRecovery =
    /// Every successful classification is still a hold; no provider retry or token action.
    val inspect:
        handoffPins:ProtectedIssueCensusHandoffPins ->
        nativePins:ProtectedIssueCensusNativeAttemptPins ->
        expectedMarker:ProtectedIssueCensusHandoffRequest ->
        handoffPort:IProtectedIssueCensusHandoffPort option ->
        nativePort:IProtectedIssueCensusNativeAttemptPort option ->
        Result<ProtectedIssueCensusRecoveryHold, string>
