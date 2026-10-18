# PROC lossless source feasibility

Status: **source codec prototype; live integration rejected pending the joined
producer/consumer design below**. This does not select an encoding for any command
profile, extend a qualification ordinal, or accept a trace/runtime. The existing
raw trace contract remains unchanged.

`ProcLosslessSourcePrototype` in `eng/retain-choreo-c2-diagnostics.py` is callable
only as an explicitly selected source-feasibility helper. Existing command
execution never calls it. Its selection string is not a root grant. The source
prototype drains an already nonblocking pipe into a fresh private regular output
FD and performs strict single-member gzip decoding. It preserves raw bytes and
uses caller-supplied original monotonic deadlines without renewing them. It adds
no process, shell, pathname lookup for trace inputs, or metadata observation cache.
The caller owns and independently closes all FDs. A failure consumes the codec
object; partial output remains held and no retry or successful manifest follows.

The fixed upper limits are 8 MiB of codec-owned stored bytes, including a reserved
16 KiB manifest allowance, and 32 MiB of captured/decoded bytes across phases.
Tests may select smaller limits, never larger ones. Input saturation can refuse
conservatively before a further read, rather than allocate a byte past the cap to
probe EOF. Decoding refuses CRC failure, truncation, trailing bytes, another gzip
member, expanded overflow and decoded byte/hash mismatch. Decoded callbacks are
**provisional**: only complete successful return establishes decoded identity;
callbacks cannot grant trace or effect acceptance. The manifest explicitly says
`nativeAccepted=false` and `completeTraceQualified=false`.

The focused synthetic controls exercise real nonblocking pipes, the actual codec
methods, all three phases, exact ordered roundtrip bytes, manifest accounting,
incompressible storage overflow, aggregate expansion, corrupt/truncated/trailing
and multi-member streams, decoded identity, missing phases, occupied outputs,
unselected encoding, original-clock expiry, and a first consumer failure without
retry or closing caller FDs. Existing read-drift controls remain unchanged. These
are source controls, not an actual tracer/client qualification.

## Concrete producer obstruction

The current `proc_trace_argv` selects `strace -ff` and a pathname prefix. The tracer
opens one file for each actual PID/thread itself. `proc_measure_once` currently
passes only its one-use handshake FD into the tracer and sends stdout/stderr to a
separate log. Its loop observes readiness, accounting and terminal state; it does
not drain a trace pipe. The complete normal parser reads per-PID files and joins
forks, chronological cwd/dirfd/FD provenance, semantic reads and exact terminal
records. Negative-control traces are intentionally incomplete and remain separate.

A post-hoc gzip pass cannot prevent the raw live aggregate limit from being
exceeded. Deleting or replacing raw files to obtain space would lose original
custody and is not selected. An output-pipe shell or compressor process would add
an undeclared executable, actor, inherited-FD and retirement boundary. A switch
from `-ff` per-PID files to one `-f` stream changes PID framing and output semantics;
it cannot be treated as an interchangeable filename change.

## Required joined source design before live support

1. Define one exact prospective producer contract: pinned tracer options, selected
   syscalls/full string detail, framing including PID and timestamps, inherited
   trace-only write FD, authenticated root subject/fork graph and read/config closure.
   No generic command, shell or compressor process is implied. A source-owned
   in-process codec can avoid another actor only if the existing apparatus becomes
   the sole bounded pipe reader.
2. Multiplex trace draining with the existing handshake/readiness/accounting and
   original work/cleanup clocks. Specify pipe EOF, backpressure, tracer/subject
   failures and kill/retirement ordering. A blocking drain or delayed accounting
   is not equivalent. Complete normal EOF/terminal evidence is mandatory; partial
   negative controls retain their original limited meaning. No event filtering,
   case omission or change to any per-use drift observation is selected.
3. Bind a fresh private namespace and complete manifest/file census to retained
   directory and file capabilities. The current codec checks supplied FD metadata;
   it does not authenticate namespace path bindings, hold every prior artifact FD,
   detect later mutation of an earlier artifact at manifest time, or inventory
   unrelated files. Thus its byte counter is a codec-owned-output bound, **not yet
   proof of the aggregate physical namespace contract**. Those checks must cover
   every phase, partial artifact, manifest, diagnostic, stream close and first cause.
4. Feed the exact decoded complete stream into the existing strict semantic parser
   without dropping records or conflating PID framing/continuations. The prototype
   verifies byte identity but does not perform this parser join. Reader inputs need
   canonical schema/name/phase/inventory/custody binding; caller-supplied checksums
   are not trusted-source authentication.
5. Declare gzip/zlib/interpreter/extension imports and any inherited output path in
   the prospective source/runtime closure. Imports are local to the unused helper,
   so current profiles acquire no new import behavior. A future caller does acquire
   those dependencies. Reconcile derived source pins, the owning plan and Atlas;
   preserve current default refusal and ordinal maximum 4. Source delivery itself
   supplies no machine admission or new runtime slot.

The missing supplier is the existing Execution/PROC source owner for this joined
capture/parser/custody implementation, with root selecting its material contract.
No external operator, new credential, native probe or repeated qualification can
substitute for it. Until that join is designed and reviewed, reject promotion of
this prototype to a production trace representation. A retained incomplete sample
cannot establish complete-path storage, full normal-stream fit, hidden-read
exclusion, transient actor/RSS safety or native acceptance.
