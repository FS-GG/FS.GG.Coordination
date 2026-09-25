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

type ProtectedIssueCensusNativeAttemptHead =
    { AttemptResourceId: string
      Generation: int64
      SealSha256: string }

type ProtectedIssueCensusNativeAttemptSnapshot =
    { Head: ProtectedIssueCensusNativeAttemptHead
      AttemptId: string
      Complete: bool
      Records: ProtectedIssueCensusNativeAttemptRecord list }

type ProtectedIssueCensusRecoveryHold =
    | NativeResultUnknown
    | NativeRevocationRequired
    | ProtectedReceiptRequired

/// Read-only fake port. The installed authority must return a complete snapshot
/// between linearizable heads; the seal field alone is not an authentic signature.
/// Reads must never come from candidate files or a caller-supplied provider result.
type IProtectedIssueCensusNativeAttemptPort =
    abstract Describe: unit -> ProtectedIssueCensusNativeAttemptDescription
    abstract ReadHead: unit -> ProtectedIssueCensusNativeAttemptHead option
    abstract ReadAttempts: string -> ProtectedIssueCensusNativeAttemptSnapshot option

[<RequireQualifiedAccess>]
module MigrationProtectedIssueCensusAttemptRecovery =
    /// Refuse malformed UTF-16 before default UTF-8 replacement can alias identities.
    val validUtf8Atom:
        value:string -> bool

    /// Structural readback check only; the protected owner must authenticate the selection.
    val validSelectionShape:
        selection:ProtectedIssueCensusSelection -> bool

    /// Structural claim-to-reservation chain; authentic store and journal readback remain required.
    val validMarkerChain:
        marker:ProtectedIssueCensusHandoffRequest -> bool

    /// Structural native phase shape; receipt digests alone are not native readback.
    val validAttemptPhase:
        record:ProtectedIssueCensusNativeAttemptRecord -> bool

    /// Unsigned canonical commitment over the complete native snapshot bytes.
    val expectedSnapshotSealSha256:
        snapshot:ProtectedIssueCensusNativeAttemptSnapshot -> string

    /// Returns the exact stable snapshot used for this hold classification;
    /// the caller must authenticate that same value before any signed qualification.
    val inspectWithSnapshot:
        handoffPins:ProtectedIssueCensusHandoffPins ->
        nativePins:ProtectedIssueCensusNativeAttemptPins ->
        expectedMarker:ProtectedIssueCensusHandoffRequest ->
        handoffPort:IProtectedIssueCensusHandoffPort option ->
        nativePort:IProtectedIssueCensusNativeAttemptPort option ->
        Result<ProtectedIssueCensusNativeAttemptSnapshot * ProtectedIssueCensusRecoveryHold, string>

    /// Every successful classification is still a hold; no provider retry or token action.
    val inspect:
        handoffPins:ProtectedIssueCensusHandoffPins ->
        nativePins:ProtectedIssueCensusNativeAttemptPins ->
        expectedMarker:ProtectedIssueCensusHandoffRequest ->
        handoffPort:IProtectedIssueCensusHandoffPort option ->
        nativePort:IProtectedIssueCensusNativeAttemptPort option ->
        Result<ProtectedIssueCensusRecoveryHold, string>
