# Inert phase/result protocol controls

`controls.fsx` exercises the actual compiled `PhaseResultProtocol` source entirely
in memory. `paired.fsx` additionally consumes a bounded, SHA256-pinned canonical
vector file from the owning domain adapter and compares the start and all three
ACK frames byte for byte. Supply the compiled production assembly with the
existing SDK400 FSI `--reference` option. Execute only through the maintained,
admitted source qualification route and its original resource/deadline limits.

These controls create no child processes, native leases, credential descriptors,
HTTP requests or real persistent result. Their retention callbacks are explicit
inert stand-ins. Domain payload assertions and the actual durable writer/readback
belong to the consumer. An equal frame digest establishes byte identity only.

The generic session requires independently prepared canonical binding bytes. It
checks the child echo against those exact bytes; it cannot authenticate an App,
workflow, process birth or clock adapter. The caller's domain validator runs on
its retained parser/reporting task, outside the custody loop and session lock.

`BeginRetention` starts and retains one persistence/readback task per pending ACK
gate. `TakeAck` requires exact successful readback and the live original cutoff,
and yields ACK bytes at most once. `RetentionTasks` and `LocalTasks` must join the
existing execution result's reader/task and cleanup accounting. A blocked task
remains outstanding after refusal or expiry. Taking an ACK conservatively means
possibly delivered; a later failed write cannot clear the retained possible effect.
Returning ACK bytes does not open child ingress. The trusted channel owner must
complete the full bounded ACK write and call `ConfirmAckSent` before reading the
next phase. The decoder records barriers at wire ingress, including partial
prefixes, so later processing cannot authorize bytes received before that ACK.
Ungated token-owned/revocation and revoked/terminal pairs may share a read.
The owning execution session must retire on `Refused` within its original budget.
A validated terminal arriving before a pending ACK may be retained as failure
evidence while the original work budget remains; it invalidates that ACK and
leaves any writer task accounted for. A failed or uncertain local callback or
channel write closes further callbacks, including terminal failure retention.
Every new validation, persistence and ACK
write must enter before its original applicable work cutoff. The total reserve
settles already-started tasks and permits retirement and observation only. Bytes
returned by an already-started read after admission closes are counted without
decoding. A late callback cannot revive the ACK or session qualification. The consumer writer also owns
monotone replacement so an older callback cannot erase a newer retained snapshot.

The finite production profile remains unavailable. These checks establish source
codec/reducer behavior only; native supervision, durable result replacement,
child-directed credential custody and installed consumer acceptance remain separate.


The internal `FiniteCustodyComposition` source is intended to compile in the
production Execution project and is tested directly by independent xUnit Facts in
its existing `FiniteCustodyWindowTests` module. The production source grants
internal access only to `FS.GG.Coordination.Orchestration.Execution.Tests`, following
the repository's named friend assembly convention. This deliberate testability
boundary also exposes the assembly's other internal members to that one test
assembly. No public constructor or native profile is enabled. The supplemental
scripts contain no linked or loaded copy of the composition.

The selected maintained CI seam is a separate focused execution of
`FullyQualifiedName~FiniteCustodyWindowTests` through `compiler-and-tests.sh`, with
its own fresh `finite-custody-inert.trx` census. The production project includes the module after its phase-protocol dependency,
and the compiler gate gives this focused cohort its own timestamp, results
directory and fresh TRX census. The host-allocation cohort and its census remain
separate. The shell budget adds the measured three total and distinct lines
(858 to 861 total; 643 to 646 distinct), preserving the existing one-line total
headroom. Inert source qualification requires a passing fresh census from the
exact final candidate. It does not establish native or paired domain acceptance.

The composition keeps ingress separate from potentially blocked validation and
persistence. It accounts for start/ACK writers, readers, parsers, persistence and
retirement tasks through cleanup. Full confirmed writes and exact durable readback
precede ACK confirmation. Fake children advance only after the internal confirmed
ACK observation; native channel timing remains unqualified. Fake clock ticks keep
the original start and cutoffs; fixture wall-clock watchdogs only detect a stuck
test. Fixture disposal releases each blocking port and awaits retained tasks even
when an assertion fails.

The closed descriptor compares independently expected operation, generation,
invocation, source/runtime/runner identities, workflow, App/install, channel roles
and result destination. Its closed wire schema additionally joins source,
workflow, App/install, process and the original finite clock cutoffs. Runtime
closure, channel roles and destination remain independent descriptor expectations;
the domain wire schema does not carry those fields. Admission retains consumed
invocations for the original operation window. Recreating admission does not grant
permission to replay an operation.

The source scenarios cover native/source/runner/binding refusal, repeated admission,
a six-phase inert lifecycle, partial ACKs, blocked retention, early ingress, blocked
validation, cancellation, read/retirement failure and late callback refusal.
Successful start writes, ACK writes and retention callbacks also exercise
cancellation and normal/work expiry before the next supervisor poll. These source
cases preserve the original stop source instead of reporting a transport failure.
`JoinCleanup` retains the supplied owner before supervision is sealed and whenever
termination or local tasks remain unresolved. Existing unknown cleanup facts remain
unknown. Domain callbacks returning `Ok` are inert stand-ins, so UTEL payload-domain
validation, paired real consumer integration and installed acceptance remain I2/I3
obligations. Production availability stays false and all native/R1 holds remain.


## Managed ownership source inventory v1

`fsgg.finite-custody-managed-ownership/1` is an opt-in inert source inventory.
`requireClosedPhysicalCopyProfile` refuses: no selected endpoint has proved its
capture/release behavior. Legacy `Prepare` remains callable and carries no fit
assertion. Production availability is false. This inventory does not establish
65,536-byte joined fit, an interface freeze, or managed heap reclamation.

| Representation | Source owner / aliases | Retirement and physical-copy limitation |
| --- | --- | --- |
| Up to six decoded exact phase frames, each at most 2,048 bytes | Snapshot owns one array; payload view, decoded/retaining sets, queue, accepted list, pending ACK and gates alias it | Retained with session/gates; terminal, expiry and task settlement do not prove GC or release of caller references |
| Canonical payload | Offset/length view into that exact immutable frame; no separate retained payload array | Lifetime is the frame lifetime; canonicalization still allocates bounded synchronous temporaries |
| Public `Frame` and `Payload` | Each accessor creates an independent caller-owned copy | Arbitrary repeated calls and caller retention are unbounded; excluded from any closed fit assertion |
| Callback correlation | Pending metadata preserves kind, invocation, binding, sequence, ordinal, digest and encoded length; no base64 data | Preserved after close/refusal; no retry or reservation reuse; reply acceptance still requires complete write |
| Base64 and callback encoding | Synchronous encoder owns transient base64 string, stream/body/frame buffers; internal base64 reads the owned frame directly | Scope ending does not prove physical reclamation; no temporary is counted as an alias of the frame |
| Encoded async callback request, at most 4,096 bytes per request | Exchange task and supplied writer may retain the same array or make copies | Outstanding writes stay in retained tasks; response/cancellation/timeout do not prove release by an arbitrary stream or completed task |
| Phase parser input 2,048 bytes; callback parser input 512 bytes | Protocol session owns each fixed array | Retained session lifetime; parser documents, slices and canonical buffers are distinct temporary allocations |
| Child read buffer 2,048 bytes; callback reader buffer 1 byte | Retained read tasks and supplied streams | Each child read currently allocates anew; stream captures and completed-task results require endpoint proof |
| Binding and start/control frames | Adapter, composition, result session and callback session own independent binding arrays; start frame and generated ACK arrays are separate | No cross-owner deduplication assumed; task/channel capture and terminal/uncertain writes remain obligations |
| Restart | Admission retains used invocation identity; unresolved session/tasks retain their original bytes | A new object or restart is not release evidence or replay authority; no replacement allocation may be claimed to reuse an unresolved physical slot |

The exact canonical BE-u32 start, phase, ACK and callback schema/encoding remain
unchanged. Payload offsets rely on the already validated canonical closed root
order `b,k,n,p,v` and its fixed `v` suffix. Existing blocked validation/retention,
partial-write, deadline/cancellation, pre-ACK terminal and malformed/seventh input
controls remain required. New controls check frame-backed payload immutability,
base64-free correlation across close, blocked validation and one-use refusal.
The paired callback corpus and actual Python callback journey must qualify the
changed source on the hosted route. Pure static checks cannot replace compilation.

The source ceiling of six times 2,048 bytes describes retained snapshot arrays
only. It excludes binding/start/ACK, encoding/parser temporaries, async requests,
caller copies, owner/backend/peer storage and object overhead. The complete
physical population remains unproved and must refuse admission. No sink,
filesystem, signer, credential, native constructor or original-operation effect
is introduced by this source profile.

## Same-execution terminal cut source witness

The terminal-cut source test holds the Python controlled backend inside its
sixth readback stage while the managed callback transport's actual
`WriteAsync` request memories and independent endpoint copies remain live.
A fresh test nonce, canonical binding digest and digest of the thirteen
requests read by Python bind both observations to one execution. The pinned
Python slot lifetime observer samples sixteen object identities and lengths
before and after the managed readback; its release requires the exact nonce,
binding, request, ordinal and stage. The test then checks all six typed slots,
three ACKs, terminal confirmation and settled retained tasks. Failure retires
the child and joins retained tasks. Separate Python controls retain the
original readback, short-write and flush uncertainty cases.

This witnesses one held source cut and its scoped aliases. The managed
endpoint's thirteen supplied requests and thirteen deliberate copies account
for 25,738 logical bytes. The selected Python observer lists 11,660 logical
bytes across sixteen objects. These scoped counts exclude other live objects,
allocator capacity, task/channel storage and uncertain paths; they are not
a complete simultaneous physical population or a 65,536-byte fit. The test
does not establish native authentication, host custody, replay permission,
original-operation effects or installed adoption.

### Managed backing arrays at that same cut

The same test inspects fields of the live adapter, composition, protocol,
snapshot and retained-task roots before releasing Python. It never calls the
copying public `Frame`/`Payload` accessors. The test checks these eleven distinct
backing arrays, separately from the existing twenty-six request/capture arrays:

| Reachable representation | Objects | Byte-array capacity |
| --- | ---: | ---: |
| Snapshots, ordinals 1–6 | 6 | 1,141 + 972 + 830 + 917 + 831 + 991 = 5,682 |
| Result parser input | 1 | 2,048 |
| Callback parser input | 1 | 512 |
| Result and callback binding copies | 2 | 994 each = 1,988 |
| Composition start frame | 1 | 1,123 |
| Selected additional backing arrays | 11 | 11,353 |

Payload memories alias the snapshot backing arrays; their 4,835 slice bytes
are not another allocation or the backing capacity. The start and captured
request memories expose their full arrays with zero offset. All eleven selected
arrays are disjoint from the twenty-six existing request/capture arrays. A bounded
field walk reaches twelve byte arrays: the eleven above and the current 295-byte
callback request, whose reference equals the thirteenth captured original. That
request is counted once. Array references and inspected objects remain local to
the inspection; output retains scalar counts and lengths only.

The walker follows instance fields within the execution assembly and explicitly
selected task, memory, collection, option and tuple containers. It excludes fixture
streams, processes and other runtime objects. Depth, object count and container
length are bounded. Named fields, exact snapshot ordinals, object identities,
lengths and the discovered byte-array population are assertions; unavailable or
changed inspected structure fails the test instead of substituting byte allowances.

This proves reachability for the selected arrays only. The three generated
199-byte ACK arrays, child-read 2,048-byte buffers and callback-reader one-byte
buffer have no established live-root join in this bounded view. Their absence
from this view does not prove death, reclamation or zero bytes. Runtime/stream
roots, encoding temporaries, other caller/backend/peer objects, object headers,
allocator padding, alternative cuts and uncertainty populations remain unresolved.
The new count is neither a complete population nor a revised physical-fit budget.
`requireClosedPhysicalCopyProfile` continues to refuse. Installed custody, native
ACK authentication, original `.103` replay/effects and `.104` remain held.

### Held third ACK and pending child read

The same source journey also pauses inside the third ACK `WriteAsync` call,
for phase ordinal four, after the fixture queues phases five and six and
before that write returns. The fifth child `ReadAsync` call has taken phase
five from the queue but waits for ordinal four to be confirmed; phase six
remains queued. The confirmed ordinal is still two. Both actual stream tasks
must remain incomplete throughout inspection.

At this earlier cut, the actual ACK memory exposes a 199-byte backing array
and the pending child-read destination exposes a distinct 2,048-byte array.
Both memories have zero offset and span their complete backing capacity.
The ACK's binding, ordinal and phase-four frame digest identify the write.
Neither array aliases the result parser input or any of the nine current
callback request arrays and nine endpoint copies.

The test follows named fields only: session → ports → channel endpoint →
pending call/task → production adapter continuation/state machine. The
production read state's `buffer` is the exact supplied read backing; its
awaiter holds that exact read task. The production write state's `bytes`
aliases the exact supplied ACK backing; its active awaiter holds that exact
write task. Both states belong to the execution assembly and retain the
same channel objects as the session's corresponding ports. Generated state
names and field paths are asserted, with at most eight ancestor lookups;
changed or unavailable structure fails instead of falling back to scanning.

The fixture keeps one observation of each pending call and clears it when
that call settles. Inspection references remain local and output contains
only scalar call ordinals, capacities and assertion results. Releasing the
hold continues the existing terminal readback correlation, eleven-array
census, six exact slots, three ACKs, terminal confirmation and settled-task
checks. On test failure, the fixture releases the ACK hold and joins cleanup;
closed-profile admission remains refused.

This earlier cut does not establish that either object remains live or has
been reclaimed at the terminal cut. Earlier ACK arrays, other
encoding/runtime/stream roots, uncertainty paths and the complete
physical population remain unresolved. The observations from the two cuts
are not a combined peak or a fit budget. Closed-profile admission, installed
custody, native ACK authentication, original `.103` replay/effects and `.104`
remain held.

### Pending callback read at terminal readback

At the existing ordinal-six Python readback hold, a transparent test stream
observes the callback reader's actual pending stdout read. It forwards the
identical `Memory<byte>` and cancellation token once to
`owner.StandardOutput.BaseStream`, with no synthetic bytes, copying or
read-ahead. Its task awaits the real stream task, and clears its pending-call
observation in `finally` when that read settles.

The bounded named-field witness requires both tasks and their registered
continuations to remain incomplete. The production callback state belongs
to the execution assembly, retains the session's exact callback bridge, and
awaits the observer task. That bridge's channel endpoint is the observer;
the observer's target is the actual Python stdout stream. The observer state
awaits the exact underlying stream task. Generated state names and all field
joins are asserted, with at most eight ancestor lookups and no scanning
fallback. Changed or unavailable structure refuses the test.

The production state's buffer is the exact supplied memory backing: one
byte of complete array capacity, memory length one, offset zero. Reference
identity checks distinguish it from the eleven selected terminal backings
and all twenty-six request/capture arrays. Output retains only scalar
capacity, memory length, offset and join results. Settlement asserts that
the observation is cleared. Failure releases the holds, retires Python and
joins the existing cleanup; six exact slots, three ACKs, terminal confirmation,
Python resampling and settled-task checks remain required.

This adds one terminal-cut root relation. It does not combine the earlier
ACK/child-read cut with the terminal cut or establish physical reclamation,
a complete simultaneous population or a fit budget. Other runtime/stream,
encoding, caller/backend/peer and uncertainty populations remain unresolved.
Closed-profile admission stays refused; installed custody, native ACK,
original `.103` replay/effects and `.104` remain held.

### Process stdio adapter roots at terminal readback

At the same held ordinal-six readback, the test follows the actual Python
`Process` through its `_standardInput`, `_standardOutput` and `_standardError`
text adapters. Their `_stream` fields identify the exact request-wrapper
and response-observer targets, and the separate diagnostic stderr stream.
The actual diagnostic task is the stderr reader's `_asyncReadTask`; it is
pending during inspection. The callback read remains pending with its
existing exact task/state/buffer joins.

Only each adapter's `_byteBuffer` and `_charBuffer` fields are selected.
The complete six-field observation contains five distinct arrays and one
absent optional writer buffer in the qualified source journey:

| Adapter | Ownership | Byte-array capacity | Character elements |
| --- | --- | --- | --- |
| stdin writer | Protocol transport | Buffer absent | 4,096 |
| stdout reader | Protocol transport | 4,096 | 4,097 |
| stderr reader | Diagnostic transport | 4,096 | 4,097 |

Character element counts are separate from byte-array capacities. The
absent optional writer buffer is not populated for measurement and supplies
no claim about other encoding allocations. Diagnostic storage is not a
protocol subtotal. Reference identity checks distinguish every selected
array between adapters and fields, and from the prior eleven selected
backings, one callback-read backing and twenty-six request/capture arrays.
A same-length array substituted for the observed reference fails identity
with the actual retained adapter field. No initial-allocation identity or
replacement history is asserted. The fixed field set, concrete adapter/array types, endpoint joins
and capacities are assertions; unavailable or changed structure refuses.

Qualification records the actual loaded framework/runtime version, core
and Process assembly informational versions and module identities. The SDK
pin or a public runtime tag does not establish those loaded bytes. The
initial local observation used .NET 10.0.12; hosted qualification records
its own runtime metadata. This witness supports the observed .NET 10 field
shape and does not establish other runtime compatibility.

The observation performs no stream read, flush, encoding or GC operation.
Existing diagnostic consumption and callback transport continue through
their original owners. Inspection references remain local; output contains
scalar roles, counts and runtime metadata. Terminal correlation, Python
resampling, six exact slots, three ACKs, observation clearing and joined
cleanup remain required. These additional roots do not establish complete
physical population, reclamation or fit. The earlier ACK/child-read cut
remains separate. Closed-profile admission, installed custody, native ACK,
original `.103` replay/effects and `.104` remain held.

### Additive closed managed source facade

`FiniteCustodyClosedSource` selects `fsgg.utel-closed-source/1` without editing
`FiniteCustodySessionAdapter.fs` or either frozen producer manifest. The separate
`eng/utel-closed-callback-source.json` and the selected slot pin now coherently
select protected producer `dff1248d` (public producer successor PR #4617). The
nine-file closed manifest is `96d9bf0e…`; slot profile
`inert-slot-callback-pair/2` selects bundle `6a932c24…` and vectors `bf3b2649…`.
The qualifier requires the slot and closed pins to name the same revision and
vectors before either selected checkout is acquired. It rejects historical `/1`
selection against the successor rather than falling forward. The separate legacy
`eng/utel-callback-source.json` selection and all six frozen managed source roles,
including the session adapter, stay byte-identical. No wire version changes.

The actual paired Python control asserts `/2` evidence after the maintained
thirteen-exchange/six-slot journey. The closed facade verifies the same new vector
digest and replays that unsigned transcript with its fixed owned endpoints. The
terminal-cut owner and managed assertion select the same protected revision and
profile source. Its observer pin advances to the already-protected `fd1c6ab7…`
observer: the selected-value and emission observations landed earlier in PR #4582,
not in this adoption. The held terminal readback still requires the same sixteen
objects and unchanged identities across release. Neither those identity checks nor
the expanded observer infer a complete physical population or concurrent closed
provider qualification. Hosted managed execution remains required for these
controls; pure pin checks alone do not establish their runtime result.

The public `Prepare(byte array)` accepts only the exact canonical
profile digest and refuses with `closed-source-provider-unavailable`. Unknown,
noncanonical, duplicate-key, changed or oversized profile bytes refuse by exact
digest and the 65,536-byte input ceiling. A sealed byte-array input avoids caller-supplied `MemoryManager` callbacks. No
caller clock, stream, binding, writer,
runtime, endpoint, evidence path or activation flag is accepted. Actual provider,
authenticated role/original/generation/clock/endpoint enrollment, durable receipt
and physical accounting remain null. Public preparation performs no file lookup,
reservation, process launch, signer, credential or network operation.

The internal friend-test constructor consumes its original admission before any
fixture lookup or construction. It builds every descriptor, binding, clock,
stream, termination observation and retirement callback itself, then calls the
maintained `FiniteCustodySessionAdapter.Prepare`. Its returned legacy handle and
retained tasks stay private. The facade returns a fixed completion enum from
`Run`; it exposes no legacy capture, task array, snapshot, raw byte or endpoint
accessor. API-surface reflection occurs only in the excluded test population,
never as an implementation technique.

Owned fixed fixture endpoints compare all thirteen requests to the pinned frozen
transcript and deliver its exact responses. Six phases and three ACKs traverse
the actual maintained adapter/decoder/bridge/composition path. Child ingress waits
for the adapter's confirmed ACK ordinal. Frame, aggregate, exchange, read-call,
write-call and ACK-wait reservations are cumulative, including failed calls;
construction and Run cannot be repeated with the same original admission. The
source maxima are six phase frames of at most 2,048 bytes/16,384 aggregate,
thirteen callback requests of at most 4,096 bytes/40,960 aggregate and replies of
at most 512 bytes. Endpoint reads reserve at most `frames * maximumFrameBytes + 1`
call tasks; ACK gating reserves at most 4,096 delay calls across the original.
Fixed owned queues are bounded to their original frame counts. The owned synthetic
clock advances on every sample, so even stalled fixture supervision reaches the
original total cutoff; it authenticates no host time. These counts do
not supply object/header/allocator/kernel/runtime physical upper bounds.

Request-write failure, truncated response and child-control failure close the
same maintained path; retirement finishes owned inputs and original construction
stays consumed. Successful source Run waits internally for retained managed tasks
but infers no GC, physical release, durable settlement or renewed capacity.
Required first/late-cause and cleanup semantics remain in the maintained adapter;
this facade supplies no operational receipt or replacement owner.

This is **managed fixed-endpoint source/refusal qualification**, with no live
Python callback process in the new route. Python's accepted closed core owns a
preloaded transcript and its public constructor refuses; it currently has no live
closed provider endpoint or CLI. The existing generic paired Python journey
continues separately and cannot establish concurrent closed-parent/provider
acceptance. A future provider join needs a source-owned constructor/provider ABI,
authenticated transport transfer, durable original reservations and commit/readback
receipts, retained signed diagnostic evidence and enrolled recipient custody.
Those missing supplier capabilities are not filled by these fixture responses.

The existing compiler gate prepares this opt-in pin, executes the transport module
and checks its expanded exact census. Relevant pure pin/census controls run first.
Under the current resource hold, local CLR execution is prohibited: managed
compilation and focused transport tests must pass through maintained hosted
qualification for the exact candidate. Full coherent and required CI remain
mandatory. Production availability/admission remain false, physical bound null,
fit unproved and cap 65,536. Original `.103` remains attempted-unknown/unreplayed;
`.104`, credentials, deployment, native execution and unknown reservations remain held.
