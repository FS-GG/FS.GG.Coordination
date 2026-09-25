# GS2-09.7 protected census attestation claim

Status: **source-only fake durable journal**. No installed protected claim
authority, signer, clock, store head, Q5/Q6 receipt or OperatingV1 admission.

The [seal verifier](gs2-09-7-q5-protected-census-seal-attestation.md) can
verify the same signed attestation repeatedly. This draft derives one stable
claim ID from the exact run, attempt, nonce, workflow, candidate, target and
store generation. Signature bytes, corpus and issuance time do not change that
ID, so a second signed handoff for the same decision must hit the same journal
slot. After signature verification, an injected journal port attempts one
atomic `ClaimOnce` and requires exact durable readback of the claim request
and positive commit generation. Duplicate, unknown CAS result, missing or
forged readback and candidate-readable/writable journal descriptors refuse.
Unknown results are not retried. Independent fake-port negatives were red
against the verifier-only scaffold and pass after the source check.

The journal port is a contract, not an installed authority. A dishonest fake
can assert two successful commits; source code cannot establish atomicity,
durability or candidate-inaccessible ACL. The protected owner must pin and
attest a linearizable journal, signer, clock and store generation, prove
one-use semantics across process crash and concurrent claims, and join the
claim to native readback and custom receipts. The signature validity check
and claim are separate source calls; a protected launch boundary must prevent
expiry or revocation between them. #3690's OperatingV1 admission violation,
all nine Q5 authorities and five Q6 rollback domains remain open. No sandbox
effect, App token handoff, receiver pin, protected merge, Authority write,
receipt or cutover follows from this draft.
