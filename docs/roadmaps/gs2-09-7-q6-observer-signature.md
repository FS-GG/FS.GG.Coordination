# GS2-09.7 Q6 observer signature hold

Status: **source-only authentication contract; no installed observer, protected
pin, native readback, Q5/Q6 receipt or admission**.

`GitHubRollbackReadbackSignature.verifySigned` requires an independently
supplied, lowercase SHA-256 pin of an RSA subject public key. It first checks
the existing [readback provenance contract](gs2-09-7-q6-readback-provenance.md),
then verifies an RSA-PSS/SHA-256 signature over a versioned, length-framed
payload. The payload binds the selected plan digest and seal, expected run
nonce, challenge and observer resource, all five ordered receipts and claims,
each native revision, and the terminal OperatingV1 epoch claim. A blank or
foreign pin, changed native revision, foreign signer, or replay under a new
expected run nonce refuses. Tests generate disposable keys; this repository
does not contain a live signer or pin.

The protected Q5/Q6 owner still has to supply, from a candidate-inaccessible
source, the observer key pin and an exact sandbox, target and epoch binding.
The installed observer must attest that each native revision was read after
its corresponding receipt, in order, from the selected sandbox and that the
terminal epoch read was fresh. A signature on self-authored claims does not
establish these facts. The owner must also read back the complete nine-authority
Q5 result and six interruption cuts and record the protected qualification
receipt before admitting GS2-09.7. The #3690 source merge remains an
unadmitted observation under OperatingV1. No accepted GS2-09.6 command bytes
or live configuration change here.
