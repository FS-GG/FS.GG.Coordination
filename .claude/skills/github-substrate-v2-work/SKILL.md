---
name: github-substrate-v2-work
description: Execute one explicitly named GitHub Substrate v2 roadmap unit in FS.GG.Coordination when its exact accepted prerequisites must be proven, its candidate evidence must be bound, and only its declared qualification commands may run. Use for GS2 roadmap implementation; do not use to schedule from Project status or to continue into a successor unit.
---

# GitHub Substrate v2 work

Work on exactly one unit named by the caller. The canonical roadmap and accepted receipts are authority; Project status is not authority.

## Inputs

- Unit ID such as `GS2-01.7`.
- Exact roadmap bytes from `FS-GG/.github` at the revision pinned in `eng/github-substrate-v2-units.json`.
- `eng/github-substrate-v2-units.json`, `eng/github-substrate-v2-gates.json`, and `evidence/github-substrate-v2/accepted/` from the candidate checkout.
- A clean committed candidate. Never manufacture or amend an acceptance receipt while executing its consumer.

Use the repository CLI through:

```text
dotnet run --project src/FS.GG.Coordination.Cli --configuration Release --no-build -- roadmap-work <operation> ...
```

## Select the delivery route

Use the canonical shared routine-development policy at the roadmap revision in force. A unit uses
`routine-development/v1` only when that roadmap and policy explicitly admit its source change and the
change stays outside every protected operation and path. Routine source work has one accountable owner,
one `routine/<slug>` branch, one PR, an exact-head eligibility marker, the smallest relevant automated
checks, native merge, and merged-state readback. Do not layer on a second issue or claim, SDD artifact
family, independent critique, feedback cycle, telemetry receipt, receipt-only PR, projection PR, or
metadata-`Done` write. Roadmap inspection, prerequisite validation, manifest binding, and declared gates
remain because they protect unit sequencing and executable identity; they do not create extra delivery
phases.

Migration or cutover, settings or credential changes, publication or deployment, destructive effects,
external-contract acceptance, and any unit that requires an immutable acceptance receipt remain strict.
If eligibility is absent or unclear, refuse the routine route rather than weakening a protected boundary.

## Workflow

1. Run `roadmap-work inspect` with `--index`, `--roadmap`, and `--unit`. Confirm the owner, permission ceiling, exit gate, Q gates, and gate commands match the assigned unit. A roadmap digest mismatch, unknown unit, or incomplete index is a stop.
2. Run `roadmap-work prerequisites` with the same inputs plus `--receipts`. Proceed only when it returns `ready: true`. Missing, duplicate, rejected, stale, malformed, contradictory, or tampered receipts are refusals; never replace them with prose, checkboxes, or Project fields.
3. Implement only the named unit within its declared touch-set and permission ceiling, using the route selected above. This skill grants no claim, scheduling, settings, deployment, or production-write authority.
4. Commit the candidate and require a clean worktree. Run `roadmap-work manifest` with the candidate's tracked `--index`, the same `--receipts` directory proven in step 2, explicit `--repo`, canonical UTC `--created-at YYYY-MM-DDTHH:MM:SSZ`, one or more tracked `--artifact name=path` values, and an ignored `--output artifacts/roadmap-work/<unit>/candidate.json`. When the unit declares gate commands, the artifact set MUST bind the tracked catalog that step 5 will execute, for example `--artifact gate-catalog=eng/github-substrate-v2-gates.json`. The output state is `candidate`, not qualified or accepted.
5. Run `roadmap-work gates` with the exact manifest, the same tracked index, roadmap, unit, receipts, and repository, plus the reviewed `--catalog` and an ignored result `--output`. Before starting any process, it requires the tracked candidate index and catalog to be exact manifest artifacts, revalidates prerequisite receipts, and requires each selected catalog entry to match the unit's ordered command ID, Q-gate, and executable-plus-arguments digest. It stops on the first failure, rechecks the candidate/artifact bindings, and writes results beneath `artifacts/roadmap-work/`.
6. Put evidence available before merge in the owning PR. Keep telemetry, receiver adoption, and other
   post-merge observation asynchronous and non-blocking unless the unit's exact external acceptance
   contract requires those facts synchronously. Acceptance still requires the merged PR or protected
   administrative receipt named by the roadmap.

## Claim-dependent protected evidence

Activate only the checks needed to substantiate claims made by the selected unit. They are not a
universal six-check preflight for ordinary source work. A unit-specific test may add to an activated
check but may not replace it.

1. `fresh-exact-candidate-checkout`: when acceptance depends on isolated reproducibility, export or create an isolated Git checkout at the exact candidate
   revision. Copy no ignored, untracked, generated, tool-cache, or working-directory bytes into it.
2. `provider-artifacts-tracked-and-hash-bound`: when the unit consumes provider evidence, enumerate every provider input declared by the unit's
   evidence contract. Require `git ls-files --error-unmatch`, file presence, and an exact match to the
   declared digest for every row. An ignored local prerequisite is missing evidence even if a dirty
   authoring checkout can read it.
3. `provider-contract-canonical-version`: when the unit pins a provider or tool, install the exact version named by the unit or
   provider contract in the isolated checkout. An ambient newer or older executable is not equivalent.
4. `two-consecutive-coherent-no-change`: when the unit claims a provider fixed point, run the canonical provider verification twice against the same
   clean candidate. Both runs must report success, `coherent=true`, only `noChange` operations, zero
   diagnostics/blockers, and leave the Git tree clean. The second run proves the first did not merely
   repair stale generated state.
5. `exact-head-hosted-run-artifact`: when acceptance says a workflow, runner, event, permission, secret,
   queue, or hosted integration works, bind a successful hosted run at the exact candidate head and
   download and validate its retained typed decision artifact. A local, detached, or schedule-emulated
   execution is useful diagnostic evidence but never satisfies a hosted claim.
6. `descendant-authority-matrix`: when authority is meant to survive protected advancement, exercise the
   implementation merge, its receipt descendant, a further unrelated descendant, and a descendant with
   an unrefreshed relevant mutation. The first three must pass; the relevant mutation must fail closed.

For an activated check, any unreadable declaration, missing artifact, unavailable canonical version,
absent hosted artifact, dirty fixed point, or unexercised required matrix is a refusal. Repair a
class-level producer or process at its source rather than filing unit-specific ceremony to satisfy it.

## One item across implementation and receipt phases

Do not open a receipt phase for routine work merely to copy merged facts. When a strict unit's exact
acceptance contract requires an append-only acceptance or repair receipt containing facts that exist only
after its implementation merge, use `single-owning-item-two-phase-receipt`:

1. Declare implementation and receipt paths in the original touch-set. The implementation PR must not
   carry a closing keyword for the issue.
2. Review and guarded-merge the implementation PR, but do not run terminal delivery completion or stamp
   the issue Done. Wait for exact protected-merge checks and retain their identities.
3. Use `verified-markerless-in-progress-handoff`: keep the same issue continuously reserved while rotating the claim generation required for a new
   merge-election fence: release with `--status "In progress"`, verify the markerless `In progress` row is
   visible and therefore unschedulable, and immediately reacquire that same issue before authoring the
   receipt PR. Refuse if the reservation write or its read-back is not exact. This fenced generation
   handoff is a second phase of the selected unit, not permission to inspect or execute a successor unit.
4. Independently review the receipt PR, verify historical receipt immutability and all bound Git/run
   identities, guarded-merge it, run protected verification, then perform the unit's single terminal Done
   transition.

Never create a receipt-only board issue. One extra PR is sometimes structurally necessary; one extra
scheduling row is not.

## Refusals

- Do not accept changed roadmap bytes without a reviewed index revision and digest update; preserve an accepted receipt only when its independently validated unit-contract digest is unchanged.
- Do not accept an unclean candidate, external or untracked index, incomplete gate contract, untracked evidence subject, path traversal, symlink, command override, shell interpolation, unknown gate, or catalog/index mismatch.
- Do not infer a ready unit from a checkbox, Project status, issue label, comment, branch name, or successful unrelated CI run.
- Do not create the selected unit's acceptance receipt before its protected merge or administrative acceptance operation.
- Do not label local or detached execution as hosted evidence, accept a provider fixed point from ignored
  files, close the owning issue between its implementation and receipt phases, or release it into a
  schedulable board state during the required claim-generation handoff.

## Stop at the unit boundary

After the selected exit gate and any required receipt phase are reported, stop. A successor may be
mentioned as inert context, but this workflow must not inspect its readiness, claim it, create its output,
run its gates, or mutate its state. Start a new invocation with separately established authority for the
successor.
