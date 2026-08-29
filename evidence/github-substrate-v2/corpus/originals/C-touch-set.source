namespace FS.GG.Coord.Tests

open Xunit
open FsCheck
open FsCheck.Xunit
open FS.GG.Coord
open FS.GG.Coord.Types

/// The `Paths:` grammar. Every test here is an incident.
module TouchSetTests =

    // ---- #277: a declaration is a line you WROTE as one --------------------------------------------
    // A `Paths:` line inside a fenced code block is a QUOTATION of the grammar, not a use of it — and
    // the protocol docs quote it constantly. An item that "declares" its touch-set only inside a fence
    // has declared nothing, and a token that reserves nothing conflicts with nothing, which is how two
    // workers end up in one file. The rule: unschedulable beats mis-scheduled.

    [<Fact>]
    let ``#277 a Paths: line inside a fence is a QUOTATION, not a declaration`` () =
        let body =
            "Here is how you declare a touch-set:\n\
             \n\
             ```\n\
             Paths: src/Example/**\n\
             ```\n\
             \n\
             ...and this issue does not declare one."

        Assert.Equal(Undeclared, TouchSet.parse body)

    [<Fact>]
    let ``#277 a real declaration OUTSIDE the fence is still found`` () =
        let body =
            "Quoting the grammar:\n\
             \n\
             ```\n\
             Paths: src/Quoted/**\n\
             ```\n\
             \n\
             Paths: src/Real/**"

        Assert.Equal(Declared [ Matchable "src/Real/**" ], TouchSet.parse body)

    // ---- #435: a BACKTICKED declaration was refused as unmatchable ---------------------------------
    // ...so the item silently never scheduled, and `take` reported an empty queue over it. Backticks
    // are markdown, not grammar.

    [<Fact>]
    let ``#435 backticks are stripped — a backticked declaration is a declaration`` () =
        Assert.Equal(Declared [ Matchable "src/Scene/**" ], TouchSet.parse "Paths: `src/Scene/**`")

    [<Fact>]
    let ``#2104 a declaration is one physical line — later lines are not silently path tokens`` () =
        // `TouchSet.parse` intentionally reads issue-body declarations one physical line at a time. This
        // pins that boundary so argv callers must split embedded newlines before constructing their
        // synthetic `Paths:` body rather than assuming the parser will reinterpret ordinary body lines.
        Assert.Equal(
            Declared [ Matchable "src/A.fs" ],
            TouchSet.parse "Paths: src/A.fs\nsrc/B.fs\nsrc/C.fs"
        )

    // ---- #496: `Paths: none` is a DECISION; a missing line is an OMISSION ---------------------------
    // Before they were told apart they rendered identically, so no gate could be written at all: nine
    // items of real work went invisible to every worker who asked for work, and the one surface whose
    // job is board health reported `0 error(s)` over a dead queue. Neither is schedulable. Only one is
    // a bug. THE TYPE now makes conflating them impossible.

    [<Fact>]
    let ``#496 'Paths: none' is the SENTINEL, not an omission`` () =
        Assert.Equal(DeclaredNone, TouchSet.parse "An epic.\n\nPaths: none")

    [<Fact>]
    let ``#496 no Paths: line at all is an OMISSION, and a different fact`` () =
        Assert.Equal(Undeclared, TouchSet.parse "An item somebody forgot to declare.")

    [<Fact>]
    let ``#496 the two are not equal — the whole point of the issue`` () =
        Assert.NotEqual<TouchSet>(DeclaredNone, Undeclared)

    // ---- #863: the sentinel is decided over the TOKEN SET, not the concatenated string --------------
    // `parse` concatenated every declaration into one string and asked `isNoneSentinel` of THAT. Two
    // bare `Paths: none` lines make `"none none"` — not `"none"` — so the sentinel was lost and `none`
    // fell through to `classify`, which called it `Matchable`: it is a perfectly path-shaped token. The
    // epic went `Startable` AND reserved a `none` directory that exists nowhere, so it read as disjoint
    // from every worker on the board. #496 and #273 firing together, through the parser built to end
    // both. The `Unmatchable` gate was structurally blind to it — `none` IS matchable; it matches no
    // file that EXISTS, which is the one thing the gate does not test.
    //
    // The tests are on the REPEATED declaration specifically. A single `Paths: none` passed throughout,
    // which is precisely how this shipped: the covering test reproduced the blind spot.

    [<Fact>]
    let ``#863 a REPEATED 'Paths: none' is still the sentinel — not a path called none`` () =
        Assert.Equal(DeclaredNone, TouchSet.parse "An epic.\n\nPaths: none\n\nMore prose.\n\nPaths: none")

    [<Fact>]
    let ``#863 five of them are still the sentinel — the union does not accumulate into a path`` () =
        let body = "An epic.\n" + String.replicate 5 "\nPaths: none\n"
        Assert.Equal(DeclaredNone, TouchSet.parse body)

    [<Fact>]
    let ``#863 a repeated sentinel is never a Matchable token — the exact fail-open`` () =
        // The precise shape of the bug: `Declared [Matchable "none"]` is startable and reserves a
        // directory that does not exist. Assert against the FAIL-OPEN, not merely for the right answer.
        match TouchSet.parse "Paths: none\nPaths: none" with
        | Declared ts ->
            Assert.Fail(
                $"a repeated sentinel parsed as a DECLARATION of %A{ts} — it reserves a 'none' directory that does not exist, so it is disjoint from every worker on the board"
            )
        | DeclaredNone -> ()
        | other -> Assert.Fail $"expected DeclaredNone, got %A{other}"

    [<Fact>]
    let ``#863 case and backticks do not defeat the token-set test`` () =
        Assert.Equal(DeclaredNone, TouchSet.parse "Paths: `none`\nPaths: NONE")

    [<Fact>]
    let ``#863 'none' beside real paths is a CONTRADICTION — Unmatchable, never unioned in`` () =
        // "I touch nothing" and "I touch src/A" cannot both hold. Refuse it: `Unmatchable` reserves
        // nothing and covers nothing, so `Schedulability` refuses the item rather than handing out a
        // touch-set with a fake `none` reservation silently mixed into it.
        Assert.Equal(
            Declared [ Unmatchable "none"; Matchable "src/A/**" ],
            TouchSet.parse "Paths: none\nPaths: src/A/**"
        )

    [<Fact>]
    let ``#863 a contradictory declaration is reported as unmatchable, so the gate can see it`` () =
        Assert.Equal<string list>([ "none" ], TouchSet.parse "Paths: none src/A/**" |> TouchSet.unmatchable)

    [<Fact>]
    let ``#863 a bare 'Paths:' is an OMISSION — List.forall is vacuously true on the empty set`` () =
        // The trap inside the fix itself. Testing `List.forall isNoneSentinel` over an EMPTY token list
        // answers TRUE, so a `Paths:` line with nothing after it would report `DeclaredNone` — a
        // DECISION nobody made. That is #496's conflation with the two sides swapped, and it would read
        // as deliberate rather than as the omission it is. The empty case must be decided FIRST.
        Assert.Equal(Undeclared, TouchSet.parse "Paths:")
        Assert.Equal(Undeclared, TouchSet.parse "Paths:\nPaths:   ")

    // ---- #273: not a glob language ------------------------------------------------------------------
    // A token that matches nothing CONFLICTS WITH NOTHING, so it reads as DISJOINT against every other
    // worker — ADR-0021's own failure, one level down: a lock that succeeds under exactly the
    // conditions it exists to prevent.

    [<Theory>]
    [<InlineData("src/Scene/**")>]
    [<InlineData("src/Scene/*")>]
    [<InlineData("src/Scene/")>]
    [<InlineData("Directory.Packages.props")>]
    let ``#273 the sanctioned grammar is matchable`` (token: string) =
        match TouchSet.classify token with
        | Matchable _ -> ()
        | Unmatchable t -> failwith $"'%s{t}' is part of the grammar and must be matchable"

    [<Theory>]
    [<InlineData("**/packages.lock.json")>] // a LEADING **/ matches nothing
    [<InlineData("src/*/Types.fs")>] // a * in the MIDDLE matches nothing
    [<InlineData("src/Scene/?.fs")>]
    [<InlineData("src/[Ss]cene/**")>]
    let ``#273 a token that can match no file is refused, never tolerated`` (token: string) =
        match TouchSet.classify token with
        | Unmatchable _ -> ()
        | Matchable t -> failwith $"'%s{t}' can match no file — treating it as a token makes it DISJOINT against everyone"

    // ---- #1507: a FLAG is never a path ---------------------------------------------------------------
    // `widen <ref> --paths <tokens> --json` wrote `--json` into a live claim's `Paths:` line at exit 0.
    // The parser handed it over (fixed in `Options`), but this module is why it LANDED: `--json` carries
    // no glob metacharacter, so `classify` called it `Matchable` and `Writes.validate` — the one gate
    // between a bad token and the PATCH — had nothing to object to.
    //
    // Note which way that fails. #273 above is about tokens that are REFUSED for matching nothing. This
    // was the opposite and worse: a token ACCEPTED as matching something, which then matched nothing, so
    // the claim read DISJOINT against every worker while reserving none of the files its author named.

    [<Theory>]
    [<InlineData("--json")>]
    [<InlineData("--text")>]
    [<InlineData("--lease")>]
    [<InlineData("-n")>]
    [<InlineData("--include-backlog")>]
    let ``#1507 a flag-shaped token can never be a path — Matchable was the fail-open`` (token: string) =
        Assert.True(TouchSet.isFlagShaped token, $"'%s{token}' is argv, not a path")

        match TouchSet.classify token with
        | Unmatchable _ -> ()
        | Matchable t ->
            failwith $"'%s{t}' is a FLAG — calling it matchable is what let `widen --paths ... --json` corrupt a live declaration"

    [<Fact>]
    let ``#1507 the flag-shape rule survives the separator whitespace parse leaves behind`` () =
        // `parse` splits on commas AND spaces, so a token arrives with whatever padding the author left.
        // A rule that only caught the unpadded spelling is a rule the next corrupt write walks around.
        Assert.True(TouchSet.isFlagShaped " --json")
        Assert.True(TouchSet.isFlagShaped "--json ")

    [<Fact>]
    let ``#1507 a real path is NOT flag-shaped — the guard must not eat the grammar`` () =
        // The mirror. A guard that also refused real declarations would just move the defect.
        for token in [ "src/FS.GG.Coord.Core/"; "Directory.Packages.props"; "src/Scene/**"; "-- odd/name" ] do
            if token.StartsWith "-" then
                Assert.True(TouchSet.isFlagShaped token)
            else
                Assert.False(TouchSet.isFlagShaped token, $"'%s{token}' is a path and must survive")

                match TouchSet.classify token with
                | Matchable _ -> ()
                | Unmatchable t -> failwith $"'%s{t}' is a legitimate declaration and must stay matchable"

    [<Fact>]
    let ``#1507 a swallowed flag makes the WHOLE declaration unusable, not a quietly ignored extra`` () =
        // THE END-TO-END SHAPE OF THE REPORTED BUG, read back through `parse`. This is the exact body
        // `widen` wrote to FS.GG.Governance#326. Before the fix it was `Usable`: every token matchable,
        // the item schedulable, and `--json` reserving nothing at all.
        //
        // `SomeUnmatchable` is the RIGHT verdict rather than merely a louder one — it is #646's worse
        // case, where the item LOOKS declared while one token silently reserves nothing. It is what makes
        // `take` exit 3 (EX_REFUSED) instead of handing a second worker the same files.
        let ts =
            TouchSet.parse
                "Paths: .claude/skills/, .codex/skills/, scripts/materialize-skill-roots.sh, --json"

        Assert.Equal(TouchSet.SomeUnmatchable [ "--json" ], TouchSet.usability ts)

    // ---- overlap: exact equality OR subtree containment, either direction ---------------------------

    [<Fact>]
    let ``overlap: a directory contains its own files`` () =
        Assert.True(TouchSet.tokensOverlap "src/Scene" "src/Scene/Types.fs")
        Assert.True(TouchSet.tokensOverlap "src/Scene/Types.fs" "src/Scene")

    [<Fact>]
    let ``overlap: a trailing glob is the same subtree`` () =
        Assert.True(TouchSet.tokensOverlap "src/Scene/**" "src/Scene/Types.fs")

    [<Fact>]
    let ``overlap: a PREFIX is not a subtree — src/Scene must not swallow src/SceneGraph`` () =
        // The `/` in the containment test is the whole difference. Without it, every worker in
        // `src/Scene` would block every worker in `src/SceneGraph`, and the scheduler would serialise
        // the repo for no reason.
        Assert.False(TouchSet.tokensOverlap "src/Scene" "src/SceneGraph/Types.fs")

    [<Fact>]
    let ``#309 declaring a PARENT reserves the child exactly as effectively as naming it`` () =
        // The trap behind #309: FS.GG.Game declared `readiness/**` to cover a generated baseline, and
        // every [core] item then pairwise-overlapped every other — the whole P6 Game phase collapsed to
        // ONE worker, in the phase the protocol exists to fan out.
        let parent = Declared [ Matchable "readiness/**" ]
        let child = Declared [ Matchable "readiness/surface-baselines/pkg.txt" ]
        Assert.NotEmpty(TouchSet.conflicts parent child)

    [<Fact>]
    let ``#1843 directory reservation strictly contains a sibling's future file`` () =
        let wide = TouchSet.parse "Paths: docs/reports"
        let narrow = TouchSet.parse "Paths: docs/reports/new-file.md"

        Assert.True(TouchSet.strictlyContains wide narrow)
        Assert.False(TouchSet.strictlyContains narrow wide)

    [<Fact>]
    let ``disjoint touch-sets may run in parallel`` () =
        let a = Declared [ Matchable "src/Scene/**" ]
        let b = Declared [ Matchable "src/Audio/**" ]
        Assert.Empty(TouchSet.conflicts a b)

    [<Fact>]
    let ``#1732 equal tokens in different declared repo scopes are disjoint by construction`` () =
        let a = Declared [ Matchable "scripts/skill-view" ]
        let b = Declared [ Matchable "scripts/skill-view" ]

        Assert.Empty(
            TouchSet.scopedConflicts "FS-GG" "FS.GG.Audio" "FS-GG" ".github" a b
        )

    [<Fact>]
    let ``#1732 equal tokens in the same declared repo scope still collide`` () =
        let a = Declared [ Matchable "scripts/skill-view" ]
        let b = Declared [ Matchable "scripts/skill-view" ]

        Assert.NotEmpty(
            TouchSet.scopedConflicts "FS-GG" "FS.GG.Audio" "FS-GG" "FS.GG.Audio" a b
        )

    // ---- .github#2305 / ADR-0044: generated, CI-gated artifacts are not reservable ------------------
    // `verify-paths` already excludes a repo's generated artifacts from DRIFT (ADR-0044, #309, #498).
    // That exemption never reached the RESERVATION side: `widen` granted a real reservation on a
    // generated manifest exactly as though it were authored, and the reservation serialised a second,
    // genuinely disjoint worker behind the first for a file neither of them authors — the row's own
    // measured instance, `.github#2254`/`.github#2248` colliding on `registry/driver-skill-manifest.json`.

    [<Fact>]
    let ``#2305 generatedTokens is empty when nothing requested is generated`` () =
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        Assert.Empty(TouchSet.generatedTokens generated [ "src/Scene/Types.fs"; "docs/reports" ])

    [<Fact>]
    let ``#2305 generatedTokens names an exact match`` () =
        let generated =
            Set.ofList [ "registry/driver-skill-manifest.json"; "registry/coordination-kit-skill-manifest.json" ]

        let requested = [ ".claude/skills/drive-board/SKILL.md"; "registry/driver-skill-manifest.json" ]

        Assert.Equal<string list>([ "registry/driver-skill-manifest.json" ], TouchSet.generatedTokens generated requested)

    [<Fact>]
    let ``#2305 generatedTokens does NOT catch a directory-prefix request — the #309 trap stays open for real claims`` () =
        // Declaring the generated file's PARENT is a real claim over everything under it, generated or
        // not (the same reasoning `#309 declaring a PARENT reserves...` pins above). `generatedTokens`
        // must not treat `registry/**` as though it named `registry/driver-skill-manifest.json` — a
        // worker who genuinely means to touch the whole directory is not exempted.
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        Assert.Empty(TouchSet.generatedTokens generated [ "registry/**" ])

    [<Fact>]
    let ``#2305 generatedTokens is a no-op against the FS.GG.Kit Version field — it is absent from the generated roster`` () =
        // AC-4's distinction, proven rather than merely asserted: the kit csproj is a genuine
        // single-writer semantic field (check-kit-published-coherence), not a generated artifact, so it
        // must never be caught here — this is what keeps it colliding normally.
        let generated =
            Set.ofList [ "registry/driver-skill-manifest.json"; "registry/coordination-kit-skill-manifest.json" ]

        Assert.Empty(TouchSet.generatedTokens generated [ "src/FS.GG.Kit/FS.GG.Kit.csproj" ])

    [<Fact>]
    let ``#2305 excludeGenerated drops a pair where both sides exactly name the same generated artifact`` () =
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        let pairs = [ ("registry/driver-skill-manifest.json", "registry/driver-skill-manifest.json") ]
        Assert.Empty(TouchSet.excludeGenerated generated pairs)

    [<Fact>]
    let ``#2305 excludeGenerated keeps a pair that shares no generated token`` () =
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        let pairs = [ ("src/Scene/Types.fs", "src/Scene/Types.fs") ]
        Assert.Equal<(string * string) list>(pairs, TouchSet.excludeGenerated generated pairs)

    [<Fact>]
    let ``#2305 excludeGenerated keeps an ASYMMETRIC pair — a directory claim over a generated file still collides`` () =
        // The other half of the #309 guard: one side names the generated file exactly, the other
        // declares its parent directory. That is a real claim (the parent side may touch other, real
        // files too), so the pair must NOT be dropped even though one stem is in `generated`.
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        let pairs = [ ("registry/driver-skill-manifest.json", "registry/**") ]
        Assert.Equal<(string * string) list>(pairs, TouchSet.excludeGenerated generated pairs)

    [<Fact>]
    let ``#2305 excludeGenerated is a no-op against the FS.GG.Kit Version field pair — genuinely serialises`` () =
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        let pairs = [ ("src/FS.GG.Kit/FS.GG.Kit.csproj", "src/FS.GG.Kit/FS.GG.Kit.csproj") ]
        Assert.Equal<(string * string) list>(pairs, TouchSet.excludeGenerated generated pairs)

    [<Fact>]
    let ``#2305 equivalent constructed pair — two disjoint skill edits sharing only a generated manifest token are DISJOINT`` () =
        // The row's own measured instance, reconstructed: `.github#2254` edited a driver skill and
        // regenerated `registry/driver-skill-manifest.json`; `.github#2248` edited a pnext-item
        // reference and needed the same regeneration. Their REAL subjects never overlapped — only the
        // generated token did — and this is the acceptance-criterion-1 demonstration that they can now
        // lane concurrently.
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]

        let a =
            Declared [ Matchable ".claude/skills/drive-board/SKILL.md"; Matchable "registry/driver-skill-manifest.json" ]

        let b =
            Declared
                [ Matchable ".claude/skills/pnext-item/references/independent-review.md"
                  Matchable "registry/driver-skill-manifest.json" ]

        Assert.NotEmpty(TouchSet.conflicts a b) // raw conflicts still fires on the shared generated token today
        Assert.Empty(TouchSet.conflicts a b |> TouchSet.excludeGenerated generated) // the remedy clears it

    [<Fact>]
    let ``#2305 a genuinely shared non-generated path still reports OVERLAP after exclusion`` () =
        let generated = Set.ofList [ "registry/driver-skill-manifest.json" ]
        let a = Declared [ Matchable "src/FS.GG.Kit/FS.GG.Kit.csproj" ]
        let b = Declared [ Matchable "src/FS.GG.Kit/FS.GG.Kit.csproj" ]
        Assert.NotEmpty(TouchSet.conflicts a b |> TouchSet.excludeGenerated generated)

    // ---- properties ---------------------------------------------------------------------------------

    [<Property>]
    let ``overlap is symmetric`` (a: NonNull<string>) (b: NonNull<string>) =
        TouchSet.tokensOverlap a.Get b.Get = TouchSet.tokensOverlap b.Get a.Get

    [<Property>]
    let ``a token always overlaps itself`` (t: NonNull<string>) =
        TouchSet.tokensOverlap t.Get t.Get

    [<Fact>]
    let ``an unmatchable token is never silently dropped — it survives as a NAMED case`` () =
        // The failure mode this forecloses: a parser that discards what it cannot understand produces
        // a touch-set that looks smaller and cleaner than the truth, and reserves less than the author
        // asked for. Every token must come back, classified.
        match TouchSet.parse "Paths: src/Ok/**, **/bad.json" with
        | Declared tokens ->
            Assert.Equal(2, List.length tokens)
            Assert.Equal<string list>([ "**/bad.json" ], TouchSet.unmatchable (Declared tokens))
        | other -> failwith $"expected a declaration, got %A{other}"

    // ---- usability: THE RULE, AND IT LIVES HERE ONCE (#864) ---------------------------------------

    [<Fact>]
    let ``ANY unmatchable token makes a touch-set unusable — not every one of them (#864)`` () =
        // THE THRESHOLD, PINNED. It is the whole reason `usability` exists rather than every caller
        // counting `unmatchable`'s list for itself: `Schedulability` refused on >=1 dead token while
        // `Lanes` laned on >=1 LIVE token, so a partly-dead declaration was simultaneously "never
        // startable" and "lanable, not a chore" — each pinned by a green test asserting the negation of
        // the other's (#864). A partly-dead declaration is the WORSE case, not the lesser one: it looks
        // declared, and its dead tokens reserve nothing.
        Assert.Equal(TouchSet.Usable, TouchSet.usability (Declared [ Matchable "src/A/" ]))

        Assert.Equal(
            TouchSet.AllUnmatchable [ "**/x" ],
            TouchSet.usability (Declared [ Unmatchable "**/x" ])
        )

        // The case the two modules disagreed about. ONE live token does NOT rescue it.
        Assert.Equal(
            TouchSet.SomeUnmatchable [ "**/x" ],
            TouchSet.usability (Declared [ Matchable "src/A/"; Unmatchable "**/x" ])
        )

    [<Fact>]
    let ``usability tells EVERY-dead from SOME-dead — the distinction lint renders (#945, #646)`` () =
        // THE DISTINCTION LIVES IN THE RULE, not in the caller that renders it. It used to be absent
        // here: `Usability` carried one `Unusable` case, because the two callers #864 migrated reach the
        // same verdict either way. That forced the THIRD caller — `lint` — to keep deciding the question
        // itself with its own `List.forall`, since it renders a different sentence for each and #646
        // exists because the partial case is the more urgent one. A caller that must re-derive half the
        // answer has not stopped deciding, and its half is exactly what drifts (#864).
        //
        // Both cases are UNUSABLE. The split is about which sentence is true, never about the verdict —
        // which is why `Schedulability` and `Lanes` collapse it at their own call sites.
        Assert.Equal(
            TouchSet.AllUnmatchable [ "**/x"; "**/y" ],
            TouchSet.usability (Declared [ Unmatchable "**/x"; Unmatchable "**/y" ])
        )

        // ONE live token is the whole difference between the two cases.
        Assert.Equal(
            TouchSet.SomeUnmatchable [ "**/y" ],
            TouchSet.usability (Declared [ Matchable "src/A/"; Unmatchable "**/y" ])
        )

        // ...and it is the LIVE token that decides it, wherever it sits in the declaration.
        Assert.Equal(
            TouchSet.SomeUnmatchable [ "**/y" ],
            TouchSet.usability (Declared [ Unmatchable "**/y"; Matchable "src/A/" ])
        )

    [<Fact>]
    let ``usability names EVERY dead token, and only the dead ones`` () =
        // The tokens are the remedy: they are what the worker passes back to `widen`. Naming a live one
        // sends them to "fix" a declaration that is correct.
        let ts =
            Declared [ Matchable "src/A/"; Unmatchable "**/x"; Matchable "src/B/"; Unmatchable "a*b" ]

        Assert.Equal(TouchSet.SomeUnmatchable [ "**/x"; "a*b" ], TouchSet.usability ts)

    [<Fact>]
    let ``a touch-set with no tokens is `Usable` — which is NOT a claim that it is schedulable`` () =
        // `Undeclared`, `DeclaredNone` and `Unreadable` have no tokens, so they have no unusable ones.
        // None of the three is schedulable, and each is refused for its OWN reason before any caller
        // asks this — an omission, a decision, and an unread body are three different facts (#496).
        // Answering `Unusable []` here would be the conflation #496 exists to end, and would hand every
        // caller an empty token list to render.
        Assert.Equal(TouchSet.Usable, TouchSet.usability Undeclared)
        Assert.Equal(TouchSet.Usable, TouchSet.usability DeclaredNone)
        Assert.Equal(TouchSet.Usable, TouchSet.usability (Unreadable "boom"))

    [<Fact>]
    let ``the `none` sentinel BESIDE a real path is unusable — it is a contradiction (#863)`` () =
        // `parse` answers `DeclaredNone` when every token is the sentinel, so a `none` that reaches
        // `classify` stands next to real paths: "I touch nothing" and "I touch src/A" at once. It is
        // `Unmatchable`, so the rule refuses the whole declaration rather than reserving a `none`
        // directory that exists nowhere and therefore collides with no one.
        Assert.Equal(TouchSet.SomeUnmatchable [ "none" ], TouchSet.usability (TouchSet.parse "Paths: none src/A/**"))

    // ---- the leading dot: `./` is noise, `.github/` is a DIRECTORY ---------------------------------

    [<Fact>]
    let ``a DOTFILE path keeps its dot — .github is a directory, not a stray prefix`` () =
        // `TrimStart('.', '/')` ate it, so `.github/workflows/**` parsed as `github/workflows/**` — a
        // directory that does not exist. In this org that is most of the fabric (.github/, .agents/,
        // .claude/, .config/), so most touch-sets named nothing.
        //
        // The SHADOW cannot catch this: it compares outcomes, and a consistent renaming of every token
        // preserves the overlap relation, so both engines agree on every verdict while one parses wrong.
        // It turns into a real fail-open at the flip, when the engine's tokens meet actual file paths
        // and a token matching no file conflicts with nothing (#273).
        match TouchSet.parse "x\n\nPaths: .github/workflows/**, .agents/skills/foo/, .claude/skills/" with
        | Declared tokens ->
            let names =
                tokens
                |> List.map (
                    function
                    | Matchable t -> t
                    | Unmatchable t -> t
                )

            Assert.Contains(".github/workflows/**", names)
            Assert.Contains(".agents/skills/foo/", names)
            Assert.Contains(".claude/skills/", names)
        | other -> failwith $"expected Declared, got %A{other}"

    [<Fact>]
    let ``a leading ./ IS stripped — it is noise, and bash strips it too`` () =
        match TouchSet.parse "x\n\nPaths: ./src/Foo/" with
        | Declared [ Matchable t ] -> Assert.Equal("src/Foo/", t)
        | other -> failwith $"expected one Matchable 'src/Foo/', got %A{other}"

    [<Fact>]
    let ``a dotfile touch-set still CONFLICTS with an overlapping one`` () =
        // The point of keeping the dot is that the token names a real place. It must still collide.
        let a = TouchSet.parse "x\n\nPaths: .github/workflows/**"
        let b = TouchSet.parse "y\n\nPaths: .github/workflows/ci.yml"

        Assert.NotEmpty(TouchSet.conflicts a b)

    [<Fact>]
    let ``.github and github are DIFFERENT places and must not collide`` () =
        // The old normalisation collapsed them, so an item touching `.github/` and one touching a
        // hypothetical `github/` were serialised against each other for no reason.
        let a = TouchSet.parse "x\n\nPaths: .github/workflows/"
        let b = TouchSet.parse "y\n\nPaths: github/workflows/"

        Assert.Empty(TouchSet.conflicts a b)

    // ---- #1103 leg 8: `Paths: any` — the schedulable file-less chore -------------------------------
    // The counterpart to `Paths: none`. Both reserve nothing; only `any` is schedulable. They must
    // parse to DIFFERENT touch-sets, or the collapse leg 8 exists to break survives in the parser.

    [<Fact>]
    let ``Paths: any parses to the DeclaredChore sentinel, distinct from none`` () =
        Assert.Equal(DeclaredChore, TouchSet.parse "x\n\nPaths: any")
        Assert.Equal(DeclaredNone, TouchSet.parse "x\n\nPaths: none")
        Assert.NotEqual<TouchSet>(DeclaredNone, DeclaredChore)

    [<Fact>]
    let ``Paths: any is case- and space-insensitive, like the none sentinel`` () =
        Assert.Equal(DeclaredChore, TouchSet.parse "x\n\nPaths:   ANY  ")

    [<Fact>]
    let ``a chore reserves nothing, so it CONFLICTS with nothing`` () =
        let chore = TouchSet.parse "x\n\nPaths: any"
        let real = TouchSet.parse "y\n\nPaths: src/Anything/**"
        Assert.Empty(TouchSet.conflicts chore real)

    [<Fact>]
    let ``mixing the two sentinels is a contradiction — it does NOT parse to either`` () =
        // `none any` cannot be both unschedulable and a schedulable chore; it falls through to
        // Declared, where both reserved words are Unmatchable — caught as an unusable declaration.
        match TouchSet.parse "x\n\nPaths: none any" with
        | Declared tokens ->
            Assert.All(tokens, (fun t -> Assert.True((match t with Unmatchable _ -> true | _ -> false))))
        | other -> failwith $"expected a Declared-with-unmatchable contradiction, got %A{other}"

    [<Fact>]
    let ``any alongside a real path is a contradiction, like none is (#863)`` () =
        match TouchSet.parse "x\n\nPaths: any src/A" with
        | Declared tokens -> Assert.Contains(Unmatchable "any", tokens)
        | other -> failwith $"expected a Declared contradiction, got %A{other}"
