module FS.GG.Coord.GitHub.Tests.GraphQlErrorsFirstTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open FS.GG.Coord.GitHub
open FS.GG.Coord.GitHub.Errors
open FS.GG.Coord.GitHub.Transport

/// A PARTIAL 200 IS NOT A COMPLETE ANSWER (`.github#2534`).
///
/// GitHub reports an exhausted GraphQL budget — and any other partial field failure — as an HTTP **200
/// carrying BOTH `data` and `errors`**. At the transport layer it is byte-indistinguishable from a
/// complete response: same status, same shape, populated `data`. The only thing that separates them is
/// whether the reader ASKS.
///
/// Three reads in `Reads.fs` did not ask. `recentCommentBodies` and `subIssues` accepted a partially
/// populated `nodes` array as a complete one; `prClosingRef` turned a rate-limited response into
/// `Ok None` — the positive assertion *"this PR closes nothing"* — which `verify-paths` renders as
/// `FSGG-PATHS SKIP … ExitGreen`. A failed read wearing a green verdict's clothes: #461's founding shape,
/// one layer up.
///
/// THIS MODULE HOLDS THE TWO HALVES OF THE REPAIR.
///
/// 1. **Behaviour** — each read, driven through the real transport seam with a real partial 200, must
///    report a FAILED READ, and must report a rate limit AS a rate limit (`Budget.ofGraphQlErrors` first)
///    rather than folding it into the generic malformed arm. Each has a controlled counterpart: the same
///    read, the same fixture minus the `errors` key, still parses normally. A refusal that also refuses
///    the good response is not a guard, it is an outage.
///
/// 2. **Structure** — the ordering was a convention enforced by repetition (`Board.GraphQl.decode` was
///    correct; `Scan.fs`, `Done.fs` and `Reads.fs`'s content-edit read each re-implemented it correctly;
///    nothing made a read that SKIPPED it fail). `GraphQl.decode` in `Reads.fs` makes it a function every
///    read must call to reach `data`, and `the errors check precedes every data extraction` below makes
///    the layer-wide property a build failure rather than a fourth comment.
module private Fixtures =

    /// A transport that answers every request with one canned body, at HTTP 200.
    let serving (body: string) =
        Fake.Recorder(fun _ ->
            Ok
                { Status = 200
                  Body = body
                  ETag = None
                  NextLink = None
                  Headers = Map.empty })

    /// GitHub's primary-budget wording, as it arrives inside a 200's `errors` array.
    let [<Literal>] RateLimitMessage = "API rate limit exceeded for installation ID 1234."

    /// GitHub's secondary (abuse-detection) wording — a DIFFERENT fact, and #1666 is the cost of
    /// conflating them.
    let [<Literal>] SecondaryMessage = "You have exceeded a secondary rate limit."

    /// A generic partial-field failure: not a budget, still not a complete answer.
    let [<Literal>] PartialMessage = "Could not resolve to a node with the global id of 'abc'."

    /// `errors`, rendered as GitHub renders it.
    let errorsArray (message: string) = $""""errors":[{{"message":"{message}","path":["repository"]}}]"""

    // ---- the four GraphQL reads in `Reads.fs`, each as a COMPLETE payload -------------------------
    //
    // Every one of these is a genuinely well-formed, fully-populated response. That is the point: the
    // `errors` key is the ONLY difference between the partial fixtures below and these, so a test that
    // passes on both has proved nothing, and one that fails on both has broken the read.

    let [<Literal>] CommentsData =
        """"data":{"repository":{"issue":{"comments":{"nodes":[{"body":"first"},{"body":"second"}]}}}}"""

    let [<Literal>] SubIssuesData =
        """"data":{"repository":{"issue":{"subIssues":{"totalCount":1,"nodes":[{"number":398,"state":"CLOSED","repository":{"nameWithOwner":"FS-GG/FS.GG.SDD"}}]}}}}"""

    /// A PR that genuinely closes ONE issue.
    let [<Literal>] ClosingRefData =
        """"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[{"number":2534,"repository":{"nameWithOwner":"FS-GG/.github"}}]}}}}"""

    /// A PR that genuinely closes NOTHING — the one state `Ok None` is entitled to describe.
    let [<Literal>] ClosingRefEmptyData =
        """"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[]}}}}"""

    let [<Literal>] ContentEditsData =
        """"data":{"repository":{"issueOrPullRequest":{"userContentEdits":{"totalCount":1,"nodes":[{"editedAt":"2026-08-13T10:00:00Z","editor":{"login":"EHotwagner"}}]}}}}"""

    /// The complete payload, alone — a clean 200 with no `errors` key at all.
    let clean (data: string) = "{" + data + "}"

    /// The SAME complete payload, with `errors` beside it — GitHub's partial 200.
    let partial (data: string) (message: string) = "{" + data + "," + errorsArray message + "}"

open Fixtures

// ---- 1. behaviour: each read refuses a partial 200 --------------------------------------------------

[<Fact>]
let ``.github#2534 recentCommentBodies refuses a partial 200 instead of returning the visible comments`` () =
    // BEFORE THE REPAIR this returned `Ok [ "first"; "second" ]` — a truncated comment tail rendered as
    // the whole one. `Client.readDeliveryRouteVerdict` searches that tail for the newest receipt marker
    // and reads "no match" as "no current receipt", so a partial page becomes a refusal to schedule an
    // item that is in fact routed — or, on the other side of the same coin, a stale receipt read as
    // current because the fresher one was in the part that did not arrive.
    let transport = serving (partial CommentsData PartialMessage)

    match Reads.recentCommentBodies transport "FS-GG" ".github" 2534 20 with
    | Error(GraphQlErrors messages) -> Assert.Contains(PartialMessage, messages)
    | other -> failwith $"a partial 200 is a failed read, never a complete comment tail — got %A{other}"

[<Fact>]
let ``.github#2534 recentCommentBodies reports an exhausted budget AS a rate limit`` () =
    // `Budget.ofGraphQlErrors` FIRST. Folding this into the generic malformed arm destroys the one fact
    // the caller needs — that the condition is TEMPORARY — and turns a back-off into a refusal.
    let transport = serving (partial CommentsData RateLimitMessage)

    match Reads.recentCommentBodies transport "FS-GG" ".github" 2534 20 with
    | Error(RateLimited(GraphQlBudget, None)) -> ()
    | other -> failwith $"an exhausted GraphQL budget must be reported as one — got %A{other}"

[<Fact>]
let ``.github#2534 recentCommentBodies still reads a clean 200`` () =
    // THE CONTROLLED COUNTERPART. Same payload, no `errors` key.
    let transport = serving (clean CommentsData)

    match Reads.recentCommentBodies transport "FS-GG" ".github" 2534 20 with
    | Ok bodies -> Assert.Equal<string list>([ "first"; "second" ], bodies)
    | other -> failwith $"a clean 200 must still parse — got %A{other}"

[<Fact>]
let ``.github#2534 subIssues refuses a partial 200 instead of rolling up a truncated graph`` () =
    // BEFORE THE REPAIR this returned `Ok { Total = 1; Children = [ #398 closed ] }`, and the rollup
    // reads "every child is done" off a set already known to be short — #266's shape exactly, and
    // `SubIssueSet`'s own `Total`-vs-`Children` guard cannot see it, because a partial 200 corrupts
    // BOTH numbers consistently.
    let transport = serving (partial SubIssuesData PartialMessage)

    match Reads.subIssues transport "FS-GG" "FS.GG.SDD" 50 with
    | Error(GraphQlErrors messages) -> Assert.Contains(PartialMessage, messages)
    | other -> failwith $"a partial 200 is a failed read, never a complete sub-issue graph — got %A{other}"

[<Fact>]
let ``.github#2534 subIssues reports a SECONDARY limit as a secondary limit, not the GraphQL budget`` () =
    // #1666: ties go to `Secondary`, and the reset is `None` — a secondary limit is account-wide and
    // carries no budget reset. Printing the primary reset here sends the fleet back in at full
    // concurrency the moment it elapses.
    let transport = serving (partial SubIssuesData SecondaryMessage)

    match Reads.subIssues transport "FS-GG" "FS.GG.SDD" 50 with
    | Error(RateLimited(SecondaryLimit(None, None), None)) -> ()
    | other -> failwith $"a secondary limit must not be reported as the GraphQL budget — got %A{other}"

[<Fact>]
let ``.github#2534 subIssues still reads a clean 200`` () =
    let transport = serving (clean SubIssuesData)

    match Reads.subIssues transport "FS-GG" "FS.GG.SDD" 50 with
    | Ok graph ->
        Assert.Equal(1, graph.Total)
        Assert.Equal<string list>([ "FS-GG/FS.GG.SDD#398" ], graph.Children |> List.map _.Ref)
    | other -> failwith $"a clean 200 must still parse — got %A{other}"

[<Fact>]
let ``.github#2534 prClosingRef refuses a partial 200 instead of answering 'this PR closes nothing'`` () =
    // THE SHARPEST OF THE THREE. Before the repair a rate-limited response reached the `nodes` extraction,
    // threw, and was caught into `Ok None` — which `verify-paths` prints as
    // `FSGG-PATHS SKIP … ExitGreen`: a GREEN touch-set verdict on a PR whose closing graph nobody read.
    let transport = serving (partial ClosingRefEmptyData RateLimitMessage)

    match Reads.prClosingRef transport "FS-GG" ".github" 2540 with
    | Error(RateLimited(GraphQlBudget, None)) -> ()
    | other -> failwith $"a rate-limited closing-ref read is not 'closes nothing' — got %A{other}"

[<Fact>]
let ``.github#2534 prClosingRef refuses a partial 200 even when the visible nodes look complete`` () =
    let transport = serving (partial ClosingRefData PartialMessage)

    match Reads.prClosingRef transport "FS-GG" ".github" 2540 with
    | Error(GraphQlErrors messages) -> Assert.Contains(PartialMessage, messages)
    | other -> failwith $"a partial 200 is a failed read, never a closing reference — got %A{other}"

[<Fact>]
let ``.github#2534 prClosingRef Ok None means MEASURED none - an empty connection, cleanly read`` () =
    // THE ONE STATE `Ok None` STILL DESCRIBES, and it must survive the repair intact: a PR that genuinely
    // closes no issue is a real answer, not a failure, and `verify-paths`'s green skip is correct for it.
    let transport = serving (clean ClosingRefEmptyData)

    match Reads.prClosingRef transport "FS-GG" ".github" 2540 with
    | Ok None -> ()
    | other -> failwith $"an empty, cleanly-read connection is a measured none — got %A{other}"

[<Fact>]
let ``.github#2534 prClosingRef still reads a real closing reference`` () =
    let transport = serving (clean ClosingRefData)

    match Reads.prClosingRef transport "FS-GG" ".github" 2540 with
    | Ok(Some ref) ->
        Assert.Equal("FS-GG", ref.Owner)
        Assert.Equal(".github", ref.Repo)
        Assert.Equal(2534, ref.Number)
    | other -> failwith $"a clean 200 must still parse — got %A{other}"

[<Fact>]
let ``.github#2534 prClosingRef refuses a reference it cannot NAME`` () =
    // A node is PRESENT and unreadable. The connection reported a closing reference and we could not name
    // it — the opposite of "it closes nothing", so it may not borrow that answer's value.
    let transport =
        serving """{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[{"number":null,"repository":{}}]}}}}}"""

    match Reads.prClosingRef transport "FS-GG" ".github" 2540 with
    | Error(Malformed(_, detail)) -> Assert.Contains("FAILED READ", detail)
    | other -> failwith $"an unnameable reference is a failed read — got %A{other}"

[<Fact>]
let ``.github#2534 contentEditProvenance keeps refusing a partial 200 through the shared helper`` () =
    // This read was ALREADY correct — `.github#2477` wrote the ordering into it by hand. The repair
    // hoisted that hand-written block into `GraphQl.decode`, so this leg is the regression proof that the
    // hoist preserved it, including the non-object guard `.github#2418` put there.
    let transport = serving (partial ContentEditsData RateLimitMessage)

    match Reads.contentEditProvenance transport "FS-GG" ".github" 2534 with
    | Error(RateLimited(GraphQlBudget, None)) -> ()
    | other -> failwith $"an exhausted budget must survive the hoist as a rate limit — got %A{other}"

[<Fact>]
let ``.github#2534 the non-object guard survives the hoist into GraphQl.decode`` () =
    // `[]`, `"text"`, `7`, `true` — valid JSON, not a GraphQL response of any shape. `TryGetProperty` on
    // these THROWS rather than answering false (`.github#2418`/PR #2419), and the throw is outside every
    // caller's `try`. Now guarded once, for every read in the module rather than the one that wrote it.
    for body in [ "[]"; "\"text\""; "7"; "true"; "null" ] do
        let transport = serving body

        match Reads.contentEditProvenance transport "FS-GG" ".github" 2534 with
        | Error(Malformed(_, detail)) -> Assert.Contains("FAILED READ", detail)
        | other -> failwith $"a non-object root is a failed read, never zero edits — %s{body} gave %A{other}"

        match Reads.subIssues transport "FS-GG" "FS.GG.SDD" 50 with
        | Error(Malformed _) -> ()
        | other -> failwith $"a non-object root must refuse in subIssues too — %s{body} gave %A{other}"

        match Reads.prClosingRef transport "FS-GG" ".github" 2540 with
        | Error(Malformed _) -> ()
        | other -> failwith $"a non-object root must refuse in prClosingRef too — %s{body} gave %A{other}"

[<Fact>]
let ``.github#2534 a response carrying neither data nor errors is a failed read`` () =
    let transport = serving """{"extensions":{"warnings":[]}}"""

    match Reads.subIssues transport "FS-GG" "FS.GG.SDD" 50 with
    | Error(Malformed(_, detail)) -> Assert.Contains("neither `data` nor `errors`", detail)
    | other -> failwith $"a response with no data and no errors is a failed read — got %A{other}"

// ---- 2. structure: the ordering is a build failure, not a comment -----------------------------------

/// THE SCANNER, kept apart from the corpus it scans so it can be pointed at a synthetic violation and
/// PROVED to fire. A gate that has only ever seen compliant input has not been shown to be a gate.
module private Ordering =

    /// One place a GraphQL response's root `data` is opened.
    type Site = { File: string; Line: int; Fn: string; Text: string }

    /// `.GetProperty("data")`, `.GetProperty "data"`, `.TryGetProperty "data"` — every spelling the layer
    /// actually uses, and the space form is not hypothetical: `Scan.fs` uses it.
    let private dataExtraction =
        Regex(@"(Try)?GetProperty\s*\(?\s*""data""", RegexOptions.Compiled)

    /// Either half of the ordering's first step: reading the `errors` array, or handing its messages to
    /// the classifier. `GraphQl.decode` callers do neither — they do not open `data` either, so they are
    /// never sites at all.
    let private errorsInspection =
        Regex(@"(Try)?GetProperty\s*\(?\s*""errors""|ofGraphQlErrors", RegexOptions.Compiled)

    /// A TOP-LEVEL binding in a `module X =` — four spaces, then `let`. Nested `let`s (a `let rec page`
    /// inside a paging read, a lambda's binding) sit deeper and deliberately do NOT reset the enclosing
    /// function: the ordering is a property of the whole read, and `Scan.fs`'s paging loop checks
    /// `errors` in the outer scope of the extraction it guards.
    let private topLevelLet = Regex(@"^    let\s+(?:private\s+|rec\s+|inline\s+)*([^\s(:]+)", RegexOptions.Compiled)

    /// Every site in one file's text, paired with whether its enclosing binding asked about `errors`
    /// FIRST. Whole-line comments are skipped — the corpus is full of prose about this very rule, and
    /// counting a comment as either a violation or an absolution would make the gate about the wrong
    /// artefact. A TRAILING comment is deliberately not stripped: `//` inside a string literal cannot be
    /// told from a comment by a line scanner, and of the two errors, over-reporting a site is the one
    /// that fails closed.
    let scan (file: string) (text: string) : Site list * Site list =
        let lines = text.Replace("\r\n", "\n").Split('\n')
        let mutable fn = "(module level)"
        let mutable errorsSeenInFn = false
        let guarded = ResizeArray<Site>()
        let unguarded = ResizeArray<Site>()

        for i in 0 .. lines.Length - 1 do
            let line = lines[i]
            let trimmed = line.TrimStart()

            let m = topLevelLet.Match line

            if m.Success then
                fn <- m.Groups[1].Value
                errorsSeenInFn <- false

            if not (trimmed.StartsWith "//") then
                if errorsInspection.IsMatch line then
                    errorsSeenInFn <- true

                if dataExtraction.IsMatch line then
                    let site = { File = file; Line = i + 1; Fn = fn; Text = trimmed }
                    if errorsSeenInFn then guarded.Add site else unguarded.Add site

        List.ofSeq guarded, List.ofSeq unguarded

    let private rec_root (dir: DirectoryInfo) =
        let rec walk (d: DirectoryInfo) =
            if isNull (box d) then
                failwith "walked past the filesystem root without finding a .git — cannot locate the layer's sources"
            elif File.Exists(Path.Combine(d.FullName, ".git")) || Directory.Exists(Path.Combine(d.FullName, ".git")) then
                d.FullName
            else
                walk d.Parent

        walk dir

    /// The layer's own sources, on disk. A worktree, not a build output — the property being asserted is
    /// about what is WRITTEN.
    let layerSources () =
        let root = rec_root (DirectoryInfo(AppContext.BaseDirectory))
        let dir = Path.Combine(root, "src", "FS.GG.Coord.GitHub")

        Assert.True(Directory.Exists dir, $"the layer's source directory is not where this gate looks: {dir}")

        Directory.GetFiles(dir, "*.fs")
        |> Array.sort
        |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)

    /// The ONE site in this layer that opens `data` without asking about `errors`, by design.
    ///
    /// `GraphQlEnvelope.tryMeter` is not a payload read: it lifts `data.rateLimit` off ANY 2xx GraphQL body —
    /// including the partial ones this whole gate exists to refuse — and answers `option`, where `None`
    /// means "no meter here" and asserts nothing about completeness. Reading the meter off a rate-limited
    /// response is the point of it. The exemption is NAMED rather than pattern-shaped, and
    /// `no exemption outlives its reason` below fails the moment it stops being needed.
    /// The exception now lives inside the monopoly itself; Budget has no envelope access.
    let exemptions = set [ "GraphQlEnvelope.fs", "tryMeter" ]

[<Fact>]
let ``.github#2534 the errors check precedes every data extraction in the GitHub layer`` () =
    // THIS IS AC3, AND IT IS THE HALF THAT OUTLIVES THE THREE REPAIRS. `Board.GraphQl.decode` was correct
    // from the start; `Scan.fs`, `Done.fs` and `Reads.fs`'s content-edit read each re-derived it
    // correctly; and three reads still drifted, because a convention that nothing can fail is not
    // enforced. Adding a fourth read that opens `data` first now reds the build here.
    let offenders =
        Ordering.layerSources ()
        |> Array.collect (fun (file, text) -> Ordering.scan file text |> snd |> Array.ofList)
        |> Array.filter (fun site -> not (Ordering.exemptions.Contains(site.File, site.Fn)))

    Assert.True(
        offenders.Length = 0,
        "a GraphQL `data` extraction is not preceded by an `errors` inspection in its own function — a "
        + "partial HTTP 200 (how GitHub reports an exhausted budget) would be read as a complete answer "
        + "there. Route it through `GraphQl.decode` (`Reads.fs`) or `Board.GraphQl.decode`:\n"
        + (offenders
           |> Array.map (fun s -> $"  {s.File}:{s.Line} in `{s.Fn}` — {s.Text}")
           |> String.concat "\n")
    )

[<Fact>]
let ``.github#2534 the ordering gate is measuring a non-empty corpus`` () =
    // A GATE THAT SCANS NOTHING PASSES. Renaming `GetProperty`, moving the layer, or pointing the scan at
    // a build output would each empty the corpus silently and leave the assertion above vacuously green —
    // which is the failure mode a source-text gate has and a behavioural one does not. So the corpus's
    // own size is asserted, and the guarded sites are named: these are the re-derivations the repair
    // consolidated, and if they vanish this gate needs re-deriving too, not re-baselining.
    let guarded =
        Ordering.layerSources ()
        |> Array.collect (fun (file, text) -> Ordering.scan file text |> fst |> Array.ofList)

    Assert.True(
        guarded.Length >= 2,
        $"the ordering gate found only {guarded.Length} guarded `data` extractions — the corpus it scans has "
        + "moved or been renamed, and the gate above is passing on an empty set"
    )

    let files = guarded |> Array.map _.File |> Set.ofArray
    Assert.Contains("GraphQl.fs", files)

[<Fact>]
let ``.github#2534 the ordering gate FIRES on a data extraction that skips the errors check`` () =
    // THE GATE'S OWN INVERSION, RUN RATHER THAN ASSERTED. The scanner is pointed at a synthetic read
    // shaped exactly like the three `.github#2534` repaired — `data` opened, `errors` never asked — and
    // must report it. Without this leg, "no offenders" is indistinguishable from "the scanner never
    // detects anything", and a gate that cannot fail is not a gate.
    let drifted =
        String.concat
            "\n"
            [ "module Reads ="
              "    let subIssues (transport: IGitHubTransport) ="
              "        match parse subject response.Body with"
              "        | Ok doc ->"
              "            let nodes = doc.RootElement.GetProperty(\"data\").GetProperty(\"repository\")"
              "            Ok nodes" ]

    let _, unguarded = Ordering.scan "Drifted.fs" drifted
    let site = Assert.Single unguarded
    Assert.Equal("subIssues", site.Fn)
    Assert.Equal(5, site.Line)

[<Fact>]
let ``.github#2534 the ordering gate ACCEPTS the errors-first shape, in both spellings`` () =
    // The counterpart: the same read with the check restored is NOT reported. A gate that flags
    // everything is as useless as one that flags nothing, and it would be repaired by weakening it.
    let repairedByInspection =
        String.concat
            "\n"
            [ "module Reads ="
              "    let subIssues (transport: IGitHubTransport) ="
              "        match doc.RootElement.TryGetProperty \"errors\" with"
              "        | true, errs -> Error(GraphQlErrors [])"
              "        | _ -> Ok(doc.RootElement.GetProperty(\"data\"))" ]

    // `Done.fs`'s spelling: the classifier call, without a literal `"errors"` on the same line.
    let repairedByClassifier =
        String.concat
            "\n"
            [ "module Reads ="
              "    let subIssues (transport: IGitHubTransport) ="
              "        match Budget.ofGraphQlErrors messages with"
              "        | Some limited -> Error limited"
              "        | None -> Ok(doc.RootElement.GetProperty \"data\")" ]

    for source in [ repairedByInspection; repairedByClassifier ] do
        let guarded, unguarded = Ordering.scan "Repaired.fs" source
        Assert.Empty unguarded
        Assert.Single guarded |> ignore

[<Fact>]
let ``.github#2534 a data extraction in a LATER function does not inherit an earlier one's errors check`` () =
    // THE SCANNER'S OWN SHARP EDGE. `errors` seen anywhere in the file would make the gate unfalsifiable
    // in exactly this layer, where every file already contains a correct read: the check is per enclosing
    // top-level binding, and crossing into the next one resets it.
    let twoReads =
        String.concat
            "\n"
            [ "module Reads ="
              "    let good (transport: IGitHubTransport) ="
              "        match doc.RootElement.TryGetProperty \"errors\" with"
              "        | _ -> Ok(doc.RootElement.GetProperty(\"data\"))"
              "    let drifted (transport: IGitHubTransport) ="
              "        Ok(doc.RootElement.GetProperty(\"data\"))" ]

    let guarded, unguarded = Ordering.scan "TwoReads.fs" twoReads
    Assert.Equal("good", (Assert.Single guarded).Fn)
    Assert.Equal("drifted", (Assert.Single unguarded).Fn)

[<Fact>]
let ``.github#2534 no exemption outlives its reason`` () =
    // AN ALLOWLIST IS A LIABILITY THE MOMENT IT STOPS BEING NEEDED — it silently absolves a site that has
    // since drifted back into scope, or a name that no longer exists. Every exemption must still BE an
    // unguarded site in the live sources; a stale one reds here rather than quietly widening the gate.
    let unguarded =
        Ordering.layerSources ()
        |> Array.collect (fun (file, text) -> Ordering.scan file text |> snd |> Array.ofList)
        |> Array.map (fun site -> site.File, site.Fn)
        |> Set.ofArray

    for exemption in Ordering.exemptions do
        Assert.True(
            unguarded.Contains exemption,
            $"the exemption %A{exemption} no longer names an unguarded `data` extraction — delete it "
            + "rather than leaving a hole in the gate"
        )

// ==== .github#2542 — READING `errors` IS HALF THE CONTRACT; CLASSIFYING THEM IS THE OTHER HALF ========
//
// `.github#2534` above made STEP 1 structural: no read reaches `data` without first inspecting `errors`.
// It did not, and could not, reach STEP 2 — hand those messages to `Budget.ofGraphQlErrors` FIRST, so an
// exhausted budget is reported as an exhausted budget. `Scan.freshNodeFacts` is the site that remembered
// step 1 and forgot step 2, and the consequence is an EXIT CODE: `Errors.exitCode` maps
// `RateLimited _ -> 75` (EX_RATE, the fleet-wide back-off signal) but `GraphQlErrors _ -> 1`, and
// `Client.failWith` states in as many words that "a caller that saw a generic 1 would treat a temporary
// condition as permanent".
//
// That site is the claim scan's fresh body/comment-cardinality read, on the hot path of EVERY `take` —
// and a board scan is precisely the operation that exhausts the GraphQL budget. The single most likely
// `errors` payload it will ever see was the one it misclassified.
//
// THE TWO HALVES, AGAIN. Behaviour below drives the real `Scan.snapshot` entry point through the real
// transport seam and executes `Errors.exitCode` on what comes back — the mapping is RUN, never read off
// `Errors.fs`. Structure then makes step 2 a build failure the way `.github#2534` made step 1 one, because
// the cause here is not that an author forgot: it is that the classifier call was still a CONVENTION while
// its sibling clause had become a MECHANISM, and a convention that nothing can fail is not enforced.

module private NodeFacts =

    open FS.GG.Coord.Types

    let private response (body: string) =
        Ok
            { Status = 200
              Body = body
              ETag = None
              NextLink = None
              Headers = Map.empty }

    /// One board candidate carrying a node id — the shape `freshNodeFacts` actually reads for. A row with
    /// `NodeId = None` takes the legacy REST path (#2308) and never reaches this query at all, so the id
    /// is what puts the fixture on the hot path rather than beside it.
    let row (n: int) : Scan.Row =
        { Ref = { Owner = "FS-GG"; Repo = "FS.GG.SDD"; Number = n }
          Title = $"item %d{n}"
          Status = BoardStatus.Ready
          BlockedByRaw = ""
          State = IssueState.Open
          IsPullRequest = false
          PathRepo = "FS.GG.SDD"
          BoardClass = None
          BoardKind = None
          CommentCount = None
          Severity = Unset
          Phase = None
          CreatedAt = None
          SweptBody = None
          NodeId = Some $"I_node_%d{n}" }

    /// The node-facts payload for `row 99`, COMPLETE and well-formed — zero comments, a real touch-set.
    /// Every partial fixture below is this exact body plus an `errors` key, so the `errors` array is the
    /// only difference between a refusal and a clean scan.
    let [<Literal>] CompleteData =
        """"data":{"n0":{"id":"I_node_99","body":"Paths: src/Board/**","comments":{"totalCount":0}}}"""

    /// `errors` with MORE THAN ONE entry — what proves the messages stay a list rather than one
    /// `"; "`-glued string.
    let errorsArrayMany (messages: string list) =
        let entries =
            messages
            |> List.map (fun m -> $"""{{"message":"{m}","path":["repository"]}}""")
            |> String.concat ","

        $""""errors":[{entries}]"""

    let partialMany (messages: string list) =
        "{" + CompleteData + "," + errorsArrayMany messages + "}"

    /// A transport that answers the claim scan's node-facts read and REFUSES everything else loudly.
    ///
    /// The refusal is the assertion: a `freshNodeFacts` failure must short-circuit `snapshot`'s whole
    /// candidate loop, so if any per-candidate REST read still happens the fixture says which one rather
    /// than letting a second, unrelated error masquerade as this one's verdict.
    let servingNodeFacts (body: string) =
        Fake.Recorder(fun (req: Request) ->
            match req.Path, req.Subject with
            | "graphql", "fresh issue body and comment-count facts" -> response body
            | path, subject ->
                Error(
                    Http(
                        500,
                        $"a failed node-facts read must short-circuit the scan — nothing else may be read (saw %s{path} / %s{subject})"
                    )
                ))

    /// The same routes plus the off-board reservation sweep, for the CONTROLLED counterpart: a clean 200
    /// must still assemble a snapshot, or the guard is an outage rather than a guard.
    let servingCleanScan (body: string) =
        Fake.Recorder(fun (req: Request) ->
            match req.Path, req.Subject with
            | "graphql", "fresh issue body and comment-count facts" -> response body
            | path, _ when path.EndsWith "/issues" -> response "[]"
            | path, _ when path.Contains "/pulls" || path.Contains "matching-refs" -> response "[]"
            | path, subject -> Error(Http(500, $"unexpected read %s{path} / %s{subject}")))

    /// **THE PRODUCTION ROUTE, NOT A PRIVATE ONE.** `freshNodeFacts` is private; this is the public entry
    /// `scripts/fsgg-coord take`/`next`/`batch` reach it through (`Scan.fs`'s `snapshot`), so the fixture
    /// exercises the same composition the CLI does.
    let scan (transport: IGitHubTransport) =
        Scan.snapshot transport [ row 99 ] (Some "FS.GG.SDD") false None 120

    /// What `Client.failWith` does with the error — `Errors.exitCode e`, RUN rather than read off
    /// `Errors.fs`. The only thing downstream of this in production is the process `exit` itself.
    let exitCodeOf (result: IoResult<string * Scan.Receipt>) =
        match result with
        | Error e -> Errors.exitCode e
        | Ok _ -> failwith "a partial 200 must not assemble a snapshot at all — there is no exit code to read"

// ---- 3. behaviour: the claim scan's hot-path read classifies what it reads --------------------------

[<Fact>]
let ``.github#2542 the claim scan's node-facts read reports an exhausted GraphQL budget AS a rate limit`` () =
    // BEFORE THE REPAIR this arm built `GraphQlErrors [ "…rate limit exceeded…" ]` with no classifier in
    // front of it, so the one fact the fleet needs — that this is TEMPORARY — was destroyed at the point
    // of construction.
    match NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ RateLimitMessage ])) with
    | Error(RateLimited(GraphQlBudget, None)) -> ()
    | other -> failwith $"an exhausted GraphQL budget on the claim scan must be reported as one — got %A{other}"

[<Fact>]
let ``.github#2542 an exhausted budget on the claim scan's hot path exits EX_RATE 75, not a generic 1`` () =
    // THE ACCEPTANCE CRITERION, AND THE MAPPING IS EXECUTED. `Errors.exitCode` is exactly what
    // `Client.failWith` calls; nothing downstream of it but the process `exit`.
    let observed =
        NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ RateLimitMessage ]))
        |> NodeFacts.exitCodeOf

    Assert.Equal(75, observed)

    // AND THE CODE THE UNCLASSIFIED SHAPE PRODUCED, likewise executed — this is the `1` the old arm
    // returned for the identical payload, and asserting it here is what makes "75, not 1" a measured
    // DIFFERENCE rather than a claim about a table nobody ran.
    Assert.Equal(1, Errors.exitCode (GraphQlErrors [ RateLimitMessage ]))
    Assert.NotEqual(Errors.exitCode (GraphQlErrors [ RateLimitMessage ]), observed)

    // `renderFailureJson` carries this to `--json` callers as the remedy CLASS, so it is part of the
    // repaired answer rather than a detail of it.
    match NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ RateLimitMessage ])) with
    | Error e -> Assert.Equal(Some Primary, Errors.rateLimitKind e)
    | Ok _ -> failwith "a partial 200 must not assemble a snapshot"

[<Fact>]
let ``.github#2542 a rate-limited claim scan is QUEUEABLE, which a generic GraphQlErrors is not`` () =
    // THE SECOND CONSEQUENCE OF THE SAME DISCARD. `Errors.isQueueable` answers `true` for `RateLimited`
    // and `false` for every permanent failure, and #510 is the cost of getting that wrong in either
    // direction. Misclassifying the budget did not only pick the wrong exit code; it moved the failure
    // into the class that may never be retried.
    match NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ RateLimitMessage ])) with
    | Error e ->
        Assert.True(Errors.isQueueable e)
        Assert.False(Errors.isQueueable (GraphQlErrors [ RateLimitMessage ]))
    | Ok _ -> failwith "a partial 200 must not assemble a snapshot"

[<Fact>]
let ``.github#2542 the claim scan tells a SECONDARY limit from the primary budget`` () =
    // #1666, on this site. A secondary limit is account-wide, carries NO reset, and its remedy is "reduce
    // concurrency" — printing the primary's reset sends the fleet back in at full concurrency the moment
    // it elapses. `isRateLimited` cannot make this distinction; `ofGraphQlErrors` is what can.
    let result = NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ SecondaryMessage ]))

    match result with
    | Error(RateLimited(SecondaryLimit(None, None), None)) -> ()
    | other -> failwith $"a secondary limit must not be reported as the GraphQL budget — got %A{other}"

    Assert.Equal(75, NodeFacts.exitCodeOf result)

[<Fact>]
let ``.github#2542 a genuine field error is still GraphQlErrors, and still exits 1`` () =
    // THE CONTROLLED COUNTERPART FOR THE CLASSIFIER. If every `errors` payload became a rate limit the
    // repair would be a different bug — a permanent failure retried forever — so the arm that must NOT
    // move is asserted beside the one that must.
    let result = NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ PartialMessage ]))

    match result with
    | Error(GraphQlErrors messages) -> Assert.Equal<string list>([ PartialMessage ], messages)
    | other -> failwith $"a genuine field error is not a rate limit — got %A{other}"

    Assert.Equal(1, NodeFacts.exitCodeOf result)

[<Fact>]
let ``.github#2542 the messages stay a LIST, not one glued string`` () =
    // THE SECOND DEFECT IN THE SAME EIGHT LINES. This arm `String.concat "; "`-ed the array into a
    // SINGLE-element list where every sibling site carries the array, so a consumer inspecting individual
    // messages saw one glued entry. Two errors in, two messages out — the glued shape gives exactly one
    // element and fails here.
    let other = "Field 'nope' doesn't exist on type 'Issue'."

    match NodeFacts.scan (NodeFacts.servingNodeFacts (NodeFacts.partialMany [ PartialMessage; other ])) with
    | Error(GraphQlErrors messages) -> Assert.Equal<string list>([ PartialMessage; other ], messages)
    | result -> failwith $"two GraphQL errors are two messages — got %A{result}"

[<Fact>]
let ``.github#2542 a non-object 200 at the node-facts read is a typed refusal, not an unhandled crash`` () =
    // FOUND WHILE REPAIRING THE CLASSIFIER, IN THE SAME EIGHT LINES, AND IT IS THE SAME ROOT CAUSE: this
    // site re-derived the shared GraphQL contract from memory and missed a SECOND clause.
    //
    // `TryGetProperty` on anything but an object THROWS `InvalidOperationException` rather than answering
    // `false` (`.github#2418`), and `freshNodeFacts`'s own `with` catches `JsonException` and
    // `KeyNotFoundException` — NEITHER of those. So the throw escaped `readChunk`, `freshNodeFacts` and
    // `snapshot`, and the compiled engine surfaced it as `DEFECT — InvalidOperationException` with a stack
    // trace at exit 2:
    //
    //     $ fsgg-coord-engine scan --repo FS.GG.SDD     # node-facts read answered `[]`, HTTP 200
    //     fsgg-coord-engine: DEFECT — InvalidOperationException: The requested operation requires an
    //     element of type 'Object', but the target element has type 'Array'.
    //        at FS.GG.Coord.GitHub.Scan.readChunk$cont@865(...) in Scan.fs:line 868
    //
    // `.github#2534` hoisted this guard into `Reads.GraphQl.decode` "where every read gets it". This site
    // never routed through it — which is exactly the consolidation the item names as the open question.
    for body in [ "[]"; "\"text\""; "7"; "true"; "null" ] do
        match NodeFacts.scan (NodeFacts.servingNodeFacts body) with
        | Error(Malformed(_, detail)) -> Assert.Contains("FAILED READ", detail)
        | other -> failwith $"a non-object root is a failed read, never a crash and never facts — %s{body} gave %A{other}"

[<Fact>]
let ``.github#2542 a clean 200 still assembles the claim-scan snapshot`` () =
    // THE OUTAGE CHECK. Same payload, no `errors` key: the scan must still produce the document `decide`
    // consumes, carrying the candidate's declared touch-set.
    match NodeFacts.scan (NodeFacts.servingCleanScan (clean NodeFacts.CompleteData)) with
    | Ok(document, _) -> Assert.Contains("src/Board/**", document)
    | other -> failwith $"a clean 200 must still assemble the snapshot — got %A{other}"

// ---- 4. structure: the CLASSIFIER call is a build failure too ---------------------------------------

/// THE SECOND SCANNER, deliberately built as a sibling of `Ordering` rather than folded into it: the two
/// properties fail for different reasons and name different repairs. `Ordering` asks *"was `errors` read
/// before `data`?"*; this one asks *"were the messages CLASSIFIED before `GraphQlErrors` was built?"* —
/// and `Scan.freshNodeFacts` satisfied the first while violating the second, which is the whole reason
/// `.github#2542` exists as a distinct cause.
module private Classification =

    type Site = { File: string; Line: int; Fn: string; Text: string }

    /// `GraphQlErrors` in EXPRESSION position — a value being CONSTRUCTED.
    ///
    /// Three shapes are deliberately not sites, and each is excluded by a stated rule rather than by
    /// exempting the file that contains it (`Errors.fs` holds all three, and exempting it would put the
    /// union's own definition outside the gate forever):
    ///
    ///   * `GraphQlErrors of messages: …` — the union DECLARATION.
    ///   * `GraphQlErrors _`             — a wildcard DESTRUCTURING.
    ///   * anything with `->` LATER ON THE SAME LINE — pattern position (`| GraphQlErrors messages -> …`).
    ///     Position is what separates this from `| None -> Error(GraphQlErrors messages)`, where the only
    ///     arrow is BEFORE the construction.
    ///
    /// Both word boundaries are load-bearing. Without the leading one, `let ofGraphQlErrors (messages …` —
    /// the CLASSIFIER'S OWN DEFINITION — reads as a construction site and the gate is permanently red;
    /// without the trailing one, `GraphQlErrorsFirstTests` in a doc comment does the same.
    let private construction =
        Regex(@"(?<![A-Za-z0-9_])GraphQlErrors(?![A-Za-z0-9_])(?!\s+of\s)(?!\s*_)(?![^\n]*->)", RegexOptions.Compiled)

    /// The classifier having run: either the call itself, or one of the two shared helpers that make it
    /// for you. `Board.setFieldBatch` is the reason the helper form is accepted — `GraphQl.decode` already
    /// classified the rate limit before its partial-apply arm rebuilds a `GraphQlErrors` from the failing
    /// aliases, and flagging it would be a false positive repaired by weakening the gate.
    let private classifier = Regex(@"ofGraphQlErrors|GraphQl.decode", RegexOptions.Compiled)

    let private topLevelLet =
        Regex(@"^    let\s+(?:private\s+|rec\s+|inline\s+)*([^\s(:]+)", RegexOptions.Compiled)

    /// Construction sites in one file's text, split by whether the classifier ran FIRST in their own
    /// enclosing top-level binding.
    ///
    /// THE BINDING LINE ITSELF GRANTS NO ABSOLUTION, and that asymmetry is the point: a function merely
    /// NAMED `GraphQl.decode` would otherwise absolve itself on its own `let` line without ever calling the
    /// classifier. The line is still SCANNED for constructions — skipping it entirely would be fail-open
    /// in the other direction.
    let scan (file: string) (text: string) : Site list * Site list =
        let lines = text.Replace("\r\n", "\n").Split('\n')
        let mutable fn = "(module level)"
        let mutable classifiedInFn = false
        let classified = ResizeArray<Site>()
        let unclassified = ResizeArray<Site>()

        for i in 0 .. lines.Length - 1 do
            let line = lines[i]
            let trimmed = line.TrimStart()
            let m = topLevelLet.Match line

            if m.Success then
                fn <- m.Groups[1].Value
                classifiedInFn <- false

            if not (trimmed.StartsWith "//") then
                if not m.Success && classifier.IsMatch line then
                    classifiedInFn <- true

                if construction.IsMatch line then
                    let site = { File = file; Line = i + 1; Fn = fn; Text = trimmed }
                    if classifiedInFn then classified.Add site else unclassified.Add site

        List.ofSeq classified, List.ofSeq unclassified

    /// NO EXEMPTIONS, and that is a finding rather than an omission: every construction site in this layer
    /// is a read or a write reporting a GraphQL failure, and every one of them can be rate-limited. If a
    /// site ever genuinely needs one, add it here WITH its reason and `.github#2534`'s
    /// `no exemption outlives its reason` discipline — an empty set needs no such upkeep.
    let exemptions : Set<string * string> = Set.empty

[<Fact>]
let ``.github#2542 every GraphQlErrors construction in the GitHub layer is preceded by the classifier`` () =
    let offenders =
        Ordering.layerSources ()
        |> Array.collect (fun (file, text) -> Classification.scan file text |> snd |> Array.ofList)
        |> Array.filter (fun site -> not (Classification.exemptions.Contains(site.File, site.Fn)))

    Assert.True(
        offenders.Length = 0,
        "a `GraphQlErrors` is constructed without `Budget.ofGraphQlErrors` running first in its own "
        + "function — an exhausted GraphQL budget arriving there exits 1 (`permanent`) instead of EX_RATE "
        + "75 (`back off`), and the fleet-wide rate-limit stop never fires. Route the operation through "
        + "the canonical GraphQl adapter before constructing this error:\n"
        + (offenders
           |> Array.map (fun s -> $"  {s.File}:{s.Line} in `{s.Fn}` — {s.Text}")
           |> String.concat "\n")
    )

[<Fact>]
let ``.github#2542 the classification gate is measuring a non-empty corpus`` () =
    // A GATE THAT SCANS NOTHING PASSES. Renaming the union case, moving the layer, or tightening the
    // construction regex past the shapes the layer actually writes would each empty the corpus silently.
    let classified =
        Ordering.layerSources ()
        |> Array.collect (fun (file, text) -> Classification.scan file text |> fst |> Array.ofList)

    Assert.True(
        classified.Length >= 1,
        $"the classification gate found only {classified.Length} classified `GraphQlErrors` constructions — "
        + "the corpus it scans has moved or been renamed, and the gate above is passing on an empty set"
    )

    let files = classified |> Array.map _.File |> Set.ofArray
    Assert.Contains("GraphQl.fs", files)

    // The constructions now live at the single adapter boundary rather than being copied into readers.

[<Fact>]
let ``.github#2542 the classification gate FIRES on the exact pre-repair freshNodeFacts shape`` () =
    // THE GATE'S OWN INVERSION, RUN RATHER THAN ASSERTED, against the code as it actually stood: `errors`
    // read (so `.github#2534`'s gate is satisfied and silent), messages glued, `GraphQlErrors` built with
    // no classifier anywhere in the binding.
    let preRepair =
        String.concat
            "\n"
            [ "module Scan ="
              "    let private freshNodeFacts (transport: IGitHubTransport) (rows: Row list) ="
              "        match doc.RootElement.TryGetProperty \"errors\" with"
              "        | true, errors when errors.GetArrayLength() > 0 ->"
              "            let messages = errors.EnumerateArray() |> Seq.map msg |> String.concat \"; \""
              "            Error(GraphQlErrors [ messages ])"
              "        | _ -> Ok(doc.RootElement.GetProperty \"data\")" ]

    // `.github#2534`'s gate is SILENT on this input — which is precisely why a second gate was needed.
    Assert.Empty(Ordering.scan "PreRepair.fs" preRepair |> snd)

    let classified, unclassified = Classification.scan "PreRepair.fs" preRepair
    Assert.Empty classified
    let site = Assert.Single unclassified
    Assert.Equal("freshNodeFacts", site.Fn)
    Assert.Equal(6, site.Line)

[<Fact>]
let ``.github#2542 the classification gate ACCEPTS the repaired shape, direct and via the helper`` () =
    // The counterpart: a gate that flags everything is as useless as one that flags nothing, and it would
    // be "repaired" by weakening it. Both accepted spellings are exercised.
    let repairedDirect =
        String.concat
            "\n"
            [ "module Scan ="
              "    let private freshNodeFacts (transport: IGitHubTransport) (rows: Row list) ="
              "        let messages = errors.EnumerateArray() |> Seq.map msg |> List.ofSeq"
              "        match Budget.ofGraphQlErrors messages with"
              "        | Some limited -> Error limited"
              "        | None -> Error(GraphQlErrors messages)" ]

    // `Board.setFieldBatch`'s spelling: the shared helper classified upstream, and this arm rebuilds the
    // generic error from the failing aliases knowing the budget was not the cause.
    let repairedViaHelper =
        String.concat
            "\n"
            [ "module Board ="
              "    let setFieldBatch (transport: IGitHubTransport) ="
              "        match GraphQl.decode subject response.Body with"
              "        | Ok _ -> Ok()"
              "        | Error(RateLimited _ as e) -> Error e"
              "        | Error(GraphQlErrors _) -> Error(GraphQlErrors(failedAliases |> List.map snd))" ]

    for source in [ repairedDirect; repairedViaHelper ] do
        let classified, unclassified = Classification.scan "Repaired.fs" source
        Assert.Empty unclassified
        Assert.Single classified |> ignore

[<Fact>]
let ``.github#2542 a function merely NAMED GraphQl.decode does not absolve itself`` () =
    // THE SHARP EDGE OF ACCEPTING A HELPER NAME. The absolution token is a CALL, and a call cannot appear
    // on the binding line that introduces the name. Without this asymmetry, renaming any drifted read to
    // `GraphQl.decode` would silence the gate over it.
    let impostor =
        String.concat
            "\n"
            [ "module Reads ="
              "    let private GraphQl.decode (subject: string) (root: JsonElement) ="
              "        match root.TryGetProperty \"errors\" with"
              "        | true, e -> Error(GraphQlErrors (messages e))"
              "        | _ -> Ok(root.GetProperty \"data\")" ]

    let _, unclassified = Classification.scan "Impostor.fs" impostor
    Assert.Equal("GraphQl.decode", (Assert.Single unclassified).Fn)

[<Fact>]
let ``.github#2542 the helpers the gate TRUSTS actually classify`` () =
    // M2 replaced the two compatibility helpers with one authoritative envelope implementation.
    let graphQl = Ordering.layerSources () |> Array.find (fun (file, _) -> file = "GraphQl.fs") |> snd
    Assert.Contains("let private envelope", graphQl)
    Assert.Contains("Budget.ofGraphQlErrors", graphQl)

[<Fact>]
let ``.github#2542 a construction in a LATER function does not inherit an earlier one's classifier`` () =
    // `Ordering`'s own sharp edge, restated for this property: a classifier seen anywhere in the file
    // would make the gate unfalsifiable in exactly this layer, where every file already contains one
    // correct site. Crossing into the next top-level binding resets it.
    let twoSites =
        String.concat
            "\n"
            [ "module Reads ="
              "    let good (transport: IGitHubTransport) ="
              "        match Budget.ofGraphQlErrors messages with"
              "        | None -> Error(GraphQlErrors messages)"
              "    let drifted (transport: IGitHubTransport) ="
              "        Error(GraphQlErrors messages)" ]

    let classified, unclassified = Classification.scan "TwoSites.fs" twoSites
    Assert.Equal("good", (Assert.Single classified).Fn)
    Assert.Equal("drifted", (Assert.Single unclassified).Fn)

[<Fact>]
let ``.github#2542 the construction regex tells building a GraphQlErrors from matching on one`` () =
    // WITHOUT THIS DISCRIMINATION `Errors.fs` — the union's own definition, its exit-code table and its
    // `explain` arm — would be permanently red, and the gate would be "repaired" by exempting the one file
    // that must never be outside it. Each excluded shape is asserted individually so a regex edit that
    // loses one is named rather than absorbed.
    let notConstructions =
        [ "        | GraphQlErrors of messages: string list", "the union declaration"
          "        | GraphQlErrors _", "a wildcard destructuring"
          "        | Error(GraphQlErrors _) ->", "a wildcard destructuring inside a wrapper"
          "        | GraphQlErrors messages -> \"GraphQL refused the query: \"", "a named destructuring"
          "    let ofGraphQlErrors (messages: string list) =", "the classifier's own definition" ]

    for line, what in notConstructions do
        let source = String.concat "\n" [ "module M ="; "    let f () ="; line ]
        let classified, unclassified = Classification.scan "Shapes.fs" source
        Assert.True(
            List.isEmpty classified && List.isEmpty unclassified,
            $"{what} is not a construction site, but the gate counted it: {line}"
        )

    // ...and the shapes that ARE constructions, in every spelling the layer writes.
    let constructions =
        [ "        | None -> Error(GraphQlErrors messages)"
          "        Error(GraphQlErrors [ messages ])"
          "        Error(GraphQlErrors(failedAliases |> List.map snd))" ]

    for line in constructions do
        let source = String.concat "\n" [ "module M ="; "    let f () ="; line ]
        let _, unclassified = Classification.scan "Shapes.fs" source
        Assert.Single unclassified |> ignore
