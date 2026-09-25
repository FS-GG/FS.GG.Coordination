# GS2-09.7 protected rehearsal decision packet

Status: **hold; no sandbox dispatch**. This packet reviews source and specifies the evidence needed for a later protected decision. It is not a run approval, Q5/Q6 result, acceptance receipt, or authority to use the candidate's token.

## Reviewed objects

| Object | Exact identity | Observation |
| --- | --- | --- |
| Protected `.github` Q4 workflow from [#3690](https://github.com/FS-GG/.github/pull/3690) | Merge `ff425734d277fa54c3d71601da90fe7b22619c15`; workflow SHA-256 `292052ea238080458cd73dc6a0f34688a56caf39c2d3738e05d9a863f8625e66` | Source is merged, but its V1 delivery was recorded as an unadmitted exception. Its presence is not an admitted protected effect or a completed rehearsal. |
| Coordination [#552](https://github.com/FS-GG/FS.GG.Coordination/pull/552) | Draft head `5f5110ccd69006896390a59531f7ab188f845cc5`, base `b8225f6b63f764f58ec20735d3cf849b37c57276` | Candidate checks the sanitized mint proof before its existing live Q4 routes. It has no installed nine-authority migration interpreter or copy-specific Q5/Q6 command. |
| [Rehearsal source plan](../../docs/roadmaps/gs2-09-7-representative-rehearsal.md) | Same #552 head | Defines the two rounds, interruption cuts, archive, rollback, and native readback obligations; none is a completed provider report. |

The workflow digest above was computed from the exact file at the #3690 merge commit and matches its V1 writer-census entry. A future dispatch must re-read the workflow at the *actual protected run revision* and bind that revision in the run packet. The #3690 merge observation must receive its own admission decision; this packet cannot repair that history.

## Boundary readback

| Boundary | Present in reviewed source | Required before a GS2-09.7 candidate call |
| --- | --- | --- |
| Host and candidate | The workflow requires `refs/heads/main`, checks out `.github` at `github.sha` before App secret use, checks out Coordination at the 40-hex input, and verifies both checkout heads. Its nonce includes run ID, attempt, and candidate SHA. | Retain the actual workflow SHA, run ID/attempt, environment decision, candidate SHA, nonce, and checkout observations as one protected run identity. Reject a changed candidate or rerun as a new decision. |
| App credential | The protected mint script uses the App private key, checks App ID/slug, installation owner, exactly one selected repository (ID `1353050537`, node `R_kgDOUKXpqQ`), required grants, expiry, and the App actor. It emits a sanitized proof and passes a scoped token to the candidate. The candidate sees the token and proof, but not the App key. | Bind the provider mint response, actor readback, selected repository and *effective* grants to this exact run. Retain only digests and sanitized proof; never retain token or private key. The candidate-side token hash check is a consistency check, not authorization. |
| Target | The workflow reads repository and Project 2 node `PVT_kwDOEYAWY84BiESo` before Q4 execution. The repository token selection is narrow; `organization_projects:write` is broader than Project 2. | Pin the exact repository and Project identities at the protected host and at the installed migration interpreter before any effect. A project mismatch, foreign item, or unavailable native read must stop before dispatch. |
| Mint handoff | The workflow's `preflight.json` records candidate SHA and nonce; `mint-grants.json` records App, repository, grants, expiry, and token/mint/viewer hashes. The workflow compares the token hash, repository node, and Projects grant before Q4 execution. | Add a protected host decision that joins proof digest, token digest, preflight, run ID/attempt, workflow SHA, candidate SHA, nonce, target, and expiry. A proof/token pair reused with another run or candidate must fail **at the host** before the candidate's first provider call. |
| Cleanup | The Q4 workflow runs cleanup, revokes a minted token, uploads evidence, and requires execute/cleanup/revoke success in its final verdict. | Retain native cleanup readback, revocation response/verdict, and any pending or indeterminate outcome. Do not infer cleanup from a script exit or a 2xx effect response alone. |

The reviewed candidate's offline fake-token control already shows that a valid proof/token pair can be replayed after changing candidate SHA and nonce. The protected workflow's current preflight and proof files are separate; it does not make their cross-run association an authenticated candidate input. An in-repository negative control cannot create that protected authority. A new protected host binding and its independent wrong-run control must be reviewed before dispatch.

## Q5/Q6 decision inputs

The first possible protected rehearsal decision requires all of the following, tied to **one** candidate and run identity:

1. An admitted protected workflow route, reviewed exact candidate and installed migration command, selected sandbox repository/Project, App grant/readback and host-generated run binding as above. The Q4 command in #3690 is not a migration command.
2. A copy-specific Q5 inspect artifact from two complete, stable provider passes over all nine named discovery authorities, including raw page chains, source and target heads, high-water marks, source bytes, fresh discovery, immutable manifest, transforms and operation dispositions. A newly added source subject, unknown Project item, changed head, missing page, or unavailable authority must refuse before any effect.
3. A Q5 execution artifact for each closed typed effect: durable intent before dispatch, exact expected revision and journal generation, request identity/count, response classification, independent native poststate, and a settled receipt. Retain archive records, lookup index, verifier result, first round, rollback-restored state, and a distinct second round with fresh reads and compared final state and receipt counts.
4. A Q6 artifact for every interruption cut and each of the five reverse-order rollback domains. It must show fresh-process recovery, lost-response reconciliation through native readback, no second dispatch for an applied effect, zero-write settled replay, chained rollback receipts, restored epoch/state, and refusal after `OpenV2`.
5. Independently authored black-box controls against the *installed* command, exact run/artifact hashes, zero unauthorized effects on refusal, cleanup native readback, and token revocation. A source test, Q4 closure, or synthetic report cannot satisfy these controls.

**Decision at this reviewed state: hold.** #552 has the candidate proof guard and a detailed source plan, while #3690 supplies an unadmitted Q4 mint handoff. The installed nine-authority capture, migration interpreter, protected run binding, Q5/Q6 native report, independent controls, and GS2-09.7 acceptance receipt are absent. No sandbox call, provider mutation, protected merge, receipt, or cutover is part of this packet.
