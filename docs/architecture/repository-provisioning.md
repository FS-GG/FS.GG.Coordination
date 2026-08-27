# Repository provisioning boundary

GS2-01.1 treats live GitHub responses as authority. The reviewed desired state is
`eng/repository-settings/desired.json`; the two request documents beside it are the only ruleset payloads
that may be applied. `prestate.json` preserves the canonical state immediately before the final idempotent
ruleset verification batch. `verify.fsx` recomputes its file digest while validating the compact post-state
receipt before independent acceptance.

The apply sequence is deliberately staged: merge CODEOWNERS and this contract, prove the public dependency
graph, attach organization code-security configuration 17, verify its full policy projection and CodeQL default setup,
enable Dependabot alerts and supported repository security, select GitHub-owned Actions, create the signed immutable `v*` tag ruleset, and create the
default-branch ruleset last. Every write is followed by an authoritative reread. A partial, forbidden, lost,
or contradictory response stops the batch.

The `main-protected` ruleset has no bypass actor. It blocks deletion and non-fast-forward updates and requires
one independently approved, current pull request with resolved conversations, CODEOWNERS approval, and the
six exact GitHub Actions checks proven by bootstrap qualification. Receipt capture rereads each ruleset's
detailed endpoint, not the summary collection, so all review and check parameters remain bound. The release-tag ruleset has no bypass,
requires signed `v*` tags, and blocks their update or deletion.

Organization Actions SHA enforcement is not a repository setting. A 403 from that organization endpoint is
bound to the exact `GET /orgs/FS-GG/actions/permissions` operation and recorded as `unsupported`; it is never
projected as enabled. Repository workflow-token defaults are independently bound to the workflow-permissions
endpoint rather than inferred from general Actions permissions. This unit creates no environment, secret,
runtime, webhook, release, tag, package, production subscription, or GS2-01.9 output.

The repository `security_and_analysis` projection reports direct repository settings and can lag or omit an
attached organization configuration's overlay. Configuration 17's repository association and organization
configuration endpoints are authoritative for that overlay; the receipt also preserves the repository projection
without using it to contradict or invent attached policy state. In the current organization entitlement, validity
checks and non-provider secret patterns remain disabled in that effective repository projection even though
configuration 17 requests them; both are explicitly recorded as license-unsupported. Dynamic timestamps are excluded
from desired state. Configuration 17's separate generic-secrets field is `not_set` and is neither conflated with
non-provider patterns nor claimed as an operational or unsupported control.

The first live apply checkpoint retained only a pre-state digest and is not accepted as independently replayable.
The final receipt therefore binds a later, explicit idempotent verification batch: capture the already-protected
state as canonical pre-state bytes, reapply the unchanged reviewed tag and branch rulesets, and capture a fresh
post-state. This supersedes the earlier unaccepted batch without reconstructing or inventing historical bytes.
