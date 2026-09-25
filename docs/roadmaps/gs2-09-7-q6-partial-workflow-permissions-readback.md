# GS2-09.7 Q6 partial workflow-permissions readback hold

Status: **source-only, read-only fake transport; one partial Actions settings
surface, no Q5/Q6 receipt or admission**.

`MigrationGitHubRead.readRepositoryWorkflowPermissions` reads the repository
identity, the [repository default workflow permissions endpoint](https://docs.github.com/en/rest/actions/permissions#get-default-workflow-permissions-for-a-repository),
and the identity again. It retains raw response bodies and digests and accepts
only the documented `read` or `write` default token permission and an explicit
review-approval boolean. It refuses a missing or malformed field, duplicate
JSON member, unexpected pagination, unavailable response, identity drift, or
changed raw identity bytes.

`MigrationRollbackWorkflowPermissionsReadback.capturePartial` binds two exact
reader passes to a selected rollback plan, independently pinned core and
workflow-permissions raw digests, and a final exact core reread. Controlled
tests refuse a changed second policy pass, stale expected digest, foreign plan
before transport, and cross-surface core drift. Its result explicitly sets
`SettingsAuthorityComplete=false`.

This observes only repository `GITHUB_TOKEN` defaults. Organization and
enterprise inheritance, fork and retention settings, the other Actions policy
surfaces, and the full eleven-surface repository-settings state remain outside
this proof. Protected native response custody, signer, journal, and terminal
epoch are still required. #3690 remains unadmitted, Q5/Q6 remain open, and
accepted GS2-09.6 command bytes and live pins remain unchanged.
