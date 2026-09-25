# GS2-09.7 Q5 native activity identity precursor

Status: **source-only negative control; no nine-authority result, Q5/Q6
receipt or admission**.

`MigrationNativeActivity.reconcile` already requires exact issue/PR number
populations and a stream per censused subject. An independently written
control showed it still accepted one issue and one PR with the same native
node ID when each stream agreed with its local record. The test was red
before the repair. Reconciliation now refuses blank or duplicate node IDs
across the combined issue/PR census before a native activity digest can be
formed. This prevents one native identity from occupying two subject slots.

A second independent control showed the same node ID could occupy a censused
subject slot and an event slot, or an activity slot could have a blank node
ID. That control was red before the follow-up repair. Reconciliation now
checks uniqueness across census and activity records together and refuses
blank activity node IDs. This remains a typed-capture consistency check;
it does not prove that the typed records were parsed from provider bytes.

A third independent control showed that a self-consistent event stream page
from another repository could pass typed reconciliation. It was red before
the repair. Reconciliation now derives the repository path and API origin from
the first canonical issue-census page and requires every issue, PR and stream
page to remain on the corresponding repository and subject path. Controlled
cases refuse a foreign repository, foreign host and wrong issue number. This
checks request scope within the captured input; it cannot independently
prove the initial census belongs to an admitted sandbox or that page hashes
match retained raw response bytes. The provider reader and future
raw-to-typed inspect adapter still owe those proofs.

A fourth independent control found that the scoped typed capture still
accepted `per_page=1`, a skipped continuation page number and a census query
for `state=open`. It was red before the repair. Reconciliation now applies
the native reader's exact query shape to each census and stream page:
`state=all&per_page=100` for issue/PR censuses, `per_page=100` for streams,
and the exact page ordinal for continuations. It refuses duplicate query keys
and a first-page cursor. This prevents an undersized or filtered page from
masquerading as the complete typed capture; retained provider bytes and
initial sandbox identity are still separate authority obligations.

A fifth independent control showed that two event rows with different node
IDs but the same native database ID, or an issue and PR comment with the same
comment database ID, could pass reconciliation. It was red before the repair.
Reconciliation now requires unique database IDs within each native activity
record kind, combining issue and PR comments because both use the issue
comment endpoint and record type. This prevents one record from occupying two
typed activity slots. It still cannot prove that the database IDs and
payloads came from retained provider responses.

A sixth independent control reached the native issue reader itself: it
accepted an `unknown` issue state as a typed censused subject, while the PR
reader already refused unknown states. The control was red before the repair.
`readIssues` now accepts only `open` or `closed` issue state from the
[repository issues endpoint](https://docs.github.com/en/rest/issues/issues#list-repository-issues),
before it creates subject evidence. This is one provider-parser field check;
the complete nine-authority raw-to-typed inspect adapter and protected
claim/receipt correspondence remain absent.

A seventh independent raw-parser control showed that an issue-list object
with duplicate `state` or `pull_request` members could still be classified
using one value while retaining ambiguous raw bytes. It was red before the
repair. `readIssues` now refuses duplicate members on every raw list item
before parsing an issue or counting a PR marker. This prevents ambiguous
raw JSON from contributing to the issue/PR census; it does not supply the
missing complete raw-to-typed inspect authority.

An eighth independent control found the PR-list reader could classify a raw
PR with duplicate top-level `state` or nested `head.sha`/`base.repo.id`
members. It was red before the repair. `readPullRequests` now refuses
duplicate members at the root and in the consumed `head`, `base` and
`base.repo` objects before producing typed PR evidence. This prevents a
single raw PR from presenting two revisions or two base repository IDs.

A ninth independent control found the PR review reader could accept duplicate
raw `state`, `commit_id` or `user.login` members and still produce typed
review evidence. It was red before the repair. `readPullRequestReviews` now
refuses duplicate members at the review root and in the consumed `user`
object before parsing a review. This is a partial raw parser control only.

A tenth independent control found the issue event reader could accept
duplicate raw `event`, `created_at` or `actor.login` members and produce
typed event evidence. It was red before the repair. `readIssueEvents` now
refuses duplicate members at the event root and in the consumed `actor`
object before parsing an event. This remains partial native activity evidence.

An eleventh independent control found the shared issue/PR comment reader
could accept duplicate raw `issue_url`, `body` or `user.login` members and
produce typed comment evidence. It was red before the repair.
`readSubjectComments` now refuses duplicate members at the comment root and
in the consumed `user` object before parsing a comment. Claim text remains
partial evidence until the protected journal and custom receipts are read.

A twelfth independent control found the inline review comment reader could
accept duplicate raw `pull_request_review_id`, `path` or `body` members and
produce typed comment evidence. It was red before the repair.
`readPullRequestReviewComments` now refuses duplicate root members before
parsing an inline review comment. This remains partial native activity evidence.

A thirteenth independent control found the native relation reader could
accept duplicate raw repository IDs on an initial GraphQL response and
duplicate connection counts on a continuation. It was red before the repair.
Both relation response paths now reject duplicate JSON members recursively
before interpreting node, endpoint, connection or page facts. This does not
provide the missing protected native observer or complete typed inspect proof.

A fourteenth independent control found the issue type GraphQL reader could
accept duplicate raw type names or `hasNextPage` values into a typed census.
It was red before the repair. The issue type reader now shares the recursive
GraphQL member check with native relation reads before interpreting type or
pagination facts. Complete native inspect authority remains outstanding.

A fifteenth independent control found the project item GraphQL reader could
accept duplicate raw content `__typename`, repository `databaseId` or
`hasNextPage` members into typed inventory. It was red before the repair.
`readProjectItems` now checks recursive member uniqueness before interpreting
item identity, content or pagination. This remains partial project evidence.

A sixteenth independent control found the project field GraphQL reader could
accept duplicate raw `dataType`, option `name` or `hasNextPage` members into
typed schema evidence. It was red before the repair. `readProjectFields` now
checks recursive member uniqueness before interpreting fields, options or
pagination. This remains partial project authority evidence.

A seventeenth independent control found the project value GraphQL reader
could accept duplicate raw field IDs, selected `optionId` or nested
`hasNextPage` members into typed values. It was red before the repair.
`readProjectValues` now checks recursive member uniqueness before
interpreting field identity, value content or nested pagination. Protected
native readback and complete project authority remain outstanding.

An eighteenth independent control found the shared repository identity read
could accept duplicate raw `id` members, while the core settings read could
accept duplicate `default_branch` members. It was red before the repair.
Both reads now refuse duplicate root members before accepting repository
scope or core settings. This does not establish protected native provenance.

A nineteenth independent control found repository ruleset detail parsing
could accept duplicate raw `conditions.ref_name` members, choosing one branch
or tag target set. It was red before the repair. `rulesetConditions` now
refuses duplicate members in `conditions` before parsing `ref_name` itself.
This remains partial settings readback, not complete native authority.

A twentieth independent control found a repository ruleset rule could retain
ambiguous nested `parameters.required_status_checks[].context` members while
only its parameter root was checked. It was red before the repair.
`rulesetRule` now refuses duplicate members recursively within `parameters`
before retaining their raw JSON. Native installed ruleset proof remains held.

A twenty-first independent control found the issue census discarded the
numbers of PR markers and retained only their count. A PR list with the same
count but a different subject was accepted before the repair. The issue
census now retains unique marker numbers; PR census and native activity
reconciliation require the exact set. A second red-before control found the
raw-to-typed issue adapter did not bind that new set; it now compares marker
numbers from captured raw pages. This is still a source precursor: initial
sandbox census, protected native observer and full inspect authority remain
unproven.

A twenty-second independent control found the raw-to-typed issue adapter
could accept a captured page with duplicate `pull_request` members after
the page digest was supplied. It was red before the repair. The adapter now
refuses duplicate JSON members recursively before extracting issue rows or
PR marker numbers. This does not establish candidate-inaccessible capture.

A twenty-third independent control found the raw-to-typed project adapter
could accept captured item and field pages with duplicate `hasNextPage`
members while the typed population stayed unchanged. Both controls were red
before the repair. The adapter now checks recursive JSON member uniqueness
before interpreting project item, field or value pages. This remains a
source-only precursor; captured-page provenance and complete native inspect
authority are unproven.

The native activity capture remains a precursor only. The
`claim-and-event-streams` Q5 authority still lacks protected claim journal,
custom receipt, exact scope and raw-to-typed adapter proof. Its current
inspect adapter continues to return `authority-adapter-unavailable`; the
other missing nine-authority rows are not supplied by this change.

Read-only GitHub status shows [FS-GG/.github #3690](https://github.com/FS-GG/.github/pull/3690)
merged at `2026-09-25T05:35:26Z` as
`ff425734d277fa54c3d71601da90fe7b22619c15`. The sandbox-route owner
reported native merge before the OperatingV1 effect-admission stop. This
process violation awaits protected-owner adjudication; the merge is not an
OperatingV1 admission, sandbox qualification or Q5/Q6 clearance. No provider
effect, receiver pin, Authority write, protected merge or cutover was made
by this source-only work.
