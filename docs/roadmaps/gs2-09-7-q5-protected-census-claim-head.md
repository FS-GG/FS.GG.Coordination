# GS2-09.7 protected census claim head

Status: **source-only fake journal CAS contract**. No protected durable
journal, signer, clock, store head, Q5/Q6 receipt or OperatingV1 admission is
installed.

The [one-use claim draft](gs2-09-7-q5-protected-census-claim-once.md)
accepted any positive commit generation after `ClaimOnce`. A fake journal
could report a jump, an unchanged head or a lost head read and still produce
an apparent success. An independent negative test was red before this change.

The claim port now reads a bounded prehead and includes its exact generation
and SHA-256 digest in the claim request. A committed record must advance that
head by exactly one generation and carry the deterministic successor digest.
The final journal head must match the committed record. Unknown prehead or
final-head reads, malformed or foreign heads, generation jumps, lost CAS
results and conflicts refuse without retry. A CAS conflict has a distinct
refusal from a duplicate claim; neither authorizes a second attempt.

The fake port can assert a head and receipt without storing either. The
protected owner must install a candidate-inaccessible linearizable journal
whose `ClaimOnce` compares the supplied prehead and commits the claim and
successor head in one durable transaction. Crash recovery must prove whether
that exact transaction committed before any launch or receipt. The pinned
signer, clock, store generation, OperatingV1 admission and native readback
remain external. #3690's admission violation requires adjudication; all nine
Q5 authorities and five Q6 rollback domains remain open. No sandbox effect,
token handoff, receiver pin, protected merge, Authority write, receipt or
cutover follows from this draft.
