# GitHub live-operation handling

GS2-09.4 classifies every live operation captured by the immutable migration manifest. The qualification
boundary covers the exact families `claim`, `queued-write`, `review`, `delivery`, `release`, and
`cutover-adjacent`. Each ordered obligation has exactly one decision with its stable global identity,
source state, source bytes, and dependency-set digest.

The closed disposition union is `Drain`, `Migrate`, `Park`, or `Invalid`. A drained operation binds its
completion receipt and authority fence. A migrated operation retains the global ID and binds the target,
schema, payload, and mapping. A parked operation binds its parking location, resume condition, payload,
and evidence. An invalid operation is explicit and binds a stable code, reason, and evidence.

The normalized digest and seal cover the accepted GS2-09.3 receipt, immutable-manifest and typed-transform
digests and seals, planner fingerprint, complete obligation population, every disposition, and timestamp.
Missing, duplicate, reordered, malformed, identity-changing, or altered entries refuse. Q5 and Q6 run
the controlled plan and fresh-process deterministic replay with complete generated and independent control
inventories.

This source qualification does not execute, drain, migrate, or park provider operations. It performs no
provider read or mutation, creates no archive, executes no rollback or migration, changes no receiver or
setting, and grants no cutover, OpenV2, acceptance, completion, or successor authority.
