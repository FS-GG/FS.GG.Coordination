# Inert pilot permit boundary

`PILOT-PermitV1` prepares a finite ownership-transfer protocol without enabling a host. The
canonical catalogue and executable model remain in `Protocol.md`; the F# pilot project consumes
that generated catalogue entry and supplies typed event sourcing and persistence contracts.

A permit names one canonical `WorkItemIdentity`, one generation, stable and pilot principals, an
expiry, attempt and resource ceilings, and ordinary versus recovery capacity. Issuing a permit
does not transfer ownership. The stable route first persists current evidence that it is quiesced
and excluded. The pilot becomes owner only after a trusted readback capability returns a fresh,
generation-bound transfer acknowledgement and that event is durably appended.

Each assignment consumes one attempt and monotonic token, runtime, and cost reservations. It also
records the O0 reservation identity and deadline and, when planning is involved, the O1 planning
budget digest. Reservations are not released by heartbeat loss. Heartbeat loss records an unknown
outcome; fresh operation-bound readback must reconcile it before further assignment or return.
Pause and revoke preserve ownership, unknown outcomes, and active reservations.

The PostgreSQL adapter uses separate pilot metadata, stream, event, and inbox tables. Append is a
single serializable transaction and recovery is one repeatable-read snapshot. Both enforce the
shared O0 backup identity, schema, generation fence, and read-only state plus the pilot schema
metadata. Recovery verifies hashes, versions, contiguous head/tail state, stream identity, and
legal typed event history. A replacement process treats historical readback as non-current and
requires a fresh, durable reconnect before it accepts another assignment. Each later assignment
also requires a new trusted admission readback whose subject, ownership generation, purpose, and
operation identity match the command; the store backup-generation fence is a separate recovery
control and does not substitute for this ownership-generation evidence.

This assembly contains no provider writer, runner dispatcher, listener, hosted service, deployment,
or production credential. `FS.GG.Coordination.App` remains inert and nonpackable. OperatingV2,
the hosted-writer amendment, qualified Main configuration, authenticated provider evidence, and a
reviewed finite pilot remain activation prerequisites.

The PostgreSQL qualification covers the supported typed lifecycle, deduplication, sequence CAS,
semantic corruption, generation fences, and process replacement. It does not qualify physical
failover, power loss, full-disk behavior, or a production hosted route.

The canonical formal lane keeps two different progress claims. `normalProgressStep` checks return
under explicit stable-authority and weak-fairness assumptions. The fault-inclusive lane checks
safety, and the directed coverage lane reaches transfer, dispatch, settlement, unknown-outcome
reconciliation, restart/reconnect, pause, revoke, and return witnesses. TLC can stutter at `Paused`
in the fault-inclusive state space, so this change does not claim unconditional fault-path
liveness. The additive model increased generated root artifacts from about 5.1 MiB to a measured
8.804–8.832 MiB; the profile keeps a finite 10 MiB ceiling while retaining all prior state,
sample, time, and memory limits.
