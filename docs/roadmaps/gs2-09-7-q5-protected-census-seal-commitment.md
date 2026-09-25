# GS2-09.7 protected census inventory commitment

Status: **source-only, unsigned commitment**. No protected signer, native
store seal, freshness witness, Q5/Q6 receipt or OperatingV1 admission is
installed.

The [inventory readback draft](gs2-09-7-q5-protected-census-inventory.md)
required only a syntactically valid 64-character SHA-256 seal. Any unrelated
digest could accompany an otherwise matching fake inventory. An independent
negative test was red before this check.

`expectedInventoryCommitment` now defines a domain-separated, length-framed
digest over the exact run/attempt/nonce/candidate/workflow/target selection,
reader and store artifact and ACL pins, host and candidate principals, and
every ordered storage object, request, response, header and retained body
digest. The binder requires the inventory's seal to equal that commitment
before object readback, then requires the second inventory to match the first.
The outer corpus digest uses a new version and includes the inventory seal.

This is a consistency commitment, not an attestation. Candidate code can
compute the public digest and can forge matching fake ports. The protected
owner must supply an independently pinned signer or immutable store head,
candidate-inaccessible key and ACL, native byte custody, a linearizable full
inventory and a fresh order witness tied to the selected run. A stale replay
of a correctly computed commitment remains possible until those facts are
installed. #3690's OperatingV1 admission violation, selected sandbox,
journal/custom-receipt joins, all nine Q5 authorities and five Q6 rollback
domains remain open. No sandbox effect, token handoff, receiver pin, protected
merge, Authority write, receipt or cutover follows.
