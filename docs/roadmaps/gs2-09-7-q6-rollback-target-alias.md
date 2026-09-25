# GS2-09.7 Q6 rollback target alias hold

Status: **source-only, no native restore or Q6 receipt**. The accepted
[representative rehearsal](gs2-09-7-representative-rehearsal.md) restores
authority snapshot, schedules, v1 projections, receiver pins and settings as
five distinct domains with exact captured prestate and chained receipts.

The pure rollback plan already required five ordered domains and distinct
step IDs, but accepted two domains with one identical `TargetIdentity` and
different restore payload digests. Its controlled restore fixture keyed state
by that identity, so the duplicate collapsed two planned subjects into one
map entry. An independent public-`qualify` test failed red-before on this
alias. The validator now requires distinct target identities for all five
steps, preserving the existing plan encoding for well-formed plans.

The installed interpreter must define each target at the exact restorable
authority surface, not merely the shared repository containing it. This
source guard cannot prove that an asserted target name corresponds to a
native repository, Project, schedule or receiver. Protected execution still
needs exact prestate, restore dispatch, native poststate/epoch readback,
fresh-process resume and zero duplicate effects for every domain. #3690
remains unadmitted, and no controlled result here closes Q5/Q6.
