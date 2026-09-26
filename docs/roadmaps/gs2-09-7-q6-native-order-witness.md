# GS2-09.7 Q6 native order witness hold

Status: **source-only signed witness contract; no installed protected clock,
observer, sandbox readback, Q5/Q6 receipt or admission**.

The [observer signature contract](gs2-09-7-q6-observer-signature.md) binds
readback and receipt claims but has no native order evidence. An otherwise
valid signature can cover claims whose native reads occurred before their
receipts. `payloadForOrderedSigning` now binds a separate, protected-source
expected sandbox ID, epoch authority ID, witness ID and exact generation to
the same five-domain batch. It requires exactly one event per restoration
step, each with the selected target, receipt and state digest. Its single
ordinal namespace requires each durable receipt commit before its native
read, each read before the next receipt commit, and the terminal epoch read
after the last native read. The RSA-PSS/SHA-256 signature covers the full
order witness as well as the earlier readback batch. Controlled tests refuse
a foreign sandbox or epoch, stale generation, omitted or duplicate event,
crossed or equal ordinals, foreign target and post-signature alteration.

The ordinals and generation are **claims** until a candidate-inaccessible
protected journal issues them from one linearizable sequence and an observer
with native access signs the resulting batch. The protected owner must pin
the journal identity and current generation independently, show that receipt
commit and native read events share that namespace, retain raw native
responses, and show the terminal epoch is the selected OperatingV1 authority
read after the last restore. The installed observer must bind its key to the
selected sandbox and reject replayed challenges. No source-only green verdict
is a Q5/Q6 result. #3690 remains unadmitted; accepted GS2-09.6 command
bytes and all live pins remain untouched.
