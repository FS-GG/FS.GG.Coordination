# GitHub rollback plans

GS2-09.6 qualifies a deterministic rollback plan from the exact accepted GS2-09.5 receipt, immutable
manifest, sealed history, current roadmap authority, and captured restoration inputs. It is a
repository-local source contract and does not execute rollback or contact GitHub.

The plan starts at the last rollback-eligible epoch, `VerifiedV2`, and ends at `OperatingV1`. Its exact
reverse sequence restores the authority snapshot, schedules, v1 projections, receiver pins, and settings.
Every step binds a stable target identity, the captured state digest, and the restore payload digest.
All five domains are mandatory; missing, duplicated, forward-ordered, malformed, or changed steps refuse.

Successful steps produce a receipt bound to the plan seal, step identity, result digest, and previous
receipt digest. Receipts must be an exact prefix of the plan. Resume therefore selects only the next
uncompleted reverse step; gaps, reordering, foreign-plan receipts, altered results, and broken chains
refuse. A complete prefix means rollback has no remaining step.

The Q5 `github-rollback-plan-contract` and Q6 `github-rollback-plan-recovery-contract` run independent
complete control inventories and deterministic fresh-process replay. The implementation contains no
provider client or effect executor. Qualification does not change settings, receiver pins, projections,
schedules, authority, epochs, or acceptance state.
