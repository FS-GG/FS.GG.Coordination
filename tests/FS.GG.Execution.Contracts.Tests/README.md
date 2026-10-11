# Contract conformance source

These deterministic xUnit controls call actual Budget, Validation, BoundedOutput
and Lifecycle functions. They cover domain/regression/overflow and fixed expiry,
literal command and allocation refusal, binary prefix/tail capture, concurrent
shared charging, terminal reserve, failed append/retirement, launch-at-most-once,
exit/EOF separation, capability/owner release gates, first cause and bounded
omissions, unknown cleanup and requested/obtained resource joins. Execution-bound
adapter controls live in the existing Execution test project to avoid an upward
reference from this portable test project.

Source preparation is not a passing test receipt. Restore/build/test requires its
own exact toolchain and finite admission. No process is launched by these tests.
RetainedSessionTests exercise the interfaces with a caller-held fake (no native authority),
original expiry and refused release while local obligations remain. Expanded negative
controls and actual deterministic acceptance remain .2-B; real disposable fixtures are .2-C.
Neither pure controls nor historical .1 receipts qualify a new native backend.
