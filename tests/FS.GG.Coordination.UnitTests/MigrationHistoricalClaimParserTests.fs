module FS.GG.Coordination.MigrationHistoricalClaimParserTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Cli

let private sha256 (value: string) =
    value
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

[<Fact>]
let ``retained C claim without renewed parses without inventing freshness`` () =
    let body = "<!-- fsgg:claim worker=ghost-222 lease=120 -->\nheld"

    Assert.Equal(
        Ok(
            Some
                {
                    Worker = "ghost-222"
                    LeaseMinutes = 120
                    Session = None
                    BodySha256 = sha256 body
                }
        ),
        MigrationHistoricalClaimParser.tryParse body
    )

[<Fact>]
let ``retained C claim session is preserved as opaque producer data`` () =
    let session = "309bd638-8a1c-42b7-952b-898efb8d1064"
    let body = $"<!-- fsgg:claim worker=plover-c71 lease=120 session={session} -->\nheld"

    match MigrationHistoricalClaimParser.tryParse body with
    | Ok(Some marker) ->
        Assert.Equal("plover-c71", marker.Worker)
        Assert.Equal(120, marker.LeaseMinutes)
        Assert.Equal(Some session, marker.Session)
        Assert.Equal(sha256 body, marker.BodySha256)
    | result -> failwithf "unexpected parse result: %A" result

[<Fact>]
let ``non markers quoted claims and renewed markers are outside historical parser`` () =
    let rows =
        [ "ordinary comment"
          "<!-- fsgg:msg from=a to=b -->\nquoted <!-- fsgg:claim worker=ghost lease=120 -->"
          "<!-- fsgg:claim worker=worker-a lease=30 renewed=1 -->" ]

    for body in rows do
        Assert.Equal(Ok None, MigrationHistoricalClaimParser.tryParse body)

[<Theory>]
[<InlineData("<!-- fsgg:claim lease=120 -->\nheld")>]
[<InlineData("<!-- fsgg:claim lease=120 worker=ghost -->\nheld")>]
[<InlineData("<!-- fsgg:claim worker=ghost worker=twin lease=120 -->\nheld")>]
[<InlineData("<!-- fsgg:claim worker=ghost lease=0120 -->\nheld")>]
[<InlineData("<!-- fsgg:claim worker=ghost lease=0 -->\nheld")>]
[<InlineData("<!-- fsgg:claim worker=ghost lease=120 unknown=x -->\nheld")>]
[<InlineData("<!-- fsgg:claim worker=ghost lease=120 -->")>]
[<InlineData("<!-- fsgg:claim worker=ghost lease=120 -->\r\nheld")>]
let ``anchored pre renewal claims outside retained grammar refuse`` body =
    match MigrationHistoricalClaimParser.tryParse body with
    | Error reason -> Assert.StartsWith("historical-claim-", reason)
    | result -> failwithf "expected refusal, got %A" result

[<Fact>]
let ``parser publishes exact retained source identity`` () =
    Assert.Equal("FS-GG/.github", MigrationHistoricalClaimParser.sourceRepository)
    Assert.Equal("95de1c77674b9dd8d7a9ce568d1ee175a7797e5e", MigrationHistoricalClaimParser.sourceRevision)
    Assert.Equal(
        "tests/coord-engine-parity/casadversarial_server.py",
        MigrationHistoricalClaimParser.sourcePath
    )
    Assert.Equal(
        "ce2f01a5bf7983ff98cced126c13a6c8bf7dd7a13198c8b7fda85372b4d9868f",
        MigrationHistoricalClaimParser.sourceSha256
    )
