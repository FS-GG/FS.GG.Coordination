# Prospective PROC lossless trace source

Status: **joined source implementation with inert controls; native runtime
disabled**. Raw qualification retains its 8 MiB contract and ordinals 1–4.
The real command entrypoint also supports a separately selected, prospective
lossless runtime contract for exactly global ordinal 5. The unchanged maintained
admission does not enable it. The source-only helper profile remains distinct.
Neither contract nor a successful source test grants execution.

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

Before any future native runtime is selected, the existing PROC owner/root must
reconcile its exact tracer/interpreter/zlib Python and extension/library source
closure, bootstrap capacity, directory/negative lookup inventory, original finite
clocks and stored/expanded budgets. The existing immutable instrumentation and
current raw bootstrap contract do not implicitly cover new codec imports or the
new producer grammar. A separately reviewed runtime contract and explicit native
admission remain necessary. The integration below supplies a dormant contract;
it does not create or launch a fifth qualification attempt.

## Prospective source closure join

The future apparatus now requires a separate `futureTraceSourceClosure` value in
both the existing bootstrap evidence and its existing authority. It derives the
expected value from the selected descriptor, after the existing bootstrap and
instrumentation checks and before creating the trace store or launching a tracer.
The old raw route does not acquire this requirement. This source-closure join
alone enables no CLI profile, ordinal, release flag or instrumentation exception;
the runtime selection below binds it separately.

The closed source contract selects the already retained Python3.14 executable,
libpython3.14, Python zlib extension, libz1.3.2 and selected strace7.2 bytes. Each
role must equal its declared path/size/mode/SHA256 pin, belong to the declared
read allowlist, and join the relevant directory entry. The libz alias must join
its directory entry and resolved target; all three selected glibc-hwcaps misses
must join their declared ancestor identity. The complete declared read inventory,
including its other libraries, aliases, misses and runtime-data contract, has a
canonical digest. This does not replace the existing complete pin/bootstrap join
or any fresh `proc_read_drift`, capacity, process or lifetime observation.

The manifest and its digest use the domain
`fsgg.proc.future-trace-source-closure/1`. The digest travels through the held
store context and existing one-use measured handshake. The measured caller
refuses a missing or changed closure, including an otherwise valid namespace
context without the digest. Context observations recompute the declared join;
the retained digest is not a metadata-observation cache. Lower-level unbound
codec/parser fixtures remain source controls and cannot satisfy this handshake.
Matching a manifest is not authentication by itself: its actual use depends on
the existing descriptor/bootstrap/authority chain and a separately selected
future runtime contract.

The prospective producer case pins release `strace.c` SHA256
`06525ee83048d477139312ad96e750585354e5f9f56663bdde0f959dce5b16dc`
from release archive SHA256
`4bde6246926890dcee824f6e6ac42a06752f47d77e5097d86e3c0d6d4b709fe5`.
Its source distinguishes separate per-PID output (`984–988`) from follower PID
prefixes (`899–919`, `3293`). The fixed named FIFO is opened by the same tracer;
the output stream gets close-on-exec (`679–690`) and the pre-exec subject closes
that stream (`1682`, `1870`). Only the existing handshake descriptor is inherited
from the apparatus. No `|`/`!` output form, `/proc/self/fd` writer inheritance,
shell or compressor actor is selected. Source reasoning about these paths is
distinct from observed FIFO/backpressure behavior in an actual tracer.

The original instrumentation boundary remains unchanged. Its explicit changed-
argv review requirement is not waived: this is the additional prospective source
case for `-f` and fixed-FIFO output, not a new native admission or a promotion of
the old boundary. The existing source bound of two possible startup probes,
unknown unsampled births/lifetimes/peak RSS, pidfd-only apparatus signaling and
early-abort private holds remain. No complete instrumentation census is claimed.

Focused controls exercise missing or crossed role pins, directory/alias/negative
joins, borrowed bootstrap inventories, missing/changed manifests, full-inventory
digest changes, refusal before store/launch, and actual caller/handshake downgrade
refusal. Existing actual FIFO, shared parser, first-cause, independent cleanup and
WORK/TOTAL controls remain. These are synthetic source controls without a real
tracer, client, native provider or credential operation.
The existing required `compiler-and-tests` runner selects both
`FutureSourceClosureControls` and `FutureTraceJoinedControls` through its actual
unittest invocation, with verbose case names for readback. This adds the 24
focused assertions to the maintained gate; actual hosted results remain a
separate exact-head observation.
The measurement-bound unit fixture uses its test runner's observed allocation
and separately verifies real mismatched-affinity refusal; a fixture's allocation
does not select or widen the native qualification profile.

The source owner can derive the prospective manifest from the retained historical
inventory without altering any original. A future supplier must still provide a
fresh source/profile-bound bootstrap packet through the existing PROC owner/root:
current selected runtime/source identities, directory/alias/negative inventory,
actual capacity, original finite clocks and the explicitly reviewed encoding
contract. Historical inode observations and old clocks are not current evidence.
Current raw maximum4 remains binding; its four consumed attempts cannot be reused
or extended by this source manifest. A different qualification admission requires
the existing root-owned decision before any native launch. Publication, scoped-App
observation and compatible installed FsQuint adoption retain their own gates.


### Canonical public temporary-file census

The first corrected hosted selector passed all 64 source controls at `6dabbf23`.
A separate canonical base command then refused when TLC retired a temporary
`TLC.tla` between directory enumeration and `lstat`. Its model's no-error message
was not command acceptance: the original command retained `FileNotFoundError`,
exit `-15`, `commandAccepted=false` and observed-empty-group cleanup. The overall
Bootstrap run failed even though its six required contexts completed successfully.

Only the public canonical command's declared `mutableScope.tmpdir` opts into
advisory treatment of descendant `ENOENT` during a disk sample. The original TMP
root's device/inode/mode/owner is fixed before launch and checked for every sample,
before and after inventory, and when an entry disappears. A vanished descendant
or subtree increments `vanishedEntries`; `byteCoverage=observed-only` explicitly
means its unobserved bytes are unknown. A zero count reports `sampled`, never
continuous or complete peak coverage. Existing observed byte/population/time caps
still refuse, including a population cap on attempted entries. No unknown bytes
are assigned zero or claimed to meet a complete disk bound.

The default census remains strict for PROC and held namespaces. A missing TMP
root, replacement, missing non-TMP entry, permission failure, special file, input
pin drift or expired original deadline still refuses. The closed optional public
diagnostic reader admits legacy samples and the explicit two-field extension;
unknown fields or invalid coverage/counts remain omissions. Controls inject the
original race through the actual public caller with an inert owner receipt,
retain an unrelated original first cause and cleanup, and reject TMP replacement.
They also exercise directory disappearance, strict census, caps, deadlines,
permissions and exact legacy/new diagnostic projection. They do not rerun TLC,
trace or qualify a native provider, and do not alter raw maximum4 or CLI refusal.

## Disabled enforcing runtime contract

[Delivery 1037](https://github.com/FS-GG/FS.GG.Coordination/issues/1037) extends the
existing command entrypoint with `traceRuntimeProfile`. Its exact
`fsgg.proc.lossless-qualification-profile/1` value is defined by
`PROC_LOSSLESS_RUNTIME_PROFILE` in
[`retain-choreo-c2-diagnostics.py`](../../eng/retain-choreo-c2-diagnostics.py).
It is valid only for the existing non-effect client-qualification operation,
exactly global ordinal 5, and `nativeGet=false`. It cannot rename the original
operation, reuse ordinals 1–4, admit raw ordinal 5, or select ordinal 6. Omitting
the new field preserves the existing raw contract and roots.

The runtime contract retains aggregate 8 MiB stored and 32 MiB captured/decoded
bounds, a 16 KiB manifest reserve, 32 PIDs, 64 KiB physical lines, 131072 records
and the 128 MiB conservative retained-object bound. The existing 90-second work
plus 10-second cleanup ceiling, one CPU, sampled 512 MiB RSS, 1 MiB logs and
16 MiB disk contract remain. These are refusal bounds; they prove neither stream
fit nor hard aggregate memory, allocation, containment or timely cleanup.

The descriptor, authority and bootstrap must carry matching exact runtime values.
Authority also needs explicit `losslessQualificationApproved=true`, existing
non-effect approval, `publicationAuthorized=false`, `nativeGetApproved=false`,
and the unchanged source-only codec profile and complete source-closure manifest.
These observations do not substitute for fresh root admission.

A separate top-level `losslessQualification` selection in the existing admission
document must contain exactly `enabled`, `profile`, `operation` and
`qualificationAttempt`, joined to that same contract and ordinal. Missing or
false `enabled` refuses before capture or canonical claim. The maintained
admission file remains unchanged and has no such selection, so the new runtime
is disabled. Existing recovery keys and publisher flags are unchanged; adding a
new field beneath the closed recovery object would still refuse.

The real `execute_proc_command` route passes the selected codec profile to the
existing apparatus. It validates bootstrap, complete read drift and source
closure, then rejoins bounded original descriptor/authority bytes, original
canonical input/result and held directory identities before the exclusive claim
or store creation. A consumed canonical result never becomes another attempt.
The held FIFO context carries a domain-separated runtime-profile digest alongside
the complete source-closure digest. The actual one-use handshake refuses a
missing or crossed runtime digest and cannot downgrade this operation to raw
capture. The existing normal and two negative-control phases, shared owner,
strict parser, first cause and independent cleanup remain in use.

Controls in the already selected `FutureTraceJoinedControls` and
`FutureSourceClosureControls` reach the real entrypoint and apparatus with inert
process/kernel/input adapters. They cover successful joined collection, default
refusal, exact ordinals, every closed profile field, bootstrap/source drift,
adjacent descriptor/authority replacement, replay, runtime-bound handshake,
overflow and independent close failure. They do not launch a real tracer, client,
native API operation or CLR. The positive entrypoint control fails against the
previous source at its source-only refusal. Source tests and a merged capability
do not establish native fit or acceptance.

Programme/root retains the separate decision to admit any genuine ordinal 5,
including exact current source/runtime and bootstrap inventory, resources,
original finite clock, custody and retirement. All four consumed unaccepted
originals, historical Unknowns, original publisher effects and parent
[1026](https://github.com/FS-GG/FS.GG.Coordination/issues/1026) publication,
installed adoption, containment and consolidation obligations remain unchanged.
