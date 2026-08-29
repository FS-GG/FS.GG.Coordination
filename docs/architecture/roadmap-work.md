# Bounded roadmap work

GS2-01.6 adds a local execution contract for one explicitly selected GitHub
Substrate v2 roadmap unit. It does not add a scheduler. The canonical roadmap in
`FS-GG/.github`, the versioned unit index, and accepted receipt bytes are inputs;
the Coordination Project remains a visibility projection.

## Contracts

`eng/github-substrate-v2-units.json` uses
`fsgg.coordination.roadmap-index/1`. It pins the roadmap repository, exact commit,
path, and SHA-256, then registers the accepted GS2-01 units, accepted GS2-02.1–02.9,
and the active GS2-02.10 frontier with stable IDs, owner,
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

GS2-02.4 reuses those exact command identities and adds a closed four-value human
scheduling-intent catalogue. Claim, blocker, pull-request, review, and delivery observations remain
independent fact/outcome pairs; derived lifecycle status is never accepted as intent. Only observed
presence and proven absence contribute lifecycle knowledge, every other GS2-02.3 outcome derives
`indeterminate`, and observation/status-refresh actions preserve the separately authorized intent.
The unit stops before relation algebra and grants no network or production write authority.

GS2-02.5 reuses those exact command identities and adds closed, directed
`REL-ParentChild` and `REL-Blocks` kinds. Native relation state is a set of typed edges:
duplicate adds and absent removes converge, edge-local changes preserve unrelated edges,
relation kind and endpoint direction remain distinct, and self edges fail closed. Relation
observation failures retain the GS2-02.3 outcome algebra and cannot become proven absence;
lifecycle intent remains independent. The unit stops before protocol streams and generalized
mutation envelopes and grants no network or production write authority.

GS2-02.6 reuses those exact command identities and adds closed stream and payload
catalogues for claim/lease/touch-set, operation-lock/election, review, delivery, and
operation receipts. Every envelope binds stable stream, subject, generation, sequence,
event, predecessor, payload, retention, and checkpoint identities. Append is idempotent
only for byte-equivalent envelope facts; conflicting event reuse, sequence gaps,
cross-stream payload substitution, generation regression, and retention relabeling fail
closed. Claim/lease/touch-set and operation-lock liveness are ephemeral; elections,
accepted reviews, delivery completions, and operation receipts are durable checkpoints.
Compaction may remove only ephemeral material whose decision is durably checkpointed,
and observation failures never become absence. The unit stops before generalized
mutation algebra and grants no network or production write authority.

GS2-02.7 reuses those exact command identities and adds a closed eight-kind mutation
algebra for create, append, add/remove edge, set/clear, transition, and compensation.
Every intent binds operation, subject, expected revision, idempotency key, and content
digest. Exact terminal replay converges, conflicting reuse of either operation or key
fails closed, and stale revisions classify as conflicts. Applied, idempotent, rejected,
and revision-conflict results are terminal; rate-limited, unavailable, timed-out, and
incomplete results remain explicitly uncertain and never claim an effect. Compensation
is permitted only against an applied, non-compensation predecessor for the same subject
and resulting revision. The unit stops before durable plan sequencing and grants no
network, GitHub mutation, or production write authority.

GS2-02.8 reuses the immutable Q1/Q2 command identities and adds ordered, resumable
durable-plan steps. Each step binds plan, predecessor, sequence, causation, correlation,
compensation boundary, and mutation intent. A checkpoint binds the exact operation receipt;
terminal success advances, uncertain outcomes require receipt re-read, and terminal refusal
chooses replan or reverse compensation according to whether the boundary already contains an
applied step. Compensation is limited to the same boundary and reverse application order.
The profile-2 authority fence continues to generate the retained contract, while one adjacent
authored `quint-test` fence supplies executable critic witnesses without consuming the compiler's
fixed authority-graph ceiling. The validator assembles those exact fences, reruns all prior mutation
negative controls, and requires each durable-plan binding to fail independently when weakened. The
unit stops before desired-state specification semantics and grants no network, GitHub mutation, or
production write authority.

GS2-02.9 reuses the immutable Q1/Q2 command identities and adds pure desired-state
specification semantics for issue schema, repository properties, Projects,
repository profiles, workflow pins, releases, permissions, and security/supply-chain
policy. Every fact binds its subject, profile, content, authority revision, support,
permission, and freshness. Inspect, plan, apply, and verify are closed phases;
unsupported, unauthorized, incomplete, stale, and identity-mismatched facts fail
closed instead of becoming a mutation plan. The compiled profile exports a compact,
revision-bound contract that explicitly names every governed settings surface and the
closed phase/refusal contract. The adjacent canonical `quint-test` fence carries the
typed surface sets, transition predicate, and witnesses within the compiler's fixed authority-graph ceiling.
This unit defines intent and verification only: it adds no writer, network, GitHub
mutation, deployment, publication, or production authority.

GS2-02.10 reuses the immutable Q1/Q2 command identities and derives a closed,
canonically ordered compiled-output contract from the same literate Quint authority.
The nine families are schemas, command metadata, permission census, mutation census,
settings plans, projection views, semantic diff, diagrams, and model-test inventory;
projection views require both Markdown and JSON. Every typed output fact binds family,
ordinal, source, profile, contract, content, support, completeness, and freshness.
Missing, duplicate, substituted, unsupported, incomplete, reordered, stale, or
single-format projection sets fail closed. The retained profile-2 projection exports
the exact ordered family, identity, qualification, view-format, and refusal contracts.
`eng/generate-compiled-contract-outputs.fsx` consumes that contract and the canonical
literate source to materialize a nine-entry typed manifest plus concrete schema,
command, permission, mutation, settings-plan, JSON/Markdown projection, semantic-diff,
Mermaid diagram, and model-test-inventory files. Every entry carries the real source
SHA-256, profile, contract fingerprint, ordinal, qualification flags, file paths, and
content digests. The independent validator regenerates the directory and byte-compares
it; architecture mutants delete, duplicate, substitute, stale, truncate, and alter the
retained set. The adjacent executable `quint-test` fence remains the bounded semantic
witness within the compiler's fixed graph ceiling. This unit remains repository-local
and stops before deterministic deployment identity or any writer, network, hosted
runtime, publication, or production authority.

GS2-03.4 keeps the same canonical profile-2 behavior and adds seven bounded
qualification roots in the adjacent executable Quint fence. Four state-machine
roots expose authority, lifecycle, relation, and protocol-stream transitions while
importing only their enumerated canonical types, values, and pure definitions. The
validator extracts those imports from the executable module bodies and compares them
directly with each root's retained executable closure; there is no JSON-authored
executable DAG that can agree with itself while the Quint source says something else,
and every imported module/definition pair participates rather than only imports from
the canonical module.
Their valid steps
reach positive and adversarial witnesses, while separate invalid parameter actions
must violate the root invariant without rewriting the invariant or its witness;
three test roots isolate mutation/durable-plan, desired-state, and qualification
closures. `eng/quint-qualification.json` classifies every canonical state variable
and action, binds exact source-derived root closures and witnesses,
selection modes, budgets, and future-module admission. Ordinary pull requests use
the complete reverse-dependency closure selected from changed modules, paths,
oracles, bounds, budgets, backends, and toolchains. Reuse binds the exact source
identity plus a receipt binding the configuration, baseline, backend, toolchain, and
selected closure. The separately named `selectionImports` graph is conservative CI
routing metadata only and is never presented as executable topology. Proposed future
behavior must already be present in the canonical behavior-digest-bound literate source;
its declared root, invariants, and witnesses must match the schema-checked admission
payload, and those exact source bytes must match the canonical observed
extraction-and-typecheck receipt from the pinned Quint toolchain. Admission then
re-extracts the canonical literate fences and executes the digest-pinned Quint binary;
a receipt is retained evidence, never a substitute for reproducing typecheck,
and main, acceptance, freeze, and release checkpoints use the full inventory.

`eng/validate-quint-qualification.fsx` is the separately authored black-box
observer for claim exclusion, stale projection, dependency concurrency, partial
operation, old-client fencing, ledger tamper, exact-head review, post-merge
verification, dual-feed recovery, bounded concrete/abstract equivalence, and the
scale envelope. Every oracle has a subject mutation that must change its outcome.
The validator also rejects cycles, incomplete closure, missing witnesses, stale
identity, missing or exceeded measurements, unsound selection, and incomplete
future admission. `eng/quint-qualification-baseline.json` retains the runner-class
calibration and all required depth/state/sample/time/memory/artifact measurements;
the evidence index binds the independent oracle implementation and both retained
contracts. These surfaces are repository-local and grant no network, GitHub
mutation, deployment, publication, or production-write authority.

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
