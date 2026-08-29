#!/usr/bin/env bash
# Fixture for scripts/check-claim-fence.py and .github/workflows/fsgg-claim-fence.yml
# (.github#2719, slice 4 of the GitHub-native executor fencing design, under .github#1858).
#
# OFFLINE. `gh` is stubbed on PATH and fails LIKE THE REAL ONE — a 404 is a real answer ("this issue
# does not exist"), a 403 is a permission a human must grant, and a rate limit is neither. Same idiom
# as tests/claim-generation/run.sh over a wider subject: this gate reads TWO kinds of marker on the
# item (`fsgg:claim` for check 3, `fsgg:merge-election` for check 4) and resolves a pull request from
# a merge-group ref.
#
# EVERY NEGATIVE LEG ASSERTS THE REASON — a distinctive substring — not merely a non-zero exit.
# tests/feed-coherence/run.sh's note applies: a "must fail" test whose non-zero exit came from the
# wrong guard is a vacuous pass wearing a red badge.
#
# ============================================================================================
# THE TWO THINGS THIS FIXTURE EXISTS TO MAKE NON-VACUOUS
# ============================================================================================
#
# (1) CHECK 4's NEGATIVE LEGS NEED A REAL LOSING ELECTION. "`grant=` is not the lowest" is only
#     reachable in a world where MORE THAN ONE election exists for the opkey. A fixture that asserts
#     it against a single-candidate world has tested nothing: the branch it claims to cover cannot be
#     entered. Section F below constructs two real candidates and asserts BOTH directions against the
#     SAME world — the loser reds and names both ids, and the winner passes. `#266` is the class
#     anchor for a gate whose negative leg cannot be reached, and this row's family already cites it.
#
# (2) A GATE ASSERTING AN ABSENCE IS SATISFIED BY A CHECK THAT MATCHES NOTHING. `.github#2312`'s
#     ordering gate shipped evadable for exactly this reason: a fourth copy of the rule in a different
#     spelling escaped it and all 823 tests still passed. Section G is the repair applied here — a
#     RECOGNITION CORPUS in which EIGHT spellings of an election marker MUST be recognised and SEVEN
#     lookalikes MUST NOT, asserted in both directions over the same code path. A regex that matched
#     nothing would fail all eight positives; a regex that matched everything would fail all seven
#     negatives. THREE of the negatives are lifted from live production text in this repository
#     (the design doc's own `fsgg:op-effect` and `fsgg:pr-authorization` blocks, and a real
#     `fsgg:claim` marker), which is the point: those are the confusable strings that actually exist
#     on real items, not ones invented to be easy to reject.
#
# Section K asserts the fail-closed boundary structurally over the workflow file itself, and does it
# in both directions too: a mutated copy that enforces its verdict must be caught, or the assertion
# is another absence that a broken check would satisfy.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
TOOL="$ROOT/scripts/check-claim-fence.py"
FLOW="$ROOT/.github/workflows/fsgg-claim-fence.yml"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/claim-fence-fixture.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

export PYTHONDONTWRITEBYTECODE=1
unset GITHUB_TOKEN GH_TOKEN FSGG_CLAIM_LEASE_MIN || true
# No retries and no sleeping: the stub's failures are deterministic, and a fixture that waited out
# three exponential backoffs per unreachable leg would spend eight seconds proving nothing.
export FSGG_CLAIM_FENCE_TRIES=1
export FSGG_CLAIM_FENCE_RETRY_DELAY=0

pass=0; failcount=0
ok()  { echo "PASS  $1"; pass=$((pass+1)); }
bad() { echo "FAIL  $1"; [ -n "${2:-}" ] && printf '%s\n' "$2" | sed 's/^/    | /'; failcount=$((failcount+1)); }

# ---------------------------------------------------------------------------------------------
# The `gh` stub. Serves a WORLD directory:
#   $WORLD/comments/<owner>__<repo>__<n>.ndjson   one {id,body,updated_at} object per line — exactly
#                                                  the shape `gh api --paginate --jq '.[] |
#                                                  {id,body,updated_at}'` would print.
#   $WORLD/comments/<slug>.forbidden               403 — the token may not read this item's comments
#   $WORLD/comments/<slug>.unreachable             rate limit / outage — never a verdict
#   $WORLD/pulls/<n>.json                          one {body,ref,sha} object — the post-`--jq` shape
#                                                  the merge-group resolution reads.
#   $WORLD/pulls/<n>.forbidden                     403 on resolving that pull request
#
# An ABSENT comments file is a 404 — this item does not exist. Deliberately different from a PRESENT,
# empty (or marker-free) file, which is a real "this item exists and nobody holds it" answer.
# Collapsing the two would make "wrong item number" and "correct item, currently unclaimed"
# indistinguishable, which is precisely the pair this gate's `Missing` vs. `RELEASED` handling keeps
# apart.
#
# The stub serves the ALREADY-`--jq`'d shape, as tests/claim-generation/run.sh's does. That is a
# deliberate fixture concession, named here so nobody mistakes it for fidelity: it exercises this
# gate's parsing of the lines it receives, not `gh`'s own `--jq`.
# ---------------------------------------------------------------------------------------------
STUB="$WORK/stub"; mkdir -p "$STUB"
cat > "$STUB/gh" <<'STUB'
#!/usr/bin/env bash
set -uo pipefail
path=""
for a in "$@"; do
  case "$a" in
    repos/*) path="$a";;
  esac
done

notfound()  { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
forbidden() { echo "gh: Resource not accessible by integration (HTTP 403)" >&2; exit 1; }
apifail()   { echo "gh: API rate limit exceeded for installation (HTTP 500)" >&2; exit 1; }

base="${path%%\?*}"
rest="${base#repos/}"
case "$rest" in
  */issues/*/comments)
    repo="${rest%%/issues/*}"; num="${rest#*/issues/}"; num="${num%/comments}"
    slug="${repo//\//__}__${num}"
    [ -e "$WORLD/comments/$slug.forbidden" ]   && forbidden
    [ -e "$WORLD/comments/$slug.unreachable" ] && apifail
    f="$WORLD/comments/$slug.ndjson"
    [ -f "$f" ] || notfound
    cat "$f"
    ;;
  */pulls/*)
    num="${rest#*/pulls/}"
    [ -e "$WORLD/pulls/$num.forbidden" ] && forbidden
    f="$WORLD/pulls/$num.json"
    [ -f "$f" ] || notfound
    cat "$f"
    ;;
  *) notfound ;;
esac
STUB
chmod +x "$STUB/gh"

# ---------------------------------------------------------------------------------------------
# The subject under test, fixed once so every leg keys on the same tuple.
# ---------------------------------------------------------------------------------------------
REPO="FS-GG/.github"
NUM=2719
ITEM="$REPO#$NUM"
GEN=5309319124
HEAD=1111111111111111111111111111111111111111
BRANCH="item/2719-claim-fence-gate"

# THE OPKEY, COMPUTED BY AN INDEPENDENT SHA-256 (`sha256sum`), NEVER BY THE GATE ITSELF, AND NEVER
# HARD-CODED. Design doc §3.3: `opkey = sha256(item \n gen \n receiver \n op)`, lowercase hex — the
# same composition `FS.GG.Coord.Core.Operation.compose` performs
# (`String.concat "\n" [item; generation; receiver; wire op]` |> UTF8 |> SHA256 |> lowercase hex).
# Every OK leg below uses this value, so if `compose_opkey` ever computed a different pre-image
# spelling or a different digest, the whole positive half of this fixture goes red at once.
#
# WHAT THIS DOES AND DOES NOT PIN, stated because the obvious claim overreaches it: it pins this
# Python gate against an INDEPENDENT SHA-256 implementation and against the exact four-field
# pre-image. It does NOT load `FS.GG.Coord.Core`, so it cannot by itself certify agreement with the
# F# producer — that half is established by reading `Operation.fs` and by an out-of-band execution
# recorded in this slice's review handoff. Two halves, two kinds of evidence.
OPKEY="$(printf '%s\n%s\n%s\n%s' "$ITEM" "$GEN" "$REPO" "merge" | sha256sum)"
OPKEY="${OPKEY%% *}"
case "$OPKEY" in
  [0-9a-f]*) [ "${#OPKEY}" = 64 ] || { echo "fixture bug: sha256sum did not yield 64 hex chars"; exit 1; } ;;
  *) echo "fixture bug: sha256sum output not hex"; exit 1 ;;
esac
# A second, genuinely different key — the same tuple under a DIFFERENT generation. Used to prove that
# an election bearing another opkey does not enter this one's race (design doc §4.2 consequence 1).
OTHER_OPKEY="$(printf '%s\n%s\n%s\n%s' "$ITEM" "9999999999" "$REPO" "merge" | sha256sum)"
OTHER_OPKEY="${OTHER_OPKEY%% *}"

iso_ago() { date -u -d "-${1} minutes" +%Y-%m-%dT%H:%M:%SZ; }

new_world() {  # -> prints a fresh empty world dir
  local w; w="$(mktemp -d "$WORK/world.XXXXXX")"
  mkdir -p "$w/comments" "$w/pulls"
  printf '%s' "$w"
}

# raw_comment <world> <repo> <num> <id> <ago-minutes>   — body on stdin, appended verbatim.
# Verbatim is the whole point for section G: the corpus needs to control the EXACT bytes of a comment
# body, including leading prose, so that the anchoring rule is what is being tested.
raw_comment() {
  local world="$1" repo="$2" num="$3" id="$4" ago="$5"
  local slug="${repo//\//__}__${num}"
  python3 -c '
import json, sys
print(json.dumps({"id": int(sys.argv[1]), "body": sys.stdin.read(), "updated_at": sys.argv[2]}))
' "$id" "$(iso_ago "$ago")" >> "$world/comments/$slug.ndjson"
}

# claim_comment <world> <id> <ago-minutes> [lease] — one `fsgg:claim` marker on the subject item.
claim_comment() {
  local world="$1" id="$2" ago="$3" lease="${4:-120}"
  printf '<!-- fsgg:claim worker=fixture-worker lease=%s renewed=638000000000000000 -->' "$lease" \
    | raw_comment "$world" "$REPO" "$NUM" "$id" "$ago"
}

# election_comment <world> <id> <opkey> [item] [gen] [receiver] [op] — one `fsgg:merge-election`.
election_comment() {
  local world="$1" id="$2" key="$3"
  local item="${4:-$ITEM}" gen="${5:-$GEN}" recv="${6:-$REPO}" op="${7:-merge}"
  printf '<!-- fsgg:merge-election v=1 opkey=%s item=%s gen=%s receiver=%s op=%s -->' \
    "$key" "$item" "$gen" "$recv" "$op" \
    | raw_comment "$world" "$REPO" "$NUM" "$id" 0
}

# auth <opkey> <grant> [item] [gen] [head] [v] — the PR body's authorization marker, on stdout.
auth() {
  local key="$1" grant="$2"
  local item="${3:-$ITEM}" gen="${4:-$GEN}" head="${5:-$HEAD}" v="${6:-1}"
  printf 'Delivers %s.\n\n<!-- fsgg:pr-authorization v=%s item=%s gen=%s opkey=%s grant=%s head=%s -->\n' \
    "$item" "$v" "$item" "$gen" "$key" "$grant" "$head"
}

# gate <world> <args...> — run the gate against a world; sets OUT and RC.
gate() {
  local world="$1"; shift
  set +e
  OUT="$(env "WORLD=$world" "PATH=$STUB:$PATH" python3 "$TOOL" "$@" 2>&1)"
  RC=$?
  set -e
}

# assert <name> <want-rc> [substring...] — the whole assertion vocabulary of this fixture.
assert() {
  local name="$1" want="$2"; shift 2
  if [ "$RC" != "$want" ]; then
    bad "$name" "want rc=$want, got rc=$RC
$OUT"
    return
  fi
  local needle
  for needle in "$@"; do
    case "$OUT" in
      *"$needle"*) ;;
      *) bad "$name" "rc=$want as expected, but the reason is missing: $needle
$OUT"; return;;
    esac
  done
  ok "$name"
}

body_file() { local f; f="$(mktemp "$WORK/body.XXXXXX")"; cat > "$f"; printf '%s' "$f"; }

echo "=== A. Applicability — the branch, never a paths: filter ==============================="

W="$(new_world)"
B="$(printf 'nothing to see' | body_file)"
gate "$W" --repo "$REPO" --head-ref "renovate/some-bump" --head-sha "$HEAD" --body "$B"
assert "A1 a non-item branch reports a real OK, vacuously" 0 \
  "OK" "is not an \`item/<n>-*\` branch"

gate "$W" --repo "$REPO" --head-ref "main" --head-sha "$HEAD" --body "$B"
assert "A2 the default branch is not an item branch either" 0 "is not an \`item/<n>-*\` branch"

echo "=== B. Check 1 — exactly one well-formed authorization ================================="

W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"

B="$(printf 'A pull request with no authorization at all.\n' | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B1 no marker is check 1, and names the #1858 shape" 1 \
  "[check1]" "no \`fsgg:pr-authorization\` marker" "never called a coordination verb"

B="$( { auth "$OPKEY" 900; auth "$OPKEY" 900; } | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B2 two markers are as bad as none, and say so" 1 \
  "[check1]" "2 \`fsgg:pr-authorization\` markers" "exactly one is required"

B="$(printf '<!-- fsgg:pr-authorization v=1 item=%s gen=%s head=%s -->\n' "$ITEM" "$GEN" "$HEAD" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B3 the four-field claim-generation marker is NOT enough for this gate" 1 \
  "[check1]" "missing required field(s): opkey, grant"

B="$(auth "$OPKEY" 900 "$ITEM" "$GEN" "$HEAD" 2 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B4 an unsupported v= is refused, not tolerated" 1 "[check1]" "names \`v=2\`"

B="$(auth "$OPKEY" 900 ".github#2719" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B5 the board's <repo>#N shorthand is not GitHub grammar (.github#2107)" 1 \
  "[check1]" "is not shaped like \`owner/repo#n\`"

B="$(auth "$OPKEY" 900 "FS-GG/.github#2720" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B6 an authorization for another item cannot ride this branch" 1 \
  "[check1]" "does not match FS-GG/.github#2719"

B="$(auth "not-a-digest" 900 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B7 an opkey that is not 64 lowercase hex is refused before it is compared" 1 \
  "[check1]" "is not a 64-character lowercase hex digest"

B="$(auth "$OPKEY" "not-an-id" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "B8 a grant= that is not a comment id is refused" 1 \
  "[check1]" "is not shaped like an election-marker comment id"

echo "=== C. Check 2 — head= binds the artifact =============================================="

B="$(auth "$OPKEY" 900 "$ITEM" "$GEN" "2222222222222222222222222222222222222222" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "C1 a force-push after authorization is check 2, quoting delivery's own rule" 1 \
  "[check2]" "does not equal this pull request's current head SHA" "no longer at the inspected head"

echo "=== D. Check 5 — the opkey recomputes =================================================="

B="$(auth "$OTHER_OPKEY" 900 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "D1 a well-shaped opkey for a DIFFERENT tuple does not recompute" 1 \
  "[check5]" "does not recompute" "$OPKEY"

# The gate must print the digest it expected, or a worker cannot repair the marker. Asserted above by
# requiring "$OPKEY" — the independently computed value — to appear in the check-5 diagnosis.
ok "D2 the check-5 diagnosis publishes the digest it expected (asserted in D1)"

echo "=== E. Check 3 — the LIVE claim generation ============================================="

W="$(new_world)"; election_comment "$W" 900 "$OPKEY"
B="$(auth "$OPKEY" 900 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "E1 an item with no claim marker at all has no live winner" 1 \
  "[check3]" "has no LIVE \`fsgg:claim\` winner"

W="$(new_world)"; claim_comment "$W" "$GEN" 500 120; election_comment "$W" 900 "$OPKEY"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "E2 a marker whose 120-minute lease lapsed 500 minutes ago is not a live winner" 1 \
  "[check3]" "has no LIVE \`fsgg:claim\` winner"

W="$(new_world)"; claim_comment "$W" 5309999999 5; election_comment "$W" 900 "$OPKEY"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "E3 a claim that moved on is STALE, and says ids are monotone" 1 \
  "[check3]" "currently held under claim generation 5309999999" "monotone"

# The CAS's rule is LOWEST LIVE id, not most recent. A second, higher-id live claim must not become
# the generation — `Reads.winner` sorts before taking the head precisely so two racers cannot compute
# two different winners.
W="$(new_world)"; claim_comment "$W" "$GEN" 5; claim_comment "$W" 5309999999 1
election_comment "$W" 900 "$OPKEY"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "E4 the LOWEST live id wins, not the most recently written" 0 "OK"

# ...and the inverse, so E4 is not a coincidence: naming the higher id reds.
B_HI="$(auth "$OPKEY" 900 "$ITEM" 5309999999 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_HI"
assert "E5 naming the HIGHER live id is stale, which is what makes E4 non-vacuous" 1 \
  "[check5]" "does not recompute"

# E5 lands on check 5 rather than check 3 because the opkey contains the generation — changing `gen=`
# alone changes the key. That is the design's own point (§4.2: "the binding to the claim is carried by
# `gen` INSIDE the opkey"), so the leg is kept and its true diagnosis asserted rather than reshaped.
# The check-3 path proper is E3, where the opkey is consistent and the LIVE claim is what moved.

echo "=== F. Check 4 — a REAL LOSING ELECTION ==============================================="
# THE POINT OF THIS SECTION. "`grant=` is not the lowest" is only reachable when more than one
# election exists for the opkey. Every leg here is asserted against a world that CONTAINS a loser.

W="$(new_world)"; claim_comment "$W" "$GEN" 5
B="$(auth "$OPKEY" 900 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "F1 an authorization naming an election that does not exist is refused" 1 \
  "[check4]" "no \`fsgg:merge-election\` marker" "cannot satisfy by typing"

# TWO REAL CANDIDATES, ONE OPKEY — the losing election this row's route receipt demands.
W="$(new_world)"; claim_comment "$W" "$GEN" 5
election_comment "$W" 900 "$OPKEY"
election_comment "$W" 901 "$OPKEY"

B_LOSER="$(auth "$OPKEY" 901 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_LOSER"
assert "F2 the LOSER of a real two-candidate election is refused, and both ids are named" 1 \
  "[check4]" "NOT the lowest-id merge election" "900, 901" "lowest is 900" \
  "THIS IS WHERE A SECOND EXECUTOR IS REFUSED"

B_WINNER="$(auth "$OPKEY" 900 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "F3 the WINNER of that SAME election passes — the negative leg is reachable, not vacuous" 0 "OK"

# Cross-key non-interference (design doc §4.2 consequence 1): a LOWER-id election bearing a different
# opkey is a different race. If it displaced this one, a worker legitimately electing for another item
# or another tenancy would red an unrelated pull request — the exact defect §4.2 removed.
W="$(new_world)"; claim_comment "$W" "$GEN" 5
election_comment "$W" 800 "$OTHER_OPKEY"
election_comment "$W" 900 "$OPKEY"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "F4 a lower-id election for ANOTHER opkey does not enter this race" 0 "OK"

# ...and the same world proves the filter is a filter rather than a no-op: naming the foreign
# election's id reds, because it is not in this opkey's candidate set at all.
B_FOREIGN="$(auth "$OPKEY" 800 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_FOREIGN"
assert "F5 naming a foreign-opkey election is not the lowest of THIS opkey" 1 \
  "[check4]" "NOT the lowest-id merge election" "lowest is 900"

# The winning election's own recorded facts must agree with the authorization. Without this, an
# election held for one (item, generation, receiver) could authorize a merge for another.
for f in receiver gen item op; do
  W="$(new_world)"; claim_comment "$W" "$GEN" 5
  case "$f" in
    receiver) election_comment "$W" 900 "$OPKEY" "$ITEM" "$GEN" "FS-GG/FS.GG.Net" "merge";;
    gen)      election_comment "$W" 900 "$OPKEY" "$ITEM" 4444444444 "$REPO" "merge";;
    item)     election_comment "$W" 900 "$OPKEY" "FS-GG/.github#2720" "$GEN" "$REPO" "merge";;
    op)       election_comment "$W" 900 "$OPKEY" "$ITEM" "$GEN" "$REPO" "dispatch:kit-materialize";;
  esac
  gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
  assert "F6.$f the winning election recording a different \`$f=\` is refused" 1 \
    "[check4]" "records \`$f=" "Recorded fields disagreeing"
done

W="$(new_world)"; claim_comment "$W" "$GEN" 5
printf '<!-- fsgg:merge-election v=1 opkey=%s -->' "$OPKEY" | raw_comment "$W" "$REPO" "$NUM" 900 0
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "F7 an election that records nothing grounds nothing" 1 \
  "[check4]" "missing required field(s)" "grounds nothing"

echo "=== G. THE RECOGNITION CORPUS — an absence bound in BOTH directions ===================="
# EIGHT spellings must be recognised as an election; SEVEN lookalikes must not. Three of the
# negatives are lifted from live production text in this repository. A regex that matched nothing
# would fail all eight positives; one that matched everything would fail all seven negatives.

# election_world [comment-id] — body on stdin becomes that comment on the item, with a live claim
# beside it. The id defaults to 900, which is what `$B_WINNER`'s `grant=900` names, so a POSITIVE
# grounds the authorization and a false-positive NEGATIVE would show up as a green.
election_world() {
  local id="${1:-900}"
  local w; w="$(new_world)"
  claim_comment "$w" "$GEN" 5
  raw_comment "$w" "$REPO" "$NUM" "$id" 0
  printf '%s' "$w"
}

# --- POSITIVES: each of these IS an election marker and must ground the authorization -------------
declare -a POS_NAME POS_BODY
add_pos() { POS_NAME+=("$1"); POS_BODY+=("$2"); }

add_pos "G+1 canonical single line" \
"<!-- fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge -->"
add_pos "G+2 no space after the comment open" \
"<!--fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge -->"
add_pos "G+3 generous internal whitespace" \
"<!--    fsgg:merge-election   v=1   opkey=$OPKEY   item=$ITEM   gen=$GEN   receiver=$REPO   op=merge   -->"
add_pos "G+4 wrapped across lines, as the design doc's own markers are" \
"<!-- fsgg:merge-election v=1 opkey=$OPKEY
     item=$ITEM gen=$GEN
     receiver=$REPO op=merge -->"
add_pos "G+5 fields in a different order" \
"<!-- fsgg:merge-election op=merge receiver=$REPO gen=$GEN item=$ITEM opkey=$OPKEY v=1 -->"
add_pos "G+6 a tab between the prefix and the first field" \
"$(printf '<!-- fsgg:merge-election\tv=1 opkey=%s item=%s gen=%s receiver=%s op=merge -->' "$OPKEY" "$ITEM" "$GEN" "$REPO")"
add_pos "G+7 prose AFTER the marker, which the anchoring permits" \
"<!-- fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge -->

Elected by \`delivery\` before writing the authorization."
add_pos "G+8 a trailing newline inside the comment" \
"<!-- fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge
-->"

i=0
while [ "$i" -lt "${#POS_NAME[@]}" ]; do
  W="$(printf '%s' "${POS_BODY[$i]}" | election_world)"
  gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
  assert "${POS_NAME[$i]} IS recognised" 0 "OK"
  i=$((i+1))
done

# --- NEGATIVES: none of these is an election marker, and each must leave check 4 unsatisfied -------
declare -a NEG_NAME NEG_BODY NEG_ID
# The third argument is the comment id, and it is 900 for every negative but one. G-7's body IS a real
# `fsgg:claim` marker, so it is correctly parsed as one — and at id 900 it would become the LOWEST live
# claim and move the generation, redding check 3 before check 4 is ever reached. Giving it an id above
# the subject's own generation keeps it out of the CAS's winner position, so the leg tests the thing it
# is named for: that a claim marker does not enter the ELECTION.
add_neg() { NEG_NAME+=("$1"); NEG_BODY+=("$2"); NEG_ID+=("${3:-900}"); }

add_neg "G-1 prose BEFORE the marker forges nothing (the anchoring rule)" \
"We elected this merge: <!-- fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge -->"
add_neg "G-2 a longer prefix is a different marker" \
"<!-- fsgg:merge-election-note v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge -->"
add_neg "G-3 quoted in backticks, with no HTML comment opened" \
"The election is \`fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge\`."
add_neg "G-4 inside a fenced code block, still not an opened comment at position 0" \
"\`\`\`
<!-- fsgg:merge-election v=1 opkey=$OPKEY item=$ITEM gen=$GEN receiver=$REPO op=merge -->
\`\`\`"
# LIFTED FROM LIVE PRODUCTION TEXT (1/3): the effect receipt, exactly as §4.3 of
# docs/reports/2026-08-04-github-native-executor-fencing-design.md spells it. It carries `opkey=` AND
# `grant=`, which makes it the single most confusable sibling in the whole protocol — and §4.3 is
# explicit that it "is audit for the merge path and authority for nothing". A gate that counted a
# receipt as an election would let an executor win a race by posting its own audit trail.
add_neg "G-5 an fsgg:op-effect RECEIPT is audit, never authority (design §4.3, lifted verbatim)" \
"<!-- fsgg:op-effect v=1 opkey=$OPKEY grant=900
     receiver=$REPO op=merge evidence=https://github.com/FS-GG/.github/actions/runs/1 -->"
# LIFTED FROM LIVE PRODUCTION TEXT (2/3): the PR authorization marker, as §6.3 spells it and as
# `scripts/check-claim-generation.py`'s docstring carries it. It also bears `opkey=` and `grant=`.
add_neg "G-6 a pr-authorization marker on the ITEM is not an election (design §6.3, lifted verbatim)" \
"<!-- fsgg:pr-authorization v=1 item=$ITEM gen=$GEN
     opkey=$OPKEY grant=900 head=$HEAD -->"
# LIFTED FROM LIVE PRODUCTION TEXT (3/3): a real `fsgg:claim` marker, the one marker on this item that
# the CAS itself reads. It must never be mistaken for an election in either direction.
add_neg "G-7 an fsgg:claim marker is the CAS's, not the election's" \
"<!-- fsgg:claim worker=wren-ef76 lease=120 renewed=638000000000000000 -->" \
5399999999

i=0
while [ "$i" -lt "${#NEG_NAME[@]}" ]; do
  W="$(printf '%s' "${NEG_BODY[$i]}" | election_world "${NEG_ID[$i]}")"
  gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
  assert "${NEG_NAME[$i]} is NOT recognised" 1 "[check4]" "no \`fsgg:merge-election\` marker"
  i=$((i+1))
done

# The authorization marker gets the same treatment, over its own (deliberately weaker) anchoring: it
# lives in a PR BODY full of prose, so it anchors on the comment OPEN rather than on position 0.
W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"

B="$(printf 'Prose first, then the marker inline: <!-- fsgg:pr-authorization v=1 item=%s gen=%s opkey=%s grant=900 head=%s --> and prose after.\n' "$ITEM" "$GEN" "$OPKEY" "$HEAD" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "G+9 an authorization embedded in prose IS recognised (a PR body is prose)" 0 "OK"

B="$(printf 'Not a marker: `fsgg:pr-authorization v=1 item=%s gen=%s opkey=%s grant=900 head=%s`\n' "$ITEM" "$GEN" "$OPKEY" "$HEAD" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "G-8 a backticked authorization opens no comment and is not one" 1 \
  "[check1]" "no \`fsgg:pr-authorization\` marker"

B="$(printf '<!-- fsgg:pr-authorization-note v=1 item=%s gen=%s opkey=%s grant=900 head=%s -->\n' "$ITEM" "$GEN" "$OPKEY" "$HEAD" | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B"
assert "G-9 fsgg:pr-authorization-note is a different marker" 1 \
  "[check1]" "no \`fsgg:pr-authorization\` marker"

# THE `fsgg:claim` READER GETS THE CORPUS TREATMENT TOO, and this pair was ADDED BECAUSE THE
# AUTHORING-TIME INVERSION SWEEP FOUND IT MISSING. Un-anchoring the claim pattern — dropping its `^`
# AND switching `.match` to `.search` — SURVIVED the fixture as first written, which means a comment
# that merely QUOTES a claim marker in prose could have become this gate's idea of the CAS winner and
# moved check 3's answer. That is precisely what `Reads.fs`'s own anchoring comment warns about: an
# un-anchored pattern lets a body that quotes a marker "forge a lock on the item it is posted" on. The
# forged comment is given a LOW id on purpose — the CAS takes the LOWEST live marker, so a low id is
# the only id at which a forgery is worth anything.
W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"
printf '<!-- fsgg:claim worker=forger lease=120 renewed=638000000000000000 -->' \
  | raw_comment "$W" "$REPO" "$NUM" 100 1
B_LOW="$(auth "$OPKEY" 900 "$ITEM" 100 | body_file)"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_LOW"
assert "G+10 a REAL low-id claim marker does become the winner (so G-10 is not vacuous)" 1 \
  "[check5]" "does not recompute"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "G+10b ...and it displaces the higher-id claim, so gen=\$GEN is now stale" 1 \
  "[check3]" "currently held under claim generation 100"

W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"
printf 'For reference, the marker looks like this: <!-- fsgg:claim worker=forger lease=120 renewed=638000000000000000 -->' \
  | raw_comment "$W" "$REPO" "$NUM" 100 1
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "G-10 a QUOTED claim marker forges no lock, so the real winner still holds" 0 "OK"

echo "=== H. merge_group — the trigger that is not optional =================================="

W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"
python3 -c '
import json, sys
print(json.dumps({"body": open(sys.argv[1], encoding="utf-8").read(), "ref": sys.argv[2], "sha": sys.argv[3]}))
' "$B_WINNER" "$BRANCH" "$HEAD" > "$W/pulls/17.json"

QUEUE_SHA=3333333333333333333333333333333333333333
gate "$W" --repo "$REPO" --merge-group-ref "refs/heads/gh-readonly-queue/main/pr-17-$QUEUE_SHA"
assert "H1 a merge-group ref resolves its pull request and evaluates identically" 0 "OK"

# THE ONE THAT WOULD RED EVERY QUEUED PR IF GOT WRONG: the authorization binds to the PULL REQUEST's
# head, not to the queue's temporary merge commit. H1 already proves it — the marker names $HEAD while
# the ref carries $QUEUE_SHA — and this leg makes the dependency explicit by moving the PR's head.
python3 -c '
import json, sys
print(json.dumps({"body": open(sys.argv[1], encoding="utf-8").read(), "ref": sys.argv[2], "sha": sys.argv[3]}))
' "$B_WINNER" "$BRANCH" "4444444444444444444444444444444444444444" > "$W/pulls/18.json"
gate "$W" --repo "$REPO" --merge-group-ref "refs/heads/gh-readonly-queue/main/pr-18-$QUEUE_SHA"
assert "H2 check 2 compares the PULL REQUEST's head, and says so on a merge group" 1 \
  "[check2]" "NOT the merge queue's temporary merge commit"

gate "$W" --repo "$REPO" --merge-group-ref "refs/heads/gh-readonly-queue/main/nonsense"
assert "H3 a ref this gate has never seen is a no-verdict, never a guess" 3 \
  "does not end in \`pr-<number>-<sha>\`"

gate "$W" --repo "$REPO" --merge-group-ref "refs/heads/gh-readonly-queue/main/pr-17-$QUEUE_SHA" \
     --head-sha "$HEAD"
assert "H4 supplying both entry points is refused rather than reconciled" 3 "must not also be given"

gate "$W" --repo "$REPO" --merge-group-ref "refs/heads/gh-readonly-queue/main/pr-99-$QUEUE_SHA"
assert "H5 a merge-group ref naming a pull request that is not there is a no-verdict" 3 "does not exist"

# A base branch containing digits and slashes must not have a PR number read out of its middle.
python3 -c '
import json, sys
print(json.dumps({"body": open(sys.argv[1], encoding="utf-8").read(), "ref": sys.argv[2], "sha": sys.argv[3]}))
' "$B_WINNER" "$BRANCH" "$HEAD" > "$W/pulls/17.json"
gate "$W" --repo "$REPO" --merge-group-ref "refs/heads/gh-readonly-queue/release/pr-99-x/pr-17-$QUEUE_SHA"
assert "H6 the ref is parsed from its END, so a base branch cannot spoof the number" 0 "OK"

echo "=== I. Check 6 — a failed read is never a pass ========================================="

W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"
touch "$W/comments/FS-GG__.github__2719.forbidden"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "I1 a 403 is a permanent no-verdict, not green and not a finding" 3

W="$(new_world)"; claim_comment "$W" "$GEN" 5; election_comment "$W" 900 "$OPKEY"
touch "$W/comments/FS-GG__.github__2719.unreachable"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "I2 a rate limit is a RETRYABLE no-verdict, distinguishable from I1" 2

W="$(new_world)"   # no comments file at all: the item does not exist
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "I3 a 404 is a real answer about the item, so a finding rather than a no-verdict" 1 \
  "[check1]" "does not exist"

gate "$W" --repo "not-a-repo" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "I4 an unusable --repo is a no-verdict before anything is read" 3 "must be \`owner/name\`"

gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "nope" --body "$B_WINNER"
assert "I5 an unusable --head-sha is a no-verdict" 3 "40-character hex SHA"

echo "=== J. The lease is the marker's own, and --lease-minutes outranks it =================="

W="$(new_world)"; claim_comment "$W" "$GEN" 200 480; election_comment "$W" 900 "$OPKEY"
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "J1 a marker declaring a 480-minute lease is live at 200 minutes" 0 "OK"

gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER" --lease-minutes 60
assert "J2 an explicit --lease-minutes outranks the marker's own declaration" 1 \
  "[check3]" "has no LIVE"

W="$(new_world)"; claim_comment "$W" "$GEN" 200 480; election_comment "$W" 900 "$OPKEY"
OUT=""; RC=0
set +e
OUT="$(env "WORLD=$W" "PATH=$STUB:$PATH" FSGG_CLAIM_LEASE_MIN=60 python3 "$TOOL" \
        --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER" 2>&1)"
RC=$?
set -e
assert "J3 the env var is only a fallback — the marker's own lease still outranks it" 0 "OK"

set +e
OUT="$(env "WORLD=$W" "PATH=$STUB:$PATH" FSGG_CLAIM_LEASE_MIN=nonsense python3 "$TOOL" \
        --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER" 2>&1)"
RC=$?
set -e
assert "J4 a malformed FSGG_CLAIM_LEASE_MIN is a no-verdict, never a guessed lease" 3 \
  "needs a number of minutes"

# An election marker is NEVER stale. This is §4.2's load-bearing property: a lease here would recreate
# the fail-ALWAYS shape (#463) in which a correctly-behaving worker who released on schedule is read
# as unauthorized. The election below is a year old and must still ground the authorization.
W="$(new_world)"; claim_comment "$W" "$GEN" 5
printf '<!-- fsgg:merge-election v=1 opkey=%s item=%s gen=%s receiver=%s op=merge -->' \
  "$OPKEY" "$ITEM" "$GEN" "$REPO" | raw_comment "$W" "$REPO" "$NUM" 900 525600
gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$B_WINNER"
assert "J5 an election a year old is still the election — it has no lease (design §4.2)" 0 "OK"

echo "=== K. The fail-closed boundary, asked of the parsed document =========================="
# Slice 8 makes the producer fail closed while keeping branch-protection activation separate.
#
# ROUND-1 REPAIR (critic teal-79dd on #2740). The first version of this section parsed the workflow and
# then ASKED THE PARSED DOCUMENT FOR A SPELLING: it searched the raw text for `^\s*exit\s+([0-9]+)`.
# Six mutations broke it and four genuinely armed the workflow — `[ "$rc" = 0 ] || exit "$rc"`; a
# one-line `run: exit 1`; a bare `false`, which carries NO `exit` token at all; and
# `if [ "$rc" != 0 ]; then exit 1; fi`. The other two were not `exit` spellings in any sense:
# `paths-ignore:` is a trigger filter, and a JOB-level `permissions:` block REPLACES the workflow-level
# one, so the key the old check read was no longer the key GitHub applies. `.github#266` Instance 5.
#
# ROUND-2 REPAIR (same critic, same instance). The three questions became rules; THE ENVIRONMENT THEY
# ARE MEASURED IN DID NOT. `substitute()` recognised two `${{ }}` spellings and mapped everything else
# to a placeholder, so `env: ARMED: ${{ steps.fence.outputs.rc != '0' }}` plus
# `if [ "$ARMED" = "true" ]; then exit 1; fi` read CLEAN — and the comparison form is what this
# workflow's own `if:` blocks already use, so it is the MORE likely arming spelling, not an exotic one.
# `shell:` was refused at the step level and ignored at the top level, where `defaults: {run: {shell:
# bash}}` changes the shell EVERY step runs under. And the sandbox SYMLINKED the real `scripts/`, so an
# executed `run:` could write the working tree. Same shape, moved from the subject into the harness.
#
# THE CLASS IS MATCHERS ENUMERATING SPELLINGS IN ANY MEDIUM — INCLUDING THE MEASURING INSTRUMENT. The
# repair is never a longer list. It is the pattern this file now applies in SEVEN places: permit a
# named set and refuse the rest.
#
#   1. IS THERE A TRIGGER FILTER?  An ALLOWLIST of the keys each trigger may carry. `paths`,
#      `paths-ignore`, `branches`, `branches-ignore` and anything GitHub adds tomorrow are refused by
#      one rule that names none of them.
#
#   2. WHAT PERMISSIONS APPLY?  The EFFECTIVE value under GitHub's replacement semantics — a job-level
#      block replaces the workflow-level block entirely — so the check reads the value that actually
#      governs the token rather than a key that may be inert.
#
#   3. DOES THIS WORKFLOW FAIL CLOSED? Not asked of the text at all. Every step's `run:` is EXECUTED
#      with the gate stubbed to return each verdict in turn. Exactly one `id: enforce` step must exit
#      zero for verdict 0 and non-zero for every substantive, no-verdict, and unclassified result;
#      all diagnostic steps must still render without becoming an accidental second gate.
#
#   4. WHAT IS THE MEASUREMENT ALLOWED TO ASSUME?  An ALLOWLIST of the `${{ }}` expressions this
#      harness knows how to resolve. Anything else is NOT MEASURED and is reported as a violation —
#      never silently placeholdered into a green. `#266`: "I could not evaluate this" is never
#      "I evaluated it and it passed."
#
#   5/6/7. KEY ALLOWLISTS AT ALL THREE LEVELS — top, job, step. A subject closed at one level and open
#      at another is round-0's E6 in a different key, and it is exactly how `defaults:` escaped.
#
# THE SANDBOX IS A COPY, NEVER A LINK. Steps run in a scratch tree holding a COPY of `scripts/` and a
# stub `tests/claim-fence/run.sh` that exits 0 — so the fixture step is executed verbatim without
# re-entering this file, and an executed `run:` that writes cannot reach the working tree. Leg R20
# proves that with a probe, and the meta-inversion below shows the leg reds if the copy becomes a link.

cat > "$WORK/flowcheck.py" <<'FLOWPY'
"""Ask a workflow document the questions section K means. One line per violation."""
import os, re, shutil, subprocess, sys, tempfile, yaml

WORKFLOW, ROOT = sys.argv[1], sys.argv[2]

# --- The seven allowlists. Each names what is PERMITTED; nothing anywhere names what is forbidden. ---

# 1. A trigger may carry only these keys.
TRIGGER_KEYS = {"pull_request": {"types"}, "merge_group": {"types"}}
REQUIRED_TRIGGERS = ("pull_request", "merge_group")
# 2. The effective permissions this producer must run under (design §6.2, §8.2).
REQUIRED_PERMISSIONS = {"contents": "read", "issues": "read"}
# 5/6/7. Keys permitted at each level. `defaults:` is refused at the top precisely because
# `defaults.run.shell` changes the shell EVERY step runs under, which is the premise question 3 rests
# on; `shell:` and `continue-on-error:` are refused at the step level for the same reason.
TOP_KEYS = {"name", "on", "permissions", "jobs"}
JOB_KEYS = {"runs-on", "timeout-minutes", "steps", "permissions"}
STEP_KEYS = {"name", "id", "uses", "run", "env", "if"}
PERMITTED_USES = {"actions/checkout@v7", "./.github/actions/setup-policy-python"}

# 4. The `${{ }}` expressions this harness knows how to resolve, and nothing else. Extending this map
# is a deliberate act by an author who has thought about what the new expression means for the
# measurement; falling back to a placeholder is the confident wrong answer round 2 was filed for.
def resolvable(gate_exit, event):
    return {
        "steps.fence.outputs.rc": gate_exit,
        "steps.fence.outputs.verdict": "FIXTURE-VERDICT",
        "github.event_name": event,
        "github.repository": "FS-GG/.github",
        "github.event.pull_request.body": "FIXTURE-PR-BODY",
        "github.event.pull_request.head.ref": "item/2719-fixture",
        "github.event.pull_request.head.sha": "1" * 40,
        "github.event.merge_group.head_ref":
            "refs/heads/gh-readonly-queue/main/pr-1-" + "1" * 40,
        "secrets.GITHUB_TOKEN": "FIXTURE-TOKEN",
    }

# 3. Both shells GitHub can give a step that declares no `shell:`. The bare default on Linux is
# `bash -e {0}`; the explicit `bash` spelling — which `defaults.run.shell` or a step-level `shell:`
# could impose — is `bash --noprofile --norc -eo pipefail {0}`, adding `-o pipefail`.
#
# THIS SECOND AXIS IS MEASURED NOT TO BE LOAD-BEARING TODAY, AND IT IS KEPT ANYWAY — the same
# defence-in-depth disposition, and the same honesty about it, that the `^`/`.match` anchoring pair
# gets in `scripts/check-claim-fence.py`. Dropping the axis SURVIVES the fixture: no step in this
# workflow carries a pipe whose left-hand failure `-o pipefail` would surface, and the top-level and
# step-level key allowlists now refuse both spellings that could impose the stricter shell, so the
# premise is ENFORCED rather than assumed. The axis is what stops the measurement from depending on
# that enforcement — one rule failing would otherwise silently change what every execution below
# means. Recorded rather than asserted so a later reader does not mistake a redundant axis for a
# firing one, and does not delete it believing it was load-bearing.
SHELLS = (
    ("bash -e (GitHub's bare default)", ["bash", "-e"]),
    ("bash -eo pipefail (GitHub's `shell: bash`)", ["bash", "--noprofile", "--norc", "-eo", "pipefail"]),
)
GATE_EXITS = ("0", "1", "2", "3", "7")
EVENTS = ("pull_request", "merge_group")

EXPR = re.compile(r"\$\{\{(.*?)\}\}", re.DOTALL)


def substitute(text, gate_exit, event, unresolved):
    """Resolve `${{ }}` expressions from the allowlist; record every one that is not in it.

    Recording rather than placeholdering is the whole of round 2's F2a. An unrecognised expression
    means this harness cannot say what the step does under a real verdict — which is a NO-VERDICT about
    that step, not a clean bill.
    """
    table = resolvable(gate_exit, event)

    def one(match):
        expression = match.group(1).strip()
        if expression in table:
            return table[expression]
        unresolved.append(expression)
        return "UNRESOLVED"

    return EXPR.sub(one, text)


def build_sandbox(root):
    """A scratch tree the executed steps cannot escape.

    COPIED, never symlinked (round 2's F2c): `build_sandbox` used to link the real `scripts/`, so a
    `run:` that wrote a file reached the working tree — measured by the critic with a probe that landed
    in their checkout. Execution buys correctness and creates a surface; this is the surface being
    closed. `tests/` holds ONLY a stub `claim-fence/run.sh`, so the fixture step executes verbatim
    without re-entering this file, and a step redirected at any other suite finds nothing and is caught
    as a failing step rather than special-cased.
    """
    box = tempfile.mkdtemp(prefix="flowcheck.")
    os.makedirs(os.path.join(box, "tests", "claim-fence"))
    stub = os.path.join(box, "tests", "claim-fence", "run.sh")
    with open(stub, "w", encoding="utf-8") as handle:
        handle.write("#!/usr/bin/env bash\n# stub: the real fixture is what is running us\nexit 0\n")
    os.chmod(stub, 0o755)
    shutil.copytree(os.path.join(root, "scripts"), os.path.join(box, "scripts"))
    os.makedirs(os.path.join(box, "bin"))
    return box


def gate_stub(box, code):
    for name in ("python", "python3"):
        path = os.path.join(box, "bin", name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(f"#!/usr/bin/env bash\nexit {code}\n")
        os.chmod(path, 0o755)


def run_step(box, step, gate_exit, event, shell):
    """Execute one step's script and return its real exit status, or None if it has no script."""
    script = step.get("run")
    if script is None:
        return None
    discard = []
    environment = {
        "PATH": os.path.join(box, "bin") + os.pathsep + os.environ.get("PATH", ""),
        "HOME": box,
        "GITHUB_OUTPUT": os.path.join(box, "github_output"),
        "GITHUB_STEP_SUMMARY": os.path.join(box, "github_step_summary"),
    }
    for key, value in (step.get("env") or {}).items():
        environment[str(key)] = substitute(str(value), gate_exit, event, discard)
    path = os.path.join(box, "step.sh")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(substitute(script, gate_exit, event, discard))
    try:
        proc = subprocess.run(shell + [path], cwd=box, env=environment,
                              capture_output=True, text=True, timeout=120)
    except subprocess.TimeoutExpired:
        return 124
    return proc.returncode


def main():
    problems = []
    document = yaml.safe_load(open(WORKFLOW, encoding="utf-8"))

    # ---- Top-level key allowlist (round 2, F2b) ------------------------------------------------
    # PyYAML reads a bare `on:` key as the boolean True under its 1.1 schema.
    top = {("on" if key is True else key) for key in document}
    extra = sorted(top - TOP_KEYS)
    if extra:
        problems.append(
            f"the workflow carries unexpected top-level key(s) {extra} — only {sorted(TOP_KEYS)} are "
            "permitted. `defaults:` in particular sets `defaults.run.shell`, which changes the shell "
            "EVERY step runs under and so changes what the execution measurement below MEANS. A "
            "subject closed at the job and step levels and open at the top is the same escape in a "
            "different key."
        )

    # ---- Question 1: is there a trigger filter? (allowlist) ------------------------------------
    triggers = document.get("on", document.get(True))
    if not isinstance(triggers, dict):
        problems.append("the `on:` block is not a mapping")
    else:
        for required in REQUIRED_TRIGGERS:
            if required not in triggers:
                problems.append(
                    f"no `{required}` trigger — design 6.2 requires both: a gate that only runs on "
                    "pull_request is bypassed by a merge queue, and one that only runs on merge_group "
                    "reports on no ordinary pull request at all"
                )
        for name, config in triggers.items():
            if name not in TRIGGER_KEYS:
                problems.append(f"unexpected trigger `{name}` — only {sorted(TRIGGER_KEYS)} are permitted")
                continue
            if config is None:
                continue
            if not isinstance(config, dict):
                problems.append(f"trigger `{name}` is not a mapping")
                continue
            extra = sorted(set(config) - TRIGGER_KEYS[name])
            if extra:
                problems.append(
                    f"trigger `{name}` carries filter key(s) {extra} — only "
                    f"{sorted(TRIGGER_KEYS[name])} is permitted. ANY filter means GitHub may create no "
                    "check run for some pull requests, and branch protection then waits forever "
                    "(design 6.2). This is an allowlist on purpose: `paths`, `paths-ignore`, "
                    "`branches`, `branches-ignore` and anything GitHub adds later are all refused by "
                    "one rule that names none of them."
                )

    # ---- Question 2: what permissions apply? (effective, after job-level replacement) -----------
    jobs = document.get("jobs") or {}
    if list(jobs) != ["claim-fence"]:
        problems.append(f"jobs are {list(jobs)!r}, not exactly ['claim-fence']")

    for job_id, job in jobs.items():
        if not isinstance(job, dict):
            problems.append(f"job `{job_id}` is not a mapping")
            continue
        extra = sorted(set(job) - JOB_KEYS)
        if extra:
            problems.append(
                f"job `{job_id}` carries unexpected job key(s) {extra} — only {sorted(JOB_KEYS)} are "
                "permitted. A job-level `if:` in particular would let this producer silently not "
                "report, which is the one thing an arming candidate must never do."
            )
        if "permissions" in job:
            effective, source = job["permissions"], (
                "the JOB-level block, which REPLACES the workflow-level block entirely rather than "
                "merging with it"
            )
        else:
            effective, source = document.get("permissions"), "the workflow-level block"
        if effective != REQUIRED_PERMISSIONS:
            problems.append(
                f"job `{job_id}`'s EFFECTIVE permissions are {effective!r}, from {source} — not "
                f"exactly {REQUIRED_PERMISSIONS}. Design 6.2 fixes the scopes, and 8.2 makes the "
                "narrowness load-bearing rather than tidy."
            )

        # ---- Structural pass, once per step: keys, uses, and what the harness may assume --------
        measurable = []
        enforcement = []
        for index, step in enumerate(job.get("steps") or []):
            if not isinstance(step, dict):
                problems.append(f"job `{job_id}` step {index} is not a mapping")
                continue
            extra = sorted(set(step) - STEP_KEYS)
            if extra:
                problems.append(
                    f"job `{job_id}` step {index} carries unexpected step key(s) {extra} — only "
                    f"{sorted(STEP_KEYS)} are permitted. `shell:` and `continue-on-error:` both change "
                    "what a step's exit status MEANS, so admitting either would make the execution "
                    "measurement below answer a different question than the one it reports."
                )
            uses = step.get("uses")
            if uses is not None and uses not in PERMITTED_USES:
                problems.append(
                    f"job `{job_id}` step {index} uses `{uses}`, which is not one of "
                    f"{sorted(PERMITTED_USES)} — an action's failure is not a verdict this producer is "
                    "allowed to enforce, and its script cannot be executed here to check."
                )

            # Question 4. Probe every expression this step carries against the allowlist ONCE,
            # before any execution, so an unrecognised one is reported as a NO-VERDICT about the step
            # rather than repeated per axis or — as in round 1 — silently placeholdered into a green.
            unresolved = []
            substitute(str(step.get("run") or ""), "0", "pull_request", unresolved)
            for value in (step.get("env") or {}).values():
                substitute(str(value), "0", "pull_request", unresolved)
            if unresolved:
                problems.append(
                    f"job `{job_id}` step {index} carries `${{{{ }}}}` expression(s) "
                    f"{sorted(set(unresolved))} that this harness does not know how to resolve, so its "
                    "exit status under a real verdict is NOT MEASURED — which is not the same as "
                    "clean (#266). The comparison form `steps.fence.outputs.rc != '0'` is exactly how "
                    "a step arms itself without an `exit` token in sight. Extend the `resolvable` "
                    "allowlist deliberately if the expression is legitimate; do not placeholder it."
                )
                continue
            measurable.append((index, step))
            if step.get("id") == "enforce":
                enforcement.append((index, step))

        if len(enforcement) != 1:
            problems.append(
                f"job `{job_id}` has {len(enforcement)} `id: enforce` steps, not exactly one — the "
                "captured verdict must have one auditable consumer"
            )

        # ---- Question 3: does this workflow fail closed? (executed, never matched) --------------
        box = build_sandbox(ROOT)
        try:
            for gate_exit in GATE_EXITS:
                gate_stub(box, gate_exit)
                for event in EVENTS:
                    for label, shell in SHELLS:
                        for index, step in measurable:
                            status = run_step(box, step, gate_exit, event, shell)
                            is_enforcement = step.get("id") == "enforce"
                            expected_failure = gate_exit != "0"
                            if is_enforcement and ((status == 0) == expected_failure):
                                problems.append(
                                    f"job `{job_id}` enforcement step {index} EXITS {status} when the "
                                    f"gate returns {gate_exit} on a {event} event under {label} — only "
                                    "verdict 0 may pass; findings, no-verdicts, and unclassified codes "
                                    "must fail closed. Measured by EXECUTING the step, not matching text."
                                )
                            elif not is_enforcement and status not in (None, 0):
                                problems.append(
                                    f"job `{job_id}` step {index} "
                                    f"({step.get('name') or step.get('uses')}) EXITS {status} when the "
                                    f"gate returns {gate_exit} on a {event} event under {label} — only "
                                    "the named enforcement step may consume the verdict. Measured by "
                                    "EXECUTING the step, not by matching its text."
                                )
        finally:
            shutil.rmtree(box, ignore_errors=True)

    for problem in problems:
        print(problem)


main()
FLOWPY

flow_check() {  # <workflow-file> -> one line per violation
  python3 "$WORK/flowcheck.py" "$1" "$ROOT"
}

VIOL="$(flow_check "$FLOW")"
if [ -z "$VIOL" ]; then
  ok "K1 unfiltered, read-only by effective permission, and fail-closed on every non-OK verdict"
else
  bad "K1 unfiltered, read-only by effective permission, and fail-closed on every non-OK verdict" "$VIOL"
fi

# ------------------------------------------------------------------------------------------------
# THE INVERSIONS. Each mutant is a real edit to a copy of the workflow, and each must be CAUGHT with
# the reason named — a mutation caught by the wrong rule is a vacuous pass wearing a red badge, and a
# mutation that changes nothing has tested nothing. R1-R6 are the six round-1 escapes; R18-R19 are the
# two round-2 escapes that this section can express as a violation, and R20 is the third, which is
# about where a write LANDS rather than about a verdict.
# ------------------------------------------------------------------------------------------------
PROBE="PROBE-$$-$(date +%s)"

cat > "$WORK/mutants.py" <<'MUTPY'
"""Write one mutant workflow per inversion, each with the substring its violation must contain."""
import json, os, sys

FLOW, OUT, PROBE = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(FLOW, encoding="utf-8").read()
os.makedirs(OUT, exist_ok=True)

ARMS = "only the named enforcement step"
FAILS_OPEN = "must fail closed"
FILTER = "carries filter key(s)"
PERMS = "EFFECTIVE permissions"
UNRESOLVED = "NOT MEASURED"
TOPKEY = "unexpected top-level key(s)"

EVALUATE_TAIL = '          echo "rc=$rc" >> "$GITHUB_OUTPUT"\n'
FINDING_WARNING = (
    '          echo "::error title=fsgg-claim-fence: finding::The merge fence refuses this head. See '
    'the job summary for which of design §6.3\'s checks failed."\n'
)
INCONCLUSIVE_WARNING = (
    '          echo "::error title=fsgg-claim-fence: inconclusive::The gate exited with an unclassified '
    'code; the fence fails closed."\n'
)
INCONCLUSIVE_ENV = "          RC: ${{ steps.fence.outputs.rc }}\n"

MUTANTS = [
    # --- the six round-1 escapes ---------------------------------------------------------------
    ("R1 `[ \"$rc\" = 0 ] || exit \"$rc\"` on the evaluate step arms it",
     lambda t: t.replace(EVALUATE_TAIL, EVALUATE_TAIL + '          [ "$rc" = 0 ] || exit "$rc"\n', 1), ARMS),
    ("R2 a one-line `run: exit 1` step arms it",
     lambda t: t.replace("      - uses: actions/checkout@v7\n",
                         "      - uses: actions/checkout@v7\n      - name: arm\n        run: exit 1\n", 1), ARMS),
    ("R3 a bare `false` arms it, and carries NO `exit` token at all",
     lambda t: t.replace(FINDING_WARNING, FINDING_WARNING + "          false\n", 1), ARMS),
    ("R4 `if [ \"$RC\" != 0 ]; then exit 1; fi` arms it — #266 Instance 4's own `then ...` spelling",
     lambda t: t.replace(INCONCLUSIVE_WARNING,
                         INCONCLUSIVE_WARNING + '          if [ "$RC" != 0 ]; then exit 1; fi\n', 1), ARMS),
    ("R5 `paths-ignore:` is a real trigger filter, not an `exit` spelling",
     lambda t: t.replace("    types: [opened, synchronize, reopened, edited]\n",
                         '    types: [opened, synchronize, reopened, edited]\n    paths-ignore: ["docs/**"]\n', 1), FILTER),
    ("R6 a JOB-level permissions block REPLACES the workflow-level one",
     lambda t: t.replace("  claim-fence:\n    runs-on: ubuntu-latest\n",
                         "  claim-fence:\n    permissions:\n      contents: write\n    runs-on: ubuntu-latest\n", 1), PERMS),
    # --- dimensions the round-1 escapes did not reach, bound by the same rules -------------------
    ("R7 `branches:` is a filter too, and the allowlist names it nowhere",
     lambda t: t.replace("    types: [opened, synchronize, reopened, edited]\n",
                         "    types: [opened, synchronize, reopened, edited]\n    branches: [main]\n", 1), FILTER),
    ("R8 `paths:` — the one the round-1 check DID catch — is still caught by the allowlist",
     lambda t: t.replace("    types: [opened, synchronize, reopened, edited]\n",
                         '    types: [opened, synchronize, reopened, edited]\n    paths: ["scripts/**"]\n', 1), FILTER),
    ("R9 removing the merge_group trigger is caught",
     lambda t: t.replace("  merge_group:\n", "", 1), "no `merge_group` trigger"),
    ("R10 removing the pull_request trigger is caught",
     lambda t: t.replace("  pull_request:\n    types: [opened, synchronize, reopened, edited]\n", "", 1),
     "no `pull_request` trigger"),
    ("R11 widening the workflow-level permissions is caught",
     lambda t: t.replace("  contents: read\n", "  contents: write\n", 1), PERMS),
    ("R12 a job-level `if:` would let the producer silently not report",
     lambda t: t.replace("  claim-fence:\n    runs-on: ubuntu-latest\n",
                         "  claim-fence:\n    if: github.actor != 'nobody'\n    runs-on: ubuntu-latest\n", 1),
     "unexpected job key(s)"),
    ("R13 `continue-on-error:` changes what an exit status MEANS",
     lambda t: t.replace("      - name: Run the claim-fence fixture (no network)\n",
                         "      - name: Run the claim-fence fixture (no network)\n        continue-on-error: true\n", 1),
     "unexpected step key(s)"),
    ("R14 a step-level `shell:` changes it too",
     lambda t: t.replace("      - name: Run the claim-fence fixture (no network)\n",
                         "      - name: Run the claim-fence fixture (no network)\n        shell: sh\n", 1),
     "unexpected step key(s)"),
    ("R15 an unvetted `uses:` cannot be executed, so it cannot be certified",
     lambda t: t.replace("      - uses: actions/checkout@v7\n",
                         "      - uses: actions/checkout@v7\n      - uses: some/action@v1\n", 1),
     "which is not one of"),
    ("R16 pointing the fixture step at another path is caught, with no special case for it",
     lambda t: t.replace("        run: bash tests/claim-fence/run.sh\n",
                         "        run: bash tests/claim-generation/run.sh\n", 1), ARMS),
    ("R17 failing the OK report step creates an accidental second gate",
     lambda t: t.replace('            echo "### fsgg-claim-fence: OK"\n',
                         '            echo "### fsgg-claim-fence: OK"\n            exit 3\n', 1), ARMS),
    # --- the two round-2 escapes this section expresses as a violation ---------------------------
    ("R18 F2a: `${{ ... != '0' }}` in env plus a plain string test arms it, with no `exit` in the run",
     lambda t: t.replace(
         INCONCLUSIVE_ENV,
         INCONCLUSIVE_ENV + "          ARMED: ${{ steps.fence.outputs.rc != '0' }}\n", 1
     ).replace(
         INCONCLUSIVE_WARNING,
         INCONCLUSIVE_WARNING + '          if [ "$ARMED" = "true" ]; then exit 1; fi\n', 1
     ), UNRESOLVED),
    ("R19 F2b: `defaults: {run: {shell: bash}}` changes the shell EVERY step runs under",
     lambda t: t.replace("\njobs:\n", "\ndefaults:\n  run:\n    shell: bash\n\njobs:\n", 1), TOPKEY),
    # R20 (F2c) is asserted separately: it is about where a write LANDS, not about a verdict.
    (f"R20 F2c: a step that writes `scripts/` must not reach the working tree",
     lambda t: t.replace("      - uses: actions/checkout@v7\n",
                         f"      - uses: actions/checkout@v7\n      - name: probe\n        run: ': > scripts/{PROBE}'\n", 1),
     "__PROBE__"),
    ("R21 replacing the consumed verdict with success makes the required context fail open",
     lambda t: t.replace('            1|2|3) exit "$RC" ;;\n',
                         '            1|2|3) exit 0 ;;\n', 1), FAILS_OPEN),
]

index = []
for number, (name, mutate, expect) in enumerate(MUTANTS, start=1):
    mutated = mutate(text)
    slug = f"{number:02d}"
    open(os.path.join(OUT, slug + ".yml"), "w", encoding="utf-8").write(mutated)
    index.append({"slug": slug, "name": name, "expect": expect, "changed": mutated != text})
open(os.path.join(OUT, "index.json"), "w", encoding="utf-8").write(json.dumps(index))
MUTPY

MUTANTS="$WORK/mutants"
python3 "$WORK/mutants.py" "$FLOW" "$MUTANTS" "$PROBE"

while IFS=$'\t' read -r slug name expect changed; do
  if [ "$changed" != "True" ]; then
    bad "$name" "the mutation changed nothing, so this inversion tested nothing"
    continue
  fi
  got="$(flow_check "$MUTANTS/$slug.yml" || true)"
  if [ "$expect" = "__PROBE__" ]; then
    # R20 is the ONE leg whose subject is the harness's own blast radius rather than the workflow's
    # verdict. Two assertions, and BOTH are needed: the probe must not exist in the working tree, and
    # the step must have exited 0 — because a sandbox with no `scripts/` at all would make the write
    # FAIL, which would look like a pass here for entirely the wrong reason.
    # `case`, not `printf | grep -q`: this repo's own `pipefail-assertions` gate refuses a pipeline
    # whose status is tested, because an early-exiting right-hand side masks the left's. It caught
    # this very line, which is the gate working.
    probe_failed=0
    case "$got" in *EXITS*) probe_failed=1;; esac
    if [ "$probe_failed" = 1 ]; then
      bad "$name" "the probe step failed inside the sandbox, so this leg proves nothing about escape:
$got"
    elif [ -e "$ROOT/scripts/$PROBE" ]; then
      rm -f "$ROOT/scripts/$PROBE"
      bad "$name" "THE WRITE ESCAPED into the working tree — the sandbox is linking, not copying"
    else
      ok "$name"
    fi
    continue
  fi
  case "$got" in
    *"$expect"*) ok "$name";;
    *) bad "$name" "NOT CAUGHT — this is the escape shape itself. flow_check said: ${got:-<nothing>}";;
  esac
done < <(python3 -c '
import json, sys
for row in json.load(open(sys.argv[1])):
    print("\t".join([row["slug"], row["name"], row["expect"], str(row["changed"])]))
' "$MUTANTS/index.json")

echo
echo "=== L. The FINDING summary DERIVES the failing check — it never predicts one ============"
# WHAT THIS SECTION IS FOR, and it is a repair of a defect this fixture MISSED. Until `.github#2719`
# was reopened, the FINDING step carried this sentence, addressed to operators:
#
#     "Note that `check4` is expected to fail on every real pull request today: nothing writes a
#      `fsgg:merge-election` marker, because slice 3 (.github#2395) landed the authorization half
#      without the election half."
#
# Both halves were wrong, in OPPOSITE directions, and the second is the one that matters. Check 4 was
# not *failing*; it was never EVALUATED. `scripts/check-claim-fence.py` requires SIX authorization
# fields and the pre-`.github#2395` producer composed FOUR, so the missing-field arm returned
# `check1` and control never reached check 4 at all. A PREDICTED red and an UNREACHABLE branch are
# indistinguishable in a job summary — nothing an operator can observe tells them apart — so the
# sentence was unfalsifiable rather than merely stale. That is `.github#266`'s class arriving at the
# operator-guidance layer.
#
# THE REPAIR IS NOT A CORRECTED PREDICTION, because a corrected prediction goes stale the same way.
# It is to stop predicting: the heading is DERIVED from the gate's own verdict at read time. This
# section holds that in two independent ways —
#
#   L2-L4  EXECUTE the step over a corpus of verdicts and assert the heading names the check the
#          verdict names AND NONE OF THE OTHER FIVE. A derivation that hard-coded any one check would
#          pass one corpus entry and red five; one that stopped deriving reds all six. L5/L6 apply
#          exactly those two mutations and require the reds.
#   L7-L10 assert that NO operator-facing text in this workflow names a check literally at all, bound
#          by a recognition corpus in both directions (section G's lesson, applied to a second
#          absence) and by a mutation that re-inserts the REMOVED SENTENCE ITSELF.
#
# THE SURFACE L7 SCANS IS DELIBERATE AND BOUNDED: every step's `name`, `run` and `env` values, as
# PARSED — the text that reaches an operator in the job log and the job summary. YAML comments are
# NOT scanned, because they are maintainer-facing and this very file's header now discusses check 1
# and check 4 at length in order to explain the defect. That boundary is asserted rather than assumed:
# L10 puts the removed sentence in a comment and requires the scan to stay silent, so a later reader
# can see the surface is where this fixture says it is and not accidentally wider or narrower.

cat > "$WORK/findingstep.py" <<'FINDPY'
"""Extract ONE step's `run:` script from a workflow, selected BY ID, with `${{ }}` resolved.

BY ID, never by position and never by name: an assertion that quietly starts grading a different step
is the same defect class this fixture exists to close, and `tests/receiver-validate/run.sh` states the
argument for its own extraction in the same words.

The ONLY expression resolved is the one this harness supplies a value for. Anything else is a hard
error rather than a placeholder — section K's question 4, applied here too, because a step whose text
this harness cannot fully resolve is a step whose behaviour is NOT MEASURED, which is not the same as
clean (#266).
"""
import re, sys, yaml

WORKFLOW, JOB_ID, STEP_ID, OUT = sys.argv[1:5]
BINDINGS = {"steps.fence.outputs.verdict": "@VERDICT@"}

doc = yaml.safe_load(open(WORKFLOW, encoding="utf-8"))
job = (doc.get("jobs") or {}).get(JOB_ID)
if not isinstance(job, dict):
    sys.exit("no job `%s` in %s" % (JOB_ID, WORKFLOW))
steps = [s for s in (job.get("steps") or []) if isinstance(s, dict) and s.get("id") == STEP_ID]
if len(steps) != 1:
    sys.exit("expected exactly one step with `id: %s`, found %d" % (STEP_ID, len(steps)))
step = steps[0]

unresolved = []


def sub(text):
    def one(match):
        expression = match.group(1).strip()
        if expression in BINDINGS:
            return BINDINGS[expression]
        unresolved.append(expression)
        return "UNRESOLVED"

    return re.sub(r"\$\{\{(.*?)\}\}", one, text, flags=re.DOTALL)


script = sub(str(step.get("run") or ""))
env = {str(k): sub(str(v)) for k, v in (step.get("env") or {}).items()}
if unresolved:
    sys.exit("unresolvable `${{ }}` expression(s) in step `%s`: %s" % (STEP_ID, sorted(set(unresolved))))
if env != {"VERDICT": "@VERDICT@"}:
    sys.exit("step `%s` env is %r, not the single VERDICT binding this harness resolves" % (STEP_ID, env))
if not script.strip():
    sys.exit("step `%s` has no `run:` script to execute" % STEP_ID)
open(OUT, "w", encoding="utf-8").write(script)
FINDPY

extract_finding() {  # <workflow> <out-script> -> stderr carries the reason on failure
  python3 "$WORK/findingstep.py" "$1" "claim-fence" "finding" "$2"
}

FINDING_SH="$WORK/finding-step.sh"
if extract_finding "$FLOW" "$FINDING_SH" 2>"$WORK/finding.err"; then
  ok "L1 the FINDING step is selectable BY ID, and every \`\${{ }}\` it carries is one this harness resolves"
else
  bad "L1 the FINDING step could not be extracted by id, so nothing below measures anything" \
    "$(cat "$WORK/finding.err")"
fi

# run_finding <script> <verdict> — executes the extracted step and sets FRC and FHEAD (its first
# summary line, which is the heading). Under `bash -eo pipefail`, the STRICTER of the two shells
# GitHub can give a step that declares none: a derivation that acquired a status from a pipeline
# would be caught here as a non-zero FRC rather than silently tolerated.
run_finding() {
  local script="$1" verdict="$2" sum
  sum="$(mktemp "$WORK/summary.XXXXXX")"
  set +e
  env VERDICT="$verdict" GITHUB_STEP_SUMMARY="$sum" \
    bash --noprofile --norc -eo pipefail "$script" >/dev/null 2>&1
  FRC=$?
  set -e
  FHEAD="$(head -n 1 "$sum")"
}

# THE CORPUS. One verdict per check the gate can name, each in the EXACT `[checkN]` shape
# `scripts/check-claim-fence.py` prints (`print(f"check-claim-fence: [{check}] {message}")`). The
# message text deliberately spells no check at all, so the assertion below cannot be satisfied by the
# quoted verdict leaking into the heading.
derive_check() {  # <script> -> prints one line per corpus failure; silence is a pass
  local script="$1" n m
  for n in 1 2 3 4 5 6; do
    run_finding "$script" "check-claim-fence: [check$n] a fixture verdict, deliberately naming none in prose."
    if [ "$FRC" != 0 ]; then
      echo "the diagnostic step EXITED $FRC on a [check$n] verdict — reporting must complete before enforcement"
      continue
    fi
    case "$FHEAD" in
      *"check$n"*) ;;
      *) echo "a [check$n] verdict produced the heading: ${FHEAD:-<empty>} — which does not name check$n";;
    esac
    for m in 1 2 3 4 5 6; do
      if [ "$m" != "$n" ]; then
        case "$FHEAD" in
          *"check$m"*) echo "a [check$n] verdict produced a heading naming check$m as well: $FHEAD";;
        esac
      fi
    done
  done
  return 0
}

DERIVE="$(derive_check "$FINDING_SH")"
if [ -z "$DERIVE" ]; then
  ok "L2 over six verdicts, the heading names the check the verdict names — and none of the other five"
else
  bad "L2 over six verdicts, the heading names the check the verdict names — and none of the other five" "$DERIVE"
fi

# The gate can also emit a finding whose text this step cannot parse. "I could not tell WHICH" must be
# a NAMED state, never a blank heading that reads like a clean one (#266).
run_finding "$FINDING_SH" "check-claim-fence: something went sideways and no check id was printed"
case "$FHEAD" in
  *unnamed*) ok "L3 a verdict naming no check yields a NAMED \`unnamed\` heading, not a blank one" ;;
  *) bad "L3 a verdict naming no check yields a NAMED \`unnamed\` heading, not a blank one" "heading was: ${FHEAD:-<empty>}" ;;
esac
[ "$FRC" = 0 ] \
  && ok "L4 …and the report step exits 0, so diagnostics render before the separate fail-closed consumer" \
  || bad "L4 …and the report step exits 0 before the separate fail-closed consumer" "rc=$FRC"

# ---- MUTATIONS: the two ways this derivation can regress, each applied and each required to red ----
# Both mutations assert they CHANGED the script first. A mutation that silently matched nothing would
# leave L5/L6 asserting that an unmodified script still passes, which is the vacuous shape these legs
# exist to refuse.
mutate_finding() {  # <name> <sed-expression> <expect-changed-marker>
  local label="$1" expr="$2" mutant="$WORK/finding-mutant.sh"
  sed "$expr" "$FINDING_SH" > "$mutant"
  if cmp -s "$FINDING_SH" "$mutant"; then
    bad "$label" "the mutation changed nothing, so this inversion tested nothing"
    return
  fi
  if [ -z "$(derive_check "$mutant")" ]; then
    bad "$label" "NOT CAUGHT — L2 still passed against the mutated derivation, so L2 asserts nothing"
  else
    ok "$label"
  fi
}

mutate_finding \
  "L5 MUTATION: hard-coding a PREDICTED check (\`fired=check4\`) in place of the derivation reds L2" \
  's/fired="\${BASH_REMATCH\[1\]}"/fired=check4/'
mutate_finding \
  "L6 MUTATION: a derivation regex that can never match reds L2 (every heading becomes \`unnamed\`)" \
  's/(check\[0-9\]+)/(NOTACHECK[0-9]+)/'

# ------------------------------------------------------------------------------------------------
# L7-L11. NO OPERATOR-FACING TEXT IN THIS WORKFLOW NAMES A CHECK LITERALLY.
# ------------------------------------------------------------------------------------------------
cat > "$WORK/namescheck.py" <<'NAMEPY'
"""Does this workflow's OPERATOR-FACING text name a specific check? One line per offending fragment.

A workflow that spells a check id in its own prose is either PREDICTING which one will fire — the
sentence `.github#2719` was reopened for — or restating what the gate's verdict already says, which
is the same claim with a second copy free to drift. Either way it is an assertion this file cannot
keep true. The check id reaches the operator by DERIVATION or not at all.

THE RECOGNIZER IS NOT A SPELLING LIST. `check`, word-initial, an optional single separator, then a
DIGIT. Requiring the digit is what keeps `checkout`, `checks`, `checked` and `check-claim-fence.py`
out; the word-initial lookbehind is what keeps `rechecking` out; permitting the separator is what
keeps `check 4` in — the spelling the removed header sentence used. Modes `--corpus` and `--scan`
share this one object, so the corpus binds the recognizer the scan actually uses.
"""
import re, sys, yaml

NAMES_A_CHECK = re.compile(r"(?<![A-Za-z0-9_-])check[ \t_-]?[0-9]", re.IGNORECASE)

# Lifted from the real removed text and from live production strings in this repository, so the
# negatives are the confusable ones that ACTUALLY occur here rather than ones invented to be easy.
MUST_MATCH = [
    "Note that `check4` is expected to fail on every real pull request today",
    "so check 4 currently finds no election on any real pull request",
    "Check 4 was never evaluated at all",
    "CHECK 2 fires here",
    "the verdict stopped at check-1",
    "expect (check6) to be red",
    "check_3 is the live one",
]
MUST_NOT_MATCH = [
    "      - uses: actions/checkout@v7",
    "out=\"$(python scripts/check-claim-fence.py \"$@\" 2>&1)\" || rc=$?",
    "One of the substantive checks of the fencing design 6.3 refused this head",
    "they elect on the SAME opkey and pass checks 1, 2, 3 and 5 identically",
    "rechecking the verdict changes nothing",
    "a failed read is never a pass, and it is checked",
    "A check run is per-SHA, and branch protection consumes the last result",
    "if [[ \"$VERDICT\" =~ \\[(check[0-9]+)\\] ]]; then",
    "the gate reported a finding but its verdict names no check in `[checkN]` form",
    "name: fsgg-claim-fence",
]


def corpus():
    for text in MUST_MATCH:
        if not NAMES_A_CHECK.search(text):
            print("MUST MATCH but did not: %r" % text)
    for text in MUST_NOT_MATCH:
        found = NAMES_A_CHECK.search(text)
        if found:
            print("MUST NOT MATCH but matched %r in: %r" % (found.group(0), text))


def scan(path):
    """Every step's `name`, `run` and `env` values, as PARSED. Comments are deliberately out of
    scope — they are maintainer-facing, and this workflow's header explains the defect at length."""
    doc = yaml.safe_load(open(path, encoding="utf-8"))
    for job_id, job in (doc.get("jobs") or {}).items():
        if not isinstance(job, dict):
            continue
        for index, step in enumerate(job.get("steps") or []):
            if not isinstance(step, dict):
                continue
            surfaces = [("name", step.get("name")), ("run", step.get("run"))]
            surfaces += [("env." + str(k), v) for k, v in (step.get("env") or {}).items()]
            for where, value in surfaces:
                if value is None:
                    continue
                for line in str(value).splitlines():
                    found = NAMES_A_CHECK.search(line)
                    if found:
                        print(
                            "job `%s` step %d %s names a check literally (%r) in: %s"
                            % (job_id, index, where, found.group(0), line.strip())
                        )


if sys.argv[1] == "--corpus":
    corpus()
else:
    scan(sys.argv[2])
NAMEPY

CORPUS="$(python3 "$WORK/namescheck.py" --corpus)"
if [ -z "$CORPUS" ]; then
  ok "L7 the recognizer is bound in BOTH directions: 7 spellings it must catch, 10 lookalikes it must not"
else
  bad "L7 the recognizer is bound in BOTH directions: 7 spellings it must catch, 10 lookalikes it must not" "$CORPUS"
fi

names_check() { python3 "$WORK/namescheck.py" --scan "$1"; }

NAMED="$(names_check "$FLOW")"
if [ -z "$NAMED" ]; then
  ok "L8 no step name, run script or env value in this workflow names a check literally"
else
  bad "L8 no step name, run script or env value in this workflow names a check literally" "$NAMED"
fi

# THE HISTORICAL MUTATION. This is the exact sentence `.github#2719` was reopened for, put back where
# it was. If L8 does not catch it, L8 is decoration.
STALE_ECHO="            echo 'Note that \`check4\` is expected to fail on every real pull request today: nothing'"
python3 - "$FLOW" "$WORK/flow-stale.yml" "$STALE_ECHO" <<'STALEPY'
import sys
flow, out, line = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(flow, encoding="utf-8").read()
anchor = "            echo '```'\n"
if anchor not in text:
    sys.exit("fixture bug: the anchor this mutation inserts after is gone from the workflow")
open(out, "w", encoding="utf-8").write(text.replace(anchor, line + "\n" + anchor, 1))
STALEPY
if cmp -s "$FLOW" "$WORK/flow-stale.yml"; then
  bad "L9 MUTATION: re-inserting the removed \`check4\` prediction into a step's run block is caught" \
    "the mutation changed nothing, so this inversion tested nothing"
else
  case "$(names_check "$WORK/flow-stale.yml")" in
    *"names a check literally"*) ok "L9 MUTATION: re-inserting the removed \`check4\` prediction into a step's run block is caught" ;;
    *) bad "L9 MUTATION: re-inserting the removed \`check4\` prediction into a step's run block is caught" \
         "NOT CAUGHT — this is the escape shape itself, and it is the one this row was reopened for" ;;
  esac
fi

# A SECOND MUTATION FOR THE SAME SENTENCE, ON A SURFACE THAT IS DELIBERATELY OUT OF SCOPE. Same bytes,
# moved into a YAML comment: the scan must stay SILENT. Paired with L9 this pins the SURFACE as well
# as the recognizer, so the boundary is measured rather than assumed — and so nobody later widens the
# scan to comments and reds this workflow's own header for explaining the defect.
python3 - "$FLOW" "$WORK/flow-comment.yml" <<'CMTPY'
import sys
flow, out = sys.argv[1], sys.argv[2]
text = open(flow, encoding="utf-8").read()
anchor = "name: fsgg-claim-fence\n"
if anchor not in text:
    sys.exit("fixture bug: the workflow `name:` anchor is gone")
note = "# Note that `check4` is expected to fail on every real pull request today.\n"
open(out, "w", encoding="utf-8").write(text.replace(anchor, note + anchor, 1))
CMTPY
#
# The assertion is that the comment adds NO NEW violation, compared against the SAME scan of the
# unmutated file — not that the mutant scans clean absolutely. The difference is not pedantry: an
# absolute assertion here would also red whenever L8's own subject is dirty, so one defect would show
# up as two failures and this leg would be reporting on a fact it does not own.
NAMED_COMMENT="$(names_check "$WORK/flow-comment.yml")"
if cmp -s "$FLOW" "$WORK/flow-comment.yml"; then
  bad "L10 the scanned surface is the EXECUTED text: the same sentence in a YAML comment adds no violation" \
    "the mutation changed nothing, so this inversion tested nothing"
elif [ "$NAMED_COMMENT" = "$NAMED" ]; then
  ok "L10 the scanned surface is the EXECUTED text: the same sentence in a YAML comment adds no violation"
else
  bad "L10 the scanned surface is the EXECUTED text: the same sentence in a YAML comment adds no violation" \
    "the scan's surface is wider than this fixture documents. New violation(s):
$NAMED_COMMENT"
fi

echo
echo "=== M. THE PRODUCERS ARE REAL — field sets pinned, and check 4 REACHABLE by execution ==="
# `tests/receiver-validate/run.sh:18-25` states why this section has to exist, and states it about
# THIS gate: "a fixture that builds its own marker and then asserts the gate reads it proves only that
# the fixture and the gate agree — which is exactly how a four-field producer and a six-field gate
# coexist green." Every section above this one builds its own marker. Until `.github#2719` was
# reopened this file carried ZERO references to the production writer, and that is precisely how the
# unreachable-check-4 state survived a full round of independent review.
#
# THE RELATION IS CONTAINMENT, NEVER SET EQUALITY. Slice 6's own leg asked for equality and had to be
# corrected, because equality "reds the moment the producer becomes correct": the election producer
# legitimately writes a seventh field (`pr=`, its idempotence discriminator) that this gate does not
# require, and the gate ignores fields it does not require by design. Containment still catches BOTH
# failures this section exists for — a producer that STOPS writing a field the gate requires, and a
# gate that STARTS requiring a field no producer writes — and M6/M7 apply exactly those two mutations
# in exactly those two directions. M4 measures that the extra-field case is LIVE today, so the choice
# of containment is a measurement rather than a preference.
CLIENT_FS="$ROOT/src/FS.GG.Coord.Cli.Lifecycle/LiveHandlers.fs"
DELIVERY_FS="$ROOT/src/FS.GG.Coord.Cli.Lifecycle/DeliveryApplication.fs"

cat > "$WORK/fieldsets.py" <<'FIELDPY'
"""Emit {gate: {...}, producer: {...}} as JSON: what this gate REQUIRES and what production WRITES.

THE GATE'S HALF IS IMPORTED, NOT MATCHED. `REQUIRED_AUTH_FIELDS` and `REQUIRED_ELECTION_FIELDS` are
read from the loaded module object, so a text-matching mistake cannot quietly yield an empty set that
makes every containment assertion below trivially true.

THE PRODUCER'S HALF IS PARSED FROM F# SOURCE, because there is no way to import it here. Each parse
is required to be non-empty by M1/M2, which is what stops a regex that silently matched nothing from
satisfying containment for the wrong reason.
"""
import importlib.util, json, re, sys

GATE, CLIENT, DELIVERY, OUT = sys.argv[1:5]

spec = importlib.util.spec_from_file_location("check_claim_fence", GATE)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

FIELD = re.compile(r"(?<![A-Za-z0-9_])([A-Za-z][A-Za-z0-9_]*)=")


def template(path, binder):
    src = open(path, encoding="utf-8").read()
    match = re.search(r"let %s[^=]*=\s*\n\s*\$\"(?P<body>[^\"]*)\"" % binder, src)
    if not match:
        return None, []
    body = match.group("body")
    return body, FIELD.findall(body)


auth_body, auth_fields = template(CLIENT, "authorizationMarker")
election_body, election_fields = template(DELIVERY, "electionMarker")

json.dump(
    {
        "gate_auth": list(module.REQUIRED_AUTH_FIELDS),
        "gate_election": list(module.REQUIRED_ELECTION_FIELDS),
        "auth_body": auth_body,
        "auth_fields": auth_fields,
        "election_body": election_body,
        "election_fields": election_fields,
    },
    open(OUT, "w", encoding="utf-8"),
)
FIELDPY

FIELDS="$WORK/fieldsets.json"
python3 "$WORK/fieldsets.py" "$TOOL" "$CLIENT_FS" "$DELIVERY_FS" "$FIELDS"

# f <index-expression> [len] — read one fact out of the parsed field-set JSON. `len` asks for the
# LENGTH, because an EMPTY list still prints as the non-empty string `[]` and would satisfy a naive
# `-n` test — and emptiness is exactly what M1/M2 must be able to see.
f() {
  if [ "${2:-}" = len ]; then
    python3 -c "import json;print(len(json.load(open('$FIELDS'))$1))"
  else
    python3 -c "import json;print(json.load(open('$FIELDS'))$1)"
  fi
}

[ "$(f "['auth_body']")" != "None" ] && [ "$(f "['auth_fields']" len)" -gt 0 ] \
  && ok "M1 the AUTHORIZATION producer exists: LiveHandlers.fs carries an \`authorizationMarker\` this gate can be pinned to" \
  || bad "M1 LiveHandlers.fs has NO authorizationMarker — this gate reads a marker nothing writes, which is how slice 3 landed inert"

[ "$(f "['election_body']")" != "None" ] && [ "$(f "['election_fields']" len)" -gt 0 ] \
  && ok "M2 the ELECTION producer exists: DeliveryApplication.fs carries an \`electionMarker\` — check 4 has a subject" \
  || bad "M2 DeliveryApplication.fs has NO electionMarker — check 4 reads a marker nothing writes"

# containment <meta-json> <gate-key> <producer-key>
containment() {
  python3 - "$1" "$2" "$3" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
gate, producer = set(d[sys.argv[2]]), set(d[sys.argv[3]])
if not gate or not producer:
    sys.exit(1)
sys.exit(0 if gate <= producer else 1)
PY
}

show() { python3 -c "import json;d=json.load(open('$FIELDS'));print('gate requires:',d['$1']);print('producer writes:',d['$2'])"; }

containment "$FIELDS" gate_auth auth_fields \
  && ok "M3 every AUTHORIZATION field this gate requires is a field \`authorizationMarker\` writes (containment)" \
  || bad "M3 the gate requires an authorization field the producer does not write" "$(show gate_auth auth_fields)"

containment "$FIELDS" gate_election election_fields \
  && ok "M4 every ELECTION field this gate requires is a field \`electionMarker\` writes (containment)" \
  || bad "M4 the gate requires an election field the producer does not write" "$(show gate_election election_fields)"

python3 - "$FIELDS" <<'PY' && ok "M5 containment-not-equality is the LIVE case: the election producer writes a field this gate does not require" || bad "M5 no producer writes a field beyond the gate's required set, so nothing here measures why equality would be wrong" "$(show gate_election election_fields)"
import json, sys
d = json.load(open(sys.argv[1]))
sys.exit(0 if set(d["election_fields"]) - set(d["gate_election"]) else 1)
PY

# ---- MUTATIONS, one per direction containment exists to cover ----------------------------------
mutate_fields() {  # <label> <python-mutation-body> <gate-key> <producer-key>
  local label="$1" body="$2" gk="$3" pk="$4" out="$WORK/fields-mutant.json"
  python3 - "$FIELDS" "$out" "$gk" "$pk" <<PY
import json, sys
d = json.load(open(sys.argv[1]))
gate_key, producer_key = sys.argv[3], sys.argv[4]
$body
json.dump(d, open(sys.argv[2], "w"))
PY
  if cmp -s "$FIELDS" "$out"; then
    bad "$label" "the mutation changed nothing, so this inversion tested nothing"
  elif containment "$out" "$gk" "$pk"; then
    bad "$label" "NOT CAUGHT — containment still held, so the leg above asserts nothing" \
      "$(python3 -c "import json;d=json.load(open('$out'));print('gate requires:',d['$gk']);print('mutated producer writes:',d['$pk'])")"
  else
    ok "$label"
  fi
}

# Direction 1 — THE INERT-READER DIRECTION: the producer stops writing a field the gate requires. This
# is the exact regression that made check 4 unreachable, expressed as a mutation of the parsed facts.
mutate_fields \
  "M6 MUTATION: a producer that DROPS a gate-required authorization field reds M3 (the inert-reader direction)" \
  'd[producer_key] = [x for x in d[producer_key] if x != sorted(set(d[gate_key]))[0]]' \
  gate_auth auth_fields
mutate_fields \
  "M7 MUTATION: a producer that DROPS a gate-required election field reds M4" \
  'd[producer_key] = [x for x in d[producer_key] if x != sorted(set(d[gate_key]))[0]]' \
  gate_election election_fields
# Direction 2 — the gate starts requiring a field no producer writes. Containment must catch this too,
# or "containment rather than equality" would have been a relaxation instead of a re-statement.
mutate_fields \
  "M8 MUTATION: a gate that ADDS a required authorization field no producer writes reds M3" \
  'd[gate_key] = list(d[gate_key]) + ["nonce"]' \
  gate_auth auth_fields
mutate_fields \
  "M9 MUTATION: a gate that ADDS a required election field no producer writes reds M4" \
  'd[gate_key] = list(d[gate_key]) + ["nonce"]' \
  gate_election election_fields

# ------------------------------------------------------------------------------------------------
# M10/M11. THE REACHABILITY LEG — set comparison is not enough, so this one EXECUTES.
#
# M3 would still pass if the gate stopped at check 1 for some reason other than a missing field. What
# `.github#2719` was reopened for is a REACHABILITY claim, so it is measured by running the real gate
# against a marker instantiated from the producer's OWN PARSED TEMPLATE — not from this fixture's
# `auth()` helper, which is the very thing that cannot detect a producer/gate disagreement.
#
# M11 IS THE CONTROL, AND IT IS WHAT MAKES M10 MEAN ANYTHING: the same machinery, with the two fields
# `.github#2395` added removed again, must land on a DIFFERENT check — check 1, naming `opkey` and
# `grant`. A harness that answered "check4" whatever it was fed would fail M11.
# ------------------------------------------------------------------------------------------------
instantiate() {  # <template> <opkey> <grant> — the producer's own template, filled in, on stdout
  python3 - "$1" "$ITEM" "$GEN" "$2" "$3" "$HEAD" <<'PY'
import re, sys
tpl, item, gen, opkey, grant, head = sys.argv[1:7]
values = {"item": item, "gen": gen, "opkey": opkey, "grant": grant, "head": head}
missing = []


def one(match):
    name = match.group(1)
    if name not in values:
        missing.append(name)
        return "UNBOUND"
    return values[name]


out = re.sub(r"%[sd]\{(\w+)\}", one, tpl)
if missing:
    sys.exit("the producer template carries placeholder(s) this fixture has no value for: %s" % sorted(set(missing)))
print(out)
PY
}

#
# NEITHER LEG MAY ABORT THIS FIXTURE. Both build their subject from the LIVE producer template, so a
# real producer regression can make that construction fail — and under `set -e` a failed command
# substitution would kill the run before the summary line, turning "M3 and M10 red" into a truncated
# transcript with no verdict at all. Every construction below is therefore status-checked and
# converted into a NAMED `bad`, which is the same discipline `assert` applies to the gate's own
# output. Measured: removing `grant=` from `LiveHandlers.fs` aborted this fixture before it was fixed.
build_body() {  # <template> <opkey> <grant> -> BODY_FILE, or empty on a construction failure
  local tpl="$1" key="$2" grant="$3" text
  BODY_ERR=""
  BODY_FILE=""
  if ! text="$(instantiate "$tpl" "$key" "$grant" 2>&1)"; then
    BODY_ERR="$text"
    return 0
  fi
  BODY_FILE="$(printf '%s\n' "$text" | body_file)"
}

AUTH_TPL="$(f "['auth_body']")"
W="$(new_world)"
claim_comment "$W" "$GEN" 0
build_body "$AUTH_TPL" "$OPKEY" 9999
if [ -z "$BODY_FILE" ]; then
  bad "M10 the PRODUCER's own marker template reaches CHECK 4 — the reachability this row was reopened for" \
    "the producer's template could not be instantiated, so nothing was measured:
$BODY_ERR"
else
  gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$BODY_FILE"
  assert "M10 the PRODUCER's own marker template reaches CHECK 4 — the reachability this row was reopened for" 1 \
    "[check4]" "no \`fsgg:merge-election\` marker on $ITEM bears"
fi

# The pre-`.github#2395` producer, rebuilt from THIS template by deleting exactly the two fields that
# landing added. Same world, same helper, different check.
M11="M11 CONTROL: the four-field producer that shipped before .github#2395 stops at CHECK 1, naming both fields"
set +e
OLD_TPL="$(python3 - "$AUTH_TPL" 2>&1 <<'OLDPY'
import re, sys
# Delete exactly the two fields `.github#2395` added, from TODAY's template — so this control is
# rebuilt from the current producer rather than from a re-typed copy of the old one. The count is
# asserted: a template whose shape moved makes this a NAMED fixture failure, never a silent no-op.
out, n = re.subn(r" (?:opkey|grant)=%[sd]\{\w+\}", "", sys.argv[1])
if n != 2:
    sys.exit("expected to delete exactly 2 fields from the producer template, deleted %d" % n)
print(out)
OLDPY
)"
OLD_RC=$?
set -e
if [ "$OLD_RC" != 0 ]; then
  bad "$M11" "the pre-#2395 control could not be rebuilt from the live template, so nothing was measured:
$OLD_TPL"
else
  build_body "$OLD_TPL" "$OPKEY" 9999
  if [ -z "$BODY_FILE" ]; then
    bad "$M11" "the control template could not be instantiated, so nothing was measured:
$BODY_ERR"
  else
    gate "$W" --repo "$REPO" --head-ref "$BRANCH" --head-sha "$HEAD" --body "$BODY_FILE"
    assert "$M11" 1 "[check1]" "missing required field(s): opkey, grant"
  fi
fi

echo
echo "----------------------------------------------------------------------"
echo "claim-fence fixture: $pass passed, $failcount failed"
[ "$failcount" -eq 0 ] || exit 1
