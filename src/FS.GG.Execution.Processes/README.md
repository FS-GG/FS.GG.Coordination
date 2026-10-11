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
