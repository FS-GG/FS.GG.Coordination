namespace FS.GG.Coord.Cli.Tests

open Xunit
open FS.GG.Coord
open FS.GG.Coord.Types
open FS.GG.Coord.Cli

/// `lint` ASKS the usability rule; it does not decide it (#945, #864, #646).
///
/// THE DEFECT THIS PINS. `TouchSet.usability` was introduced by #864 so that "is this touch-set usable"
/// had ONE home, and `Schedulability` and `Lanes` were migrated onto it. `lint`'s BAD-TOUCH-SET rule was
/// left behind — it reached the same verdict with its own `List.exists` (the threshold), its own
/// `List.choose` (the offending tokens) and its own `List.forall` (the every/some split). It AGREED,
/// because #646 had taught it the partial case by hand.
///
/// Agreeing is not asking, and this org has the receipt: `Schedulability` and `Lanes` agreed too, right
/// up until they drifted into OPPOSITE verdicts on the same partly-unmatchable item — each pinned by a
/// green test asserting the negation of the other's. The rot is in the THRESHOLD, because
/// `TouchSet.unmatchable` hands back a LIST and a list leaves every caller to decide how long it has to
/// be.
///
/// WHY #864 COULD NOT JUST DELETE THE COPY, and what changed. `lint` renders a DIFFERENT SENTENCE for
/// "every token is dead" and "some are", and the partial one is the more urgent (#646). `Usability`
/// carried a single `Unusable` case, so migrating `lint` would have cost it that distinction — it would
/// have had to re-derive the split beside the ask, which is the same copy wearing a smaller hat. #945
/// moved the distinction INTO the rule (`AllUnmatchable` / `SomeUnmatchable`) and let the callers whose
/// verdict does not differ collapse it at their own call sites.
///
/// WHAT THIS FILE ASSERTS, and why it can. `Client.badTouchSetDetail` takes a `Usability` — not a
/// touch-set — so there is no threshold left inside it to get wrong, and the only thing a test must
/// check is that each case the core can produce renders the sentence it should. That is lint's whole
/// remaining share of the question.
module TouchSetLintTests =

    let private status = "Ready"

    let private detailFor (ts: TouchSet) =
        TouchSet.usability ts |> Client.badTouchSetDetail status

    [<Fact>]
    let ``a usable declaration produces NO finding`` () =
        Assert.Equal(None, detailFor (Declared [ Matchable "src/A/" ]))

    [<Fact>]
    let ``the three no-token shapes produce no finding HERE — they are other rules' business (#496)`` () =
        // `Undeclared`, `DeclaredNone` and `Unreadable` are `Usable` (they have no unusable tokens), and
        // that is emphatically not a claim that they are schedulable. Each is refused for its own reason
        // by a rule that runs BEFORE this one — NO-TOUCH-SET names the omission, the `none` sentinel is a
        // decision, an unread body is a failure to look. Conflating the three is exactly what #496 is
        // about, so BAD-TOUCH-SET must stay silent on all of them rather than claim a clean bill.
        for name, ts in
            [ "no declaration", Undeclared
              "the `none` sentinel", DeclaredNone
              "body never read", Unreadable "boom" ] do
            Assert.True(
                (detailFor ts).IsNone,
                $"%s{name}: BAD-TOUCH-SET spoke about a shape that is not its business (#496)"
            )

    [<Fact>]
    let ``EVERY token dead renders the 'as dead as no touch-set' sentence`` () =
        let detail =
            detailFor (Declared [ Unmatchable "**/x"; Unmatchable "**/y" ])

        match detail with
        | None -> failwith "an all-unmatchable declaration produced no BAD-TOUCH-SET finding"
        | Some d ->
            Assert.Contains("EVERY declared `Paths:` token is unmatchable", d)
            // The offending tokens, by name — and ONLY them.
            Assert.Contains("**/x, **/y", d)

    [<Fact>]
    let ``SOME tokens dead renders the WORSE sentence, and names only the dead ones (#646)`` () =
        // The #864/#646 case, and the one the two sentences exist to tell apart. Naming the matchable
        // token here would send the author to re-spell a path that already works.
        let detail =
            detailFor (
                Declared
                    [ Matchable "src/A/"
                      Unmatchable "**/x" ]
            )

        match detail with
        | None -> failwith "a partly-unmatchable declaration produced no BAD-TOUCH-SET finding — this is the #646 defect"
        | Some d ->
            Assert.Contains("at least one of its `Paths:` tokens is unmatchable", d)
            Assert.Contains("WORSE than every token being so", d)
            Assert.Contains("**/x", d)
            Assert.DoesNotContain("src/A/", d)

    [<Fact>]
    let ``the two sentences are DIFFERENT — the distinction #646 bought is not quietly collapsed`` () =
        // WHICH COLLAPSE THIS CATCHES, and which one the COMPILER catches first — measured, because the
        // first draft of this comment asserted the wrong one.
        //
        // Merging the branches (`| AllUnmatchable bad | SomeUnmatchable bad ->`) does NOT reach here: F#
        // rejects it outright with FS0026 "This rule will never be matched", because the surviving arm
        // below becomes unreachable. That is a stronger guard than a test and it is free — it is the
        // whole reason the every/some split lives in the TYPE (#945) rather than in a bool.
        //
        // What no compiler can see is the two arms staying alive and rendering the SAME words. That is
        // the realistic regression — an edit to one sentence copied over the other — and it is what this
        // asserts. Driven: pointing `SomeUnmatchable` at `AllUnmatchable`'s sentence fails the suite.
        //
        // Note this assertion is the weaker half of that guard. Two sentences differing SOMEWHERE is not
        // two sentences meaning different things; the `WORSE`/`at least one` assertions above are what
        // actually pin the meanings, and they are what caught the driven mutation. This one is the
        // backstop for a collapse that leaves no other trace.
        let all = detailFor (Declared [ Unmatchable "**/x" ])

        let some =
            detailFor (
                Declared
                    [ Matchable "src/A/"
                      Unmatchable "**/x" ]
            )

        Assert.True(all.IsSome && some.IsSome, "both shapes must produce a finding")
        Assert.NotEqual<string option>(all, some)

    [<Fact>]
    let ``lint's verdict is the CORE's for every shape — the agreement #864 broke, now three-way`` () =
        // THE PIN. Not "does lint agree today" — it did, for lint's whole life, and so did `Lanes` and
        // `Schedulability` until they didn't. This asserts the two cannot disagree by construction: lint
        // speaks exactly when `TouchSet.usability` says unusable, and is silent exactly when it does not.
        // Its sibling in Core.Tests (`LANES AND THE SCHEDULER AGREE ABOUT EVERY TOUCH-SET SHAPE`) pins the
        // other two consumers to the same rule, so all three surfaces are tied to one function.
        let shapes =
            [ "every token live", Declared [ Matchable "src/A/" ]
              "every token dead", Declared [ Unmatchable "**/x" ]
              "SOME tokens dead — the #864 case",
              Declared
                  [ Matchable "src/A/"
                    Unmatchable "**/x" ]
              "some dead, dead one first",
              Declared
                  [ Unmatchable "**/x"
                    Matchable "src/A/" ]
              "no declaration", Undeclared
              "the `none` sentinel", DeclaredNone
              "body never read", Unreadable "boom" ]

        for name, ts in shapes do
            let usable = TouchSet.usability ts = TouchSet.Usable
            let lintSilent = (detailFor ts).IsNone

            let said = if lintSilent then "said nothing" else "raised BAD-TOUCH-SET"

            Assert.True(
                (usable = lintSilent),
                $"%s{name}: TouchSet.usability says usable=%b{usable} but lint %s{said} — lint is deciding usability for itself again (#945)"
            )

/// `CONSOLIDATION-CANDIDATE` (.github#1914): the overlap graph read for "are these the SAME WORK?"
/// rather than for "can these run at once?".
///
/// THE TWO TRAPS THESE TESTS EXIST TO PIN, both measured on the live board before a line was written.
///
///  1. CONNECTED COMPONENTS IS USELESS HERE. Transitive closure over "shares at least one token" puts
///     74 of 78 `Ready` rows into ONE cluster, because a few files are hubs (`scripts/repos-audit.sh`
///     nine rows, `tests/repos-audit` six, `registry/repos.yml` five). A rule that clustered that way
///     would report the board as one item. So similarity is PAIRWISE and a group is a mutual-similarity
///     set, never a reachability set — and `a chain collapses under closure` is the fixture for it.
///
///  2. HIGH OVERLAP IS NOT THE SAME OPERATION. Four rows scored 0.50+ on the hub token
///     `registry/repos.yml` alone with entirely unrelated objectives, and two of them declared NOTHING
///     BUT that token — plain Jaccard 1.00.
///
/// AND THE PART THAT IS NOT OBVIOUS, which `identical touch-sets are judged by WHO ELSE declares them`
/// is the whole point of: down-weighting hubs INSIDE the similarity ratio cannot fix trap 2, because the
/// weight appears in both the numerator and the denominator and CANCELS. A set is exactly similar to
/// itself under every weighting. The discount only bites when it is spent ABSOLUTELY, on how much
/// distinct evidence the shared part carries — which is why the rule has a second, undivided factor and
/// why a test that only checked "hubs weigh less" would pass over the defect it was written for.
module ConsolidationLintTests =

    open System

    let private repoRow repo ref tokens : LintApplication.ConsolidationRow =
        { Ref = ref
          Repo = repo
          TouchSet = Declared(tokens |> List.map Matchable) }

    let private row ref tokens = repoRow "r" ref tokens

    let private verdict rows =
        LintApplication.consolidationVerdict rows

    let private memberSets (v: LintApplication.ConsolidationVerdict) =
        v.Groups |> List.map (fun g -> Set.ofList g.Members) |> Set.ofList

    let private groupsWith (v: LintApplication.ConsolidationVerdict) (refs: string list) =
        let wanted = Set.ofList refs
        v.Groups |> List.filter (fun g -> Set.isSubset wanted (Set.ofList g.Members))

    // ---- AC2: pairwise, never transitive -----------------------------------------------------------

    /// A CHAIN THAT COLLAPSES INTO ONE COMPONENT UNDER CLOSURE, and must not here.
    ///
    /// Every row declares `hub`, so "shares at least one token" makes all four mutually reachable — the
    /// exact shape that swallowed 74 of 78 real rows. Adjacent rows additionally share a token only they
    /// two declare. Pairwise similarity must therefore resolve A-B, B-C and C-D and must NOT put A with
    /// D, whose entire overlap is the hub.
    [<Fact>]
    let ``a chain collapses under closure and must resolve into ADJACENT pairs only (AC2)`` () =
        let v =
            verdict
                [ row "#A" [ "hub"; "ab" ]
                  row "#B" [ "hub"; "ab"; "bc" ]
                  row "#C" [ "hub"; "bc"; "cd" ]
                  row "#D" [ "hub"; "cd" ] ]

        let sets = memberSets v

        Assert.Equal<Set<Set<string>>>(
            Set.ofList
                [ Set.ofList [ "#A"; "#B" ]
                  Set.ofList [ "#B"; "#C" ]
                  Set.ofList [ "#C"; "#D" ] ],
            sets
        )

        // The closure's signature, asserted directly so a regression names itself.
        Assert.DoesNotContain(sets, fun s -> Set.contains "#A" s && Set.contains "#D" s)
        Assert.DoesNotContain(sets, fun s -> Set.count s = 4)

    /// The structural invariant that IS "pairwise": every pair inside every reported group clears both
    /// floors on its own. A transitive step would show up here as a member pair that does not.
    [<Fact>]
    let ``EVERY pair inside EVERY reported group independently clears both floors (AC2)`` () =
        let rows =
            [ row "#A" [ "hub"; "ab" ]
              row "#B" [ "hub"; "ab"; "bc" ]
              row "#C" [ "hub"; "bc"; "cd" ]
              row "#D" [ "hub"; "cd" ]
              row "#E" [ "solo/one.fs"; "solo/two.fs" ]
              row "#F" [ "solo/one.fs"; "solo/two.fs" ] ]

        let v = verdict rows
        Assert.NotEmpty v.Groups

        let stemsOf ref =
            rows
            |> List.pick (fun r -> if r.Ref = ref then Some r.TouchSet else None)
            |> function
                | Declared ts -> ts |> List.choose (function Matchable t -> Some t | Unmatchable _ -> None)
                | _ -> []

        for g in v.Groups do
            for a in g.Members do
                for b in g.Members do
                    if a < b then
                        // The pair shares SOMETHING — a member pair with no overlap at all could only
                        // have arrived through a third row.
                        let sa = stemsOf a
                        let sb = stemsOf b

                        Assert.True(
                            sa |> List.exists (fun x -> sb |> List.exists (TouchSet.tokensOverlap x)),
                            $"%s{a} and %s{b} are in one group and share no token — that is a transitive admission (AC2)"
                        )

    /// A group must name a touch-set its WHOLE membership declares. A set with no common token is not a
    /// candidate operation, whatever the pairwise scores say.
    [<Fact>]
    let ``every group's Shared touch-set is non-empty and declared by every member`` () =
        let v =
            verdict
                [ row "#A" [ "hub"; "ab" ]
                  row "#B" [ "hub"; "ab"; "bc" ]
                  row "#C" [ "hub"; "bc"; "cd" ]
                  row "#D" [ "hub"; "cd" ] ]

        Assert.NotEmpty v.Groups

        for g in v.Groups do
            Assert.NotEmpty g.Shared

    // ---- AC3: hub tokens are DOWN-WEIGHTED, and the weighting is stated --------------------------

    /// THE TRAP-2 TEST, and the one a naive fix passes without fixing anything.
    ///
    /// Both pairs have IDENTICAL touch-sets, so both score plain Jaccard 1.00 — the maximum — and no
    /// coverage threshold can tell them apart. The ONLY difference is how many OTHER rows declare the
    /// token: `hub` is declared by five rows, `rare` by two. The rare pair must be reported and the hub
    /// pair must not.
    ///
    /// This is also the test that catches the plausible wrong fix. Weighting the similarity RATIO leaves
    /// both pairs at 1.00 — the weight cancels — so a rule that only did that reports the hub pair and
    /// reds here.
    [<Fact>]
    let ``identical touch-sets are judged by WHO ELSE declares them, not by the ratio (AC3)`` () =
        let v =
            verdict
                [ row "#HubP" [ "hub" ]
                  row "#HubQ" [ "hub" ]
                  row "#Other1" [ "hub"; "one/a.fs" ]
                  row "#Other2" [ "hub"; "two/b.fs" ]
                  row "#Other3" [ "hub"; "three/c.fs" ]
                  row "#RareP" [ "rare" ]
                  row "#RareQ" [ "rare" ] ]

        Assert.NotEmpty(groupsWith v [ "#RareP"; "#RareQ" ])

        Assert.Empty(groupsWith v [ "#HubP"; "#HubQ" ])

    /// The weighting, pinned at the three values its stated reading depends on. `1/sqrt(n-1)`, and the
    /// floor is `1/sqrt 2` — so ONE shared token suffices alone at exactly three declaring rows and
    /// stops suffiing at four. A weighting change that broke that reading changes these numbers.
    [<Fact>]
    let ``the weight is 1 over sqrt(n-1), and ONE token suffices at THREE rows but not FOUR (AC3)`` () =
        Assert.Equal(1.0, LintApplication.consolidationTokenWeight 2, 9)
        Assert.Equal(1.0 / sqrt 2.0, LintApplication.consolidationTokenWeight 3, 9)

        // The floor's plain reading, asserted as the inequality a reader would reason with.
        Assert.True(LintApplication.consolidationTokenWeight 2 >= LintApplication.consolidationEvidenceFloor)
        Assert.True(LintApplication.consolidationTokenWeight 3 >= LintApplication.consolidationEvidenceFloor)
        Assert.True(LintApplication.consolidationTokenWeight 4 < LintApplication.consolidationEvidenceFloor)

        // STRICTLY decreasing in the number of declaring rows — "a token declared by many rows
        // contributes less than one declared by two" is the whole rule, so a flat or rising weight is
        // not a milder version of it.
        for n in 2..11 do
            Assert.True(
                LintApplication.consolidationTokenWeight (n + 1) < LintApplication.consolidationTokenWeight n,
                $"weight did not fall from %d{n} to %d{n + 1} declaring rows — hub tokens are not being down-weighted (AC3)"
            )

    /// The measured board shape, in miniature: a real group whose shared tokens are THEMSELVES hubs must
    /// survive, while a single hub token must not carry a pair on its own.
    ///
    /// This is the constraint that rejects the undamped weighting. `repos-audit`'s two shared tokens are
    /// declared by nine and six rows; under `1/(n-1)` the pair scores 0.33 and the real group is lost.
    /// TWO middling hubs must outweigh ONE.
    [<Fact>]
    let ``TWO hub tokens together carry a pair that ONE hub token cannot (AC3)`` () =
        let others =
            [ for i in 1..4 -> row $"#Filler%d{i}" [ "hubA"; "hubB"; $"filler%d{i}.fs" ] ]

        let v =
            verdict (
                [ row "#Pair1" [ "hubA"; "hubB" ]
                  row "#Pair2" [ "hubA"; "hubB" ]
                  row "#Single1" [ "hubC" ]
                  row "#Single2" [ "hubC" ]
                  row "#SingleFill1" [ "hubC"; "s1.fs" ]
                  row "#SingleFill2" [ "hubC"; "s2.fs" ]
                  row "#SingleFill3" [ "hubC"; "s3.fs" ] ]
                @ others
            )

        Assert.NotEmpty(groupsWith v [ "#Pair1"; "#Pair2" ])
        Assert.Empty(groupsWith v [ "#Single1"; "#Single2" ])

    // ---- AC7: the negative control — a PLANTED cluster must make the rule say YES -------------------

    /// A rule that cannot say YES on a planted cluster is not a rule (.github#1810 AC3). Two rows with
    /// an identical touch-set, injected into a population that otherwise produces nothing, must be found
    /// AND NAMED — both refs, and the touch-set they share.
    [<Fact>]
    let ``injecting two rows with an IDENTICAL touch-set makes the rule fire and NAME them (AC7)`` () =
        let background =
            [ row "#Bg1" [ "alpha/one.fs" ]
              row "#Bg2" [ "beta/two.fs" ]
              row "#Bg3" [ "gamma/three.fs" ] ]

        Assert.Empty (verdict background).Groups

        let v =
            verdict (
                background
                @ [ row "#Planted1" [ "planted/x.fs"; "planted/y.fs" ]
                    row "#Planted2" [ "planted/x.fs"; "planted/y.fs" ] ]
            )

        match groupsWith v [ "#Planted1"; "#Planted2" ] with
        | [] -> failwith "the rule did not fire on a planted identical-touch-set cluster (AC7)"
        | g :: _ ->
            Assert.Equal<string list>([ "#Planted1"; "#Planted2" ], g.Members)
            Assert.Equal<string list>([ "planted/x.fs"; "planted/y.fs" ], g.Shared)

            let detail = LintApplication.consolidationDetail g
            Assert.Contains("#Planted1", detail)
            Assert.Contains("#Planted2", detail)

    // ---- AC5: FAIL CLOSED — an unreadable row is reported, never dropped ---------------------------

    /// #266. A row whose `Paths:` could not be read is a row that was compared against NOTHING, so the
    /// verdict over that board is a NO-VERDICT and not an absence of clusters. Silently shrinking the
    /// population would answer "nothing to consolidate" about rows nobody looked at.
    [<Fact>]
    let ``a row whose Paths could not be read is REPORTED, and the population records the shortfall (AC5)`` () =
        let v =
            verdict
                [ row "#Ok1" [ "planted/x.fs"; "planted/y.fs" ]
                  row "#Ok2" [ "planted/x.fs"; "planted/y.fs" ]
                  { Ref = "#Blind"
                    Repo = "r"
                    TouchSet = Unreadable "boom" } ]

        Assert.Equal<(string * string) list>([ "#Blind", "boom" ], v.Unreadable)

        // Never dropped: the shortfall is VISIBLE in the counts, so "no clusters" can be told from
        // "clusters over a population that was short a row".
        Assert.Equal(3, v.Population)
        Assert.Equal(2, v.Compared)

        // And the groups it COULD reach are still reported — fail-closed is not fail-silent.
        Assert.NotEmpty(groupsWith v [ "#Ok1"; "#Ok2" ])

        let detail = LintApplication.consolidationUnreadableDetail "boom"
        Assert.Contains("boom", detail)
        Assert.Contains("INCOMPLETE, not empty", detail)

    /// The three token-less shapes are not unreadable and are not a finding here — each is another
    /// rule's business (#496), and they reserve nothing to compare.
    [<Fact>]
    let ``Undeclared, the none sentinel and the any chore are compared against nothing and reported by nobody here`` () =
        let v =
            verdict
                [ { Ref = "#None"; Repo = "r"; TouchSet = DeclaredNone }
                  { Ref = "#Undeclared"; Repo = "r"; TouchSet = Undeclared }
                  { Ref = "#Chore"; Repo = "r"; TouchSet = DeclaredChore } ]

        Assert.Empty v.Groups
        Assert.Empty v.Unreadable
        Assert.Equal(0, v.Compared)
        Assert.Equal(3, v.Population)

    /// #273: a token that can match no file reserves nothing, so it cannot make two rows the same work
    /// either. Admitting unmatchable tokens would let a group be proposed on the strength of two tokens
    /// that name nothing at all.
    [<Fact>]
    let ``UNMATCHABLE tokens never make a group, however identical they are (#273)`` () =
        let v =
            verdict
                [ { Ref = "#Dead1"
                    Repo = "r"
                    TouchSet = Declared [ Unmatchable "**/x"; Unmatchable "**/y" ] }
                  { Ref = "#Dead2"
                    Repo = "r"
                    TouchSet = Declared [ Unmatchable "**/x"; Unmatchable "**/y" ] } ]

        Assert.Empty v.Groups
        Assert.Equal(0, v.Compared)

    // ---- #353: tokens are repo-relative --------------------------------------------------------

    /// `TouchSet.conflicts` states the contract — both token lists must come from the same repo — and
    /// `lint` without `--repo` scans every repo on the board. Comparing across repos invents collisions
    /// that do not exist, and here it would invent whole shared operations.
    [<Fact>]
    let ``two rows with IDENTICAL touch-sets in DIFFERENT repos are never one group (#353)`` () =
        let v =
            verdict
                [ repoRow "alpha" "alpha#1" [ "src/A.fs"; "src/B.fs" ]
                  repoRow "beta" "beta#1" [ "src/A.fs"; "src/B.fs" ] ]

        Assert.Empty v.Groups

        // ...and the same two declarations inside ONE repo ARE a group, so the test is measuring the
        // repo partition and not some accident of the fixture.
        let same =
            verdict
                [ repoRow "alpha" "alpha#1" [ "src/A.fs"; "src/B.fs" ]
                  repoRow "alpha" "alpha#2" [ "src/A.fs"; "src/B.fs" ] ]

        Assert.NotEmpty same.Groups

    // ---- AC1 / AC6 / AC8: what the finding must SAY ------------------------------------------------

    /// The finding carries the four things the runner needs and cannot get elsewhere: who, WHAT THEY
    /// SHARE (AC1 — so it can judge same-operation without opening every issue), what the rule cannot
    /// see (AC6 — in the finding, not only in a docstring), and whose decision this is (AC8).
    [<Fact>]
    let ``the finding prints the shared tokens, states its blind spot, and hands the decision to the runner`` () =
        let v =
            verdict
                [ row "#One" [ "shared/a.fs"; "shared/b.fs" ]
                  row "#Two" [ "shared/a.fs"; "shared/b.fs" ] ]

        let detail =
            v.Groups |> List.head |> LintApplication.consolidationDetail

        // AC1 — the shared touch-set, by name.
        Assert.Contains("shared/a.fs", detail)
        Assert.Contains("shared/b.fs", detail)

        // AC6 — the blind spot, stated. Disjoint `Paths:` can still be one operation and nothing here
        // sees that; a shared touch-set is evidence about FILES, not about objectives.
        Assert.Contains("DISJOINT", detail)
        Assert.Contains("never proof of the same OBJECTIVE", detail)

        // AC8 — the disposition. Not automatic, and not escalated to somebody else.
        Assert.Contains("THE DISPOSITION IS YOURS", detail)
        Assert.Contains("writes nothing", detail)

    // ---- AC4: it decides, and that is ALL it does --------------------------------------------------

    /// `lint` stays report-only, and the shape of that promise here is that the verdict is a pure
    /// function of the rows: same input, same answer, and the ORDER rows arrive in cannot change it.
    /// An implementation that had reached for anything outside its arguments would not be able to
    /// promise the second.
    [<Fact>]
    let ``the verdict is a pure function of its rows — order cannot change it (AC4)`` () =
        let rows =
            [ row "#A" [ "hub"; "ab" ]
              row "#B" [ "hub"; "ab"; "bc" ]
              row "#C" [ "hub"; "bc"; "cd" ]
              row "#D" [ "hub"; "cd" ]
              row "#E" [ "solo/one.fs"; "solo/two.fs" ]
              row "#F" [ "solo/one.fs"; "solo/two.fs" ] ]

        let forward = verdict rows
        let backward = verdict (List.rev rows)

        Assert.Equal<Set<Set<string>>>(memberSets forward, memberSets backward)
        Assert.Equal<string list list>(
            forward.Groups |> List.map (fun g -> g.Shared),
            backward.Groups |> List.map (fun g -> g.Shared)
        )
        Assert.Equal(forward.Compared, backward.Compared)

    /// The scores a reader is shown are a FLOOR over the group, never a mean that flatters it: the
    /// weakest pair's numbers, so "every pair here is at least this similar" is literally true.
    [<Fact>]
    let ``a group's reported scores are its WEAKEST pair's, not an average`` () =
        let v =
            verdict
                [ row "#A" [ "core/x.fs"; "core/y.fs" ]
                  row "#B" [ "core/x.fs"; "core/y.fs" ]
                  row "#C" [ "core/x.fs"; "core/y.fs"; "core/z.fs" ] ]

        match groupsWith v [ "#A"; "#B"; "#C" ] with
        | [] -> failwith "three rows sharing two exclusive tokens pairwise were not grouped"
        | g :: _ ->
            // A-B is Jaccard 1.00; A-C and B-C are 2/3. The group must report the 2/3.
            Assert.True(
                g.Coverage < 1.0,
                $"the group reported coverage %f{g.Coverage} — that is the BEST pair's score, not the weakest"
            )

            Assert.True(g.Coverage >= LintApplication.consolidationCoverageFloor)
            Assert.True(g.Evidence >= LintApplication.consolidationEvidenceFloor)

    /// A MUTUALLY-SIMILAR SET WITH NO TOKEN IN COMMON is not a candidate operation, and this is the
    /// fixture where that stops being a formality.
    ///
    /// The four rows below are pairwise similar — every one of the six pairs shares two of a four-token
    /// union, clearing both floors — yet NO token is declared by all four: each row is missing exactly
    /// the one the other three share. So "these four are one piece of work" is a claim nothing in the
    /// data supports; what the data supports is the four overlapping TRIPLES, each with its own shared
    /// file. Proposing the quadruple would hand the runner a merge with no common subject.
    ///
    /// FOUND BY SEARCH, NOT BY INTUITION. Three mutually-similar rows CANNOT do this — at coverage 0.5
    /// a triangle is forced to share something — so the guard looks vacuous until you reach four, and a
    /// fixture built by hand at three would have left it untested. The live board happens not to contain
    /// one today, which is exactly why it is pinned here rather than left to be noticed later.
    [<Fact>]
    let ``four mutually-similar rows with NO common token are never proposed as one group`` () =
        let v =
            verdict
                [ row "#W" [ "a.fs"; "b.fs"; "d.fs" ]
                  row "#X" [ "a.fs"; "c.fs"; "d.fs" ]
                  row "#Y" [ "b.fs"; "c.fs"; "d.fs" ]
                  row "#Z" [ "a.fs"; "b.fs"; "c.fs" ]
                  // Holds `d.fs` down to a fourth declarer so the fixture's weights are stable; its own
                  // coverage against every other row is 1/3, so it never joins anything.
                  row "#Filler" [ "d.fs" ] ]

        // The quadruple is pairwise-legal and still must not be proposed; what IS proposed is every
        // maximal TRIPLE, each named by the one file its three members actually share.
        //
        // Asserting the whole set rather than just the quadruple's absence is deliberate. Refusing the
        // quadruple is easy to do by ACCIDENT — a search that drops the infeasible vertex in the wrong
        // place reports `#W #X #Y` and silently loses the other three triples, which is a worse answer
        // than the quadruple was: real candidates vanish and nothing says so. That defect was live in
        // this file until this assertion was written.
        Assert.Equal<Set<Set<string>>>(
            Set.ofList
                [ Set.ofList [ "#W"; "#X"; "#Y" ]
                  Set.ofList [ "#W"; "#X"; "#Z" ]
                  Set.ofList [ "#W"; "#Y"; "#Z" ]
                  Set.ofList [ "#X"; "#Y"; "#Z" ] ],
            memberSets v
        )

        Assert.Empty(groupsWith v [ "#W"; "#X"; "#Y"; "#Z" ])

        // What IS proposed still has a subject, every time.
        for g in v.Groups do
            Assert.NotEmpty g.Shared
