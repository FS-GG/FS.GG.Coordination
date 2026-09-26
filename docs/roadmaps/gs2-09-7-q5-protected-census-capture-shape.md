# GS2-09.7 protected census capture shape

Status: **source-only fake-port control**. This is not an installed native
reader or Q5/Q6 acceptance.

The [issue census port](gs2-09-7-q5-protected-issue-census-port.md) formerly
reconstructed a GET from the stored URI and a JSON string. It had no field for
the method actually captured, the final response URI, or the original body
bytes. An independent test showed that write-shaped, redirected and
byte-substituted fake captures could pass. The read record now carries the
request method, final response URI and base64 of the captured body bytes. The
binder requires exact `GET`, no redirect, canonical base64, strict UTF-8 and
byte-for-byte agreement with the JSON text used by the raw-to-typed adapter.
Those values also contribute to the corpus digest. The negative test was red
before the source check and green after it.

The port still trusts a caller-provided descriptor and record. A protected
owner must install a candidate-inaccessible reader/store and independently
attest its ACL, immutable artifact, native response-header bytes, provider
identity, complete inventory, freshness and exact admission/target pins. A
fake `GET` field cannot itself prove that the native request was read-only.
The journal, all nine Q5 authorities, Q6 native rollback readbacks and
protected receipt remain separate. #3690's merge remains an OperatingV1
admission process violation for owner adjudication. No sandbox effect, App
token handoff, protected merge, Authority write, receipt or cutover follows.
