# GS2-09.7 protected issue census port

Status: **source-only fake-port contract**. No protected reader, custody store,
option pin, native census, Q5/Q6 receipt or OperatingV1 admission is installed.

`MigrationProtectedIssueCensus.bind` accepts one protected reader descriptor and
one complete batch for the selected run ID, attempt, nonce, candidate, workflow,
API origin and numeric repository ID. It refuses blank or changed reader/store
pins, a missing or failed port, a foreign batch, an incomplete page seal,
nonconsecutive read ordinals, repeated storage object IDs, changed page bodies
or continuations, and an identity URI outside the selected repository. It then
reconstructs the captured read-only calls and uses the independent
raw-to-typed issue adapter to check repository identity, issue and pull request
subjects, and the terminal page chain. A corpus digest binds the selection,
installation descriptor, read ordinals, object IDs, URIs, raw-body digests and
continuations. The fake-port controls include a single unknown-result read with
no retry.

This contract cannot authenticate its inputs. The protected owner must install
a candidate-inaccessible native reader and immutable store, attest their
principal, ACL, artifact digest and endpoint identity, and supply a current
protected selection rather than candidate-written options. The store must seal
the full response bytes and headers for each exact request and provide an
independent complete inventory with a linearizable high-water mark. The port's
`Complete` flag, page count, object IDs and descriptor are assertions until
that installation exists. This slice retains JSON response bodies and `Link`
continuations; it does not yet prove full HTTP-header byte custody, provider
origin, or native read freshness. The adapter's local `ObservedAt` timestamp
must not be treated as a protected native observation time.

The [protected census owner packet](gs2-09-7-q5-protected-census-owner-handoff.md)
lists the separate admission, target, journal, nine-authority inspect and Q6
readback obligations. #3690's merge bypassed OperatingV1 effect admission and
requires owner adjudication. A source-only fake-port result does not admit a
sandbox run or close Q5/Q6. No sandbox dispatch, App token handoff, receiver
pin, protected merge, Authority write, receipt or cutover is authorized here.
