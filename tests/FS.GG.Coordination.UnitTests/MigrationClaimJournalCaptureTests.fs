module FS.GG.Coordination.MigrationClaimJournalCaptureTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

type private UnavailableTransport() =
    interface IMigrationGitHubReadTransport with
        member _.Send _ = NetworkFailure

[<Fact>]
let ``claim journal capture stays unavailable until reader implementation`` () =
    let options =
        { ApiBase=Uri "https://api.github.test/"
          GraphQLUri=Uri "https://api.github.test/graphql"
          Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=1L
          Token="test"; UserAgent="test" }
    Assert.Equal(
        Error "claim-journal-capture-unavailable",
        MigrationClaimJournalCapture.captureTwoPass options (UnavailableTransport()))

