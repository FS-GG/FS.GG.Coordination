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
