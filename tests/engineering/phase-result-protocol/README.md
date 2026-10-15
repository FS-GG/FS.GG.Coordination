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
