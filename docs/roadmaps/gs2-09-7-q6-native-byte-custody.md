# GS2-09.7 Q6 native response custody hold

Status: **source-only byte commitment contract; no protected native reader,
canonicalizer, custody store, Q5/Q6 receipt or admission**.

The [signed native order witness](gs2-09-7-q6-native-order-witness.md) binds
receipt and read ordinals, native revisions and state digests, but contains no
response bytes. `payloadForCustodySigning` adds a domain-separated signed
commitment to each raw native response and the corresponding canonical state
bytes. It checks exact step, target, read ordinal and revision against the
ordered witness, then hashes the canonical state and compares it with the
selected restoration digest. The terminal response has its own raw-byte
commitment and an exact canonical `OperatingV1`, plan seal, completion and
authorization projection. Missing or duplicate responses, foreign revisions,
empty bytes, changed canonical state and changed post-signature raw bytes
refuse in controlled tests. The verifier computes hashes in memory; it does
not log or persist native payloads.

The protected owner must install a candidate-inaccessible native reader and
canonicalizer, keep the exact raw responses and canonical projections in
protected storage, and bind their custody identities to the selected sandbox,
targets, receipt commits and epoch authority. The signer must receive these
bytes through that protected path and sign only after the journal has recorded
the read. A supplied byte array alone does not prove native origin or correct
canonicalization. Unknown, missing or inaccessible responses keep Q6 pending.
The full Q5 nine-authority and Q6 six-cut readback and protected receipt remain
unmet. #3690 remains unadmitted; accepted GS2-09.6 command bytes and live
pins remain unchanged.
