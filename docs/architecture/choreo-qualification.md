# Hosted-writer Choreo qualification decision

Date: 2026-09-18. Owner: FS-GG Coordination.

The two hosted-writer formal qualification entries use the four-process Choreo
model. `O2HostedWriterModel` remains an executable abstraction test. It is not
removed or treated as a runtime transition oracle. The other seventeen formal
entries and the published Quint 0.32.0/profile boundary remain unchanged.

## Qualification boundaries

`O2HostedWriterChoreoProgressQualification` runs the existing non-faulting
listener schedule through all seven effects. Its weak-fairness progress property
requires eventual native-readback completion. Simulation depth is 80 rather than
the flat model's 20 because journal requests, replies, and Host acceptance are
separate steps. TLC exhausts the 64-state graph; this is not a claim that 80-step
random sampling proves liveness.

`O2HostedWriterChoreoFaultQualification` nondeterministically initializes either
the provider/Claim partition or the reachable post-Claim Runner/ProcessWork
partition. Its immutable lane flag selects the same bounded schedule as C2.
Both partitions admit one proven-absence retry and one completed crash/recovery
cycle. TLC explores their disjoint union: 1,162 distinct states. The unrestricted
model step and all operation-envelope comparisons remain unchanged. The seven
effect witnesses and explicit stale, duplicate, wrong-identity, and journal
sequence scenarios remain required by the base gate.

Both entries retain the 10,000-state, 10,000-transition, 10,000-sample,
300,000-millisecond, 6,144-MiB, and 6-MiB-artifact limits. The fault simulation
retains depth 20. These are qualification limits, not production retry limits.

The deliberately unsafe completion control inserts native completion without
its prerequisite effects and must violate safety. Removing native readback
must violate progress after Merge. Removing external application must violate
progress while the operation awaits its external effect. Each removed-transition
control has two matching TLC diagnostics and two deterministic Rust ITF
projections; retained evidence binds source, toolchain, and ordered states.

Quint 0.32.0 cannot reliably flatten unrelated modules following parameterized
Choreo aliases. The validator typechecks the complete canonical source, then
mechanically selects its pre-Choreo prefix for legacy roots and the pinned
Choreo region for Choreo roots. Both are projections of the same literate source;
there is no second authored model or committed `.qnt` file.

## Parity and intentional differences

`eng/verify-choreo-c5-parity.py` exports six actual legacy Quint runs and compares
observations with the regenerated Choreo fixtures. It never implements a
transition function. It compares stage, operation ID/status, pause/readback,
claim, candidate, branch, pull-request, merge, and native-readback facts.
Consecutive identical observations collapse only message-level stuttering.

| Scenario | Shared comparison | Explicit difference |
| --- | --- | --- |
| Happy path | 22 admitted/durable milestones across all seven effects | Choreo exposes 64 raw states; legacy has atomic journal/provider transitions |
| Applied response lost | 5 milestones through unknown and reconciliation | Choreo separates remote application, loss, journal acknowledgement and settlement |
| Proven absence | 5 milestones, no dispatch while unknown, absence does not advance | Choreo permits retrying the claim itself; legacy requires an already current claim |
| Same-operation retry | 8 relative milestones with stable operation identity | Legacy retries ProcessWork, Choreo fixture retries Claim; compare relative stage, not equal effect IDs |
| Restart | 4 distinct pause/authority/resume observations | Choreo additionally requires a separately witnessed journal-recovery gate |
| Missing native readback | 19 milestones stop at Merge | Legacy adapter-completion claim has no authority; Choreo requires the provider message |
| Duplicate/stale/foreign messages | No extra completion; named negative witnesses and production replay | These messages are not represented by the flat model; Choreo adds explicit rejection |
| Retry revisions and journal ordering | Same operation and durable-before-effect rules retained | Choreo adds exact revision, sequence and delayed-response rejection |

Choreo starts after admission. Capacity, permit expiry, delivery/execution windows,
and claim loss are not independently varied by its message model. This is an
explicit scope restriction, not a claim of equivalence over all legacy states.
The base gate therefore also exhausts the unchanged legacy `step` safety graph
(2,376 distinct states, including those conditions), within its retained
10,000-state/transition bounds and a 150-second process ceiling. Production
admission, deadline, and effect fencing remain checked by their existing tests.
No legacy safety obligation is deleted to obtain Choreo qualification.

This establishes scenario refinement and bounded fault evidence, not a general
bisimulation proof or a proof of Akka, PostgreSQL, or GitHub internals. Recording
adapters provide external facts while the production Host/journal/actor seams
make policy decisions. C4 replay covers memory and real PostgreSQL journals.

## Baseline measurements

Formal state and transition counts come from each completed TLC graph. Sample,
time, memory and artifact counts come from the canonical shard receipts. The
smaller root baseline is also refreshed: its state counts are the union observed
in 100 seeded Rust traces, and its test-root counts are the actual passed tests.
Those sampling counts are not exhaustive proofs. Root command time and peak RSS
are measured separately from the canonical command profile; artifact sizes are
its emitted JSON sizes. The `mutation-saga` root currently executes seventeen
tests, and the ordered protocol-stream sample reaches four distinct states.

## Evidence refresh

Only source-derived identities and regenerated qualification artifacts move.
The compiler's behavioral contract and published toolchain identities stay
fixed. Accepted historical operational receipts are not rewritten. Choreo's
vendored upstream bytes and license are unchanged. The C3 trace bytes remain
unchanged; their source pin is advanced to the commit containing the new
qualification helpers.

Toolchain preparation explicitly fixes `CGO_ENABLED=1` when rebuilding the
pinned Go literate extractor. The exact expected binary digest is unchanged;
the setting prevents an ambient Go configuration from producing different
bytes. A fresh download/build preparation must pass the existing byte checks.

Installed O3 adoption remains a separate operation. This qualification decision
does not install, activate, dispatch, or publish a runtime dependency.
