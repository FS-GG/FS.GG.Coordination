# GS2-09.7 Q6 declared receiver-pin readback hold

Status: **source-only, read-only fake transport; no exhaustive receiver
inventory, protected native reader, Q5/Q6 receipt or admission**.

`MigrationRollbackReceiverReadback.captureDeclared` is a narrow bridge from
the existing `MigrationReceiverCapture.capturePinBytesTwoPass` reader to a
selected Q6 rollback plan. It requires an independently expected plan seal,
the exact `receiver-cohort:<cohort SHA-256>` target on the receiver-pin step,
and a state digest derived from both raw-bound pin snapshots. The existing
reader validates repository and ref identity, commit and complete tree,
declared workflow/package blob paths and exact blob bytes, then closes the
ref. The bridge refuses a foreign plan or cohort before a transport call,
and a changed pin state after two reads. It returns a
`DeclaredReceiverRollbackReadback` with `InventoryBound=false`; the function
cannot produce a complete Q6 native readback verdict.

The declared receiver and pin lists are not an exhaustive provider census.
The protected owner must supply a candidate-inaccessible copy inventory,
pin the selected cohort and plan, retain the native request and response
bytes in the protected custody store, and bind the read ordinals and signer
from the [custody port contract](gs2-09-7-q6-protected-native-custody-port.md).
The receiver domain also needs scoped receiver settings and any other
pin-bearing surfaces absent from this declared blob read. The four other
rollback domains and terminal OperatingV1 epoch remain unimplemented. This
source-only result cannot close Q5/Q6. #3690 remains unadmitted; accepted
GS2-09.6 command bytes and live pins remain unchanged.
