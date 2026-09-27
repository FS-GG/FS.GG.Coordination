namespace FS.GG.Coordination.Cli

open FS.GG.Coordination.GitHub

type MigrationLegacyClaimMarker =
    {
        SubjectNumber: int
        CommentNodeId: string
        Worker: string
        LeaseMinutes: int
        Renewed: int64
        SessionOperationId: string option
        PayloadSha256: string
    }

type MigrationClaimEventPartialCapture =
    {
        NativeFirst: MigrationNativeActivityCapture
        NativeSecond: MigrationNativeActivityCapture
        Journals: MigrationClaimJournalTwoPass
        LegacyInventory: MigrationLegacyReceiptInventory
        ClaimMarkers: MigrationLegacyClaimMarker list
        MissingAuthorities: string list
        Fingerprint: string
    }

[<RequireQualifiedAccess>]
module MigrationClaimEventInspectBinder =
    /// Revalidates the native and protected-journal captures, independently reparses every
    /// native comment payload, and joins session-bearing legacy claim markers to a protected
    /// claim operation. The result is deliberately partial while producer retention is missing.
    val bindPartial:
        options: MigrationGitHubReadOptions ->
        nativeFirst: MigrationNativeActivityCapture ->
        nativeSecond: MigrationNativeActivityCapture ->
        journals: MigrationClaimJournalTwoPass ->
        legacyInventory: MigrationLegacyReceiptInventory ->
            Result<MigrationClaimEventPartialCapture, string>

    /// Refuses until every producer family has protected retained source authority.
    val qualifyCanonical:
        capture: MigrationClaimEventPartialCapture -> Result<MigrationClaimEventPartialCapture, string>
