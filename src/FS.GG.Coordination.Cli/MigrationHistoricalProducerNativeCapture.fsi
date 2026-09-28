namespace FS.GG.Coordination.Cli

open System
open System.Net.Http

type MigrationHistoricalProducerNativeResponse =
    {
        StatusCode: int
        FinalUri: string
        Headers: Map<string, string>
        PayloadBytes: byte array
    }

type IMigrationHistoricalProducerNativeTransport =
    abstract StartFreshPass: passOrdinal: int -> Result<string, string>

    abstract Get:
        passIdentity: string * requestedUri: string -> Result<MigrationHistoricalProducerNativeResponse, string>

/// GET-only GitHub transport. The caller owns token acquisition and HttpClient redirect policy.
type HttpMigrationHistoricalProducerNativeTransport =
    new: client: HttpClient * token: string -> HttpMigrationHistoricalProducerNativeTransport
    interface IMigrationHistoricalProducerNativeTransport

type MigrationHistoricalProducerNativePass =
    {
        PassIdentity: string
        Pages: MigrationHistoricalProducerNativePage list
    }

type MigrationHistoricalProducerNativeCapture =
    {
        FirstPass: MigrationHistoricalProducerNativePass
        SecondPass: MigrationHistoricalProducerNativePass
        Plan: MigrationHistoricalProducerRosterPlan
    }

[<RequireQualifiedAccess>]
module MigrationHistoricalProducerNativeCapture =
    /// Read only the five pinned issue-comment populations twice. Every page retains exact bytes,
    /// request identity and the validated next link. This does not prove repository-wide history.
    val capture:
        proposal: MigrationHistoricalLossProposal ->
        sourceReads: MigrationHistoricalProducerSourceEvidence list ->
        transport: IMigrationHistoricalProducerNativeTransport ->
            Result<MigrationHistoricalProducerNativeCapture, string>
