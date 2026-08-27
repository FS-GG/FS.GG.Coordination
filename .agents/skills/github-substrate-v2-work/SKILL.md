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

## Workflow

1. Run `roadmap-work inspect` with `--index`, `--roadmap`, and `--unit`. Confirm the owner, permission ceiling, exit gate, Q gates, and gate commands match the assigned unit. A roadmap digest mismatch, unknown unit, or incomplete index is a stop.
2. Run `roadmap-work prerequisites` with the same inputs plus `--receipts`. Proceed only when it returns `ready: true`. Missing, duplicate, rejected, stale, malformed, contradictory, or tampered receipts are refusals; never replace them with prose, checkboxes, or Project fields.
3. Implement only the named unit within its declared touch-set and permission ceiling. Complete repository SDD and ordinary review requirements separately; this skill grants no claim, scheduling, settings, deployment, or production-write authority.
4. Commit the candidate and require a clean worktree. Run `roadmap-work manifest` with explicit `--repo`, `--created-at`, one or more tracked `--artifact name=path` values, and an ignored `--output artifacts/roadmap-work/<unit>/candidate.json`. The output state is `candidate`, not qualified or accepted.
5. Run `roadmap-work gates` with the exact manifest and reviewed catalog. It executes only the selected unit's closed command IDs, stops on the first failure, rechecks the candidate/artifact bindings, and writes results beneath `artifacts/roadmap-work/`.
6. Publish generated and independently authored evidence through the unit's owning review path. Acceptance still requires the merged PR or protected administrative receipt named by the roadmap.

## Refusals

- Do not accept changed roadmap bytes without a reviewed index revision and digest update; preserve an accepted receipt only when its independently validated unit-contract digest is unchanged.
- Do not accept an unclean candidate, untracked evidence subject, path traversal, symlink, command override, shell interpolation, unknown gate, or catalog/index mismatch.
- Do not infer a ready unit from a checkbox, Project status, issue label, comment, branch name, or successful unrelated CI run.
- Do not create the selected unit's acceptance receipt before its protected merge or administrative acceptance operation.

## Stop at the unit boundary

After the selected exit gate is reported, stop. A successor may be mentioned as inert context, but this workflow must not inspect its readiness, claim it, create its output, run its gates, or mutate its state. Start a new invocation with separately established authority for the successor.
