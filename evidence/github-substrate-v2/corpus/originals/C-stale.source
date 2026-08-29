#!/usr/bin/env bash
# Fixture for scripts/check-engine-freshness.py — the gate that asks whether the engine's SOURCE has
# outrun the version the fleet can restore (.github#1075, epic #266).
#
# The gate exists because a check that passes when its subject is missing manufactures confidence, and
# because the subject here is invisible to every version comparison in the repo: this engine's
# `<Version>` moves only at RELEASE time, so `version == package-version` is precisely the state the
# bug lives in. The gate counts COMMITS instead. So this fixture spends most of its length on the
# FAILURE legs: it proves the gate goes red when the wire surface has drifted OR an unreleased
# commit closes a defect-class issue, and ERRORS — never "no drift" — when the feed is unreadable,
# the tag is absent, issue metadata is malformed, or a measured path has moved.
#
# Every negative leg asserts the REASON, not just a non-zero exit — the .github#266 vacuous-failure
# defect (SDD#299) was a "must fail" test whose non-zero exit came from a path guard rather than from
# the thing under test. `must_fail` therefore takes a required pattern.
#
# Throwaway git trees under a temp dir, no network (the gate's --fixture flag serves a canned feed).
# Mirrors tests/feed-coherence/run.sh.

set -euo pipefail

# The suite runs the gate by path, which would otherwise litter scripts/__pycache__ into a repo that
# has no .gitignore.
export PYTHONDONTWRITEBYTECODE=1

# `--fixture` is locked to this harness: the gate refuses a canned feed unless this is set, so a
# stray `--fixture` in CI fails rather than silently reporting green. See the gate's docstring.
export FSGG_ENGINE_FIXTURE_OK=1

HERE="$(cd "$(dirname "$0")" && pwd)"
GATE="$HERE/../../scripts/check-engine-freshness.py"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/engine-freshness-fixture.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

pass=0; failcount=0
ok()  { echo "PASS  $1"; pass=$((pass+1)); }
bad() { echo "FAIL  $1"; [ -n "${2:-}" ] && printf '%s\n' "$2" | sed 's/^/    | /'; failcount=$((failcount+1)); }

# A fixture git repo INHERITS ~/.gitconfig unless every relevant knob is pinned (.github#709: a
# global `status.showUntrackedFiles` silently switched a guard off, and the suite stayed green on the
# author's machine while reddening on everyone else's). So identity, the initial branch, GPG signing
# and the pager are all pinned here rather than assumed — a leg that depends on the runner's dotfiles
# is a leg that proves nothing.
# `core.hooksPath` is pinned for the same reason as the rest and is the one most likely to bite: a
# global hooks path with a pre-commit hook would fail every `commit` below, reddening the suite for a
# reason that is not its subject.
git_() { git -c user.name=fixture -c user.email=fixture@example.com -c commit.gpgsign=false \
             -c init.defaultBranch=main -c core.pager=cat -c tag.gpgsign=false \
             -c core.hooksPath=/dev/null "$@"; }

# Build a synthetic engine repo: the three source trees the gate measures, plus the wire-surface file.
# `$1` = repo dir.
make_repo() {
  local r="$1"
  mkdir -p "$r"
  git_ -C "$r" init -q
  # EVERY tree named in the gate's ENGINE_SOURCE must exist here, because the gate asserts each one
  # exists at HEAD rather than silently measuring a path that has moved (.github#2725 added the fourth).
  # A fixture missing one does not test the gate leniently — the gate refuses outright, and all 32 legs
  # below fail for a reason that has nothing to do with what they assert.
  mkdir -p "$r/src/FS.GG.Coord.Cli" "$r/src/FS.GG.Coord.Cli.Kernel" \
           "$r/src/FS.GG.Coord.Core" "$r/src/FS.GG.Coord.GitHub"
  echo "module Protocol"  > "$r/src/FS.GG.Coord.Core/Protocol.fs"
  echo "module Client"    > "$r/src/FS.GG.Coord.Cli/Client.fs"
  echo "module Options"   > "$r/src/FS.GG.Coord.Cli.Kernel/Options.fs"
  echo "module Reads"     > "$r/src/FS.GG.Coord.GitHub/Reads.fs"
  echo "unrelated"        > "$r/README.md"
  git_ -C "$r" add -A
  git_ -C "$r" commit -qm "engine 0.3.0"
  git_ -C "$r" tag "coord-engine/v0.3.0"
}

# Append a commit touching $2 (a path under the repo $1), with subject $3.
touch_commit() {
  local r="$1" path="$2" subject="$3"
  echo "// $subject" >> "$r/$path"
  git_ -C "$r" add -A
  git_ -C "$r" commit -qm "$subject"
}

feed() { # $1 = file, $2... = versions
  local f="$1"; shift
  local vs=""
  for v in "$@"; do vs="$vs\"$v\","; done
  printf '{"FS.GG.Coord.Cli": [%s]}' "${vs%,}" > "$f"
}

run() { # $1 = repo, $2 = feed json  -> stdout+stderr, exit code in $rc
  set +e
  out="$(python3 "$GATE" --repo "$1" --fixture "$2" 2>&1)"
  rc=$?
  set -e
}

must_pass() { # $1 = label, $2 = required stdout pattern
  if [ "$rc" -ne 0 ]; then bad "$1 (expected exit 0, got $rc)" "$out"; return; fi
  if ! grep -q -- "$2" <<<"$out"; then bad "$1 (exit 0 but did not say: $2)" "$out"; return; fi
  ok "$1"
}

must_fail() { # $1 = label, $2 = required reason pattern
  if [ "$rc" -eq 0 ]; then bad "$1 (expected non-zero, got 0)" "$out"; return; fi
  if ! grep -q -- "$2" <<<"$out"; then bad "$1 (failed, but not for the stated reason: $2)" "$out"; return; fi
  ok "$1"
}

# --- the machine report (.github#2231). These live up here with the other helpers rather than beside
# the section that introduced them, because the DEFECT-class red in section 4 needs them 120 lines
# earlier than section 12 — and that distance is exactly how the defect leg came to be the one report
# arm nobody asserted.
run_report() { # $1 = repo, $2 = feed json, $3 = report path  -> stdout+stderr in $out, exit in $rc
  set +e
  out="$(python3 "$GATE" --repo "$1" --fixture "$2" --report "$3" 2>&1)"
  rc=$?
  set -e
}

# Reads one top-level scalar out of the report, or prints nothing if it cannot. python3 rather than
# jq: the workflow provisions python and nothing else, so a jq assertion would pass here and be
# unrunnable in CI.
field() { # $1 = report path, $2 = key
  python3 -c 'import json,sys
try:
    print(json.dumps(json.load(open(sys.argv[1]))[sys.argv[2]]))
except Exception:
    pass' "$1" "$2" 2>/dev/null
}

expect_field() { # $1 = label, $2 = report, $3 = key, $4 = expected JSON scalar
  local got; got="$(field "$2" "$3")"
  if [ "$got" = "$4" ]; then ok "$1"; else bad "$1 (expected $3=$4, got '${got:-<absent>}')" "$out"; fi
}

echo "== check-engine-freshness fixture =="

# ---------------------------------------------------------------------------------------------
# 1. GREEN: the feed's newest version is tagged, and nothing has landed since.
# ---------------------------------------------------------------------------------------------
R="$WORK/clean"; make_repo "$R"
F="$WORK/feed-clean.json"; feed "$F" 0.1.0 0.2.0 0.3.0
run "$R" "$F"
must_pass "a tag with no commits after it is CLEAN" "no engine commits since coord-engine/v0.3.0"

# ---------------------------------------------------------------------------------------------
# 2. THE ACCEPTANCE CASE: commits after the tag, touching the wire surface => RED.
# ---------------------------------------------------------------------------------------------
R="$WORK/wire"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Core/Protocol.fs" "protocol: a new take exit code"
F="$WORK/feed-wire.json"; feed "$F" 0.3.0
run "$R" "$F"
must_fail "a wire-surface commit after the tag is RED" "WIRE SURFACE has outrun the feed"
# The message must name the remedy, not merely the fault: a gate that reds without naming the fix is
# one the next worker routes around.
if grep -q 'push the matching coord-engine/v<version> tag' <<<"$out"; then
  ok "the RED names the remedy (bump + tag)"
else
  bad "the RED names the remedy (bump + tag)" "$out"
fi

# ---------------------------------------------------------------------------------------------
# 3. Drift that does NOT touch the wire surface: REPORTED, never red. This is the leg that keeps the
#    gate from being red-by-design between releases — the failure mode that gets a gate ignored.
# ---------------------------------------------------------------------------------------------
R="$WORK/internal"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: an internal refactor"
touch_commit "$R" "src/FS.GG.Coord.GitHub/Reads.fs" "github: another internal change"
F="$WORK/feed-internal.json"; feed "$F" 0.3.0
run "$R" "$F"
must_pass "internal-only drift is GREEN" "none touching the wire surface"
# ...but it must still be VISIBLE. "Below the bar" and "nothing here" must not render identically.
if grep -q 'cli: an internal refactor' <<<"$out" && grep -q '2 unreleased engine commit' <<<"$out"; then
  ok "internal-only drift is still REPORTED in full"
else
  bad "internal-only drift is still REPORTED in full" "$out"
fi

# ---------------------------------------------------------------------------------------------
# 4. CLASS IS THE SECOND RED BAR (#1671). Hardening stays reported/green; an issue whose unfenced
#    declaration says `Class: defect` reds immediately. The commit sha keys the fixture because the
#    live gate asks GitHub for the merged commit's associated PR and structural closing-issue edge —
#    it never guesses from `#123` prose in the subject.
# ---------------------------------------------------------------------------------------------
R="$WORK/hardening"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: harden the parser (#77)"
sha="$(git_ -C "$R" rev-parse HEAD)"
F="$WORK/feed-hardening.json"
printf '{"FS.GG.Coord.Cli":["0.3.0"],"_closingIssues":{"%s":[{"number":77,"body":"Class: hardening"}]}}' \
  "$sha" > "$F"
run "$R" "$F"
must_pass "a hardening-class engine commit stays GREEN" "Reported, not red"

R="$WORK/defect"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: repair a live wrong answer (#88)"
sha="$(git_ -C "$R" rev-parse HEAD)"
F="$WORK/feed-defect.json"
printf '{"FS.GG.Coord.Cli":["0.3.0"],"_closingIssues":{"%s":[{"number":88,"body":"Class: hardening\\n\\nClass: defect"}]}}' \
  "$sha" > "$F"
REP="$WORK/report-defect.json"
run_report "$R" "$F" "$REP"
must_fail "an unreleased DEFECT-class engine commit is RED" "unreleased engine commit(s) close an issue declaring"
if grep -q 'DEFECT .*closes #88' <<<"$out"; then
  ok "the defect RED names the commit and structurally closing issue"
else
  bad "the defect RED names the commit and structurally closing issue" "$out"
fi

# THE REPORT MUST NOT CONTRADICT THE VERDICT IT CARRIES, and this is the only arm where it could:
# the defect red is the one red with an EMPTY wire_drift, so a `red` derived from the wire leg alone
# emits `red: false` on a run the gate exits 1 for, and every other leg in this file stays green
# while it does. Section 12's four terminal states did not include this one.
expect_field "the DEFECT red is reported as red=true, not just exited 1" "$REP" red true
expect_field "...with the defect counted" "$REP" defectCount 1
expect_field "...and NO wire drift, which is what makes this arm the uncovered one" "$REP" wireCount 0
expect_field "...and a release owed as well" "$REP" releaseOwed true

# A `Class: defect` example inside a fence is documentation, not a declaration — the same grammar
# the engine's Class.fromBody uses. This keeps the new red bar from classifying prose examples.
R="$WORK/fenced"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: document a hardening (#99)"
sha="$(git_ -C "$R" rev-parse HEAD)"
F="$WORK/feed-fenced.json"
printf '{"FS.GG.Coord.Cli":["0.3.0"],"_closingIssues":{"%s":[{"number":99,"body":"```\\nClass: defect\\n```\\n\\nClass: hardening"}]}}' \
  "$sha" > "$F"
run "$R" "$F"
must_pass "a fenced defect EXAMPLE does not reclassify a hardening" "Reported, not red"

# An unreadable metadata shape is not "no defect". The gate owns a policy decision based on this
# read, so malformed issue data must fail closed before it can pronounce the drift harmless.
R="$WORK/bad-issues"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: metadata cannot be guessed"
F="$WORK/feed-bad-issues.json"
printf '{"FS.GG.Coord.Cli":["0.3.0"],"_closingIssues":[]}' > "$F"
run "$R" "$F"
must_fail "malformed closing-issue metadata is an ERROR" '_closingIssues` is not an object'

# ---------------------------------------------------------------------------------------------
# 5. A commit OUTSIDE the engine's source trees is not drift at all.
# ---------------------------------------------------------------------------------------------
R="$WORK/outside"; make_repo "$R"
touch_commit "$R" "README.md" "docs: unrelated to the engine"
F="$WORK/feed-outside.json"; feed "$F" 0.3.0
run "$R" "$F"
must_pass "a commit outside the engine trees is not drift" "no engine commits since"

# ---------------------------------------------------------------------------------------------
# 6. A TAG IS NOT A PUBLISH. The comparison point is the FEED's newest, never the newest tag: a tag
#    that was cut but never published must not be believed. (The fs-gg-ui-template PHANTOM 0.9.1
#    precedent: three tags cut, zero packages.) Here v0.4.0 is tagged but the feed still serves
#    0.3.0 — the gate must measure from v0.3.0 and SEE the drift, not from v0.4.0 and report clean.
# ---------------------------------------------------------------------------------------------
R="$WORK/phantom"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Core/Protocol.fs" "protocol: shipped in the phantom tag"
git_ -C "$R" tag "coord-engine/v0.4.0"
F="$WORK/feed-phantom.json"; feed "$F" 0.3.0          # 0.4.0 tagged, never published
run "$R" "$F"
must_fail "a PHANTOM tag is not believed — drift is measured from the FEED" "since coord-engine/v0.3.0"

# ---------------------------------------------------------------------------------------------
# 7. FAIL CLOSED — the feed's newest version has no tag. "I cannot name the released commit" is an
#    ERROR, never "no drift".
# ---------------------------------------------------------------------------------------------
R="$WORK/notag"; make_repo "$R"
F="$WORK/feed-notag.json"; feed "$F" 0.9.9
run "$R" "$F"
must_fail "an untagged feed version is an ERROR, not 'current'" "has no tag"

# ---------------------------------------------------------------------------------------------
# 8. FAIL CLOSED — the feed has no such package / zero versions / only prereleases.
# ---------------------------------------------------------------------------------------------
R="$WORK/feedbad"; make_repo "$R"
printf '{"Some.Other.Package": ["1.0.0"]}' > "$WORK/feed-absent.json"
run "$R" "$WORK/feed-absent.json"
must_fail "a package absent from the feed is an ERROR" "not on the org feed"

printf '{"FS.GG.Coord.Cli": []}' > "$WORK/feed-empty.json"
run "$R" "$WORK/feed-empty.json"
must_fail "a feed serving zero versions is an ERROR" "zero versions"

F="$WORK/feed-pre.json"; feed "$F" 0.4.0-preview.1
run "$R" "$F"
must_fail "a feed with only prereleases is an ERROR" "no stable version"

run "$R" "$WORK/does-not-exist.json"
must_fail "an unreadable fixture is an ERROR" "cannot read fixture"

# ---------------------------------------------------------------------------------------------
# 9. FAIL CLOSED — a measured path has MOVED. A hard-coded path that silently measures nothing is
#    the exact fails-open shape this gate exists to refuse, so its absence must red.
# ---------------------------------------------------------------------------------------------
R="$WORK/moved"; make_repo "$R"
git_ -C "$R" rm -q "src/FS.GG.Coord.Core/Protocol.fs"
git_ -C "$R" commit -qm "protocol: moved elsewhere"
F="$WORK/feed-moved.json"; feed "$F" 0.3.0
run "$R" "$F"
must_fail "a wire-surface file that has moved is an ERROR" "does not exist at"

R="$WORK/notree"; make_repo "$R"
git_ -C "$R" rm -q -r "src/FS.GG.Coord.GitHub"
git_ -C "$R" commit -qm "github: tree removed"
F="$WORK/feed-notree.json"; feed "$F" 0.3.0
run "$R" "$F"
must_fail "a missing engine source tree is an ERROR" "does not exist at"

# ---------------------------------------------------------------------------------------------
# 10. THE FIXTURE HOOK IS LOCKED. A --fixture that works outside this harness is a way to turn the
#    gate into a no-op, which is the defect class above.
# ---------------------------------------------------------------------------------------------
R="$WORK/lock"; make_repo "$R"
F="$WORK/feed-lock.json"; feed "$F" 0.3.0
set +e
out="$(env -u FSGG_ENGINE_FIXTURE_OK python3 "$GATE" --repo "$R" --fixture "$F" 2>&1)"; rc=$?
set -e
must_fail "--fixture is REFUSED without the harness opt-in" "Refusing to run"

# The live path with no token must ERROR rather than skip.
set +e
out="$(env -u GITHUB_TOKEN -u GH_TOKEN python3 "$GATE" --repo "$R" 2>&1)"; rc=$?
set -e
must_fail "a missing token is an ERROR, not a skip" "not skip it"

# ---------------------------------------------------------------------------------------------
# 11. FAIL CLOSED — git itself unreadable.
# ---------------------------------------------------------------------------------------------
mkdir -p "$WORK/notgit"
F="$WORK/feed-notgit.json"; feed "$F" 0.3.0
run "$WORK/notgit" "$F"
# The REASON, not merely a non-zero exit: "failed" would match a path typo in this harness just as
# happily as the thing under test, which is the vacuous-failure defect this file's header cites.
must_fail "a non-repo is an ERROR" "not a git repository"

# ---------------------------------------------------------------------------------------------
# 12. THE MACHINE REPORT (.github#2231). The "reported, not red" arm is a correct severity call with
#    no actor behind it, and twice on 2026-08-04 the owed release reached the board only because an
#    agent happened to mention it. `--report` is the destination interface. These legs exist because
#    the field a consumer branches on — `releaseOwed` — is the one that must be TRUE in exactly the
#    state the gate calls GREEN, which is the least intuitive thing about this whole file.
# ---------------------------------------------------------------------------------------------
# THE RECURRING STATE ITSELF: internal-only drift. Green, correctly — and a release is owed anyway.
R="$WORK/report-owed"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: an internal refactor"
touch_commit "$R" "src/FS.GG.Coord.GitHub/Reads.fs" "github: another internal change"
F="$WORK/feed-report-owed.json"; feed "$F" 0.3.0
REP="$WORK/report-owed.json"
run_report "$R" "$F" "$REP"
must_pass "the report does not disturb the green verdict" "Reported, not red"
expect_field "GREEN drift still reports releaseOwed=true" "$REP" releaseOwed true
expect_field "...and red=false, so the two facts stay separable" "$REP" red false
expect_field "...and the count matches the human output" "$REP" unreleasedCount 2
expect_field "...and the schema is declared" "$REP" schema '"fsgg.engine-freshness/1"'

# The commit list is the payload a filer needs; an empty one with a non-zero count would be a report
# that says a release is owed and cannot say for what.
if [ "$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(len(d["commits"]))' "$REP")" = "2" ]; then
  ok "the report carries one entry per unreleased commit"
else
  bad "the report carries one entry per unreleased commit" "$(cat "$REP")"
fi

# NOTHING OWED must be distinguishable from OWED. A report whose releaseOwed is true whatever the
# tree looks like would satisfy every assertion above and be worthless.
R="$WORK/report-clean"; make_repo "$R"
F="$WORK/feed-report-clean.json"; feed "$F" 0.3.0
REP="$WORK/report-clean.json"
run_report "$R" "$F" "$REP"
must_pass "a clean tree is still clean with --report" "no engine commits since"
expect_field "a clean tree reports releaseOwed=false" "$REP" releaseOwed false
expect_field "...with a zero count" "$REP" unreleasedCount 0

# THE RED PATH MEASURES TOO. An owed release is owed whether or not the wire surface also drifted,
# and the run that goes red is exactly the one whose numbers someone will want.
R="$WORK/report-red"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Core/Protocol.fs" "protocol: a new take exit code"
F="$WORK/feed-report-red.json"; feed "$F" 0.3.0
REP="$WORK/report-red.json"
run_report "$R" "$F" "$REP"
must_fail "wire drift is still RED with --report" "WIRE SURFACE has outrun the feed"
expect_field "the RED path still writes a report" "$REP" red true
expect_field "...and it is owed as well as red" "$REP" releaseOwed true
expect_field "...and the wire commit is counted as such" "$REP" wireCount 1

# FAIL CLOSED — a gate that could not measure writes NO report. "I could not look" must not arrive
# at a consumer as a document saying nothing is owed (epic #266): absence is the only honest
# encoding of no-verdict, so the file must not exist at all.
R="$WORK/report-notag"; make_repo "$R"
F="$WORK/feed-report-notag.json"; feed "$F" 0.9.9
REP="$WORK/report-notag.json"
run_report "$R" "$F" "$REP"
must_fail "an untagged feed version is still an ERROR with --report" "has no tag"
if [ -e "$REP" ]; then
  bad "a gate that could not measure writes NO report" "$(cat "$REP")"
else
  ok "a gate that could not measure writes NO report"
fi

# A DESTINATION THAT CANNOT BE WRITTEN IS NOT A DESTINATION. The measurement succeeded, so the gate
# would otherwise exit 0 while the thing that acts on it got nothing — the same silence, arrived at
# from the other side.
R="$WORK/report-unwritable"; make_repo "$R"
touch_commit "$R" "src/FS.GG.Coord.Cli/Client.fs" "cli: an internal refactor"
F="$WORK/feed-report-unwritable.json"; feed "$F" 0.3.0
run_report "$R" "$F" "$WORK/no-such-dir/report.json"
must_fail "an unwritable --report fails the gate instead of exiting green" "could not write --report"

echo
echo "engine-freshness fixture: $pass passed, $failcount failed"
[ "$failcount" -eq 0 ] || exit 1
