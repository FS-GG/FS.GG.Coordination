# GS2-09.9 protected key and nonce admission decision

Status: source-only owner decision input, 2026-09-25, based on draft [#804](https://github.com/FS-GG/FS.GG.Coordination/pull/804) at `0543231e8b39bda71549b583c0b43f50208002df`. This packet selects no key, signer, verifier, nonce store, runnable artifact or protected run. It cannot approve installation or the [#550 native one-POST plan](https://github.com/FS-GG/FS.GG.Coordination/pull/550).

## Current boundary

The [canonical envelope adapter](../../eng/callable_isolated_v2_v5_issuer_envelope.py) calls an injected verifier by key ID. The [key-registry witness](../../eng/callable_isolated_v2_v5_key_registry.py) checks a caller-supplied 32-byte public-key fingerprint, claimed approval time and later completed-envelope observation. The [post-registry adapter](../../eng/callable_isolated_v2_v5_keyed_signature.py) passes those selected bytes and the exact canonical payload/signature to another injected verifier. Both verifier answers can be fake `True`; neither authenticates an Ed25519 signature, key approval, revocation service or issuer. Their results stay `authorized=false`, `can_dispatch=false`, `live_effects=0`.

The [current no-grant manifest](v5-no-grant-manifest.json) pins a single-member exit-78 ZIP archive, SHA-256 `a8b3bdc58f220061bb2e2e115d6aa0d042facd9881323a77dfe884b3b461f05c`. It contains no verifier or native dispatcher. The [effect producer](../../.github/workflows/callable-isolated-v2-effect-release.yml) and [execution workflow](../../.github/workflows/callable-isolated-v2-execute.yml) both have `if: ${{ false }}` and an exit-78 gate. On this development host, `python3 -I -S` (Python 3.14.7) finds neither `cryptography` nor `nacl`; that local result says nothing about an unselected protected runner. The proposed verifier therefore needs an independently reviewed implementation and exact runtime dependency closure before a runnable archive can be selected.

## Decisions and evidence that must be selected

The protected owner and a distinct reviewer must approve one immutable record with these coordinates. A candidate-supplied `scope()` value, matching digest or fake `True` cannot fill any evidence cell.

| Decision | Independent record and readback needed |
| --- | --- |
| Integrated verifier and release | Protected Coordination commit/tree and exact blobs for entry, verifier, issuer, registry, nonce adapter, manifest and both workflows; reviewed cryptographic implementation and its package/native-library hashes; deterministic archive/member hashes; producer run/attempt/artifact; reviewer event; immutable runner image, interpreter and runtime-closure hashes. Recompute all pins after integration. The closed ZIP above refuses in the runnable slot. |
| Trust root and key approval before signing | Selected trust-root version and custodian; key approval event ID, actor, UTC time and expiry; exact 32 public-key bytes and SHA-256 key ID; Ed25519 usage and no-grant audience; selected issuer identity; immutable approval object and authenticated reader principal/credential. The signer cannot approve its own key. A later record's `approvedAt` claim cannot stand in for this earlier event. |
| Signature and after-signing observation | Exact canonical payload bytes, 64 signature bytes, envelope SHA-256, nonce, issuer event/actor/time and selected run/attempt/artifact; an independently sourced completed-envelope event ID/time after signing. The installed verifier must consume the approved public-key bytes, reject malformed keys and signatures, and return an auditable result tied to those exact bytes. |
| Current revocation | Separate authenticated revocation reader, key/version, immutable read event and UTC time immediately before grant issuance; full active/revoked/unknown result. Missing, stale or ambiguous status refuses. The signing record cannot attest its own current revocation state. |
| Nonce custody | A protected backend and named custodian for one-use `(key ID, nonce, envelope digest, run ID, attempt)` reservation, with atomic generation/head, expiry, independent fresh-process readback and a different replay reader credential. A lost reservation acknowledgment is spent or Unknown until independently resolved; no second grant or POST follows from it. No such backend or generation is selected here. |

The trusted verifier must be part of a **new reviewed runnable revision**. The #804 fake byte join defines its data interface only. The owner must decide how the selected cryptographic implementation is packaged under the exact `python3 -I -S` entry and prove that every consumed dependency belongs to the approved archive or runtime closure. A changed dependency, key or runner pin requires a new release selection and reviewer event.

## Installed refusal and replay qualification

These are unrun protected controls. An independent installed observer must retain the selected archive/path/image/runtime before and after each probe and count execution-token reads, nonce reservations, journal CAS, provider POST/PUT and cleanup outside the candidate process.

| Single changed fact | Required result |
| --- | --- |
| No grant, closed ZIP substituted, wrong integrated source/workflow/artifact/runner pin. | Exit/refuse before token, nonce reservation, journal CAS or provider write. |
| Wrong approved public-key bytes, key ID, verifier package hash, malformed or altered signature/payload, foreign run/attempt/issuer, missing prior key-approval event. | Verification fails; zero token, grant issuance, journal CAS or provider write. Include one independently known valid Ed25519 vector and altered-key/signature controls for the actual verifier implementation. |
| Revoked, expired, unknown or stale key status; reviewer equals issuer or dispatch actor; after-signing observation missing or before signing. | No grant issuance or provider write, with the independent event IDs and read times retained. |
| Repeat the same nonce or lose its reservation acknowledgment; reuse its event under another run or artifact. | No second grant or provider write. Fresh-process readback treats ambiguous reservation as spent until resolved by the protected nonce custodian. |

Nonce reservation is distinct from the later [native journal intent](native-effect-admission-matrix.md). Neither a signed envelope nor a nonce reservation authorizes the target-specific #550 POST. That later decision still needs a separately admitted disposable target, selected-repository App token, protected journal CAS, one-use grant for the exact execution run/attempt, and complete native readback.

## First owner action and stop point

The source owner first prepares a reviewable runnable verifier/nonce design and recomputed archive/manifest/closure pins without enabling either workflow. The protected release owner and an independent reviewer can then select the integrated source, cryptographic implementation, trust-root/key approval event, producer artifact and runner facts. Only a separately authorized protected installation may run the installed refusal controls. This packet supplies none of those selected values, so no protected approval is requested now.

After installed refusal, a separate owner can evaluate revocation and nonce custody for grant issuance. The final one-POST authorization remains the distinct #550 reviewer and grant decision, after target, effective App scope, journal generation and native prestate are independently read back. [#545](https://github.com/FS-GG/FS.GG.Coordination/pull/545) disputed receipt, both disabled workflows, gate/index, protected merge, Authority write and cutover remain held.
