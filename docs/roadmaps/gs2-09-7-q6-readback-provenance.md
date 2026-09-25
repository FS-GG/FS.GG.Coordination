# GS2-09.7 Q6 readback provenance claim hold

Status: **source-only, with no protected observer or Q6 receipt**. The pure
[five-domain readback checker](gs2-09-7-q6-native-readback-claims.md) compares
restored state digests and the terminal epoch, but an old observation of the
same bytes can look identical to a fresh post-restore read. The accepted
[rehearsal](gs2-09-7-representative-rehearsal.md) requires native recovery
and readback after each interruption.

`GitHubRollbackReadbackProvenance.verifyProvenance` adds a separate claim
binding: exact expected plan seal, run nonce, one challenge and observer
resource ID; one claimed native revision and **that step's receipt hash** for
each of the five domains; and a terminal epoch revision tied to the final
receipt. It composes the existing pinned-plan, complete-receipt and
five-domain state checks before considering these fields. Controlled cases
refuse a prior step's receipt, foreign observer, changed challenge/run,
missing native revision, stale terminal receipt and missing expected
resource. The source preserves accepted GS2-09.6 gate command bytes.

These are still **claims**. The `AfterReceiptSha256` field does not prove a
provider read occurred after the receipt; candidate code could invent the
same value. The protected owner must install an independently authenticated
observer outside the candidate workspace, pin its resource/key and target
scope, issue an unpredictable challenge, and retain raw native response
bytes, request identity, revisions and a protected time/order witness after
each durable receipt. The terminal epoch must be read from its protected
authority after the final restoration, with exact run/plan lineage. A
missing or unverifiable authentication, inaccessible native subject, stale
revision, partial read or unknown result must keep Q6 pending. No source
green verdict here is a native Q5/Q6 result. #3690 remains unadmitted; no
sandbox mutation, receiver pin or Authority write is installed.
