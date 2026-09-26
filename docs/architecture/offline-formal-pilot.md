# Offline formal shard pilot

The first offline CI slice runs one canonical Quint shard in a disposable rootless Podman
container on Main. The container receives only an exported candidate tree, the pinned toolchain
archive and an empty evidence directory. It has no network, host credentials, Podman socket,
persistent workspace or privileged mount. The host supervisor validates the shard receipt and
signs its exact payload only after the container exits successfully. The signing key enters the
supervisor by file descriptor and is never mounted into candidate code.

`eng/offline-formal-pilot.json` pins the active signer and transport contract. For pull requests,
the hosted verifier consumes one signed `authority-reconciliation` receipt; push, merge-group,
schedule and dispatch runs still execute that shard on GitHub-hosted runners. The existing
`canonical-quint` aggregate remains authoritative. The policy pins the image manifest digest and full Main
toolchain archive observed in `.github#3851`; a different local image or caller-selected archive
is refused. Signed evidence is valid for at most six hours so the current saturated hosted queue
can start its verifier without asking Main to regenerate an unchanged exact-head result. Exact
head/base/tree bindings and create-only per-head transport prevent reuse for another candidate;
expiry still refuses an old result for the same head. Activation of the pull-request route
requires these facts:

1. this verifier is present on protected `main`, so a pull request cannot replace its verifier;
2. the committed Main public key and SPKI digest are verified from protected `main`;
3. Main publishes the dedicated create-only evidence ref from `.github#3854` for the exact
   candidate head, base and shard before the hosted verifier starts; and
4. a hosted test accepts one current exact-head envelope and refuses wrong-head, wrong-shard,
   stale and altered-signature envelopes.

The workflow derives its trusted root, candidate root, exact head and base from protected workflow
context. The verifier refuses a policy, verifier or public key outside the protected root,
and reads the hosted verification time from the runner clock. It fetches only the public
signed envelope from the evidence ref, compares the candidate head/base/tree, policy, toolchain and
bound script digests, then publishes the normal `coherent-formal-fragment` artifact. The unchanged
hosted aggregate remains the GitHub check producer and rejects a missing or invalid shard. Rollback
restores the shard to the hosted matrix and disables the join in the same workflow change; the
policy may then return to `shadow`.

Main can rehearse the executor with its protected signing key, a digest-pinned image and a
fresh private copy of the preseeded NuGet cache:

```text
FSGG_OFFLINE_SIGNING_KEY_FD=3 \
  eng/run-offline-formal-shard.sh CHECKOUT HEAD BASE TOOLCHAIN IMAGE PUBLIC_KEY KEY_ID OUTPUT NUGET_CACHE_COPY \
  3<PRIVATE_KEY
```

The wrapper is intentionally host-invoked and does not register a GitHub Actions runner. It exports
the exact candidate tree into a temporary minimal Git repository because the canonical validator
uses `git ls-files`; the staged tree must equal the requested commit tree before Podman starts.
Podman resolves the policy-pinned local image manifest digest before launch and uses `--pull=never`.
`NUGET_CACHE_COPY` is a disposable writable copy; candidate code never sees the host's persistent
cache and cannot fetch missing packages over the network. The container sets `NuGetAudit=false`
because network-disabled restore cannot fetch vulnerability
metadata; the package lock and preseeded cache still govern package resolution. The
supervisor creates evidence timestamps from its host clock only after the shard exits.
Receipt transport is public signed evidence; no registration token, GitHub token or signing key
crosses the candidate-container boundary.

Main's host supervisor publishes the envelope to a create-only Git ref and reads back its object
ID. The Optimistic workflow uses the signed join for pull requests and retains hosted execution
for every other event. The protected-base fetcher reads only `envelope.json` from
`refs/heads/evidence/coordination-offline-formal-pilot/<head>/<base>/authority-reconciliation`
in `FS-GG/.github`. The verifier and policy come from a separate checkout at the event's protected
base; the candidate checkout supplies only source identity. The signed archive digest must match
the protected policy pin; the hosted shard aggregate separately compares semantic toolchain and
input digests to the hosted base shard. A valid signed envelope is converted
to the existing `coherent-formal-fragment-<head>-authority-reconciliation` artifact containing
exactly `candidate-obligation.json`, `partition-plan.json`, and `receipt.json`. The first two files
come from the current hosted `prepare` artifact and are checked against the expected head, base,
and formal partition; the receipt is emitted only after protected-base signature verification.
The existing formal aggregate keeps its complete-fragment validation and is unchanged.

For pull requests, the hosted matrix entry for `authority-reconciliation` skips execution and
artifact upload, while the signed join supplies that shard's normal fragment. `formal-aggregate`
depends on both jobs and requires the complete set of 20 fragments. A missing or invalid envelope
fails the join and aggregate; it cannot silently fall back to an unverified result. The join
refuses a `shadow` policy. Rollback re-enables the hosted shard and disables the join in one
workflow change.

A draft pull request does not start this coherent route until it becomes ready for review;
explicit exact-candidate dispatch remains available. This lets Main publish the exact signed
envelope before the ready event starts the hosted verifier.
