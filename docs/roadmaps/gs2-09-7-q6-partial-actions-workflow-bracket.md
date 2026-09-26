# GS2-09.7 Q6 partial Actions and workflow readback bracket

Status: **source-only, read-only fake transport; a bounded partial settings
observation, no Q5/Q6 receipt or admission**.

Separate partial [Actions policy](gs2-09-7-q6-partial-actions-policy-readback.md)
and [workflow-token defaults](gs2-09-7-q6-partial-workflow-permissions-readback.md)
readbacks can each be internally stable while describing different moments.
`MigrationRollbackActionsWorkflowReadback.capturePartial` takes one selected
rollback plan and independently pinned core, Actions and workflow-policy raw
digests. It reads core twice, Actions policy, workflow defaults twice, Actions
policy again, and core at the end. It requires exact raw and parsed equality
across each pair and the same repository identity throughout. This prevents
an observed Actions-policy change while the workflow-default reads occur.

An independently written control first failed compilation without the bridge.
Fake-transport tests then require the bounded baseline and refuse a changed
second Actions body, a changed second workflow body and final core drift.
The result explicitly sets `SettingsAuthorityComplete=false`.

The bracket is a temporal observation, not an atomic provider snapshot: a
change and reversal between reads is not detectable. It does not cover
organization or enterprise inheritance, fork and retention settings, the
other settings surfaces, or protected native response custody, journal,
signer and terminal epoch. The complete eleven-surface settings authority
and Q5/Q6 native qualification remain open. #3690 remains unadmitted;
accepted GS2-09.6 command bytes and live pins remain unchanged.
