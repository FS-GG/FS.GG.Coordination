# FourD native acquisition source contract

This is the bounded source mapping for [Delivery 1051](https://github.com/FS-GG/FS.GG.Coordination/issues/1051).
It prepares the existing qualification-local `FourD.Typed` producer for FourD's native-image route.
It does not establish consumer activation, publication, native acceptance or retirement. The
[owning plan](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-10-01-typed-administrative-fsharp-quint.md)
continues to govern the complete .3/.4 promise.

## Current production mapping

The source inspection uses FourD PR48's protected merge
[`f52a9bb1c4fc74ccb8bc38e0d10d321711c08eae`](https://github.com/FS-GG/FS.GG.FourD/commit/f52a9bb1c4fc74ccb8bc38e0d10d321711c08eae).
Live refs and operation facts must be refreshed before adoption. Coordination#917's accepted source
and the retired workflow remain historical evidence; Coordination#956's retirement is not reversed.

| Production seam at that revision | Current observation/decision owner | Replacement boundary and remaining proof |
| --- | --- | --- |
| `.github/workflows/fourd-native-image-qualification.yml` | Fixed first `workflow_dispatch` on protected FourD main; runner identity/job readback; one finite window | Preserve runner and original-attempt safeguards. Bind the final root packet to this workflow, source, producer closure and operation before effects. No run is admitted by this document. |
| `native-image.py:resource_admission` | Python combines current CPU/memory/filesystem observations and chooses admission | Keep OS probes as adapters; move the decision into the native-profile typed owner. Reservation/authority remains external to resource measurement. |
| `native-image.py:exact_source` | Git HEAD/tree/cleanliness and `source-inventory.py --git` | Preserve Git custody observations. `validate-native-source` independently verifies the complete reconstructed bytes against the expected source inventory. Revalidate at consumption; a previous validation is not an immutable filesystem handle. |
| `prepare-image.py:archive_source` and reconstruction | Held output stream receives `git archive`; `tarfile` reconstructs a plaintext build context | Existing capsule sealing/reconstruction must be joined to this actual route. Checkout and plaintext archive alone do not fulfill encrypted transport. Preserve raw commit, modes, umask077, child settlement and held-inode cleanup. |
| `native-image.py:fresh_load_inner/fresh_load` | Python joins archive/manifest/config identities and verifies post-namespace identity | Keep Podman, file and namespace observations as adapters. Native-profile F# admission/result transitions must consume the actual joins and retain provisional-versus-final identity. |
| `native-image.py:qualify_p2` | Executes the separate production P2 assembly through the FourD FSI adapter | P2 remains the execution authority. A completed FSI child is not positive P2 operation evidence. Consume exact source/image/result evidence and retain unknown or unacknowledged outcomes. |
| `native-image.py:cleanup`; workflow final acceptance/disposition | Python removes scoped stores and combines P2/acceptance/cleanup step outcomes | Preserve guaranteed emergency cleanup and resource ownership. Typed classification must separately consume actual child/namespace/store/credential retirement facts. A successful cleanup cannot manufacture an operation result. |

The workflow pins Coordination P2 source `9d99573628ee31e966a61a28b9b36f7414b10f33`,
tree `e96774e11969b2730ebcf40ab47a06d7a17d46d6`. That pin identifies P2, not the
replacement acquisition producer. FourD's source revision, the Coordination producer revision and
P2's revision are three distinct identities. Existing `typed_policy.py:executable` defaults its
producer source check to `GITHUB_SHA`; it cannot be reused unchanged when the consumer SHA belongs
to FourD. `Join.fs` and `RunObservation.fs` still select the retired Coordination workflow and its
attempt2/capacity+qualification job protocol; none is a native-image admission contract.

## Implemented reconstructed-source constructor

The existing producer gains the closed `validate-native-source` command. Its request schema is
`fsgg.fourd.native-source-request/1`; the only additional fields are `expectedRevision`,
`expectedTree`, `expectedInventorySha256`, `observedRevision`, `observedTree` and `sourceRoot`.
Expected values come from the authenticated final packet; observed revision/tree come from the
existing Git acquisition adapter. The command does not authenticate either producer or packet.

`NativeSource.acquire` constructs a private validated snapshot only when revision/tree observations
match their expected values and the complete acquired directory hashes to the expected inventory.
It enumerates all files, including any extra files, and uses FourD's exact UTF-8 path, NUL, file
bytes, NUL inventory algorithm. UTF-8 lexical order matches Python's Unicode scalar ordering,
including non-BMP names. Strict UTF-8 encoding refuses invalid names. The constructor rejects
symlinked roots/ancestors/entries, nonregular held inodes, more than 8192 files, more than 16384 total
filesystem entries, nesting deeper than 128 levels and more than 64 MiB
of acquired file bytes. This bounded profile covers the inspected FourD tree's 179 regular blobs
and 1776671 source bytes; symlink-bearing future sources need an explicit compatible contract.

Linux `open` uses `O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`, and `statx` inspects the held descriptor
before reading. FIFO input refuses without waiting for a writer. Neither validation nor refusal
modifies source bytes or replacement targets. Filesystem mutation after validation still requires
consumer revalidation and the owning lifecycle's invalidation transition; this source slice does
not supply a race-free build handoff or cleanup ownership protocol.

The successful projection schema is `fsgg.fourd.native-source/1`. It carries revision, tree,
inventory SHA256, file/byte counts and an identity derived from the three joined source identities.
Duplicate, missing, extra or incorrectly typed JSON fields refuse through the existing closed CLI
boundary. This is a stateless acquisition constructor; the existing lifecycle reducer and canonical
Quint model are unchanged. Tests exercise the compiled command, an independent Python inventory
oracle, Unicode ordering, stale bindings, changed/missing/extra bytes, raw local Git commit
reconstruction under umask077, symlink replacement preservation, FIFO and oversized sparse files.
The wrong source/inventory controls first establish that the same source/setup passes, so a setup
failure cannot satisfy the negative. The path-filtered `fourd-typed-source.yml` source-only gate
builds the existing producer with the repository's pinned SDK 10.0.400 and executes these controls
on a standard hosted runner within five minutes. It does not enroll a native route. There is no
cross-job reuse, image work or mutable shared output; local focused checks took about three seconds
including an incremental build. Cold hosted setup/restore cost remains unmeasured.

## Producer acquisition and budget decisions still required

The producer remains nonpackable. Its existing runtime closure is exactly `FourD.Typed`,
`FourD.Typed.dll`, `FourD.Typed.deps.json`, `FourD.Typed.runtimeconfig.json` and `FSharp.Core.dll`.
The existing binding hashes those five files and records the Coordination source SHA. A native
successor must bind that producer source/tree, qualified exact runtime closure, binding bytes,
runtime/SDK provenance and final packet independently of FourD/P2 identities, and verify acquired
bytes before invoking them. A self-authored matching hash map alone is not publication or custody.
Reuse FourD's qualified source/runtime acquisition route. If an installed CLI/package is selected,
publish and pin that exact existing producer through its normal route; a local build is not
installed delivery. No package or source/runtime adoption is selected by this constructor.

The native workflow has a 90-minute full window and a 20-minute preflight-only window. The legacy
operation protocol validates at most 2700 seconds, with its run protocol ending new effects at 2460
seconds. These contracts are incompatible as a whole-operation replacement. Root must select a
native-profile lifecycle/time contract before consumer integration; this slice expands neither
limit and does not divide the operation into renewing budgets. Preserve the existing monotonic
full window, phase deadlines, P2 allowance, cleanup reserve and original attempt identity. Any
stateful native-profile extension requires corresponding canonical Quint/F# traces and negatives.

## Regression and acceptance remainder

The maintained `test_source_capsule.py` scenarios cover real sealing/raw commit reconstruction,
mode/commit/tree/inventory/order changes, traversal/duplicate/link/gitlink refusal and cancellation
during reconstruction. `test_qualification.py` retains child-scope settlement, unacknowledged effects,
held-original-inode wiping with replacement preservation, plaintext created before unseal failure,
cleanup failures and sticky cancellation. `test_typed_policy.py` retains stale admission, missing
identity, forged state, incomplete cleanup and false-success refusal. These maintained regressions
remain source evidence for their original route. The relevant scenarios must also run through the
native replacement; merely rerunning the retired adapter's tests does not establish parity.

The root-wrapper regression set additionally retains raw bounded HTTP identity, original/target
attempt discrimination, complete job census, cancellation intent/result separation, aggregate
monotonic budget, signal-during-retirement and unknown remote settlement. Its frozen packet
identities remain in the [accepted source evidence](ops-typed-01.3-source-integration-20261001.md).
A native successor must map applicable invariants to its actual first-dispatch/local-child protocol;
the old attempt2/job names are not copied into a new workflow.

Remaining Delivery 1051 acceptance includes protected reservation, encrypted source transport,
source/root/producer packet adoption, native typed lifecycle integration, effect-free final-packet
preflight, all relevant replacement regressions, refusal/rollback, native result and cleanup/credential
retirement, and parity-proven retirement of only the superseded Python policy. Preserve FourD#37's
accepted scope and original failed/refused attempts. A capable runner is required for the final native
effect; its absence does not block this source work. No source PR closes Delivery 1051.
