# GitHub event security

GS2-07.4 treats every incoming GitHub event as untrusted evidence. The pure
qualification contract authenticates the exact raw payload with HMAC-SHA256,
then strictly parses installation, repository, subject, and revision from those
authenticated bytes. Only the configured installation/repository and matching
API subject/revision can pass. It applies an
inclusive timestamp window and delivery-id replay check before comparing the
payload subject and revision with an independently supplied API observation.

Replay state includes both delivery IDs and authenticated payload SHA-256
digests. Consequently a captured signed payload remains a duplicate even when
presented with a different, unauthenticated delivery header.

Permission input is canonical and exact: a missing permission and an excessive
permission are both refusals. A successful result contains only the disposition
`schedule-reconciliation`, a deterministic scheduling key, and a seal over all
security-relevant output fields. It contains no command that writes derived
state. The existing reconciliation loop remains the exclusive derived-state
writer.

The contract performs no network, production queue, or GitHub mutation I/O.
Generated and independently authored retained controls exercise all positive,
boundary, adversarial, replay, and architecture obligations.
