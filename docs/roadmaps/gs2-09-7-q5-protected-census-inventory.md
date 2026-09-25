# GS2-09.7 protected census inventory readback

Status: **source-only fake-port no-omission control**. It is not a native
inventory, freshness witness, Q5/Q6 receipt or OperatingV1 admission.

The [store readback port](gs2-09-7-q5-protected-census-store-readback.md)
checked only objects named by the reader batch. An independent negative test
showed that an extra sealed object, an omitted object or a duplicate inventory
entry could be invisible. The store port now supplies a complete inventory
for the exact run selection, store resource and high-water ordinal. Its ordered
object IDs must equal the reader's identity and issue-page objects exactly;
the seal must have a well-formed SHA-256 digest. The binder reads this
inventory before and after individual object readback and refuses any changed
field or unknown second result. The seal digest contributes to the versioned
corpus digest. Fake-port controls for extra, missing, duplicate, partial,
foreign, stale and changing inventories were red before the source check and
pass after it.

`Complete`, high-water and seal are still assertions of a fake store. The
protected owner must provide a candidate-inaccessible, linearizable inventory
of **all** native objects for the selected run, prove the seal's creation and
storage transaction, retain raw provider bytes, and provide an independent
freshness/order witness. Neither a matching self-reported list nor two matching
reads can prove that unseen native objects do not exist. Actual IAM/ACL,
artifact bytes and candidate exclusion remain owner installation facts.

#3690's merge remains an OperatingV1 admission violation for adjudication.
Selected sandbox identity, journal and custom-receipt joins, all nine Q5
authorities, five Q6 rollback domains and a protected receipt are outstanding.
No sandbox effect, App token handoff, receiver pin, protected merge, Authority
write, receipt or cutover follows from this draft.
