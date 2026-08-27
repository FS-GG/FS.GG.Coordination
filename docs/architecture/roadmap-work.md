# Bounded roadmap work

GS2-01.6 adds a local execution contract for one explicitly selected GitHub
Substrate v2 roadmap unit. It does not add a scheduler. The canonical roadmap in
`FS-GG/.github`, the versioned unit index, and accepted receipt bytes are inputs;
the Coordination Project remains a visibility projection.

## Contracts

`eng/github-substrate-v2-units.json` uses
`fsgg.coordination.roadmap-index/1`. It pins the roadmap repository, exact commit,
path, and SHA-256, then registers the accepted GS2-01 units, accepted GS2-02.1,
GS2-02.2, and the active GS2-02.3 frontier with stable IDs, owner,
prerequisites, permission ceiling, exit gate, Q-gate evidence lanes, closed
command IDs, independently pinned command identities, and a canonical unit-contract SHA-256. Any roadmap byte change requires a reviewed pin update. The command
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
tree, a contained tracked unit index, and tracked regular artifacts, rejects path and symlink escape, and binds the
roadmap and index SHA-256, unit, commit/tree, prerequisite receipt digests, Q gates, command
IDs, command contracts, artifact SHA-256 values, generator, and one canonical
second-precision UTC time (`YYYY-MM-DDTHH:MM:SSZ`). Date-only, offset, and
alternate fractional forms are refused. Its state is only
`candidate`; it cannot assert `qualified` or `accepted`.

`eng/github-substrate-v2-gates.json` uses
`fsgg.coordination.gate-catalog/1`. Catalog commands are `dotnet` plus literal
argument arrays. Each selected entry must match the unit contract's ordered ID,
Q gate, and SHA-256 of its executable-plus-arguments identity before any process
starts. An admitted mutation such as `--list-tests` is therefore refused. There is
no shell interpolation or caller override, and an external index cannot redefine
the selected unit. Gate execution revalidates the manifest, current commit/tree,
tracked index, and artifacts, runs the selected
unit's exact command order, stops at the first failure, and writes compact result
digests under ignored `artifacts/roadmap-work/`.

The `Q0` and `Q7` labels on GS2-01.6 identify the evidence lanes to which its
architecture/skill controls and reproducible build/tests contribute. A successful
unit run does not claim the complete fleet-level Q0 or Q7 gate; those gates remain
subject to all roadmap-required evidence and later protected acceptance.

GS2-02.1 replaces the rejected conditional GS2-01.9 runtime branch in the
executable frontier. Its Q1 command regenerates the profile-2 manifest and
compiled projections from the canonical Markdown with the published 1.5.0 CLI,
an exact content-addressed Q1 cache, and byte-compares the retained review
artifacts. Its Q2 command additionally runs native Quint simulation, the authored
example, bounded Apalache verification, and an independently selected false
invariant that must produce an ITF counterexample. The commands require
`FSGG_SDD_CLI`, `FSGG_QUINT_CACHE`, `FSGG_QUINT_HOME`, and `JAVA_HOME` to point
at preseeded qualified tools; tool and artifact identities are checked before
execution and no acquisition occurs inside the gate.

GS2-02.2 reuses those exact Q1/Q2 command identities against the extended
literate source. It adds a closed seven-family authority catalogue and proves
that incomplete, contradictory, stale-revision, wrong-revision-kind,
wrong-authority, and omitted-family inputs fail closed. It does not add the
GS2-02.3 observation-outcome algebra or any external write authority.

GS2-02.3 reuses the same immutable Q1/Q2 command identities and adds a closed
nine-outcome observation algebra. `Observed` and `ProvenAbsent` are the only
knowledge-bearing outcomes; contradictory, unreadable, unsupported,
unauthorized, incomplete, stale, and rate-limited observations cannot silently
become absence. Retry classification is explicit, and proven absence is accepted
only from complete, non-contradictory evidence at the authority's bound revision.
The unit remains a pure repository-local model and grants no lifecycle intent,
network, deployment, publication, or production write authority.

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
