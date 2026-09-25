# GS2-09.7 native recovery owner admission

Status: **read-only decision packet, 2026-09-25**. The signed and unsigned
recovery paths remain hold-only. This packet supplies no protected signer,
reader, clock, token, sandbox observation, Q5/Q6 result, or acceptance receipt.

## Current source boundary

At Coordination draft [#807](https://github.com/FS-GG/FS.GG.Coordination/pull/807),
`MigrationProtectedIssueCensusSignedRecovery.inspectSigned` first obtains one
complete native-attempt snapshot between equal pre/post heads, then checks a
P-256 signature over that **same** snapshot's content seal, attempt ID, head
generation, native resource and a short clock window. The unsigned
`MigrationProtectedIssueCensusAttemptRecovery.inspect` remains a separate
hold-only path. The signed path returns a recovery hold, not permission to
retry, revoke, release a token, or issue a receipt. Its ports and test keys
are injected fakes; neither path is connected to an installed provider reader
or signer. See the [signed entry point](../../src/FS.GG.Coordination.Cli/MigrationProtectedIssueCensusSignedRecovery.fs),
[snapshot reader](../../src/FS.GG.Coordination.Cli/MigrationProtectedIssueCensusAttemptRecovery.fs),
and [signature verifier](../../src/FS.GG.Coordination.Cli/MigrationProtectedIssueCensusNativeAttestation.fs).

The installed `.github` `main` read at `2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4`
still exposes the [Q4 sandbox workflow](https://github.com/FS-GG/.github/blob/2e553e41e58ee2f5e27aedcffc7403ce50e7cdd4/.github/workflows/github-substrate-v2-sandbox-qualification.yml).
It mints an App token and passes it to candidate scripts without a visible
OperatingV1 effect-admission read. [PR #3690](https://github.com/FS-GG/.github/pull/3690)
merged at `ff425734d277fa54c3d71601da90fe7b22619c15` outside that admission
boundary. The protected owner must adjudicate the incident; a merge and a Q4
mint proof are not GS2-09.7 admission. The earlier
[owner handoff](gs2-09-7-q5-protected-census-owner-handoff.md) also records that
the Q4 diagnostic sandbox is not a declared migration copy.

## Protected facts and owner decisions

| Owner | Exact evidence to install and read back | Refusal without it |
| --- | --- | --- |
| OperatingV1 admission owner | Adjudicate #3690, then supply a current protected decision binding workflow blob, candidate commit, run ID, attempt, nonce, selected sandbox repository and Project, and allowed effect scope. Read it from OperatingV1 authority independently of candidate input. | No Q5/Q6 invocation or token exposure from the existing workflow alone. |
| Sandbox and App owner | Declare the isolated migration copy, numeric and node IDs, initial complete nine-authority census and epoch. Independently pin App ID, installation ID, selected repository ID, permission digest and API origin. | The diagnostic Q4 subject, a caller-supplied target, or a foreign App installation cannot become the rehearsal target. |
| Protected store owner | Name immutable resource and artifact digests for the census store, claim journal, release reservation, handoff marker, token vault and native-attempt store. Show candidate read/write denial and the shared atomic compare/consume/mark transaction, durable across crash. | A fake `Describe` response, candidate-writable file, lost CAS result or unrelated store head supplies no durable authority. |
| Native reader owner | Pin the protected reader and native-attempt resource/artifact; retain raw provider request, status, headers, body, provider identity, page chain, object IDs and read ordinals. Prove complete authoritative readback and stable pre/post head for the exact attempt. | A typed record, unsigned digest, missing page, unknown result or changed head remains unknown. |
| Signer and clock owner | Install a candidate-inaccessible P-256 private key; independently pin its public SPKI digest and signer artifact. Pin a candidate-inaccessible monotonic UTC clock resource/artifact and maximum attestation age. Sign only the exact native snapshot produced by the protected reader after durable handoff. | A test key, self-supplied SPKI, stale signature, foreign signer or untrusted time cannot authenticate recovery. |
| Recovery and Q5/Q6 owner | Join the native attempt ID and signed snapshot to the same reservation, claim, run/attempt/nonce, vault and journal heads. Supply authoritative native token/revocation outcome and durable finalizer scheduling for every unknown result. Then retain the six interruption cuts, five rollback-domain readbacks, two rounds and complete nine-authority receipts. | Neither signed nor unsigned recovery hold advances to a receipt or clears Q5/Q6. |

These are installation and evidence requests, not installed pins. The
[representative rehearsal contract](gs2-09-7-representative-rehearsal.md)
requires the complete provider run and independent controls before acceptance.

## Read-only negative controls for the protected owner

Before any protected effect, independently inject a wrong workflow or
candidate, stale run/attempt/nonce, foreign repository or App installation,
candidate-readable vault, candidate-writable native store, changed signer or
clock artifact, and a valid signature over a different snapshot or generation.
Each must refuse before token handoff. Inject lost marker, native call and
revoke results; a fresh process must retain the unknown outcome, schedule
recovery durably, and never retry a possibly exposed native attempt. Repeat
with a missing terminal page, duplicated record, changed pre/post head and
stale clock window. Native bytes, not candidate-authored fixtures, must decide
the result.

No source-only verifier can prove the installed ACL, provider origin, shared
transaction, signer custody, clock monotonicity or complete native readback.
Those facts are the smallest protected-owner input needed before this recovery
contract can be qualified. Sandbox dispatch, token handoff, Q5/Q6, receiver
pin, protected merge, Authority write, receipt and cutover remain held.
