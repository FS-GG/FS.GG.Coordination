# GitHub sealed history

GS2-09.5 preserves the exact legacy source material needed for durable verification without extending
the v2 production model with permanent v1 upcasters. Every ordered archive record binds its source
identity, schema, base64-encoded source bytes, byte digest, normalized value digest, and a typed
`Verified` or `Rejected` expected outcome.

The standalone verifier fingerprint and the complete sorted lookup index are part of the seal. Each
lookup key resolves to exactly one archive identity and the digest of that exact record. The production
closure has `V1UpcasterCount = 0`, exposes the archive verifier only, and makes lookup read-only. Missing,
duplicate, reordered, malformed, repointed, or byte-inconsistent records refuse.

Archive, lookup, normalized, and seal digests bind the accepted GS2-09.4 receipt; immutable manifest;
typed transforms; live-operation plan; verifier and production-closure artifacts; source records;
expected outcomes; index; and timestamp. Q5 and Q6 prove the controlled archive and fresh-process replay.

This qualification does not publish an archive, execute a live operation, rollback, or migration, read or
mutate a provider, change a receiver or setting, or grant cutover, OpenV2, acceptance, completion, or
successor authority.
