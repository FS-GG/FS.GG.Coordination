# Typed migration transforms

GS2-09.3 consumes the accepted immutable manifest and resolves every declared transform obligation to one
closed typed outcome. The ten required transform families are:

- `taxonomy`
- `planning-fields`
- `repository-scope`
- `body-metadata`
- `blockers`
- `hierarchy`
- `scheduling-holds`
- `touch-sets`
- `lifecycle-receipts`
- `desired-settings`

Each obligation identifies one manifest subject and family. Qualification requires the ordered transform
population to match the ordered obligation population exactly, so an omitted, duplicated, reordered, or
invented decision refuses. Every transform binds its source schema, old bytes digest, old value digest,
stable global ID, transformer fingerprint, manifest digest, and manifest seal.

`Migrated` retains the subject global ID and binds one target identity, schema, payload digest, and mapping
digest. `Ambiguous` retains an explicit reason, evidence digest, and at least two sorted distinct candidate
targets. `Unsupported` retains a stable code, reason, and evidence digest. Empty prose, malformed hashes,
one-candidate ambiguity, duplicate candidates, and a changed migrated global ID refuse qualification.

Q5 seals the complete typed decision population. Q6 rebuilds it in a fresh process and requires the same
normalized digest. The contract is pure and has no provider client or mutation surface. It classifies
transform results but does not choose live-operation handling, produce archives, execute rollback, prepare
receivers, or authorize migration and cutover.
