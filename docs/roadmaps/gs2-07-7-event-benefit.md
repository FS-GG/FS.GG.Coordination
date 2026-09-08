# GS2-07.7 — Measure event benefit

Owner: FS-GG/FS.GG.Coordination. Unified stage: V1.

Identity and cost lineage: the existing native `GS2-07.7`, including its
registration, implementation, qualification, repairs, and any required acceptance
phase.

This subroadmap is an execution outline, not a second completion ledger. The pinned
`.github` roadmap, Coordination's `eng/github-substrate-v2-units.json`, and the
accepted receipt under `evidence/github-substrate-v2/accepted/` remain the sole completion authority.

## Outcome and boundary

Produce reproducible measurements of narrow reconciliation, same-subject hint
coalescing, scheduled complete-audit repair, and subject isolation. Record latency,
API cost, schedule count, dropped-event repair, false and unknown outcomes, measured
population, provenance, coverage, and limits.

Scheduled complete audits remain authoritative. The default is to retain polling.
Replay or sandbox qualification may establish a bounded benefit without establishing
installed or production savings. This feature neither activates a writer nor changes
production polling.

## Starting evidence

- Coordination main at registration intake:
  `f5f070642eeef019d00c28676cd97c4be123a877`.
- Pinned `.github` roadmap revision:
  `7216ec4aae14b17f151a1ed3616eb8a2f4ed2d47`, SHA-256
  `0498209c27cdf75d3c1067dad2c3b88084b03c20f1bd87dcb99192aa43457c36`.
- Accepted GS2-07.6 receipt digest:
  `eaf032038cc3ed1fb3f1a21db81a32f7af7969f84a0d9b77cd1d7eea68346bc6`.
- Accepted GS2-07.1 through GS2-07.6 contracts and retained evidence provide event
  identity/replay, narrow routing, audit repair, event security, merge-group, and
  sandbox/queue controls. They do not prove an installed event worker.
- `evidence/github-substrate-v2/gs2-07-6/routine-burst.json` is one useful fixture,
  not a comparison cohort or production critical-path decomposition.
- The event/audit contracts currently have no measured timing, API-cost, or
  schedule-count fields. Existing narrow bureaucracy instrumentation is incomplete;
  useful tests are excluded, and this strict migration unit is not a routine sample.
- `src/FS.GG.Coordination.Cli/Program.fs` enables no production v2 commands.

Reported measurements must derive from retained inputs, not caller-supplied success
booleans.

## Native-unit execution phases

These are ordered phases of one native unit, not additional mutable GS2 milestones.

### 1. Register the measurement contract

Validate the exact GS2-07.6 accepted receipt and pinned roadmap. Add exactly GS2-07.7
to the existing unit index, immutable Q3 measurement/replay and Q4 bounded read-only
provider-observation identities to the existing gate catalog, focused architecture
tests and documentation. Reuse `work/318-gs2-07-6-registration/`; do not add a registry
or rewrite predecessor receipts.

Registration allows repository-local implementation and future read-only provider
observation only. It grants no production write, polling/visibility/settings change,
deployment, secret access or change, publication, acceptance receipt, measurement
claim, or GS2-07.8 authority. Changed prerequisite, roadmap or command digests,
missing metric provenance, and added write permission fail closed.

Stop this first window after protected merge, native readback, and strict lifecycle
cleanup. Registration does not claim measurement or GS2-07.7 acceptance.

### 2. Implement and exercise bounded measurement

After phase 1 merges, use the registered strict `github-substrate-v2-work` route.
Implement an additive pure measurement contract plus bounded read-only collection and
replay. Reuse existing event, narrow-reconciliation, audit-repair, and queue contracts;
do not build a hosted controller.

Declare a finite population and observation window before collection. Preserve these
source categories exactly:

- current provider observation;
- historical provider evidence;
- executable replay;
- injected negative control.

Retain complete pagination and run-attempt coverage, actual event/ingestion/queue/start/
end timestamps where available, call attempts, response/rate outcomes, scan scope,
hints, subjects, schedule admissions, run/attempt identities, repair delay, and native
outcomes. Separate collector overhead from workload cost. Missing counters, prices,
timestamps, revisions, or observations remain unknown rather than zero.

Compare a full-scan baseline and narrow/coalesced path against the same declared
workload. Preserve distinct subjects, semantic commands, approval/grant changes, and
non-idempotent operation identities; never cancel an applying effect because a newer
hint arrived. Withhold a known hint and require the next complete audit to discover
and converge it. Tampered timestamps, subjects, heads, pages, or source digests fail.

Any hosted claim requires a retained typed exact-head artifact bound to repository,
workflow, run/attempt, tested head, population/window, and source digests. Local
emulation cannot satisfy it. A valid result may be “no demonstrated benefit; retain
polling.” It may not claim deployed event coverage from replay.

### 3. Accept the measured result

Use the same owning strict item and existing protected acceptance workflow after the
registered exit contract passes. Bind the clean candidate, exact catalog, retained
inputs, predecessor receipt, generated/adversarial controls, hosted evidence when
claimed, review, and native merge evidence through existing manifest/gate/receipt
mechanisms. If post-merge facts require a receipt phase, use the existing two-phase
reservation handoff; do not create a receipt-only issue.

Preserve every historical accepted receipt byte. Accept GS2-07.7 only when the
registered measurement outcome is fulfilled. Installed coverage remains distinct and
polling remains unless supported installed-path/audit-fallback evidence and current
operational authority justify a later change.

## Observation and cost accounting

Reuse existing qualification economics only where its observed population applies. It
does not supply complete event/schedule populations, API request accounting, or item
critical-path intervals. Preserve original item/attempt lineage and available
provider/model/effort, timing, administrative work, repair, and native outcomes.

Do not convert aggregate waiting into bureaucracy. Exclude useful test execution;
count overlapping waits once on the actual delivery critical path. Missing attribution
is neither a passing budget nor a confirmed breach. This strict migration unit does
not become a routine sample by relabeling, and no manual replacement counter or new
reporting ceremony is added.

## Source contracts

- Unified Roadmap §§4, 7.4, 9.3, and 10:
  `.github/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md`.
- Native roadmap GS2-07 and §§12.2–12.4:
  `.github/docs/github-substrate-v2-roadmap.md`.
- Q0/runtime design §6.4:
  `.github/docs/coordination/2026-08-25-github-substrate-v2-fleet-cutover-design.md`.
- Remaining-migration review §3.7:
  `.github/docs/coordination/2026-08-30-github-substrate-v2-remaining-migration-architecture-review.md`.
- Native execution:
  `.agents/skills/github-substrate-v2-work/SKILL.md` and
  `docs/architecture/roadmap-work.md`.

The missing feedback materialization is already tracked by `.github#2366`; do not
duplicate it.
