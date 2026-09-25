# GS2-09.7 Q6 partial repository-settings readback hold

Status: **source-only, read-only fake transport; one of eleven settings
surfaces observed, no Q5/Q6 receipt or admission**.

`MigrationRollbackSettingsReadback.captureCorePartial` binds the existing
repository-core settings reader to the selected rollback plan. It requires a
protected expected plan seal, repository node ID and raw response digest. The
settings step must name the exact `repository-settings:<numeric ID>:<node ID>`
target. The adapter reads the native repository response twice, compares the
exact response bytes and typed core fields, and refuses a changed raw body,
foreign target or stale expected digest. The result is explicitly
`SettingsAuthorityComplete=false`. Its core response digest is deliberately
**separate** from the rollback step's captured state digest, which must cover
the complete settings authority.

The [repository settings adapter](../../src/FS.GG.Coordination.GitHub/RepositorySettingsAdapter.fsi)
defines eleven applicable surfaces. This bridge reads only the repository
core surface. The protected owner still needs the other ten surfaces,
including inherited rulesets, Actions, environments, security, dependency
and release controls, plus a native raw-to-typed canonicalizer with complete
coverage. The protected [custody port](gs2-09-7-q6-protected-native-custody-port.md)
must retain exact requests, responses, journal order and signer identity for
the selected sandbox. No partial core read can establish the five-domain Q6
rollback or the Q5 nine-authority result. #3690 remains unadmitted; accepted
GS2-09.6 command bytes and live pins remain unchanged.
