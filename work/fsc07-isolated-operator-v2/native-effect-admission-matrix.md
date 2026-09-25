# GS2-09.9 runnable native-effect admission matrix

Status: read-only owner decision input, 2026-09-25. This matrix records what a protected owner must select and independently read back before considering a **new runnable** isolated v2 artifact. It is neither approval to enable a workflow nor approval for the [#550 one-POST plan](https://github.com/FS-GG/FS.GG.Coordination/pull/550). No protected selection, credential, grant, journal generation or provider result is supplied here.

## Current source ceiling

The [closed scaffold](closed-effect-scaffold.md) is pinned to archive SHA-256 `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19` and manifest-file SHA-256 `2d2d450b23d12ebcb83371c69124060dfa23fef03efcf03072d671a144da0b6f`. Its [entry](../../eng/callable_isolated_v2_effect_entry.py) exits 78. Its [gate](../../eng/callable_isolated_v2_effect_closed.py) calls no token, CAS or provider port, even when given grant bytes. The provisional [native operator](../../eng/callable-cli-isolated-operation-v2.py) is an external pinned source input, absent from the archive. The [producer proposal](../../.github/workflows/callable-isolated-v2-effect-release.yml) and [execution proposal](../../.github/workflows/callable-isolated-v2-execute.yml) each retain an `if: ${{ false }}` job gate and an exit-78 step. The distinct [#563 inspect-only workflow](https://github.com/FS-GG/FS.GG.Coordination/pull/563) also remains disabled.

Draft [#723](https://github.com/FS-GG/FS.GG.Coordination/pull/723) at `fb3df0645d60759f5bc8c056bc4f98af5e27f178` is a source candidate, not an integrated protected commit. Its fake [release preflight](../../eng/callable_isolated_v2_effect_release_preflight.py), [Git-tree witness](../../eng/callable_isolated_v2_effect_git_tree_witness.py), [producer/artifact witness](../../eng/callable_isolated_v2_effect_producer_artifact.py) and [runner readback](../../eng/callable_isolated_v2_effect_runner_readback.py) compare exact objects but do not authenticate a protected observer. None returns dispatch authority. The [custody decision](native-custody-decision.md) identifies the separate target, execution App, journal, grant and result owners.

## Owner-selected read-only release record

A release owner must produce one immutable record with every cell below, and a different protected reviewer must independently attest the exact record and event time. Empty, candidate-supplied or mutable coordinates cannot satisfy a cell. The selected artifact must be a **new reviewed runnable revision**; the closed scaffold digest above and the #555 inspect-only digest must refuse in the runnable slot.

| Coordinate to select | Independent evidence required | Current status |
| --- | --- | --- |
| Coordination source | Repository numeric ID; full name `FS-GG/FS.GG.Coordination`; integrated commit and tree object IDs; exact Git blob IDs and bytes for builder, manifest, native source, source controls and both workflow files. | No integrated runnable revision or protected Git reader event selected. |
| Producer workflow | Exact workflow path, immutable blob digest and workflow ID at that commit; protected environment; disabled-gate change separately reviewed; producer principal, run ID and attempt, head commit/tree, completion and UTC time. | Existing proposal is disabled and produces only a closed scaffold. No run exists. |
| Runnable archive | Artifact ID, run association, immutable download object and bundle digest; exact archive, member/source hashes, executable entry and independently approved manifest digest. | Current archive has no executable native member or effect port. No runnable artifact exists. |
| Release review | Reviewer actor ID distinct from producer and dispatch actor; active membership; immutable approval event ID, timestamp, expiry and exact source/workflow/artifact/manifest binding; separate approval reader principal and credential. | No protected event or reviewer selected. |
| Runner and installed observer | Immutable image reference and attestation digest; interpreter and runtime-closure digests; absolute installed path, device/inode/size and before/after archive hashes; distinct observer actor and durable audit-event ID/time. | No protected runner, installed artifact or independent audit. |

The source, producer, reviewer, runner and audit readers must report their own authenticated principal, credential scope, immutable object/event ID and UTC observation time. A `scope()` dictionary from a fake port or a candidate's digest is only a consistency input. A protected owner must verify that each read came from the selected provider object and that producer, reviewer, dispatch actor, grant issuer and independent audit actor are distinct where their roles require separation.

## Refusal controls before an execution decision

Run these controls at the eventual installed entry with independent counters. Each row mutates one selected coordinate while all other independently read values stay fixed. The first four rows must finish with **zero token reads, zero journal reads and writes, zero CAS, zero provider POST or PUT, and zero cleanup attempts**. The observer must read back the counters outside the candidate process and retain exact installed archive/path/runtime observations.

| Negative control | Required result |
| --- | --- |
| No grant; grant bytes through an alternate entry; closed or inspect-only archive substituted for the runnable digest. | Exit/refusal before any authority port. No fallback native module import or provider transport. |
| Change integrated commit/tree, workflow blob, producer run attempt or actor, artifact ID/bundle/archive/member bytes, approved manifest digest, image, interpreter or runtime closure. | Refuse on the single mismatch. A newly computed candidate digest does not repair the independent selection. |
| Omit or swap the independent reviewer, approval event, source reader, runner or audit event; reuse producer/dispatch/grant-issuer identity as reviewer; expire or replay a selected event. | Refuse before token or CAS; retain authenticated event ID, time and reader custody evidence. |
| Change selected target repository ID or node ID, installation ID, source/base ref or SHA, or broaden the effective App repository set/permissions. | Refuse before token or CAS. The installed job has no setup or cleanup credential. |
| Lose a protected CAS acknowledgment, change its parent generation/head, or present only the writer's own journal readback. | **Zero POST**. An independent fresh-process reader must treat the ambiguous attempt as spent until separately resolved. |
| Lose the sole POST response or receive 500; substitute stale head/base, foreign repository, incomplete pagination or changed protection state during native readback. | At most one counted POST across processes. The result stays Unknown unless two complete native snapshots prove the exact single poststate; no retry or cleanup follows. |

The last two rows are future protected one-attempt acceptance controls, not permission to run them from this packet. The [native v2 source plan](plan.md) defines complete PR and branch-protection readback, while the [custody decision](native-custody-decision.md) states the selected execution App, durable CAS and separate native observer requirements. Offline transport callbacks and local SQLite do not satisfy these rows.

## Decisions in order

1. **Source integration and release selection:** a Coordination owner proposes a separate runnable revision and reviewed producer/execution workflows, then selects the immutable source, producer, artifact, reviewer and runner coordinates above. A protected reviewer attests that exact selection. Until a separate owner action changes the workflow gates, both proposals stay disabled. This packet cannot name an integrated head or approval event because neither exists.
2. **Installed refusal admission:** after separately authorized protected release and installation, independent observers prove the exact selected bytes/runtime and the first four zero-effect controls. This admits only the installed no-grant boundary; it does not issue a token or grant.
3. **Native operation authorization:** a later, separate owner decision selects a disposable target, effective one-repository App credential, complete native prestate, protected journal generation and independent CAS readback, exact run/attempt and one-use grant. Only then may the #550 one-POST window be considered. Lost CAS or POST acknowledgment never authorizes a repeat.

The smallest missing protected action now is an owner selection of a **reviewed integrated runnable source/workflow proposal** and a distinct release reviewer for its immutable coordinates. Implementing protected token, CAS or HTTP ports before that selection would have no approved artifact or target to bind. #545 remains disputed; #550, the typed gate/index, receiver pin, Authority boundary and cutover remain held.
