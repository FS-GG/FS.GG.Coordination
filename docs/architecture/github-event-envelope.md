# GitHub event envelope and cursor

GS2-07.1 introduces a pure repository-local qualification contract. It does not ingest webhooks,
schedule reconciliation, call GitHub, mutate a queue, or publish a workflow. Those boundaries remain
outside this unit.

`GitHubEventSource` binds the event kind, installation, repository, and protected source revision.
Each `GitHubEventDelivery` binds a positive cursor position, delivery and event identities, subject and
monotonic subject revision, causal and correlation identities, and receipt identity/disposition. The
compiler validates every field before it constructs state, collapses only byte-identical duplicates,
orders distinct deliveries by cursor position, rejects gaps and conflicting reuse, and derives the
complete cursor. The envelope seal is SHA-256 over nested UTF-8 byte-length frames for the schema,
source, ordered deliveries, and cursor; delimiter-bearing values therefore cannot alias one another.

Serialization emits one deterministic JSON shape. Parsing recompiles semantics, compares cursor and
seal, and requires byte-identical canonical serialization. Replay first verifies the prior envelope,
requires the exact same source authority, and compiles the union. Exact replay is a no-op; independent
reordering converges; conflicting reuse is refused without replacing prior facts.

The registered Q3 command `github-event-envelope-contract` executes the baseline, generated mutation
cases, and a separately authored independent control inventory. Its retained contract pins the exact
roadmap, accepted GS2-06.8 receipt, registration source revision, unit contract digest, and canonical
Quint protocol digest. Static controls additionally prove that the new implementation has no network,
queue, or production mutation route.
