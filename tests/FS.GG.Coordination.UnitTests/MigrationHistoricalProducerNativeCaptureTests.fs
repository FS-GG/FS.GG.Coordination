module FS.GG.Coordination.MigrationHistoricalProducerNativeCaptureTests

open System
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private digest = String.replicate 64 "a"
let private cutoff = DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)

let private families =
    [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ]

let private proposal () =
    {
        RepositoryId = 1351660651L
        RepositoryFullName = "FS-GG/FS.GG.Coordination"
        CutoffUtc = cutoff
        Pages = []
        Subjects = []
        Families =
            families
            |> List.map (fun family ->
                {
                    Family = family
                    ObservationState = "observed-zero"
                    Observations = []
                    ProducerGap = $"protected-producer-unavailable:{family}"
                    HistoryGap = $"historical-completeness-unresolved:{family}"
                })
        UnresolvedProducerFamilies = families
        UnresolvedHistoryFamilies = families
        CensusFingerprint = digest
        ProposalFingerprint = digest
    }

let private body (commentId: int64) (marker: string) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartArray()
    writer.WriteStartObject()
    writer.WriteNumber("id", commentId)
    writer.WriteString("node_id", $"IC_{commentId}")
    writer.WriteString("created_at", "2026-08-10T00:00:00Z")
    writer.WriteString("body", marker)
    writer.WriteEndObject()
    writer.WriteEndArray()
    writer.Flush()
    stream.ToArray()

let private emptyBody () = Text.Encoding.UTF8.GetBytes "[]"

let private subjects =
    [
        "FS-GG/FS.GG.Coordination", 274, 5522351065L, "<!-- fsgg:done-receipt v=1 -->"
        "FS-GG/Net", 71, 5301936886L, "<!-- fsgg:done-receipt v=1 -->"
        "FS-GG/.github", 2214, 5178100645L, "<!-- fsgg:delivery-receipt id=one head=a evidence=e -->"
        "FS-GG/.github", 2879, 5386930796L, "<!-- fsgg:delivery-receipt id=two head=b evidence=e -->"
        "FS-GG/.github", 3231, 5552693865L, "<!-- fsgg:delivery-receipt id=three head=c evidence=e -->"
    ]

let private uri repository number page =
    let suffix = if page = 1 then "" else $"&page={page}"
    $"https://api.github.com/repos/{repository}/issues/{number}/comments?per_page=100{suffix}"

let private responses () =
    subjects
    |> List.map (fun (repository, number, commentId, marker) ->
        let requested = uri repository number 1

        requested,
        {
            StatusCode = 200
            FinalUri = requested
            Headers = Map.empty
            PayloadBytes = body commentId marker
        })
    |> Map.ofList

type private Transport
    (
        first: Map<string, MigrationHistoricalProducerNativeResponse>,
        second: Map<string, MigrationHistoricalProducerNativeResponse>,
        ?reusePassIdentity: bool
    ) =
    let mutable calls: (string * string) list = []
    member _.Calls = List.rev calls

    interface IMigrationHistoricalProducerNativeTransport with
        member _.StartFreshPass ordinal =
            if defaultArg reusePassIdentity false then
                Ok "same-pass"
            else
                Ok $"fresh-pass-{ordinal}"

        member _.Get(passIdentity, requestedUri) =
            calls <- (passIdentity, requestedUri) :: calls

            let selected =
                if passIdentity.EndsWith("1", StringComparison.Ordinal) then
                    first
                else
                    second

            match selected |> Map.tryFind requestedUri with
            | Some response -> Ok response
            | None -> Error "missing-fixture-response"

let private capture transport =
    MigrationHistoricalProducerNativeCapture.capture
        (proposal ())
        MigrationHistoricalProducerRosterEvidence.expectedSources
        transport

let private expectError (fragment: string) =
    function
    | Error reason -> Assert.Contains(fragment, reason)
    | Ok value -> failwithf "expected refusal, got %A" value

[<Fact>]
let ``five pinned populations are read in two distinct fresh passes`` () =
    let fixtures = responses ()
    let transport = Transport(fixtures, fixtures)
    let captured = capture transport |> Result.defaultWith failwith
    Assert.Equal("fresh-pass-1", captured.FirstPass.PassIdentity)
    Assert.Equal("fresh-pass-2", captured.SecondPass.PassIdentity)
    Assert.Equal(5, captured.FirstPass.Pages.Length)
    Assert.Equal(10, transport.Calls.Length)
    Assert.Equal(5, captured.Plan.Survivors.Length)
    Assert.False(captured.Plan.ClaimEventAuthorityAvailable)
    Assert.Equal<string list>(families, captured.Plan.UnresolvedHistoryFamilies)

[<Fact>]
let ``sequential Link pages retain exact raw bodies and terminal proof`` () =
    let fixtures = responses ()
    let repository, number, _, _ = subjects.Head
    let firstUri = uri repository number 1
    let secondUri = uri repository number 2
    let survivor = fixtures[firstUri]

    let paged =
        fixtures
        |> Map.add
            firstUri
            { survivor with
                Headers = Map [ "link", $"<{secondUri}>; rel=\"next\", <{secondUri}>; rel=\"last\"" ]
                PayloadBytes = emptyBody ()
            }
        |> Map.add secondUri { survivor with FinalUri = secondUri }

    let captured = capture (Transport(paged, paged)) |> Result.defaultWith failwith
    Assert.Equal(6, captured.FirstPass.Pages.Length)
    Assert.Equal(Some secondUri, captured.FirstPass.Pages.Head.NextUri)
    Assert.True(emptyBody () = captured.FirstPass.Pages.Head.PayloadBytes)
    Assert.True(captured.FirstPass.Pages[1].NextUri.IsNone)

[<Theory>]
[<InlineData(401)>]
[<InlineData(403)>]
[<InlineData(404)>]
let ``inaccessible population refuses`` status =
    let fixtures = responses ()
    let requested = fixtures |> Map.toSeq |> Seq.head |> fst

    let denied =
        fixtures
        |> Map.add
            requested
            { fixtures[requested] with
                StatusCode = status
            }

    capture (Transport(denied, denied)) |> expectError $"http-status-{status}"

[<Fact>]
let ``changed second pass and partial response refuse`` () =
    let first = responses ()
    let requested = first |> Map.toSeq |> Seq.head |> fst

    let changed =
        first
        |> Map.add
            requested
            { first[requested] with
                PayloadBytes = emptyBody ()
            }

    capture (Transport(first, changed)) |> expectError "two-pass-native-drift"

    let partial =
        first
        |> Map.add
            requested
            { first[requested] with
                Headers = Map [ "link", $"<{requested}&page=2>; rel=\"next\"" ]
            }

    capture (Transport(partial, partial)) |> expectError "missing-fixture-response"

[<Fact>]
let ``escaped Link and reused pass identity refuse`` () =
    let fixtures = responses ()
    let requested = fixtures |> Map.toSeq |> Seq.head |> fst

    let escaped =
        fixtures
        |> Map.add
            requested
            { fixtures[requested] with
                Headers =
                    Map
                        [
                            "link",
                            "<https://api.github.com/repos/FS-GG/Other/issues/1/comments?per_page=100&page=2>; rel=\"next\""
                        ]
            }

    capture (Transport(escaped, escaped))
    |> expectError "escaped-or-nonsequential-link"

    capture (Transport(fixtures, fixtures, reusePassIdentity = true))
    |> expectError "fresh-pass-identity-reused"
