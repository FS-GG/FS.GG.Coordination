# GS2-09.7 Q6 partial custom-properties readback hold

Status: **source-only, read-only fake transport; two partial settings surfaces,
no Q5/Q6 receipt or admission**.

`MigrationRollbackCustomPropertiesReadback.capturePartial` extends the
[partial core-settings bridge](gs2-09-7-q6-partial-settings-readback.md).
It requires independently pinned raw digests for the repository core response
and the organization custom-property schema plus repository values. The
existing custom-property reader validates repository identity, definition
types and values, refuses missing or unauthorized endpoints, and keeps exact
raw responses. The bridge requires two identical custom-property captures
and a final core-settings read matching the initial core response. A
controlled negative showed the first version could report two surfaces
after core settings changed during the custom-property reads; the final
read now refuses that drift. It retains all three core and both custom
captures, with `SettingsAuthorityComplete=false`.

This is still an observation of two partial surfaces, not one atomic native
settings snapshot. A provider could change and return to the same raw bytes
between reads. Enterprise-inherited custom-property definitions are refused
by the existing reader. Nine further settings surfaces, the complete
rollback state digest, protected native parser/custody/journal/signer and
terminal epoch remain required. The protected owner must show a complete
candidate-inaccessible copy inventory and authoritative endpoint readback.
No source-only result here closes Q5/Q6. #3690 remains unadmitted; accepted
GS2-09.6 command bytes and live pins remain unchanged.
