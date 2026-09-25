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
