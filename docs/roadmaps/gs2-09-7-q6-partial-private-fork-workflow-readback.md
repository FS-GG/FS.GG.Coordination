# GS2-09.7 Q6 partial private-fork workflow readback hold

Status: **source-only, read-only fake transport; one partial Actions settings
surface, no Q5/Q6 receipt or admission**.

`MigrationGitHubRead.readPrivateForkWorkflowSettings` reads the private
repository identity, the [private fork PR workflow settings endpoint](https://docs.github.com/en/rest/actions/permissions#get-private-repo-fork-pr-workflow-settings-for-a-repository),
and the identity again. It retains exact raw bodies and digests and requires
all four documented booleans: run fork workflows, send write tokens, send
secrets and variables, and require approval. It refuses non-private or foreign
identity, malformed or duplicate fields, unexpected pagination, unavailable
403/404 responses and changed raw identity bytes. The endpoint requires
repository Administration read permission; unavailable is never an empty or
disabled policy.

`MigrationRollbackPrivateForkWorkflowReadback.capturePartial` binds two exact
reader passes to the selected rollback plan, independently pinned core and
fork-policy digests, and a final core reread. Controlled tests refuse a changed
second policy pass, stale expected digest, foreign plan before transport and
cross-surface core drift. The result explicitly sets
`SettingsAuthorityComplete=false`.

Organization or enterprise inheritance, public and internal repository
applicability, other Actions settings, and the full eleven-surface
repository-settings state remain outside this proof. Repeated GETs cannot
detect a change and reversal between observations; a protected provider
revision/order authority is still required to close that ABA window. Native
response custody, journal, signer and terminal epoch are also absent. #3690
remains unadmitted, Q5/Q6 remain open, and accepted GS2-09.6 command bytes
and live pins remain unchanged.

## #3690 post-merge hold

Read-only GitHub PR status on 2026-09-25 shows [FS-GG/.github #3690](https://github.com/FS-GG/.github/pull/3690)
as `MERGED` at `2026-09-25T05:35:26Z`, with head
`1b8b4cd5b7bd11c8cb0697b3b3d17d96c4a1862f` and merge commit
`ff425734d277fa54c3d71601da90fe7b22619c15`. The sandbox-route owner
reported a native merge before the OperatingV1 effect-admission stop. That is
an observed process violation for owner adjudication, not a protected
OperatingV1 admission or Q5/Q6 result. No sandbox run or provider readback is
inferred from the merge. The installed workflow, admission record and any
effect exposure require separate protected-owner reconciliation before route
use; this source-only draft does not perform that action.
