# Repository provisioning boundary

GS2-01.1 treats live GitHub responses as authority. The reviewed desired state is
`eng/repository-settings/desired.json`; the two request documents beside it are the only ruleset payloads
that may be applied. `verify.fsx` validates the compact post-state receipt before independent acceptance.

The apply sequence is deliberately staged: merge CODEOWNERS and this contract, prove the public dependency
graph, enable Dependabot alerts and supported repository security, select GitHub-owned Actions, create the signed immutable `v*` tag ruleset, and create the
default-branch ruleset last. Every write is followed by an authoritative reread. A partial, forbidden, lost,
or contradictory response stops the batch.

The `main-protected` ruleset has no bypass actor. It blocks deletion and non-fast-forward updates and requires
one independently approved, current pull request with resolved conversations, CODEOWNERS approval, and the
six exact GitHub Actions checks proven by bootstrap qualification. Receipt capture rereads each ruleset's
detailed endpoint, not the summary collection, so all review and check parameters remain bound. The release-tag ruleset has no bypass,
requires signed `v*` tags, and blocks their update or deletion.

Organization Actions SHA enforcement is not a repository setting. A 403 from that organization endpoint is
recorded as `unsupported`; it is never projected as enabled. This unit creates no environment, secret,
runtime, webhook, release, tag, package, production subscription, or GS2-01.9 output.
