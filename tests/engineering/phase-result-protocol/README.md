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
A validated terminal arriving before a pending ACK is retained as failure evidence;
it invalidates that ACK and leaves any writer task accounted for. A late callback
cannot revive the ACK or session qualification. The consumer writer also owns
monotone replacement so an older callback cannot erase a newer retained snapshot.

The finite production profile remains unavailable. These checks establish source
codec/reducer behavior only; native supervision, durable result replacement,
child-directed credential custody and installed consumer acceptance remain separate.


`controls.fsx` also loads the exact internal `FiniteCustodyComposition.fs` source
against the compiled protocol and window. This keeps the composition internal
without granting assembly-wide test access. Its separate ingress and parser tasks
keep early child bytes observable while a validator or durable writer is blocked.
The supervisor samples only the original clock, cached protocol/task state and
last-thread observation; channel I/O, domain callbacks and retirement requests run
on retained tasks. Full write counts and exact durable readback precede ACK
confirmation. A fake child advances only after the internal confirmed-ACK ordinal
is observed. This controlled timing is not native channel qualification.

The closed descriptor binds the independently expected original operation,
generation, invocation, source/runtime/runner identities, workflow, App/install,
channel roles and result destination. Its wire binding also joins source, workflow,
App/install, process and the original finite clock cutoffs. The runtime closure,
channel roles and destination are independently supplied descriptor expectations;
the current domain wire schema does not carry them. Reusing the same admission
owner refuses a consumed invocation. The caller must retain that owner through the
original operation window; constructing a new owner does not authorize replay.

Composed controls cover source/native/runner refusal before channel work, repeated
admission, full lifecycle, outstanding retention at the original work/total cutoffs,
late successful readback and partial ACK writes. They exercise fake ports only.
`JoinCleanup` retains the supplied custody owner whenever last-thread termination
or any local task remains unresolved and preserves pre-existing unknown task facts.
These controls are source drafts until executed under an admitted CLR profile.
