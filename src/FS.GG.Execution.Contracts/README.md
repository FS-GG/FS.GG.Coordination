# Portable process contracts

This non-packable net10.0 project is V2-PROC-01.2 source work. It depends only on
BCL/FSharp.Core. It defines bounded values, live monotonic budgets, a shared memory
byte ledger, pure lifecycle observations and session interfaces. It launches no
process and implements no production backend, native lease, journal or service.

`Types.fsi`, `Budget.fsi`, `BoundedOutput.fsi`, `Lifecycle.fsi` and `Validation.fsi`
are the source API. `ExecutionBudget` and `LifecycleState` representations are hidden.
Commands are literal full paths and ordered argv. Environment mode is explicit;
caller executable trust/version policy stays outside. Request validation rejects
NUL, ambiguous environment names and structural/resource bounds. Diagnostics use
codes, field names and evidence references; validation errors never echo argv or
secret values. Callers must supply sanitized diagnostic text and evidence references.

Create one budget in a live clock domain. Work expiry and total expiry are fixed;
cleanup reserve is part of total, not additional time. Integer conversion cannot
extend an expiry. Remaining-budget probes reject wrong domains or pre-start ticks;
the reducer also rejects regressions after an accepted tick. Descriptions cannot
restore lost budget authority. No conversion from persisted UTC creates a new budget.

`BoundedOutput.create` allocates only validated prefix/tail storage. Append charges
observed input once through a shared lock before retention. Evidence/protocol bytes
also spend the total ledger; only terminal evidence can spend the carved reserve.
Overflow or an oversized chunk requires retirement and retains no bytes from that
chunk. The first failure stays latched; bounded TerminalEvidence may still spend its
carved reserve and remaining total. Its charged category is explicit in snapshots. Captured bytes remain bytes.
Prefix/tail do not overlap; omission counts are
observed minus retained, not unread child output. Snapshot copies cannot mutate the
sink. This interface has no user callback, file writer or unbounded queue. Frame and
incremental-decoder adapters must enforce the declared frame/line bounds separately.
Managed scheduling and lock contention are not hard real-time containment.

The reducer separates launch, leader exit, stop request, stream EOF, local tasks,
workload emptiness and reaping. Launch is consumed once, ambiguity does not retry,
and release requires obtained capabilities/limits. Stable process generations are
evidence, never numeric-PID authority. First failure survives later failure records;
collections and result metadata have finite bounds. A 2KiB reserve within the existing
64KiB metadata ceiling preserves the first minimal cause; omitted diagnostic details
and omitted secondary failures are counted separately. `Complete` requires positive
proof for the declared cleanup obligations; pending EOF is not supplied by exit0.

An existing caller retains `IProcessSession` before launch. Backend methods spend
the original budget and return bounded snapshots without abandoning authority.
`TryRelease` is nonblocking and refuses unresolved local handle/task obligations;
release alone does not prove unknown descendants ended. The interfaces are not a
qualified implementation. A required retained-custody profile must refuse before
launch if its existing owner cannot retain the actual session/resources. No hidden
reaper, indefinite holder, automatic `IDisposable` success or new owner registry
is provided here. Later administrative retirement needs separate admission.

The additive Coordination adapter maps all ExecutionKey fields without narrowing.
Its original LaunchIntent/InputDigest/journal remain authoritative for their scope;
input digest is not substituted for source digest, and mappings confer no authority.

The selected plan is the existing .github shared-process-supervision owning plan
plus its .2 contract selection. .2-A source review, .2-B deterministic build/tests
and .2-C disposable fixture evidence are separate boundaries. No publication,
consumer adoption or existing custody/profile change follows from these sources.

Observation scope is the prepared InvocationId in canonical Guid D format. The
platform-qualified ProcessGeneration carries that operation scope alongside its
platform/birth identity; backend-specific kernel scope joins remain in its qualified
provenance. Known/absent proofs require the original clock domain and operation
window. Pre-effect capabilities/limits have no target generation and must be
revalidated at the release stamp. Process/reader/cleanup proofs carry the exact
observed generation. Mutable capabilities, whole limit bundles and cleanup bundles
replace older values; uncertainty cannot inherit old Known proof. Positive violations
stay latched. Exact-generation exit and terminal reader outcomes retain immutable
original provenance. Two bounded private terminal reader outcomes prevent a later
EOF from replacing an omitted failure proof. Secondary retirement-action details
may yield to the first cause/current proof, with each omission counted. The phase table keeps late starts Retiring after stop/return
and duplicate terminal evidence Terminal. A release request is not an observed
start: confirmed launch refusal may report NoEffect; ambiguity cannot.

When failure-bearing metadata cannot fit, the reducer first compacts diagnostic
details, then reports omitted positive proof as Unknown. An oversized cleanup
bundle can project Incomplete with unavailable custody and counted unresolved
generations. Private actual-completion history only rejects contradictory later
bundles; it never restores omitted public proof or grants release authority.
The first cause keeps its originating event phase and original timestamp.
