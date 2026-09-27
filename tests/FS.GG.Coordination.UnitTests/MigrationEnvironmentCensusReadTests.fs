module FS.GG.Coordination.MigrationEnvironmentCensusReadTests

open System
open Xunit
open FS.GG.Coordination.GitHub

type private CountingTransport() =
    let mutable calls = 0
    member _.Calls = calls
    interface IMigrationGitHubReadTransport with
        member _.Send _ =
            calls <- calls + 1
            NetworkFailure

let private options =
    { ApiBase=Uri "https://api.github.test/"
      GraphQLUri=Uri "https://api.github.test/graphql"
      Owner="FS-GG"; Repository="copy"; ExpectedRepositoryId=1L
      Token="test"; UserAgent="test" }

[<Fact>]
let ``environment census slot is fail closed before worker implementation`` () =
    let inner = CountingTransport()
    let transport = inner :> IMigrationGitHubReadTransport
    let guarded = MigrationEnvironmentCensusRead.guardReadTransport options transport
    let request =
        Rest
            { Method=Get; Uri=Uri "https://api.github.test/repos/FS-GG/copy/environments"
              Headers=Map.empty; Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
    Assert.Equal(NetworkFailure, guarded.Send request)
    Assert.Equal(0, inner.Calls)
    Assert.Equal(
        Error "environment-census-read-unavailable",
        MigrationEnvironmentCensusRead.capturePass options transport)
    Assert.Equal(
        Error "environment-census-read-unavailable",
        MigrationEnvironmentCensusRead.captureTwoPass options transport)
