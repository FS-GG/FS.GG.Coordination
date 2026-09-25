# GS2-09.7 protected census store installation contract

Status: **source-only fake-port preflight**. This does not attest a real ACL,
store, native reader, Q5/Q6 receipt or OperatingV1 admission.

The [store readback draft](gs2-09-7-q5-protected-census-store-readback.md)
compared only a store resource ID, and did so after invoking the reader. A
fake store with candidate write access, a changed ACL digest or a different
artifact could pass. An independent negative test was red before this source
change.

The selected pins and store description now include the store artifact digest,
ACL policy digest, reader and writer principal IDs, and candidate principal
ID. The preflight requires exact pinned values, distinct candidate and host
principals, candidate read/write denial and immutable objects **before** the
reader is called. It rereads the description before object reads, refusing a
descriptor that drifts between preflight and readback. The versioned corpus
digest binds these pins. Fake-port tests cover candidate access, mutable
objects, artifact/ACL/principal drift and a mid-read descriptor change.

The fields are assertions until the protected owner independently proves the
actual IAM or filesystem ACL, process identities, artifact bytes, immutability
and candidate exclusion on the installed host. A candidate-controlled fake
store can claim all fields and replay matching objects, so this source result
does not establish custody, completeness or freshness. The protected owner
must also adjudicate #3690's OperatingV1 admission violation and supply the
selected sandbox, native inventory, journal/custom-receipt joins, all nine Q5
authorities and five Q6 rollback domains. No sandbox effect, token handoff,
receiver pin, protected merge, Authority write, receipt or cutover follows.
