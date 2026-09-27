namespace FS.GG.Coordination.Cli

open System

type MigrationHistoricalLossSourceBinding =
    { Family: string
      ProducerId: string
      SourceRevision: string
      SourceUri: string
      SourceBlobSha256: string }

type MigrationHistoricalLossRequest =
    { RepositoryId: int64
      RepositoryFullName: string
      CutoffUtc: DateTimeOffset
      MissingFamilies: string list
      SourceBindings: MigrationHistoricalLossSourceBinding list
      CallerCompletenessFlags: Map<string, bool> }

type MigrationHistoricalLossPageBinding =
    { Stream: string
      SubjectNumber: int option
      RequestedUri: string
      PayloadSha256: string
      NextUri: string option }

type MigrationHistoricalLossSubjectBinding =
    { SubjectKind: string
      SubjectNumber: int
      NodeId: string
      ObservedAtUtc: DateTimeOffset
      PayloadSha256: string }

type MigrationHistoricalLossObservation =
    { Family: string
      SubjectKind: string
      SubjectNumber: int
      NodeId: string
      ObservedAtUtc: DateTimeOffset
      PayloadSha256: string
      MarkerSha256: string }

type MigrationHistoricalLossFamilyCensus =
    { Family: string
      ObservationState: string
      Observations: MigrationHistoricalLossObservation list
      ProducerGap: string
      HistoryGap: string }

type MigrationHistoricalLossProposal =
    { RepositoryId: int64
      RepositoryFullName: string
      CutoffUtc: DateTimeOffset
      Pages: MigrationHistoricalLossPageBinding list
      Subjects: MigrationHistoricalLossSubjectBinding list
      Families: MigrationHistoricalLossFamilyCensus list
      UnresolvedProducerFamilies: string list
      UnresolvedHistoryFamilies: string list
      CensusFingerprint: string
      ProposalFingerprint: string }

type internal MigrationHistoricalLossTextSource =
    { SourceKind: string
      SubjectNumber: int
      NodeId: string
      ObservedAtUtc: DateTimeOffset
      PayloadSha256: string
      Body: string }

[<RequireQualifiedAccess>]
module MigrationHistoricalLossCensus =
    /// Revalidates the immutable partial capture and produces an evidence-bound proposal only.
    /// Zero observations are reported as observed-zero and never as proof of historical absence.
    val propose:
        request: MigrationHistoricalLossRequest ->
        capture: MigrationClaimEventPartialCapture ->
            Result<MigrationHistoricalLossProposal, string>

    val internal proposeVerifiedForTests:
        request: MigrationHistoricalLossRequest ->
        pages: MigrationHistoricalLossPageBinding list ->
        subjects: MigrationHistoricalLossSubjectBinding list ->
        texts: MigrationHistoricalLossTextSource list ->
            Result<MigrationHistoricalLossProposal, string>
