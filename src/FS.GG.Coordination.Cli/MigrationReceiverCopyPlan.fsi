namespace FS.GG.Coordination.Cli

open System
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

type MigrationReceiverCopyAcceptedEvidence =
    { Gs2083ReceiptBytes: ReadOnlyMemory<byte>
      ReceiverSourceBindingBytes: ReadOnlyMemory<byte>
      ReceiverCensusBytes: ReadOnlyMemory<byte>
      ReceiverSourceManifestsGzipBytes: ReadOnlyMemory<byte>
      ReceiverSourceBlobsGzipBytes: ReadOnlyMemory<byte> }

type MigrationReceiverCopyMapping =
    { ReceiverCopyId: string
      ReceiverCopyRepository: string
      ReceiverCopySourceRevision: string
      ReceiverCopySourceTree: string
      ReceiverCopyPlannedRef: string
      ReceiverCopyRequiredEntries: MigrationReceiverTreeEntry list
      ReceiverCopyMissingBlobSha1s: string list }

type MigrationReceiverCopyPlanResult =
    { ReceiverCopyRunIdentity: MigrationSandboxSeedRequest
      ReceiverCopyReceiptVerification: AcceptanceReceiptDigestVerification
      ReceiverCopyCensus: ReceiverCensusSnapshot
      ReceiverCopyMappings: MigrationReceiverCopyMapping list
      ReceiverCopyRetainedBlobSha256BySha1: Map<string, string>
      ReceiverCopyFingerprint: string }

[<RequireQualifiedAccess>]
module MigrationReceiverCopyPlan =
    /// Derives exactly seven receiver mappings from the pinned accepted GS2-08.3 bytes.
    /// Missing full-tree bytes remain explicit and cannot become copy-ready provider evidence.
    val derive:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
            Result<MigrationReceiverCopyPlanResult, string>

    val verify:
        acceptedEvidence:MigrationReceiverCopyAcceptedEvidence ->
        runIdentity:MigrationSandboxSeedRequest ->
        observed:MigrationReceiverCopyPlanResult ->
            Result<MigrationReceiverCopyPlanResult, string>

    /// Test-only structural validation beneath the immutable accepted-receipt byte boundary.
    val internal deriveUnpinnedForTests:
        receiverCensusBytes:ReadOnlyMemory<byte> ->
        receiverSourceManifestsGzipBytes:ReadOnlyMemory<byte> ->
        receiverSourceBlobsGzipBytes:ReadOnlyMemory<byte> ->
        runIdentity:MigrationSandboxSeedRequest ->
            Result<MigrationReceiverCopyMapping list * Map<string, string>, string>
