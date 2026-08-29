module FS.GG.Coord.GitHub.Tests.ConnectionPaginationTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open FS.GG.Coord.GitHub
open FS.GG.Coord.GitHub.Errors
open FS.GG.Coord.GitHub.Transport
open FS.GG.Coord.GitHub.Board

/// **EXCEEDING `N` MUST NOT BE INDISTINGUISHABLE FROM ABSENCE** (`.github#2535`).
///
/// Five connections in this layer were selected with a bare `first: N` and no cursor. Such a read answers
/// the same JSON shape whether the set had N members or ten thousand — `nodes` is N long either way — so
/// *"there were N or fewer"* and *"there were more than N"* arrive byte-identical, and every reader that
/// walked `nodes`, found no match, and answered "absent" was reading the second as the first.
///
/// It is `.github#2534`'s theme one axis over — a partial answer rendered as a complete one — and the two
/// were deliberately NOT consolidated: that one is the ERROR CHANNEL (a response that announced its own
/// incompleteness and was not asked), this one is PAGINATION (a response that was complete as far as it
/// went and never said there was more). Fixing either leaves the other fully live.
///
/// THE SHARP ONE IS `projectItems(first: 20)`. `Board.fs` says of its `None`: *"That is the only path to
/// `item-add`, and it cannot be reached from a failure."* #421 made that true for a read that ERRORED. It
/// was not true for a read that TRUNCATED: an issue sitting on more than twenty boards, ours being the
/// twenty-first, returned a full window that simply did not contain us — and that non-answer licensed
/// adding the row again.
///
/// **EVERY LEG IN THIS MODULE WAS OBSERVED RED BEFORE THE REPAIR.** The gate-inversion evidence is recorded
/// on the PR: each guard was deleted or weakened in turn, this file was run, and the named legs failed.
module private Fixtures =

    let private ok (body: string) =
        Ok
            { Status = 200
              Body = body
              ETag = None
              NextLink = None
              Headers = Map.empty }

    /// A transport that answers every request with one canned body. Safe only where the read under test
    /// makes exactly one call, or where every call legitimately gets the same answer.
    let serving (body: string) = Fake.Recorder(fun _ -> ok body)

    /// Responses in order. A read that calls more times than the test scripted is itself a finding, so it
    /// fails loudly rather than looping.
    let scripted (bodies: string list) =
        let queue = System.Collections.Generic.Queue<string>(bodies)

        Fake.Recorder(fun _ ->
            if queue.Count = 0 then
                failwith "the read called the transport more times than the test scripted"
            else
                ok (queue.Dequeue()))

    let board =
        { Number = 12
          Id = "PVT_coord"
          Owner = "FS-GG"
          Title = "Coordination"
          Fields = Map.ofList [ "Status", { Id = "PVTSSF_status"; Type = Text } ] }

    // ---- project list (the `pageInfo` remedy) ----------------------------------------------------

    /// A project that is NOT the one we are looking for.
    let otherProject (n: int) =
        $"""{{"number":{n},"title":"Other {n}","id":"PVT_o{n}"}}"""

    let coordinationProject = """{"number":12,"title":"Coordination","id":"PVT_coord"}"""

    let projectPage (nodes: string list) (pageInfo: string) =
        let joined = String.concat "," nodes
        $"""{{"data":{{"organization":{{"projectsV2":{{"pageInfo":{pageInfo},"nodes":[{joined}]}}}}}}}}"""

    let lastPage = """{"hasNextPage":false,"endCursor":null}"""
    let moreAt (cursor: string) = $"""{{"hasNextPage":true,"endCursor":"{cursor}"}}"""

    /// A FULL first window of projects, none of them ours — the shape the old code answered `NotFound` on.
    let fiftyOtherProjects = [ for i in 1..50 -> otherProject i ]

    // ---- field map (the `totalCount` remedy) -----------------------------------------------------

    let fieldNode (n: int) =
        $"""{{"id":"PVTF_{n}","name":"Field {n}","dataType":"TEXT"}}"""

    let fieldsPage (nodes: string list) (totalCount: string) =
        let joined = String.concat "," nodes
        $"""{{"data":{{"organization":{{"projectV2":{{"fields":{{{totalCount}"nodes":[{joined}]}}}}}}}}}}"""

    /// `"totalCount":n,` — or nothing at all, for the arm where the connection did not say.
    let total (n: int) = $""""totalCount":{n},"""
    let noTotal = ""

    // ---- project items (the sharp one) -----------------------------------------------------------

    /// An `ItemIdDoc` node: a board item id, and the board it sits on.
    let itemIdNode (projectNumber: int) (id: string) =
        $"""{{"id":"{id}","project":{{"number":{projectNumber}}}}}"""

    let projectItemsPage (nodes: string list) (totalCount: string) =
        let joined = String.concat "," nodes
        $"""{{"data":{{"repository":{{"issue":{{"projectItems":{{{totalCount}"nodes":[{joined}]}}}}}}}}}}"""

    /// An `ItemStatusDoc` node — the same connection, projecting one field value.
    let statusNode (projectNumber: int) (status: string) =
        $"""{{"project":{{"number":{projectNumber}}},"fieldValueByName":{{"name":"{status}"}}}}"""

    /// An `ItemBlockedByDoc` node.
    let blockedByNode (projectNumber: int) (text: string) =
        $"""{{"project":{{"number":{projectNumber}}},"fieldValueByName":{{"text":"{text}"}}}}"""

    /// Twenty items, none of them on OUR board — a full `first: 20` window that answers nothing.
    let twentyForeignItems = [ for i in 1..20 -> itemIdNode (100 + i) $"PVTI_foreign{i}" ]
    let twentyForeignStatuses = [ for i in 1..20 -> statusNode (100 + i) "Ready" ]
    let twentyForeignBlockedBy = [ for i in 1..20 -> blockedByNode (100 + i) "FS-GG/.github#1" ]

    // ---- closing issue references ----------------------------------------------------------------

    let closingRefNode (n: int) =
        $"""{{"number":{n},"repository":{{"nameWithOwner":"FS-GG/.github"}}}}"""

    let closingRefsPage (nodes: string list) (totalCount: string) =
        let joined = String.concat "," nodes
        $"""{{"data":{{"repository":{{"pullRequest":{{"closingIssuesReferences":{{{totalCount}"nodes":[{joined}]}}}}}}}}}}"""

    let fiveClosingRefs = [ for i in 1..5 -> closingRefNode (2500 + i) ]

open Fixtures

// ---- 1. `bootstrap`'s project list: the board at #51 -------------------------------------------------

[<Fact>]
let ``.github#2535 bootstrap FINDS a board that is not on the first page of projects`` () =
    // THE DEFECT, AS A BEHAVIOUR RATHER THAN A REFUSAL. An org whose Coordination board is its 51st project
    // got `NotFound` — printed as *"no Projects v2 board titled 'Coordination' in FS-GG"*, a definite
    // CONFIGURATION verdict, with a remedy ("check `FSGG_COORD_PROJECT`") that is wrong for the actual
    // condition. Nothing anywhere said "I only looked at 50".
    //
    // BEFORE THE REPAIR this returned `Error(NotFound …)`. It is the one leg here that asserts the tool now
    // does MORE, not merely that it refuses more honestly.
    let transport =
        scripted
            [ projectPage fiftyOtherProjects (moreAt "Y3Vyc29yOjUw")
              projectPage [ coordinationProject ] lastPage
              fieldsPage [ fieldNode 1 ] (total 1) ]

    match bootstrap transport "FS-GG" "Coordination" with
    | Ok resolved ->
        Assert.Equal(12, resolved.Number)
        Assert.Equal("PVT_coord", resolved.Id)
    | other -> failwith $"a board on the second page of projects is still the board — got %A{other}"

[<Fact>]
let ``M2 bootstrap completes the connection even when the board is on the first page`` () =
    // A found value is not permission to ignore an announced tail: page-boundary mutation can invalidate
    // the apparent hit. The complete-read boundary therefore drains before returning the board.
    let transport =
        scripted
            [ projectPage [ coordinationProject ] (moreAt "Y3Vyc29yOjE")
              projectPage [] lastPage
              fieldsPage [ fieldNode 1 ] (total 1) ]

    match bootstrap transport "FS-GG" "Coordination" with
    | Ok resolved ->
        Assert.Equal(12, resolved.Number)
        Assert.Equal(3, transport.GraphQlCalls)
    | other -> failwith $"the board is on page one — got %A{other}"

[<Fact>]
let ``.github#2535 a project list with another page but no cursor REFUSES - it is not 'no such board'`` () =
    // FAIL CLOSED, IN THE DIRECTION THAT MATTERS. `NotFound` here is an assertion about the operator's
    // configuration. A walk that could not continue has established nothing about it, and may not borrow
    // that verdict's certainty.
    let transport =
        serving (projectPage fiftyOtherProjects """{"hasNextPage":true,"endCursor":null}""")

    match bootstrap transport "FS-GG" "Coordination" with
    | Error(Malformed(_, detail)) -> Assert.Contains("no usable cursor", detail)
    | Error(NotFound _) -> failwith "a walk that could not continue reported the board ABSENT — this is the whole of .github#2535"
    | other -> failwith $"an uncontinuable walk is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 a project list with NO pageInfo REFUSES rather than answering 'no such board'`` () =
    // COMPLETENESS IS A REQUIRED BOOLEAN, not an optional hint — `Board.nextPage`'s rule, which this walk
    // now shares byte-for-byte with the external-owner walk that #2166 wrote it for. A missing `pageInfo`
    // used to be unrepresentable here because nothing asked for one.
    // A full `first: 50` window has no independent short-page proof, so pageInfo is mandatory here.
    let nodes = String.concat "," fiftyOtherProjects
    let transport = serving $"""{{"data":{{"organization":{{"projectsV2":{{"nodes":[%s{nodes}]}}}}}}}}"""

    match bootstrap transport "FS-GG" "Coordination" with
    | Error(Malformed(_, detail)) -> Assert.Contains("`pageInfo` is missing", detail)
    | Error(NotFound _) -> failwith "a read with no completeness signal reported the board ABSENT"
    | other -> failwith $"a missing `pageInfo` is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 'no such board' survives the repair, once the walk is shown to FINISH`` () =
    // THE ANSWER THE REFUSALS ABOVE MUST NOT SWALLOW. A genuinely absent board is a real, definite,
    // ACTIONABLE answer — a guard that also refuses the good response is not a guard, it is an outage.
    let transport = serving (projectPage [ otherProject 1 ] lastPage)

    match bootstrap transport "FS-GG" "Coordination" with
    | Error(NotFound message) -> Assert.Contains("Coordination", message)
    | other -> failwith $"a completed walk that found nothing is a measured NotFound — got %A{other}"

// ---- 2. the field map: a partial map is refused, not cached ------------------------------------------

[<Fact>]
let ``.github#2535 a TRUNCATED field map is refused rather than cached`` () =
    // AC3. `fields(first: 50)` on a board with 60 fields dropped ten of them, and the all-empty guard
    // beside it cannot see a PARTIAL map — only a wholly unreadable one. So the truncated map was accepted
    // AND CACHED FOR A DAY, and every later write to a missing field failed with `no field named 'X' on
    // this board. Known fields: …`, which recites the truncated list as if it were the board's own: a
    // misleading error, on a correct request, that no amount of retrying could clear.
    let transport =
        scripted
            [ projectPage [ coordinationProject ] lastPage
              fieldsPage [ for i in 1..50 -> fieldNode i ] (total 60) ]

    match bootstrap transport "FS-GG" "Coordination" with
    | Error(Malformed(_, detail)) ->
        Assert.Contains("reports 60 entries and returned only 50", detail)
        // AND IT IS NOT THE ALL-EMPTY REFUSAL WEARING A NEW HAT. The two guards catch different things and
        // neither subsumes the other.
        Assert.DoesNotContain("no fields at all", detail)
    | Ok resolved -> failwith $"a truncated field map was accepted and cached — %d{Map.count resolved.Fields} fields"
    | other -> failwith $"a truncated field map is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 a FULL field window with no totalCount is refused`` () =
    // THE UNKNOWN-COMPLETENESS ARM. Exactly 50 back, and nothing said whether 50 was all of them. A
    // complete set of 50 and a truncated set of 500 are the same bytes, so this is a FAILED READ.
    let transport =
        scripted
            [ projectPage [ coordinationProject ] lastPage
              fieldsPage [ for i in 1..50 -> fieldNode i ] noTotal ]

    match bootstrap transport "FS-GG" "Coordination" with
    | Error(Malformed(_, detail)) -> Assert.Contains("filled its `first: 50` window", detail)
    | other -> failwith $"a full window with no totalCount is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 a COMPLETE field map still bootstraps, and a SHORT window needs no totalCount`` () =
    // THE TWO CONTROLLED COUNTERPARTS, TOGETHER. `totalCount` equal to what arrived is a measured complete
    // read; and a window that came back SHORT is complete on its own evidence, because `first: N` returns
    // `min(N, available)` and a page below N had nothing left to hide. The second is what keeps the whole
    // pre-existing corpus — whose fixtures predate `totalCount` entirely — testing what it always tested.
    let withTotal =
        scripted
            [ projectPage [ coordinationProject ] lastPage
              fieldsPage [ fieldNode 1; fieldNode 2 ] (total 2) ]

    match bootstrap withTotal "FS-GG" "Coordination" with
    | Ok resolved -> Assert.Equal(2, Map.count resolved.Fields)
    | other -> failwith $"a measured-complete field map must bootstrap — got %A{other}"

    let withoutTotal =
        scripted
            [ projectPage [ coordinationProject ] lastPage
              fieldsPage [ fieldNode 1; fieldNode 2 ] noTotal ]

    match bootstrap withoutTotal "FS-GG" "Coordination" with
    | Ok resolved -> Assert.Equal(2, Map.count resolved.Fields)
    | other -> failwith $"a SHORT window is complete on its own evidence — got %A{other}"

// ---- 3. `itemId`: the truncated read that licensed `item-add` ----------------------------------------

[<Fact>]
let ``.github#2535 a TRUNCATED projectItems read is NEVER 'not on the board'`` () =
    // **THE ONE WITH TEETH.** `Board.fs` states of this `None`: *"That is the only path to `item-add`, and
    // it cannot be reached from a failure."* #421 made that true for a read that ERRORED. A read that
    // TRUNCATED reached it anyway: an issue on 25 boards, ours the 21st, returns a full window of twenty
    // that does not contain us — and "we walked its items" was a claim about a WINDOW, not a connection.
    //
    // BEFORE THE REPAIR this returned `Ok None`.
    let transport = serving (projectItemsPage twentyForeignItems (total 25))

    match itemId transport board "FS-GG" ".github" 2535 with
    | Ok None ->
        failwith
            "a TRUNCATED project-item read reported the issue NOT ON THE BOARD — and that answer is what licenses `item-add`"
    | Error(Malformed(_, detail)) -> Assert.Contains("reports 25 entries and returned only 20", detail)
    | other -> failwith $"a truncated lookup is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 a FULL projectItems window with no totalCount is NEVER 'not on the board'`` () =
    let transport = serving (projectItemsPage twentyForeignItems noTotal)

    match itemId transport board "FS-GG" ".github" 2535 with
    | Ok None -> failwith "a full window with no completeness signal reported the issue NOT ON THE BOARD"
    | Error(Malformed(_, detail)) -> Assert.Contains("filled its `first: 20` window", detail)
    | other -> failwith $"an unmeasured full window is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 'not on the board' is still reachable - from a MEASURED read`` () =
    // THE ANSWER `item-add` IS ENTITLED TO. A short window, cleanly read, that does not contain our board:
    // the issue is genuinely not on it. The repair must leave this reachable, or `add` stops working.
    let transport = serving (projectItemsPage [ itemIdNode 999 "PVTI_elsewhere" ] (total 1))

    match itemId transport board "FS-GG" ".github" 2535 with
    | Ok None -> ()
    | other -> failwith $"a measured absence is still an absence — got %A{other}"

[<Fact>]
let ``.github#2535 truncation cannot unmake a HIT`` () =
    // THE COMPLETENESS QUESTION IS ONLY ASKED ON THE MISS, and that is a correctness statement as much as a
    // cost one: if our board is in the window, the tail cannot change the answer. The same rule the
    // external-owner walk follows when it returns on a hit without consulting `pageInfo`.
    let nodes = itemIdNode 12 "PVTI_ours" :: twentyForeignItems

    match itemId (serving (projectItemsPage nodes (total 9999))) board "FS-GG" ".github" 2535 with
    | Ok(Some id) -> Assert.Equal("PVTI_ours", id)
    | other -> failwith $"a hit is a hit, however truncated the tail — got %A{other}"

// ---- 4. the two field-value twins on the same connection ---------------------------------------------

[<Fact>]
let ``.github#2535 a TRUNCATED itemStatus read is not 'not on this board'`` () =
    // `claim` records the column it is about to overwrite so `release` can put it back rather than guess
    // `Ready` (#481). Both `Error` and `Ok None` make it record nothing — but they are not the same fact,
    // and only one of them may be ASSERTED. A definite "there is no column here" manufactured from a window
    // is #266's class, whatever the caller then does with it.
    let transport = serving (projectItemsPage twentyForeignStatuses (total 40))

    match itemStatus transport board "FS-GG" ".github" 2535 with
    | Ok None -> failwith "a truncated Status read reported the issue NOT ON THIS BOARD"
    | Error(Malformed(_, detail)) -> Assert.Contains("reports 40 entries and returned only 20", detail)
    | other -> failwith $"a truncated Status read is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 itemStatus still reads a measured absence and a real column`` () =
    match itemStatus (serving (projectItemsPage [ statusNode 999 "Ready" ] (total 1))) board "FS-GG" ".github" 2535 with
    | Ok None -> ()
    | other -> failwith $"a measured absence is still an absence — got %A{other}"

    match itemStatus (serving (projectItemsPage [ statusNode 12 "In progress" ] (total 1))) board "FS-GG" ".github" 2535 with
    | Ok(Some status) -> Assert.Equal(FS.GG.Coord.Types.BoardStatus.InProgress, status)
    | other -> failwith $"a real column must still be read — got %A{other}"

[<Fact>]
let ``.github#2535 a TRUNCATED itemBlockedBy read is not 'nothing recorded'`` () =
    let transport = serving (projectItemsPage twentyForeignBlockedBy (total 40))

    match itemBlockedBy transport board "FS-GG" ".github" 2535 with
    | Ok None -> failwith "a truncated `Blocked by` read reported the issue NOT ON THIS BOARD"
    | Error(Malformed(_, detail)) -> Assert.Contains("reports 40 entries and returned only 20", detail)
    | other -> failwith $"a truncated `Blocked by` read is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 itemBlockedBy still reads a measured absence and a real edge`` () =
    match itemBlockedBy (serving (projectItemsPage [ blockedByNode 999 "x" ] (total 1))) board "FS-GG" ".github" 2535 with
    | Ok None -> ()
    | other -> failwith $"a measured absence is still an absence — got %A{other}"

    match
        itemBlockedBy (serving (projectItemsPage [ blockedByNode 12 "FS-GG/.github#2534" ] (total 1))) board "FS-GG" ".github" 2535
    with
    | Ok(Some text) -> Assert.Equal("FS-GG/.github#2534", text)
    | other -> failwith $"a real edge must still be read — got %A{other}"

// ---- 5. `prClosingRef` ------------------------------------------------------------------------------

[<Fact>]
let ``.github#2535 a TRUNCATED closing-issue connection refuses rather than naming a head of five`` () =
    // `prClosingRef`'s whole contract is *"the ONE item this PR implements"*, and `Seq.tryHead` takes one of
    // whatever arrived inside `first: 5`. A PR whose closing graph we could only partly see does not have a
    // defensible "one", so the honest answer is a refusal — and the direction matters exactly as it did for
    // `.github#2534`: `verify-paths` renders `Ok None` as `FSGG-PATHS SKIP … ExitGreen`.
    let transport = serving (closingRefsPage fiveClosingRefs (total 7))

    match Reads.prClosingRef transport "FS-GG" ".github" 2542 with
    | Ok(Some ref) -> failwith $"a truncated closing graph named #%d{ref.Number} as THE item this PR implements"
    | Error(Malformed(_, detail)) -> Assert.Contains("reports 7 entries and returned only 5", detail)
    | other -> failwith $"a truncated closing-ref read is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 a FULL closing-issue window with no totalCount refuses`` () =
    let transport = serving (closingRefsPage fiveClosingRefs noTotal)

    match Reads.prClosingRef transport "FS-GG" ".github" 2542 with
    | Error(Malformed(_, detail)) -> Assert.Contains("filled its `first: 5` window", detail)
    | other -> failwith $"an unmeasured full window is a failed read — got %A{other}"

[<Fact>]
let ``.github#2535 prClosingRef still reads a real reference, and a measured 'closes nothing'`` () =
    // BOTH OF `.github#2534`'s SURVIVING ANSWERS, RE-ASSERTED THROUGH THE NEW GUARD. A PR that closes one
    // issue still names it; a PR that genuinely closes nothing is still the measured `Ok None` that
    // `verify-paths`'s green skip is correct for.
    match Reads.prClosingRef (serving (closingRefsPage [ closingRefNode 2535 ] (total 1))) "FS-GG" ".github" 2542 with
    | Ok(Some ref) ->
        Assert.Equal("FS-GG", ref.Owner)
        Assert.Equal(".github", ref.Repo)
        Assert.Equal(2535, ref.Number)
    | other -> failwith $"a clean, complete read must still name the reference — got %A{other}"

    match Reads.prClosingRef (serving (closingRefsPage [] (total 0))) "FS-GG" ".github" 2542 with
    | Ok None -> ()
    | other -> failwith $"an empty, cleanly-read connection is a measured none — got %A{other}"

// ---- 6. the guard and the document must agree about `N` ----------------------------------------------

/// The layer's sources on disk. A worktree, not a build output — the property is about what is WRITTEN.
module private Sources =

    let private repoRoot () =
        let rec walk (d: DirectoryInfo) =
            if isNull (box d) then
                failwith "walked past the filesystem root without finding a .git — cannot locate the layer's sources"
            elif File.Exists(Path.Combine(d.FullName, ".git")) || Directory.Exists(Path.Combine(d.FullName, ".git")) then
                d.FullName
            else
                walk d.Parent

        walk (DirectoryInfo AppContext.BaseDirectory)

    let read (file: string) =
        let path = Path.Combine(repoRoot (), "src", "FS.GG.Coord.GitHub", file)
        Assert.True(File.Exists path, $"the layer source this gate reads is not where it looks: {path}")
        File.ReadAllText path

    /// Every `<connection>(first: N) { totalCount` in one file, as the N it asked for.
    let windowsOf (text: string) (connection: string) =
        Regex.Matches(text, Regex.Escape connection + @"\(first: (\d+)[^)]*\)\s*\{\s*totalCount\b")
        |> Seq.map (fun m -> int m.Groups[1].Value)
        |> List.ofSeq

    /// The value of a `[<Literal>]` window beside it.
    let literal (text: string) (name: string) =
        let m = Regex.Match(text, @"let private " + Regex.Escape name + @"\s*=\s*(\d+)")
        Assert.True(m.Success, $"the window literal `{name}` is gone — the guard below no longer has a value to agree with")
        int m.Groups[1].Value

[<Fact>]
let ``.github#2535 the connection windows in the documents agree with the guards`` () =
    // THE HALF THAT OUTLIVES THE REPAIR. The window a document ASKS for and the window the guard is TOLD
    // about are two facts in two places, and they are silently catastrophic when they disagree: a guard told
    // 20 against a document asking 100 accepts eighty hidden entries as a measured absence — the exact
    // defect, restored, with the guard still in place and still green. Behavioural legs cannot see this,
    // because they supply both halves themselves.
    let boardText = Sources.read "Board.fs"
    let readsText = Sources.read "Reads.fs"
    let doneText = Sources.read "Done.fs"

    let projectItemWindows = Sources.windowsOf boardText "projectItems"

    // NON-VACUITY: four `projectItems` documents, or this gate has stopped measuring the thing it names.
    Assert.Equal<int list>([ 20; 20; 20; 20 ], projectItemWindows)
    Assert.Equal(Sources.literal boardText "ProjectItemsWindow", List.head projectItemWindows)

    let fieldWindows = Sources.windowsOf boardText "fields"
    Assert.Equal<int list>([ 50 ], fieldWindows)
    Assert.Equal(Sources.literal boardText "FieldsWindow", List.head fieldWindows)

    let closingRefWindows = Sources.windowsOf readsText "closingIssuesReferences"
    Assert.Equal<int list>([ 5 ], closingRefWindows)
    Assert.Equal(Sources.literal readsText "ClosingRefWindow", List.head closingRefWindows)

    // .github#2561 extends the same agreement gate to Done.facts's four whole-set reads. Keep each
    // separately named: their deliberately different windows are part of the decision, not coincidence.
    Assert.Equal<int list>([ 10 ], Sources.windowsOf doneText "closedByPullRequestsReferences")
    Assert.Equal(Sources.literal doneText "ClosedByPullRequestsWindow", List.head (Sources.windowsOf doneText "closedByPullRequestsReferences"))
    Assert.Equal<int list>([ 10 ], Sources.windowsOf doneText "closingIssuesReferences")
    Assert.Equal(Sources.literal doneText "ClosingIssuesWindow", List.head (Sources.windowsOf doneText "closingIssuesReferences"))
    Assert.Equal<int list>([ 5 ], Sources.windowsOf doneText "associatedPullRequests")
    Assert.Equal(Sources.literal doneText "AssociatedPullRequestsWindow", List.head (Sources.windowsOf doneText "associatedPullRequests"))
    Assert.Equal<int list>([ 20 ], Sources.windowsOf doneText "projectItems")
    Assert.Equal(Sources.literal doneText "ProjectItemsWindow", List.head (Sources.windowsOf doneText "projectItems"))

    // `timelineItems(last: 10)` is intentionally a recent-event window. It must not be silently converted
    // into a whole-set read while repairing the four `first:N` connections.
    Assert.Matches(@"timelineItems\(last: 10, itemTypes: \[CLOSED_EVENT\]\)", doneText)
    Assert.Empty(Sources.windowsOf doneText "timelineItems")

    // The project list took the OTHER remedy, so it carries a cursor and a `pageInfo` instead of a window.
    Assert.Matches(@"projectsV2\(first: \d+, after: \$cursor\)\s*\{\s*pageInfo\b", boardText)
    Assert.Matches(@"projectsV2\(first: \d+, after: \$cursor\).*?totalCount", boardText)

[<Fact>]
let ``.github#2535 the window-agreement gate FIRES on a document and guard that disagree`` () =
    // THE GATE'S OWN INVERSION, RUN RATHER THAN ASSERTED. Pointed at a synthetic module whose document asks
    // for 100 while its literal says 20 — the silent-catastrophe shape above — the extractors must report
    // the disagreement. Without this leg, "they agree" is indistinguishable from "the regexes match
    // nothing", which is how a source-text gate passes vacuously.
    let drifted =
        String.concat
            "\n"
            [ "module Board ="
              "    [<Literal>]"
              "    let private ItemIdDoc ="
              "        \"query { projectItems(first: 100) { totalCount nodes { id } } }\""
              "    [<Literal>]"
              "    let private ProjectItemsWindow = 20" ]

    Assert.Equal<int list>([ 100 ], Sources.windowsOf drifted "projectItems")
    Assert.Equal(20, Sources.literal drifted "ProjectItemsWindow")
    Assert.NotEqual(Sources.literal drifted "ProjectItemsWindow", List.head (Sources.windowsOf drifted "projectItems"))

[<Fact>]
let ``.github#2535 the window extractor does not credit a connection that dropped totalCount`` () =
    // THE OTHER WAY THE GATE COULD GO BLIND: a document that reverts to a bare `first: N`. The extractor
    // requires `totalCount` to be the connection's FIRST selection, so a reverted document produces no
    // window at all — and the `Assert.Equal([20;20;20], …)` above then fails on the count rather than
    // silently approving a connection with no guard behind it.
    let reverted = "        \"query { projectItems(first: 20) { nodes { id } } }\""
    Assert.Empty(Sources.windowsOf reverted "projectItems")
