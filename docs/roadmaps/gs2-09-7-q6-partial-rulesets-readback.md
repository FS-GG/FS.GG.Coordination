# GS2-09.7 Q6 partial repository rulesets readback hold

Status: **source-only, read-only fake transport; one partial settings surface,
no Q5/Q6 receipt or admission**.

`MigrationRollbackRulesetsReadback.capturePartial` binds the existing native
repository-owned branch and tag rulesets reader to the selected rollback plan
and [partial core-settings readback](gs2-09-7-q6-partial-settings-readback.md).
It requires independent core and rulesets raw-response digests. The rulesets
list and every detail response are read twice; their ordered request URIs,
continuation links, raw body digests, repository identity and parsed state
must match. A final core-settings read must match the initial core response.
The result is explicitly `SettingsAuthorityComplete=false`.

Controlled tests refuse a changed second list page, a stale expected rulesets
digest, a foreign plan before transport, and core drift across the ruleset
reads. Existing native reader tests refuse incomplete pagination, changed
detail, inherited or push rulesets, and hidden bypass state. Those refusals
do not establish absence of those settings; they leave the full surface
unobserved.

The complete eleven-surface repository-settings state digest, protected native
parser and response custody, journal, signer and terminal epoch remain
required. This read-only source proof is not a Q5/Q6 native qualification.
#3690 remains unadmitted; accepted GS2-09.6 command bytes and live pins remain
unchanged.
