# GS2-09.7 Q6 rollback domain order hold

Status: **source-only characterization, no rollback effect or Q6 receipt**.
This branch is stacked on the pure six-cut fixture in draft #615. The accepted
[representative rehearsal](gs2-09-7-representative-rehearsal.md) requires
reverse restoration of exactly five domains: authority snapshot, schedules,
v1 projections, receiver pins, then settings.

The reused GS2-09.6 `GitHubRollbackPlanQualification` previously checked a
contiguous descending numeric order and membership of all five domains. It
did not bind each domain to its required position or reject a sixth repeated
domain. A caller could create a new valid digest and seal for either plan,
then `qualify` would return success. Two independent red-before tests expose
those cases through the public `qualify` function rather than by corrupting
an already sealed plan.

The pure validator now requires the complete domain list in the accepted
reverse order. The existing receipt-prefix and deterministic resume tests
continue to pass. This closes only the source-contract false green. It does
not identify the real sandbox restoration targets, capture their exact
prestate, execute a restore, observe native poststate, prove crash recovery,
or verify the final OperatingV1 epoch. The protected Q5/Q6 gate must still
run each domain on the registered isolated copy, interrupt and resume from
durable chained receipts, and refuse an attempted rollback after `OpenV2`.
No source test or draft PR here can issue the protected acceptance receipt.
