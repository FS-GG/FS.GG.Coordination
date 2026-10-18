# Prospective PROC lossless trace source

Status: **joined source implementation with inert controls; runtime admission
refused**. The current raw 8 MiB contract, profiles and maximum qualification
ordinal 4 remain unchanged. No command-line profile selects this representation.
The explicit helper profile is a source-control input, not an execution grant.

## Producer and lifetime

`proc_qualification_apparatus(..., trace_profile=...)` joins the prospective store
to the existing three-phase measurement apparatus. It requires the exact
`PROC_FUTURE_TRACE_SOURCE_PROFILE` in both caller and authority, qualification
role and `nativeGet=false`. A missing, widened or crossed selection refuses.
`execute_proc_command` refuses a future selection without the authenticated
measured context before claiming or modifying the existing operation slot.

The prospective producer changes `strace -ff -o PREFIX` to `-f -o FIXED_FIFO`.
The apparatus exclusively creates one 0600 named FIFO per phase under a fresh
0700 `lossless-traces` directory and opens its nonblocking reader before starting
the existing tracer. Only the existing one-use handshake FD is inherited through
`Popen`; no trace-output FD, shell or compressor actor is added. The tracer opens
its own named output. In the reviewed tracer source, its shared output is closed
in the tracee and its retained stream descriptor has close-on-exec semantics.
This source design is not proof of behavior in a real tracer run.

The existing monitor drains at most four 64 KiB chunks per turn before performing
its existing readiness, capacity, per-use read-drift, affinity, actor and lifetime
checks. Initial FIFO EOF does not establish completion. Observed output followed
by EOF cannot reopen. Producer terminal observation and drained EOF are both
required to seal a gzip member. A monitor or producer error retains the original
first cause; sibling stream, FD, artifact and reporting cleanup remain independent
and their errors remain secondary. Unknown producer settlement leaves partial
private artifacts and cannot produce a successful manifest.

The original WORK deadline governs operation and draining. After terminal
observation, the original TOTAL deadline funds seal, verification, parsing and
reporting. The codec's temporary retirement clock is restored in `finally`.
Neither deadline is renewed. Store close failures make the prospective result
failed-or-unknown even when a manifest was already retained. All prospective
results retain `rootAccepted=false`, `qualificationAccepted=false` and
`publicationAuthorized=false`.

## Bytes, custody and parsing

The store holds the namespace directory and every FIFO, gzip and manifest FD.
It observes fresh FD/path identity, owner, type, link count and mode, complete
fixed-name census and prior-artifact hashes. FIFO I/O timestamps are not mistaken
for replacement; FIFO device/inode/owner/mode/link/size are checked anew. The
ordinary disk census accepts a FIFO only through its exact authenticated
capability; raw operation census still refuses special files.

The aggregate stored-byte cap is 8 MiB across all three gzip members, partial
outputs and manifest, with 16 KiB reserved for the manifest. This is a file-size
bound, not an 8 MiB physical allocation guarantee; the existing separate disk
census still accounts for logical and allocated bytes. Aggregate captured bytes
and aggregate decoded bytes each have a strict 32 MiB cap across all phases.
Conservative saturation refusal is allowed before an additional EOF probe.
No raw original is compacted, replaced or deleted. No event is filtered to fit.

The strict reader checks one complete gzip member, CRC, encoded and decoded
identity, count, metadata, EOF and complete census. Truncation, corruption,
trailing bytes, additional members and expansion overflow refuse. Decoded
callbacks remain provisional until the complete reader succeeds.

Combined trace bytes remain the physical artifact. PID/timestamp framing creates
per-PID projections in lexical `qualification.trace.<PID>` filename order for
the same semantic engine as raw files; per-PID
hashes are explicitly derived projections. Existing fork, chronological cwd,
dirfd, FD, source-read, continuation and terminal checks remain. Equal-time event
ordering preserves the original lexical-filename/per-PID serial rule, including
different-width PIDs such as 10 and 2. Numeric PID sorting would change equal-time
lineage decisions; an actual raw/future parser regression retains that case. Complete normal
traces reject incomplete final lines. Aborted control tails remain in the encoded
artifact with their count/hash and never establish complete trace or read closure.
Future namespace metadata access does not authorize reading its gzip/FIFO content.

Projection allows at most 32 PIDs and 64 KiB per physical line. The future semantic
parser decodes one line at a time, avoiding whole-file Unicode/split copies. It
refuses above 131072 physical records or a 128 MiB conservative retained-object
budget: four times each actual line-string allocation plus 4096 bytes per retained
event for derived argument/path/FD/provenance/read objects. These are refusal and
planning bounds, not a hard RSS proof. Captured/decoded byte caps do not by
themselves bound Python object overhead. The separately sampled aggregate
apparatus/tracer/subject RSS guard remains necessary for eventual runtime admission.

## Source evidence and remaining admission

The synthetic controls exercise actual FIFO/store/codec functions, strict reader
failures, original WORK/TOTAL separation, complete namespace drift, prior-artifact
mutation, exact raw/future parser agreement and hidden-read refusal. They invoke
the real `proc_measure_once` and one-use handshake with inert process/kernel
adapters, preserving original monitor plus independent close/report failures and
unknown settlement. Separate controls invoke the actual negative-control
retirement helper, actual capacity/read-drift/census joining, and the full
qualification apparatus's parser/manifest/result finalization. A cleanup-only
failure stays failed-or-unknown; a self-reported successful subject cannot grant
root or qualification acceptance. Current CLI refusal is checked by its exact
refusal cause, with no process launch or operation-slot change.

These fixtures do not execute a tracer, client, native provider, scoped App or
credential operation. They do not establish full normal-stream fit, tracer FIFO
framing/backpressure behavior, kernel no-birth safety, observational equivalence
or machine resource conformance. Original failed/unknown qualification evidence
and all holds remain. A retained incomplete sample fitting gzip proves none of
those outcomes.

Before any future runtime profile is selected, the existing PROC owner/root must
reconcile its exact tracer/interpreter/zlib Python and extension/library source
closure, bootstrap capacity, directory/negative lookup inventory, original finite
clocks and stored/expanded budgets. The existing immutable instrumentation and
current raw bootstrap contract do not implicitly cover new codec imports or the
new producer grammar. A separate reviewed profile and explicit native admission
would be necessary; this source delivery supplies neither and creates no fifth
qualification ordinal.
