# V2-LANG-01.2 — Portable workspace integration

**Part:** V2-LANG-01, window .2. This plan owns the portable contract from Coordination source through
publication and receiver qualification. The accepted programme plan remains
[`FS-GG/.github`'s language-independent workspace amendment](https://github.com/FS-GG/.github/blob/main/docs/roadmaps/2026-09-29-language-independent-workspaces-and-agent-integration.md).

**Status:** P1 and P2 are complete. The exact 0.2.0 producer is published and independently read back from both
feeds and its immutable GitHub release. P3 remains open until SDD and Templates land their exact receiver pins.
P4 source is reserved under the independent 0.2.1 successor identity; installed receiver qualification,
explicit fresh adoption and P5 remain pending.

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
- [x] **P2 — Bounded executable binding.** Bind one real fixed-operation executor to the prepared source revision,
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

P2 closed after native source delivery and actual supported-runtime qualification. Publication and adoption
retain P3–P5's existing boundaries.

## P2 protected qualification evidence

The protected producer is commit `d25b9eaec991c94593adcecda6869d07dabdfb43`, tree
`169b7df260ee6668b8b28d43857183ad669b0e12`. Preparation run `36794564231` completed successfully and retained
artifact `11133062598`, named `callable-cli-d25b9eaec991c94593adcecda6869d07dabdfb43`, through 2026-10-15. The
186,566,428-byte archive has SHA-256
`27c52b6cc7aeb415be0c313fda40165c83daf1e30b5fe8e89d16cb9828824aa9`.

Independent bounded inspection established the following exact candidate identities:

- package `FS.GG.Coordination.Cli.0.2.0.nupkg`:
  `8ee67f83cecb3898eee12fd69f54cad0e3d1e232b3c88c18c434e3969019ab13`;
- callable package manifest: `88d1dcf0922328a7c31161a67c505ec7ec926778a07cd1704242e56758d86ee4`;
- portable bundle: `c4ccc949ba02d67eba27302dfdffa258ecd4a55b628315fff82c26b2e4566abc`;
- Linux amd64 OCI archive: `24dfd6fbf5e5d86b664963e5bcf896f2bbaba8803fdd9125c343a9389d6295b6`;
- portable release manifest: `bd08411e277c77f0f607716c510cc487ac438e4aeeff766b6b76e8a63e49d390`;
- image manifest: `349fe0b3339ce25467842603bf79ac2a41d420a458c633f19dfde1ce23f4e7e4`;
- image qualification: `98f3b09aafe4f2f34c55620471c789ab18a5f8632ac4002479d8ebed9e9a5234`;
- executor evidence: `5855f5d18fbdb62c9dd69c1b56b8ce2b0f32ffb91a6ae1d0ed8838625ed706dc`;
- packaged qualification receipt: `1f2a09a06aa19a036fdb34430b69e725820073b8b6ed80f163e5617802350f23`.

The package-bound qualification passed all six operations with zero failures, zero unknown outcomes and zero
remaining execution roots. The receipt binds embedded execution assembly
`d02f6f1e5fc58afd17fe2363a26e81b3f201be3e1c007488b466c7db47595aa5` and Akka assembly
`1ef266d80a923b25db758987a15e4db99acaa05863a236871bac4dbd6315c5b7`. The release manifest retains source,
image, manifest and custody identities while setting publication, tag and activation authorization to false.

## P3 qualified publication pins

The 0.2.0 publication workflow was pinned to the exact protected source, tree, package, bundle, OCI archive,
portable manifest, successful preparation run, retained artifact and artifact archive listed above. It rechecks
the live immutable preparation identities, reproduces the package, reruns all six packaged operations, requires
zero remaining execution roots, observes collisions, verifies provenance, publishes the same package bytes to
GitHub Packages before nuget.org, reads both feeds back and creates the tag and release only after both feeds
settle. Recovery run `36813849644` completed every gate. Release `400643766` and tag `v0.2.0` resolve to frozen
commit `d25b9eaec991c94593adcecda6869d07dabdfb43`; all five retained assets match the hashes above. The private
root readback receipt has SHA-256 `0b28f16f45a52d7d8f7176c83d5eceb94b889b353c996eab6a07062cc3303b9f`.
The release API's `target_commitish` metadata says `main`, while the actual immutable tag readback binds the
required frozen commit. Producer publication is **CLOSED**. Installation or activation is not implied.

SDD and Templates receiver pin candidates bind version 0.2.0, the frozen source and tree, release/run identities,
and exact package, contract bundle, OCI archive and portable manifest digests. Both candidates keep adoption
disabled and require separate installed qualification. P3 remains open until those receiver pins land on their
protected branches and are read back.

The accepted P4 source from PR #905, exact candidate `2933d7e4dd77fb321c001aeee4b78efa0eb63346` and protected
merge `e70d41fd9e48896e863bdf1ba33822ad6f09ee6b`, is excluded from the frozen 0.2.0 bytes. The current producer
source assigns that successor package version 0.2.1 and restricts future preparation to 0.2.1. This is source
preparation only: no 0.2.1 package, image, tag or release is claimed, and P4 remains open.

## Workspace impact

P1–P3 change Coordination source and distribution only. They do not change fresh workspace creation, retained
workspaces, a product default or an enabled runtime. P4 is the first possible workspace change and requires exact
producer publication plus receiver-owned clean-creation proof. P5 expands verified adoption without rewriting the
frozen V2 cohort or historical outcomes.
