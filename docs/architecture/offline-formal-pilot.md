# Offline formal shard pilot

The first offline CI slice runs one canonical Quint shard in a disposable rootless Podman
container on Main. The container receives only an exported candidate tree, the pinned toolchain
archive and an empty evidence directory. It has no network, host credentials, Podman socket,
persistent workspace or privileged mount. The host supervisor validates the shard receipt and
signs its exact payload only after the container exits successfully. The signing key enters the
supervisor by file descriptor and is never mounted into candidate code.

`eng/offline-formal-pilot.json` now pins an active signer and transport contract, while the hosted
join job remains disabled. The existing hosted shard and the `canonical-quint` check remain
authoritative. The policy pins the image manifest digest and full Main
toolchain archive observed in `.github#3851`; a different local image or caller-selected archive
is refused. Signed evidence is valid for at most six hours so the current saturated hosted queue
can start its verifier without asking Main to regenerate an unchanged exact-head result. Exact
head/base/tree bindings and the planned immutable per-head transport prevent reuse for another
candidate; expiry still refuses an old result for the same head. Routing one shard to the hosted
join requires a separate reviewed workflow change after all of these facts exist:

1. this verifier is present on protected `main`, so a pull request cannot replace its verifier;
2. the committed Main public key and SPKI digest are verified from protected `main`;
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
cache and cannot fetch missing packages over the network. The
container sets `NuGetAudit=false` because network-disabled restore cannot fetch vulnerability
metadata; the package lock and preseeded cache still govern package resolution. The
supervisor creates evidence timestamps from its host clock only after the shard exits.
Receipt transport is public signed evidence; no registration token, GitHub token or signing key
crosses the candidate-container boundary.

The declared evidence ref has no writer in this shadow slice. The tests use a fabricated
schema-valid passing receipt to exercise envelope and trust-boundary refusals; they are not formal
execution proof. A real Podman run, protected key installation, immutable per-head publication and
current-head hosted rehearsal remain activation gates owned by Main and the follow-up PR.

The Optimistic workflow now carries a hard-disabled `offline-formal-shadow` join. It schedules no
hosted job while the policy is `shadow`, leaves the `authority-reconciliation` hosted matrix shard
in place, and does not alter `formal-aggregate` dependencies. After activation, its protected-base
fetcher reads only `envelope.json` from
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

Activation must atomically disable the hosted matrix entry for this one shard, enable the join,
and add it as a `formal-aggregate` dependency after Main's anchored public key reaches protected
main. The evidence writer must create the per-head ref once without force, publish the fixed
envelope path, and read back its object ID. The join currently refuses a `shadow` policy even if
someone removes its false workflow condition. Other event types continue with hosted execution.
