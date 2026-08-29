namespace FS.GG.Coord.Cli.Tests

open System
open System.IO
open Xunit
open FS.GG.Coord
open FS.GG.Coord.Types
open FS.GG.Coord.Cli
open FS.GG.Coord.GitHub
open FS.GG.Coord.GitHub.Transport

module DeliveryApplicationTests =
    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let commentWithId id body : Driver.ReviewComment = { Id = id; Url = $"https://example.test/{id}"; Body = body }
    let comment body = commentWithId 1L body

    let guardedLandingFacts claimGeneration : Delivery.Snapshot =
        { Freshness =
            { ItemRef = ".github#2131"
              ClaimGeneration = claimGeneration
              Executor = "wren-c948"
              Branch = "item/2131-pnext-item-protocol"
              Worktree = "/tmp/2131"
              PullRequest = Some 2174
              HeadSha = String.replicate 40 "a"
              DeclaredPaths = Delivery.Known [ "src/FS.GG.Coord.Cli" ]
              BoardState = "In review" }
          ItemBranchCanonical = true
          ClosingLinkageCanonical = true
          PathsVerified = true
          InReview = true
          Review = Some { MarkerValid = true; Subject = Some ".github#2131/pr/2174"; ClaimGeneration = Some claimGeneration; BaseSha = Some(String.replicate 40 "b"); CriticIdentity = Some "critic"; HeadSha = Some(String.replicate 40 "a"); Rounds = [ 1 ]; RepairPhase = false; ChecksGreen = true; HostAccepted = true; RuntimeRouteEvidence = Some(Driver.NotMeaningful "pure adapter test"); DiffAuditRequired = false; DiffAuditHead = None }
          ReviewProblem = None
          Landable = true
          Merged = false
          MergeReachable = false
          IssueClosed = false
          BoardDone = false
          ClaimReleased = false
          PendingWrites = 0
          CleanupEligible = false
          ObligationsDeclared = true
          Obligations = []
          ParkedReason = None }

    let review id url body : Driver.ReviewComment = { Id = id; Url = url; Body = body }

    [<Fact>]
    let ``completion writer consumes the shared decision instead of rebuilding admission`` () =
        let writer = File.ReadAllText(Path.Combine(repositoryRoot, "src/FS.GG.Coord.Cli.Lifecycle/LiveHandlers.fs"))
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(writer, "Delivery\\.decideCompletion\\b").Count)
        Assert.Equal(
            1,
            System.Text.RegularExpressions.Regex.Matches(
                writer,
                "Delivery\\.CompletionDecision\\.ProjectCompletion"
            ).Count
        )

        let doneStart = writer.IndexOf("let private runDone", StringComparison.Ordinal)
        let doneEnd = writer.IndexOf("let doneCmd", doneStart, StringComparison.Ordinal)
        let doneWriter = writer.Substring(doneStart, doneEnd - doneStart)
        let selfHostReplay = doneWriter.IndexOf("Done.selfHostReplayState", StringComparison.Ordinal)
        let createReceipt = doneWriter.IndexOf("Delivery.createCompletionReceipt", StringComparison.Ordinal)
        let receipt = doneWriter.IndexOf("Writes.deliveryCompletionReceipt", StringComparison.Ordinal)
        let issueClose = doneWriter.IndexOf("Writes.closeIssueCompleted", receipt, StringComparison.Ordinal)
        let boardDone = doneWriter.IndexOf("Board.boardWrite", issueClose, StringComparison.Ordinal)
        let claimRelease = doneWriter.IndexOf("Writes.release", boardDone, StringComparison.Ordinal)
        Assert.True(receipt >= 0, "delivery completion receipt writer is not wired")
        Assert.True(selfHostReplay >= 0, "delivery completion does not inspect durable self-host replay")
        Assert.True(createReceipt > selfHostReplay, "completion authority can be minted before self-host replay agrees")
        Assert.True(issueClose > receipt, "a corrected issue is closed before the completion receipt")
        Assert.True(boardDone > receipt, "Status=Done is projected before the completion receipt")
        Assert.True(claimRelease > boardDone, "the claim is released before the receipt and board projection")
        Assert.Contains("Delivery.advance", doneWriter)
        Assert.Contains("Delivery.createCompletionReceipt", doneWriter)
        Assert.DoesNotContain("Writes.doneReceipt", doneWriter)
        Assert.DoesNotContain("fsgg:done-receipt v=1", doneWriter)

    [<Fact>]
    let ``#2207 client delivery adapter retains malformed parser diagnostics`` () =
        let malformed =
            [ review 10L "https://reviews/initial" "<!-- fsgg:review-decision/v2 -->\n{}" ]
        let parsed, problem = FS.GG.Coord.Cli.Lifecycle.LiveHandlers.deliveryReviewEvidence true malformed
        let facts = { guardedLandingFacts "claim-generation-a" with Review = parsed; ReviewProblem = problem }

        match Delivery.inspect facts with
        | Delivery.Next transition ->
            match transition.Action with
            | Delivery.RefreshReview reason -> Assert.Contains("required field is missing", reason)
            | action -> failwithf "expected malformed review refresh, got %A" action
        | Delivery.NoVerdict reason -> failwith reason

    [<Fact>]
    let ``#2207 client delivery adapter accepts a real multi-round chain for guarded land`` () =
        let chain =
            StructuredFixtures.acceptedReviewComments
                "FS-GG/.github#2131/pr/2174" (String.replicate 40 "a") "kestrel-1"
            |> List.map (fun (id, url, body) -> review id url body)
        let parsed, problem = FS.GG.Coord.Cli.Lifecycle.LiveHandlers.deliveryReviewEvidence true chain
        let facts = { guardedLandingFacts "claim-generation-a" with Review = parsed; ReviewProblem = problem }

        match Delivery.inspect facts with
        | Delivery.Next transition -> Assert.Equal(Delivery.GuardedLand, transition.Action)
        | Delivery.NoVerdict reason -> failwith reason

    let private renderDelivery format facts =
        let opts =
            match Options.parse [ "delivery"; "--snapshot"; "unused.json"; format ] with
            | Ok parsed -> parsed
            | Error error -> failwith error

        let original = Console.Out
        use output = new StringWriter()
        Console.SetOut output
        try
            let code = DeliveryApplication.render opts facts
            code, output.ToString()
        finally
            Console.SetOut original

    [<Theory>]
    [<InlineData("--json")>]
    [<InlineData("--text")>]
    let ``#2773 repairReviewHandoff renders its concrete problem`` format =
        let facts = { guardedLandingFacts "claim-generation-a" with PathsVerified = false }
        let code, rendered = renderDelivery format facts

        Assert.Equal(0, code)
        Assert.Contains("repairReviewHandoff", rendered)
        Assert.Contains("declared paths are not verified", rendered)

    [<Fact>]
    let ``#2773 delivery and verify-paths project every admission case identically`` () =
        let expected =
            function
            | Delivery.DeclaredPath
            | Delivery.GeneratedPath
            | Delivery.MandatorySddPath -> true
            | Delivery.UndeclaredAuthoredPath
            | Delivery.UnknownPath -> false

        let admissions =
            [ Delivery.DeclaredPath
              Delivery.GeneratedPath
              Delivery.MandatorySddPath
              Delivery.UndeclaredAuthoredPath
              Delivery.UnknownPath ]

        for admission in admissions do
            let classification: Delivery.PathClassification =
                { Path = $"fixture/%A{admission}"
                  Admission = admission
                  Reason = "fixture"
                  AuthorityRevisions = [] }

            let delivery = Client.projectPathVerdict Client.DeliveryReceiptProjection [ classification ]
            let verifyPaths = Client.projectPathVerdict Client.VerifyPathsProjection [ classification ]

            Assert.Equal(expected admission, delivery)
            Assert.Equal(delivery, verifyPaths)

    [<Fact>]
    let ``#2131 non-empty obligation receipt is head-bound and verifies only its declared id`` () =
        let comments =
            [ comment "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a -->"
              comment "<!-- fsgg:delivery-receipt id=nuget head=head-a evidence=https://nuget.example/package -->" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok [ obligation ] ->
            Assert.Equal("nuget", obligation.Id)
            Assert.Equal("publication", obligation.Kind)
            Assert.True(obligation.Verified)
        | other -> failwithf "expected one verified obligation, got %A" other

    [<Fact>]
    let ``#2239 version-bearing obligation and receipt ids are accepted`` () =
        let comments =
            [ commentWithId 17L "<!-- fsgg:delivery-obligation id=new-sdd-workspace-0.9.0 kind=publication head=head-a -->"
              commentWithId 18L "<!-- fsgg:delivery-receipt id=new-sdd-workspace-0.9.0 head=head-a evidence=https://nuget.example/package -->" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok [ obligation ] ->
            Assert.Equal("new-sdd-workspace-0.9.0", obligation.Id)
            Assert.True(obligation.Verified)
        | other -> failwithf "expected one verified version-bearing obligation, got %A" other

    [<Fact>]
    let ``#2239 malformed obligation ids name their comment and field`` () =
        let comments = [ commentWithId 19L "<!-- fsgg:delivery-obligation id=New-Sdd kind=publication head=head-a -->" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Error reason ->
            Assert.Contains("19", reason)
            Assert.Contains("id", reason)
        | other -> failwithf "expected malformed id refusal, got %A" other

    [<Fact>]
    let ``#2239 malformed receipt ids name their comment and field`` () =
        let comments =
            [ comment "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a -->"
              commentWithId 20L "<!-- fsgg:delivery-receipt id=New-Sdd head=head-a evidence=https://nuget.example/package -->" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Error reason ->
            Assert.Contains("20", reason)
            Assert.Contains("id", reason)
        | other -> failwithf "expected malformed receipt id refusal, got %A" other

    [<Fact>]
    let ``#2131 stale and undeclared obligation facts are refused`` () =
        match DeliveryApplication.obligationsFromComments "head-b" [ comment "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a -->" ] with
        | Error reason -> Assert.Contains("stale", reason)
        | other -> failwithf "expected stale declaration refusal, got %A" other

        match DeliveryApplication.obligationsFromComments "head-a" [] with
        | Error reason -> Assert.Contains("undeclared", reason)
        | other -> failwithf "expected undeclared refusal, got %A" other

    // .github#2347: `obligationDeclaration`/`obligationReceipt`/the `none` sentinel anchored their
    // regex against the comment's ENTIRE trimmed body, so the org's universal writing style — marker
    // line, blank line, explanatory prose — read as malformed (declaration/receipt) or undeclared
    // (none), even though the marker was correctly the comment's own leading line. `.github#2221`
    // made this identical whole-body-to-whole-line correction for review markers; this applies it to
    // the three delivery markers, which never received it.

    [<Fact>]
    let ``#2347 a declaration with trailing explanatory prose parses successfully`` () =
        let comments =
            [ comment "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a -->\n\nThis obligation covers publishing the nuget package once the merge lands."
              comment "<!-- fsgg:delivery-receipt id=nuget head=head-a evidence=https://nuget.example/package -->\n\nPublished and verified on both feeds." ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok [ obligation ] ->
            Assert.Equal("nuget", obligation.Id)
            Assert.True(obligation.Verified)
        | other -> failwithf "expected one verified obligation parsed past the trailing prose, got %A" other

    [<Fact>]
    let ``#2347 the none sentinel with trailing explanatory prose parses successfully`` () =
        let comments =
            [ comment "<!-- fsgg:delivery-obligations none head=head-a -->\n\nNo package, deployment, or registry surface moves in this change." ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok [] -> ()
        | other -> failwithf "expected the none sentinel to clear past the trailing prose, got %A" other

    [<Fact>]
    let ``#2347 a marker merely quoted later in a comment, never its own leading line, stays inert`` () =
        // The comment does not itself start with the declaration prefix, so it is excluded by the
        // same `StartsWith` filter round 1 (.github#2264) already relies on — the leading-line fix
        // must not loosen that boundary.
        let comments =
            [ comment "For context, a declaration will look like:\n<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a -->\nonce it is posted." ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Error reason -> Assert.Contains("undeclared", reason)
        | other -> failwithf "expected the quoted marker to stay inert and read as undeclared, got %A" other

    [<Fact>]
    let ``#2347 trailing text appended to the marker's own line, not a new line, is still malformed`` () =
        let comments =
            [ comment "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a --> and more on the same line" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Error reason -> Assert.Contains("malformed", reason)
        | other -> failwithf "expected same-line trailing text to remain malformed, got %A" other

    [<Fact>]
    let ``#2347 the real kit-0.48.0 declaration from .github#2264 PR #2271 (comment 5225891717) parses`` () =
        // Reproduced verbatim (https://github.com/FS-GG/.github/issues/comments/5225891717) — the exact
        // shape the issue measured as unparseable in production: marker line, blank line, prose.
        let body =
            "<!-- fsgg:delivery-obligation id=kit-0.48.0 kind=publication head=366b28a43251962de4a03a4fdac39651dc9b72e9 -->\n\n\
This PR edits `.claude/skills/check-board/references/deep-detail.md` and its `.agents` twin, plus\n\
`mechanical-reconciliation.md` in both skill roots. `check-board` is one of the four skills\n\
`registry/repos.yml`'s `kit:` rows pack, so the packed kit manifest changes and `FS.GG.Kit` must be\n\
released past the newest published version.\n\n\
This worker does NOT tag or publish — release sequencing is the host's, per explicit dispatch\n\
instruction. The obligation remains open (no `fsgg:delivery-receipt` yet) until the merged commit is\n\
tagged `kit/v0.48.0` and the identical artifact is published to GitHub Packages and nuget.org."
        match DeliveryApplication.obligationsFromComments "366b28a43251962de4a03a4fdac39651dc9b72e9" [ comment body ] with
        | Ok [ obligation ] ->
            Assert.Equal("kit-0.48.0", obligation.Id)
            Assert.Equal("publication", obligation.Kind)
            Assert.False(obligation.Verified)
        | other -> failwithf "expected the real production declaration to parse with a real verdict, got %A" other

    // .github#2544: `obligationsFromComments` selected its candidates with a RAW, untrimmed
    // `Body.StartsWith` while every parse below that used `leadingLine`, which trims first. The pre-filter
    // was therefore strictly STRICTER than the parser it fed, and a declaration opening with a newline or a
    // space — which `leadingLine` was written to accept — was discarded before `leadingLine` ever ran.
    //
    // Legs A-E below are the exact matrix the issue measured against the real engine before it was filed.
    // A and D were already green and are kept as controls that pin `.github#2347`'s fix in place; B, C and
    // E were red. Leg E is NOT a parse change: a marker that is not the comment's own leading line stays
    // INERT, and only the diagnostic learns to name the comment carrying it. The legs after E are that
    // inertness boundary itself — if the trimmed pre-filter ever made a quoted or fenced marker live again,
    // it would have broken exactly what `.github#2347` acceptance 2 and the `.github#2264` round-1
    // anchoring fix exist to protect.
    let private noneMarker = "<!-- fsgg:delivery-obligations none head=head-a -->"

    [<Fact>]
    let ``#2544 leg A the none declaration at byte 0 parses`` () =
        match DeliveryApplication.obligationsFromComments "head-a" [ comment noneMarker ] with
        | Ok [] -> ()
        | other -> failwithf "expected the byte-0 declaration to clear, got %A" other

    [<Fact>]
    let ``#2544 leg B a leading newline before the marker parses exactly as byte 0 does`` () =
        match DeliveryApplication.obligationsFromComments "head-a" [ comment ("\n" + noneMarker) ] with
        | Ok [] -> ()
        | other -> failwithf "expected a leading newline to parse as byte 0 does, got %A" other

    [<Fact>]
    let ``#2544 leg C a leading space before the marker parses exactly as byte 0 does`` () =
        match DeliveryApplication.obligationsFromComments "head-a" [ comment (" " + noneMarker) ] with
        | Ok [] -> ()
        | other -> failwithf "expected a leading space to parse as byte 0 does, got %A" other

    [<Fact>]
    let ``#2544 leg D the marker, a blank line, then prose still parses`` () =
        let body = noneMarker + "\n\nNo package, deployment, or registry surface moves in this change."
        match DeliveryApplication.obligationsFromComments "head-a" [ comment body ] with
        | Ok [] -> ()
        | other -> failwithf "expected .github#2347's fix to remain green, got %A" other

    [<Fact>]
    let ``#2544 leg E prose above the marker stays inert, and the refusal names that comment`` () =
        // The shape four independent lanes posted in a single session, each believing they had declared.
        let body = "## Post-merge obligations: **none**\n\n" + noneMarker
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 4242L body ] with
        | Ok _ -> failwith "leg E must NOT become a live declaration; only its diagnostic changes"
        | Error reason ->
            Assert.Contains("undeclared", reason)
            Assert.Contains("4242", reason)
            Assert.Contains("leading line", reason)

    [<Fact>]
    let ``#2544 a real obligation and its receipt parse when both bodies open with a newline`` () =
        // The whole matrix above uses the `none` sentinel; this drives the same repair through the
        // declaration and receipt grammars, which are separately filtered and separately parsed.
        let comments =
            [ commentWithId 51L "\n<!-- fsgg:delivery-obligation id=kit-0.49.0 kind=publication head=head-a -->\n\nTags and publishes the kit."
              commentWithId 52L "\n<!-- fsgg:delivery-receipt id=kit-0.49.0 head=head-a evidence=https://nuget.example/kit -->" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok [ obligation ] ->
            Assert.Equal("kit-0.49.0", obligation.Id)
            Assert.Equal("publication", obligation.Kind)
            Assert.True(obligation.Verified)
        | other -> failwithf "expected the newline-led declaration and receipt to parse, got %A" other

    [<Fact>]
    let ``#2544 a marker inside a fenced block is still inert once the pre-filter trims`` () =
        // The fence is the comment's leading line, so the marker on line 2 is not a declaration. This is
        // the boundary the trimmed pre-filter must not cross, stated as an executed leg rather than a claim.
        let body = "```\n" + noneMarker + "\n```"
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 61L body ] with
        | Ok _ -> failwith "a fenced marker must not become a live declaration"
        | Error reason ->
            Assert.Contains("undeclared", reason)
            Assert.Contains("61", reason)

    [<Fact>]
    let ``#2544 a marker quoted inside a sentence is inert and is not accused of being a declaration`` () =
        // Prose that merely mentions a marker mid-line is not a misplaced declaration, so the sharper
        // diagnostic must not point at it — a message that names every comment discussing the protocol is
        // no more actionable than the one it replaces.
        let body = $"For context, a declaration will look like `{noneMarker}` once it is posted."
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 62L body ] with
        | Ok _ -> failwith "a marker quoted inside a sentence must not become a live declaration"
        | Error reason ->
            Assert.Contains("undeclared", reason)
            Assert.DoesNotContain("62", reason)

    [<Fact>]
    let ``#2544 a comment leading with a receipt is not reported as carrying an inert marker`` () =
        // Its leading line IS a marker, so "the marker is not this comment's leading line" would be a
        // false description of it even though a declaration marker sits further down.
        let body =
            "<!-- fsgg:delivery-receipt id=kit head=head-a evidence=https://nuget.example/kit -->\n\n\
<!-- fsgg:delivery-obligation id=kit kind=publication head=head-a -->"
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 63L body ] with
        | Ok _ -> failwith "a receipt with no declaration must still refuse"
        | Error reason ->
            Assert.Contains("undeclared", reason)
            Assert.DoesNotContain("63", reason)

    // ROUND-1 REVIEW REPAIR (.github#2544, critic `tern-bde7`). The first cut of this fix trimmed leading
    // whitespace without limit, which did not merely make an inert marker live — it made the parse
    // FAIL-OPEN and let a BYSTANDER break somebody else's PR. Four spaces (or a tab) opens a CommonMark
    // indented code block, so a comment whose first content is an indented code SAMPLE was read as a real
    // declaration. The limit below is CommonMark's own, and it is exactly the line between invisible and
    // visible: 0-3 spaces render as nothing, 4+ render as a code block. `independent-review.md:16`'s
    // generated review-policy block already states the rule this restores — a marker "inside a fence, an
    // indented code block, or prose that only mentions it" is inert.

    let private indentedSample = "    <!-- fsgg:delivery-obligation id=example kind=publication head=head-a -->"

    [<Fact>]
    let ``#2544 round 1: a bystander's indented code sample cannot destroy a valid declaration`` () =
        // THE FINDING, as the critic executed it against live PR bytes: a good `none` declaration already
        // on the PR, plus one added comment carrying an indented sample. Unlimited trimming turned the
        // sample into a second declaration and the pair then collided, so the refusal accused the author
        // of combining `none` with obligations when somebody had merely posted documentation.
        let comments = [ commentWithId 71L noneMarker; commentWithId 72L indentedSample ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok [] -> ()
        | other -> failwithf "a code sample must not disturb an existing valid declaration, got %A" other

    [<Fact>]
    let ``#2544 round 1: an indented declaration and receipt pair does not read as verified`` () =
        // The fail-OPEN direction, and the one this subsystem must never move in: under unlimited
        // trimming this pair reported `Verified = true` — a discharged obligation nobody declared.
        let comments =
            [ commentWithId 73L "    <!-- fsgg:delivery-obligation id=kit kind=publication head=head-a -->"
              commentWithId 74L "    <!-- fsgg:delivery-receipt id=kit head=head-a evidence=https://nuget.example/kit -->" ]
        match DeliveryApplication.obligationsFromComments "head-a" comments with
        | Ok obligations ->
            failwithf "an indented code sample must never produce an obligation, let alone a verified one, got %A" obligations
        | Error reason -> Assert.Contains("undeclared", reason)

    [<Fact>]
    let ``#2544 round 1: a four-space indented marker is inert, and is NAMED rather than silently ignored`` () =
        // Both halves matter. Narrowing alone would send this declaration back to being invisible, which
        // is the failure the whole row exists to kill — so criterion 2's diagnostic must reach it too.
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 75L ("    " + noneMarker) ] with
        | Ok _ -> failwith "a four-space indented marker is a code block, not a declaration"
        | Error reason ->
            Assert.Contains("undeclared", reason)
            Assert.Contains("75", reason)
            Assert.Contains("leading line", reason)

    [<Fact>]
    let ``#2544 round 1: a tab-indented marker is inert, and is NAMED`` () =
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 76L ("\t" + noneMarker) ] with
        | Ok _ -> failwith "a tab is one tab stop, so a tab-indented marker is a code block"
        | Error reason ->
            Assert.Contains("undeclared", reason)
            Assert.Contains("76", reason)

    [<Fact>]
    let ``#2544 round 1: three spaces still declares, because three spaces render invisibly`` () =
        // The boundary is CommonMark's, so it has to be pinned from BOTH sides: this is the widest
        // indentation a reader cannot see, and leg C would be arbitrary if this one did not hold.
        match DeliveryApplication.obligationsFromComments "head-a" [ comment ("   " + noneMarker) ] with
        | Ok [] -> ()
        | other -> failwithf "three spaces render as nothing, so the marker is still the leading line, got %A" other

    [<Fact>]
    let ``#2544 round 1: blank lines above a correctly-indented marker still declare`` () =
        match DeliveryApplication.obligationsFromComments "head-a" [ comment ("\n\n  " + noneMarker) ] with
        | Ok [] -> ()
        | other -> failwithf "blank lines are not indentation, got %A" other

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // THE SHARED CROSS-LANGUAGE CORPUS (.github#2563). This is where the boundary is now STATED.
    //
    // `#2544` collapsed a rule that lived in two places INSIDE the engine into one function, and its
    // round-1 repair then re-created a weaker version of the same hazard ACROSS the F#/Python language
    // boundary: `DeliveryApplication.leadingLine` and `check-kit-published-coherence.py`'s
    // `_leading_line` each held their own copy of the CommonMark indent limit, each side pinned its
    // copy with its OWN fixture legs, and the only coupling was two prose sentences that nothing read.
    //
    // A one-sided edit reds that side. What was caught by nothing was a COORDINATED one-sided edit —
    // moving one language's constant AND updating that same language's legs to match. That is not
    // exotic; it is what a careful engineer does when they believe they are fixing a bug, and the
    // fixtures agreed with them the whole way.
    //
    // `tests/delivery-leading-line/corpus.json` is the repair. Both sides consume it and NEITHER keeps
    // a private leg asserting a SINGLE COMMENT BODY's declares/inert verdict, so a coordinated edit has
    // nowhere to hide: move the limit here and this test reds against the corpus; edit the corpus to
    // restore it and the `kit-published-coherence` fixture reds instead, because it grades
    // `obligation_declarations` — the gate's real entry point — against the same verdicts.
    //
    // WHAT THE CORPUS DOES NOT SUBSUME, AND IT IS RIGHT ABOVE THIS COMMENT. The four `#2544` legs at
    // `indentedSample` (:304, used at :307 and :492) and at the :318 declaration+receipt pair carry
    // four-space bodies in the DECLARATION form and stay HERE, privately. They are not duplicates the
    // corpus could absorb: :307 and :318 are MULTI-COMMENT scenarios that a one-body-one-verdict corpus
    // cannot express — and :318 turns on a `fsgg:delivery-receipt` marker `obligation_declarations`
    // never parses, so the gate has no answer even in principle — while :492 asserts this engine's
    // diagnostic WORDING, which the gate does not emit. They make THIS side stricter, never more
    // permissive, so they cannot mask a divergence: under the coordinated one-sided edit they red
    // alongside the corpus, `Failed: 4, Passed: 801`. Expect that, and do not "fix" it by loosening
    // them. The corpus's residual is exactly this — it couples SINGLE-COMMENT bodies only.
    //
    // THIS TEST DRIVES `obligationsFromComments`, NOT `leadingLine`. Transcribing a parser into another
    // language to check it is literally this row's bug class, and checking only the private helper
    // would leave the candidate pre-filter — the half `#2544` was actually filed about — ungraded.
    //
    // The `none`-form indent legs above stay where they are and are NOT duplicated into the corpus.
    // `obligation_declarations` skips `<!-- fsgg:delivery-obligations none … -->` by design, so Python
    // answers "no declarations" for it whether it is indented or not and has no verdict to compare.
    // That is the honest reason those legs cannot be shared, recorded rather than glossed.

    /// The corpus is repository DATA, not a build output — no `.fsproj` in this repository copies
    /// content beside its test assembly — so this walks up from the assembly to the repository root,
    /// the idiom `RuleSubsetTests` and `DocumentedInvocationTests` already use. The sentinel IS the
    /// corpus, so a tree that does not carry it fails naming the path it looked for rather than
    /// walking off the filesystem root into a null reference.
    let private corpusPath =
        let relative = Path.Combine("tests", "delivery-leading-line", "corpus.json")

        let rec up (d: DirectoryInfo) =
            if isNull (box d) then
                failwith
                    $"DeliveryApplicationTests: walked past the filesystem root without finding %s{relative}. The shared cross-language leading-line corpus (.github#2563) is the only statement of this boundary, and this suite refuses to pass without reading it."
            elif File.Exists(Path.Combine(d.FullName, relative)) then
                Path.Combine(d.FullName, relative)
            else
                up d.Parent

        up (DirectoryInfo AppContext.BaseDirectory)

    /// STATED, not derived from the file. A count read out of the corpus could be edited in the same
    /// breath as the entry it counts, which is the vacuity `.github#2534` (an empty-corpus green) and
    /// `.github#1768` (157 passing legs while the script was dying mid-run) each measured. The Python
    /// consumer states its own copy of this number independently, so adding or removing an entry is a
    /// deliberate three-file edit rather than something that can happen to a corpus quietly.
    let private corpusEntryCount = 21

    [<Fact>]
    let ``#2563 the engine agrees with the shared cross-language leading-line corpus, entry for entry`` () =
        use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText corpusPath)
        let head = doc.RootElement.GetProperty("head").GetString()
        let entries = doc.RootElement.GetProperty("entries").EnumerateArray() |> Seq.toList
        let verdictOf (e: System.Text.Json.JsonElement) = e.GetProperty("verdict").GetString()

        // NON-VACUITY, asserted before a single verdict is compared. A corpus that is missing, empty,
        // truncated, or reduced to one verdict class must RED here — a shorter corpus than this suite
        // claims to check is exactly how a coupling stops coupling without anyone noticing.
        Assert.Equal(corpusEntryCount, List.length entries)

        Assert.True(
            entries |> List.exists (fun e -> verdictOf e = "declares"),
            "the corpus carries no `declares` entry, so it pins no lower bound on the indent tolerance"
        )

        Assert.True(
            entries |> List.exists (fun e -> verdictOf e = "inert"),
            "the corpus carries no `inert` entry, so it pins no upper bound and could not catch a fail-open"
        )

        // AND THE DISCRIMINATING SHAPES SURVIVE, which the count alone does not buy. A stated count
        // forces a deliberate edit to add or remove an entry, but an author moving the limit could
        // delete exactly the entries that discriminate and lower both stated counts, leaving two
        // implementations that disagree over a corpus with nothing left to disagree ABOUT. `spaces-3`
        // and `spaces-4` are the two shapes that discriminate a limit move in either direction — raise
        // the limit and `spaces-4` changes verdict, lower it and `spaces-3` does — so they must be
        // PRESENT.
        //
        // Presence, deliberately, and not a required verdict or a required disagreement between them. A
        // limit that legitimately moved to 8 in BOTH languages is a coherent change this suite must let
        // through, and it leaves those two entries agreeing; a leg demanding they disagree would red on
        // exactly the correct action, which is how a gate teaches people to edit it out. The DIRECTION
        // lives in the corpus alone — restating it here would re-create the second copy .github#2563
        // exists to remove.
        let present name =
            entries |> List.exists (fun e -> e.GetProperty("name").GetString() = name)

        Assert.True(
            present "spaces-3" && present "spaces-4",
            "the corpus must keep `spaces-3` and `spaces-4`: they are the two shapes either side of the CommonMark indented-code-block limit, and a corpus that has lost them can no longer tell a moved limit from an unmoved one"
        )

        let failures = ResizeArray<string>()
        let mutable executed = 0

        for entry in entries do
            let name = entry.GetProperty("name").GetString()
            let body = entry.GetProperty("body").GetString()
            let verdict = verdictOf entry
            executed <- executed + 1

            match verdict, DeliveryApplication.obligationsFromComments head [ commentWithId 2563L body ] with
            // A `declares` entry must produce THE obligation the corpus names, not merely some `Ok`.
            | "declares", Ok [ obligation ] when obligation.Id = name -> ()
            // An `inert` entry must be inert AND NAMED — both halves, because narrowing without the
            // diagnostic sends a real declaration back to being invisible, which is the failure
            // `#2544` exists to kill rather than a lesser version of it.
            | "inert", Error reason when reason.Contains "leading line" -> ()
            | "inert", Error reason ->
                failures.Add $"%s{name}: inert as expected, but the refusal never names the leading-line rule, so an author who indented a real declaration is not told why it did not take: %s{reason}"
            | ("declares" | "inert"), actual -> failures.Add $"%s{name}: corpus says %s{verdict}, engine said %A{actual}"
            | other, _ -> failures.Add $"%s{name}: corpus carries the unknown verdict %s{other}; only `declares` and `inert` are defined"

        // Every entry READ was also EXECUTED. `failures.Count = 0` alone cannot tell "all agreed" from
        // "the loop never ran" (.github#1768).
        Assert.Equal(List.length entries, executed)

        if failures.Count > 0 then
            failwith
                $"""the engine disagrees with %s{corpusPath} in %d{failures.Count} of %d{executed} entries. That corpus is the ONE statement of this boundary and `check-kit-published-coherence.py` is graded against the same verdicts, so a change here that is right must be made THERE — changing the limit and this suite together is precisely the coordinated one-sided edit .github#2563 exists to catch.
%s{String.Join("\n", failures)}"""

    [<Fact>]
    let ``#2544 round 1: the diagnostic does not tell a documentation author to make their sample declare`` () =
        // The advice was unconditional — and under the indented-code-block case it was advice to perform
        // the exact mutation that turns a code sample into a live declaration.
        match DeliveryApplication.obligationsFromComments "head-a" [ commentWithId 77L indentedSample ] with
        | Ok _ -> failwith "expected the indented sample to stay inert"
        | Error reason ->
            Assert.DoesNotContain("edit that comment to lead with it", reason)
            Assert.Contains("meant to declare", reason)
            Assert.Contains("code sample", reason)

    // Round-1 review repair (.github#2264 PR #2271): `FS.GG.Coord.Cli.Lifecycle.LiveHandlers.outstandingObligations` is the extracted,
    // directly-testable core of `reconcile`'s lifecycle fold, which previously scanned live PR comments
    // with bulk, unanchored `.Contains` — a comment quoting ANOTHER obligation's receipt marker in prose
    // made `Outstanding` compute `false` while a genuine obligation was still open. These tests reproduce
    // the critic's exact scenario over the REAL production parser it now reuses.
    let private commentBody id url body : Reads.CommentBody = { Id = id; Url = url; Body = body }

    [<Fact>]
    let ``#2264 round 1: a receipt quoted in prose cannot clear a different obligation`` () =
        let comments : Reads.CommentBody list =
            [ commentBody 1L "https://example.test/1" "<!-- fsgg:delivery-obligation id=a kind=publication head=head-a -->"
              commentBody 2L "https://example.test/2" "<!-- fsgg:delivery-obligation id=b kind=publication head=head-a -->"
              commentBody 3L "https://example.test/3" "<!-- fsgg:delivery-receipt id=a head=head-a evidence=https://example.test/a -->"
              // Quotes `b`'s receipt shape in prose, in the org's ordinary reviewer-comment style — never
              // its own comment's entire body, so the anchored parser cannot mistake it for a real receipt.
              commentBody 4L "https://example.test/4" "For context, `b`'s receipt will look like:\n`<!-- fsgg:delivery-receipt id=b head=head-a evidence=https://example.test/b -->`\nonce it lands." ]
        Assert.True(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.outstandingObligations (Ok "head-a") (Ok comments))

    [<Fact>]
    let ``#2264 round 1: every obligation genuinely receipted clears Outstanding`` () =
        let comments : Reads.CommentBody list =
            [ commentBody 1L "https://example.test/1" "<!-- fsgg:delivery-obligation id=a kind=publication head=head-a -->"
              commentBody 2L "https://example.test/2" "<!-- fsgg:delivery-receipt id=a head=head-a evidence=https://example.test/a -->" ]
        Assert.False(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.outstandingObligations (Ok "head-a") (Ok comments))

    [<Fact>]
    let ``#2264 round 1: an unreadable head or comment thread fails closed as Outstanding`` () =
        Assert.True(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.outstandingObligations (Error(Errors.NotFound "no head")) (Ok []))
        Assert.True(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.outstandingObligations (Ok "head-a") (Error(Errors.NotFound "no comments")))

    [<Fact>]
    let ``#2264 round 1: a malformed or stale declaration fails closed as Outstanding`` () =
        let staleHead : Reads.CommentBody list =
            [ commentBody 1L "https://example.test/1" "<!-- fsgg:delivery-obligation id=a kind=publication head=old-head -->" ]
        Assert.True(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.outstandingObligations (Ok "head-a") (Ok staleHead))

    [<Fact>]
    let ``#2216 stale declaration identifies its comment and append-proof repair`` () =
        let comments =
            [ commentWithId 41L "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-a -->"
              commentWithId 42L "<!-- fsgg:delivery-obligation id=nuget kind=publication head=head-b -->" ]

        match DeliveryApplication.obligationsFromComments "head-b" comments with
        | Error reason ->
            Assert.Contains("comment 41", reason)
            Assert.Contains("edit it in place or delete it", reason)
            Assert.Contains("adding a declaration cannot repair it", reason)
        | other -> failwithf "expected stale declaration repair refusal, got %A" other

    [<Fact>]
    let ``#2131 delivery adapter refuses a stale claim generation before issuing a merge`` () =
        let facts = guardedLandingFacts "claim-generation-a"
        let transition =
            match Delivery.inspect facts with
            | Delivery.Next next -> next
            | Delivery.NoVerdict reason -> failwith reason
        let mutable mergeCalls = 0
        let attemptMerge () = mergeCalls <- mergeCalls + 1; "merge endpoint was called"

        match DeliveryApplication.guardedLanding transition.FreshnessToken transition.ActionKey facts (Some "claim-generation-b") (Some facts.Freshness.HeadSha) (Some(String.replicate 40 "b")) attemptMerge with
        | Ok result -> failwith result.Result
        | Error reason -> Assert.Contains("generation changed", reason)

        Assert.Equal(0, mergeCalls)

    [<Fact>]
    let ``#2360 guarded landing refuses a moved effective base and names both revisions`` () =
        let facts = guardedLandingFacts "claim-generation-a"
        let transition = Delivery.inspect facts |> function Delivery.Next next -> next | Delivery.NoVerdict reason -> failwith reason
        let mutable mergeCalls = 0
        let acceptedBase = String.replicate 40 "b"
        let movedBase = String.replicate 40 "c"
        let attemptMerge () = mergeCalls <- mergeCalls + 1

        match DeliveryApplication.guardedLanding transition.FreshnessToken transition.ActionKey facts (Some "claim-generation-a") (Some facts.Freshness.HeadSha) (Some movedBase) attemptMerge with
        | Ok _ -> failwith "a moved base authorized a merge"
        | Error reason ->
            Assert.Contains(acceptedBase, reason)
            Assert.Contains(movedBase, reason)

        Assert.Equal(0, mergeCalls)

    [<Fact>]
    let ``#2360 guarded landing emits the exact head and base receipt used by the conditional write`` () =
        let facts = guardedLandingFacts "claim-generation-a"
        let transition = Delivery.inspect facts |> function Delivery.Next next -> next | Delivery.NoVerdict reason -> failwith reason
        let acceptedBase = String.replicate 40 "b"

        match DeliveryApplication.guardedLanding transition.FreshnessToken transition.ActionKey facts (Some "claim-generation-a") (Some facts.Freshness.HeadSha) (Some acceptedBase) (fun () -> "merged") with
        | Error reason -> failwith reason
        | Ok receipt ->
            Assert.Equal(facts.Freshness.HeadSha, receipt.HeadSha)
            Assert.Equal(acceptedBase, receipt.BaseSha)
            Assert.Equal("merged", receipt.Result)

    // -- repair round 1 (critic `crake-0420`, PR #2301): the `declaredPaths` JSON wire shapes were only
    // proven by the critic executing the built CLI artifact by hand — this closes that with a committed
    // `dotnet test` gate over `DeliveryApplication.run`'s real snapshot-file boundary.

    /// A complete, otherwise-valid `delivery --snapshot` JSON document with `declaredPaths` substituted
    /// in verbatim, so each case below exercises only the ONE field under test.
    let private snapshotJson (declaredPathsJson: string) =
        $$"""{"freshness":{"itemRef":"FS-GG/.github#2233","claimGeneration":"fixture-claim","executor":"heron-d4fb","branch":"item/2233-fixture","worktree":"/tmp/fixture","pullRequest":42,"headSha":"fixture-head","declaredPaths":{{declaredPathsJson}},"boardState":"In review"},"itemBranchCanonical":true,"closingLinkageCanonical":true,"pathsVerified":true,"inReview":true,"review":{"markerValid":true,"criticIdentity":"curlew-ced5","headSha":"fixture-head","rounds":[1],"repairPhase":false,"checksGreen":true,"hostAccepted":true,"routeNotMeaningfulReason":"hermetic fixture"},"landable":true,"merged":false,"mergeReachable":false,"issueClosed":false,"boardDone":false,"claimReleased":false,"pendingWrites":0,"cleanupEligible":false,"obligationsDeclared":true,"obligations":[],"parkedReason":null}"""

    /// Runs `DeliveryApplication.run` over a real `--snapshot FILE` the way the live CLI is invoked,
    /// capturing stdout/stderr rather than reaching into the private JSON parser directly.
    let private runSnapshot (declaredPathsJson: string) : int * string * string =
        let path = Path.GetTempFileName()
        File.WriteAllText(path, snapshotJson declaredPathsJson)
        try
            match Options.parse [ "delivery"; "--snapshot"; path; "--json" ] with
            | Error message -> failwith message
            | Ok opts ->
                let originalOut = Console.Out
                let originalErr = Console.Error
                use capturedOut = new StringWriter()
                use capturedErr = new StringWriter()
                Console.SetOut capturedOut
                Console.SetError capturedErr
                try
                    let exitCode = DeliveryApplication.run opts
                    exitCode, capturedOut.ToString(), capturedErr.ToString()
                finally
                    Console.SetOut originalOut
                    Console.SetError originalErr
        finally
            File.Delete path

    [<Fact>]
    let ``#2233 declaredPaths as a plain array parses as Known and reaches guarded land`` () =
        let exitCode, out, _err = runSnapshot """["src/FS.GG.Coord.Cli"]"""
        Assert.Equal(0, exitCode)
        Assert.Contains("\"verdict\":\"next\"", out)
        Assert.Contains("\"action\":\"guardedLand\"", out)

    [<Fact>]
    let ``#2233 declaredPaths as an empty array is a genuine known omission`` () =
        let exitCode, out, _err = runSnapshot "[]"
        Assert.NotEqual(0, exitCode)
        Assert.Contains("\"verdict\":\"noVerdict\"", out)
        Assert.Contains("declared paths", out)
        Assert.DoesNotContain("were not read", out)

    [<Fact>]
    let ``#2233 declaredPaths as {unread: reason} refuses with a reason naming the read`` () =
        let exitCode, out, _err = runSnapshot """{"unread":"issue body fetch timed out"}"""
        Assert.NotEqual(0, exitCode)
        Assert.Contains("\"verdict\":\"noVerdict\"", out)
        Assert.Contains("were not read", out)
        Assert.Contains("issue body fetch timed out", out)

    [<Fact>]
    let ``#2233 declaredPaths as {declaredNone: true} refuses with the deliberate-omission reason`` () =
        let exitCode, out, _err = runSnapshot """{"declaredNone":true}"""
        Assert.NotEqual(0, exitCode)
        Assert.Contains("\"verdict\":\"noVerdict\"", out)
        Assert.Contains("Paths: none", out)

    [<Fact>]
    let ``#2233 declaredPaths as {undeclared: true} refuses with the never-declared reason`` () =
        let exitCode, out, _err = runSnapshot """{"undeclared":true}"""
        Assert.NotEqual(0, exitCode)
        Assert.Contains("\"verdict\":\"noVerdict\"", out)
        Assert.Contains("no Paths: line", out)

    [<Theory>]
    [<InlineData("42")>]
    [<InlineData("null")>]
    [<InlineData("""{"foo":"bar"}""")>]
    [<InlineData("""{"unread":""}""")>]
    let ``#2233 a malformed declaredPaths shape is refused, never a confident verdict`` (malformed: string) =
        let exitCode, out, err = runSnapshot malformed
        Assert.NotEqual(0, exitCode)
        Assert.DoesNotContain("\"verdict\":\"next\"", out)
        Assert.True(String.IsNullOrEmpty out, $"expected no verdict document on stdout for a malformed snapshot, got: %s{out}")
        // Every malformed shape names ITS OWN offending field ("declaredPaths" for the shape itself,
        // or "unread" for a malformed value nested inside an otherwise well-shaped object) — never a
        // silent fallback to a confident empty read.
        Assert.False(String.IsNullOrWhiteSpace err, "expected a non-empty diagnostic on stderr")
        Assert.True(err.Contains("declaredPaths") || err.Contains("unread"), $"expected the diagnostic to name the offending field, got: %s{err}")

    // ============================================================================================
    // .github#2395 — THE MERGE ELECTION AND THE AUTHORIZATION GROUNDED IN IT
    // ============================================================================================
    //
    // §11.2 row 3 of the fencing design declares TWO acts: *"`delivery` posts the merge election,
    // THEN writes the PR authorization marker NAMING it and bound to head."* The row's first landing
    // shipped only the second, with FOUR fields, and the consequence was not a narrower pass —
    // `scripts/check-claim-fence.py` returns at CHECK 1 on a marker missing `opkey`/`grant`, so its
    // check 4 was never evaluated on any real pull request while the fence workflow told operators
    // check 4 was failing for a known reason.
    //
    // WHAT THESE TESTS ARE FOR, AND WHAT THEY ARE NOT. They pin the PRODUCER: the six-field
    // authorization, the election that grounds it, the idempotence rule that keeps a repeated
    // `delivery` call from denying its own pull request, the lowest-id selection, and the
    // fail-closed refusal. They deliberately do NOT grade the producer against
    // `scripts/check-claim-fence.py`'s own required-field tuples: that bidirectional agreement leg
    // belongs to `.github#2719`, which declares that script and `tests/claim-fence`, and it must be
    // written after this lands because it reds until the six-field marker exists. The executed
    // demonstration that check 4 is REACHED and ABLE TO FAIL is recorded on this change's pull
    // request instead.

    /// `sha256("FS-GG/.github#2395\n5267541214\nFS-GG/.github\nmerge")`, lowercase hex.
    ///
    /// A LITERAL, AND DELIBERATELY NOT `Operation.compose`'s OWN ANSWER. Asserting the production
    /// path against itself would pass for any key it chose to compute, including a wrong one. This
    /// constant was computed out of band from design §3.3's own formula and is byte-identical to what
    /// `scripts/check-claim-fence.py`'s independent `compose_opkey` produces for the same four
    /// components — so it pins the WIRE AGREEMENT between the F# producer and the Python gate, which
    /// is the only property check 5 actually needs.
    let private expectedOpKey = "09ff79967fd2476062df93ab2b293f620d16e614f84276ded003a9e190e8f018"

    // -------------------------------------------------------------------------------------------
    // The authorization marker, pure
    // -------------------------------------------------------------------------------------------

    [<Fact>]
    let ``#2395 authorizationMarker renders all six fields the fence requires, in the gate's order`` () =
        let marker =
            FS.GG.Coord.Cli.Lifecycle.LiveHandlers.authorizationMarker
                "FS-GG/.github#2395"
                "5267541214"
                expectedOpKey
                "5309319124"
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        Assert.Equal(
            $"<!-- fsgg:pr-authorization v=1 item=FS-GG/.github#2395 gen=5267541214 opkey=%s{expectedOpKey} grant=5309319124 head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa -->",
            marker
        )

    let private authMarker gen head =
        FS.GG.Coord.Cli.Lifecycle.LiveHandlers.authorizationMarker "FS-GG/.github#2395" gen expectedOpKey "5309319124" head

    [<Fact>]
    let ``#2395 a body with no marker at all is rebound, not left missing`` () =
        let body = "Implements the thing.\n\nCloses #2395"
        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.rebindAuthorization body "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" "head-a" with
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationRebound updated ->
            Assert.Contains(authMarker "5267541214" "head-a", updated)
            Assert.Contains("Closes #2395", updated)
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationCurrent -> failwith "expected a rebind: the body carried no marker at all"

    [<Fact>]
    let ``#2395 a marker bound to a superseded head is rebound to the current one, not left stale`` () =
        let body = "Implements the thing.\n\n" + authMarker "5267541214" "head-old"
        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.rebindAuthorization body "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" "head-new" with
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationRebound updated ->
            Assert.Contains(authMarker "5267541214" "head-new", updated)
            Assert.DoesNotContain("head-old", updated)
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationCurrent -> failwith "expected a rebind: the marker's head was superseded"

    [<Fact>]
    let ``#2395 a marker naming a superseded GRANT is rebound, not left grounded in a lost election`` () =
        let stale =
            FS.GG.Coord.Cli.Lifecycle.LiveHandlers.authorizationMarker "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319000" "head-a"
        let body = "Implements the thing.\n\n" + stale
        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.rebindAuthorization body "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" "head-a" with
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationRebound updated ->
            Assert.Contains(authMarker "5267541214" "head-a", updated)
            Assert.DoesNotContain("5309319000", updated)
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationCurrent -> failwith "expected a rebind: the marker named a different election"

    // THE MIGRATION, AS A TEST RATHER THAN A PROMISE. Every pull request open when this landed carries
    // the FOUR-field marker the row's first landing wrote. Nothing rebinds it but this rule: a marker
    // that is not byte-identical to the freshly rendered one takes the rebound arm, so the next
    // `delivery` call upgrades it — one marker in, one marker out — with no cutover, no dual-shape
    // acceptance and no rebinding campaign.
    [<Fact>]
    let ``#2395 a legacy FOUR-field marker is replaced in place by the six-field one, never left beside it`` () =
        let legacy = "<!-- fsgg:pr-authorization v=1 item=FS-GG/.github#2395 gen=5267541214 head=head-a -->"
        let body = $"Implements the thing.\n\n{legacy}"
        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.rebindAuthorization body "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" "head-a" with
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationRebound updated ->
            let occurrences =
                System.Text.RegularExpressions.Regex.Matches(updated, System.Text.RegularExpressions.Regex.Escape "<!-- fsgg:pr-authorization").Count
            Assert.Equal(1, occurrences)
            Assert.Contains(authMarker "5267541214" "head-a", updated)
            Assert.DoesNotContain(legacy, updated)
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationCurrent -> failwith "expected a rebind: a four-field marker is not the six-field one"

    [<Fact>]
    let ``#2395 two markers collapse to exactly one, never left duplicated`` () =
        let stale = authMarker "111" "head-old"
        let alsoStale = authMarker "222" "head-older"
        let body = $"Implements the thing.\n\n{stale}\n\n{alsoStale}"
        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.rebindAuthorization body "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" "head-new" with
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationRebound updated ->
            let occurrences =
                System.Text.RegularExpressions.Regex.Matches(updated, System.Text.RegularExpressions.Regex.Escape "<!-- fsgg:pr-authorization").Count
            Assert.Equal(1, occurrences)
            Assert.Contains(authMarker "5267541214" "head-new", updated)
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationCurrent -> failwith "expected a rebind: two stale markers must collapse to one current one"

    [<Fact>]
    let ``#2395 a body already carrying exactly the desired marker is reported current, not rewritten`` () =
        let desired = authMarker "5267541214" "head-current"
        let body = $"Implements the thing.\n\n{desired}"
        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.rebindAuthorization body "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" "head-current" with
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationCurrent -> ()
        | FS.GG.Coord.Cli.Lifecycle.LiveHandlers.AuthorizationRebound updated -> failwithf "expected no rewrite for an already-current marker, got %s" updated

    // -------------------------------------------------------------------------------------------
    // The election marker, pure
    // -------------------------------------------------------------------------------------------

    [<Fact>]
    let ``#2395 electionMarker renders the six fields the fence requires plus the pr discriminator`` () =
        Assert.Equal(
            $"<!-- fsgg:merge-election v=1 opkey=%s{expectedOpKey} item=FS-GG/.github#2395 gen=5267541214 receiver=FS-GG/.github op=merge pr=9001 -->",
            DeliveryApplication.electionMarker expectedOpKey "FS-GG/.github#2395" "5267541214" "FS-GG/.github" 9001
        )

    let private electionComment id body : Driver.ReviewComment =
        { Id = id; Url = $"https://example.test/{id}"; Body = body }

    let private anElection pr =
        DeliveryApplication.electionMarker expectedOpKey "FS-GG/.github#2395" "5267541214" "FS-GG/.github" pr

    [<Fact>]
    let ``#2395 electionsFromComments reads a marker that opens the comment, with its fields`` () =
        match DeliveryApplication.electionsFromComments [ electionComment 700L (anElection 9001) ] with
        | [ election ] ->
            Assert.Equal(700L, election.Id)
            Assert.Equal(Some expectedOpKey, election.Fields.TryFind "opkey")
            Assert.Equal(Some "FS-GG/.github#2395", election.Fields.TryFind "item")
            Assert.Equal(Some "5267541214", election.Fields.TryFind "gen")
            Assert.Equal(Some "FS-GG/.github", election.Fields.TryFind "receiver")
            Assert.Equal(Some "merge", election.Fields.TryFind "op")
            Assert.Equal(Some "9001", election.Fields.TryFind "pr")
        | other -> failwithf "expected exactly one election, got %A" other

    [<Fact>]
    let ``#2395 trailing prose after the marker is outside it and pollutes no field`` () =
        match DeliveryApplication.electionsFromComments [ electionComment 700L (anElection 9001 + "\n\nMerge election for pr=9999 op=nonsense.") ] with
        | [ election ] ->
            Assert.Equal(Some "9001", election.Fields.TryFind "pr")
            Assert.Equal(Some "merge", election.Fields.TryFind "op")
        | other -> failwithf "expected exactly one election, got %A" other

    // THE ANCHORING BOUNDARY, AND IT IS THE FENCE'S RATHER THAN THIS MODULE'S CHOICE.
    // `scripts/check-claim-fence.py` matches the election with `re.match` on the RAW comment body and
    // never trims it, so a marker one byte from position 0 is INVISIBLE to the only reader that
    // grades it. A producer whose parse were more permissive than that reader would reuse an election
    // the fence cannot see, and would then grant an id check 4 refuses. Each leg below is one byte of
    // difference from the accepted case above.
    [<Theory>]
    [<InlineData("\n")>]
    [<InlineData(" ")>]
    [<InlineData("\t")>]
    [<InlineData("We elected this merge: ")>]
    let ``#2395 a marker that does not open the comment at byte 0 is not an election`` (prefix: string) =
        Assert.Empty(DeliveryApplication.electionsFromComments [ electionComment 700L (prefix + anElection 9001) ])

    [<Fact>]
    let ``#2395 a suffixed prefix is a different marker, never this one`` () =
        let note = (anElection 9001).Replace("fsgg:merge-election", "fsgg:merge-election-note")
        Assert.Empty(DeliveryApplication.electionsFromComments [ electionComment 700L note ])

    [<Fact>]
    let ``#2395 electionsOwnedBy keeps this opkey and this pull request, and nothing else`` () =
        let otherKey = String.replicate 64 "b"
        let comments =
            [ electionComment 700L (anElection 9001)
              electionComment 701L (anElection 9002)
              electionComment 702L (DeliveryApplication.electionMarker otherKey "FS-GG/.github#2395" "5267541214" "FS-GG/.github" 9001) ]
        match DeliveryApplication.electionsFromComments comments |> DeliveryApplication.electionsOwnedBy expectedOpKey 9001 with
        | [ election ] -> Assert.Equal(700L, election.Id)
        | other -> failwithf "expected exactly the opkey-and-pr match, got %A" other

    // -------------------------------------------------------------------------------------------
    // The wired path
    // -------------------------------------------------------------------------------------------
    //
    // Everything above drives pure functions — real coverage of the DECISIONS, but none of it proves
    // the LIVE path reaches the transport with the right method, path, order and body.
    // `FS.GG.Coord.Cli.Lifecycle.LiveHandlers.electionGrounding` and `FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization` are that wiring, and the cases
    // below drive them directly against a `Fake.Recorder` — the same "reuse the internal seam instead
    // of restating the whole `delivery` command's board-scan/PR-facts machinery" idiom
    // `AuthorizedMarkerTests.fs` already uses for `FS.GG.Coord.Cli.Lifecycle.LiveHandlers.authorizedMarker`.

    let private jsonBody (body: string) : string =
        System.Text.Json.JsonSerializer.Serialize {| body = body |}

    let private ensureAuthorizationTransport (route: Request -> Errors.IoResult<Response>) : Fake.Recorder =
        Fake.Recorder(fun (req: Request) -> route req)

    let private okResponse (body: string) : Errors.IoResult<Response> =
        Ok
            { Status = 200
              Body = body
              ETag = None
              NextLink = None
              Headers = Map.empty }

    let private ensureAuthorizationTarget: Ref =
        { Owner = "FS-GG"; Repo = ".github"; Number = 2395 }

    let private ensureAuthorizationMarker: Reads.Marker =
        { Id = 5267541214L
          Worker = WorkerId "smew-f1e2"
          Session = None
          AgeSeconds = 30
          PreviousStatus = None
          PathRepo = None
          AgentContract = None
          Raw = "<!-- fsgg:claim worker=smew-f1e2 lease=120 -->" }

    let private ensureAuthorizationContext (transport: Fake.Recorder) : Kernel.Context =
        { Transport = transport
          Owner = "FS-GG"
          Title = "Coordination"
          DefaultRepo = Some ".github"
          ChoreLocks = [] }

    /// A REST comment listing carrying the given `(id, body)` pairs, exactly as `commentsWithIdentity`
    /// parses it.
    let private commentListing (comments: (int64 * string) list) : string =
        comments
        |> List.map (fun (id, body) ->
            System.Text.Json.JsonSerializer.Serialize
                {| id = id
                   html_url = $"https://example.test/{id}"
                   body = body |})
        |> String.concat ","
        |> sprintf "[%s]"

    /// One scripted world for the wired legs: the item's comment listing, what a comment POST returns,
    /// and the pull request's body. Every request is TALLIED, so a leg can assert that a write did NOT
    /// happen rather than merely that the call returned `Ok`.
    type private World =
        { mutable Requests: (string * string) list
          mutable PostedBodies: string list
          mutable PatchedBodies: string list }

    let private world () =
        { Requests = []; PostedBodies = []; PatchedBodies = [] }

    let private scripted
        (w: World)
        (itemComments: Errors.IoResult<Response>)
        (postResult: Errors.IoResult<Response>)
        (prBody: string)
        : Fake.Recorder =
        ensureAuthorizationTransport (fun req ->
            w.Requests <- w.Requests @ [ req.Method, req.Path.Trim '/' ]

            match req.Method, req.Path.Trim '/' with
            | "GET", "repos/FS-GG/.github/issues/2395/comments" -> itemComments
            | "POST", "repos/FS-GG/.github/issues/2395/comments" ->
                match req.Body with
                | Json payload ->
                    use doc = System.Text.Json.JsonDocument.Parse payload
                    w.PostedBodies <- w.PostedBodies @ [ doc.RootElement.GetProperty("body").GetString() ]
                | _ -> failwith "expected the election POST to carry a JSON body"

                postResult
            | "GET", "repos/FS-GG/.github/pulls/9001" -> okResponse (jsonBody prBody)
            | "PATCH", "repos/FS-GG/.github/pulls/9001" ->
                match req.Body with
                | Json payload ->
                    use doc = System.Text.Json.JsonDocument.Parse payload
                    w.PatchedBodies <- w.PatchedBodies @ [ doc.RootElement.GetProperty("body").GetString() ]
                    okResponse "{}"
                | _ -> failwith "expected the authorization PATCH to carry a JSON body"
            | method', path -> Error(Errors.NotFound $"unexpected request in the #2395 fixture: %s{method'} %s{path}"))

    let private head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

    [<Fact>]
    let ``#2395 ensureAuthorization posts the election FIRST, then PATCHes a marker naming its comment id`` () =
        let w = world ()

        let transport =
            scripted w (okResponse (commentListing [])) (okResponse """{"id":5309319124}""") "Implements the thing.\n\nCloses #2395"

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) head false with
        | Error e -> failwithf "expected ensureAuthorization to succeed, got %A" e
        | Ok() ->
            // The election is APPENDED before the authorization is written, and that order is the
            // design's: an election is never deleted, so a failure between the two leaves a durable
            // fact the next call reuses. The reverse would write an authorization naming an election
            // that does not exist.
            let posted = w.Requests |> List.findIndex (fun (m, p) -> m = "POST" && p = "repos/FS-GG/.github/issues/2395/comments")
            let patched = w.Requests |> List.findIndex (fun (m, p) -> m = "PATCH" && p = "repos/FS-GG/.github/pulls/9001")
            Assert.True(posted < patched, $"expected the election POST before the authorization PATCH, got %A{w.Requests}")

            // The election comment BEGINS with its marker, because the fence anchors at byte 0.
            let electionBody = Assert.Single w.PostedBodies
            Assert.StartsWith("<!-- fsgg:merge-election v=1 opkey=", electionBody)
            Assert.Contains($"opkey=%s{expectedOpKey} ", electionBody)
            Assert.Contains("pr=9001 -->", electionBody)

            let body = Assert.Single w.PatchedBodies
            Assert.Contains(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.authorizationMarker "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" head, body)
            Assert.Contains("Closes #2395", body)

    [<Fact>]
    let ``#2395 a second call posts NO second election and grants the same comment id`` () =
        let w = world ()

        let transport =
            scripted
                w
                (okResponse (commentListing [ 5309319124L, anElection 9001 ]))
                (Error(Errors.NotFound "a second election must never be posted for the same opkey and pull request"))
                "Implements the thing."

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) head false with
        | Error e -> failwithf "expected ensureAuthorization to succeed, got %A" e
        | Ok() ->
            Assert.Empty w.PostedBodies
            let body = Assert.Single w.PatchedBodies
            Assert.Contains($"grant=5309319124 ", body)

    // THE BOUNDARY OF `Reads.lowestId`, NOT MERELY ITS BRANCH. Two elections this target owns,
    // adjacent comment ids, supplied HIGHEST FIRST so that "take the first one read" and "take the
    // lowest id" give different answers. The fence grants only the lowest, so a producer that named
    // the other would be refused at check 4 by its own writes.
    [<Fact>]
    let ``#2395 with two owned elections the LOWER comment id is granted, whatever order they are read in`` () =
        let w = world ()

        let transport =
            scripted
                w
                (okResponse (commentListing [ 5309319125L, anElection 9001; 5309319124L, anElection 9001 ]))
                (Error(Errors.NotFound "no election should be posted when this target already owns one"))
                "Implements the thing."

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) head false with
        | Error e -> failwithf "expected ensureAuthorization to succeed, got %A" e
        | Ok() ->
            let body = Assert.Single w.PatchedBodies
            Assert.Contains("grant=5309319124 ", body)
            Assert.DoesNotContain("grant=5309319125 ", body)

    // THE OTHER DIRECTION OF THE `pr=` DISCRIMINATOR. An election posted for a DIFFERENT pull request
    // under the same item and generation is a CONTENDER, never this pull request's grant. Reusing it
    // would let two executors sharing one generation both pass check 4, which is exactly the
    // "at most one merge per (item, generation, receiver)" guarantee the election exists to provide.
    [<Fact>]
    let ``#2395 an election posted for a different pull request is not reused, and this target elects its own`` () =
        let w = world ()

        let transport =
            scripted w (okResponse (commentListing [ 5309319100L, anElection 9002 ])) (okResponse """{"id":5309319124}""") "Implements the thing."

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) head false with
        | Error e -> failwithf "expected ensureAuthorization to succeed, got %A" e
        | Ok() ->
            Assert.Single w.PostedBodies |> ignore
            let body = Assert.Single w.PatchedBodies
            Assert.Contains("grant=5309319124 ", body)

    // FAIL CLOSED, AND THE ASSERTION IS THE ABSENCE OF A WRITE RATHER THAN THE PRESENCE OF AN ERROR.
    // A grounding that could not be established must leave the pull-request body exactly as it was:
    // no four-field fallback, because a marker the fence calls ungrounded is the decorative case the
    // design names, and no partial six-field marker, because there is no grant to name.
    [<Fact>]
    let ``#2395 an unreadable item comment list writes NO authorization at all`` () =
        let w = world ()

        let transport =
            scripted w (Error(Errors.Malformed("FS-GG/.github#2395", "the fixture refuses this read"))) (okResponse """{"id":1}""") "Implements the thing."

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) head false with
        | Ok() -> failwith "expected a refusal: the elections could not be read, so nothing grounds an authorization"
        | Error _ ->
            Assert.Empty w.PatchedBodies
            Assert.Empty w.PostedBodies

    [<Fact>]
    let ``#2395 a failed election POST writes NO authorization at all`` () =
        let w = world ()

        let transport =
            scripted w (okResponse (commentListing [])) (Error(Errors.Malformed("FS-GG/.github#2395", "the fixture refuses this write"))) "Implements the thing."

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) head false with
        | Ok() -> failwith "expected a refusal: no election was obtained, so no grant exists to name"
        | Error _ -> Assert.Empty w.PatchedBodies

    // The engine's OWN sentinel for "nobody holds this item" is the literal string `released`, and a
    // key composed on it would name a tenancy that does not exist — `Operation.compose` refuses it as
    // `GenerationNotServerAssigned`. The refusal must happen BEFORE any IO: a component we already
    // know is wrong is not worth a REST read.
    [<Fact>]
    let ``#2395 an operation key that cannot be composed refuses before it spends any IO`` () =
        let transport =
            ensureAuthorizationTransport (fun req ->
                Error(Errors.NotFound $"a compose refusal must precede the transport, got %s{req.Method} %s{req.Path}"))

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.electionGrounding (ensureAuthorizationContext transport) ensureAuthorizationTarget "released" 9001 with
        | Ok grounding -> failwithf "expected a refusal for the released sentinel, got %A" grounding
        | Error e ->
            let rendered = $"%A{e}"
            Assert.Contains("operation key", rendered)

    // Replaces the deleted `#2395 ensureAuthorization makes no request at all without --apply` test.
    // That test asserted the very defect #2488 measured: it is the only reason no live `item/<n>-*` PR
    // ever carried a current marker at the moment `claim-generation` evaluated it (five for five,
    // #2488's own evidence table). `apply` no longer exists as a parameter — every call at this
    // signature performs the read-modify-write below.
    [<Fact>]
    let ``#2488 ensureAuthorization is no longer gated on --apply: a plain live status read writes the marker too`` () =
        let w = world ()
        let plainHead = "dddddddddddddddddddddddddddddddddddddddd"

        let transport =
            scripted w (okResponse (commentListing [])) (okResponse """{"id":5309319124}""") "Implements the thing."

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) plainHead false with
        | Error e -> failwithf "expected ensureAuthorization to succeed, got %A" e
        | Ok() ->
            let body = Assert.Single w.PatchedBodies
            Assert.Contains(FS.GG.Coord.Cli.Lifecycle.LiveHandlers.authorizationMarker "FS-GG/.github#2395" "5267541214" expectedOpKey "5309319124" plainHead, body)

    // A genuinely still-live no-op: a merged PR's body is never rewritten — nothing further needs
    // authorizing once landing has already happened. Now stronger than before: it also proves the
    // election is not posted on a merged pull request, which would be an append nothing could undo.
    [<Fact>]
    let ``#2488 ensureAuthorization still makes no request once the PR has merged`` () =
        let transport =
            ensureAuthorizationTransport (fun req -> Error(Errors.NotFound $"expected zero requests once merged, got %s{req.Method} %s{req.Path}"))

        match FS.GG.Coord.Cli.Lifecycle.LiveHandlers.ensureAuthorization (ensureAuthorizationContext transport) ensureAuthorizationTarget (Some ensureAuthorizationMarker) (Some 9001) "head-a" true with
        | Error e -> failwithf "expected ensureAuthorization to succeed as a no-op, got %A" e
        | Ok() -> ()
