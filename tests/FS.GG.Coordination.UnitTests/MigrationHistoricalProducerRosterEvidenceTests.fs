module FS.GG.Coordination.MigrationHistoricalProducerRosterEvidenceTests

open System
open System.IO
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private hash = String.replicate 64 "a"
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
        CensusFingerprint = hash
        ProposalFingerprint = hash
    }

let private payload (commentId: int64) (marker: string) (created: string) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartArray()
    writer.WriteStartObject()
    writer.WriteNumber("id", commentId)
    writer.WriteString("node_id", $"IC_{commentId}")
    writer.WriteString("created_at", created)
    writer.WriteString("body", marker)
    writer.WriteEndObject()
    writer.WriteEndArray()
    writer.Flush()
    stream.ToArray()

let private page repository kind number commentId marker =
    {
        Repository = repository
        SubjectKind = kind
        SubjectNumber = number
        RequestedUri = $"https://api.github.com/repos/{repository}/issues/{number}/comments?per_page=100"
        NextUri = None
        PayloadBytes = payload commentId marker "2026-08-10T00:00:00Z"
    }

let private pages () =
    [
        page "FS-GG/FS.GG.Coordination" "issue" 274 5522351065L "<!-- fsgg:done-receipt v=1 -->\nverified"
        page "FS-GG/FS.GG.Net" "issue" 71 5301936886L "<!-- fsgg:done-receipt v=1 -->\nverified"
        page
            "FS-GG/.github"
            "pull-request"
            2214
            5178100645L
            "<!-- fsgg:delivery-receipt id=one head=abc evidence=run-1 -->"
        page
            "FS-GG/.github"
            "pull-request"
            2879
            5386930796L
            "<!-- fsgg:delivery-receipt id=two head=def evidence=run-2 -->"
        page
            "FS-GG/.github"
            "pull-request"
            3231
            5552693865L
            "<!-- fsgg:delivery-receipt id=three head=012 evidence=run-3 -->"
    ]

let private prepare sourceReads first second =
    MigrationHistoricalProducerRosterEvidence.prepare (proposal ()) sourceReads first second

let private expectError (suffix: string) =
    function
    | Error reason -> Assert.EndsWith(suffix, reason)
    | Ok value -> failwithf "expected refusal, got %A" value

[<Fact>]
let ``recovered producers and nonzero survivors preserve unresolved history`` () =
    let native = pages ()

    let plan =
        prepare MigrationHistoricalProducerRosterEvidence.expectedSources native native
        |> Result.defaultWith failwith

    Assert.False(plan.ClaimEventAuthorityAvailable)
    Assert.Equal<string list>([ "intake-receipt" ], plan.UnresolvedProducerFamilies)
    Assert.Equal<string list>(families, plan.UnresolvedHistoryFamilies)
    Assert.Equal(5, plan.Survivors.Length)

    Assert.Contains(
        plan.Survivors,
        fun survivor ->
            survivor.Repository = "FS-GG/FS.GG.Coordination"
            && survivor.SubjectNumber = 274
            && survivor.CommentId = 5522351065L
    )

    Assert.Contains(
        plan.Survivors,
        fun survivor ->
            survivor.Repository = "FS-GG/.github"
            && survivor.SubjectKind = "pull-request"
            && survivor.SubjectNumber = 3231
            && survivor.CommentId = 5552693865L
    )

    Assert.Equal<string list>(
        [ "observed-zero"; "observed-zero"; "observed-zero" ],
        plan.PriorCensusStates |> List.map snd
    )

    let delivery = plan.Families |> List.find (_.Family >> (=) "delivery-receipt")
    let doneReceipt = plan.Families |> List.find (_.Family >> (=) "legacy-done-receipt")
    Assert.Equal("protected-agent-authoring-protocol-recovered", delivery.ProducerStatus)
    Assert.Equal("observed-nonzero:3", delivery.SurvivorStatus)
    Assert.Equal("protected-executable-producer-recovered", doneReceipt.ProducerStatus)
    Assert.Equal("observed-nonzero:2", doneReceipt.SurvivorStatus)

    Assert.All(
        plan.Families,
        fun family -> Assert.StartsWith("historical-completeness-unresolved:", family.HistoricalCompleteness)
    )

    Assert.All(plan.Families, fun family -> Assert.StartsWith("loss-disposition-unapproved:", family.LossDisposition))

[<Fact>]
let ``unknown protected source identity refuses`` () =
    let source = MigrationHistoricalProducerRosterEvidence.expectedSources.Head

    let altered =
        { source with
            Revision = String.replicate 40 "0"
        }
        :: MigrationHistoricalProducerRosterEvidence.expectedSources.Tail

    let native = pages ()
    prepare altered native native |> expectError "protected-source-identity"

[<Fact>]
let ``two pass native drift refuses`` () =
    let first = pages ()

    let changed =
        { first.Head with
            PayloadBytes = payload 5522351065L "<!-- fsgg:done-receipt v=1 -->\nchanged" "2026-08-10T00:00:00Z"
        }
        :: first.Tail

    prepare MigrationHistoricalProducerRosterEvidence.expectedSources first changed
    |> expectError "two-pass-native-drift"

[<Fact>]
let ``missing survivor cannot be inferred as historical absence`` () =
    let first = pages ()

    let empty =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartArray()
        writer.WriteEndArray()
        writer.Flush()
        stream.ToArray()

    let missing = { first.Head with PayloadBytes = empty } :: first.Tail

    prepare MigrationHistoricalProducerRosterEvidence.expectedSources missing missing
    |> expectError "survivor-missing-or-wrong-family:5522351065"

[<Fact>]
let ``cutoff and terminal pagination are mandatory`` () =
    let first = pages ()

    let postCutoff =
        { first.Head with
            PayloadBytes = payload 5522351065L "<!-- fsgg:done-receipt v=1 -->" "2026-09-29T00:00:00Z"
        }
        :: first.Tail

    prepare MigrationHistoricalProducerRosterEvidence.expectedSources postCutoff postCutoff
    |> expectError "native-page-shape"

    let unterminated =
        { first.Head with
            NextUri = Some(first.Head.RequestedUri + "&page=2")
        }
        :: first.Tail

    prepare MigrationHistoricalProducerRosterEvidence.expectedSources unterminated unterminated
    |> expectError "native-page-chain"
