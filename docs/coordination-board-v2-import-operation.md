# Coordination V2 fixed import operation

`tools/coordination-board-v2-import.py` is the fixed Projects principal for the
COORD-BOARD-V2-01.2 representative pilot. It binds `.github` manifest commit
`ee809452e32e2745a37626a3d1ab114ef914e0c3`, manifest SHA-256
`10d6e84ebf1c33e75d5e0e2d2c25c43c0d6b893dc12d14a145ba51842a029e35`, the
organization and title, the legacy Project 1 exclusion, the four fields and the
three source issue node IDs. Callers cannot substitute those values.

The read-only preflight is:

```console
GH_TOKEN=... python3 tools/coordination-board-v2-import.py preflight
```

It uses one complete Projects query, reads and reports GitHub's GraphQL budget,
and refuses incomplete pagination, source issue drift, duplicate project titles,
GraphQL errors and any target resolving to legacy Project 1. It never claims that
a read proves create permission; `createCapability` remains
`unknown-not-probed` until an admitted operation performs the protected effect.

The 2026-09-30 preflight with the current development PAT reached GitHub but was
refused at `organization.projectsV2.nodes[0]` with `FORBIDDEN: Resource not
accessible by personal access token`. Repository administration rights therefore
do not establish the required organization Projects capability. The eventual
admitted run needs an injected credential that can read and write FS-GG
organization Projects v2; no credential value belongs in arguments, state,
receipts or logs.

## GraphQL principal policy

The worker-shared PAT route remains prohibited. The repository workflow
`.github/workflows/coordination-board-v2-import.yml` uses the policy's separate
workflow budget: it mints a run-scoped GitHub App installation token with only
`organization-projects: write`, invokes this fixed operation once and lets the
token action revoke the credential at job completion. No worker PAT is read.

The workflow itself is safe to merge before activation. Its environment
`coordination-board-v2-import` must be configured separately with required
reviewers, a `main`-only deployment-branch rule, and environment secrets
`BOARD_V2_APP_ID` and `BOARD_V2_APP_PRIVATE_KEY`. The dedicated App must be
installed for FS-GG, selected only to `.github` and `FS.GG.Coordination`, and
granted organization Projects write with no repository contents, issues,
administration or other organization write permission. The App's read-only
preflight must resolve all three fixed public issue nodes before an execute run
is approved. Missing environment protection, App installation, successful
preflight or secret enrollment leaves the effect pending; source merge does not
imply activation.

The workflow's first job is credential-free and qualifies the exact dispatched
protected source. The environment job rechecks that receipt, optionally restores
a same-source recovery artifact, mints the App token and runs either preflight or
execute. A fixed concurrency group serializes all runs. Recovery artifacts
contain no token and expire after 30 days. If a runner dies before retaining its
state, a later run refuses the unbound existing target rather than creating a
second project; recovery then needs a separately reviewed state binding.

The effect entry point is intentionally separate:

```console
GH_TOKEN=... python3 tools/coordination-board-v2-import.py execute \
  --state /private/coordination-board-v2-state.json \
  --admitted-source <reviewed-Coordination-source-SHA>
```

The admitting owner supplies the exact reviewed source SHA and a private durable
state path. Do not run this command from an unreviewed branch. The operation
persists a `0600` stage before every mutation, reserves eight observed GraphQL
points, batches schema, membership and field writes, and rereads the complete
target between stages. A retry either observes the exact prior effect or repeats
only a stage whose entire pre-state remains absent.

The operation refuses an existing unbound target, unknown project or field IDs,
field or option drift, partial membership, duplicate membership, partial field
seeding, conflicting field values and incomplete item or value pages. All effect
variables derive from the newly bound V2 project. The legacy project ID and
Project 1 are rejected before mutation. It does not edit issue state, bodies,
dependencies, assignees or legacy membership.

Run the focused checks with:

```console
PYTHONDONTWRITEBYTECODE=1 python3 tests/coordination-board-v2-import/run.py
```
