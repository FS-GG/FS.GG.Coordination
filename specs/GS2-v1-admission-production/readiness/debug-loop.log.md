# V1 admission signing-payload debug loop

## Iteration 1 — 2026-09-24 11:01 UTC

**Verify command:** Main host sign-intent on the public runner payload, then
python3 -m unittest eng/test-github-v1-admission-main-custody.py and the focused
F# unit test.
**Exit code:** 3 for the host integration; 1 for the new Python regression.

**Primary failure:** runtime-exception
- Signal: eng/github-v1-admission-main-custody.py:215: admission-signing-payload-binding
- Hypothesis: Python reserialization omits the F# canonical writer's escaped
  U+002B offsets and final LF.

**Fix applied:**
- eng/github-v1-admission-main-custody.py — validate the bounded producer
  shape and compare exact F# canonical bytes before any native/key read.
- tests/FS.GG.Coordination.UnitTests/fixtures/v1-admission-signing-payload.json
  — shared public exact-byte fixture.
- Python and F# tests — require this fixture and wrong-byte refusals.

**Narrow re-run result:** Python 5/5 and focused F# 1/1 pass; the live public
payload parses read-only with the patched helper.
**Full verify result:** Release build passed with zero warnings/errors; full
unit suite passed 488/488. A broad solution test command failed because
installed PostgreSQL fixture paths are absent in this container and two
generated Python bytecode files made the supply-chain reproducibility test
see a dirty tree. After removing only those generated files and disabling
bytecode output, the supply-chain test and full architecture suite passed
690/690. Hosted qualification is pending.

## Result

The targeted integration regression is green after one source iteration.
The automatic solution-wide verify command is not green in this container
because its PostgreSQL installed-fixture prerequisites are absent; no assertion
or unrelated test was weakened. Protected live qualification remains pending
hosted checks, reviewed merge, and a new source-bound native run.
