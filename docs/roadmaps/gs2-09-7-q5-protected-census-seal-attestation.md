# GS2-09.7 protected census seal attestation verifier

Status: **source-only fake signer and clock**. The [inventory
commitment](gs2-09-7-q5-protected-census-seal-commitment.md) remains unsigned
in the installed route; no protected key, signer, clock, store head, Q5/Q6
receipt or OperatingV1 admission is installed.

The new verifier defines a length-framed signed payload for the selected
workflow, candidate, run, attempt, nonce, repository and store; the corpus
digest; an exact store generation; signer artifact and public-key pins; and a
short UTC validity window. It checks the pinned P-256 public key's SPKI digest,
requires a 64-byte P1363 ECDSA/SHA-256 signature, and reads time once through
an injected protected clock port. Missing, unsigned, foreign, stale, future,
wrong-generation, wrong-artifact and unknown-clock fake claims refuse. A
runtime-generated test key exercises the signature path; no private key is
stored in this repository.

The verifier returns only a source-level result. A caller can choose its own
key, clock and generation until a protected owner independently pins the
public key and signer artifact, keeps the private key and clock inaccessible to
candidate code, and reads the exact linearizable store generation. A signature
within its validity window can also be replayed for the same run unless a
protected one-use claim and journal join are installed. The full native
inventory, raw byte custody, all nine Q5 authorities, five Q6 rollback
domains, custom receipts and protected acceptance remain outstanding. #3690's
OperatingV1 admission violation requires owner adjudication. No sandbox
effect, App token handoff, receiver pin, protected merge, Authority write,
receipt or cutover follows from this draft.
