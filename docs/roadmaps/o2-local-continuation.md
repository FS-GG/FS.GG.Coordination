# O2 local continuation

Feature: `standalone-telemetry-host-and-orchestration`  
Item: `O2-I4c`  
Source dependency: Coordination PR 388 and its accepted merge revision.

Backlink: [Unified Development Roadmap §9.8](https://github.com/FS-GG/.github/blob/main/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md#98-feature-parts-and-subroadmap-index).

## B — paused post-start recovery

The replacement Host starts paused and invalidates route readback. An authenticated
`POST /v1/main/recover` binds the unchanged admission document to the recovered
route, assignment, attempt, reservation, candidate, generation, and operation IDs.
It reads repository and issue identity from GitHub, records that fresh readback with
a new revision-bound command identity, and remains paused. `POST /v1/resume` is the
separate operator decision that enables new effects.

Subscription authority has two immutable clocks. `ExecutionDeadline` and
`MaximumRuntime` remain at most 30 minutes and bind the sole model invocation and
executor reservation. `DeliveryDeadline` is at most two hours from admission and
only bounds recovery and GitHub delivery. Version 1 budgets never acquire a
delivery window during upgrade. Reconciliation may observe an already exposed
operation after either deadline; it cannot launch a model or create a new effect.
New admissions bind the external claim lease to `DeliveryDeadline`, and every
later provider mutation freshly verifies that the same claim is still visible.
An expired or displaced claim stops delivery without renewal.

Acceptance requires focused Core and Host tests plus the real PostgreSQL replacement
test. The replacement test must show a fresh post-start readback, a separately
authorized resume, exact identity reuse, and no repeated provider mutation.

## CI prerequisite — bounded formal partition fanout

- [x] Split the optimistic formal obligation into the existing canonical base,
  all 18 semantic scenarios, and the independently bounded epoch scenario under one
  six-execution matrix cap. Strict candidate-bound aggregation restores logical
  partition 1, preserving the final six-receipt coherent contract. Focused
  fixtures cover complete, missing, foreign, failed, stale-envelope, stale-base,
  and test-census cases; native workflow timing remains the performance evidence.

## C — lost-host administrative retirement

When the original private journal is unavailable, retirement records
`AdministrativelyRetiredWithLostHistory`. It does not synthesize Core completion,
known usage, released reservation, or recovered process state. A bounded helper
preserves the original candidate `H` and native evidence, advances the exact old
branch from `H` to a deterministic same-tree tombstone `T` under an expected-`H`
lease, permanently freezes `T`, closes or observes the original pull request,
maintains subject exclusion, and verifies every effect before removing its
temporary main-branch hold. An already accepted merge of `H` is reconciled as a
separate terminal path. A cached old-client merge request for `H` must be rejected
after the `H` to `T` fence, including when it returns after retirement.

## Remaining execution

The remainder of O2-I4c has exactly four outcomes:

1. Land the administrative-retirement implementation and its affected source,
   model, and isolated native checks.
2. Retire the old scope with the qualified helper and verify its terminal native
   GitHub state.
3. Build one final admission and immutable replacement bundle from the accepted
   state.
4. Run one physical reboot pilot from that bundle and fresh admission.

There is no separate intermediate Unified-roadmap projection. Coordination records
one final progress update after the physical pilot; dependencies receive only the
short handoffs needed to unblock the next outcome. Native GitHub responses and
readbacks are reused as the operation evidence instead of being copied into a
second bespoke receipt layer. Subsequent changes run narrow affected tests unless
a contract or model input changes; such an input change reruns its existing
canonical qualification path rather than adding another qualification layer.

## Operational boundary

Source qualification does not activate a Host or resume an attempt. A physical
reboot pilot starts only from an installed immutable bundle and a newly admitted,
unexpired subject. Existing attempt and permit clocks are never renewed or retimed.
Under [Unified §9.9](https://github.com/FS-GG/.github/blob/main/docs/2026-09-07-154210-fs-gg-unified-development-roadmap.md#99-when-new-workspaces-change),
this source window changes no fresh-workspace contents or defaults. The behavior is
available only after explicit adoption of a qualified replacement Host bundle; a
clean install and an existing-store upgrade retain separate qualification paths.
