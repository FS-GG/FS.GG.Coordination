# Offline formal shard pilot

The first offline CI slice runs one canonical Quint shard in a disposable rootless Podman
container on Main. The container receives only an exported candidate tree, the pinned toolchain
archive and an empty evidence directory. It has no network, host credentials, Podman socket,
persistent workspace or privileged mount. The host supervisor validates the shard receipt and
signs its exact payload only after the container exits successfully. The signing key enters the
supervisor by file descriptor and is never mounted into candidate code.

`eng/offline-formal-pilot.json` keeps this slice in `shadow` mode. The existing hosted shard and
the `canonical-quint` check remain authoritative. The policy pins the image manifest digest and full Main
toolchain archive observed in `.github#3851`; a different local image or caller-selected archive
is refused. Signed evidence is valid for at most six hours so the current saturated hosted queue
can start its verifier without asking Main to regenerate an unchanged exact-head result. Exact
head/base/tree bindings and the planned immutable per-head transport prevent reuse for another
candidate; expiry still refuses an old result for the same head. Activation requires a second reviewed change
after all of these facts exist:

1. this verifier is present on protected `main`, so a pull request cannot replace its verifier;
2. Main's public key and SPKI digest replace the pending signer anchor;
3. Main and fdev agree the dedicated evidence ref from `.github#3854`, including bounded polling
   and immutable per-head paths; and
4. a hosted test accepts one current exact-head envelope and refuses wrong-head, wrong-shard,
   stale and altered-signature envelopes.

The active workflow must derive its trusted root, candidate root, exact head and base from protected
workflow context. The verifier refuses a policy, verifier or public key outside the protected root,
and reads the hosted verification time from the runner clock. It will fetch only the public
signed envelope from the evidence ref, compare the candidate head/base/tree, policy, toolchain and
bound script digests, then publish the normal `canonical-quint-shard-<id>` artifact. The unchanged
hosted aggregate remains the GitHub check producer and rejects a missing or invalid shard. Rollback
is the policy-only return to `shadow`, which restores that shard to the existing hosted matrix.

Main can rehearse the executor after supplying a digest-pinned image and a throwaway key:

```text
FSGG_OFFLINE_SIGNING_KEY_FD=3 \
  eng/run-offline-formal-shard.sh CHECKOUT HEAD BASE TOOLCHAIN IMAGE PUBLIC_KEY KEY_ID OUTPUT \
  3<PRIVATE_KEY
```

The wrapper is intentionally host-invoked and does not register a GitHub Actions runner. It exports
the exact candidate tree into a temporary minimal Git repository because the canonical validator
uses `git ls-files`; the staged tree must equal the requested commit tree before Podman starts.
Podman resolves the policy-pinned local image manifest digest before launch and uses `--pull=never`. The
supervisor creates evidence timestamps from its host clock only after the shard exits.
Receipt transport is public signed evidence; no registration token, GitHub token or signing key
crosses the candidate-container boundary.

The declared evidence ref has no fetcher or writer in this shadow slice. The tests use a fabricated
schema-valid passing receipt to exercise envelope and trust-boundary refusals; they are not formal
execution proof. A real Podman run, protected key installation, immutable per-head transport and
current-head hosted workflow join remain activation gates owned by Main and the follow-up PR.
