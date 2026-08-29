namespace FS.GG.Coord.Cli.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open FS.GG.Coord
open FS.GG.Coord.Cli

module ReviewApplicationTests =
    let private head = String.replicate 40 "a"
    let private subject = "FS-GG/.github#2175/pr/42"

    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private snapshot comments checks =
        JsonSerializer.Serialize
            {| binding =
                {| itemRef = "FS-GG/.github#2175"; pr = 42; headSha = head
                   claimGeneration = "fixture-claim"; implementerIdentity = "worker-1"
                   phase = "ordinary"; round = 1 |}
               facts =
                {| comments = comments; checks = checks; repairPhaseGranted = null
                   repairRouteAvailable = true |} |}

    let private run (projection: string) (raw: string) =
        let path = Path.GetTempFileName()
        File.WriteAllText(path, raw)
        try
            let opts = Options.parse [ "review"; "--snapshot"; path; projection ] |> function Ok value -> value | Error error -> failwith error
            let oldOut, oldErr = Console.Out, Console.Error
            use stdout = new StringWriter()
            use stderr = new StringWriter()
            Console.SetOut stdout
            Console.SetError stderr
            try ReviewApplication.run opts, stdout.ToString(), stderr.ToString()
            finally Console.SetOut oldOut; Console.SetError oldErr
        finally File.Delete path

    let private comments values =
        values
        |> List.map (fun (id, url, body) -> {| id = id; url = url; body = body |})
        |> List.toArray

    let private renderWithWait binding facts waitState =
        let opts = Options.parse [ "review"; "FS-GG/.github#2175"; "--pr"; "42"; "--json" ] |> function Ok value -> value | Error error -> failwith error
        let oldOut, oldErr = Console.Out, Console.Error
        use stdout = new StringWriter()
        use stderr = new StringWriter()
        Console.SetOut stdout
        Console.SetError stderr
        try ReviewApplication.renderWithWait opts binding facts waitState, stdout.ToString(), stderr.ToString()
        finally Console.SetOut oldOut; Console.SetError oldErr

    [<Fact>]
    let ``ordinary exhaustion consumers cannot restore a local terminal predicate`` () =
        let readSide = File.ReadAllText(Path.Combine(repositoryRoot, "src/FS.GG.Coord.Cli.Lifecycle/ReviewApplication.fs"))
        let writer = File.ReadAllText(Path.Combine(repositoryRoot, "src/FS.GG.Coord.Cli.Lifecycle/LiveHandlers.fs"))

        for source in [ readSide; writer ] do
            Assert.Equal(1, Regex.Matches(source, "Review\\.decideOrdinaryExhaustion\\b").Count)
            Assert.DoesNotContain("Review.isOrdinaryExhaustionTerminal", source)

        Assert.Contains("Review.projectOrdinaryExhaustion exhaustionDecision", readSide)

    [<Fact>]
    let empty_thread_dispatches_critic () =
        let code, output, _ = run "--json" (snapshot (comments []) "pending")
        Assert.Equal(0, code)
        Assert.Contains("\"state\":\"awaitingInitialReview\"", output)
        Assert.Contains("\"action\":\"dispatchCritic\"", output)
        Assert.DoesNotContain("evidenceClassification", output)

    [<Fact>]
    let typed_acceptance_reaches_accept () =
        let chain = StructuredFixtures.acceptedReviewComments subject head "kestrel-1" |> comments
        let code, output, _ = run "--json" (snapshot chain "green")
        Assert.Equal(0, code)
        Assert.Contains("\"state\":\"accepted\"", output)
        Assert.Contains("\"action\":\"accept\"", output)
        Assert.Contains(head, output)
        Assert.Contains("kestrel-1", output)

    [<Fact>]
    let malformed_structured_record_fails_closed () =
        let malformed = comments [ 1L, "https://reviews/1", "<!-- fsgg:review-decision/v2 -->\n{}" ]
        let code, output, error = run "--json" (snapshot malformed "green")
        Assert.True(code <> 0 || output.Contains("malformedEvidence"), output + " | " + error)
        Assert.True(error.Contains("required field") || output.Contains("stateErrors"))

    [<Fact>]
    let historical_prose_cannot_authorize () =
        let retired = "<!-- fsgg:independent-review/" + "v1 -->\ncritic: kestrel-1\nreviewed-head: " + head + "\nverdict: pass"
        let legacy = comments [ 1L, "https://reviews/old", retired ]
        let code, output, error = run "--json" (snapshot legacy "green")
        Assert.Equal(0, code)
        Assert.Contains("\"state\":\"awaitingInitialReview\"", output)
        Assert.Contains("\"action\":\"dispatchCritic\"", output)
        Assert.DoesNotContain("\"action\":\"accept\"", output)
        Assert.Empty error

    [<Fact>]
    let text_projection_has_no_dual_read_classification () =
        let code, output, _ = run "--text" (snapshot (comments []) "pending")
        Assert.Equal(0, code)
        Assert.Contains("awaitingInitialReview", output)
        Assert.Contains("dispatchCritic", output)
        Assert.DoesNotContain("evidence", output)

    [<Fact>]
    let malformed_snapshot_is_refused () =
        let code, _, error = run "--json" "{\"binding\":\"not-an-object\",\"facts\":{}}"
        Assert.NotEqual(0, code)
        Assert.Contains("malformed", error)

    [<Fact>]
    let successor_wait_uses_typed_state_round_not_live_binding_default () =
        let movedHead = String.replicate 40 "b"
        let reviewComments =
            StructuredFixtures.movedHeadRepairComments subject head "critic-1"
            |> List.map (fun (id, url, body) -> ({ Id = id; Url = url; Body = body }: Driver.ReviewComment))
        let binding: Review.Binding =
            { ItemRef = "FS-GG/.github#2175"
              Pr = 42
              HeadSha = movedHead
              ClaimGeneration = "fixture-claim"
              ImplementerIdentity = "worker-1"
              Phase = Review.Ordinary
              Round = 1 }
        let facts: Review.Facts =
            { Comments = reviewComments
              Checks = Types.PrPending
              RepairPhaseGranted = None
              RepairRouteAvailable = true
              DiffAuditTrusted = None }
        let receipt: ReviewWait.WaitReceipt =
            { Item = binding.ItemRef
              ClaimGeneration = binding.ClaimGeneration
              ReviewGeneration = ReviewWait.generationToken movedHead ReviewWait.RepairConfirmation 2
              Kind = ReviewWait.RepairConfirmation
              EnteredAt = DateTimeOffset.Parse "2026-08-21T00:00:00Z"
              ExpiresAt = DateTimeOffset.Parse "2026-08-21T04:00:00Z"
              EvidenceRef = "https://reviews/queue" }

        let code, output, error = renderWithWait binding facts (ReviewWait.Waiting receipt)
        Assert.Equal(0, code)
        Assert.Contains("\"stateRound\":2", output)
        Assert.Contains("\"action\":\"dispatchSuccessor\"", output)
        Assert.Empty error

        let wrongRound = { receipt with ReviewGeneration = ReviewWait.generationToken movedHead ReviewWait.RepairConfirmation binding.Round }
        let wrongCode, wrongOutput, _ = renderWithWait binding facts (ReviewWait.Waiting wrongRound)
        Assert.NotEqual(0, wrongCode)
        Assert.Contains("\"verdict\":\"noVerdict\"", wrongOutput)
        Assert.Contains("expected generation", wrongOutput)

    [<Fact>]
    let pass_at_completed_round_three_turnover_enters_exhaustion_only_for_settled_red_checks () =
        let reviewComments =
            StructuredFixtures.ordinaryRoundThreePassComments subject head "critic-1"
            |> List.map (fun (id, url, body) -> ({ Id = id; Url = url; Body = body }: Driver.ReviewComment))
        let binding: Review.Binding =
            { ItemRef = "FS-GG/.github#2175"
              Pr = 42
              HeadSha = head
              ClaimGeneration = "new-claim"
              ImplementerIdentity = "worker-1"
              Phase = Review.Ordinary
              Round = 1 }
        let waitReceipt: ReviewWait.WaitReceipt =
            { Item = binding.ItemRef
              ClaimGeneration = "old-claim"
              ReviewGeneration = ReviewWait.generationToken head ReviewWait.RepairConfirmation 3
              Kind = ReviewWait.RepairConfirmation
              EnteredAt = DateTimeOffset.Parse "2026-08-22T00:00:00Z"
              ExpiresAt = DateTimeOffset.Parse "2026-08-22T04:00:00Z"
              EvidenceRef = "https://reviews/4" }
        let completed = ReviewWait.Completed(waitReceipt, "https://reviews/4")
        let facts checks: Review.Facts =
            { Comments = reviewComments
              Checks = checks
              RepairPhaseGranted = None
              RepairRouteAvailable = true
              DiffAuditTrusted = None }

        let redCode, redOutput, redError = renderWithWait binding (facts Types.PrRed) completed
        Assert.Equal(0, redCode)
        Assert.Contains("\"state\":\"ordinaryExhaustion\"", redOutput)
        Assert.Contains("\"action\":\"park\"", redOutput)
        Assert.Contains("\"waitStatus\":\"ordinaryExhaustion\"", redOutput)
        Assert.Empty redError

        let pendingCode, pendingOutput, pendingError = renderWithWait binding (facts Types.PrPending) completed
        Assert.Equal(0, pendingCode)
        Assert.Contains("\"state\":\"passedAwaitingChecks\"", pendingOutput)
        Assert.Contains("\"action\":\"awaitChecks\"", pendingOutput)
        Assert.Empty pendingError

        let greenCode, greenOutput, greenError = renderWithWait binding (facts Types.PrGreen) completed
        Assert.Equal(0, greenCode)
        Assert.Contains("\"state\":\"awaitingHostAcceptance\"", greenOutput)
        Assert.Contains("\"action\":\"requestHostAcceptance\"", greenOutput)
        Assert.Empty greenError

        let preRoundPassComments =
            StructuredFixtures.ordinaryRoundThreePassCommentsWithInitialVerdict
                subject head "critic-1" StructuredDecision.Pass
            |> List.map (fun (id, url, body) -> ({ Id = id; Url = url; Body = body }: Driver.ReviewComment))
        let prePassFacts = { (facts Types.PrRed) with Comments = preRoundPassComments }
        let prePassCode, prePassOutput, prePassError =
            renderWithWait binding prePassFacts completed
        Assert.Equal(0, prePassCode)
        Assert.DoesNotContain("\"state\":\"ordinaryExhaustion\"", prePassOutput)
        Assert.DoesNotContain("\"waitStatus\":\"ordinaryExhaustion\"", prePassOutput)
        Assert.Empty prePassError

        let retiredComments =
            StructuredFixtures.ordinaryRoundThreePassCommentsWithRetiredGeneration
                subject head "critic-1"
            |> List.map (fun (id, url, body) -> ({ Id = id; Url = url; Body = body }: Driver.ReviewComment))
        let retiredFacts = { (facts Types.PrRed) with Comments = retiredComments }
        let retiredCode, retiredOutput, retiredError =
            renderWithWait binding retiredFacts completed
        Assert.Equal(0, retiredCode)
        Assert.Contains("\"state\":\"ordinaryExhaustion\"", retiredOutput)
        Assert.Contains("\"waitStatus\":\"ordinaryExhaustion\"", retiredOutput)
        Assert.Empty retiredError
