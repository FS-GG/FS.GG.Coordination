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
class. The current host still recovers pilot state rather than a WorkItem command
aggregate and has no provider-effect adapter for these seven operations. Host
startup also does not yet persist the modeled restart transition that pauses work
and invalidates readback currency. A later executor integration must close those
two gaps before it can qualify or activate a writer.
