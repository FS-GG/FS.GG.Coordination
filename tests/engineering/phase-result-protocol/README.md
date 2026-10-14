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
The owning execution session must retire on `Refused` within its original budget.
A validated terminal arriving before a pending ACK is retained as failure evidence;
it invalidates that ACK and leaves any writer task accounted for. A late callback
cannot revive the ACK or session qualification. The consumer writer also owns
monotone replacement so an older callback cannot erase a newer retained snapshot.

The finite production profile remains unavailable. These checks establish source
codec/reducer behavior only; native supervision, durable result replacement,
child-directed credential custody and installed consumer acceptance remain separate.
