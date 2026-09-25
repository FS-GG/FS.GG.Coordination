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
