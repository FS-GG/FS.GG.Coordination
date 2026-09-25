# Prospective isolated v2 one-use grant parser

Status: source-only draft, 2026-09-25. This branch stacks on draft #550. It defines a versioned grant shape and strict parser without a grant issuer, CLI action, token lookup, HTTP client, journal write or provider effect. The current GS2-09.9 typed permission ceiling, gate catalog, unit index and receipt are unchanged. The [protected authority amendment](authority-amendment.md) remains a proposal, not an approval.

`eng/callable_isolated_v2_grant.py` accepts at most 16 KiB of canonical UTF-8 JSON with schema `fsgg.coordination.callable-isolated-v2-grant/1`. It rejects duplicate keys, nonfinite values, missing or extra fields, changed payload digest and noncanonical encodings. It returns `authorized:false`, `can_dispatch:false`, and `live_effects:0` even for a structurally valid synthetic grant. There is no call site in the v2 operator or installed CLI.

## Bound fields

| Object | Required binding |
| --- | --- |
| Grant | Exact schema/state, unique grant ID and nonce; the payload SHA-256 must match the independently supplied artifact digest. |
| Operation | Versioned v2 identity, immutable operation ID, canonical request SHA-256, exactly one `POST` to `repos/{selected repository}/pulls`, `maxProviderWrites:1`. |
| Protected authority | `FS-GG/.github`, named v2 workflow and environment, workflow commit/hash, run ID/attempt, environment/approval IDs, distinct dispatch actor/reviewer IDs, active reviewer membership, UTC approval/expiry within 30 minutes. |
| Source | Coordination revision and exact operator, contract, proposal and installed-command hashes. These are source identities only; the parser does not prove publication or installation. |
| Target | One FS-GG repository ID/name/node, App installation ID, distinct source/base refs and SHAs, and complete prestate digest. The repository and ref segments reject path traversal. |
| Execution credential | One GitHub App installation, exact target ID/name selection, `metadata:read`, `contents:read`, `pull_requests:write`, and expiry no earlier than the grant. No token value is part of the grant. |
| Durable intent | Protected `.github` operation journal ref, positive generation, exact head and operation ID, and `intent-committed` state. The ordinary delivery journal is not accepted as this effect's authority. |

The parser compares the entire grant with an independently supplied `expected` object and requires a complete replay observation bound to the same grant ID, operation ID and journal generation/head with `used:false`. These inputs are *interfaces*, not trusted facts by themselves. A future protected adapter must derive `expected` from separate run, source, target, actor and credential readbacks, verify the grant artifact envelope and installation, and atomically reserve the grant in the protected journal before any POST. Reusing a stale `used:false` snapshot cannot prevent replay; this parser deliberately exposes no dispatch capability. Failure to supply provenance or atomic reservation keeps the future live gate closed.

The test corpus uses invented IDs and an unselected fixture name. It never mints a token or contacts GitHub. Tests cover a positive parse that remains unauthorized; every nested boundary's missing and extra members; duplicate/noncanonical JSON and digest drift; foreign run/source/target/actor/journal bindings; broad method, write count, repository selection and permission; self-review; expiry; and used, unknown, incomplete or mismatched replay observations. The next candidate must add independent artifact and native journal evidence, then test no-grant and one-attempt behavior through the installed executable and protected workflow before seeking the one-operation authority described in #550.
