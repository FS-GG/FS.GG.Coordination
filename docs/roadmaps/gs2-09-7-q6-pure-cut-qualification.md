# GS2-09.7 Q6 pure interruption cuts

Status: **source-only characterization; no isolated provider run or Q6
receipt**. This branch adds three explicit cut points to the existing
`MigrationStepExecution.advance` boundary. The accepted
[representative rehearsal](gs2-09-7-representative-rehearsal.md) requires six
cuts for every actual effect, fresh-process recovery and authoritative native
readback. This fixture exercises only one sealed `SetIssueType` step with an
in-memory runtime.

| Accepted interruption point | Pure boundary cut | Existing or new control |
| --- | --- | --- |
| Before durable intent | `StopBeforeIntent` | New: preflight reads occur; no journal write or dispatch. A later invocation may start the step. |
| After intent | `StopAfterIntent` | Existing: intent is durable in the fake journal; resume sends once. |
| Before dispatch | `StopAfterInFlight` | Existing: an in-flight marker with a possible send stays pending on apparent absence. |
| After dispatch with lost response | `StopAfterDispatch` | Existing: effect readback settles without another send. |
| After effect readback | `StopAfterReadback` | New: applied effect has been observed, but final target readback and settlement have not occurred. If the target changes before recovery, settlement refuses and no second send occurs. |
| After target readback, before receipt | `StopBeforeReceipt` | New: exact target readback has occurred, journal remains in-flight, and recovery settles without another send. |

`StopAfterEffect` remains for existing callers. It interrupts on an applied
effect observation before final target readback and retains its prior result
name. The two new post-dispatch cuts have distinct read counts and journal
stages, so an apparent post-readback green cannot be inferred from a receipt
that was never persisted.

The negative controls use a fake runtime. They do not prove that the selected
sandbox's native readback is authoritative, that a process restart uses the
same protected journal, that the other effect cases are supported by a GitHub
provider, or that the nine discovery authorities and five rollback domains
are complete. The current effect union omits several accepted operation
families; this source branch does not extend it. The installed GS2-09.7
interpreter and the protected Q5/Q6 gate remain absent. A real Q6 result
must run all six cuts for each effect on the registered isolated copy, restart
from durable state, reconcile lost responses, verify zero duplicate effects
and reverse all five restoration domains with chained receipts. Q5 requires
the complete nine-authority manifest, two rounds, archive verification and
native receiver readback. No controlled test here can substitute for those
provider observations or the protected acceptance receipt.
