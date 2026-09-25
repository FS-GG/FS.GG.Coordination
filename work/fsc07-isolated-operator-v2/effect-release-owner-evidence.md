# Protected owner evidence for the closed v2 effect scaffold

Status: non-authorizing source packet, 2026-09-25. This is the decision input for a future protected **closed scaffold** release, not an installation request or a native effect approval. No integrated protected revision, reviewer, producer run, artifact ID, runner or approved manifest event has been selected.

## Exact draft inputs

| Input | Verified draft revision or local digest | Use and limit |
| --- | --- | --- |
| [#563 inspect-only workflow](https://github.com/FS-GG/FS.GG.Coordination/pull/563) | `55ca33b7b45fd81889ffbccb4fbd4939f352b7d3` | Separate workflow; `if: ${{ false }}` and placeholder pins. It cannot release or run this effect scaffold. |
| [#573 inspect-only release chain](https://github.com/FS-GG/FS.GG.Coordination/pull/573) | `e457a3ffd2b3a3c682e8b22ffa08ba9f9d75cf44` | Source-only archive/runtime closure controls for a different inspect artifact. No protected runner observation transfers to this scaffold. |
| [#614 closed scaffold](https://github.com/FS-GG/FS.GG.Coordination/pull/614) | `fe23cb9f1712c24059104f1141bc00306475eadd` | Native source bytes are external to its deterministic archive; the installed entry refuses every command. |
| [#617 source/approval join](https://github.com/FS-GG/FS.GG.Coordination/pull/617) | `ed8b924a8f48403b4943b371685c4f29103ba025` | Two fake ports join exact source/artifact bytes and a distinct manifest approval event; output remains `authorized:false`, `can_dispatch:false`. |

The [local scaffold manifest](effect-scaffold-manifest.json) records archive SHA-256 `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`, manifest-file SHA-256 `3f000d8e60f5ab8a13d4d5d8289d1963ef9661acef232b9e0e35c9cfa72d5acf`, builder SHA-256 `51753b3b773437db34453ab1778b41817a7beda6851199a228fe5cab46733de0`, external native-source SHA-256 `5dea86000e990e66884b5e203687b8ff241c56b8197d1e5625d9f54cc1c16d02`, and disabled execution-workflow SHA-256 `7406efeabb5aca0504e8ae39a514038d7473b6fd97a66d2e4efcc5b2c94799db`. These are draft local bytes, not approved protected pins. Source or workflow edits require a fresh manifest and independent review.

## Independent protected observations still required

| Owner and source | Exact evidence to return | Refusal if absent or inconsistent |
| --- | --- | --- |
| Protected Coordination source reader | One integrated commit and tree in `FS-GG/FS.GG.Coordination`; file-object IDs and exact bytes for builder, external native source, disabled execution workflow and manifest. The reader's App installation, repository selection and effective read scope must be authenticated outside its `scope()` claim. | Wrong repository, commit/tree relation, file object, byte digest or shared execution credential. |
| Producer run and artifact observer | Reviewed producer workflow revision/path/hash; run ID/attempt, head commit, producer actor, completed result, artifact ID, downloaded archive bytes and independent artifact SHA-256. Bind each object to the integrated source revision. | Artifact from another workflow, actor, attempt, source tree or mutable URL; a candidate-supplied hash cannot repair a mismatch. |
| Independent release reviewer and approval-event observer | Active reviewer identity distinct from producer and dispatch actor; immutable event ID, UTC time, exact integrated revision/tree, producer run/attempt/artifact, approved manifest-file digest and expiry. Record the observer's separate principal and credential. | Self-approval, absent event, stale approval, wrong manifest, replayed event or reader/producer credential reuse. |
| Protected runner and separate installed observer | Immutable runner image and attestation, interpreter and runtime-closure digests, exact installed archive path/object identity and pre/post byte hashes. Run the closed entry under the pinned `python3 -I -S`; capture status 78 and external counts of token reads, journal reads/writes, CAS, POST and cleanup attempts, all zero. | Symlink, replacement or transient mutation, wrong interpreter/image, archive mismatch, any effect-port access or incomplete independent readback. |

The [#617 preflight](effect-release-preflight.md) can compare fake source and approval observations but cannot authenticate their origin, prove Git commit/tree membership, identify the producer workflow or observe a protected installation. The [#614 byte verifier](closed-effect-scaffold.md) requires an approved manifest digest as input; it cannot approve that digest itself. Source-level matching and a local clean install are not protected acceptance evidence.

## Decision sequence and owners

1. **Source owner working now:** prepare a reviewed integration proposal containing the chosen closed scaffold and release controls, with an exact commit/tree and producer workflow design. Completion signal: a reviewable immutable source selection with member/manifest bytes; no protected merge is requested by this packet.
2. **Protected release owner and independent reviewer waiting:** after an explicit source selection and protected release decision, return the source, producer/artifact and approval-event objects above from separately authenticated observers. Completion signal: exact IDs, digests, principals and times on one run/attempt, plus independent negative controls for wrong source, actor, manifest and approval.
3. **Runner owner waiting:** only after those readbacks and a separate installation decision, run the closed no-grant probe and return external zero-effect counters and installed path/runtime observations. A local self-report of `liveEffects:0` is insufficient.
4. **Native target/App, CAS, grant and result owners waiting:** a different reviewed **runnable** effect artifact, selected disposable target and one-attempt authorization are needed before their roles begin. The closed scaffold and its release cannot open the [#550 native window](https://github.com/FS-GG/FS.GG.Coordination/pull/550). The Authority owner has no action from this packet.

Both proposed workflows remain disabled. #545's disputed receipt, the #550 one-POST hold, typed gate/index, receiver pin, Authority boundary and cutover remain unchanged. No credential, grant, protected installation, provider or journal effect is authorized here.
