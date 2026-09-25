# GS2-09.7 Q6 partial Actions-policy readback hold

Status: **source-only, read-only fake transport; one partial Actions surface,
no Q5/Q6 receipt or admission**.

`MigrationRollbackActionsReadback.capturePartial` binds the existing native
repository Actions reader to the selected rollback plan and the
[partial core-settings readback](gs2-09-7-q6-partial-settings-readback.md).
It requires independently pinned core and Actions response digests. It
reads the Actions policy twice, including the exact selected-actions
allowlist endpoint when the policy selects one, and requires identical raw
responses. A final core-settings read must match the initial core response.
Controlled tests refuse a changed selected allowlist body, a missing
selected endpoint response, a stale expected Actions digest, a foreign plan
and core drift across the policy reads. The result is explicitly
`SettingsAuthorityComplete=false`.

The reader covers repository Actions core policy and its conditional selected
allowlist. Organization inheritance, workflow-token policy, fork permissions,
retention and other Actions settings are outside it. The complete
eleven-surface repository-settings state digest, protected native parser,
response custody, journal, signer and terminal epoch remain required. This
read-only source proof is not a Q5/Q6 native qualification. #3690 remains
unadmitted; accepted GS2-09.6 command bytes and live pins remain unchanged.
