module FS.GG.Coordination.MigrationReviewDeliveryCaptureContractTests

open System
open Xunit
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Cli

[<Fact>]
let ``capture paths require all check runs and bind statuses to request commit`` () =
    let head = String.replicate 40 "a"
    Assert.Equal("repos/FS-GG/copy/commits/" + head + "/check-runs?filter=all&per_page=100",
                 MigrationReviewDeliveryCaptureContract.checkRunsPath "repos/FS-GG/copy" head)
    Assert.Equal("repos/FS-GG/copy/commits/" + head + "/statuses?per_page=100",
                 MigrationReviewDeliveryCaptureContract.statusesPath "repos/FS-GG/copy" head)
    Assert.Equal<string list>(
        [ "refs/heads/fsgg/v2/journal/review/"
          "refs/heads/fsgg/v2/journal/operation/" ],
        MigrationReviewDeliveryCaptureContract.journalRefPrefixes)

[<Fact>]
let ``request fingerprint changes with status URL commit`` () =
    let request head =
        Rest
            { Method=Get
              Uri=Uri("https://api.github.test/repos/FS-GG/copy/commits/" + head + "/statuses?per_page=100")
              Headers=Map.ofList [ "accept", "application/vnd.github+json" ]
              Body=None
              ApiVersion=ApiVersion.required
              Idempotency=ReplaySafe }
    let first = request (String.replicate 40 "a")
                |> MigrationReviewDeliveryCaptureContract.captureRequest
                |> MigrationReviewDeliveryCaptureContract.requestSha256
    let second = request (String.replicate 40 "b")
                 |> MigrationReviewDeliveryCaptureContract.captureRequest
                 |> MigrationReviewDeliveryCaptureContract.requestSha256
    Assert.NotEqual(first, second)

[<Fact>]
let ``retained request evidence excludes credentials`` () =
    let request =
        Rest
            { Method=Get; Uri=Uri "https://api.github.test/repos/FS-GG/copy"
              Headers=Map.ofList [ "authorization", "Bearer secret"; "accept", "application/json" ]
              Body=None; ApiVersion=ApiVersion.required; Idempotency=ReplaySafe }
    let captured = MigrationReviewDeliveryCaptureContract.captureRequest request
    Assert.False(captured.Headers |> Map.containsKey "authorization")
    Assert.Equal(Some "application/json", captured.Headers |> Map.tryFind "accept")
