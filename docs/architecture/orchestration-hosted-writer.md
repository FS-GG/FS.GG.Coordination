# Hosted writer source boundary

`HOSTED-WriterV1` defines one closed orchestration route for a reversible
`routine-documentation-delivery` pilot. The route binds an immutable work item,
one attempt, one durable candidate, one repository and branch, the current
permit generation and workflow revision, and seven stable operation IDs. It
does not provide a general writer or shell surface.

The journal orders claim acquisition, runner processing, candidate storage,
branch publication, pull-request creation, merge, and provider-native delivery
readback. Each stage records intent before dispatch. A stage advances only from
a typed receipt bound to the selected route, attempt, candidate, repository,
generation, revision, and observation window. A missing response stays unknown
and prevents another operation or later stage until provider readback proves
the same operation absent or applied.

Candidate bytes must be durably accepted before branch publication. Completion
requires the selected repository and pull request to report a merged delivery,
with the observed pull-request head equal to the stored candidate head and the
merge commit equal to the merge-stage receipt. Adapter or runner completion
claims cannot substitute for this readback.

The executable host remains paused and performs no external dispatch by
default. This source amendment does not publish a package, install credentials,
activate a permit, or qualify a Main deployment. Those require a sealed provider
adapter, released bytes, explicit permit and ownership evidence, and the Main
storage and service qualification described by the pilot boundary.

The pilot permit is aligned to the route's `routine-documentation-delivery`
class. The host recovers the immutable WorkItem command aggregate and exposes a
sealed provider adapter for only the seven route effects. Every process startup
first appends a pause event to that WorkItem journal and invalidates readback
currency. Resume and effect dispatch remain refused until a fresh provider
readback, bound to the selected route and current generation and workflow
revision, is durably accepted.

The source protocol is checked as a four-process Choreo model (`Host`, `Journal`,
`Runner`, `GitHubProvider`). Genuine Quint ITF traces replay through production
Host admission/workflow/effect policy, the execution-session actor and neutral
coordinator, production callbacks, and memory/PostgreSQL journals. Replay exposed
and corrected two implementation mismatches: proven absence stops effect
continuation, and retry intent metadata is reconstructed from recovered journal
state before a PostgreSQL append.

Changes to these seams must follow [the Choreo correspondence maintenance
guide](choreo-correspondence.md), including exact trace regeneration, production
replay, negative controls and canonical qualification. The [qualification
decision](choreo-qualification.md) records the retained flat-model safety
abstraction, projection scope and measured bounds. These source checks do not
activate installed O3 adoption.
