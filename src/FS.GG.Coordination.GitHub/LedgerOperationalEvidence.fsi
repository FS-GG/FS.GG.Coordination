namespace FS.GG.Coordination.GitHub

open System

type LedgerOperationalDimension = SettingsAppliedEvidence | AppCustodyEvidence | FleetInitializedEvidence | MonitoringEvidence
type LedgerOperationalEvidenceContext =
    { RepositoryId: int64; FleetRef: string; ControlIssueNumber: int64; DesiredPolicySha256: string
      ProviderObservationSha256: string; InitializationSeal: string option; MonitorStoreId: string option
      SignerPublicKeySha256: string }
type LedgerOperationalEvidence =
    { Dimension: LedgerOperationalDimension; Context: LedgerOperationalEvidenceContext; RunId: string
      InputSha256: string; ObservedAt: DateTimeOffset; ExpiresAt: DateTimeOffset; Authority: string
      SignerKeyId: string; PublicKeyPem: string; PublicKeySha256: string; Payload: byte array; Signature: byte array }

[<RequireQualifiedAccess>]
module LedgerOperationalEvidence =
    val canonicalPayload: LedgerOperationalEvidence -> byte array
    val derive: asOf: DateTimeOffset -> expected: LedgerOperationalEvidenceContext -> evidence: LedgerOperationalEvidence list -> LedgerProtectionOperationalState
