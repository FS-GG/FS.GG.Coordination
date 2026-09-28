namespace FS.GG.Coordination.Cli

open System

[<RequireQualifiedAccess>]
type MigrationHistoricalProducerRole =
    | ExecutableWriter
    | AgentAuthoringProtocol

type MigrationHistoricalProducerSourceEvidence =
    {
        Family: string
        Role: MigrationHistoricalProducerRole
        Repository: string
        Revision: string
        Path: string
        BlobOid: string
        BlobSha256: string
    }

type MigrationHistoricalProducerNativePage =
    {
        Repository: string
        SubjectKind: string
        SubjectNumber: int
        RequestedUri: string
        NextUri: string option
        PayloadBytes: byte array
    }

type MigrationHistoricalProducerFamilyEvidence =
    {
        Family: string
        ProducerStatus: string
        SurvivorStatus: string
        SurvivorCommentIds: int64 list
        HistoricalCompleteness: string
        LossDisposition: string
    }

type MigrationHistoricalProducerSurvivorEvidence =
    {
        Family: string
        Repository: string
        SubjectKind: string
        SubjectNumber: int
        CommentId: int64
    }

type MigrationHistoricalProducerRosterPlan =
    {
        CutoffUtc: DateTimeOffset
        Sources: MigrationHistoricalProducerSourceEvidence list
        PriorCensusStates: (string * string) list
        Survivors: MigrationHistoricalProducerSurvivorEvidence list
        Families: MigrationHistoricalProducerFamilyEvidence list
        UnresolvedProducerFamilies: string list
        UnresolvedHistoryFamilies: string list
        ClaimEventAuthorityAvailable: bool
        Fingerprint: string
    }

[<RequireQualifiedAccess>]
module MigrationHistoricalProducerRosterEvidence =
    /// Exact protected source identities recovered by the audit. These identify source bytes;
    /// they are not proof of a complete historical native population.
    val expectedSources: MigrationHistoricalProducerSourceEvidence list

    /// Validate two equal terminal reads of the five selected survivor comment streams and layer
    /// the recovered producer classifications over an existing unresolved loss proposal.
    val prepare:
        proposal: MigrationHistoricalLossProposal ->
        sourceReads: MigrationHistoricalProducerSourceEvidence list ->
        firstPass: MigrationHistoricalProducerNativePage list ->
        secondPass: MigrationHistoricalProducerNativePage list ->
            Result<MigrationHistoricalProducerRosterPlan, string>
