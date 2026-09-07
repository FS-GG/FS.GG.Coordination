---
feedbackSchema: 2
date: 2026-09-07
workspace: FS.GG.Coordination
cycle: roadmap-github-substrate-v2-m7-gs2-07-6-queue-sandbox-pilot
lane: github-substrate-v2
toolVersion: n/a
commit: pending-exact-candidate
---

## §1 Provenance and confidence

- **activation:** active
- **phases:** onboarding-first-build, lifecycle-authoring, implementation-test-evidence, verify-ship-pr
- **material events:** 0
- **zero-event reason:** `fs-gg-feedback-report` is not materialized in this tree (see [.github#2366](https://github.com/FS-GG/.github/issues/2366)); all four phases were exercised and no substitute checkpoint tool was used.

This report covers issue #320's recovered lifecycle, SDD, pure qualification
contract, bounded hosted merge-queue pilot, rollback, and candidate evidence.
Confidence is bound to the retained GitHub run URLs and exact readback digests.

## §2 What worked

The dedicated sandbox supported merge queue rulesets after a bounded public
transition. Exact candidate checks admitted PR #11, the first merge-group
observed an intentionally failed growth step and interruption, and a forward
base movement plus grown required-check inventory produced a different green
merge-group head. The final private settings, branch, and active-workflow
digests exactly match prestate.

The pure contract separates candidate and merge-group heads and binds the base
observation, canonical required checks, authority snapshots, expiry, sealed
resume, deterministic retry, reverse compensation, and cleanup readback.

## §3 What did not

GitHub visibility changes are asynchronous. The first immediate rollback
received HTTP 422 while the transition was in progress; retry succeeded. Some
public transitions briefly reported the repository locked, so the final harness
polls provider readiness and retries ruleset creation while retaining cleanup.

Early hosted retries also exposed stale workflow-run selection when temporary
branch names were reused. The final harness binds both merge-group observations
to the exact provider-generated queue ref containing the current PR number and
current base SHA. A disabled historical workflow registry row also required an
explicit enable before rerun and disable after cleanup.

The first full solution run was diagnostic because the supply-chain
reproducibility guard correctly refuses a dirty checkout. The clean committed
candidate rerun passed.

## §4 Findings

No board finding is filed for the provider's asynchronous transition or
historical Actions registry behavior: both are handled within the registered
sandbox recovery contract and are exercised by the retained harness. The absent
feedback skill remains deduplicated to `.github#2366`.

## §5 Did not exercise

No fleet repository, production repository, release, package publication,
stable channel, or successor-unit mutation was exercised. The sandbox PR was
closed unmerged. This is a non-game unit.

## §6 Doc-versus-behavior contradictions

The feedback contract expects `fs-gg-feedback-report`, while this partial
Coordination materialization omits it. `.github#2366` owns that contradiction.
GitHub also retains a historical workflow registry row after all containing refs
are deleted; the row is disabled and the file is absent from every live branch.

## §7 Workarounds still in the tree

The hosted harness explicitly polls asynchronous provider transitions, binds
run selection to exact generated queue refs, and disables the historical
workflow registration during cleanup. These are provider-facing recovery
controls, not production product workarounds.

## §8 Friction and avoidable cost

Repeated public/private transitions were needed while discovering the locked
propagation window and historical-run selection hazard. A future sandbox pilot
fixture could allocate unique branch names per attempt and treat Actions
workflow registration as a separately cleaned provider resource from the start.

## §9 Skill value and gaps

`github-substrate-v2-work`, `work-roadmap`, `pnext-item`, and the SDD lifecycle
kept the exercise pinned to one repository, one accepted predecessor, exact
hosted heads, mandatory rollback, and independent critique. The absent feedback
skill is the only materialization gap.

## §10 Outcome markers

- Implementation issue: [#320](https://github.com/FS-GG/FS.GG.Coordination/issues/320).
- Exact sandbox candidate: `3daa72357df81d682951f05e6c9bcbfcb7faad06`.
- Pull-request run: [34095773393](https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox/actions/runs/34095773393).
- Interrupted merge-group run: [34095815108](https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox/actions/runs/34095815108).
- Recovered merge-group run: [34095865706](https://github.com/FS-GG/FS.GG.GitHub.Substrate.Sandbox/actions/runs/34095865706).
- Typed hosted artifact: `10008553381`, content SHA-256 `3e16a87564139ecf3debb3e509cd061b8df7311d5ca74b07312045919b311df7`.
- Successful dispositions: `queue-pilot-qualified`, `queue-sandbox-recovered`.
- Controls: 24 Q4 and 20 Q6, each generated and independently authored.

## §11 Falsifiable improvements

The Coordination scaffold should materialize the feedback reporter and validator
so a future hosted cycle records event checkpoints directly. `.github#2366` is
complete only when this exact zero-event exception is unnecessary.

The hosted harness would become simpler if the provider exposed a terminal
visibility-transition status and a deletion endpoint for historical workflow
registrations; until then, exact readiness polling and disabled-row readback are
required and testable.

## §12 Development-surface coverage

| Surface | Status | Evidence and result |
|---|---|---|
| onboarding-guidance | exercised | Exact registered roadmap, predecessor, recovery claim, and touch set. |
| skills | partial | Roadmap, substrate, pnext, and SDD skills exercised; feedback skill absent. |
| sdd-authoring | exercised | Five real obligations observed; verificationReady and shipReady. |
| implementation-apis | exercised | Additive pure sealed queue/recovery contract. |
| dependencies-build | exercised | Warning-free Release build and clean candidate reproducibility. |
| testing | exercised | Full unit/architecture, Q4/Q6, hosted, negative controls, and inversion. |
| evidence | exercised | Exact provider prestate, run/job URLs, rollback digests, and TRX receipt. |
| runtime-playtest | not-exercised | Non-game unit. |
| performance | not-exercised | Bounded provider polling and pure contract. |
| documentation | exercised | Architecture, SDD, feedback, and retained hosted evidence. |
| packaging-upgrade | not-exercised | No publication or deployment authority. |
| worker-git-pr | exercised | Fresh recovery worktree, minted claim identity, clean exact candidate. |
