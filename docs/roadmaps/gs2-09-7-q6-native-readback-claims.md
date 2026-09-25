# GS2-09.7 Q6 five-domain readback claim hold

Status: **pure source contract; no native observer, restore or protected
receipt**. The accepted
[representative rehearsal](gs2-09-7-representative-rehearsal.md) requires
reverse restoration of authority snapshot, schedules, v1 projections,
receiver pins and settings, with native state and epoch readback after
fresh-process recovery. A complete receipt prefix alone records journal
progress; it cannot prove those five native targets were restored.

`GitHubRollbackReadbackQualification.verifyClaims` first requires the
externally expected plan seal and a complete valid receipt chain through
`resumePinned`. It then compares exactly five ordered readback claims with
the plan's domain, step and target identities, captured state digests and
each receipt's result digest. A terminal claim must say `OperatingV1` and
bind the same plan seal. Missing, duplicated, foreign, incomplete or
unauthorized claims, changed state, a forged result digest, an incomplete
receipt prefix, a foreign plan and a stale epoch all refuse in controlled
tests. The historical GS2-09.6 gate script and command bytes are unchanged.

These records are deliberately named **claims**. Their `Authorized` and
`Complete` flags are data supplied by a future adapter, not evidence of
actual authority. The protected interpreter must independently authenticate
the observer, pin the exact sandbox repository/Project and plan source,
derive canonical state digests from complete native reads, bind the receipt
results to those reads, and reread the terminal epoch from its protected
authority. A stale read of the original prestate could have the same digest;
the observer must prove post-restore freshness and revision/fence binding.
It must retain request IDs, raw response hashes and durable
receipt lineage across each interruption and fresh process. The pure checker
cannot supply those facts, execute a restore or issue Q5/Q6 acceptance.
#3690 remains an unadmitted source observation; no sandbox effect or live
receiver pin is installed by this draft.
