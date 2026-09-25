# GS2-09.7 Q6 rollback seal atom hold

Status: **source-only; no provider rollback or protected receipt**. This draft
stacks on #616's exact five-domain sequence check. The accepted
[representative rehearsal](gs2-09-7-representative-rehearsal.md) requires an
immutable rollback plan and exact chained receipts before a restore can be
treated as settled.

The reused GS2-09.6 pure plan encoded each step by joining order, step ID,
domain, target identity and digests with `|`, then joined plan fields with
newlines. It accepted these separators inside the free-text identity fields.
Two different steps therefore had the same normalized bytes and seal:
`StepId=unit`, `TargetIdentity=left|authority-snapshot|right` and
`StepId=unit|authority-snapshot|left`, `TargetIdentity=right` at the
`authority-snapshot` position. A red-before test created one plan and changed
the step and target under its original seal; `verify` returned success. A
separate red-before control admitted an embedded newline in a step ID. The
final suite also refuses a newline in the outer plan identity.

The validator now refuses pipe and control characters, leading or trailing
space, and empty values in the plan identity, step ID and target identity.
This preserves the normalized bytes and seals of previously well-formed
plans while refusing ambiguous records. It does not authenticate who
captured the prestate or who supplied the restore result; SHA-256 and a
source seal are not a protected receipt. The installed Q6 interpreter must
still bind native target identity and readback to each one-use restore,
persist chained receipts, recover after each interruption, and verify the
final state and epoch on the registered isolated copy. #3690 remains an
unadmitted source observation; no live pin or sandbox effect follows here.
