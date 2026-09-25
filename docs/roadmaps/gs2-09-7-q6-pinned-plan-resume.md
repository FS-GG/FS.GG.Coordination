# GS2-09.7 Q6 pinned rollback-plan resume hold

Status: **source-only; no native rollback or protected receipt**. The
accepted [representative rehearsal](gs2-09-7-representative-rehearsal.md)
requires recovery from the exact accepted rollback plan, not an alternate
plan created after the interruption.

The historical GS2-09.6 `resume(plan, receipts)` verifies a presented plan
against `plan.Seal`. A different, internally valid plan with a fresh seal can
therefore select a different next step. An independent red-before test
demonstrated that substitution. Directly changing the historical function's
signature would also change the accepted GS2-09.6 gate script and its pinned
command bytes. This draft leaves that controlled gate unchanged and adds
`resumePinned(expectedSeal, plan, receipts)` for future GS2-09.7 Q6 use.
It checks the caller's independent expected seal before interpreting any
receipt prefix. A foreign but validly resealed plan refuses; the matching
plan can resume. The existing prefix, gap and chain checks remain in force.

The installed protected interpreter must obtain `expectedSeal` from a
separately admitted, durable plan/receipt authority, bind it to the exact
sandbox cohort, manifest and run, and reread that authority after a process
restart. A candidate-supplied seal or a value copied from the presented plan
does not close this hold. The legacy self-pinned helper remains only for the
frozen GS2-09.6 controlled qualification and must not be used as Q6 replay
authority. The actual five-domain native restore, receiver readback, final
OperatingV1 epoch check and protected Q5/Q6 receipt remain separate.
#3690 remains an unadmitted source observation.
