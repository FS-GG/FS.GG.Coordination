# Bounded roadmap work

GS2-01.6 adds a local execution contract for one explicitly selected GitHub
Substrate v2 roadmap unit. It does not add a scheduler. The canonical roadmap in
`FS-GG/.github`, the versioned unit index, and accepted receipt bytes are inputs;
the Coordination Project remains a visibility projection.

## Contracts

`eng/github-substrate-v2-units.json` uses
`fsgg.coordination.roadmap-index/1`. It pins the roadmap repository, exact commit,
path, and SHA-256, then registers the active GS2-01 units with stable IDs, owner,
prerequisites, permission ceiling, exit gate, Q-gate evidence lanes, closed
command IDs, and a canonical unit-contract SHA-256. Any roadmap byte change requires a reviewed pin update. The command
also proves that every registered ID and title still has its exact roadmap heading.

`evidence/github-substrate-v2/accepted/*.json` uses
`fsgg.coordination.unit-acceptance/1`. A receipt is strict JSON with one unit ID,
`accepted` state, the unit-contract SHA-256, exact source revision, non-empty
artifact fingerprints, acceptance time, and a canonical self-digest. Exactly one
valid receipt is required for each selected-unit prerequisite. Missing, duplicate,
rejected, stale, malformed, contradictory, or tampered inputs are different
refusals; Markdown, issue comments, branch names, and Project fields cannot replace
them. A checkbox-only roadmap projection can therefore refresh the roadmap/index
pin without invalidating an unchanged accepted unit contract.

`roadmap-work manifest` emits canonical
`fsgg.coordination.unit-evidence/1` candidate bytes. It requires a clean committed
tree and tracked regular artifacts, rejects path and symlink escape, and binds the
roadmap and index SHA-256, unit, commit/tree, prerequisite receipt digests, Q gates, command
IDs, artifact SHA-256 values, generator, and explicit UTC time. Its state is only
`candidate`; it cannot assert `qualified` or `accepted`.

`eng/github-substrate-v2-gates.json` uses
`fsgg.coordination.gate-catalog/1`. Catalog commands are `dotnet` plus literal
argument arrays. There is no shell interpolation or caller override. Gate execution
revalidates the manifest, current commit/tree, and artifacts, runs the selected
unit's exact command order, stops at the first failure, and writes compact result
digests under ignored `artifacts/roadmap-work/`.

The `Q0` and `Q7` labels on GS2-01.6 identify the evidence lanes to which its
architecture/skill controls and reproducible build/tests contribute. A successful
unit run does not claim the complete fleet-level Q0 or Q7 gate; those gates remain
subject to all roadmap-required evidence and later protected acceptance.

## Command sequence

The repository-owned `github-substrate-v2-work` skill calls the existing CLI with:

```text
roadmap-work inspect
roadmap-work prerequisites
roadmap-work manifest
roadmap-work gates
```

All operations require one immutable `--unit`. No operation reads the Project,
claims work, writes GitHub settings, deploys, subscribes to events, or selects a
successor. After gate results report the selected unit boundary, the invocation
stops. A successor needs a new authority decision and invocation.
