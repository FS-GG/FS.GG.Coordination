namespace FS.GG.Coord.Tests

open System.Reflection
open FSharp.Reflection
open Xunit
open FS.GG.Coord
open FS.GG.Coord.Types

/// THE PROTOCOL IS THE SOURCE, NOT A COPY OF ONE (ADR-0034 §4.5).
///
/// `Protocol.fs` is what every projection is emitted from — the canonical doc and the `SKILL.md` bodies
/// in both skill roots. These tests guard the properties that make that safe. They are cheap and they
/// look obvious; each one is a way the projection could go quietly wrong, and "quietly" is the whole
/// problem this module exists to end.
module ProtocolTests =

    /// Every `BoardStatus` case, by reflection — the subject the `boardStatuses` guards search, derived
    /// rather than typed so this file cannot forget a case in the same breath as `Protocol.fs` does.
    let private everyBoardStatusCase: BoardStatus list =
        FSharpType.GetUnionCases typeof<BoardStatus>
        |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> BoardStatus)
        |> Array.toList

    /// A RULE THAT RESTATES ITS SOURCE IS STILL A COPY. `touchSetGrammar.Statement` must BE
    /// `Schedulability.TouchSetGrammar` — not a paraphrase of it, not a version of it that was correct
    /// when it was typed. If somebody replaces the reference with a literal, the two drift the moment
    /// one is edited, and the generated docs would then faithfully publish the stale one.
    ///
    /// This is not hypothetical: the F# grammar constant was itself typed in by hand while the flip was
    /// being written, byte-identical to bash's purely by luck.
    [<Fact>]
    let ``the grammar rule IS the enforcing constant — not a copy of it`` () =
        Assert.Equal(Schedulability.TouchSetGrammar, Protocol.touchSetGrammar.Statement)

    /// THE PROJECTED RULE MUST MATCH WHAT THE ENGINE ENFORCES (#2248), not merely read plausibly beside
    /// it. `Protocol.renderMarkerAnchorRule Protocol.LeadingBlock` is the prose the
    /// `fsgg-protocol:review-policy` region states (.github#2399 — replaces the removed
    /// `ReviewPolicyDoc.QuotedMarkerRule` string field with a function dispatched over `MarkerAnchor`);
    /// this pins its vocabulary against `Driver.parseReviewComments`' OWN behaviour for the two cases the
    /// rule names — a quotation, and a competing canonical repeat — so a change to one that is not also a
    /// change to the other reds here, rather than the projection drifting from #2221 silently the way the
    /// hand-written prose it replaces would have.
    [<Fact>]
    let ``the documented verdicts are exactly the verdicts the scheduler can return`` () =
        let cases = FSharpType.GetUnionCases typeof<Schedulability.Schedulability>
        let documented = Protocol.verdicts |> List.map (fun v -> v.Kind)

        // Two cases sharing a kind would let one hide behind the other and still pass the count below.
        Assert.Equal<string list>(List.distinct documented, documented)

        Assert.Equal<int>(cases.Length, List.length documented)

        for v in Protocol.verdicts do
            Assert.False(
                System.String.IsNullOrWhiteSpace v.Kind,
                "a verdict is documented under an empty kind — ungreppable by construction")

            Assert.False(
                System.String.IsNullOrWhiteSpace v.Meaning,
                $"verdict '%s{v.Kind}' means nothing")

    /// THE DOC'S KIND IS THE WIRE'S KIND — the same function, not two spellings that agree today.
    ///
    /// `Protocol.fsi` promises "a reader of that log can grep a verdict straight into the doc that
    /// explains it". This is that promise, asserted rather than stated: every verdict the scheduler can
    /// return renders a kind (`Schedulability.kind` — what `Snapshot` writes as the `verdict` field) that
    /// appears verbatim in `Protocol.verdicts`.
    [<Fact>]
    let ``every kind the wire can emit is greppable into the docs`` () =
        let documented = Protocol.verdicts |> List.map (fun v -> v.Kind) |> Set.ofList

        // Representative values, pinning the STRING each case renders — the one fact neither the compiler
        // (which checks only that a case HAS a kind) nor reflection (which checks only that every case is
        // documented) can see. A silent rename of `held-by` back to `held` passes both, and lands in three
        // generated documents.
        //
        // THIS IS A HAND-MAINTAINED LIST, WHICH IS THE SHAPE THAT CAUSED #865 — so it is not trusted to
        // be complete, it is FORCED to be, by the set assertion below. Its coverage rests on `documented`,
        // which reflection has already pinned to the union.
        let samples: (Schedulability.Schedulability * string) list =
            [ Schedulability.Startable, "startable"
              Schedulability.IssueClosed, "issue-closed"
              Schedulability.WrongStatus Backlog, "wrong-status"
              Schedulability.BlockedBy [], "blocked-by"
              Schedulability.AwaitingHuman AwaitingHumanDecision, "awaiting-human"
              Schedulability.AwaitingDeliveryRouteDecision [], "awaiting-delivery-route-decision"
              Schedulability.NoTouchSet, "no-touch-set"
              Schedulability.DeliberatelyNoTouchSet, "deliberately-no-touch-set"
              Schedulability.UnusableTouchSet [ "**/x" ], "unusable-touch-set"
              Schedulability.HeldBy(WorkerId "w"), "held-by"
              Schedulability.HeldByLiveWork(WorkerId "w", 1), "held-by-live-work"
              Schedulability.ItemPrOpen 1, "item-pr-open"
              Schedulability.OverlapsInFlight [], "overlaps-in-flight"
              Schedulability.Undetermined "r", "undetermined"
              Schedulability.NotAUnitOfWork Register, "not-a-unit-of-work" ]

        for case, wire in samples do
            Assert.Equal<string>(wire, Schedulability.kind case)

            Assert.True(
                documented.Contains wire,
                $"the scheduler can emit '%s{wire}' and no documented verdict explains it — a worker greps it and finds nothing")

        // EVERY documented kind is pinned above, and nothing else is. A new union case reaches `documented`
        // by construction (the compiler forces a kind and a meaning; reflection forces the enumeration) —
        // so it lands here as a set difference, and its wire string cannot ship unpinned.
        Assert.Equal<Set<string>>(documented, samples |> List.map snd |> Set.ofList)

    /// THE TWO DRIFTS #865 FOUND, PINNED INDIVIDUALLY — so a regression names itself rather than
    /// arriving as an off-by-one in a count.
    ///
    /// `held` is not merely the wrong token, which is why it outlived review: it is a LIVE token in a
    /// DIFFERENT vocabulary — `who`'s claim state (`held`/`stale`/`unclaimed`). So a worker grepping the
    /// documented `held` got a HIT, in the wrong answer set, and a wrong hit reads as an answer where a
    /// miss reads as a question.
    [<Fact>]
    let ``the verdict vocabulary says held-by, and leaves 'held' to who's claim states`` () =
        let documented = Protocol.verdicts |> List.map (fun v -> v.Kind) |> Set.ofList

        Assert.True(documented.Contains "held-by", "the wire emits 'held-by' and the docs do not explain it")

        Assert.False(
            documented.Contains "held",
            "'held' is `who`'s CLAIM-STATE vocabulary, not a schedulability verdict — documenting it here sends a grep into the wrong answer set (#865)")

    /// `ItemPrOpen` (#651) reached the wire and never reached the docs, behind a green test. It is the
    /// case that proves the point, so it is asserted by name.
    [<Fact>]
    let ``item-pr-open is documented — the verdict that shipped undocumented`` () =
        let doc = Protocol.verdicts |> List.tryFind (fun v -> v.Kind = "item-pr-open")

        match doc with
        | None ->
            Assert.Fail
                "item-pr-open is returned by `schedulable` and emitted by `Snapshot`, and no documented verdict explains it (#865)"
        | Some d -> Assert.Contains("#651", d.Meaning)

    /// A RULE WITH NO `Because` IS A RULE THAT WILL BE DELETED BY SOMEBODY WHO DOES NOT KNOW WHY.
    ///
    /// Every rule here was bought by an incident. The `Because` is what stops the next author — who is
    /// reasonably sure the rule is silly — from removing it. Half this repo's issue history is that
    /// author.
    [<Fact>]
    let ``every rule states the incident that bought it`` () =
        for r in Protocol.rules do
            Assert.False(System.String.IsNullOrWhiteSpace r.Id, $"rule '%s{r.Title}' has no id")
            Assert.False(System.String.IsNullOrWhiteSpace r.Statement, $"rule '%s{r.Id}' states nothing")
            Assert.False(System.String.IsNullOrWhiteSpace r.Because, $"rule '%s{r.Id}' has no Because")

    /// Ids are ANCHORS — a projection links to them, and a reader greps them back to the code. Two rules
    /// sharing one id would silently make one of them unreachable.
    [<Fact>]
    let ``rule ids are unique`` () =
        let ids = Protocol.rules |> List.map (fun r -> r.Id)
        Assert.Equal<string list>(List.distinct ids, ids)

    /// THE GENERATOR MUST HAVE SOMETHING TO GENERATE. An empty rule list would render an empty region,
    /// the gate would compare empty to empty, and every document would pass while stating nothing — the
    /// vacuity failure (#266, #436) in the gate built to end the vendored copies.
    [<Fact>]
    let ``the protocol is not empty`` () =
        Assert.NotEmpty Protocol.rules
        Assert.NotEmpty Protocol.verdicts
        Assert.NotEmpty Protocol.takeExitCodes
        Assert.NotEmpty Protocol.landableExitCodes
        Assert.NotEmpty Protocol.releaseColumns

    /// EVERY `ExitCodeDoc list` the protocol declares, found by REFLECTION rather than by a list
    /// somebody remembers to update.
    ///
    /// A hand-written roster here would be the defect one level up: a table added to `Protocol.fs` and
    /// not to the roster gets NONE of the invariants below, and nothing says so — a gate that silently
    /// stops covering its subject (#266, #436). `take`'s table (#889) and `landable`'s (#900) were both
    /// hand-written copies that drifted; answering that with a hand-written roster of them would be the
    /// same mistake, wearing a test's clothes. So the invariants attach to the TYPE, and a third table
    /// is covered the moment it is declared.
    let private exitTables: (string * Protocol.ExitCodeDoc list) list =
        let m =
            typeof<Protocol.Rule>.Assembly.GetType "FS.GG.Coord.Protocol"

        // The module type is found by NAME, so a rename would otherwise silently yield zero tables and
        // pass every invariant vacuously. That is the exact failure this reflection exists to refuse.
        Assert.True(not (isNull m), "FS.GG.Coord.Protocol not found — reflection cannot see the tables it is gating")

        m.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.filter (fun p -> p.PropertyType = typeof<Protocol.ExitCodeDoc list>)
        |> Array.map (fun p -> p.Name, p.GetValue null :?> Protocol.ExitCodeDoc list)
        |> List.ofArray

    /// THE FLOOR (#266, #436). Reflection finding NOTHING would make every invariant below iterate an
    /// empty list and pass — the vacuity these gates exist to refuse. This fails when a table is
    /// DELETED or renamed out of view; a table that is ADDED needs no edit here, because reflection has
    /// already covered it.
    [<Fact>]
    let ``every exit-code table the protocol declares is gated`` () =
        let names = exitTables |> List.map fst
        Assert.NotEmpty exitTables
        Assert.Contains("takeExitCodes", names)
        Assert.Contains("landableExitCodes", names)

    /// A CODE WITHOUT A REMEDY IS A CODE A WORKER INVENTS A REMEDY FOR — and the invented one is
    /// "retry", which is exactly wrong for `take` 2 (the engine is broken), `take` 5 (the queue is
    /// empty), and `landable` 3 (the PR is RED — the invented retry is the #900 hang itself).
    /// The `Meaning`/`Action` split is the whole reason these tables beat the number alone.
    [<Fact>]
    let ``every exit code says what it saw and what to do`` () =
        for cmd, codes in exitTables do
            for c in codes do
                Assert.False(
                    System.String.IsNullOrWhiteSpace c.Meaning,
                    $"%s{cmd} exit %d{c.Code} means nothing")

                Assert.False(
                    System.String.IsNullOrWhiteSpace c.Action,
                    $"%s{cmd} exit %d{c.Code} tells the caller to do nothing")

    /// Two rows for one code is two remedies for one observation, and the worker reads whichever it
    /// meets first. The old hand-written `take` table had exactly this defect: its `≠0, ≠2` row also
    /// matched 5, 6 and 75, so three codes carried two contradictory instructions each.
    [<Fact>]
    let ``exit codes are unique within a table`` () =
        for cmd, codes in exitTables do
            let ns = codes |> List.map (fun c -> c.Code)
            Assert.Equal<int list>(List.distinct ns, ns)
            Assert.True(not ns.IsEmpty, $"%s{cmd}'s table is empty")

    /// 0 IS THE ONLY SUCCESS, and the table's first row is what a worker copies. `take && work_it`
    /// firing on nothing is #585 itself; merging on a non-green `landable` is #900's.
    [<Fact>]
    let ``every table documents exactly one success code, and it leads`` () =
        for cmd, codes in exitTables do
            Assert.Equal(0, (List.head codes).Code)

            Assert.Equal(
                1,
                codes |> List.filter (fun c -> c.Code = 0) |> List.length)

            Assert.True(
                codes |> List.forall (fun c -> c.Code >= 0),
                $"%s{cmd} documents a negative exit code, which no shell can report")

    /// `landable`'s CONTRACT IS THE POLL LOOP, and #900 was that the recipe got the two codes the loop
    /// reads backwards: it called 3 "pending" (3 is RED — so the loop waits forever on a PR that will
    /// never go green) and had no row for 7 at all (7 is PENDING — so the loop reads it as an
    /// unrecognised failure and stops waiting on a PR that is merely still running).
    ///
    /// This pins the DOCUMENTED MEANINGS, in `Core`, where the engine's constants are not visible.
    /// `ExitContractTests` ties the same rows to `Client.ExitPending`/`ExitRed` — the two halves are
    /// both needed, because generating a table only makes the copies AGREE; it does not make them TRUE.
    [<Fact>]
    let ``landable's 3 is red and its 7 is pending, never the reverse`` () =
        let meaningOf code =
            Protocol.landableExitCodes
            |> List.tryFind (fun c -> c.Code = code)
            |> Option.map (fun c -> c.Meaning.ToUpperInvariant())

        // `StartsWith`, not `Contains` (#944). "REGISTERED" CONTAINS "RED" — and "none have registered
        // yet" is row 7's own PENDING text, so `Contains "RED"` PASSES on the pending row. Measured on
        // this tree: rewrite row 3 to say "The checks have not registered yet" and this test, the one
        // named for catching exactly that, stayed GREEN. It was asserting a substring of a word rather
        // than the verdict. Both rows open with their verdict word, so anchor to it.
        match meaningOf 3 with
        | None -> Assert.Fail "landable exit 3 (red/conflicted) is not documented"
        | Some m ->
            Assert.True(m.StartsWith "RED", "landable exit 3 does not say it is RED — #900 is that it said 'pending'")
            Assert.False(m.StartsWith "PENDING", "landable exit 3 is documented as PENDING — that is #900 exactly, and a loop built on it hangs")

        match meaningOf 7 with
        | None -> Assert.Fail "landable exit 7 (pending) is not documented — the recipe's table had no 7, so a loop stops waiting on a PR that is still running"
        | Some m -> Assert.True(m.StartsWith "PENDING", "landable exit 7 does not say it is PENDING")

    /// #1680 — THE SAME DEFECT, ONE STATE OVER. #900 was "3 is documented as pending"; this is "a MERGED
    /// PR is ANSWERED with pending". Same consequence, same shape: the one code the contract defines as
    /// worth retrying, returned for a state that cannot change, so `--wait` spins its whole budget.
    ///
    /// Anchored with `StartsWith` for the reason the test above records — and here the trap is sharper,
    /// because row 10's text legitimately CONTAINS the word "PENDING" (it explains what it used to
    /// return). A `Contains` assertion in either direction would be meaningless on this row.
    [<Fact>]
    let ``#1680 landable's 10 is the NOT-OPEN verdict, and it is not pending`` () =
        let meaningOf code =
            Protocol.landableExitCodes
            |> List.tryFind (fun c -> c.Code = code)
            |> Option.map (fun c -> c.Meaning.ToUpperInvariant())

        match meaningOf 10 with
        | None ->
            Assert.Fail
                "landable exit 10 (merged / closed-unmerged) is not documented — a merged PR has no documented outcome, which is #1680: the recovery path re-gates a landed PR and is told to wait"
        | Some m ->
            Assert.True(
                m.StartsWith "MERGED",
                "landable exit 10 does not open with MERGED — the caller must be able to tell 'already landed' from 'still running' from the verdict alone (#1680 AC2)")

            Assert.False(
                m.StartsWith "PENDING",
                "landable exit 10 is documented as PENDING — that is #1680 exactly, and --wait burns its full 600s budget on a settled fact")

            // AC4: the neighbouring case is DECIDED and STATED on the same row, not left to chance.
            Assert.Contains("CLOSED", m)

    /// #1680 AC1, as a property rather than a row read: the merged verdict must not be reachable through
    /// the retryable code. `landableCodes` is the engine's own return set, so this also fails if someone
    /// later maps `PrMerged` back onto `Pending` to avoid adding a row.
    [<Fact>]
    let ``#1680 the not-open verdict has a code of its own, distinct from pending`` () =
        Assert.NotEqual(ExitCode.toInt ExitCode.NotOpen, ExitCode.toInt ExitCode.Pending)
        // And distinct from RED, which is the other tempting reuse: `merged` is a SUCCESS, and 3's
        // documented action is "stop, a red check is a finding" — the wrong instruction for a successor
        // whose real next act is to STAMP the item.
        Assert.NotEqual(ExitCode.toInt ExitCode.NotOpen, ExitCode.toInt ExitCode.Red)
        Assert.Contains(ExitCode.NotOpen, ExitCode.landableCodes)

    /// THERE IS NO EX_RATE IN `landable`, and a reader of `take`'s table will expect one.
    /// `Reads.prLandableRequire` returns a bare `PrState` with no error channel, so a rate limit is
    /// `PrUnknown` — exit 4 — not 75. Documenting a 75 here would send a worker to wait out a budget
    /// reset over what is actually an unread PR.
    [<Fact>]
    let ``landable documents no rate-limit code`` () =
        Assert.DoesNotContain(75, Protocol.landableExitCodes |> List.map (fun c -> c.Code))

    /// COMPLETENESS — THE HALF #916 COULD NOT BUILD (#918). Before the `ExitCode` union, a command's
    /// return set was ints threaded through three modules, so nothing could ENUMERATE it: these tables
    /// were hand-derived and their completeness could only be proof-read. That is exactly how the first
    /// `takeExitCodes` shipped with no row for `ExitRed` — a code `take` reaches through `renderDecision`
    /// and every gate missed, caught only by a human reading `take` line by line.
    ///
    /// Now the return set is a VALUE — `ExitCode.takeCodes` / `ExitCode.landableCodes` — and this pins
    /// each table to it in BOTH directions: every code the command can return has a row (no omission),
    /// and the table documents none the command cannot return (no invention — #889's `EX_PARTIAL`
    /// under `take`). `exitTables` is reflection-discovered, so a NEW `ExitCodeDoc list` with no declared
    /// domain fails here loudly rather than going unchecked.
    [<Fact>]
    let ``each exit table documents exactly the codes its command can return`` () =
        let domainOf =
            function
            | "takeExitCodes" -> ExitCode.takeCodes
            | "landableExitCodes" -> ExitCode.landableCodes
            | other ->
                failwith
                    $"%s{other} is an exit-code table with no `ExitCode` domain declared — add one to `ExitCode` so its completeness can be checked (#918)"

        for cmd, codes in exitTables do
            let documented = codes |> List.map (fun c -> c.Code) |> Set.ofList
            let domain = domainOf cmd |> List.map ExitCode.toInt |> Set.ofList

            Assert.True(
                (domain = documented),
                $"%s{cmd} does not document exactly the codes its command returns (#918): "
                + $"missing %A{Set.difference domain documented |> Set.toList}, "
                + $"invented %A{Set.difference documented domain |> Set.toList}")

    // ================================================================================================
    // `releaseColumns` — `release`/`reap`'s column precedence (#1099), the third table in the class
    // #889/#900 proved. It is `Core`-side that these pins can live at all: the engine's `unclaimColumn`
    // is `private` in `Client.fs`, unreachable from a test, and its end-to-end behaviour is exercised by
    // `tests/coord-engine-parity` (the `release --status Blocked` legs). What CAN drift silently is the
    // DOC — the rows a worker reads — so these pin what the rows SAY, the way the landable-red/pending
    // test does. Generating the region only makes the copies AGREE; a test is what keeps them TRUE.
    // ================================================================================================

    /// Every row carries all four fields — a row with a blank `condition`, `endState` or `stdout` states
    /// nothing a worker can act on, and a projection would render an empty cell.
    [<Fact>]
    let ``every release-column row states a condition, an end state and a stdout tell`` () =
        for c in Protocol.releaseColumns do
            Assert.False(System.String.IsNullOrWhiteSpace c.Condition, "a release-column row has no condition")
            Assert.False(System.String.IsNullOrWhiteSpace c.EndState, $"release-column row '%s{c.Condition}' has no end state")
            Assert.False(System.String.IsNullOrWhiteSpace c.Stdout, $"release-column row '%s{c.Condition}' has no stdout tell")

    /// THE PRECEDENCE, AND IT IS THE WHOLE POINT (#867/#914). An explicit `--status` beats the recorded
    /// restore and the `Ready` fallback alike, and the table is ordered as `release` EVALUATES it — so
    /// the `--status` row must LEAD. #921 is the row getting this backwards: `/pnext-item` called
    /// `--status` a no-op after #914 made it the highest-precedence input.
    [<Fact>]
    let ``the explicit --status row leads the precedence`` () =
        let lead = List.head Protocol.releaseColumns
        Assert.True(lead.Condition.Contains "--status", "the first release-column row is not the explicit --status case — the precedence #867/#914 restored is stated out of order")
        Assert.True(lead.Writes, "the explicit --status row does not write the column it names")

    /// THE #331 OBSERVABLE. A PRESERVE writes NOTHING — the absence of the write is what tells
    /// "preserved" from "restored" in the board's history, and a row that says it preserves a column
    /// while claiming to write it would document the very defect #911 fixed. So no row may both name a
    /// bare `released <ref>`/`column left at`/`no column to reset` stdout AND set `writes = true`.
    [<Fact>]
    let ``a row whose stdout reports no column write is a preserve, not a write`` () =
        for c in Protocol.releaseColumns do
            let namesASetColumn = c.Stdout.Contains "→"
            if not namesASetColumn then
                Assert.False(
                    c.Writes,
                    $"release-column row '%s{c.Stdout}' claims to WRITE the board but its stdout names no column set (`→`) — a preserve/no-op writes nothing (#331/#911)")

    /// AT LEAST ONE PRESERVE ROW, or the table has lost #331 entirely: the whole reason `release` reads
    /// the LIVE column is to preserve one a worker chose during the lease rather than revert it.
    [<Fact>]
    let ``the precedence documents at least one preserve`` () =
        Assert.True(
            Protocol.releaseColumns |> List.exists (fun c -> not c.Writes),
            "no release-column row preserves — the table has lost #331, the reason release reads the live column at all")

    // ================================================================================================
    // `blockerStates` — the wire vocabulary `check-board` §1 restated by hand (#889).
    //
    // The stakes are not a misprinted doc. `check-board` §3 selects on these strings in `jq`, so a
    // drifted value matches NOTHING, every blocker reads as still-holding, `BLOCKER-CLEARED` never
    // fires, and the pass reports a CLEAN BOARD over items rotting behind shipped work (#476). A false
    // clean is that skill's worst output by its own account, and it is silent.
    // ================================================================================================

    /// REFLECTION CAN SEE THE UNION — so the guards below are not vacuous (#266).
    ///
    /// `Protocol.everyBlockerState` is built by reflection rather than typed out. That removes #865's
    /// defect (a list that silently stops naming a case) but buys a new one: if reflection returned
    /// EMPTY, every `for` below would pass by iterating nothing and this file would report green over a
    /// vocabulary it never looked at. This is the test that refuses that, and it is why the count is
    /// asserted against the UNION rather than against `5`.
    [<Fact>]
    let ``the documented blocker states are exactly the cases of BlockerState`` () =
        let cases = FSharpType.GetUnionCases typeof<BlockerState>
        let documented = Protocol.blockerStates |> List.map (fun b -> b.Wire)

        Assert.NotEmpty documented
        Assert.Equal<int>(cases.Length, List.length documented)

        // Two states sharing a wire name would let one hide behind the other and still pass the count.
        // It is also the bug directly: `jq` cannot tell them apart either.
        Assert.Equal<string list>(List.distinct documented, documented)

    /// THE DOC'S STRING IS THE WIRE'S STRING — the same function, not two spellings that agree today.
    ///
    /// This is the assertion that would have caught #1012's measured defect one level up: `merged` ->
    /// `"MERGED"` in the renderer left 775 tests green. Here it reds, because the doc is not allowed to
    /// have its own opinion about what `scan` writes.
    [<Fact>]
    let ``every documented blocker state renders the wire name the engine writes`` () =
        for b in Protocol.blockerStates do
            match blockerStateOfWireName b.Wire with
            | None ->
                Assert.Fail
                    $"the docs publish '%s{b.Wire}' as a blocker state and the engine cannot parse it back — `check-board` selects on this string in jq, so it matches nothing and every blocker reads as still-holding (#476)"
            | Some parsed ->
                Assert.True(
                    blockerStateWireName parsed = b.Wire,
                    $"'%s{b.Wire}' does not round-trip through the engine's own vocabulary")

    /// THE `holds?` COLUMN IS THE SCHEDULER'S ANSWER, NOT THE DOC'S.
    ///
    /// GENERATION MAKES COPIES AGREE; IT DOES NOT MAKE THEM TRUE (#916's trap 1). A hand-typed `Holds`
    /// in `Protocol.fs` would be a copy of `Blockers.isResolvedState` with a generator's authority
    /// behind it — strictly worse than the prose it replaced, because nobody proof-reads a generated
    /// region. The first draft of `blockerStates` did exactly that, and its five answers were RIGHT,
    /// which is precisely how #865 got in. So: pin the doc against the PREDICATE.
    [<Fact>]
    let ``a documented state holds iff the engine refuses to resolve it`` () =
        for b in Protocol.blockerStates do
            let state =
                match blockerStateOfWireName b.Wire with
                | Some s -> s
                | None -> failwith $"'{b.Wire}' is not a blocker state"

            let engineSaysHolds = not (Blockers.isResolvedState state)

            Assert.True(
                (b.Holds = engineSaysHolds),
                $"'%s{b.Wire}': the generated table says holds=%b{b.Holds}, the engine says holds=%b{engineSaysHolds} — the row a reconciler acts on disagrees with the predicate that schedules")

    /// THE FAIL-CLOSED CASES, NAMED. The two that read like non-answers and BLOCK (#266).
    ///
    /// The property above is relative — it would hold just as well if BOTH the predicate and the doc
    /// were inverted. This one is absolute, and it is the one whose failure is a real incident: a reader
    /// who treats `unknown` as "not blocking" has re-written the bug `fail-closed` exists to refuse.
    [<Fact>]
    let ``unknown and unparseable are documented as HOLDING`` () =
        let holdsOf w =
            Protocol.blockerStates |> List.tryFind (fun b -> b.Wire = w) |> Option.map (fun b -> b.Holds)

        Assert.Equal(Some true, holdsOf "unknown")
        Assert.Equal(Some true, holdsOf "unparseable")
        Assert.Equal(Some false, holdsOf "closed")
        Assert.Equal(Some false, holdsOf "merged")
        Assert.Equal(Some true, holdsOf "open")

    /// THREE REF PARSERS, ONE GRAMMAR — they may never disagree about what a ref token means.
    ///
    /// `EpicBody.childRefs` (scanning an epic's task list), `Blockers.canonicalizeBlockedBy` (parsing a
    /// `Blocked by` field) and `Rooms.parse` (scanning a `Rooms:` line, ADR-0051) all reduce a ref token
    /// to `owner/repo#n`. #1153 is what DRIFT between the first two cost: `EpicBody` accepted
    /// `owner/repo#n`, a URL, and bare `#n` but NOT the owner-less `repo#n`, so `FS.GG.SDD#8` fell through
    /// to the bare `#n` it contains and resolved against the epic's OWN repo — `.github#8` for a `.github`
    /// epic — while `Blockers` read the same token as `FS-GG/FS.GG.SDD#8`. A rollup that diffs one
    /// parser's output against the other's could then check the wrong issue. `Rooms.parse` is the third
    /// copy of that grammar (ADR-0051 reuses it deliberately), and the room's close computation diffs its
    /// output the same way, so it is pinned here too. All three against ONE token set, in all four
    /// spellings, so they cannot silently drift.
    [<Fact>]
    let ``EpicBody, Blockers and Rooms canonicalize a ref token identically`` () =
        let owner, repo = "FS-GG", ".github"

        let tokens =
            [ "#8" // bare #n → owner AND repo default to the epic's own
              "FS.GG.SDD#8" // repo#n → repo carried, owner defaults
              "FS-GG/FS.GG.Rendering#12" // owner/repo#n → both carried
              "https://github.com/FS-GG/FS.GG.Audio/issues/9" ] // a full issue URL

        for tok in tokens do
            let viaEpic = EpicBody.childRefs owner repo $"- [ ] {tok} a child"

            let viaBlockers =
                match Blockers.canonicalizeBlockedBy owner repo tok with
                | Ok(Some s) -> [ s ]
                | Ok None -> []
                | Error _ -> [ "<refused>" ]

            let viaRooms =
                Rooms.parse owner repo $"Rooms: {tok}"
                |> List.map (fun r -> $"%s{r.Owner}/%s{r.Repo}#%d{r.Number}")

            Assert.True(
                (viaEpic = viaBlockers),
                $"'%s{tok}': EpicBody canonicalizes to %A{viaEpic}, Blockers to %A{viaBlockers} — ref parsers drifted (#1153)")

            Assert.True(
                (viaEpic = viaRooms),
                $"'%s{tok}': EpicBody canonicalizes to %A{viaEpic}, Rooms to %A{viaRooms} — ref parsers drifted (#1153, ADR-0051)")

    /// A STATE DOCUMENTED UNDER AN EMPTY STRING IS UNGREPPABLE BY CONSTRUCTION, and a state that means
    /// nothing is a row a reader skips.
    [<Fact>]
    let ``every blocker state is documented with a meaning`` () =
        for b in Protocol.blockerStates do
            Assert.False(
                System.String.IsNullOrWhiteSpace b.Wire,
                "a blocker state is documented under an empty wire name")

            Assert.False(System.String.IsNullOrWhiteSpace b.Meaning, $"blocker state '%s{b.Wire}' means nothing")

    // ================================================================================================
    // `boardStatuses` — the board vocabulary `cross-repo-coordination` restated by hand (#889, #1057).
    //
    // Same stakes as `blockerStates` above, and asymmetric in the same direction: a filer who copies a
    // drifted option gets a LOUD refusal from `set-field`, but a reconciler selecting `.status` in `jq`
    // gets NO ROWS, and no rows reads as a clean board (#476).
    // ================================================================================================

    /// An item that is startable but for its column — so `schedulable`'s answer below turns on the
    /// `Status` and on nothing else. Every other field is the benign case: open issue, declared paths,
    /// no blockers, no claim, no in-flight PR.
    let private columnProbe (status: BoardStatus) : Item =
        { Ref =
            { Owner = "FS-GG"
              Repo = "FS.GG.SDD"
              Number = 1 }
          PathRepo = "FS.GG.SDD"
          Status = status
          State = Open
          TouchSet = Declared [ Matchable "src/Scene/**" ]
          Blockers = []
          Claim = None
          ItemPr = None
          ItemPrUnreadable = false
          HumanBlock = None
          Predicate = None
          Class = None
          Kind = None
          BoardKind = None
          CommentCount = None
          BoardClass = None
          DeliveryRoute = DeliveryRoute.Current { Schema = DeliveryRoute.Schema; Subject = "test"; SubjectRevision = "test"; Route = Some DeliveryRoute.Lightweight; Agent = "test"; Timestamp = "2026-01-01T00:00:00Z"; ReasonCodes = [ "test" ]; Rationale = "test"; DeclaredImpacts = [ "test" ]; ObservedFacts = [ "test" ]; SddWorkId = None; SpecHome = None; RequiredGates = [] }
          Severity = Unset
          Phase = None
          AgeDays = None }

    /// REFLECTION CAN SEE THE UNION — so the guards below are not vacuous (#266), and the count is
    /// asserted against `BoardStatus` rather than against `6`.
    ///
    /// SIX, NOT SEVEN, and the arithmetic is stated rather than hardcoded: every case except `NoStatus`,
    /// whose wire form is `""` — the absence of a column, not an option a filer can select. Pinning `6`
    /// here would pass just as happily if a new case were added and silently dropped from the doc.
    [<Fact>]
    let ``the documented board statuses are exactly the cases of BoardStatus, less NoStatus`` () =
        let cases = FSharpType.GetUnionCases typeof<BoardStatus>
        let documented = Protocol.boardStatuses |> List.map (fun s -> s.Wire)

        Assert.NotEmpty documented
        Assert.Equal<int>(cases.Length - 1, List.length documented)

        // Two options sharing a wire name would let one hide behind the other and still pass the count.
        // It is also the bug directly: `jq` cannot tell them apart either.
        Assert.Equal<string list>(List.distinct documented, documented)

        // The one case that must NOT be published. `""` is not a settable option, and a table offering it
        // as one invites #437 — `NoStatus` read as though it were `Backlog`.
        Assert.DoesNotContain("", documented)

    /// THE DOC'S STRING IS THE BOARD'S STRING — the same function, not two spellings that agree today.
    [<Fact>]
    let ``every documented board status renders the option name the engine writes`` () =
        let engineSpellings = everyBoardStatusCase |> List.map statusWireName

        for s in Protocol.boardStatuses do
            Assert.True(
                List.contains s.Wire engineSpellings,
                $"the docs publish '%s{s.Wire}' as a board Status option and `statusWireName` never writes it — `set-field` would refuse it, and a reconciler selecting it in jq matches nothing and reports a clean board (#476)")

    /// THE `startable?` COLUMN IS THE SCHEDULER'S ANSWER, NOT THE DOC'S — and this is the pin that is
    /// NOT vacuous.
    ///
    /// `Protocol.boardStatuses` derives `Startable` from `Schedulability.columnStartability`, so pinning
    /// it against `columnStartability` would compare the extraction to itself and pass however wrong both
    /// were. This asks `schedulable` — the real scheduler, whole, by the path `batch` actually calls —
    /// and it is what proves #1057's extraction did not quietly change the queue.
    [<Fact>]
    let ``a documented status is startable exactly when the scheduler offers it`` () =
        for s in Protocol.boardStatuses do
            let status =
                everyBoardStatusCase
                |> List.tryFind (fun c -> statusWireName c = s.Wire)
                |> Option.defaultWith (fun () -> failwith $"'{s.Wire}' is not a board status")

            let offered allowBacklog =
                Schedulability.schedulable Set.empty allowBacklog [] (columnProbe status) = Schedulability.Startable

            // The doc's WORD, decoded independently of the engine's own renderer — so this test states
            // what each published word must MEAN and checks the scheduler against it, rather than asking
            // `columnStartability` to confirm itself.
            let expectedPlain, expectedOptIn =
                match s.Startable with
                | "always" -> true, true
                | "with-backlog-opt-in" -> false, true
                | "never" -> false, false
                | other -> failwith $"the docs publish startable=\"{other}\", which is not a word this pin knows"

            let saidPlain = if offered false then "DOES" else "does NOT"
            let saidOptIn = if offered true then "DOES" else "does NOT"

            Assert.True(
                (offered false = expectedPlain),
                $"'%s{s.Wire}': the generated table says startable=%A{s.Startable}, but a plain `take` %s{saidPlain} offer it")

            Assert.True(
                (offered true = expectedOptIn),
                $"'%s{s.Wire}': the generated table says startable=%A{s.Startable}, but `take --include-backlog` %s{saidOptIn} offer it")

    /// THE THREE-STATE CASES, NAMED. The property above is relative — it would hold just as well if the
    /// doc and the scheduler were wrong together in the same direction. This one is absolute, and it is
    /// the row whose failure is a real incident: `Backlog` is the board's most common park, and BOTH bare
    /// answers about it are lies. `false` hides `--include-backlog`; `true` promises a queue a plain
    /// `take` never reads.
    [<Fact>]
    let ``Ready is always startable, Backlog only on opt-in, and the rest never`` () =
        let startabilityOf w =
            Protocol.boardStatuses |> List.tryFind (fun s -> s.Wire = w) |> Option.map (fun s -> s.Startable)

        Assert.Equal(Some "always", startabilityOf "Ready")
        Assert.Equal(Some "with-backlog-opt-in", startabilityOf "Backlog")
        Assert.Equal(Some "never", startabilityOf "In progress")
        Assert.Equal(Some "never", startabilityOf "Blocked")
        Assert.Equal(Some "never", startabilityOf "In review")
        Assert.Equal(Some "never", startabilityOf "Done")

    /// THE THREE WORDS ARE A CONTRACT WITH A `jq` FILTER, so a rename must red HERE — in F# — and not
    /// only in a shell script.
    ///
    /// MEASURED, and it is why this test exists: renaming a startability spelling used to compile with
    /// ZERO F# errors, and the only thing that noticed was `render_board_statuses` erroring at generation
    /// time. That is the right LAST line of defence and a terrible first one — nothing in the engine's own
    /// suite could see a vocabulary its own projection depends on.
    ///
    /// `generate-projections` selects on exactly these three words. Change one, and this test tells you
    /// the other half of the change is in a `jq` filter.
    [<Fact>]
    let ``the startability wire words are exactly the three generate-projections selects on`` () =
        let spelled =
            [ Schedulability.AlwaysStartable; Schedulability.WithBacklogOptIn; Schedulability.NeverStartable ]
            |> List.map Schedulability.columnStartabilityWireName

        Assert.Equal<string list>([ "always"; "with-backlog-opt-in"; "never" ], spelled)

        // Reflection sees the union, so this is not vacuous: a FOURTH case would reach the wire with no
        // word in the filter, and `render_board_statuses` would error at generation time.
        let cases = FSharpType.GetUnionCases typeof<Schedulability.ColumnStartability>
        Assert.Equal<int>(cases.Length, List.length spelled)
        Assert.Equal<string list>(List.distinct spelled, spelled)

    /// A STATUS DOCUMENTED UNDER AN EMPTY STRING IS UNGREPPABLE BY CONSTRUCTION, and one that means
    /// nothing is a row a filer skips.
    [<Fact>]
    let ``every board status is documented with a meaning`` () =
        for s in Protocol.boardStatuses do
            Assert.False(
                System.String.IsNullOrWhiteSpace s.Wire,
                "a board status is documented under an empty option name")

            Assert.False(System.String.IsNullOrWhiteSpace s.Meaning, $"board status '%s{s.Wire}' means nothing")

    // ================================================================================================
    // THE INVENTORY (#1027) — `factsDocument`, and the schema that describes its shape.
    // ================================================================================================

    /// The key each section states its facts under. A total match, so a new fact SHAPE cannot reach the
    /// document without this file being asked what it is called.
    let private keyOf (section: Protocol.FactSection) =
        match section with
        | Protocol.Rules(key, _) -> key
        | Protocol.Verdicts(key, _) -> key
        | Protocol.BlockerStates(key, _) -> key
        | Protocol.BoardStatuses(key, _) -> key
        | Protocol.ExitCodes(key, _) -> key
        | Protocol.ReleaseColumns(key, _) -> key
        | Protocol.WavePolicy(key, _) -> key
        | Protocol.ReviewPolicy(key, _) -> key
        | Protocol.LifecyclePolicy(key, _) -> key
        | Protocol.LedgerPolicy(key, _) -> key
        | Protocol.SnapshotShape(key, _, _) -> key

    /// How many facts a section states. `0` is the interesting answer — see the emptiness gate below.
    let private countOf (section: Protocol.FactSection) =
        match section with
        | Protocol.Rules(_, rs) -> List.length rs
        | Protocol.Verdicts(_, vs) -> List.length vs
        | Protocol.BlockerStates(_, bs) -> List.length bs
        | Protocol.BoardStatuses(_, ss) -> List.length ss
        | Protocol.ExitCodes(_, cs) -> List.length cs
        | Protocol.ReleaseColumns(_, cs) -> List.length cs
        | Protocol.WavePolicy _ -> 1
        | Protocol.ReviewPolicy _ -> 1
        | Protocol.LifecyclePolicy _ -> 1
        | Protocol.LedgerPolicy _ -> 1
        // The KEYS, not the schema: a shape stating a schema and no keys is exactly the vacuity the
        // emptiness gate below exists to catch, and counting the scalar would hide it behind a `1`.
        | Protocol.SnapshotShape(_, _, keys) -> List.length keys

    /// THE PIN THAT FORCES THE BUMP (#1027) — the one thing `factsDocument` could not do for itself.
    ///
    /// The inventory now lives in `Protocol.fs`, so adding a fact key is one edit. That is the point, and
    /// it is also what makes this gate necessary: the edit is now SO cheap that nothing about it prompts
    /// the author to reconsider the schema version. `fsgg.coord.protocol/6` was a number a human
    /// remembered to increment, and a number a human remembers to increment is a number that drifts —
    /// silently, because a payload that gained a key without a bump agrees with itself and every
    /// projection regenerates green. #266's signature: the reader is told the surface is /6 while it is
    /// looking at /7.
    ///
    /// SO THIS TEST DOES NOT DERIVE THE SCHEMA, IT REFUSES TO LET IT BE FORGOTTEN. A version computed
    /// from the document's own content would bump on a RENAME that changes nothing a reader depends on,
    /// and sit still on a semantic change to what a key MEANS — it would be a hash wearing a version's
    /// clothes. What an increment means is a judgement. This pins both halves so that changing either
    /// without confronting the other is a red test rather than a silent divergence: touch the keys, and
    /// this line makes you say what the schema is now.
    ///
    /// THE LIST IS THE DOCUMENT ORDER, and asserting it as an ordered list rather than a set is
    /// deliberate. `generate-projections` renders sections positionally into the canonical doc, and the
    /// ORDER is part of what `/6` promises a reader.
    [<Fact>]
    let ``the facts document states exactly these keys, in this order, at this schema`` () =
        let keys = Protocol.factsDocument |> List.map keyOf

        Assert.Equal<string list>(
            [ "rules"
              "filingRules"
              "reconcileRules"
              "driverRules"
              "verdicts"
              "blockerStates"
              "boardStatuses"
              "takeExitCodes"
              "landableExitCodes"
              "releaseColumns"
              "wavePolicy"
              "reviewPolicy"
              "lifecyclePolicy"
              "ledgerPolicy"
              "snapshotDocument" ],
            keys
        )

        Assert.Equal("fsgg.coord.protocol/12", Protocol.factsSchema)

    /// THE FLOOR (#266, #436), and the vacuity every gate in this file refuses: an inventory that stated
    /// nothing would make the fold emit `{"schema": …}` and nothing else, and every projection would
    /// render its regions EMPTY and pass `--check` — each block faithful to the list it read.
    [<Fact>]
    let ``no fact section is empty, and the document states some`` () =
        Assert.NotEmpty Protocol.factsDocument

        for section in Protocol.factsDocument do
            Assert.True(
                countOf section > 0,
                $"the facts document states '%s{keyOf section}' with no facts in it — its region renders empty and `--check` still passes"
            )

    /// One key, written twice, is a JSON object with a duplicate member: `Utf8JsonWriter` emits both
    /// happily, and every reader takes whichever it meets last. The projection would then render a key
    /// this file believes is stated once — a document arguing with itself, in the payload nobody authors.
    [<Fact>]
    let ``no two fact sections claim the same key`` () =
        let keys = Protocol.factsDocument |> List.map keyOf
        Assert.Equal<string list>(List.distinct keys, keys)

    /// A KEY IS A GREP TARGET — `generate-projections` selects `.filingRules[]` by name, and a section
    /// under an empty or blank key is one no `jq` filter can address and no reader can find.
    [<Fact>]
    let ``every fact section states its facts under a real key`` () =
        for section in Protocol.factsDocument do
            Assert.False(
                System.String.IsNullOrWhiteSpace(keyOf section),
                "the facts document states a section under an empty key — no projection can select it")
