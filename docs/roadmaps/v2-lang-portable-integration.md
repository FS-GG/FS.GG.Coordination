# V2-LANG-01.2 — Portable workspace integration

**Part:** V2-LANG-01, window .2. This plan owns the portable contract from Coordination source through
publication and receiver qualification. The accepted programme plan remains
[`FS-GG/.github`'s language-independent workspace amendment](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-09-29-language-independent-workspaces-and-agent-integration.md).

**Status:** P1 source and local CLI integration are complete on the isolated branch. P2 package installation
passed locally; hosted qualification and protected source merge remain pending. Publication, receiver adoption
and P3–P5 remain pending.

## Authority and compatibility

The v1 profile, command and result bytes remain stable. Product repositories do not import Coordination CLR,
Akka or agent-framework types. The CLI exposes a read-only contract utility: it exports compiled schemas and
examples, validates exact canonical bytes, computes SHA-256 digests and prepares a reviewed named operation.
Preparation proves only that profile, command, scope, revision, generation and time fences agree. It performs no
process launch, network write, approval, settlement or other effect and grants no execution authority.

Serialization fixtures may contain conspicuous placeholder revisions. Those bytes remain valid examples, but an
operation cannot be prepared until its source revision is a full lowercase hexadecimal commit identity and its
qualified image has a non-placeholder SHA-256 digest. Unsupported schema versions and null, duplicate, unknown
or noncanonical fields fail closed.

## Delivery plan

- [x] **P1 — Distributable contract source.** Add checkout-independent schema/example export, canonical
  validate/digest and read-only operation preparation to the existing CLI source. Prove F#, Python and JavaScript
  agreement for maximum unsigned counters, six-digit UTC timestamps, omission instead of null, closed objects,
  known/missing/unknown evidence and canonical digests. Refuse unsupported schemas and placeholder execution pins.
- [ ] **P2 — Coordination integration and package qualification.** Integrate the shared CLI registrations, run
  repository checks, pack the existing tool and prove the installed command works with the source checkout absent.
  Source merge and installed behavior are recorded separately; no publication is claimed by P1.
- [ ] **P3 — Publish exact producer artifacts.** Publish the accepted Coordination tool and contract artifacts,
  then have SDD/Templates pin those exact versions and digests. Preserve the existing fixed-operation and execution
  authorization boundaries.
- [ ] **P4 — First workspace adoption.** Change fresh creation only after the published producer is available.
  Adopt one selected non-.NET route explicitly, prove a clean creation and local-only operation, and keep retained
  workspace upgrade handling separate. This is the first milestone allowed to change generated workspace behavior.
- [ ] **P5 — Qualification matrix and closure.** Qualify retained adoption, mixed components, component and composed
  journeys, cancellation, duplicate delivery, recovery, unsupported toolchain refusal and the declared language
  population against exact installed artifacts. Record supported and unresolved routes before closing .2.

## P1 evidence

The focused Execution tests cover the F# codec, strict shapes, fences and placeholder refusal. Independent Python
and JavaScript standard-library codecs reconstruct the same maximum-counter command, reject the adverse shapes and
produce the same digest. A CLI unit test compares every compiled schema/example byte with its tracked v1 file.
The CLI emits canonical example JSON without the tracked text file's terminal newline, so the exported
example passes its own canonical digest check.

Run after the shared CLI project registrations are integrated:

```console
dotnet test tests/FS.GG.Coordination.Orchestration.Execution.Tests/FS.GG.Coordination.Orchestration.Execution.Tests.fsproj --no-restore
dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj --no-restore
```

## P2 local evidence

The shared CLI project and command registrations compile with zero warnings. The dependency policy accepts
the single new CLI-to-Execution reference, and the registered CLI artifact test passes. A local preview tool
package was packed and installed under `/tmp`, then invoked from outside the source checkout: schema export
worked and an exported Python profile produced canonical SHA-256 digest
`469cf5c0bdc54e4db241b98dd7ba11cae396e2080bb9beec0365a2cbc2a8537c`.
The preview package is local qualification evidence, not a published Coordination release.

## Workspace impact

P1–P3 change Coordination source and distribution only. They do not change fresh workspace creation, retained
workspaces, a product default or an enabled runtime. P4 is the first possible workspace change and requires exact
producer publication plus receiver-owned clean-creation proof. P5 expands verified adoption without rewriting the
frozen V2 cohort or historical outcomes.
