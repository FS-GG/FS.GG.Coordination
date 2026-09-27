module FS.GG.Coordination.MigrationLegacyReceiptParserTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open FS.GG.Coordination.Cli

let private sha (value: string) =
    value
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private expectError (expected: string) (result: Result<'a, string>) =
    match result with
    | Error reason -> Assert.Equal(expected, reason)
    | Ok value -> Assert.Fail($"expected {expected}, got {value}")

[<Fact>]
let ``receiver roster is source bound and explicitly incomplete`` () =
    let roster = MigrationLegacyReceiptParser.roster
    Assert.False(roster.RosterComplete)
    Assert.True(roster.MissingReservedPrefixRegistry)

    Assert.Equal<string list>(
        [ "delivery-receipt"; "intake-receipt"; "legacy-done-receipt" ],
        roster.MissingProtectedProducers
    )

    Assert.Equal<string list>(
        [
            "delivery-receipt"
            "typed-completion"
            "completion-correction"
            "legacy-done-receipt"
        ],
        roster.Sources |> List.map _.Family
    )

    Assert.All(
        roster.Sources,
        fun source ->
            Assert.Equal("FS-GG/.github", source.SourceRepository)
            Assert.Equal("ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce", source.SourceRevision)
            Assert.Matches("^[0-9a-f]{64}$", source.ParserSourceSha256)

            match source.WriterSourcePath, source.WriterSourceSha256, source.SourceKind with
            | Some path, Some digest, ReceiptProtectedWriterAndParser ->
                Assert.Equal("src/FS.GG.Coord.GitHub/Writes.fs", path)
                Assert.Matches("^[0-9a-f]{64}$", digest)
            | None, None, ReceiptProtectedParserOnly -> ()
            | _ -> Assert.Fail("writer evidence and source kind differ")
    )

    Assert.Equal<MigrationLegacyReceiptParserSourceKind list>(
        [
            ReceiptProtectedParserOnly
            ReceiptProtectedWriterAndParser
            ReceiptProtectedWriterAndParser
            ReceiptProtectedParserOnly
        ],
        roster.Sources |> List.map _.SourceKind
    )

[<Fact>]
let ``delivery receipt follows protected leading line and location grammar`` () =
    let body =
        "\n   <!-- fsgg:delivery-receipt id=kit-0.49.0 head=abc_123 evidence=https://example.test/a -->\n\nverified"

    Assert.Equal(
        Ok(Some(DeliveryReceipt("kit-0.49.0", "abc_123", "https://example.test/a"))),
        MigrationLegacyReceiptParser.tryParse PullRequestComment body
    )

    expectError "legacy-receipt-location" (MigrationLegacyReceiptParser.tryParse WorkItemComment body)

    expectError
        "legacy-receipt-delivery-shape"
        (MigrationLegacyReceiptParser.tryParse
            PullRequestComment
            "<!-- fsgg:delivery-receipt id=x head=h evidence=a b -->")

    Assert.Equal(
        Ok None,
        MigrationLegacyReceiptParser.tryParse
            PullRequestComment
            "    <!-- fsgg:delivery-receipt id=x head=h evidence=e -->"
    )

let private completionBody digest =
    let payload =
        {|
            schema = "fsgg.coord.delivery-completion/v1"
            item = "FS-GG/.github#10"
            pullRequest = 20
            mergeSha = "abc123"
            mergeReachable = true
            obligationReceipts = [||]
            postMergeVerification = null
            pendingBoardWrites = 0
            freshnessToken = "fresh"
            actionKey = "action"
            completedAt = "2026-09-27T10:00:00.0000000+00:00"
            digest = digest
        |}

    "<!-- fsgg:delivery-completion/v1 -->\n" + JsonSerializer.Serialize payload

[<Fact>]
let ``typed completion validates exact schema facts and producer digest`` () =
    let digest =
        String.concat
            "\n"
            [
                "FS-GG/.github#10"
                "20"
                "abc123"
                "True"
                "0"
                "fresh"
                "action"
                "2026-09-27T10:00:00.0000000+00:00"
            ]
        |> sha

    Assert.Equal(
        Ok(Some(DeliveryCompletion("FS-GG/.github#10", 20, "abc123", digest))),
        MigrationLegacyReceiptParser.tryParse WorkItemComment (completionBody digest)
    )

    expectError
        "legacy-receipt-completion-digest"
        (MigrationLegacyReceiptParser.tryParse WorkItemComment (completionBody (String.replicate 64 "0")))

    expectError
        "legacy-receipt-location"
        (MigrationLegacyReceiptParser.tryParse PullRequestComment (completionBody digest))

[<Fact>]
let ``typed completion rejects duplicate members before interpretation`` () =
    let duplicate =
        "<!-- fsgg:delivery-completion/v1 -->\n"
        + "{\"schema\":\"fsgg.coord.delivery-completion/v1\",\"schema\":\"fsgg.coord.delivery-completion/v1\"}"

    expectError "legacy-receipt-completion-shape" (MigrationLegacyReceiptParser.tryParse WorkItemComment duplicate)

let private completionWithRun branch digest =
    let run =
        {|
            id = 81L
            attempt = 2
            workflow = "bootstrap"
            event = "push"
            branch = branch
            sha = "abc123"
            status = "completed"
            conclusion = "success"
            url = "https://github.test/runs/81"
        |}

    let payload =
        {|
            schema = "fsgg.coord.delivery-completion/v1"
            item = "FS-GG/.github#10"
            pullRequest = 20
            mergeSha = "abc123"
            mergeReachable = true
            obligationReceipts = [||]
            postMergeVerification =
                {|
                    mergeSha = "abc123"
                    defaultBranch = "main"
                    runs = [| run |]
                |}
            pendingBoardWrites = 0
            freshnessToken = "fresh"
            actionKey = "action"
            completedAt = "2026-09-27T10:00:00.0000000+00:00"
            digest = digest
        |}

    "<!-- fsgg:delivery-completion/v1 -->\n" + JsonSerializer.Serialize payload

[<Fact>]
let ``typed completion binds exact successful post merge run`` () =
    let digest =
        String.concat
            "\n"
            [
                "FS-GG/.github#10"
                "20"
                "abc123"
                "True"
                "0"
                "fresh"
                "action"
                "2026-09-27T10:00:00.0000000+00:00"
                "postMergeVerification"
                "verified"
                "abc123"
                "main"
                "81"
                "2"
                "bootstrap"
                "push"
                "main"
                "abc123"
                "completed"
                "success"
                "https://github.test/runs/81"
            ]
        |> sha

    Assert.Equal(
        Ok(Some(DeliveryCompletion("FS-GG/.github#10", 20, "abc123", digest))),
        MigrationLegacyReceiptParser.tryParse WorkItemComment (completionWithRun "main" digest)
    )

    expectError
        "legacy-receipt-completion-verification"
        (MigrationLegacyReceiptParser.tryParse WorkItemComment (completionWithRun "other" digest))

let private correctionBody destination digest =
    let payload =
        {|
            schema = "fsgg.coord.completion-correction/v1"
            item = "FS-GG/.github#10"
            destination = destination
            observedAt = "2026-09-27T11:00:00.0000000+00:00"
            digest = digest
        |}

    "<!-- fsgg:completion-correction/v1 -->\n" + JsonSerializer.Serialize payload

[<Fact>]
let ``completion correction validates destination time and digest`` () =
    let digest =
        String.concat
            "\n"
            [
                "fsgg.coord.completion-correction/v1"
                "FS-GG/.github#10"
                "Blocked"
                "2026-09-27T11:00:00.0000000+00:00"
            ]
        |> sha

    Assert.Equal(
        Ok(
            Some(
                CompletionCorrection(
                    "FS-GG/.github#10",
                    "Blocked",
                    DateTimeOffset.Parse("2026-09-27T11:00:00Z"),
                    digest
                )
            )
        ),
        MigrationLegacyReceiptParser.tryParse WorkItemComment (correctionBody "Blocked" digest)
    )

    expectError
        "legacy-receipt-correction-fields"
        (MigrationLegacyReceiptParser.tryParse WorkItemComment (correctionBody "Done" digest))

[<Fact>]
let ``legacy done stays parser only and unknown forms refuse`` () =
    Assert.Equal(
        Ok(Some LegacyDoneReceipt),
        MigrationLegacyReceiptParser.tryParse WorkItemComment "<!-- fsgg:done-receipt v=1 -->\nlegacy"
    )

    expectError
        "legacy-receipt-location"
        (MigrationLegacyReceiptParser.tryParse PullRequestComment "<!-- fsgg:done-receipt v=1 -->")

    expectError
        "legacy-receipt-done-shape"
        (MigrationLegacyReceiptParser.tryParse WorkItemComment "<!-- fsgg:done-receipt v=2 -->")

    Assert.Equal(Ok None, MigrationLegacyReceiptParser.tryParse WorkItemComment "ordinary comment")
    expectError "legacy-receipt-body-null" (MigrationLegacyReceiptParser.tryParse WorkItemComment null)
