# GS2-09.7 protected census owner handoff

Status: **read-only decision packet**. No initial sandbox census, protected
option pin, native byte custody, nine-authority inspect result, Q5/Q6 receipt
or effect admission is established here.

## Observed boundary

At the 2026-09-25 read, FS-GG/.github `main` was
`2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4`. Its
[`github-substrate-v2-sandbox-qualification.yml`](https://github.com/FS-GG/.github/blob/2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4/.github/workflows/github-substrate-v2-sandbox-qualification.yml)
had Git blob `581f2d3a807c2e1f48aa9aef88679f7d36f34338`. The workflow accepts a
candidate SHA, derives a run/attempt/candidate nonce, mints an App grant,
checks the registered Q4 sandbox identity, then passes the token and mint
proof to the candidate's `execute` and `cleanup` scripts. Its visible steps
contain no OperatingV1 effect-admission read before that handoff. [PR
#3690](https://github.com/FS-GG/.github/pull/3690) merged at
`ff425734d277fa54c3d71601da90fe7b22619c15` before the reported
OperatingV1 admission stop. The protected owner must adjudicate that process
violation; the merge does not admit a Q5/Q6 run.

The workflow selects `FS-GG/FS.GG.GitHub.Substrate.Sandbox` (numeric repository
ID `1353050537`, node `R_kgDOUKXpqQ`) and Project 2. The
[representative-rehearsal assessment](gs2-09-7-representative-rehearsal.md)
identifies this as an earlier diagnostic sandbox, **not** a declared GS2-09.7
migration copy. The workflow's Q4 grant proof therefore cannot establish the
copy-specific Q5 target or authorize candidate provider effects.

Coordination draft [#725](https://github.com/FS-GG/FS.GG.Coordination/pull/725)
requires `MigrationNativeActivity.reconcile` to match its initial issue-page
origin, owner, repository and numeric ID to caller-supplied
`MigrationGitHubReadOptions`. That source check rejects a coherent foreign
page set. It cannot authenticate who chose those options or whether the page
digests came from retained native responses. The
[inspect adapter](../../src/FS.GG.Coordination.Cli/MigrationInspectProviderAdapter.fsi)
still returns `authority-adapter-unavailable` for five of the nine required
authorities; the [rehearsal assessment](gs2-09-7-representative-rehearsal.md)
lists the missing copy-specific work.

## Protected facts required before qualification

| Owner boundary | Evidence the protected owner must supply | Refusal when absent |
| --- | --- | --- |
| OperatingV1 admission | Adjudicated #3690 incident and an authoritative, current decision for the exact workflow blob, candidate commit, run ID, attempt, nonce, target and effect scope. | A merged workflow or self-asserted candidate SHA is not admission. |
| Sandbox selection | Independently read numeric and node repository IDs, owner/name, private status, Project identity, isolation, initial inventory and epoch for the **declared migration copy**. | The registered Q4 diagnostic sandbox or an unowned/unknown subject cannot stand in for the copy. |
| Protected option pin | Candidate-inaccessible readback of the selected API origin, owner, repository and numeric ID, bound to that admitted run and immutable candidate. | A caller-supplied `MigrationGitHubReadOptions` record remains source input only. |
| Native byte custody | Candidate-inaccessible reader/store identities, principals, ACLs and artifact digests; each exact request, response status, headers, raw body, provider identity, page link and immutable storage object ID tied to one read ordinal. | A typed record, page hash or candidate-written file cannot prove native origin, completeness or freshness. |
| Journal and Q5/Q6 readback | Protected claim/operation journal and custom receipts joined to the nine-authority two-pass census; six interruption cuts, fresh-process recovery and native readback for five rollback domains. | Partial native activity and source-only fake ports cannot issue a Q5/Q6 receipt. |

The [Q6 protected native custody port](gs2-09-7-q6-protected-native-custody-port.md)
already states the separate reader, canonicalizer, store and signer installation
requirements for rollback. No corresponding installed Q5 census custody or
protected option-pin readback is evidenced by this packet.

## No-effect qualification controls to retain

An installed protected route must refuse a missing or self-asserted admission;
foreign or stale workflow/candidate/run/attempt/nonce/target pins; changed
reader/store identity or candidate-writable custody; omitted, duplicated or
substituted raw pages; a page hash supplied without matching retained bytes;
partial pagination; and lost-result or crash recovery that reuses an
unresolved read or effect. It must show the exact protected readback and
receipt for every accepted authority and rollback domain. These are required
future controls, not tests passed by this document or draft #725.

Until those protected facts are installed and independently read back, the
Q5/Q6 gate, sandbox dispatch, App token handoff, receiver pin, protected merge,
Authority write, receipt and cutover remain held.
