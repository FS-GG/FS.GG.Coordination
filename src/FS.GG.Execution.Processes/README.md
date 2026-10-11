# BCL process sessions

V2-PROC-01.3 source preparation over unchanged `Execution.Contracts`. This isolated
net10.0 producer is non-packable and depends only on Contracts and BCL/FSharp.Core.
Its signature exports `ProcessSessions.createFactory`; session implementation and
native process handles stay private. Source preparation is not runtime qualification
or publication. Initial qualification targets .NET 10 on Linux.

The caller supplies and retains a prepared session, owner reference, live clock and
original budget before calling Run. Work and cleanup expire at the original fixed
ticks. Literal absolute executables and ArgumentList, all three environment modes,
bounded copied stdin and byte capture use the existing validated values.

Only bounded return with retained caller custody is supported. Required stable birth
identity, descendant observation/containment, aggregate limits, namespace isolation,
whole-workload emptiness, child reaping and owner-survival cleanup refuse before
launch. Every resource request also refuses. Best-effort BCL stop is not containment
or reaping proof. An exit code does not establish stream EOF or unknown descendants.

Prepared tests and maintained fixture wiring require fresh source/toolchain/resource
qualification. The old test-only fixture factory and its historical evidence remain
separate. Producer source alone does not close .3; genuine candidate consumers and
the coherent publication/adoption boundaries remain open.

Native Start and stop run on retained tasks outside the observation lock. A stop
that precedes the launch commitment prevents launch; a stop racing with a committed
BCL Start retains the in-flight attempt and requests retirement after actual start.
This sampled release/kill ordering is not atomic kernel containment or hard real-time
scheduling. The birth token identifies the retained BCL Process lease, not
an authenticated kernel birth identity; that capability remains unsupported.

Exit, readers, bounded input and stop tasks settle independently. Local release
requires every actual retained task complete, known leader termination, admitted
reader proof and the original unexpired cleanup window. After total expiry,
`TryRelease` returns `ReleaseRefused(original-time-expired)` even if private tasks
later settle. Late facts do not rewrite original in-budget proof. The caller's
separately admitted administrative retirement remains an owner obligation; this
interface supplies no fresh administrative budget or automatic disposal success.

`RunAsync` retains its bounded execution wait. `ObserveAsync` immediately returns
one truthful current snapshot under the retained session lock; it does not wait for
leader exit, EOF or local settlement. Cancellation and original work expiry still
request retirement within the original total. Observation before Run does not
consume launch, and observing an in-flight launch does not close its release gate.
Original clock history is validated without committing a return event. Observation
never grants release or later budget authority.

A caller that needs early-exit drain timing retains the actual session and Run task,
observes independently, and starts its drain grace at the observed exit stamp,
clipped to the same original total. Its response can precede actual local settlement;
the explicitly admitted outer caller continues to own all tasks/resources until
lawful release or reports an unresolved retirement obligation. This source alone
does not establish the SDD test-lifetime integration or consumer adoption.
