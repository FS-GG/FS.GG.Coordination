# V2-LANG-01.2 — Portable workspace integration

**Part:** V2-LANG-01, window .2. This plan owns the portable contract from Coordination source through
publication and receiver qualification. The accepted programme plan remains
[`FS-GG/.github`'s language-independent workspace amendment](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-09-29-language-independent-workspaces-and-agent-integration.md).

**Status:** P1 source, local CLI integration and installed-package qualification are complete on the isolated
branch. P2 remains open after review found that the first local executor did not enforce its source or OCI
image binding. The corrected source and actual pinned-image qualification are in progress. Hosted qualification,
protected source merge, publication, receiver adoption and P3–P5 remain pending.

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

- [x] **P1 — Distributable and installed contract utility.** Add checkout-independent schema/example export,
  canonical validate/digest and read-only operation preparation to the existing CLI source. Prove F#, Python and
  JavaScript agreement for maximum unsigned counters, six-digit UTC timestamps, omission instead of null, closed
  objects, known/missing/unknown evidence and canonical digests. Refuse unsupported schemas and placeholder
  execution pins. Integrate the shared CLI registrations, run repository checks, pack the existing tool and prove
  the installed command works with the source checkout absent. Source merge and installed behavior are recorded
  separately; no publication is claimed by P1.
- [ ] **P2 — Bounded executable binding.** Bind one real fixed-operation executor to the prepared source revision,
  immutable toolchain image, reviewed operation identity, working directory, finite runtime/output limits and
  verification identity. Exercise actual Python component build/test and a minimal TypeScript frontend with Python
  backend build/test plus frontend-to-backend journey in isolated fixtures. Distinguish cancellation request from
  observed termination, make duplicate delivery idempotent, preserve interrupted execution as unknown until exact
  reconciliation, and refuse unsupported toolchains, foreign scope, stale generations and arbitrary command
  injection before process launch.
- [ ] **P3 — Publish exact producer artifacts.** Publish the accepted Coordination tool and contract artifacts,
  then have SDD/Templates pin those exact versions and digests. Preserve the existing fixed-operation and execution
  authorization boundaries.
- [ ] **P4 — First workspace adoption.** Change fresh creation only after the published producer is available.
  Adopt one selected non-.NET route explicitly, prove a clean creation and local-only operation, and keep retained
  workspace upgrade handling separate. This is the first milestone allowed to change generated workspace behavior.
- [ ] **P5 — Qualification matrix and closure.** Qualify retained adoption, mixed components, component and composed
  journeys, cancellation, duplicate delivery, recovery, unsupported toolchain refusal and the declared language
  population against exact installed artifacts. Record supported and unresolved routes before closing .2.

## P4 trusted resolver source preparation

The first Python adoption now has a source-qualified, fixed `portable-workspace` command route. Its production
resolver reads only `/etc/fsgg/portable-workspaces/python-hello-v1.json`, requires a non-root selected account and
administrator-owned non-link path components with no delegated write ACL, validates a closed bounded record, and
reconstructs one compiled Python test operation. Before constructing the runner it verifies the installed CLI
payload/version, fixed Git/tar/Podman executable identities, clean receiver commit/tree and the complete projected
payload inventory. Receiver Git inspection clears ambient configuration, disables executable helpers, avoids
status/filter conversion by comparing the index with a direct non-link worktree inventory, drains bounded output
concurrently and kills the owned process tree at its fixed deadline. Direct inspection accepts only regular files
and has fixed entry, depth, per-file, aggregate-byte and read-time limits. The enrolled journal root also
derives a mode-0700 runtime layout with private HOME, containers configuration, VFS storage and run roots; existing
configuration must match the generated bytes. Caller JSON cannot add operations, arguments, environment keys or authority. Console
cancellation reaches the existing asynchronous P2 executor, and cleanup remains incomplete until exact recovery
observes cleanup without another launch.

This is source preparation only. The accepted fixture constructor remains test support and is not the production
enrollment source. No administrator grant was provisioned, no container ran, and frozen `0.2.0` does not contain
this capability. P4 stays open for the receiver fixture/provider join, successor package identity, installed
runtime-only qualification, public readback and fresh explicit adoption.

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

## P1 installed utility evidence

The shared CLI project and command registrations compile with zero warnings. The dependency policy accepts
the single new CLI-to-Execution reference, and the registered CLI artifact test passes. A local preview tool
package was packed and installed under `/tmp`, then invoked from outside the source checkout: schema export
worked and an exported Python profile produced canonical SHA-256 digest
`469cf5c0bdc54e4db241b98dd7ba11cae396e2080bb9beec0365a2cbc2a8537c`.
The preview package is local qualification evidence, not a published Coordination release.

## P2 historical local evidence

The first local Execution suite passed 42 tests using Python and Node host processes and isolated fixture
copies. Review established that source/image/toolchain checks compared metadata while mutable files and host
tools executed. Node copying also did not establish TypeScript compilation. This evidence is retained as
historical operation-dispatch testing; its earlier P2 completion claim is superseded.

## P2 enforcement amendment

The outcome remains a real fixed-operation executor bound to exact source, immutable toolchain image, reviewed
recipe, component, working directory, finite limits and verification identity. The v1 wire bytes remain stable.
The first supported profile is Linux with rootless Podman and a preloaded single-platform OCI image identified
by the existing image digest. Execution cannot pull images, install tools or activate a host service. Missing
images and unsupported runtime configurations refuse before operation launch.

The supervisor, policy, runtime installation, image store and private journal are trusted host dependencies.
Qualification does not establish protection against an administrator or another actor controlling that account.

The coherent repair window is:

1. Export regular files from the exact Git commit into a private snapshot; reject symlinks, gitlinks and
   escaping paths. Mount source read-only, verify the local manifest digest and created-container image ID,
   and execute only the reviewed fixed operation with an explicit environment and isolated writable output.
   Each component needs its own declared toolchain; product operations bind their participating components.
2. Persist a unique reservation before launch. Bind exact container identity, check deadlines before starts,
   and bound termination and output readback. Exact duplicate delivery survives executor reconstruction and
   expiry; unknown termination remains unknown until exact runtime reconciliation. Recovery never relaunches.
3. Run actual Python build/test, TypeScript compilation, frontend/backend tests and the composed localhost
   journey in the pinned image. Qualify source/image drift, hostile environment, timeout, output overflow,
   concurrent/restarted duplicates and recovery. Fake-runtime tests establish source decisions only.

P2 closes after native source delivery and actual supported-runtime qualification. Source repair may land
while runtime qualification remains pending; publication and adoption retain P3–P5's existing boundaries.

## Workspace impact

P1–P3 change Coordination source and distribution only. They do not change fresh workspace creation, retained
workspaces, a product default or an enabled runtime. P4 is the first possible workspace change and requires exact
producer publication plus receiver-owned clean-creation proof. P5 expands verified adoption without rewriting the
frozen V2 cohort or historical outcomes.
