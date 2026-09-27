# GS2-09.7 legacy claim and receipt source roster

- Status: source-bound research; canonical authority remains unavailable
- Coordination revision: `a62f36f9783cf0763978a50cba339f76972a1518`
- Protected `.github` revision: `ca6dd7bd5d14cd3c44f54c89ee87f602c3a3abce`

This audit pins the producer and parser source needed by the
`claim-and-event-streams` authority. It records evidence from immutable source
bytes. It is not a provider observation or a completeness receipt.

## Native capture

The native reader now retains independently censused, terminal comment,
issue-event and timeline streams for every issue and pull request, plus reviews
and inline comments for pull requests. It repeats both issue and pull-request
populations after all streams and requires two fresh captures to agree on typed
records, exact raw response bodies, page links and request identity. Timeline
records remain distinct observations when their node IDs and bytes overlap the
comment or issue-event endpoints. Issue bodies remain owned by the issue
authority. Native marker classification and correspondence to protected journal
history still require the final claim/event binder.

## Producer inventory

| Family | Pinned evidence | Durable source | Finding |
| --- | --- | --- | --- |
| Claim marker | `Writes.markerBody`, `Reads.readComment` and `Reads.winner` | work-item issue comment | protected producer present; historical `C-claim` omits `renewed` |
| Review decision | `StructuredDecision` and `LiveHandlers.recordReview` | pull-request issue comment | protected producer present |
| Review wait | `ReviewWait` and its `LiveHandlers` writer | pull-request issue comment | protected producer present |
| Delivery obligation | `QualificationEvidence`; `DeliveryApplication` parser | pull-request issue comment | protected producer present |
| Delivery receipt | `DeliveryApplication` parser | expected pull-request issue comment | no protected writer or exhaustive population found |
| Intake marker | `IntakeReceipt.marker` and `Writes.createIntake` | issue body | protected producer present |
| Intake transaction receipt | `Cache` | process-local JSON cache | no protected retention or provider census found |
| Typed completion | `Delivery` and `Writes.deliveryCompletionReceipt` | work-item issue comment | protected producer present |
| Completion correction | `Delivery` and `Writes.completionCorrectionReceipt` | work-item issue comment | protected producer present |
| Legacy done receipt | `Done.receiptState` | expected work-item issue comment | consumer only; current renderer does not emit the marker |

The protected tree has no exhaustive producer-owned reserved-prefix registry.
It also contains route, lifecycle, self-host, overlap, mutation-lease,
merge-election, authorization and messaging marker families. Consequently a
search result or caller-supplied completeness flag cannot establish that the
requested roster is exhaustive.

The contract now validates each inventory row against one terminal read of an
immutable GitHub Contents API URI. It independently decodes the retained
base64 bytes, verifies the Git blob SHA-1 and raw SHA-256, binds source identity
to the byte digest and revision, requires the full requested-family roster and
binds all rows in a deterministic fingerprint. Qualification still refuses
`delivery-receipt`, `intake-receipt` and `legacy-done-receipt`, including when a
caller relabels their evidence as a producer. New protected producer evidence
or an explicit loss disposition is required before that refusal can change.

These gaps keep canonical `claim-and-event-streams` unavailable. The report
does not establish a live read, migration effect or Q5/Q6 acceptance.
