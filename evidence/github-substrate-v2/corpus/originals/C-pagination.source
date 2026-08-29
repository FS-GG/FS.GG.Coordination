#!/usr/bin/env bash
# Fixture for scripts/check-recipe-pagination.py — the `gh api` LIST-read pagination gate (.github#547).
#
# The gate exists because an unpaginated `gh api` collection read returns the first 30 items and
# exits 0: the output does not look truncated, it looks like an answer. So the FAILURE LEGS are the
# point of this fixture. A gate that cannot say NO is the #266 defect it was written to close, and a
# fixture that only ever feeds it clean input would never notice.
#
# Every leg below asserts the EXIT CODE (the gate's contract) and, for findings, that the finding
# names the right file:line. No leg greps the gate's prose for a verdict.
#
# The last leg runs the gate against THIS REPO's real recipes and requires green. Without it, every
# leg above is synthetic and the gate could pass its own tests while the shipped skills truncate.

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
GATE="$REPO_ROOT/scripts/check-recipe-pagination.py"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/recipe-pagination-fixture.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

pass=0; failcount=0
ok()  { echo "PASS  $1"; pass=$((pass+1)); }
bad() { echo "FAIL  $1"; [ -n "${2:-}" ] && printf '%s\n' "$2" | sed 's/^/    | /'; failcount=$((failcount+1)); }

echo "recipe-pagination fixture — work='$WORK'"

# Build a throwaway recipe tree and run the gate over it.
# usage: gate_on <case-name> <markdown-body>   -> sets $RC and $OUT
gate_on() {
  # TWO `local`s, and that is not style: a single `local name="$1" dir="$WORK/$name/..."` expands
  # `$name` BEFORE `local` assigns it, so `dir` would silently become `$WORK//recipes/kit` — the
  # fixture would then write every case into ONE directory and still report PASS. shellcheck SC2318
  # caught exactly that, in this file, in this PR. #648
  local name="$1" body="$2"
  local dir="$WORK/$name/recipes/kit"
  mkdir -p "$dir"
  printf '%s\n' "$body" > "$dir/SKILL.md"
  set +e
  OUT="$(cd "$WORK/$name" && python3 "$GATE" --root . --recipes recipes 2>&1)"
  RC=$?
  set -e
}

# ---------------------------------------------------------------------------------------------
# FAILURE LEGS — each must fire. If any of these goes green the gate is decorative.
# ---------------------------------------------------------------------------------------------

# 1. The live #547 bug: the look-before-you-file sub_issues read.
gate_on unpaginated-subissues '# kit

```sh
gh api repos/FS-GG/<repo>/issues/<parent>/sub_issues --jq '"'"'.[] | "#\(.number)"'"'"'
```'
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'SKILL.md:4:'; then
  ok "unpaginated sub_issues read is a FINDING, named at its line"
else
  bad "unpaginated sub_issues read should exit 1 and name SKILL.md:4 (got rc=$RC)" "$OUT"
fi

# 2. The merge gate: an aggregate over `.check_runs[]`. Truncation here reports pending=0 failed=0
#    while the checks that would have stopped the merge sit on page 2.
gate_on unpaginated-checkruns '# kit

```sh
gh api "repos/FS-GG/<repo>/commits/$SHA/check-runs" \
  --jq '"'"'"pending=\([.check_runs[]|select(.status!="completed")]|length)"'"'"'
```'
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'iterates the response as an array'; then
  ok "unpaginated check-runs aggregate is a FINDING (nested array iteration)"
else
  bad "unpaginated check-runs aggregate should exit 1 (got rc=$RC)" "$OUT"
fi

# 3. The backstop: a collection read with NO jq at all. The jq signal cannot see this one, so if the
#    endpoint heuristic ever regresses this leg is the only thing standing between a worker and a
#    truncated list.
gate_on unpaginated-nojq '# kit

```sh
gh api repos/FS-GG/<repo>/issues/<parent>/sub_issues
```'
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'collection `sub_issues`'; then
  ok "collection read with NO jq is a FINDING (endpoint backstop)"
else
  bad "collection read with no jq should exit 1 via the endpoint backstop (got rc=$RC)" "$OUT"
fi

# ---------------------------------------------------------------------------------------------
# NEGATIVE LEGS — a gate that cries wolf gets ignored, and then it protects nothing.
# ---------------------------------------------------------------------------------------------

# 4. A single-resource GET reaching for a scalar. `pnext-item` §5 does exactly this to get the head
#    SHA; flagging it would be a false positive on correct code.
gate_on single-resource '# kit

```sh
SHA=$(gh api repos/FS-GG/<repo>/pulls/<n> --jq .head.sha)
```'
[ "$RC" = 0 ] && ok "single-resource scalar read is NOT flagged" \
              || bad "single-resource read must not be flagged (got rc=$RC)" "$OUT"

# 5. Writes. `--paginate` is meaningless on a POST, and both `pnext-item` §4 and §5 POST to
#    collection endpoints (`/issues`, `/pulls`) — the endpoint backstop must not fire on them.
gate_on writes '# kit

```sh
gh api -X POST repos/FS-GG/<target>/issues --input - --jq '"'"'"#\(.number)"'"'"'
gh api -X PUT repos/FS-GG/<repo>/pulls/<n>/merge -f merge_method=squash
gh api -X DELETE repos/FS-GG/<repo>/git/refs/heads/item/<n>-<slug>
```'
[ "$RC" = 0 ] && ok "writes to collection endpoints are NOT flagged" \
              || bad "writes must not be flagged (got rc=$RC)" "$OUT"

# 6. Prose. These skills DISCUSS `gh api` at length ("`gh api repos/…` will still open your PR").
#    A gate that flags prose trains workers to ignore it.
gate_on prose '# kit

When the budget is gone, `gh api repos/FS-GG/<repo>/issues/<parent>/sub_issues --jq .[]` still
works, because REST is a separate budget. That sentence is not a command.'
[ "$RC" = 3 ] && ok "prose outside a fence is not audited (and auditing NOTHING is rc=3, not green)" \
              || bad "prose-only recipe should audit nothing and exit 3 (got rc=$RC)" "$OUT"

# 7. The multi-line quoted jq — the shape the fixed recipe actually ships. `--slurp` cannot be
#    combined with `--jq`, so the aggregate is piped into a separate `jq` whose script spans lines
#    inside ONE pair of single quotes, with no backslash. A line-at-a-time reader would see the
#    second line as its own command, miss the `--paginate` on the first, and flag correct code.
gate_on multiline-quoted-jq '# kit

```sh
gh api "repos/FS-GG/<repo>/commits/$SHA/check-runs" --paginate --slurp \
  | jq -r '"'"'[.[].check_runs[]]
           | "checks=\(length) pending=\([.[]|select(.status!="completed")]|length)"'"'"'
```'
[ "$RC" = 0 ] && ok "paginated read with a multi-line quoted jq is NOT flagged" \
              || bad "multi-line quoted jq must not be flagged — logical-line joining is broken (got rc=$RC)" "$OUT"

# 7b. The line number must be the REAL line. A blank line inside the fence is consumed by the
#     logical-line splitter and emitted as nothing; a caller that advances its own counter from the
#     emitted commands loses those lines, and every finding after the first blank line lands on the
#     wrong one. Caught reviewing this gate: it reported :196 and :360 for commands genuinely on
#     lines 197 and 361. A lint that points at the wrong line is one workers stop believing.
gate_on line-number-after-blank '# kit

```sh
# a comment

# a blank line above me, and another below

gh api repos/FS-GG/<repo>/issues/<parent>/sub_issues --jq '"'"'.[]'"'"'
```'
# The command sits on line 8 (1:`# kit` 2:blank 3:fence 4:comment 5:blank 6:comment 7:blank 8:cmd).
# Two blank lines precede it inside the fence, so the pre-fix splitter reported it as line 6.
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'SKILL.md:8:'; then
  ok "the finding names the REAL line (8) across blank lines in the fence"
else
  bad "blank lines in a fence must not shift the reported line; expected SKILL.md:8 (got rc=$RC)" "$OUT"
fi

# 7c. `gh api` POSTs when a body flag is present, with no `-X` anywhere: gh's method default is
#     "GET normally, and POST if any parameters were added". `fsgg-coord child` creates a sub-issue
#     edge with exactly this shape, and intra-repo-parallel-work documents it. Reading it as a GET
#     fires the collection backstop on a WRITE.
gate_on implicit-post '# kit

```sh
gh api "repos/FS-GG/<repo>/issues/<parent>/sub_issues" -F sub_issue_id=$cid
```'
[ "$RC" = 0 ] && ok "an implicit POST (-F, no -X) is a WRITE, not an unpaginated list read" \
              || bad "-F with no -X is a POST; flagging it is a false positive (got rc=$RC)" "$OUT"

# 7d. The remedy, done wrong. `--slurp` cannot be combined with `--jq` — gh refuses the command. It
#     carries `--paginate`, so a naive pagination check passes it, and it then dies in the worker's
#     terminal. The recipe now TELLS people to reach for --slurp, so the gate owns this trap.
gate_on slurp-with-jq '# kit

```sh
gh api "repos/FS-GG/<repo>/commits/$SHA/check-runs" --paginate --slurp --jq '"'"'.[].check_runs[]'"'"'
```'
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'slurp.*cannot be combined'; then
  ok "--slurp with --jq is a FINDING (gh refuses it; the command cannot run)"
else
  bad "--paginate --slurp --jq must be a finding — gh refuses it (got rc=$RC)" "$OUT"
fi

# 7e. A shell COMMENT must not create a finding. These recipes comment their commands heavily — and
#     this one's own prose discusses `--jq '.[]'` — so matching the array-iteration pattern against
#     raw text instead of the tokenized (comment-stripped) command turns correct code red.
gate_on comment-mentions-array '# kit

```sh
SHA=$(gh api repos/FS-GG/<repo>/pulls/<n> --jq .head.sha)   # not a list, unlike --jq '"'"'.[]'"'"'
```'
[ "$RC" = 0 ] && ok "a comment mentioning .[] does not make a scalar read a list read" \
              || bad "a shell comment must not trigger a finding (got rc=$RC)" "$OUT"

# 7g. An APOSTROPHE IN A COMMENT must not open a quote (#673). This is the fail-OPEN leg, and it is
#     the one that matters: the recipes this gate guards are prose-heavy and written in the house
#     voice, which is full of contractions. A splitter that scans `don't` as code sees that
#     apostrophe open a single quote nothing closes, swallows every following line into ONE command,
#     and that glued blob carries the `--paginate` from the FIRST command — so the unpaginated read
#     below it is never audited on its own and the gate says OK. The #266 fail-open, inside the gate
#     built to close #266. The corpus was clean by luck when this was found, not by construction.
#     The FIRST command must be a paginated one, or this leg does not test what it claims: the mask
#     only happens because the glued blob inherits ITS `--paginate`. Glue an unpaginated read onto an
#     unpaginated one and the gate still (accidentally) fires — at the wrong line, but it fires.
gate_on apostrophe-in-comment '# kit

```sh
gh api "repos/FS-GG/<repo>/commits/$SHA/check-runs" --paginate --jq '"'"'.check_runs[].name'"'"'   # don'"'"'t spin
gh api repos/FS-GG/<repo>/issues/<parent>/sub_issues --jq '"'"'.[] | .number'"'"'
```'
# 1:`# kit` 2:blank 3:fence 4:paginated 5:UNpaginated. Pre-fix: 4 and 5 glued into one command that
# carries --paginate, so line 5 was never audited and the gate exited 0 — audited=1, not 2.
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'SKILL.md:5:'; then
  ok "an apostrophe in a comment does not mask the unpaginated read below it (fail-OPEN)"
else
  bad "a contraction in a comment must not swallow the next command; expected a finding at SKILL.md:5 (got rc=$RC)" "$OUT"
fi

# 7h. The same bug's fail-CLOSED face. When the bogus quote opened by `don't` is later closed by the
#     OPENING quote of a genuine multi-line jq, the splitter cuts that command in half mid-string,
#     `shlex` raises `No closing quotation`, and the gate exits 3 blaming the line the mis-split
#     STARTED on — an unrelated command, screenfuls from the apostrophe that caused it. Valid shell,
#     valid markdown, gate red, wrong line.
gate_on apostrophe-then-multiline-jq '# kit

```sh
for _ in $(seq 30); do          # ~10 min ceiling — poll, don'"'"'t spin forever
  sleep 20
done

gh api "repos/FS-GG/<repo>/commits/$SHA/check-runs" --paginate --slurp \
  | jq -r '"'"'[.[].check_runs[]]
           | "checks=\(length)"'"'"'
```'
[ "$RC" = 0 ] && ok "a contraction in a comment does not abort the audit (fail-CLOSED, wrong line)" \
              || bad "an apostrophe in a comment must not break tokenization (got rc=$RC)" "$OUT"

# 7i. A trailing backslash INSIDE a comment is not a line continuation — the shell never sees it.
#     Treating it as one glues the next command onto this one, and since this one carries
#     `--paginate`, the unpaginated read below it is masked. Same fail-open, different trigger.
gate_on backslash-in-comment '# kit

```sh
gh api repos/FS-GG/<repo>/pulls/<pr> --paginate --jq .head.sha   # not a continuation \
gh api repos/FS-GG/<repo>/issues/<parent>/sub_issues --jq '"'"'.[]'"'"'
```'
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'SKILL.md:5:'; then
  ok "a trailing backslash in a comment is not a continuation (fail-OPEN)"
else
  bad "a backslash inside a comment must not glue the next command; expected SKILL.md:5 (got rc=$RC)" "$OUT"
fi

# 7f. The roots come from `.agent-skill-roots`, not a copy inside the gate. If the gate kept its own
#     list, a root added to the declaration would go UNAUDITED while the gate reported green — the
#     #266 fail-open, reintroduced in the thing built to close it. skill-union-assert.sh reads the
#     same declaration for the same reason (#517). Declare an unusual root and prove the gate looks.
mkdir -p "$WORK/roots-decl/.wherever/skills/kit"
printf '# a comment line, and a blank one below\n\n.wherever/skills\n' > "$WORK/roots-decl/.agent-skill-roots"
printf '# kit\n\n```sh\ngh api repos/FS-GG/<r>/issues/<p>/sub_issues --jq %s.[]%s\n```\n' "'" "'" \
  > "$WORK/roots-decl/.wherever/skills/kit/SKILL.md"
set +e
OUT="$(cd "$WORK/roots-decl" && python3 "$GATE" --root . 2>&1)"; RC=$?
set -e
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q '.wherever/skills'; then
  ok "the audited roots come from .agent-skill-roots, not a private copy in the gate"
else
  bad "the gate must read .agent-skill-roots; a declared root went unaudited (got rc=$RC)" "$OUT"
fi

# 7k. #1162: a TRAILING PIPE continues the command onto the next line — `gh api … |` and the `jq` it
#     feeds are ONE pipeline. A line-at-a-time splitter cut them apart, audited `gh api … |` alone,
#     and — because the endpoint here is NOT a known collection (`…/rules/branches/main`) — the only
#     list signal is the `.[]` in the SEVERED jq, so the read passed unflagged. The endpoint is chosen
#     deliberately: a collection endpoint (`sub_issues`) would fire the backstop even when split, so it
#     would not discriminate this fix.
gate_on trailing-pipe '# kit

```sh
gh api repos/FS-GG/<repo>/rules/branches/main |
  jq -r '"'"'.[].type'"'"'
```'
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'SKILL.md:4:'; then
  ok "#1162: a trailing-pipe pipeline is ONE command; its unpaginated list read is flagged at the gh api line"
else
  bad "#1162: a trailing pipe must continue the command; expected a finding at SKILL.md:4 (got rc=$RC)" "$OUT"
fi

# 7m. #1162: docs/coordination is part of the recipe surface. Its *.md carry `gh api` list reads and
#     the sibling gates already scan it, but it is NOT a skill-union root (absent from
#     `.agent-skill-roots`) — which is exactly why it was unaudited here. A tree whose ONLY unpaginated
#     read is under docs/coordination must red, or the surface is decorative. Scanned only in the
#     default mode (no `--recipes`), so this leg drives the gate that way.
surf="$WORK/docs-surface"
mkdir -p "$surf/.claude/skills/x" "$surf/docs/coordination"
printf '.claude/skills\n' > "$surf/.agent-skill-roots"
printf '# clean\n\n```sh\nscripts/fsgg-coord landable <pr> --wait\n```\n' > "$surf/.claude/skills/x/SKILL.md"
printf '# coord doc\n\n```sh\ngh api repos/FS-GG/<r>/issues/<p>/sub_issues --jq %s.[]%s\n```\n' "'" "'" \
  > "$surf/docs/coordination/y.md"
set +e
OUT="$(cd "$surf" && python3 "$GATE" --root . 2>&1)"; RC=$?
set -e
if [ "$RC" = 1 ] && printf '%s' "$OUT" | grep -q 'docs/coordination/y.md'; then
  ok "#1162: docs/coordination is scanned — an unpaginated read there is a FINDING"
else
  bad "#1162: docs/coordination must be part of the audited surface (got rc=$RC)" "$OUT"
fi

# ---------------------------------------------------------------------------------------------
# NO-VERDICT LEGS — "I could not check" must never be mistaken for "I checked, and it's fine" (#266).
# ---------------------------------------------------------------------------------------------

# 7j. A COMMENT that merely mentions `gh api` is not a `gh api` command, and must not be COUNTED as
#     one. The command filter is a regex over the raw text (comments and all); the tokens it then
#     audits are comment-stripped. So a comment about `gh api` — and these recipes are full of them,
#     since they spend paragraphs telling workers to prefer REST — was counted as one more audited
#     command that could never produce a finding. That inflates `audited`, and `audited` is the ONLY
#     thing standing between a broken fence extractor and a green gate (leg 8, #266): one such
#     comment is enough to report `OK — 1 gh api command(s)` over a recipe that has none.
gate_on comment-mentions-gh-api '# kit

```sh
# Prefer `fsgg-coord issues`; a raw gh api read of a list truncates at 30.
scripts/fsgg-coord issues <repo>
```'
[ "$RC" = 3 ] && ok "a comment ABOUT gh api is not an audited command (audit nothing => rc=3)" \
              || bad "a comment mentioning gh api must not be counted as a command (got rc=$RC)" "$OUT"

# 8. Auditing zero commands. If the fence extractor breaks, EVERY recipe reads as clean — the gate
#    would go green across the org while the truncation it exists to catch went right on happening.
gate_on nothing-to-audit '# kit

No commands here at all.'
[ "$RC" = 3 ] && ok "auditing ZERO gh api commands is rc=3 (no verdict), NOT green" \
              || bad "a recipe tree with no gh api commands must exit 3 (got rc=$RC)" "$OUT"

# 9. A recipe dir that is not there. Same rule: cannot read the subject => no verdict.
set +e
OUT="$(cd "$WORK" && python3 "$GATE" --root . --recipes does-not-exist 2>&1)"; RC=$?
set -e
[ "$RC" = 3 ] && ok "a missing recipe dir is rc=3 (no verdict), NOT green" \
              || bad "a missing recipe dir must exit 3 (got rc=$RC)" "$OUT"

# ---------------------------------------------------------------------------------------------
# DOGFOOD — the leg that makes the eight above mean something.
# ---------------------------------------------------------------------------------------------

# 10. The shipped recipes, in every agent-skill root. Without this the gate could pass a fixture
#     suite built entirely from strings in this file while the skills a worker actually copies
#     truncate at 30 — a fixture green on a subject it never read, which is #266 one level up.
set +e
OUT="$(cd "$REPO_ROOT" && python3 "$GATE" --root . 2>&1)"; RC=$?
set -e
if [ "$RC" = 0 ]; then
  ok "this repo's own shipped recipes pass: $(printf '%s' "$OUT" | tail -1)"
else
  bad "this repo's shipped recipes do NOT pass the gate (rc=$RC)" "$OUT"
fi

echo
echo "recipe-pagination: $pass passed, $failcount failed"
[ "$failcount" -eq 0 ] || exit 1
