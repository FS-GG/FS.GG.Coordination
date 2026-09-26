# GS2-09.7 protected census store readback

Status: **source-only, read-only fake-port contract**. No protected reader,
store, ACL, native inventory, freshness witness, Q5/Q6 receipt or OperatingV1
admission is installed.

The [issue-census capture port](gs2-09-7-q5-protected-census-headers.md) had
only one injected batch. Its storage object IDs were unique within that batch,
but the binder never read the objects from a separate custody authority. An
independent negative test showed that a valid reader batch passed even when
the claimed store was absent, foreign or returned different data.

The binder now requires a distinct `IProtectedIssueCensusStorePort`. It reads
each referenced object once, in ordinal order, and requires the store's
resource ID, exact run selection and entire captured request/response record
to equal the reader batch. Missing, changed, stale and unknown-result objects
refuse; an absent store refuses before the reader call, and an unknown store
read is not retried. The corpus digest uses a new
version because a successful result now includes this extra readback step.

The interface alone cannot prove that the two ports are independent, that
their principals exclude candidate code, or that the returned objects were
sealed from native provider bytes. A protected owner must attest separate
reader and store identities, immutable artifacts, ACLs, native HTTP byte and
provider custody, a complete high-water inventory, and a fresh read order.
The current page count and `Complete` flag remain port assertions. Exact
OperatingV1 admission, selected sandbox, journal and custom-receipt joins,
all nine Q5 authorities and five Q6 rollback domains remain separate. #3690's
merge bypassed admission and requires owner adjudication. No sandbox effect,
App token handoff, receiver pin, protected merge, Authority write, receipt or
cutover follows from this draft.
