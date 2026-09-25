# GS2-09.7 protected census headers and provider identity

Status: **source-only fake-port control**. No candidate-inaccessible native
reader/store, provider authentication, freshness witness, Q5/Q6 receipt or
OperatingV1 admission is installed.

The [capture-shape draft](gs2-09-7-q5-protected-census-capture-shape.md) still
accepted a separately asserted `LinkHeader`; it did not bind the recorded
provider or other response headers. The protected issue-census port now carries
an ordered header list and a provider resource ID for each captured response.
The binder requires the exact pinned provider ID, unique case-insensitive
header names, conservative ASCII name/value syntax, and equality between the
single recorded `Link` value and the value used for pagination. It reconstructs
the adapter response from that list and binds every ordered name/value pair
to a versioned corpus digest. Independent fake-port negatives for foreign
provider, hidden `Link`, duplicate `Link` and header injection were red before
the binder checks and pass after them. A changed valid header changes the
digest.

This is a logical header record, not the native HTTP wire bytes. The protected
owner must install and independently attest the reader principal, immutable
artifact, endpoint and transport identity, candidate-inaccessible custody ACL,
lossless native request/response byte storage and a fresh complete inventory.
The pinned provider ID and headers remain self-asserted in a fake port. The
port supplies no protected timestamp or order witness; the adapter's local
`ObservedAt` cannot establish freshness. The [owner packet](gs2-09-7-q5-protected-census-owner-handoff.md)
also requires exact OperatingV1 admission, sandbox selection, journal and
custom-receipt joins, all nine Q5 authorities and five Q6 rollback domains.
#3690's merge remains an admission process violation for owner adjudication.
No sandbox effect, token handoff, receiver pin, protected merge, Authority
write, receipt or cutover follows from this source result.
