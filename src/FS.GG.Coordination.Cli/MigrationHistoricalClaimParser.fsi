namespace FS.GG.Coordination.Cli

/// A claim marker emitted by the retained pre-renewal-token producer. Freshness
/// remains a property of the native comment timestamps; this type does not invent
/// the later `renewed` field.
type MigrationHistoricalClaimMarker =
    {
        Worker: string
        LeaseMinutes: int
        Session: string option
        BodySha256: string
    }

[<RequireQualifiedAccess>]
module MigrationHistoricalClaimParser =
    /// Immutable source identity for the retained C-claim producer fixture.
    val sourceRepository: string
    val sourceRevision: string
    val sourcePath: string
    val sourceSha256: string

    /// Parses only the retained C-claim wire form. Non-claim comments and later
    /// renewed markers return None. An anchored pre-renewal claim with any shape
    /// outside the retained producer grammar refuses.
    val tryParse: body: string -> Result<MigrationHistoricalClaimMarker option, string>
